using System.Runtime.InteropServices;

namespace UnzipTool.Core.SevenZip;

internal static class HResult
{
    public const int S_OK = 0;
    public const int E_ABORT = unchecked((int)0x80004004);
}

/// <summary>
/// Open callback handed to <c>IInArchive.Open</c>. Supplies the decryption password, and —
/// for volume sets made of independent archives (RAR <c>.partN.rar</c>) — the remaining
/// volumes by name via <see cref="IArchiveOpenVolumeCallback"/>, which is the only way
/// 7z.dll's Rar handler can chain volumes that are not byte slices of one archive.
/// Byte-sliced sets (7z <c>.001</c>, zip <c>.z01</c>) never reach these members; they are
/// presented as one logical stream by <see cref="MultiVolumeStream"/> instead.
/// </summary>
internal sealed class OpenCallback : IArchiveOpenCallback, IArchiveOpenVolumeCallback, ICryptoGetTextPassword, ICryptoGetTextPassword2
{
    /// <summary>
    /// 7z.dll asks the volume callback for <c>kpidName</c>, which is 4 in the ordinary
    /// <see cref="PropId"/> namespace (verified against 7z.dll 22.01 by tracing the calls:
    /// it passes exactly 4). Answering a different PROPID makes the handler conclude the
    /// set has a single volume — <c>NumVolumes</c> stays 1 and <c>GetStream</c> is never
    /// called — which silently truncates the archive to its first volume.
    /// </summary>
    private const uint VolumeNamePropId = (uint)PropId.Name;

    private readonly string? _password;
    private readonly string? _volumeName;
    private readonly VolumeReaper? _reaper;
    private readonly Action<string>? _trace;

    /// <param name="volumeName">Name of the file 7z.dll was handed. The handler asks for it as
    /// <c>kpidName</c>; leaving it unanswered makes at least the Tar handler conclude the archive
    /// is unreadable (S_FALSE) even when it is simply empty.</param>
    /// <param name="reaper">Owns the independent-archive volume set, when this archive is one.</param>
    public OpenCallback(string? password, string? volumeName = null, VolumeReaper? reaper = null, Action<string>? trace = null)
    {
        _password = password;
        _volumeName = volumeName;
        _reaper = reaper;
        _trace = trace;
    }

    // IArchiveOpenCallback
    public int SetTotal(IntPtr files, IntPtr bytes) => HResult.S_OK;
    public int SetCompleted(IntPtr files, IntPtr bytes) => HResult.S_OK;

    // IArchiveOpenVolumeCallback — 7z.dll asks for the name of the volume it was handed,
    // then requests siblings by that name pattern.
    public int GetProperty(uint propID, ref PropVariant value)
    {
        _trace?.Invoke($"volume: GetProperty({propID})");
        if (propID == VolumeNamePropId && _volumeName is not null)
            value.SetBstr(_volumeName);
        return HResult.S_OK;
    }

