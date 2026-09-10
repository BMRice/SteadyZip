namespace UnzipTool.Core;

/// <summary>
/// Enumerates the complete set of volume files for a split archive, given any one
/// volume path. Recognises the naming schemes the spec commits to:
/// ZIP (.z01…zip), 7z (.001…), RAR (.part1.rar…).
/// </summary>
public static class VolumeSet
{
    public static IReadOnlyList<string> Enumerate(string anyVolumePath)
    {
        anyVolumePath = Path.GetFullPath(anyVolumePath);
        string dir = Path.GetDirectoryName(anyVolumePath)!;
        string name = Path.GetFileName(anyVolumePath);
        string lower = name.ToLowerInvariant();

        var result = new List<string>();

        // RAR split: foo.part1.rar … foo.partN.rar (final is foo.rar)
        if (lower.EndsWith(".rar") && lower.Contains(".part"))
        {
            string prefix = name[..name.IndexOf(".part", StringComparison.OrdinalIgnoreCase)];
            EnumerateRarVolumes(dir, prefix, result);
            return SortNumeric(result);
        }

        if (lower.EndsWith(".rar"))
        {
            string prefix = name[..^4];
            if (File.Exists(Path.Combine(dir, prefix + ".part1.rar")))
            {
                EnumerateRarVolumes(dir, prefix, result);
                return SortNumeric(result);
            }
            result.Add(anyVolumePath);
            return result;
        }

        // ZIP split: foo.z01 … foo.zip
        if (lower.EndsWith(".zip"))
        {
            string prefix = name[..^4];
            if (File.Exists(Path.Combine(dir, prefix + ".z01")))
            {
                EnumerateZipVolumes(dir, prefix, result);
                return SortNumeric(result);
            }
            result.Add(anyVolumePath);
            return result;
        }

        if (lower.Length >= 4 && lower[^4] == '.' && lower[^3] == 'z' && char.IsDigit(lower[^2]) && char.IsDigit(lower[^1]))
        {
            // foo.z01 — prefix is the part before .zNN
            string prefix = name[..^4];
            EnumerateZipVolumes(dir, prefix, result);
            return SortNumeric(result);
        }

        // 7z split: foo.7z.001 … foo.7z.NNN
        if (lower.Length >= 4 && lower[^4] == '.' && lower[^3..].All(char.IsDigit))
        {
            // prefix is everything before the final .NNN (e.g. "foo.7z")
            string prefix = name[..^4];
            foreach (var f in Directory.EnumerateFiles(dir, prefix + ".*"))
            {
                string ext = Path.GetExtension(f);
                if (ext.Length == 4 && ext[1..].All(char.IsDigit))
                    result.Add(f);
            }
            if (result.Count == 0)
                result.Add(anyVolumePath);
            return SortNumeric(result);
        }

        result.Add(anyVolumePath);
        return result;
    }

    private static void EnumerateZipVolumes(string dir, string prefix, List<string> result)
    {
        foreach (var f in Directory.EnumerateFiles(dir, prefix + ".z??"))
        {
            string ext = Path.GetExtension(f);
            if (ext.Length == 4 && ext[1] == 'z' && char.IsDigit(ext[2]) && char.IsDigit(ext[3]))
                result.Add(f);
        }
        string zip = Path.Combine(dir, prefix + ".zip");
        if (File.Exists(zip))
            result.Add(zip);
    }

    private static void EnumerateRarVolumes(string dir, string prefix, List<string> result)
    {
        foreach (var f in Directory.EnumerateFiles(dir, prefix + ".part*.rar"))
        {
            if (IsPartName(Path.GetFileName(f), prefix))
                result.Add(f);
        }
        string rar = Path.Combine(dir, prefix + ".rar");
        if (File.Exists(rar))
            result.Add(rar);
    }

    private static bool IsPartName(string name, string prefix)
    {
        if (!name.StartsWith(prefix + ".part", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".rar"))
            return false;
        string mid = name[(prefix.Length + 5)..^4];
        return mid.Length > 0 && mid.All(char.IsDigit);
    }

    private static List<string> SortNumeric(List<string> list)
    {
        return list
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => ExtractVolumeNumber(p))
            .ToList();
    }

    private static long ExtractVolumeNumber(string path)
    {
        string name = Path.GetFileName(path);
        int i = name.Length - 1;
        while (i >= 0 && char.IsDigit(name[i])) i--;
        int j = i + 1;
        if (j >= name.Length) return 0;
        return long.TryParse(name[j..], out var n) ? n : 0;
    }
}
