namespace UnzipTool.Core;

/// <summary>Progress report: current item being processed and 0..1 fraction.</summary>
public readonly record struct ProgressUpdate(string? CurrentItem, double Fraction);

public enum OverwritePolicy
{
    /// <summary>Ask the user per conflict (apply-to-all handled by the prompt callback).</summary>
    Ask,
    Skip,
    Overwrite,
    AutoRename,
}

public enum OverwriteAction
{
    Overwrite,
    Skip,
    Rename,
}

/// <summary>Called by the engine when OverwritePolicy.Ask hits an existing file.
/// Returning an action tells the engine what to do for that file. Implementations may
/// memoize an "apply to all" answer internally.</summary>
public delegate OverwriteAction OverwritePrompt(string targetPath);

public sealed class ExtractOptions
{
    public required string DestinationDirectory { get; init; }

    /// <summary>If true, files land directly in DestinationDirectory;
    /// otherwise in a subfolder named after the archive's base name.</summary>
    public bool ExtractToSubfolder { get; init; } = true;

    public OverwritePolicy Overwrite { get; init; } = OverwritePolicy.Ask;
    public OverwritePrompt? OverwritePrompt { get; init; }

    /// <summary>Delete the source archive (single file) after successful extraction.</summary>
    public bool DeleteSourceAfter { get; init; }

    /// <summary>Delete each volume right after its data is read (multi-volume only).</summary>
    public bool DeleteVolumesAfterRead { get; init; }

    /// <summary>Indices to extract; null means everything.</summary>
    public IReadOnlyCollection<uint>? SelectedIndices { get; init; }

    /// <summary>Password to try first; a null/empty list disables password attempts.</summary>
    public IReadOnlyList<string> Passwords { get; init; } = Array.Empty<string>();

    /// <summary>Upper bound on total unpacked size before a confirmation is required.</summary>
    public ulong SizeWarningThreshold { get; init; } = 100UL * 1024 * 1024 * 1024; // 100 GiB
}

public enum ExtractFileResult
{
    Ok,
    CrcError,
    DataError,
    UnsupportedMethod,
    Skipped,
    Error,
}

public sealed class ExtractReport
{
    public IReadOnlyDictionary<uint, ExtractFileResult>? FileResults { get; init; }
    public int OkCount { get; init; }
    public int ErrorCount { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public bool Cancelled { get; init; }
    public bool AllOk => ErrorCount == 0 && !Cancelled;
}

public sealed class CreateOptions
{
    public required ArchiveFormat Format { get; init; }
    public required IReadOnlyList<string> SourcePaths { get; init; }
    public required string DestinationArchive { get; init; }

    /// <summary>0 = store, 1 = fastest, 5 = normal, 9 = maximum.</summary>
    public int Level { get; init; } = 5;

    public bool Solid { get; init; }
    public string? Password { get; init; }
    public ulong? VolumeSizeBytes { get; init; }
    public bool EncryptHeaders { get; init; }
}
