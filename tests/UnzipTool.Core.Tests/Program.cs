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

    // --- RAR: 7z.dll ships separate handlers for RAR4 and RAR5, told apart by signature ---
    string fixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures");

    // Single volume, RAR5 — the common case, and what pins the RAR5 handler selection.
    string rar5 = Path.Combine(fixtureDir, "rar5-single.rar");
    var rar5c = engine.Open(rar5);
    Check(rar5c.Entries.Count(e => !e.IsDirectory) == 2, "rar5 single: 2 files listed");
    Check(rar5c.Entries.Any(e => e.IsDirectory && e.Path == "docs"), "rar5 single: folder listed");
    Check(engine.Test(rar5).AllOk, "rar5 single: integrity test passes");

    // Multi volume, RAR5 — .partN.rar volumes are independent archives, so 7z.dll must be
    // given the sibling volumes rather than one concatenated stream.
    string rar5v = Path.Combine(fixtureDir, "rar5-multivolume.part1.rar");
    var rar5vc = engine.Open(rar5v);
    Check(rar5vc.Entries.Count(e => !e.IsDirectory) == 3, "rar5 multivolume: 3 files listed");
    Check(engine.Test(rar5v).AllOk, "rar5 multivolume: integrity test passes");

    string rarOut = Path.Combine(work, "rarout");
    Directory.CreateDirectory(rarOut);
    var rarRep = engine.Extract(rar5v, new ExtractOptions
    {
        DestinationDirectory = rarOut,
        ExtractToSubfolder = false,
        Overwrite = OverwritePolicy.Overwrite,
    });
    Check(rarRep.AllOk, "rar5 multivolume: extraction succeeds");
    Check(new FileInfo(Path.Combine(rarOut, "alpha.bin")).Length == 6000
          && File.ReadAllText(Path.Combine(rarOut, "sub", "gamma.txt")) == "gamma-content",
          "rar5 multivolume: contents round-trip");

    // Delete-while-extracting must work for RAR too. It runs on a COPY so the committed
    // fixture survives, and the assertion that matters is the mid-run snapshot: by the end
    // every volume is gone either way (the engine also cleans up after a successful extract),
    // so only "some volume was already gone while extraction was still running" proves that
    // the volume was deleted as it was consumed rather than afterwards.
    string rarCopyDir = Path.Combine(work, "rarcopy");
    Directory.CreateDirectory(rarCopyDir);
    var rarVols = VolumeSet.Enumerate(rar5v);
    foreach (var v in rarVols)
        File.Copy(v, Path.Combine(rarCopyDir, Path.GetFileName(v)));
    int VolumesLeft() => rarVols.Count(v => File.Exists(Path.Combine(rarCopyDir, Path.GetFileName(v))));

    int minLeftDuringRun = int.MaxValue;
    var delRep = engine.Extract(Path.Combine(rarCopyDir, Path.GetFileName(rarVols[0])), new ExtractOptions
    {
        DestinationDirectory = Path.Combine(work, "rardelout"),
        ExtractToSubfolder = false,
        Overwrite = OverwritePolicy.Overwrite,
        DeleteVolumesAfterRead = true,
    }, new SyncProgress(_ => minLeftDuringRun = Math.Min(minLeftDuringRun, VolumesLeft())));

    Check(delRep.AllOk, "rar delete-while-extract: extraction succeeds");
    Check(minLeftDuringRun < rarVols.Count,
          $"rar delete-while-extract: freed during extraction (low water mark {minLeftDuringRun}/{rarVols.Count})");
    Check(VolumesLeft() == 0, $"rar delete-while-extract: all volumes gone ({VolumesLeft()} left)");

    // Header-encrypted RAR5. 7z.dll asks the open callback for a password; a callback that
    // answers "the password is ''" instead of declining makes the handler return S_OK with
    // zero entries — indistinguishable from an empty archive, so the caller is told nothing
    // and an archive whose password *is* in the store never gets that password tried.
    string rar5hp = Path.Combine(fixtureDir, "rar5-encrypted-headers.rar");
    Check(Throws<ArchivePasswordException>(() => engine.Open(rar5hp)),
          "rar5 encrypted headers: open with no password asks for one");
    Check(Throws<ArchivePasswordException>(() => engine.Open(rar5hp, "wrong")),
          "rar5 encrypted headers: open with a wrong password asks for one");
    var hp = engine.Open(rar5hp, "secret");
    Check(hp.Entries.Count(e => !e.IsDirectory) == 2, "rar5 encrypted headers: correct password lists 2 files");
    Check(hp.IsEncrypted, "rar5 encrypted headers: reported as encrypted");
    var hpTest = engine.Test(rar5hp);
    Check(!hpTest.AllOk && hpTest.Errors.Any(e => e.Contains("密码")),
          "rar5 encrypted headers: test without password names the problem");
    Check(engine.Test(rar5hp, new[] { "secret" }).AllOk, "rar5 encrypted headers: test with correct password passes");

    // Genuinely empty archives are the case that must not be caught by the check above.
    Check(engine.Open(Path.Combine(fixtureDir, "empty.zip")).Entries.Count == 0,
          "empty zip: still opens with no entries");
    // An empty tar is 1024 zero bytes (two 512-byte end-of-archive blocks). 7z.dll answers it with
    // S_FALSE rather than S_OK in some process states, which is exactly why S_FALSE must not be
    // treated as "corrupt" — see the note in SevenZipEngine.OpenInternal.
    string emptyTar = Path.Combine(work, "empty.tar");
    File.WriteAllBytes(emptyTar, new byte[1024]);
    Check(engine.Open(emptyTar).Entries.Count == 0, "empty tar: still opens with no entries");

    // tar.bz2 is advertised in the UI; its handler CLSID used to name no handler at all.
    var tbz = engine.Open(Path.Combine(fixtureDir, "sample.tar.bz2"));
    Check(tbz.Entries.Count(e => !e.IsDirectory) == 1, "tar.bz2: opens and lists its file");

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

static bool Throws<T>(Action act) where T : Exception
{
    try { act(); return false; }
    catch (T) { return true; }
    catch { return false; }
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

/// <summary>Reports inline instead of posting to the thread pool, so callbacks observed
/// during extraction are seen in order and the low-water mark is not raced.</summary>
sealed class SyncProgress : IProgress<ProgressUpdate>
{
    private readonly Action<ProgressUpdate> _onReport;
    public SyncProgress(Action<ProgressUpdate> onReport) => _onReport = onReport;
    public void Report(ProgressUpdate value) => _onReport(value);
}
