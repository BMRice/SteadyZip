using System.Runtime.InteropServices;

namespace UnzipTool.Core.SevenZip;

/// <summary>
/// Raw COM interop bindings for 7z.dll (the 7-Zip COM API, not the command line).
/// GUIDs, vtable layouts and PROPIDs follow the 7-Zip SDK public headers
/// (IArchive.h / IStream.h / IPassword.h / IProgress.h / PropID.h / RegisterArc.h).
///
/// Note: each interface is declared FLAT (no managed interface inheritance), because
/// the CLR's CCW generation mis-handles [ComImport] inheritance and crashes with an
/// ExecutionEngineException. The vtable order below matches the C++ headers exactly.
/// </summary>
internal static class NativeMethods
{
    // STDAPI CreateObject(const GUID *clsid, const GUID *iid, void **outObject)
    [DllImport("7z.dll", CallingConvention = CallingConvention.StdCall, PreserveSig = true)]
    internal static extern int CreateObject(
        [In] ref Guid classID,
        [In] ref Guid interfaceID,
        [Out, MarshalAs(UnmanagedType.IUnknown)] out object outObject);

    internal static T Create<T>(Guid classId, Guid interfaceId) where T : class
    {
        int hr = CreateObject(ref classId, ref interfaceId, out object obj);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
        return (T)obj;
    }
}

// ---------------------------------------------------------------------------
// Interface IDs (IID)
// ---------------------------------------------------------------------------
internal static class Iids
{
    internal static readonly Guid ISequentialInStream = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x03, 0x00, 0x01, 0x00, 0x00);
    internal static readonly Guid ISequentialOutStream = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x03, 0x00, 0x02, 0x00, 0x00);
    internal static readonly Guid IInStream = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x03, 0x00, 0x03, 0x00, 0x00);
    internal static readonly Guid IOutStream = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x03, 0x00, 0x04, 0x00, 0x00);
    internal static readonly Guid IStreamGetSize = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x03, 0x00, 0x06, 0x00, 0x00);
    internal static readonly Guid IProgress = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00);
    internal static readonly Guid IArchiveOpenCallback = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x06, 0x00, 0x10, 0x00, 0x00);
    internal static readonly Guid IArchiveExtractCallback = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x06, 0x00, 0x20, 0x00, 0x00);
    internal static readonly Guid IArchiveOpenVolumeCallback = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x06, 0x00, 0x30, 0x00, 0x00);
    internal static readonly Guid IInArchiveGetStream = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x06, 0x00, 0x40, 0x00, 0x00);
    internal static readonly Guid IArchiveOpenSetSubArchiveName = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x06, 0x00, 0x50, 0x00, 0x00);
    internal static readonly Guid IInArchive = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x06, 0x00, 0x60, 0x00, 0x00);
    internal static readonly Guid IOutArchive = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x06, 0x00, 0xA0, 0x00, 0x00);
    internal static readonly Guid IArchiveUpdateCallback = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x06, 0x00, 0x80, 0x00, 0x00);
    internal static readonly Guid IArchiveUpdateCallback2 = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x06, 0x00, 0x82, 0x00, 0x00);
    internal static readonly Guid ISetProperties = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x06, 0x00, 0x03, 0x00, 0x00);
    internal static readonly Guid ICryptoGetTextPassword = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x05, 0x00, 0x10, 0x00, 0x00);
    internal static readonly Guid ICryptoGetTextPassword2 = new(0x23170F69, 0x40C1, 0x278A, 0x00, 0x00, 0x00, 0x05, 0x00, 0x11, 0x00, 0x00);
}

// ---------------------------------------------------------------------------
// Archive handler class IDs (CLSID)
// ---------------------------------------------------------------------------
internal static class FormatIds
{
    internal static readonly Guid Zip = new(0x23170F69, 0x40C1, 0x278A, 0x10, 0x00, 0x00, 0x01, 0x10, 0x01, 0x00, 0x00);
    internal static readonly Guid SevenZip = new(0x23170F69, 0x40C1, 0x278A, 0x10, 0x00, 0x00, 0x01, 0x10, 0x07, 0x00, 0x00);
    internal static readonly Guid Rar = new(0x23170F69, 0x40C1, 0x278A, 0x10, 0x00, 0x00, 0x01, 0x10, 0x03, 0x00, 0x00);
    internal static readonly Guid Tar = new(0x23170F69, 0x40C1, 0x278A, 0x10, 0x00, 0x00, 0x01, 0x10, 0xEE, 0x00, 0x00);
    internal static readonly Guid GZip = new(0x23170F69, 0x40C1, 0x278A, 0x10, 0x00, 0x00, 0x01, 0x10, 0xEF, 0x00, 0x00);
    internal static readonly Guid BZip2 = new(0x23170F69, 0x40C1, 0x278A, 0x10, 0x00, 0x00, 0x01, 0x10, 0x02, 0x02, 0x00);
    internal static readonly Guid Split = new(0x23170F69, 0x40C1, 0x278A, 0x10, 0x00, 0x00, 0x01, 0x10, 0xEA, 0x00, 0x00);
}

