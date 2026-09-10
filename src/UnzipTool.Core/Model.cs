namespace UnzipTool.Core;

/// <summary>Archive format as understood by the engine router.</summary>
public enum ArchiveFormat
{
    Zip,
    SevenZip,
    Rar,
    Tar,
    TarGz,
    TarBz2,
    Unknown,
}

/// <summary>A single entry (file or directory) inside an archive.</summary>
public sealed class ArchiveEntry
{
    public required uint Index { get; init; }
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public ulong Size { get; init; }
    public ulong PackedSize { get; init; }
    public bool IsEncrypted { get; init; }
    public DateTime? ModifiedTime { get; init; }
    public uint? Crc { get; init; }
    public string? Method { get; init; }

    /// <summary>Index of the parent directory entry, or -1 for a root-level entry.</summary>
    public int ParentIndex { get; set; } = -1;

    public string Name => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] : Path;
}

/// <summary>The result of opening (browsing) an archive without extracting it.</summary>
public sealed class ArchiveContents
{
    public required ArchiveFormat Format { get; init; }
    public required IReadOnlyList<ArchiveEntry> Entries { get; init; }
    public required bool IsMultiVolume { get; init; }
    public required IReadOnlyList<string> Volumes { get; init; }
    public bool IsEncrypted { get; init; }
    public ulong TotalUnpackedSize { get; init; }
}
