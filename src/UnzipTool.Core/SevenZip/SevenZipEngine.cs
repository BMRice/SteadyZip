using System.Runtime.InteropServices;
using UnzipTool.Core.SevenZip;

namespace UnzipTool.Core;

/// <summary>Probes whether 7z.dll is loadable and its COM entry point works.</summary>
public static class SevenZipLibrary
{
    private static int? _probe;

    public static bool IsAvailable()
    {
        if (_probe.HasValue)
            return _probe.Value == 0;
        try
        {
            // Create a throwaway Zip IInArchive; fails if 7z.dll is missing/incompatible.
            var archive = NativeMethods.Create<IInArchive>(FormatIds.Zip, Iids.IInArchive);
            _probe = 0;
        }
        catch (Exception)
        {
            _probe = -1;
        }
        return _probe.Value == 0;
    }
}

/// <summary>An opened archive plus the resources needed to read its volumes.</summary>
internal sealed class OpenedArchive : IDisposable
{
    public required IInArchive Archive { get; init; }
    public required IDisposable Stream { get; init; }
    public required MultiVolumeStream? MultiVolume { get; init; }

    /// <summary>Set for independent-archive volume sets (RAR .partN.rar), where deletion
    /// during extraction is driven by read progress instead of by a concatenated stream.</summary>
    public VolumeReaper? Reaper { get; init; }
    public required List<ArchiveEntry> Entries { get; init; }
    public required ArchiveFormat Format { get; init; }
    public required bool IsEncrypted { get; init; }
    public required ulong TotalUnpackedSize { get; init; }
    public required IReadOnlyList<string> Volumes { get; init; }

    public void Dispose()
    {
        try { Archive.Close(); } catch { /* ignore */ }
        Stream.Dispose();
        // 7z.dll never disposes the volume streams it was handed, so we do.
        Reaper?.Dispose();
    }
}

/// <summary>7z.dll-backed engine for open / list / test / extract / create.</summary>
public sealed class SevenZipEngine
{
    /// <summary>Opens an archive and lists its entries (no extraction).</summary>
    public ArchiveContents Open(string path, string? password = null, CancellationToken cancel = default)
    {
        using var handle = OpenInternal(path, password);
        return new ArchiveContents
        {
            Format = handle.Format,
            Entries = handle.Entries,
            IsMultiVolume = handle.Volumes.Count > 1,
            Volumes = handle.Volumes,
            IsEncrypted = handle.IsEncrypted,
            TotalUnpackedSize = handle.TotalUnpackedSize,
        };
    }

