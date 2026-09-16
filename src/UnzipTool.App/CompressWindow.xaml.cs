using System.IO;
using System.Windows;
using UnzipTool.Core;

namespace UnzipTool.App;

/// <summary>One item queued for compression (file or directory).</summary>
public sealed class SourceItem
{
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
}

public partial class CompressWindow : AppWindow
{
    private readonly EngineRouter _router;
    private CancellationTokenSource? _cancel;

    public CompressWindow(EngineRouter router)
    {
        InitializeComponent();
        _router = router;
        if (!router.IsRarAvailable)
        {
            RarItem.IsEnabled = false;
            RarHint.Visibility = Visibility.Visible;
        }
        DestBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "archive.7z");
        VolumeBox.SelectionChanged += (_, _) => VolumeCustom.IsEnabled = VolumeBox.SelectedIndex == 3;
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择文件" };
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FileNames)
                SourceList.Items.Add(new SourceItem { Path = f, IsDirectory = false });
        HideHint();
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择文件夹" };
        if (dlg.ShowDialog() == true)
            SourceList.Items.Add(new SourceItem { Path = dlg.FolderName, IsDirectory = true });
        HideHint();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        foreach (var s in SourceList.SelectedItems.Cast<object>().ToList())
            SourceList.Items.Remove(s);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var fmt = SelectedFormat();
        string ext = fmt switch { ArchiveFormat.Rar => "rar", ArchiveFormat.Zip => "zip", _ => "7z" };
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = $"{fmt} 压缩包|*.{ext}", FileName = $"archive.{ext}" };
        if (dlg.ShowDialog() == true)
            DestBox.Text = dlg.FileName;
    }

    private void OnCompress(object sender, RoutedEventArgs e)
    {
        if (SourceList.Items.Count == 0)
        {
            ShowHint("请先添加要压缩的内容。");
            return;
        }
        if (string.IsNullOrWhiteSpace(DestBox.Text))
        {
            ShowHint("请指定保存路径。");
            return;
        }

        var fmt = SelectedFormat();
        if (fmt == ArchiveFormat.Rar && !_router.IsRarAvailable)
        {
            ShowHint("未找到 rar.exe，无法创建 RAR。");
            return;
        }
        HideHint();

        int level = LevelBox.SelectedIndex switch { 0 => 0, 1 => 1, 2 => 5, _ => 9 };
        var options = new CreateOptions
        {
            Format = fmt,
            SourcePaths = SourceList.Items.OfType<SourceItem>().Select(x => x.Path).ToList(),
            DestinationArchive = DestBox.Text,
            Level = level,
            Solid = fmt == ArchiveFormat.SevenZip && SolidBox.IsChecked == true,
            Password = string.IsNullOrEmpty(PasswordBox.Text) ? null : PasswordBox.Text,
            VolumeSizeBytes = ParseVolumeSize(),
        };

        _cancel = new CancellationTokenSource();
        StatusText.Text = "压缩中...";
        CompressButton.IsEnabled = false;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        var progress = new Progress<ProgressUpdate>(u => Progress.Value = u.Fraction);

        Task.Run(() => _router.Create(options, progress, _cancel.Token))
            .ContinueWith(t =>
            {
                _cancel.Dispose();
                _cancel = null;
                CompressButton.IsEnabled = true;
                if (t.IsFaulted)
                {
                    StatusText.Text = "失败";
                    MessageDialog.Show(this, MessageKind.Error, "压缩失败",
                        t.Exception!.GetBaseException().Message);
                }
                else
                {
                    StatusText.Text = "完成";
                    DialogResult = true;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cancel?.Cancel();

    private ArchiveFormat SelectedFormat() => FormatBox.SelectedIndex switch
    {
        0 => ArchiveFormat.Zip,
        1 => ArchiveFormat.SevenZip,
        _ => ArchiveFormat.Rar,
    };

    private void ShowHint(string text)
    {
        HintText.Text = text;
        HintText.Visibility = Visibility.Visible;
    }

    private void HideHint() => HintText.Visibility = Visibility.Collapsed;

    private ulong? ParseVolumeSize()
    {
        string? text = VolumeBox.SelectedIndex switch
        {
            0 => null,
            1 => "700MB",
            2 => "4GB",
            _ => VolumeCustom.Text,
        };
        if (string.IsNullOrWhiteSpace(text)) return null;

        string s = text.Trim().ToUpperInvariant().Replace(" ", "");
        ulong multiplier = 1;
        if (s.EndsWith("GB")) { multiplier = 1024UL * 1024 * 1024; s = s[..^2]; }
        else if (s.EndsWith("MB")) { multiplier = 1024UL * 1024; s = s[..^2]; }
        else if (s.EndsWith("KB")) { multiplier = 1024UL; s = s[..^2]; }
        else if (s.EndsWith("B")) { s = s[..^1]; }

        return double.TryParse(s, out var n) && n > 0 ? (ulong)(n * multiplier) : null;
    }
}
