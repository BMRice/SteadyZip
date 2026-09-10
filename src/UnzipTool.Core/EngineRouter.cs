namespace UnzipTool.Core;

/// <summary>
/// Routes each format×operation to the right engine per ADR-0001: everything except
/// "create RAR" goes through 7z.dll; "create RAR" uses rar.exe when present.
/// </summary>
public sealed class EngineRouter
{
    private readonly SevenZipEngine _sevenZip = new();
    private readonly RarEngine _rar = new();

    public bool IsSevenZipAvailable => SevenZipLibrary.IsAvailable();
    public bool IsRarAvailable => _rar.IsAvailable();

    public ArchiveContents Open(string path, string? password = null, CancellationToken cancel = default)
        => _sevenZip.Open(path, password, cancel);

    public ExtractReport Test(string path, IReadOnlyList<string>? passwords = null, IProgress<ProgressUpdate>? progress = null, CancellationToken cancel = default)
        => _sevenZip.Test(path, passwords, progress, cancel);

    public ExtractReport Extract(string path, ExtractOptions options, IProgress<ProgressUpdate>? progress = null, CancellationToken cancel = default)
        => _sevenZip.Extract(path, options, progress, cancel);

    public void Create(CreateOptions options, IProgress<ProgressUpdate>? progress = null, CancellationToken cancel = default)
    {
        if (options.Format == ArchiveFormat.Rar)
            _rar.Create(options, progress, cancel);
        else
            _sevenZip.Create(options, progress, cancel);
    }
}
