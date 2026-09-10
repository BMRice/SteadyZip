using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using UnzipTool.Core;

namespace UnzipTool.App;

public sealed class EntryVm : INotifyPropertyChanged
{
    public required ArchiveEntry Entry { get; init; }
    public string DisplayName { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string Kind => Entry.IsDirectory ? "目录" : "文件";

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class MainWindow : Window
{
    private readonly EngineRouter _router = new();
    private readonly PasswordStore _passwords = new();
    private readonly ObservableCollection<EntryVm> _entries = new();
    private CancellationTokenSource? _cancel;
    private string? _currentArchive;
    private string? _foundPassword;
    private ArchiveContents? _currentContents;

    // Decompression-bomb guard: warn before extracting past this total unpacked size.
    private const ulong BombWarningBytes = 100UL * 1024 * 1024 * 1024; // 100 GiB

    public MainWindow()
    {
        InitializeComponent();
        EntryList.ItemsSource = _entries;
        DestBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        EngineText.Text = $"7z.dll: {(_router.IsSevenZipAvailable ? "可用" : "缺失")} | rar.exe: {(_router.IsRarAvailable ? "可用" : "缺失(仅影响创建RAR)")}";
    }

    // ------------------------------------------------------------------ open / browse

    private void OnOpenClick(object sender, RoutedEventArgs e) => PickAndOpen();

    private void PickAndOpen()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开压缩包",
            Filter = "压缩包|*.zip;*.7z;*.rar;*.001;*.z01;*.tar;*.tar.gz;*.tgz;*.tar.bz2|所有文件|*.*",
        };
        if (dlg.ShowDialog() == true)
            OpenArchive(dlg.FileName);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            OpenArchive(files[0]);
    }

    private void OpenArchive(string path)
    {
        var passwords = _passwords.Load();
        // Try no password first, then each stored password (header-encrypted archives need one).
        var candidates = new List<string?> { null };
        candidates.AddRange(passwords);

        foreach (var pw in candidates)
        {
            try
            {
                var contents = _router.Open(path, pw);
                PopulateEntries(contents);
                _currentArchive = path;
                _foundPassword = pw;
                string vol = contents.IsMultiVolume ? $"，{contents.Volumes.Count} 卷" : "";
                StatusText.Text = $"{path} — {contents.Entries.Count} 项{vol}" + (contents.IsEncrypted ? "（加密）" : "");
                return;
            }
            catch (Exception)
            {
                // try next password
            }
        }

        MessageBox.Show(this, "无法打开该压缩包（可能已损坏、格式不支持或需要密码）。", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void PopulateEntries(ArchiveContents contents)
    {
        _currentContents = contents;
        _entries.Clear();
        foreach (var e in contents.Entries)
        {
            int depth = e.Path.Count(c => c == '/');
            _entries.Add(new EntryVm
            {
                Entry = e,
                DisplayName = new string(' ', depth * 4) + (e.IsDirectory ? e.Path.TrimEnd('/') : e.Path),
                SizeText = e.IsDirectory ? "" : FormatSize(e.Size),
            });
        }
    }

    private static string FormatSize(ulong bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    // ------------------------------------------------------------------ test / extract

    private void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_currentArchive is null) return;
        RunAsync(report => _router.Test(_currentArchive, Passwords(), report, _cancel!.Token),
                 "完整性测试");
    }

    private void OnExtractClick(object sender, RoutedEventArgs e)
    {
        if (_currentArchive is null) return;

        var selected = _entries.Where(x => x.IsSelected).Select(x => x.Entry.Index).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "未勾选任何文件。", "解压", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Decompression-bomb guard: confirm very large total unpacked size.
        if (_currentContents is { } c && c.TotalUnpackedSize > BombWarningBytes)
        {
            var r = MessageBox.Show(this,
                $"要解压的内容总大小约 {FormatSize(c.TotalUnpackedSize)}，超过 {FormatSize(BombWarningBytes)}。继续？",
                "解压炸弹警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes)
                return;
        }

        _applyAllAction = null;
        var policy = (OverwritePolicy)OverwriteBox.SelectedIndex;
        var options = new ExtractOptions
        {
            DestinationDirectory = DestBox.Text,
            ExtractToSubfolder = SubfolderBox.SelectedIndex == 0,
            Overwrite = policy,
            OverwritePrompt = policy == OverwritePolicy.Ask ? PromptOverwrite : null,
            DeleteSourceAfter = DeleteAfterBox.IsChecked == true,
            DeleteVolumesAfterRead = DeleteVolumesBox.IsChecked == true,
            SelectedIndices = selected,
            Passwords = Passwords(),
        };

        RunAsync(report => _router.Extract(_currentArchive, options, report, _cancel!.Token),
                 "解压");
    }

    private IReadOnlyList<string> Passwords()
    {
        var list = _passwords.Load().ToList();
        if (_foundPassword is not null && !list.Contains(_foundPassword))
            list.Insert(0, _foundPassword);
        return list;
    }

    private OverwriteAction? _applyAllAction;

    private OverwriteAction PromptOverwrite(string path)
    {
        if (_applyAllAction is { } cached)
            return cached;

        OverwriteAction result = OverwriteAction.Skip;
        Dispatcher.Invoke(() =>
        {
            var w = new OverwritePromptWindow(path) { Owner = this };
            if (w.ShowDialog() == true)
            {
                result = w.Result;
                if (w.ApplyToAll)
                    _applyAllAction = w.Result;
            }
        });
        return result;
    }

    private void RunAsync(Func<IProgress<ProgressUpdate>, ExtractReport> work, string verb)
    {
        if (_cancel is not null) return; // already running

        _cancel = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        Progress.Value = 0;

        var progress = new Progress<ProgressUpdate>(u =>
        {
            Progress.Value = u.Fraction;
            if (u.CurrentItem is not null)
                StatusText.Text = $"{verb}: {u.CurrentItem}";
        });

        Task.Run(() => work(progress))
            .ContinueWith(t =>
            {
                _cancel.Dispose();
                _cancel = null;
                CancelButton.IsEnabled = false;
                var report = t.Result;
                Progress.Value = 1;
                StatusText.Text = report.AllOk
                    ? $"{verb}完成：{report.OkCount} 个文件。"
                    : $"{verb}失败：{string.Join("；", report.Errors.Take(5))}";
                if (report.ErrorCount > 0 && verb == "解压")
                    MessageBox.Show(this, string.Join("\n", report.Errors.Take(10)), "解压有错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _cancel?.Cancel();

    // ------------------------------------------------------------------ compress / passwords

    private void OnCompressClick(object sender, RoutedEventArgs e)
    {
        var w = new CompressWindow(_router) { Owner = this };
        w.ShowDialog();
    }

    private void OnPasswordsClick(object sender, RoutedEventArgs e)
    {
        var w = new PasswordWindow(_passwords) { Owner = this };
        w.ShowDialog();
    }

    // ------------------------------------------------------------------ helpers

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var x in _entries) x.IsSelected = true;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var x in _entries) x.IsSelected = false;
    }

    private void OnBrowseDest(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() == true)
            DestBox.Text = dlg.FolderName;
    }
}
