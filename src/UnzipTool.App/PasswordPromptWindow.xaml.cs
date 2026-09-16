using System.Windows;

namespace UnzipTool.App;

/// <summary>Asks for the password of one archive when none of the stored ones opened it.
/// Returns the typed password, or null if the user gave up.</summary>
public partial class PasswordPromptWindow : AppWindow
{
    private PasswordPromptWindow() => InitializeComponent();

    /// <param name="error">Why the previous attempt failed, when there was one.</param>
    public static string? Ask(Window? owner, string archiveName, string? error = null)
    {
        var w = new PasswordPromptWindow { Owner = owner };
        w.HeadingText.Text = archiveName;
        if (!string.IsNullOrEmpty(error))
        {
            w.ErrorText.Text = error;
            w.ErrorText.Visibility = Visibility.Visible;
        }
        w.Loaded += (_, _) => w.Input.Focus();
        return w.ShowDialog() == true ? w.Input.Password : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Input.Password.Length == 0)
            return; // every archive that reaches this dialog is encrypted; "" is never the answer
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