// ---------------------------------------------------------------------------
// PROPID values (7-Zip PropID.h)
// ---------------------------------------------------------------------------
internal enum PropId : uint
{
    NoProperty = 0,
    Path = 3,
    Name = 4,
    Extension = 5,
    IsDir = 6,
    Size = 7,
    PackSize = 8,
    Attrib = 9,
    CTime = 10,
    ATime = 11,
    MTime = 12,
    Solid = 13,
    Encrypted = 15,
    SplitBefore = 16,
    SplitAfter = 17,
    Crc = 19,
    Type = 20,
    Method = 22,
    HostOS = 23,
    NumSubDirs = 31,
    NumSubFiles = 32,
    Volume = 34,
    IsVolume = 35,
    NumVolumes = 39,
    TotalSize = 56,
    Error = 55,
}

// ---------------------------------------------------------------------------
// VARTYPE values used by 7-Zip's PROPVARIANT
// ---------------------------------------------------------------------------
internal enum VarType : ushort
{
    VT_EMPTY = 0,
    VT_BSTR = 8,
    VT_BOOL = 11,
    VT_UI4 = 19,
    VT_UI8 = 21,
    VT_FILETIME = 64,
}

// ---------------------------------------------------------------------------
// NExtract::NOperationResult
// ---------------------------------------------------------------------------
internal enum OperationResult : int
{
    Ok = 0,
    UnsupportedMethod = 1,
    DataError = 2,
    CrcError = 3,
    Unavailable = 4,
    UnexpectedEnd = 5,
    DataAfterEnd = 6,
    IsNotArc = 7,
    HeadersError = 8,
    WrongPassword = 9,
}

// ---------------------------------------------------------------------------
// PROPVARIANT (24 bytes on x64; 7-Zip uses the standard OLE layout)
// ---------------------------------------------------------------------------
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort Vt;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public IntPtr Value0; // first 8 bytes of the union
    public IntPtr Value1; // second 8 bytes (unused for the types we read)

    public bool IsEmpty => Vt == (ushort)VarType.VT_EMPTY;

    public ulong GetUI8() => unchecked((ulong)Value0.ToInt64());

    // Only the low 4 bytes are meaningful; the union's high bytes may be uninitialized.
    public uint GetUI4() => unchecked((uint)(Value0.ToInt64() & 0xFFFFFFFF));

    // VARIANT_BOOL occupies the low 2 bytes (0xFFFF = true).
    public bool GetBool() => (Value0.ToInt64() & 0xFFFF) != 0;

    public long GetFileTime() => Value0.ToInt64();

    public string? GetString() => Marshal.PtrToStringUni(Value0);

    /// <summary>Frees resources owned by the variant (e.g. the BSTR it points to).</summary>
    public void Clear()
    {
        if (Vt == (ushort)VarType.VT_BSTR && Value0 != IntPtr.Zero)
        {
            Marshal.FreeBSTR(Value0);
            Value0 = IntPtr.Zero;
        }
        Vt = (ushort)VarType.VT_EMPTY;
    }

    public void SetBstr(string value)
    {
        Clear();
        Vt = (ushort)VarType.VT_BSTR;
        Value0 = Marshal.StringToBSTR(value);
    }

    public void SetUI8(ulong value)
    {
        Clear();
        Vt = (ushort)VarType.VT_UI8;
        Value0 = unchecked((IntPtr)(long)value);
    }

    public void SetUI4(uint value)
    {
        Clear();
        Vt = (ushort)VarType.VT_UI4;
        Value0 = (IntPtr)(long)value;
    }

    public void SetBool(bool value)
    {
        Clear();
        Vt = (ushort)VarType.VT_BOOL;
        Value0 = (IntPtr)(value ? -1 : 0);
    }

    public void SetFileTime(long fileTime)
    {
        Clear();
        Vt = (ushort)VarType.VT_FILETIME;
        Value0 = new IntPtr(fileTime);
    }
}

// ---------------------------------------------------------------------------
// Streams (flat vtable declarations — see class note)
// ---------------------------------------------------------------------------

