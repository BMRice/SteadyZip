using System.Runtime.InteropServices;

namespace UnzipTool.Core.SevenZip;

/// <summary>
/// A single seekable IInStream that presents the concatenation of a split archive's
/// volumes to the format handler as one logical stream. This is the ADR-0001 seam:
/// when a volume is exhausted by a *sequential* read (and deletion is armed), the
/// stream closes and permanently deletes that volume before moving on.
///
/// Deletion is only safe for sequential reads (solid archives); the engine arms it via
/// <see cref="AllowDelete"/> only after the header phase, and Seek never deletes.
/// </summary>
internal sealed class MultiVolumeStream : IInStream, ISequentialInStream, IStreamGetSize, IDisposable
{
    private readonly string[] _volumes;
    private readonly long[] _starts;   // logical start offset of each volume
    private readonly long _totalSize;
    private readonly Action<string>? _trace;

    private FileStream? _current;
    private int _currentIndex = -1;
    private long _position;
    private byte[]? _buffer;

    public bool AllowDelete { get; set; }

    public MultiVolumeStream(IReadOnlyList<string> volumes, Action<string>? trace = null)
    {
        _volumes = volumes.ToArray();
        _trace = trace;
        _starts = new long[_volumes.Length];
        long acc = 0;
        for (int i = 0; i < _volumes.Length; i++)
        {
            _starts[i] = acc;
            acc += new FileInfo(_volumes[i]).Length;
        }
        _totalSize = acc;
    }

    public int Read(IntPtr data, uint size, IntPtr processedSize)
    {
        long remaining = Math.Min((long)size, _totalSize - _position);
        int totalRead = 0;

        while (totalRead < remaining)
        {
            // Advance past exhausted volumes, deleting each (when armed) as it completes.
            if (_current is null || _current.Position >= _current.Length)
            {
                if (_current is not null)
                {
                    string done = _volumes[_currentIndex];
                    CloseCurrent();
                    if (AllowDelete)
                        TryDelete(done);
                }
                if (_currentIndex + 1 >= _volumes.Length)
                    break; // total EOF
                _currentIndex++;
                _current = new FileStream(_volumes[_currentIndex], FileMode.Open, FileAccess.Read, FileShare.Read);
                continue;
            }

            int toRead = (int)Math.Min(remaining - totalRead, _current.Length - _current.Position);
            if (toRead <= 0)
                continue;

            if (_buffer is null || _buffer.Length < toRead)
                _buffer = new byte[toRead];

            int read = _current.Read(_buffer, 0, toRead);
            if (read > 0)
                Marshal.Copy(_buffer, 0, IntPtr.Add(data, totalRead), read);
            totalRead += read;
            _position += read;
        }

        if (processedSize != IntPtr.Zero)
            Marshal.WriteInt32(processedSize, totalRead);
        return HResult.S_OK;
    }

    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        long target = seekOrigin switch
        {
            0 => offset,
            1 => _position + offset,
            2 => _totalSize + offset,
            _ => _position,
        };
        target = Math.Clamp(target, 0, _totalSize);

        int idx = FindVolume(target);
        if (_currentIndex != idx)
        {
            CloseCurrent();
            _currentIndex = idx;
            _current = new FileStream(_volumes[idx], FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        _current!.Seek(target - _starts[idx], SeekOrigin.Begin);
        _position = target;

        if (newPosition != IntPtr.Zero)
            Marshal.WriteInt64(newPosition, _position);
        return HResult.S_OK;
    }

    public int GetSize(out ulong size)
    {
        size = (ulong)_totalSize;
        return HResult.S_OK;
    }

    public void Dispose() => CloseCurrent();

    private int FindVolume(long logical)
    {
        int lo = 0, hi = _starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_starts[mid] <= logical) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private void CloseCurrent()
    {
        _current?.Dispose();
        _current = null;
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