    /// <summary>Runs a full per-file CRC test (test-mode extract) without writing anything.</summary>
    public ExtractReport Test(string path, IReadOnlyList<string>? passwords = null, IProgress<ProgressUpdate>? progress = null, CancellationToken cancel = default)
    {
        var list = passwords is { Count: > 0 }
            ? passwords.Select(p => (string?)p).ToList()
            : new List<string?> { null };
        string? lastError = null;

        foreach (var pw in list)
        {
            try
            {
                using var handle = OpenInternal(path, pw);
                var results = RunTest(handle.Archive, progress, cancel, pw);
                if (AllOk(results))
                {
                    return new ExtractReport { FileResults = results, OkCount = results.Count, ErrorCount = 0, Cancelled = cancel.IsCancellationRequested };
                }
                lastError = Describe(results);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        return new ExtractReport
        {
            OkCount = 0,
            ErrorCount = 1,
            Errors = new[] { lastError ?? "Integrity test failed." },
            Cancelled = cancel.IsCancellationRequested,
        };
    }

    /// <summary>Extracts. Always runs the integrity test first; on failure it aborts and
    /// never deletes any volume.</summary>
    public ExtractReport Extract(string path, ExtractOptions options, IProgress<ProgressUpdate>? progress = null, CancellationToken cancel = default)
    {
        var list = options.Passwords is { Count: > 0 }
            ? options.Passwords.Select(p => (string?)p).ToList()
            : new List<string?> { null };
        string? foundPassword = null;
        bool testPassed = false;
        string? lastError = null;

        // Phase 1: integrity test, trying each password in order until one passes.
        foreach (var pw in list)
        {
            try
            {
                using var testHandle = OpenInternal(path, pw);
                var testResults = RunTest(testHandle.Archive, progress, cancel, pw);
                if (AllOk(testResults))
                {
                    foundPassword = pw;
                    testPassed = true;
                    break;
                }
                lastError = Describe(testResults);
            }
            catch (Exception ex)
            {
                lastError = ex.Message; // wrong header password or unsupported — try the next one
            }
        }

        if (!testPassed)
        {
            // Integrity test never passed; abort without deleting anything.
            return Fail($"Integrity test failed: {lastError ?? "archive could not be opened"}. Extraction aborted; no volumes were deleted.");
        }

        // Phase 2: resolve targets, then extract (fresh open, delete-volumes armed if requested).
        var deleteVolumes = options.DeleteVolumesAfterRead;
        var handle = OpenInternal(path, foundPassword);
        if (deleteVolumes)
        {
            // Armed only now: the header phase above has already touched every volume, and
            // the full-CRC integrity test has passed.
            if (handle.MultiVolume is not null)
                handle.MultiVolume.AllowDelete = true;
            handle.Reaper?.Arm();
        }

        ExtractItem?[] items;
        IReadOnlyDictionary<uint, ExtractFileResult> results;
        try
        {
            items = ResolveTargets(handle.Entries, options, path);
            results = RunExtract(handle.Archive, items, progress, cancel, foundPassword);
        }
        finally
        {
            // Release all volume streams so any remaining volumes can be deleted below.
            handle.Dispose();
        }

        int ok = results.Values.Count(r => r == ExtractFileResult.Ok);
        int errors = results.Values.Count(r => r != ExtractFileResult.Ok);

        // With delete-volumes-while-extracting, every volume except the final one is already
        // gone; the final volume (which holds the directory, read only during Open) remains.
        // Remove it now that extraction succeeded.
        if ((deleteVolumes || options.DeleteSourceAfter) && errors == 0 && !cancel.IsCancellationRequested)
        {
            DeleteSourceVolumes(path);
        }

        return new ExtractReport
        {
            FileResults = results,
            OkCount = ok,
            ErrorCount = errors,
            Errors = results.Where(kv => kv.Value != ExtractFileResult.Ok)
                            .Select(kv => $"{kv.Key}: {kv.Value}")
                            .ToList(),
            Cancelled = cancel.IsCancellationRequested,
        };
    }

    /// <summary>Creates a ZIP/7z archive, optionally split into volumes. RAR creation is
    /// routed to rar.exe. 7z.dll itself only writes a single stream, so a split archive is
    /// produced by creating one archive and then chunking it — the volumes of 7z/zip/rar
    /// are byte-slices of a single archive (verified in the spike).</summary>
    public void Create(CreateOptions options, IProgress<ProgressUpdate>? progress = null, CancellationToken cancel = default)
    {
        if (options.Format != ArchiveFormat.Zip && options.Format != ArchiveFormat.SevenZip)
            throw new NotSupportedException("SevenZipEngine creates ZIP and 7z only; route RAR to RarEngine.");

        if (options.VolumeSizeBytes is { } volSize && volSize > 0)
        {
            string destDir = Path.GetDirectoryName(Path.GetFullPath(options.DestinationArchive))!;
            Directory.CreateDirectory(destDir);
            string temp = Path.Combine(destDir, ".~split-" + Guid.NewGuid().ToString("N") + Path.GetExtension(options.DestinationArchive));
            try
            {
                CreateSingle(options.Format, options.SourcePaths, temp, options.Level, options.Solid, options.Password, options.EncryptHeaders, progress, cancel);
                SplitIntoVolumes(temp, options.DestinationArchive, volSize, options.Format, cancel);
            }
            finally
            {
                try { File.Delete(temp); } catch { /* best-effort */ }
            }
        }
        else
        {
            CreateSingle(options.Format, options.SourcePaths, options.DestinationArchive, options.Level, options.Solid, options.Password, options.EncryptHeaders, progress, cancel);
        }
    }

    private static void CreateSingle(ArchiveFormat format, IReadOnlyList<string> sourcePaths, string destination,
        int level, bool solid, string? password, bool encryptHeaders, IProgress<ProgressUpdate>? progress, CancellationToken cancel)
    {
        var clsid = FormatDetection.GetClsid(format);
        var outArchive = NativeMethods.Create<IOutArchive>(clsid, Iids.IOutArchive);
        var setProps = (ISetProperties)outArchive;

        ApplyCreateProperties(setProps, format, level, solid, password, encryptHeaders);

        var items = EnumerateSourceItems(sourcePaths);
        var callback = new UpdateCallback(items, password, Report(progress), cancel);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        using var fs = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        var outStream = new OutStream(fs);
        int hr = outArchive.UpdateItems(outStream, (uint)items.Count, callback);
        if (hr != HResult.S_OK)
            Marshal.ThrowExceptionForHR(hr);
        outStream.Dispose();
    }

    private static void SplitIntoVolumes(string single, string destination, ulong volumeSize, ArchiveFormat format, CancellationToken cancel)
    {
        using var src = File.OpenRead(single);
        long total = src.Length;
        int count = (int)((total + (long)volumeSize - 1) / (long)volumeSize);

        var names = new string[count];
        for (int i = 0; i < count; i++)
            names[i] = VolumeName(format, destination, i, count);

        var buf = new byte[4 * 1024 * 1024];
        for (int i = 0; i < count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            long remaining = Math.Min((long)volumeSize, total - i * (long)volumeSize);
            using var outFs = File.Create(names[i]);
            while (remaining > 0)
            {
                int n = src.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                if (n == 0) break;
                outFs.Write(buf, 0, n);
                remaining -= n;
            }
        }
    }

    private static string VolumeName(ArchiveFormat format, string destination, int index, int count)
    {
        string dir = Path.GetDirectoryName(destination)!;
        string name = Path.GetFileName(destination);
        if (count == 1)
            return destination;

        return format switch
        {
            // zip: foo.z01 … foo.zNN, last volume is foo.zip itself
            ArchiveFormat.Zip => index == count - 1
                ? destination
                : Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + ".z" + (index + 1).ToString("D2")),
            // rar: foo.part1.rar … (last volume also partN.rar)
            ArchiveFormat.Rar => Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + ".part" + (index + 1) + ".rar"),
            // 7z: foo.7z.001 … foo.7z.00N
            _ => destination + "." + (index + 1).ToString("D3"),
        };
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private static Action<string?, double>? Report(IProgress<ProgressUpdate>? progress)
        => progress is null ? null : (path, f) => progress.Report(new ProgressUpdate(path, f));

    private static ExtractReport Fail(string message) =>
        new() { OkCount = 0, ErrorCount = 1, Errors = new[] { message } };

    private static string Describe(IReadOnlyDictionary<uint, ExtractFileResult> results)
    {
        // Reached only when the run did not succeed, i.e. either nothing was reported at all
        // or some item failed. "Nothing reported" is its own diagnosis — it is what a handler
        // returns when it opens a file it does not actually understand (a RAR5 archive handed
        // to the RAR4 handler, say), so do not dress it up as a per-file failure.
        if (results.Count == 0)
            return "archive opened but reported no testable items";

        return $"{results.First(kv => kv.Value != ExtractFileResult.Ok).Value}";
    }

    private static bool AllOk(IReadOnlyDictionary<uint, ExtractFileResult> results)
        => results.Count > 0 && results.Values.All(r => r == ExtractFileResult.Ok);

    private static OpenedArchive OpenInternal(string path, string? password, Action<string>? trace = null)
    {
        var format = FormatDetection.FromPath(path);
        if (format == ArchiveFormat.Unknown)
            throw new NotSupportedException($"Unrecognized archive format: {path}");

        var volumes = VolumeSet.Enumerate(path);

        var clsid = FormatDetection.GetClsidForOpen(format, volumes.Count > 0 ? volumes[0] : path);
        var archive = NativeMethods.Create<IInArchive>(clsid, Iids.IInArchive);

        // RAR .partN.rar holds independent archives (each with its own headers), so the
        // volumes cannot be concatenated — 7z.dll's Rar handler chains them itself through
        // IArchiveOpenVolumeCallback. Byte-sliced sets (7z .001, zip .z01) are one archive
        // cut into pieces, and those are concatenated by MultiVolumeStream instead.
        bool independentVolumes = VolumeSet.AreIndependentVolumes(volumes);

        MultiVolumeStream? multi = null;
        VolumeReaper? reaper = independentVolumes ? new VolumeReaper(volumes, trace) : null;
        IInStream stream;
        IDisposable disposable;
        if (independentVolumes)
        {
            // Volume 1 is handed over directly; the handler asks for the rest by name.
            var s = new InStream(File.OpenRead(volumes[0]));
            s.OnRead = () => reaper!.Touch(0);
            reaper!.Register(0, s);
            stream = s;
            disposable = s;
        }
        else if (volumes.Count > 1)
        {
            multi = new MultiVolumeStream(volumes, trace);
            stream = multi;
            disposable = multi;
        }
        else
        {
            var s = new InStream(File.OpenRead(path));
            stream = s;
            disposable = s;
        }

        // The handler will ask for the name of the file it was handed; answer with the first volume
        // so it can name entries and (for RAR) look up siblings.
        var callback = new OpenCallback(password, Path.GetFileName(volumes.Count > 0 ? volumes[0] : path), reaper, trace);
        int hr = archive.Open(stream, IntPtr.Zero, callback);
        uint count = 0;
        if (hr == HResult.S_OK)
            archive.GetNumberOfItems(out count);

        // A header-encrypted archive whose password did not decrypt the headers comes back in more
        // than one shape: E_ABORT when the callback declined to supply a password, S_FALSE when the
        // one it supplied was wrong, and (observed too) S_OK with zero items. Only a header-
        // encrypted archive makes the handler ask for a password at all, and that request is what
        // tells all of these apart from a genuinely empty archive — which never asks, so it keeps
        // opening with zero entries.
        bool passwordProblem = hr == HResult.E_ABORT
            || (callback.PasswordRequested && (hr != HResult.S_OK || count == 0));

        if (hr != HResult.S_OK || passwordProblem)
        {
            disposable.Dispose();
            reaper?.Dispose(); // 7z.dll never disposes the volume streams it was handed
            if (passwordProblem)
                throw new ArchivePasswordException();
            // ponytail: S_FALSE ("this file is not my format") is NOT turned into an error. It is
            // tempting — junk named .zip returns it, and so does 7z.exe erroring on the same file —
            // but measured on 7z.dll 22.01 a valid empty .tar (1024 zero bytes, which 7z.exe opens
            // happily) also comes back S_FALSE depending on what the process opened earlier, and
            // once an empty tar has opened, the same file returns S_OK. The two are not
            // distinguishable, so rejecting S_FALSE would make opening an empty tar fail
            // intermittently. A corrupt archive therefore still shows an empty listing.
            Marshal.ThrowExceptionForHR(hr);
        }

        var entries = new List<ArchiveEntry>((int)count);
        bool encrypted = false;
        ulong total = 0;

        for (uint i = 0; i < count; i++)
        {
            var pathProp = GetProp(archive, i, PropId.Path);
            var isDirProp = GetProp(archive, i, PropId.IsDir);
            var sizeProp = GetProp(archive, i, PropId.Size);
            var packedProp = GetProp(archive, i, PropId.PackSize);
            var encProp = GetProp(archive, i, PropId.Encrypted);
            var mtimeProp = GetProp(archive, i, PropId.MTime);
            var crcProp = GetProp(archive, i, PropId.Crc);
            var methodProp = GetProp(archive, i, PropId.Method);

            // 7z.dll reports 7z paths with '\'; normalize to '/' for a consistent model.
            string entryPath = (pathProp.GetString() ?? string.Empty).Replace('\\', '/');
            bool isDir = isDirProp.GetBool();
            bool isEnc = encProp.GetBool();
            encrypted |= isEnc;
            ulong size = sizeProp.IsEmpty ? 0 : sizeProp.GetUI8();

            var entry = new ArchiveEntry
            {
                Index = i,
                Path = entryPath,
                IsDirectory = isDir,
                Size = size,
                PackedSize = packedProp.IsEmpty ? 0 : packedProp.GetUI8(),
                IsEncrypted = isEnc,
                ModifiedTime = mtimeProp.IsEmpty ? null : DateTime.FromFileTime(mtimeProp.GetFileTime()),
                Crc = crcProp.IsEmpty ? null : crcProp.GetUI4(),
                Method = methodProp.GetString(),
            };
            entries.Add(entry);
            total += isDir ? 0 : size;

            pathProp.Clear();
            isDirProp.Clear();
            sizeProp.Clear();
            packedProp.Clear();
            encProp.Clear();
            mtimeProp.Clear();
            crcProp.Clear();
            methodProp.Clear();
        }

        // Link each entry to its parent directory (by longest matching dir prefix).
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.IsDirectory) continue;
            int slash = e.Path.LastIndexOf('/');
            if (slash < 0) continue;
            string parent = e.Path[..slash];
            int best = -1;
            for (int j = 0; j < entries.Count; j++)
            {
                if (entries[j].IsDirectory && string.Equals(entries[j].Path, parent, StringComparison.Ordinal))
                { best = j; break; }
            }
            entries[i].ParentIndex = best;
        }

