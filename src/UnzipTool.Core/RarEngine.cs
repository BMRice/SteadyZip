using System.Diagnostics;

namespace UnzipTool.Core;

/// <summary>
/// Creates RAR archives via an external rar.exe (WinRAR). Only the "create" operation
/// is routed here per ADR-0001; extraction and testing of RAR always use 7z.dll.
/// </summary>
public sealed class RarEngine
{
    public static string? FindRarExecutable()
    {
        foreach (var candidate in CandidatePaths())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // Fall back to PATH.
        string? onPath = null;
        try
        {
            onPath = Environment.GetEnvironmentVariable("PATH")?
                .Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, "rar.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception)
        {
            onPath = null;
        }
        return onPath;
    }

    public bool IsAvailable() => FindRarExecutable() is not null;

    public void Create(CreateOptions options, IProgress<ProgressUpdate>? progress = null, CancellationToken cancel = default)
    {
        string rar = FindRarExecutable()
            ?? throw new InvalidOperationException("rar.exe was not found; RAR creation is unavailable.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.DestinationArchive))!);

        var args = new List<string> { "a", "-r", "-ep1", "-idcd" };

        int level = options.Level switch { 0 => 0, <= 1 => 1, <= 5 => 3, _ => 5 };
        args.Add($"-m{level}");
        if (!string.IsNullOrEmpty(options.Password))
            args.Add($"-p{options.Password}");
        if (options.VolumeSizeBytes is { } vs)
            args.Add($"-v{FormatVolumeSize(vs)}");

        args.Add(options.DestinationArchive);
        args.AddRange(options.SourcePaths);

        Run(rar, args, progress, cancel);
    }

    private static void Run(string rar, IEnumerable<string> args, IProgress<ProgressUpdate>? progress, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo
        {
            FileName = rar,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // rar.exe prints "NN%" lines; parse the last number we see.
        string? err = null;
        var readErr = proc.StandardError.ReadToEndAsync();
        string? line;
        while ((line = proc.StandardOutput.ReadLine()) is not null)
        {
            if (cancel.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
            int pct = line.IndexOf('%');
            if (pct > 0 && int.TryParse(line[..pct].Trim(), out var p))
                progress?.Report(new ProgressUpdate(null, Math.Clamp(p / 100.0, 0, 1)));
        }

        proc.WaitForExit();
        err = readErr.Result;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"rar.exe failed (exit {proc.ExitCode}): {err}");
    }

    private static string FormatVolumeSize(ulong bytes)
    {
        // rar.exe expects e.g. -v700m or -v4g; round to a sensible unit.
        if (bytes % (1024UL * 1024 * 1024) == 0)
            return $"{bytes / (1024UL * 1024 * 1024)}g";
        if (bytes % (1024UL * 1024) == 0)
            return $"{bytes / (1024UL * 1024)}m";
        if (bytes % 1024 == 0)
            return $"{bytes / 1024}k";
        return bytes.ToString();
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return new[]
        {
            Path.Combine(pf, "WinRAR", "rar.exe"),
            Path.Combine(pf86, "WinRAR", "rar.exe"),
            Path.Combine(pf, "WinRAR", "Rar.exe"),
            Path.Combine(pf86, "WinRAR", "Rar.exe"),
        };
    }
}
