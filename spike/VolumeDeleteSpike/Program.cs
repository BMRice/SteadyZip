using System.Diagnostics;
using System.Security.Cryptography;
using UnzipTool.Core;
using UnzipTool.Core.SevenZip;

// Spike: verify the ADR-0001 premise that a custom volume input stream can delete a
// volume once its data has been fully read, while 7z.dll continues with the next volume.
//
// Strategy: create multi-volume archives (solid 7z, non-solid 7z, zip) with tiny volumes
// via the bundled 7z.exe, then extract them through the 7z.dll COM engine with
// delete-volumes-after-read armed, tracing every GetStream/delete and comparing output.

var sevenZip = FindSevenZipExe();
if (sevenZip is null)
{
    Console.WriteLine("FATAL: 7z.exe not found (expected under NVIDIA App).");
    return 1;
}
if (!SevenZipLibrary.IsAvailable())
{
    Console.WriteLine("FATAL: 7z.dll not loadable (copy deps/7z/7z.dll next to this exe).");
    return 1;
}

string work = Path.Combine(Path.GetTempPath(), "vd-spike-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);

try
{
    string sourceDir = Path.Combine(work, "source");
    Directory.CreateDirectory(sourceDir);
    // Deterministic files; mix of sizes, several larger than a 64 KB volume.
    var rng = new Random(12345);
    for (int i = 0; i < 8; i++)
    {
        int size = i switch { 0 => 200_000, 1 => 130_000, _ => 30_000 + i * 7_000 };
        var data = new byte[size];
        rng.NextBytes(data);
        File.WriteAllBytes(Path.Combine(sourceDir, $"file{i:D2}.bin"), data);
    }

    int total = 0;
    total += RunCase(sevenZip, work, sourceDir, "solid",    new[] { "-t7z", "-ms=on",  "-v64k" });
    total += RunCase(sevenZip, work, sourceDir, "nonsolid", new[] { "-t7z", "-ms=off", "-v64k" });
    total += RunCase(sevenZip, work, sourceDir, "zip",      new[] { "-tzip", "-v64k" });

    Console.WriteLine($"\nSPIKE SUMMARY: {(total == 0 ? "ALL PASS" : $"{total} FAILURE(S)")}");
    return total;
}
finally
{
    try { Directory.Delete(work, recursive: true); } catch { /* best-effort */ }
}

static int RunCase(string sevenZip, string work, string sourceDir, string label, string[] switches)
{
    Console.WriteLine($"\n=== CASE: {label} ===");

    string archiveName = label switch
    {
        "zip" => Path.Combine(work, label + ".zip"),
        _ => Path.Combine(work, label + ".7z"),
    };

    var args = new List<string> { "a", "-y" };
    args.AddRange(switches);
    args.Add(archiveName);
    args.Add(Path.Combine(sourceDir, "*"));
    Run(sevenZip, args, work);

    // 7z.exe `-v` produces generic .NNN split volumes for every format:
    //   7z  -> solid.7z.001 …   zip -> zip.zip.001 …
    // (The native .z01/.part1.rar conventions come from other tools and are handled by
    // VolumeSet, but 7z.exe itself uses .NNN.)
    string archiveFirstVolume = label switch
    {
        "zip" => Path.Combine(work, label + ".zip.001"),
        _ => Path.Combine(work, label + ".7z.001"),
    };

    var volumes = VolumeSet.Enumerate(archiveFirstVolume);
    if (volumes.Count < 2)
    {
        Console.WriteLine($"  WARN: archive did not split into multiple volumes (got {volumes.Count}).");
        return 1;
    }
    Console.WriteLine($"  volumes: {volumes.Count} ({Path.GetFileName(volumes[0])} ... {Path.GetFileName(volumes[^1])})");

    // Copy the volume set into an isolated dir so the originals survive for comparison.
    string caseDir = Path.Combine(work, label + "-case");
    Directory.CreateDirectory(caseDir);
    foreach (var v in volumes)
        File.Copy(v, Path.Combine(caseDir, Path.GetFileName(v)));

    string outDir = Path.Combine(caseDir, "out");
    Directory.CreateDirectory(outDir);

    string firstVolume = Path.Combine(caseDir, Path.GetFileName(volumes[0]));

    var engine = new SevenZipEngine();
    var options = new ExtractOptions
    {
        DestinationDirectory = outDir,
        ExtractToSubfolder = false,
        Overwrite = OverwritePolicy.Overwrite,
        DeleteVolumesAfterRead = true,
    };

    var report = engine.Extract(firstVolume, options, null, CancellationToken.None);

    Console.WriteLine($"  extract: ok={report.OkCount} errors={report.ErrorCount} cancelled={report.Cancelled}");
    if (report.Errors.Count > 0)
        Console.WriteLine("  errors: " + string.Join("; ", report.Errors.Take(5)));

    bool filesMatch = CompareTrees(sourceDir, outDir, out string mismatch);
    Console.WriteLine($"  extracted files match originals: {filesMatch}" + (filesMatch ? "" : $"  (mismatch: {mismatch})"));

    var remaining = volumes.Where(v => File.Exists(Path.Combine(caseDir, Path.GetFileName(v)))).ToList();
    Console.WriteLine($"  volumes remaining after extraction: {remaining.Count}/{volumes.Count}"
                      + (remaining.Count > 0 ? "  -> " + string.Join(", ", remaining.Select(v => Path.GetFileName(v))) : ""));

    bool pass = report.ErrorCount == 0 && filesMatch;
    Console.WriteLine($"  RESULT: {(pass ? "PASS" : "FAIL")}");
    return pass ? 0 : 1;
}

static bool CompareTrees(string a, string b, out string mismatch)
{
    var fa = Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories)
        .Select(p => (Rel: Path.GetRelativePath(a, p), Hash: Sha256File(p)))
        .ToDictionary(x => x.Rel, x => x.Hash, StringComparer.Ordinal);
    var fb = Directory.EnumerateFiles(b, "*", SearchOption.AllDirectories)
        .Select(p => (Rel: Path.GetRelativePath(b, p), Hash: Sha256File(p)))
        .ToDictionary(x => x.Rel, x => x.Hash, StringComparer.Ordinal);

    foreach (var kv in fa)
    {
        if (!fb.TryGetValue(kv.Key, out var h) || h != kv.Value)
        {
            mismatch = kv.Key;
            return false;
        }
    }
    mismatch = "";
    return fa.Count == fb.Count;
}

static string Sha256File(string path)
{
    using var sha = SHA256.Create();
    using var fs = File.OpenRead(path);
    return Convert.ToHexString(sha.ComputeHash(fs));
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

static void Run(string exe, IEnumerable<string> args, string workDir)
{
    var psi = new ProcessStartInfo
    {
        FileName = exe,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = workDir,
    };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    if (p.ExitCode != 0)
        throw new InvalidOperationException($"{Path.GetFileName(exe)} failed: {p.StandardError.ReadToEnd()}");
}
