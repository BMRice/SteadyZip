using System.IO;
using System.Windows;
using UnzipTool.Core;

namespace UnzipTool.App;

public partial class OverwritePromptWindow : AppWindow
{
    public OverwriteAction Result { get; private set; } = OverwriteAction.Skip;
    public bool ApplyToAll { get; private set; }

    public OverwritePromptWindow(string path)
    {
        InitializeComponent();
        DirText.Text = Path.GetDirectoryName(path) ?? "";
        FileText.Text = Path.GetFileName(path);
        Loaded += (_, _) => SkipButton.Focus(); // 跳过 is the safe default (docs/ui-design.md §6.4)
    }

    private void Finish(OverwriteAction action)
    {
        Result = action;
        ApplyToAll = ApplyAll.IsChecked == true;
        DialogResult = true;
    }

    private void OnOverwrite(object sender, RoutedEventArgs e) => Finish(OverwriteAction.Overwrite);
    private void OnRename(object sender, RoutedEventArgs e) => Finish(OverwriteAction.Rename);
    private void OnSkip(object sender, RoutedEventArgs e) => Finish(OverwriteAction.Skip);
}
