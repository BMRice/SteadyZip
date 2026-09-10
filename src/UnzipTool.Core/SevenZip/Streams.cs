using System.Runtime.InteropServices;

namespace UnzipTool.Core.SevenZip;

/// <summary>Managed input stream handed to 7z.dll (wraps a .NET Stream).</summary>
internal sealed class InStream : IInStream, ISequentialInStream, IStreamGetSize, IDisposable
{
    private readonly Stream _stream;
    private byte[]? _buffer;

    public InStream(Stream stream) => _stream = stream;

    public Stream BaseStream => _stream;

    public int Read(IntPtr data, uint size, IntPtr processedSize)
    {
        if (_buffer is null || _buffer.Length < size)
            _buffer = new byte[size];

        int read = _stream.Read(_buffer, 0, (int)size);
        if (read > 0)
            Marshal.Copy(_buffer, 0, data, read);
        if (processedSize != IntPtr.Zero)
            Marshal.WriteInt32(processedSize, read);
        return 0;
    }

    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        long pos = _stream.Seek(offset, (SeekOrigin)seekOrigin);
        if (newPosition != IntPtr.Zero)
            Marshal.WriteInt64(newPosition, pos);
        return 0;
    }

    public int GetSize(out ulong size)
    {
        size = (ulong)_stream.Length;
        return 0;
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>Managed output stream handed to 7z.dll (wraps a .NET Stream).</summary>
internal sealed class OutStream : IOutStream, ISequentialOutStream, IDisposable
{
    private readonly Stream _stream;
    private byte[]? _buffer;

    public OutStream(Stream stream) => _stream = stream;

    public int Write(IntPtr data, uint size, IntPtr processedSize)
    {
        if (size == 0)
        {
            if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
            return 0;
        }
        if (_buffer is null || _buffer.Length < size)
            _buffer = new byte[size];

        Marshal.Copy(data, _buffer, 0, (int)size);
        _stream.Write(_buffer, 0, (int)size);
        if (processedSize != IntPtr.Zero)
            Marshal.WriteInt32(processedSize, (int)size);
        return 0;
    }

    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        long pos = _stream.Seek(offset, (SeekOrigin)seekOrigin);
        if (newPosition != IntPtr.Zero)
            Marshal.WriteInt64(newPosition, pos);
        return 0;
    }

    public int SetSize(long newSize)
    {
        _stream.SetLength(newSize);
        return 0;
    }

    public void Dispose() => _stream.Dispose();
}
