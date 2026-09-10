namespace UnzipTool.Core;

/// <summary>
/// Zip-slip protection: maps an archive-relative path onto a safe path strictly inside
/// the destination root. Rejects absolute paths, drive letters, UNC and any ".." segment.
/// </summary>
public static class PathGuard
{
    /// <summary>Returns the sanitized relative path (using '\'), or null if the path is unsafe.</summary>
    public static string? Sanitize(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            return null;

        // Normalize separators; drop any leading slashes.
        string p = archivePath.Replace('/', '\\').TrimStart('\\');

        // Reject drive-relative or rooted paths.
        if (Path.IsPathRooted(p))
            return null;
        if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':')
            return null;

        // Reject any ".." traversal segment.
        foreach (var seg in p.Split('\\'))
        {
            if (seg == "..")
                return null;
            if (seg.Length >= 2 && seg[0] == '.' && seg[1] == '.')
                return null;
        }

        return p;
    }

    /// <summary>Combines a sanitized relative path with the destination root and asserts the
    /// result stays inside the root.</summary>
    public static string CombineSafe(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path escapes destination: {relative}");

        return full;
    }
}
