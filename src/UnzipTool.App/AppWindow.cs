using System.Windows;
using System.Windows.Input;

namespace UnzipTool.App;

/// <summary>Base window for the hand-drawn chrome in Theme/WindowChrome.xaml. Supplies the
/// CommandBindings the title-bar buttons bind to, so no window needs its own boilerplate.</summary>
public class AppWindow : Window
{
    public AppWindow()
    {
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand,
            (_, _) => WindowState = WindowState.Minimized));

        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand,
            (_, _) => WindowState = WindowState.Maximized,
            (_, e) => e.CanExecute = ResizeMode == ResizeMode.CanResize));

        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand,
            (_, _) => WindowState = WindowState.Normal,
            (_, e) => e.CanExecute = ResizeMode == ResizeMode.CanResize));

        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => Close()));
    }
}