[ComImport, Guid("23170F69-40C1-278A-0000-000300010000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISequentialInStream
{
    [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000300020000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISequentialOutStream
{
    [PreserveSig] int Write(IntPtr data, uint size, IntPtr processedSize);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000300030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInStream
{
    [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
    [PreserveSig] int Seek(long offset, uint seekOrigin, IntPtr newPosition);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000300040000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOutStream
{
    [PreserveSig] int Write(IntPtr data, uint size, IntPtr processedSize);
    [PreserveSig] int Seek(long offset, uint seekOrigin, IntPtr newPosition);
    [PreserveSig] int SetSize(long newSize);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000300060000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IStreamGetSize
{
    [PreserveSig] int GetSize(out ulong size);
}

// ---------------------------------------------------------------------------
// Progress / callbacks (flat)
// ---------------------------------------------------------------------------

[ComImport, Guid("23170F69-40C1-278A-0000-000000050000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IProgress
{
    [PreserveSig] int SetTotal(ulong total);
    [PreserveSig] int SetCompleted([In] ref ulong completeValue);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveOpenCallback
{
    [PreserveSig] int SetTotal(IntPtr files, IntPtr bytes);
    [PreserveSig] int SetCompleted(IntPtr files, IntPtr bytes);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600300000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveOpenVolumeCallback
{
    [PreserveSig] int GetProperty(uint propID, ref PropVariant value);
    [PreserveSig] int GetStream([MarshalAs(UnmanagedType.LPWStr)] string name, out IInStream inStream);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600200000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveExtractCallback
{
    [PreserveSig] int SetTotal(ulong total);
    [PreserveSig] int SetCompleted([In] ref ulong completeValue);
    [PreserveSig] int GetStream(uint index, out ISequentialOutStream outStream, int askExtractMode);
    [PreserveSig] int PrepareOperation(int askExtractMode);
    [PreserveSig] int SetOperationResult(int result);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000500100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICryptoGetTextPassword
{
    [PreserveSig] int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string password);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000500110000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICryptoGetTextPassword2
{
    [PreserveSig] int CryptoGetTextPassword2(out int passwordIsDefined, [MarshalAs(UnmanagedType.BStr)] out string password);
}

// ---------------------------------------------------------------------------
// Archives (flat)
// ---------------------------------------------------------------------------

[ComImport, Guid("23170F69-40C1-278A-0000-000600600000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInArchive
{
    [PreserveSig] int Open(IInStream stream, IntPtr maxCheckStartPosition, IArchiveOpenCallback openCallback);
    [PreserveSig] int Close();
    [PreserveSig] int GetNumberOfItems(out uint numItems);
    [PreserveSig] int GetProperty(uint index, uint propID, ref PropVariant value);
    [PreserveSig] int Extract(IntPtr indices, uint numItems, int testMode, IArchiveExtractCallback extractCallback);
    [PreserveSig] int GetArchiveProperty(uint propID, ref PropVariant value);
    [PreserveSig] int GetNumberOfProperties(out uint numProps);
    [PreserveSig] int GetPropertyInfo(uint index, [MarshalAs(UnmanagedType.BStr)] out string name, out uint propID, out ushort varType);
    [PreserveSig] int GetNumberOfArchiveProperties(out uint numProps);
    [PreserveSig] int GetArchivePropertyInfo(uint index, [MarshalAs(UnmanagedType.BStr)] out string name, out uint propID, out ushort varType);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600A00000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOutArchive
{
    [PreserveSig] int UpdateItems(ISequentialOutStream outStream, uint numItems, IArchiveUpdateCallback updateCallback);
    [PreserveSig] int GetFileTimeType(out uint type);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISetProperties
{
    [PreserveSig] int SetProperties(IntPtr names, IntPtr values, uint numProps);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600800000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveUpdateCallback
{
    [PreserveSig] int SetTotal(ulong total);
    [PreserveSig] int SetCompleted([In] ref ulong completeValue);
    [PreserveSig] int GetUpdateItemInfo(uint index, out int newData, out int newProps, out uint indexInArchive);
    [PreserveSig] int GetProperty(uint index, uint propID, ref PropVariant value);
    [PreserveSig] int GetStream(uint index, out ISequentialInStream inStream);
    [PreserveSig] int SetOperationResult(int operationResult);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600820000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveUpdateCallback2
{
    [PreserveSig] int SetTotal(ulong total);
    [PreserveSig] int SetCompleted([In] ref ulong completeValue);
    [PreserveSig] int GetUpdateItemInfo(uint index, out int newData, out int newProps, out uint indexInArchive);
    [PreserveSig] int GetProperty(uint index, uint propID, ref PropVariant value);
    [PreserveSig] int GetStream(uint index, out ISequentialInStream inStream);
    [PreserveSig] int SetOperationResult(int operationResult);
    [PreserveSig] int GetVolumeSize(uint index, out ulong size);
    [PreserveSig] int GetVolumeStream(uint index, out ISequentialOutStream volumeStream);
}