    public int GetStream([MarshalAs(UnmanagedType.LPWStr)] string name, out IInStream inStream)
    {
        inStream = null!;
        if (_reaper is not { } reaper)
            return HResult.S_OK;

        // Only hand back volumes the set actually contains, so 7z.dll can never wander
        // outside it. No match => null stream => "no further volumes".
        int index = -1;
        for (int i = 0; i < reaper.Volumes.Count; i++)
        {
            if (string.Equals(Path.GetFileName(reaper.Volumes[i]), name, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        _trace?.Invoke($"volume: GetStream({name}) -> {(index < 0 ? "not in set" : Path.GetFileName(reaper.Volumes[index]))}");
        if (index < 0)
            return HResult.S_OK;

        var stream = new InStream(File.OpenRead(reaper.Volumes[index]));
        int captured = index;
        stream.OnRead = () => reaper.Touch(captured);
        reaper.Register(captured, stream);
        inStream = stream;
        return HResult.S_OK;
    }

    /// <summary>
    /// Set once 7z.dll asks for a password. Only header-encrypted archives make it ask, so this
    /// is how the engine tells "encrypted, and this password did not decrypt it" apart from
    /// "genuinely empty archive" when the handler reports zero items — see
    /// <see cref="ArchivePasswordException"/>.
    /// </summary>
    public bool PasswordRequested { get; private set; }

    // ICryptoGetTextPassword — declining is how a callback says "I have no password". Answering
    // S_OK with an empty BSTR instead claims that "" *is* the password: the handler then fails
    // to decrypt the header and returns S_OK with zero items, which the caller cannot tell apart
    // from an empty archive. That is why an encrypted archive whose password was already in the
    // store listed nothing at all.
    public int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string password)
    {
        PasswordRequested = true;
        password = _password ?? string.Empty;
        return _password is null ? HResult.E_ABORT : HResult.S_OK;
    }

    // ICryptoGetTextPassword2
    public int CryptoGetTextPassword2(out int passwordIsDefined, [MarshalAs(UnmanagedType.BStr)] out string password)
    {
        PasswordRequested = true;
        passwordIsDefined = _password is null ? 0 : 1;
        password = _password ?? string.Empty;
        return HResult.S_OK;
    }
}

/// <summary>Per-item extraction target resolved by the engine.</summary>
internal sealed class ExtractItem
{
    public required uint Index { get; init; }
    public required string TargetPath { get; init; }
    public required bool IsDirectory { get; init; }
    public required bool Overwrite { get; init; }
    public required bool Skip { get; init; }
}

internal sealed class ExtractCallback : IArchiveExtractCallback, IProgress, ICryptoGetTextPassword, ICryptoGetTextPassword2
{
    private readonly ExtractItem?[] _items; // indexed by archive index
    private readonly Action<string?, double>? _progress;
    private readonly CancellationToken _cancel;
    private readonly string? _password;
    private readonly Dictionary<uint, ExtractFileResult> _results = new();
    private OutStream? _currentOut;
    private uint _currentIndex;
    private ulong _totalBytes;
    private ulong _doneBytes;
    private string? _currentPath;

    public IReadOnlyDictionary<uint, ExtractFileResult> Results => _results;

    public ExtractCallback(ExtractItem?[] items, Action<string?, double>? progress, CancellationToken cancel, string? password)
    {
        _items = items;
        _progress = progress;
        _cancel = cancel;
        _password = password;
    }

    // ICryptoGetTextPassword / ICryptoGetTextPassword2 — 7z.dll asks the *extract* callback
    // for the password when decrypting data.
    public int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string password)
    {
        password = _password ?? string.Empty;
        return HResult.S_OK;
    }

    public int CryptoGetTextPassword2(out int passwordIsDefined, [MarshalAs(UnmanagedType.BStr)] out string password)
    {
        passwordIsDefined = _password is null ? 0 : 1;
        password = _password ?? string.Empty;
        return HResult.S_OK;
    }

    // IProgress
    public int SetTotal(ulong total)
    {
        _totalBytes = total;
        return HResult.S_OK;
    }

    public int SetCompleted([In] ref ulong completeValue)
    {
        _doneBytes = completeValue;
        Report();
        return HResult.S_OK;
    }

    public int PrepareOperation(int askExtractMode) => HResult.S_OK;

    public int GetStream(uint index, out ISequentialOutStream outStream, int askExtractMode)
    {
        outStream = null!;
        _currentIndex = index;

        if (_cancel.IsCancellationRequested)
            return HResult.E_ABORT;

        // askExtractMode: 0 = extract, 1 = test, 2 = skip
        if (askExtractMode != 0)
            return HResult.S_OK;

        var item = index < _items.Length ? _items[index] : null;
        if (item is null || item.Skip)
            return HResult.S_OK;

        _currentPath = item.TargetPath;

        if (item.IsDirectory)
        {
            Directory.CreateDirectory(item.TargetPath);
            return HResult.S_OK;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);

        FileStream fs;
        if (File.Exists(item.TargetPath))
        {
            if (!item.Overwrite)
                return HResult.S_OK; // skip
            fs = new FileStream(item.TargetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        else
        {
            fs = new FileStream(item.TargetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        _currentOut = new OutStream(fs);
        outStream = _currentOut;
        return HResult.S_OK;
    }

    public int SetOperationResult(int result)
    {
        _currentOut?.Dispose();
        _currentOut = null;

        var r = (OperationResult)result;
        _results[_currentIndex] = r switch
        {
            OperationResult.Ok => ExtractFileResult.Ok,
            OperationResult.CrcError => ExtractFileResult.CrcError,
            OperationResult.DataError or OperationResult.UnexpectedEnd or OperationResult.DataAfterEnd => ExtractFileResult.DataError,
            OperationResult.UnsupportedMethod => ExtractFileResult.UnsupportedMethod,
            _ => ExtractFileResult.Error,
        };
        return HResult.S_OK;
    }

    private void Report()
    {
        double fraction = _totalBytes == 0 ? 0 : (double)_doneBytes / _totalBytes;
        _progress?.Invoke(_currentPath, Math.Clamp(fraction, 0, 1));
    }
}

/// <summary>Source item handed to 7z.dll when creating an archive.</summary>
internal sealed class UpdateItem
{
    public required string SourcePath { get; init; }
    public required string ArchivePath { get; init; }
    public required bool IsDirectory { get; init; }
    public ulong Size { get; init; }
    public DateTime MTimeUtc { get; init; }
    public DateTime CTimeUtc { get; init; }
    public DateTime ATimeUtc { get; init; }
}

internal sealed class UpdateCallback : IArchiveUpdateCallback, ICryptoGetTextPassword, ICryptoGetTextPassword2
{
    private readonly IReadOnlyList<UpdateItem> _items;
    private readonly string? _password;
    private readonly Action<string?, double>? _progress;
    private readonly CancellationToken _cancel;
    private readonly ulong _totalBytes;

    public UpdateCallback(IReadOnlyList<UpdateItem> items, string? password, Action<string?, double>? progress, CancellationToken cancel)
    {
        _items = items;
        _password = password;
        _progress = progress;
        _cancel = cancel;
        _totalBytes = items.Aggregate(0UL, (sum, it) => sum + it.Size);
    }

    // IProgress
    public int SetTotal(ulong total) => HResult.S_OK;
    public int SetCompleted([In] ref ulong completeValue)
    {
        double fraction = _totalBytes == 0 ? 0 : (double)completeValue / _totalBytes;
        _progress?.Invoke(null, Math.Clamp(fraction, 0, 1));
        return HResult.S_OK;
    }

    public int GetUpdateItemInfo(uint index, out int newData, out int newProps, out uint indexInArchive)
    {
        if (_cancel.IsCancellationRequested)
        {
            newData = newProps = 0;
            indexInArchive = 0;
            return HResult.E_ABORT;
        }
        newData = 1;
        newProps = 1;
        indexInArchive = uint.MaxValue; // no existing item
        return HResult.S_OK;
    }

    public int GetProperty(uint index, uint propID, ref PropVariant value)
    {
        var item = _items[(int)index];
        switch ((PropId)propID)
        {
            case PropId.Path:
                value.SetBstr(item.ArchivePath);
                break;
            case PropId.IsDir:
                value.SetBool(item.IsDirectory);
                break;
            case PropId.Attrib:
                value.SetUI4(item.IsDirectory ? (uint)FileAttributes.Directory : (uint)FileAttributes.Normal);
                break;
            case PropId.MTime:
                value.SetFileTime(item.MTimeUtc.ToFileTimeUtc());
                break;
            case PropId.CTime:
                value.SetFileTime(item.CTimeUtc.ToFileTimeUtc());
                break;
            case PropId.ATime:
                value.SetFileTime(item.ATimeUtc.ToFileTimeUtc());
                break;
            case PropId.Size:
                value.SetUI8(item.Size);
                break;
        }
        return HResult.S_OK;
    }

    public int GetStream(uint index, out ISequentialInStream inStream)
    {
        inStream = null!;
        var item = _items[(int)index];
        if (item.IsDirectory)
            return HResult.S_OK;

        var fs = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        inStream = new InStream(fs);
        return HResult.S_OK;
    }

    public int SetOperationResult(int operationResult) => HResult.S_OK;

    // ICryptoGetTextPassword / ICryptoGetTextPassword2 (for creating encrypted archives)
    public int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string password)
    {
        password = _password ?? string.Empty;
        return HResult.S_OK;
    }

    public int CryptoGetTextPassword2(out int passwordIsDefined, [MarshalAs(UnmanagedType.BStr)] out string password)
    {
        passwordIsDefined = _password is null ? 0 : 1;
        password = _password ?? string.Empty;
        return HResult.S_OK;
    }
}
