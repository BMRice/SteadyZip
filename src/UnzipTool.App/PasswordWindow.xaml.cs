using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using UnzipTool.Core;

namespace UnzipTool.App;

public partial class PasswordWindow : Window
{
    private readonly PasswordStore _store;
    private readonly ObservableCollection<string> _passwords = new();

    public PasswordWindow(PasswordStore store)
    {
        InitializeComponent();
        _store = store;
        foreach (var p in store.Load())
            _passwords.Add(p);
        List.ItemsSource = _passwords;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        string p = EditBox.Text;
        if (string.IsNullOrEmpty(p)) return;
        if (!_passwords.Contains(p))
            _passwords.Add(p);
        EditBox.Clear();
    }

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is string old && !string.IsNullOrEmpty(EditBox.Text))
        {
            int i = _passwords.IndexOf(old);
            _passwords[i] = EditBox.Text;
            EditBox.Clear();
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is string s)
            _passwords.Remove(s);
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is string s)
            EditBox.Text = s;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _store.Save(_passwords);
        Close();
    }
}
