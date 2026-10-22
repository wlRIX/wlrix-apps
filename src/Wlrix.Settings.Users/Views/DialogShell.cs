using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Wlrix.Settings.Users.Views;

/// <summary>
/// What the panel's small dialogs share: fixed size, centered on the panel, and closed with a
/// result or with nothing. Kept as a base class rather than repeated in five code-behinds.
/// </summary>
public abstract class DialogShell<T> : Window
{
    protected DialogShell()
    {
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    /// <summary>Close without an answer.</summary>
    protected void OnCancel(object? sender, RoutedEventArgs e) => Close(default(T));
}
