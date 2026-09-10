using System.Runtime.InteropServices;
using System.Text.Json;

namespace UnzipTool.Core;

/// <summary>
/// DPAPI-encrypted local password library. The ordered list is serialized to JSON and
/// encrypted with <c>CryptProtectData</c> (user scope), binding it to the current Windows
/// account. Stored under %LOCALAPPDATA%\UnzipTool\passwords.dat.
/// </summary>
public sealed class PasswordStore
{
    private readonly string _path;

    public PasswordStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnzipTool", "passwords.dat");
    }

    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(_path))
            return Array.Empty<string>();

        try
        {
            byte[] cipher = File.ReadAllBytes(_path);
            byte[] plain = ProtectedData.Unprotect(cipher);
            return JsonSerializer.Deserialize<List<string>>(plain) ?? new List<string>();
        }
        catch (Exception)
        {
            // Corrupt or user changed: treat as empty rather than crashing.
            return Array.Empty<string>();
        }
    }

    public void Save(IReadOnlyList<string> passwords)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(passwords.Distinct().ToList());
        byte[] cipher = ProtectedData.Protect(plain);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, cipher);
    }

    public void Add(string password)
    {
        var list = Load().ToList();
        if (!list.Contains(password))
            list.Add(password);
        Save(list);
    }

    public void Remove(string password)
    {
        var list = Load().Where(p => p != password).ToList();
        Save(list);
    }

    private static class ProtectedData
    {
        private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptProtectData(
            ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(
            ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public static byte[] Protect(byte[] plain)
        {
            var inBlob = ToBlob(plain);
            var entropy = default(DataBlob);
            try
            {
                if (!CryptProtectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                    throw new InvalidOperationException($"CryptProtectData failed: {Marshal.GetLastWin32Error()}");
                try { return FromBlob(outBlob); }
                finally { LocalFree(outBlob.pbData); }
            }
            finally { LocalFree(inBlob.pbData); }
        }

        public static byte[] Unprotect(byte[] cipher)
        {
            var inBlob = ToBlob(cipher);
            var entropy = default(DataBlob);
            try
            {
                if (!CryptUnprotectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
                    throw new InvalidOperationException($"CryptUnprotectData failed: {Marshal.GetLastWin32Error()}");
                try { return FromBlob(outBlob); }
                finally { LocalFree(outBlob.pbData); }
            }
            finally { LocalFree(inBlob.pbData); }
        }

        private static DataBlob ToBlob(byte[] data)
        {
            var blob = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
            Marshal.Copy(data, 0, blob.pbData, data.Length);
            return blob;
        }

        private static byte[] FromBlob(DataBlob blob)
        {
            var data = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, data, 0, blob.cbData);
            return data;
        }
    }
}
