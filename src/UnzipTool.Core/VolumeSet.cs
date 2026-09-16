using UnzipTool.Core.SevenZip;

namespace UnzipTool.Core;

/// <summary>
/// Permanently deletes each volume of an <em>independent-archive</em> set (RAR
/// <c>.partN.rar</c>) once 7z.dll has demonstrably moved past it, so that disk usage drops
/// as extraction proceeds. Those volumes cannot be concatenated, so the Rar handler opens
/// one stream per volume during Open and caches them; during extraction it reads them
/// strictly in order and never returns to an earlier one (measured against 7z.dll 22.01 on
/// Rar5: vol1 → vol2 → vol3 → vol4, forward only).
///
/// Arming is what makes this safe. The handler also reads <em>every</em> volume during Open
/// (each volume's headers sit at both ends of the file), so nothing may be deleted until the
/// caller has finished the header phase and the pre-extraction integrity test has passed.
/// <see cref="Touch"/> is therefore inert until <see cref="Arm"/> is called.
///
/// ponytail: correctness rests on forward-only access being an invariant of the Rar handler,
/// not on anything 7z.dll documents. A handler that re-read an earlier volume would find the
/// file gone and fail (files already written stay on disk). The mandatory full-CRC integrity
/// test that runs before extraction is the safety net that makes that acceptable.
/// </summary>
public sealed class VolumeReaper : IDisposable
{
    private readonly IReadOnlyList<string> _volumes;
    private readonly InStream?[] _streams;
    private readonly Action<string>? _trace;

    private bool _armed;
    private int _highestTouched = -1;

    internal VolumeReaper(IReadOnlyList<string> volumes, Action<string>? trace = null)
    {
        _volumes = volumes;
        _streams = new InStream?[volumes.Count];
        _trace = trace;
    }

    internal IReadOnlyList<string> Volumes => _volumes;

    /// <summary>Takes ownership of the file handle for one volume. 7z.dll never disposes the
    /// streams it is handed, so without this the handles leak and the files cannot be
    /// deleted (a delete on a file with an open handle fails with a sharing violation).</summary>
    internal void Register(int index, InStream stream)
    {
        _streams[index]?.Dispose();
        _streams[index] = stream;
    }

    /// <summary>Enables deletion. Call only once the header phase is over and the archive
    /// has passed its integrity test.</summary>
    public void Arm()
    {
        _armed = true;
        _highestTouched = -1;
    }

    /// <summary>Reports that 7z.dll has started reading volume <paramref name="index"/>.
    /// Every volume before it is finished with: its handle is dropped and the file deleted.</summary>
    internal void Touch(int index)
    {
        if (!_armed || index <= _highestTouched)
            return;

        for (int i = 0; i < index; i++)
        {
            _streams[i]?.Dispose();
            _streams[i] = null;
            TryDelete(_volumes[i]);
        }

        _highestTouched = index;
    }

    /// <summary>Drops every handle this set still holds, so the remaining volumes can be
    /// deleted and nothing is leaked back to the GC.</summary>
    public void Dispose()
    {
        for (int i = 0; i < _streams.Length; i++)
        {
            _streams[i]?.Dispose();
            _streams[i] = null;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _trace?.Invoke($"deleted: {Path.GetFileName(path)}");
            }
        }
        catch (Exception ex)
        {
            _trace?.Invoke($"delete FAILED {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}

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

    /// <summary>
    /// True when a multi-volume set is made of <em>independent archives</em> (RAR
    /// <c>.partN.rar</c>, where every volume carries its own headers), as opposed to
    /// <em>byte slices</em> of one archive (7z <c>.001</c>, zip <c>.z01</c>, rar <c>.rar.NNN</c>).
    /// Only the latter may be concatenated into a single logical stream.
    /// </summary>
    public static bool AreIndependentVolumes(IReadOnlyList<string> volumes)
        => volumes.Count > 1
           && FormatDetection.IsPartRar(Path.GetFileName(volumes[0]).ToLowerInvariant());

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
