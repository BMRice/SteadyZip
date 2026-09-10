using System.Runtime.InteropServices;

namespace UnzipTool.Core.SevenZip;

internal static class HResult
{
    public const int S_OK = 0;
    public const int E_ABORT = unchecked((int)0x80004004);
}

/// <summary>
/// Open callback for 7z.dll. Supplies the decryption password, and — for split
/// archives — each subsequent volume via <see cref="IArchiveOpenVolumeCallback"/>.
/// This is also the seam for the "delete volume once its data has been read" feature:
/// when <see cref="DeleteVolumesAfterRead"/> is on, requesting volume N+1 disposes and
/// permanently deletes volume N.
/// </summary>
/// <summary>Open callback: supplies the decryption password when 7z.dll asks for it.
/// Volume handling lives in <see cref="MultiVolumeStream"/>; a single-volume archive
/// uses a plain <see cref="InStream"/> and never needs a volume callback.</summary>
internal sealed class OpenCallback : IArchiveOpenCallback, ICryptoGetTextPassword, ICryptoGetTextPassword2
{
    private readonly string? _password;

    public OpenCallback(string? password) => _password = password;

    // IArchiveOpenCallback
    public int SetTotal(IntPtr files, IntPtr bytes) => HResult.S_OK;
    public int SetCompleted(IntPtr files, IntPtr bytes) => HResult.S_OK;

    // ICryptoGetTextPassword
    public int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string password)
    {
        password = _password ?? string.Empty;
        return HResult.S_OK;
    }

    // ICryptoGetTextPassword2
    public int CryptoGetTextPassword2(out int passwordIsDefined, [MarshalAs(UnmanagedType.BStr)] out string password)
    {
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
