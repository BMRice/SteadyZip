using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using UnzipTool.Core;

namespace UnzipTool.App;

/// <summary>One saved password. Masked by default; <see cref="IsRevealed"/> is driven by the
/// row's eye toggle (docs/ui-design.md §6.3).</summary>
public sealed class PasswordRow : INotifyPropertyChanged
{
    public required string Text { get; set; }

    private bool _isRevealed;
    public bool IsRevealed
    {
        get => _isRevealed;
        set
        {
            if (_isRevealed == value) return;
            _isRevealed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Display));
        }
    }

    public string Display => _isRevealed ? Text : new string('•', Math.Max(6, Text.Length));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class PasswordWindow : AppWindow
{
    private readonly PasswordStore _store;
    private readonly ObservableCollection<PasswordRow> _passwords = new();

    public PasswordWindow(PasswordStore store)
    {
        InitializeComponent();
        _store = store;
        foreach (var p in store.Load())
            _passwords.Add(new PasswordRow { Text = p });
        List.ItemsSource = _passwords;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        string p = EditBox.Text;
        if (string.IsNullOrEmpty(p)) return;
        if (!_passwords.Any(x => x.Text == p))
            _passwords.Add(new PasswordRow { Text = p });
        EditBox.Clear();
    }

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is PasswordRow row && !string.IsNullOrEmpty(EditBox.Text))
        {
            row.Text = EditBox.Text;
            row.IsRevealed = false;
            EditBox.Clear();
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is PasswordRow row)
            _passwords.Remove(row);
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is PasswordRow row)
            EditBox.Text = row.Text;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Save on close either way — the title-bar X must not silently discard edits.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        try
        {
            _store.Save(_passwords.Select(x => x.Text).ToList());
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, MessageKind.Error, "保存密码库失败", ex.Message);
        }
    }
}
