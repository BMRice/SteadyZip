using UnzipTool.Core.SevenZip;

namespace UnzipTool.Core;

/// <summary>Maps file paths/extensions to archive formats and 7z.dll handler CLSIDs.</summary>
public static class FormatDetection
{
    public static ArchiveFormat FromPath(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        string ext = Path.GetExtension(name);

        if (name.EndsWith(".tar.gz") || name.EndsWith(".tgz")) return ArchiveFormat.TarGz;
        if (name.EndsWith(".tar.bz2") || name.EndsWith(".tbz2")) return ArchiveFormat.TarBz2;
        if (name.EndsWith(".tar")) return ArchiveFormat.Tar;

        // Multi-volume RAR: foo.part1.rar / foo.part2.rar, and plain .rar
        if (ext == ".rar" || IsPartRar(name)) return ArchiveFormat.Rar;

        // Multi-volume ZIP: .z01..zip ; .zip is either single or the final volume.
        if (ext == ".zip" || ext == ".z01" || (ext.Length == 4 && ext[1] == 'z' && char.IsDigit(ext[2]) && char.IsDigit(ext[3])))
            return ArchiveFormat.Zip;

        // Multi-volume 7z: .7z.001.. — the .001/.002 convention is treated as 7z per spec.
        if (ext == ".7z") return ArchiveFormat.SevenZip;

        // Generic .NNN split volume (7z.exe uses this for 7z AND zip): the base name
        // before the numeric extension tells us the real format.
        if (IsNumericSplitExt(name))
        {
            string stem = name[..^4];
            if (stem.EndsWith(".zip")) return ArchiveFormat.Zip;
            if (stem.EndsWith(".rar")) return ArchiveFormat.Rar;
            if (stem.EndsWith(".tar")) return ArchiveFormat.Tar;
            return ArchiveFormat.SevenZip;
        }

        return ArchiveFormat.Unknown;
    }

    private static bool IsPartRar(string name)
    {
        // foo.part1.rar
        int idx = name.IndexOf(".part", StringComparison.Ordinal);
        if (idx <= 0 || !name.EndsWith(".rar"))
            return false;
        string mid = name[(idx + 5)..^4];
        return mid.Length > 0 && mid.All(char.IsDigit);
    }

    private static bool IsNumericSplitExt(string name)
    {
        string ext = Path.GetExtension(name); // ".001"
        return ext.Length == 4 && ext[1..].All(char.IsDigit);
    }

    internal static Guid GetClsid(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => FormatIds.Zip,
        ArchiveFormat.SevenZip => FormatIds.SevenZip,
        ArchiveFormat.Rar => FormatIds.Rar,
        ArchiveFormat.Tar => FormatIds.Tar,
        ArchiveFormat.TarGz => FormatIds.GZip,
        ArchiveFormat.TarBz2 => FormatIds.BZip2,
        _ => throw new NotSupportedException($"Unsupported format: {format}"),
    };
}
