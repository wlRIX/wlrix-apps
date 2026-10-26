using Avalonia.Controls;

namespace Wlrix.Files.Views;

/// <summary>The file operations window: one panel for every copy, move or delete under way.</summary>
/// <remarks>
/// Belongs to the application rather than to a browser window, because operations do: a copy
/// started in one window carries on when that window closes, and every window's status bar
/// opens this same one. <see cref="Services.OperationsMonitor"/> shows and hides it.
/// </remarks>
public partial class ProgressWindow : Window
{
    public ProgressWindow() => InitializeComponent();
}