        return new OpenedArchive
        {
            Archive = archive,
            Stream = disposable,
            MultiVolume = multi,
            Reaper = reaper,
            Entries = entries,
            Format = format,
            IsEncrypted = encrypted,
            TotalUnpackedSize = total,
            Volumes = volumes,
        };
    }

    private static PropVariant GetProp(IInArchive archive, uint index, PropId prop)
    {
        var v = new PropVariant();
        archive.GetProperty(index, (uint)prop, ref v);
        return v;
    }

    private static IReadOnlyDictionary<uint, ExtractFileResult> RunTest(IInArchive archive, IProgress<ProgressUpdate>? progress, CancellationToken cancel, string? password)
    {
        var callback = new ExtractCallback(new ExtractItem?[0], Report(progress), cancel, password);
        int hr = archive.Extract(IntPtr.Zero, uint.MaxValue, testMode: 1, callback);
        if (hr != HResult.S_OK && hr != HResult.E_ABORT)
            Marshal.ThrowExceptionForHR(hr);
        return callback.Results;
    }

    private static IReadOnlyDictionary<uint, ExtractFileResult> RunExtract(IInArchive archive, ExtractItem?[] items, IProgress<ProgressUpdate>? progress, CancellationToken cancel, string? password)
    {
        var callback = new ExtractCallback(items, Report(progress), cancel, password);
        IntPtr indices = IntPtr.Zero;
        GCHandle handle = default;

        int selected = items.Count(i => i is not null);
        if (selected < items.Length)
        {
            var arr = items.Select((it, idx) => (it, idx)).Where(x => x.it is not null).Select(x => (uint)x.idx).ToArray();
            handle = GCHandle.Alloc(arr, GCHandleType.Pinned);
            indices = handle.AddrOfPinnedObject();
            int hr = archive.Extract(indices, (uint)arr.Length, testMode: 0, callback);
            handle.Free();
            if (hr != HResult.S_OK && hr != HResult.E_ABORT)
                Marshal.ThrowExceptionForHR(hr);
        }
        else
        {
            int hr = archive.Extract(IntPtr.Zero, uint.MaxValue, testMode: 0, callback);
            if (hr != HResult.S_OK && hr != HResult.E_ABORT)
                Marshal.ThrowExceptionForHR(hr);
        }

        return callback.Results;
    }

    private static ExtractItem?[] ResolveTargets(IReadOnlyList<ArchiveEntry> entries, ExtractOptions options, string archivePath)
    {
        var selected = options.SelectedIndices is { Count: > 0 }
            ? options.SelectedIndices.ToHashSet()
            : null;

        string root = options.ExtractToSubfolder
            ? Path.Combine(options.DestinationDirectory, GetBaseName(archivePath))
            : options.DestinationDirectory;
        Directory.CreateDirectory(root);

        var items = new ExtractItem?[entries.Count];
        var renameCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in entries)
        {
            if (selected is not null && !selected.Contains(e.Index))
                continue;

            string? rel = PathGuard.Sanitize(e.Path);
            if (rel is null)
            {
                // Unsafe path (zip-slip): refuse to extract this entry.
                items[(int)e.Index] = new ExtractItem { Index = e.Index, TargetPath = string.Empty, IsDirectory = e.IsDirectory, Overwrite = false, Skip = true };
                continue;
            }

            string target = PathGuard.CombineSafe(root, rel);

            if (e.IsDirectory)
            {
                items[(int)e.Index] = new ExtractItem { Index = e.Index, TargetPath = target, IsDirectory = true, Overwrite = false, Skip = false };
                continue;
            }

            bool exists = File.Exists(target);
            bool overwrite = false;
            bool skip = false;

            if (exists)
            {
                switch (options.Overwrite)
                {
                    case OverwritePolicy.Overwrite:
                        overwrite = true; break;
                    case OverwritePolicy.Skip:
                        skip = true; break;
                    case OverwritePolicy.AutoRename:
                        target = AutoRename(target, renameCounters); break;
                    case OverwritePolicy.Ask:
                        {
                            var action = options.OverwritePrompt?.Invoke(target) ?? OverwriteAction.Skip;
                            if (action == OverwriteAction.Overwrite) overwrite = true;
                            else if (action == OverwriteAction.Skip) skip = true;
                            else target = AutoRename(target, renameCounters);
                            break;
                        }
                }
            }

            items[(int)e.Index] = new ExtractItem { Index = e.Index, TargetPath = target, IsDirectory = false, Overwrite = overwrite, Skip = skip };
        }

        return items;
    }

    private static string AutoRename(string target, Dictionary<string, int> counters)
    {
        string dir = Path.GetDirectoryName(target)!;
        string name = Path.GetFileNameWithoutExtension(target);
        string ext = Path.GetExtension(target);
        int n = counters.TryGetValue(target, out var c) ? c + 1 : 1;
        counters[target] = n;
        return Path.Combine(dir, $"{name} ({n}){ext}");
    }

    /// <summary>Strips volume/split extensions to get the archive's base name:
    /// "foo.7z.001" → "foo", "foo.part1.rar" → "foo", "foo.tar.gz" → "foo".</summary>
    internal static string GetBaseName(string archivePath)
    {
        string name = Path.GetFileName(archivePath);
        string lower = name.ToLowerInvariant();

        if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tar.bz2"))
            return name[..name.LastIndexOf('.', name.LastIndexOf('.') - 1)];
        if (lower.EndsWith(".tar"))
            return name[..^4];

        // foo.partN.rar
        int partIdx = lower.IndexOf(".part", StringComparison.Ordinal);
        if (partIdx > 0 && lower.EndsWith(".rar"))
            return name[..partIdx];

        // foo.7z.NNN / foo.zip / foo.zNN
        string ext = Path.GetExtension(name);
        if (ext.Length == 4 && ext[1..].All(char.IsDigit))
        {
            string stem = name[..^4];
            // strip a known format extension too: foo.7z.001 -> foo
            string stemExt = Path.GetExtension(stem);
            if (stemExt is ".7z" or ".zip" or ".rar" or ".tar")
                return stem[..^stemExt.Length];
            return stem;
        }

        // foo.z01 (the .zNN split volume of a zip set) — base is whatever precedes it
        if (ext.Length == 4 && ext[1] == 'z' && char.IsDigit(ext[2]) && char.IsDigit(ext[3]))
            return name[..^4];

        return Path.GetFileNameWithoutExtension(name);
    }

    private static void DeleteSourceVolumes(string firstVolumePath)
    {
        foreach (var v in VolumeSet.Enumerate(firstVolumePath))
        {
            try { if (File.Exists(v)) File.Delete(v); } catch { /* best-effort */ }
        }
    }

    // ------------------------------------------------------------------
    // Create support
    // ------------------------------------------------------------------

    private static void ApplyCreateProperties(ISetProperties setProps, ArchiveFormat format, int level, bool solid, string? password, bool encryptHeaders)
    {
        var names = new List<string>();
        var values = new List<PropVariant>();

        void Add(string name, PropVariant value)
        {
            names.Add(name);
            values.Add(value);
        }

        Add("x", PropVariantOfLevel(level));

        if (format == ArchiveFormat.SevenZip)
        {
            Add("s", PropVariantOfString(solid ? "on" : "off"));
            if (!string.IsNullOrEmpty(password) && encryptHeaders)
                Add("he", PropVariantOfString("on"));
        }
        else if (format == ArchiveFormat.Zip)
        {
            if (!string.IsNullOrEmpty(password))
                Add("em", PropVariantOfString("AES256")); // spec: AES-256, never ZipCrypto
        }

        if (names.Count == 0)
            return;

        IntPtr namesPtr = MarshalStrings(names);
        IntPtr valuesPtr = MarshalPropVariants(values);
        try
        {
            int hr = setProps.SetProperties(namesPtr, valuesPtr, (uint)names.Count);
            if (hr != HResult.S_OK)
                Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            FreePropVariants(values);
            FreeStrings(namesPtr, names.Count);
        }
    }

    private static PropVariant PropVariantOfLevel(int level)
    {
        var v = new PropVariant();
        v.SetUI4((uint)Math.Clamp(level, 0, 9));
        return v;
    }

    private static PropVariant PropVariantOfString(string s)
    {
        var v = new PropVariant();
        v.SetBstr(s);
        return v;
    }

    private static IntPtr MarshalStrings(List<string> strings)
    {
        var ptrs = new IntPtr[strings.Count];
        for (int i = 0; i < strings.Count; i++)
            ptrs[i] = Marshal.StringToCoTaskMemUni(strings[i]);
        IntPtr array = Marshal.AllocCoTaskMem(IntPtr.Size * strings.Count);
        Marshal.Copy(ptrs, 0, array, strings.Count);
        return array;
    }

    private static void FreeStrings(IntPtr array, int count)
    {
        var ptrs = new IntPtr[count];
        Marshal.Copy(array, ptrs, 0, count);
        foreach (var p in ptrs)
            Marshal.FreeCoTaskMem(p);
        Marshal.FreeCoTaskMem(array);
    }

    private static IntPtr MarshalPropVariants(List<PropVariant> values)
    {
        int size = Marshal.SizeOf<PropVariant>();
        IntPtr array = Marshal.AllocCoTaskMem(size * values.Count);
        for (int i = 0; i < values.Count; i++)
            Marshal.StructureToPtr(values[i], array + i * size, false);
        return array;
    }

    private static void FreePropVariants(List<PropVariant> values)
    {
        foreach (var v in values)
            v.Clear();
    }

    private static List<UpdateItem> EnumerateSourceItems(IReadOnlyList<string> sourcePaths)
    {
        var items = new List<UpdateItem>();
        foreach (var src in sourcePaths)
        {
            string full = Path.GetFullPath(src);
            if (Directory.Exists(full))
                EnumerateDirectory(full, Path.GetFileName(full), items);
            else if (File.Exists(full))
                AddFile(full, Path.GetFileName(full), items);
        }
        return items;
    }

    private static void EnumerateDirectory(string dir, string archivePrefix, List<UpdateItem> items)
    {
        items.Add(MakeDirItem(dir, archivePrefix));

        foreach (var sub in Directory.EnumerateDirectories(dir))
            EnumerateDirectory(sub, archivePrefix + "/" + Path.GetFileName(sub), items);

        foreach (var file in Directory.EnumerateFiles(dir))
            AddFile(file, archivePrefix + "/" + Path.GetFileName(file), items);
    }

    private static void AddFile(string file, string archivePath, List<UpdateItem> items)
    {
        var fi = new FileInfo(file);
        items.Add(new UpdateItem
        {
            SourcePath = file,
            ArchivePath = archivePath,
            IsDirectory = false,
            Size = (ulong)fi.Length,
            MTimeUtc = fi.LastWriteTimeUtc,
            CTimeUtc = fi.CreationTimeUtc,
            ATimeUtc = fi.LastAccessTimeUtc,
        });
    }

    private static UpdateItem MakeDirItem(string dir, string archivePath)
    {
        var di = new DirectoryInfo(dir);
        return new UpdateItem
        {
            SourcePath = dir,
            ArchivePath = archivePath,
            IsDirectory = true,
            Size = 0,
            MTimeUtc = di.LastWriteTimeUtc,
            CTimeUtc = di.CreationTimeUtc,
            ATimeUtc = di.LastAccessTimeUtc,
        };
    }
}
