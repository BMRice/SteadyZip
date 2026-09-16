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
    public string PackedText { get; init; } = "";
    public string TimeText { get; init; } = "";
    public Thickness Indent { get; init; }
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

public partial class MainWindow : AppWindow
{
    private readonly EngineRouter _router = new();
    private readonly PasswordStore _passwords = new();
    private readonly ObservableCollection<EntryVm> _entries = new();
    private CancellationTokenSource? _cancel;
    private string? _currentArchive;
    private string? _foundPassword;
    private ArchiveContents? _currentContents;
    private bool _syncingSelection;

    // Decompression-bomb guard: warn before extracting past this total unpacked size.
    private const ulong BombWarningBytes = 100UL * 1024 * 1024 * 1024; // 100 GiB

    /// <summary>List-header "select all" box. Two-way bound so the header stays in sync with
    /// per-row checkboxes without any header element lookup.</summary>
    public static readonly DependencyProperty SelectAllProperty = DependencyProperty.Register(
        nameof(SelectAll), typeof(bool), typeof(MainWindow),
        new PropertyMetadata(true, (d, e) => ((MainWindow)d).ApplySelectAll((bool)e.NewValue)));

    public bool SelectAll
    {
        get => (bool)GetValue(SelectAllProperty);
        set => SetValue(SelectAllProperty, value);
    }

    public MainWindow()
    {
        InitializeComponent();
        EntryList.ItemsSource = _entries;
        DestBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        EngineText.Text = $"7z.dll: {(_router.IsSevenZipAvailable ? "可用" : "缺失")} | rar: {(_router.IsRarAvailable ? "可用" : "缺失")}";
        UpdateCounts();
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
                ShowInfoBar(path, contents);
                return;
            }
            catch (Exception)
            {
                // try next password
            }
        }

        MessageDialog.Show(this, MessageKind.Warning, "打开失败",
            "无法打开该压缩包（可能已损坏、格式不支持或需要密码）。");
    }

    private void PopulateEntries(ArchiveContents contents)
    {
        _currentContents = contents;
        _entries.Clear();
        foreach (var e in contents.Entries)
        {
            int depth = e.Path.Count(c => c == '/');
            var vm = new EntryVm
            {
                Entry = e,
                DisplayName = e.IsDirectory ? e.Path.TrimEnd('/') : e.Path,
                SizeText = e.IsDirectory ? "" : FormatSize(e.Size),
                PackedText = e.IsDirectory ? "" : FormatSize(e.PackedSize),
                TimeText = e.ModifiedTime?.ToString("yyyy-MM-dd HH:mm") ?? "",
                Indent = new Thickness(depth * 16, 0, 0, 0),
            };
            vm.PropertyChanged += OnEntryChanged;
            _entries.Add(vm);
        }

        SelectAll = true; // Reset the header box; the DP callback marks every entry selected.
        TestButton.IsEnabled = true;
        ExtractCommandButton.IsEnabled = true;
        ExtractButton.IsEnabled = true;
        EmptyState.Visibility = Visibility.Collapsed;
        UpdateCounts();
    }

    private void ShowInfoBar(string path, ArchiveContents contents)
    {
        InfoName.Text = System.IO.Path.GetFileName(path);
        InfoMeta.Text = $"{FormatName(contents.Format)} · {FormatSize(contents.TotalUnpackedSize)}";

        VolumeText.Text = $"{contents.Volumes.Count} 卷";
        VolumeBadge.Visibility = contents.IsMultiVolume ? Visibility.Visible : Visibility.Collapsed;
        EncryptBadge.Visibility = contents.IsEncrypted ? Visibility.Visible : Visibility.Collapsed;
        InfoBar.Visibility = Visibility.Visible;
    }

    private static string FormatName(ArchiveFormat format) => format switch
    {
        ArchiveFormat.SevenZip => "7z",
        ArchiveFormat.Zip => "zip",
        ArchiveFormat.Rar => "rar",
        ArchiveFormat.Tar => "tar",
        ArchiveFormat.TarGz => "tar.gz",
        ArchiveFormat.TarBz2 => "tar.bz2",
        _ => "未知格式",
    };

    private static string FormatSize(ulong bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    // ------------------------------------------------------------------ selection

    private void ApplySelectAll(bool value)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        foreach (var x in _entries) x.IsSelected = value;
        _syncingSelection = false;
        UpdateCounts();
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EntryVm.IsSelected) || _syncingSelection) return;
        _syncingSelection = true;
        SelectAll = _entries.Count > 0 && _entries.All(x => x.IsSelected);
        _syncingSelection = false;
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        if (_entries.Count == 0)
        {
            CountsText.Text = "未打开压缩包";
            return;
        }
        CountsText.Text = $"共 {_entries.Count} 项 · 已选 {_entries.Count(x => x.IsSelected)} 项";
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => SelectAll = true;

    private void OnSelectNone(object sender, RoutedEventArgs e) => SelectAll = false;

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MoreMenu.IsOpen = true;
    }

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
            MessageDialog.Show(this, MessageKind.Info, "解压", "未勾选任何文件。");
            return;
        }

        // Decompression-bomb guard: confirm very large total unpacked size.
        if (_currentContents is { } c && c.TotalUnpackedSize > BombWarningBytes)
        {
            bool go = MessageDialog.Confirm(this, "解压炸弹警告",
                $"要解压的内容总大小约 {FormatSize(c.TotalUnpackedSize)}，超过 {FormatSize(BombWarningBytes)}。继续？");
            if (!go)
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
        ProgressArea.Visibility = Visibility.Visible;
        ProgressText.Text = $"{verb}中...";

        var progress = new Progress<ProgressUpdate>(u =>
        {
            Progress.Value = u.Fraction;
            if (u.CurrentItem is not null)
                ProgressText.Text = $"{verb}中：{u.CurrentItem}";
        });

        Task.Run(() => work(progress))
            .ContinueWith(t =>
            {
                _cancel.Dispose();
                _cancel = null;
                CancelButton.IsEnabled = false;
                var report = t.Result;
                Progress.Value = 1;
                ProgressText.Text = report.AllOk
                    ? $"{verb}完成：{report.OkCount} 个文件。"
                    : $"{verb}失败：{report.ErrorCount} 个文件。{string.Join("；", report.Errors.Take(3))}";

                if (report.ErrorCount > 0 && verb == "解压")
                    MessageDialog.Show(this, MessageKind.Error, "解压有错误",
                        $"{report.ErrorCount} 个文件未能解压。",
                        string.Join(Environment.NewLine, report.Errors.Take(50)));
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

    private void OnBrowseDest(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() == true)
            DestBox.Text = dlg.FolderName;
    }

    /// <summary>GridView columns are fixed-width, so let the name column absorb the slack.</summary>
    private void OnEntryListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double otherColumns = 36 + 100 + 100 + 70 + 150;
        double available = e.NewSize.Width - otherColumns - SystemParameters.VerticalScrollBarWidth - 2;
        if (available > 120 && Math.Abs(NameColumn.Width - available) > 0.5)
            NameColumn.Width = available;
    }
}
