using System.Windows;
using System.Windows.Media;

namespace UnzipTool.App;

/// <summary>Icon/semantic kind of a message. Confirm adds a 是/否 pair of buttons.</summary>
public enum MessageKind
{
    Info,
    Warning,
    Error,
    Confirm,
}

/// <summary>Theme-consistent replacement for <c>MessageBox</c> (docs/ui-design.md §6.5):
/// owner-centred, our own chrome, long text folded into an optional details pane.</summary>
public partial class MessageDialog : AppWindow
{
    private MessageDialog() => InitializeComponent();

    public static void Show(Window? owner, MessageKind kind, string heading, string message, string? details = null)
    {
        var d = new MessageDialog();
        d.Init(kind, heading, message, details, confirm: false);
        d.Owner = owner;
        d.ShowDialog();
    }

    public static bool Confirm(Window? owner, string heading, string message, string? details = null)
    {
        var d = new MessageDialog();
        d.Init(MessageKind.Confirm, heading, message, details, confirm: true);
        d.Owner = owner;
        return d.ShowDialog() == true;
    }

    private void Init(MessageKind kind, string heading, string message, string? details, bool confirm)
    {
        Title = confirm ? "确认" : "提示";
        HeadingText.Text = heading;
        MessageText.Text = message;

        string icon = kind switch
        {
            MessageKind.Warning => "Icon.Warning",
            MessageKind.Error => "Icon.Error",
            _ => "Icon.Info",
        };
        string brush = kind switch
        {
            MessageKind.Warning => "Brush.Warning",
            MessageKind.Error => "Brush.Danger",
            _ => "Brush.Accent",
        };
        KindIcon.Data = (Geometry)FindResource(icon);
        KindIcon.Stroke = (Brush)FindResource(brush);

        bool hasDetails = !string.IsNullOrWhiteSpace(details);
        DetailsToggle.Visibility = hasDetails ? Visibility.Visible : Visibility.Collapsed;
        DetailsText.Text = details ?? "";

        CancelButton.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnToggleDetails(object sender, RoutedEventArgs e)
    {
        bool show = DetailsPanel.Visibility != Visibility.Visible;
        DetailsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DetailsChevron.Data = (Geometry)FindResource(show ? "Icon.ChevronUp" : "Icon.ChevronDown");
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
