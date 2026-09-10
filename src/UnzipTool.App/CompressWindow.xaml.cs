using System.IO;
using System.Windows;
using UnzipTool.Core;

namespace UnzipTool.App;

public partial class CompressWindow : Window
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
            RarItem.Content = "RAR (未找到 rar.exe)";
        }
        DestBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "archive.7z");
        VolumeBox.SelectionChanged += (_, _) => VolumeCustom.IsEnabled = VolumeBox.SelectedIndex == 3;
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择文件" };
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FileNames)
                SourceList.Items.Add(f);
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择文件夹" };
        if (dlg.ShowDialog() == true)
            SourceList.Items.Add(dlg.FolderName);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var selected = SourceList.SelectedItems.Cast<object>().ToList();
        foreach (var s in selected) SourceList.Items.Remove(s);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var fmt = FormatBox.SelectedIndex switch
        {
            0 => ArchiveFormat.Zip,
            1 => ArchiveFormat.SevenZip,
            _ => ArchiveFormat.Rar,
        };
        string ext = fmt == ArchiveFormat.Rar ? "rar" : fmt == ArchiveFormat.Zip ? "zip" : "7z";
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = $"{fmt} 压缩包|*.{ext}", FileName = $"archive.{ext}" };
        if (dlg.ShowDialog() == true)
            DestBox.Text = dlg.FileName;
    }

    private void OnCompress(object sender, RoutedEventArgs e)
    {
        if (SourceList.Items.Count == 0)
        {
            MessageBox.Show(this, "请先添加要压缩的内容。", "压缩", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(DestBox.Text))
        {
            MessageBox.Show(this, "请指定保存路径。", "压缩", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var fmt = FormatBox.SelectedIndex switch
        {
            0 => ArchiveFormat.Zip,
            1 => ArchiveFormat.SevenZip,
            _ => ArchiveFormat.Rar,
        };
        if (fmt == ArchiveFormat.Rar && !_router.IsRarAvailable)
        {
            MessageBox.Show(this, "未找到 rar.exe，无法创建 RAR。", "压缩", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int level = LevelBox.SelectedIndex switch { 0 => 0, 1 => 1, 2 => 5, _ => 9 };
        var options = new CreateOptions
        {
            Format = fmt,
            SourcePaths = SourceList.Items.Cast<string>().ToList(),
            DestinationArchive = DestBox.Text,
            Level = level,
            Solid = fmt == ArchiveFormat.SevenZip && SolidBox.IsChecked == true,
            Password = string.IsNullOrEmpty(PasswordBox.Text) ? null : PasswordBox.Text,
            VolumeSizeBytes = ParseVolumeSize(),
        };

        _cancel = new CancellationTokenSource();
        StatusText.Text = "压缩中...";
        var progress = new Progress<ProgressUpdate>(u => Progress.Value = u.Fraction);

        Task.Run(() => _router.Create(options, progress, _cancel.Token))
            .ContinueWith(t =>
            {
                _cancel.Dispose();
                _cancel = null;
                if (t.IsFaulted)
                {
                    StatusText.Text = "失败";
                    MessageBox.Show(this, t.Exception!.GetBaseException().Message, "压缩失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    StatusText.Text = "完成";
                    DialogResult = true;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cancel?.Cancel();

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
