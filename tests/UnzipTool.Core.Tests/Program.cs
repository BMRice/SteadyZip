using UnzipTool.Core;

// Minimal assertion-based self-check (no test framework). Exercises the engine against the
// real 7z.dll and cross-checks with 7z.exe. Exits non-zero on any failure.

int failures = 0;
void Check(bool cond, string name)
{
    Console.WriteLine((cond ? "  ok: " : "  FAIL: ") + name);
    if (!cond) failures++;
}

string? sevenZip = FindSevenZipExe();
if (sevenZip is null || !SevenZipLibrary.IsAvailable())
{
    Console.WriteLine("FATAL: 7z.exe / 7z.dll not found. Copy deps/7z/7z.dll next to this exe.");
    return 2;
}

string work = Path.Combine(Path.GetTempPath(), "uz-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);

try
{
    var engine = new SevenZipEngine();

    // --- fixtures ---
    string src = Path.Combine(work, "src");
    Directory.CreateDirectory(Path.Combine(src, "sub"));
    Directory.CreateDirectory(Path.Combine(src, "empty"));
    File.WriteAllText(Path.Combine(src, "a.txt"), "hello " + new string('A', 5000));
    File.WriteAllText(Path.Combine(src, "sub", "b.txt"), new string('B', 3000));

    // --- create 7z + zip, then list via engine ---
    var sevenZipArchive = Path.Combine(work, "out.7z");
    engine.Create(new CreateOptions { Format = ArchiveFormat.SevenZip, SourcePaths = new[] { src }, DestinationArchive = sevenZipArchive, Level = 5 });
    var c7 = engine.Open(sevenZipArchive);
    Check(c7.Entries.Count(e => !e.IsDirectory) == 2, "7z create: 2 files listed");
    Check(c7.Entries.Any(e => e.IsDirectory && e.Path == "src/empty"), "7z create: empty dir preserved");
    Check(RunSevenZip(sevenZip, "t", sevenZipArchive) == 0, "7z create: passes 7z.exe t");

    var zipArchive = Path.Combine(work, "out.zip");
    engine.Create(new CreateOptions { Format = ArchiveFormat.Zip, SourcePaths = new[] { src }, DestinationArchive = zipArchive });
    var cz = engine.Open(zipArchive);
    Check(cz.Entries.Count(e => !e.IsDirectory) == 2, "zip create: 2 files listed");
    Check(RunSevenZip(sevenZip, "t", zipArchive) == 0, "zip create: passes 7z.exe t");

    // --- encrypted create + wrong/correct password ---
    var encArchive = Path.Combine(work, "enc.7z");
    engine.Create(new CreateOptions { Format = ArchiveFormat.SevenZip, SourcePaths = new[] { src }, DestinationArchive = encArchive, Password = "secret" });
    Check(!engine.Test(encArchive, new[] { "wrong" }).AllOk, "encrypted 7z: wrong password fails test");
    Check(engine.Test(encArchive, new[] { "secret" }).AllOk, "encrypted 7z: correct password passes test");

    // --- extract round-trip (structure preserved, incl. the src/ prefix) ---
    string outDir = Path.Combine(work, "outdir");
    Directory.CreateDirectory(outDir);
    var rep = engine.Extract(zipArchive, new ExtractOptions { DestinationDirectory = outDir, ExtractToSubfolder = false, Overwrite = OverwritePolicy.Overwrite });
    Check(rep.AllOk, "extract: zip round-trip succeeds");
    Check(File.Exists(Path.Combine(outDir, "src", "a.txt")) && File.Exists(Path.Combine(outDir, "src", "sub", "b.txt")), "extract: files land with structure");

    // --- multi-volume create + delete-while-extract ---
    var bigSrc = Path.Combine(work, "big");
    Directory.CreateDirectory(bigSrc);
    var rng = new Random(5);
    for (int i = 0; i < 6; i++) { var d = new byte[70_000]; rng.NextBytes(d); File.WriteAllBytes(Path.Combine(bigSrc, $"f{i}.bin"), d); }

    var splitDir = Path.Combine(work, "split");
    Directory.CreateDirectory(splitDir);
    var splitBase = Path.Combine(splitDir, "big.7z");
    engine.Create(new CreateOptions { Format = ArchiveFormat.SevenZip, SourcePaths = new[] { bigSrc }, DestinationArchive = splitBase, VolumeSizeBytes = 64 * 1024 });
    var vols = VolumeSet.Enumerate(Path.Combine(splitDir, "big.7z.001"));
    Check(vols.Count > 1, $"multi-volume create: split into {vols.Count} volumes");
    Check(RunSevenZip(sevenZip, "t", vols[0]) == 0, "multi-volume create: passes 7z.exe t");

    string splitOut = Path.Combine(splitDir, "out");
    Directory.CreateDirectory(splitOut);
    var srep = engine.Extract(vols[0], new ExtractOptions { DestinationDirectory = splitOut, ExtractToSubfolder = false, Overwrite = OverwritePolicy.Overwrite, DeleteVolumesAfterRead = true });
    Check(srep.AllOk, "delete-while-extract: extraction succeeds");
    int remaining = vols.Count(v => File.Exists(v));
    Check(remaining == 0, $"delete-while-extract: all volumes deleted ({remaining} left)");

    // --- zip-slip guard ---
    Check(PathGuard.Sanitize("../evil.txt") is null, "pathguard: rejects ..");
    Check(PathGuard.Sanitize("C:\\evil.txt") is null, "pathguard: rejects drive path");
    Check(PathGuard.Sanitize("a/b/c.txt") == "a\\b\\c.txt", "pathguard: normalizes separators");

    // --- password store (DPAPI) ---
    var store = new PasswordStore(Path.Combine(work, "pw.dat"));
    store.Save(new[] { "alpha", "beta" });
    Check(store.Load().SequenceEqual(new[] { "alpha", "beta" }), "password store: round-trips via DPAPI");

    // --- volume enumeration (.zNN native zip split) ---
    File.WriteAllText(Path.Combine(work, "w.z01"), "x");
    File.WriteAllText(Path.Combine(work, "w.z02"), "x");
    File.WriteAllText(Path.Combine(work, "w.zip"), "x");
    Check(VolumeSet.Enumerate(Path.Combine(work, "w.z01")).Count == 3, "volume set: enumerates .zNN set");

    Console.WriteLine(failures == 0 ? "\nALL TESTS PASSED" : $"\n{failures} FAILURE(S)");
    return failures == 0 ? 0 : 1;
}
finally
{
    try { Directory.Delete(work, recursive: true); } catch { /* best-effort */ }
}

static int RunSevenZip(string sevenZip, string cmd, string archive)
{
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = sevenZip,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    psi.ArgumentList.Add(cmd);
    psi.ArgumentList.Add("-y");
    psi.ArgumentList.Add(archive);
    using var p = System.Diagnostics.Process.Start(psi)!;
    p.WaitForExit();
    return p.ExitCode;
}

static string? FindSevenZipExe()
{
    var candidates = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVIDIA App", "7z.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
    };
    return candidates.FirstOrDefault(File.Exists);
}
