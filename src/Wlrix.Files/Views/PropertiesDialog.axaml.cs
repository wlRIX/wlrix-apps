using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Wlrix.Files.ViewModels;

namespace Wlrix.Files.Views;

/// <summary>What something is, and who may do what to it.</summary>
/// <remarks>
/// Modeless, and deliberately: a properties window is something to leave open beside the
/// listing while working, and a modal one would also freeze the window behind it while a large
/// directory is still being added up. It owns its view model, so closing it cancels that walk.
/// </remarks>
public partial class PropertiesDialog : Window
{
    public PropertiesDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
    }

    /// <summary>Opens a properties window over <paramref name="owner"/>.</summary>
    public static void Show(Window owner, PropertiesViewModel model)
    {
        var dialog = new PropertiesDialog { DataContext = model, Title = model.Title };

        // Loading starts once the window is up, so a directory that takes a minute to add up
        // is a number climbing in a window that is already on screen. The icon is fetched
        // separately and not awaited with the rest: it is the one thing here the window can
        // manage without.
        dialog.Opened += async (_, _) =>
        {
            _ = model.LoadIconAsync();
            await model.LoadAsync().ConfigureAwait(true);
        };
        dialog.Closed += (_, _) => model.Dispose();

        dialog.Show(owner);
    }
}
