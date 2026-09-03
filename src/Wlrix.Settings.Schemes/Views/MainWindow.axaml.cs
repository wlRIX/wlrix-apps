using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Wlrix.Avalonia;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Settings.Schemes.ViewModels;

namespace Wlrix.Settings.Schemes.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// The suffix a scheme dictionary's color keys carry, and which the brush keys do not:
    /// <c>WlrixFaceColor</c> is the color, <c>WlrixFace</c> the brush over it.
    /// </summary>
    private const string ColorSuffix = "Color";

    public MainWindow()
    {
        InitializeComponent();
        ShowSample(WlrixSchemes.Default);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as ColorSchemeViewModel)?.Dispose();
    }

    private void OnSchemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: WlrixScheme scheme })
            ShowSample(scheme);
    }

    private void OnApply(object? sender, RoutedEventArgs e) =>
        _ = (DataContext as ColorSchemeViewModel)?.ApplyAsync();

    private void OnReset(object? sender, RoutedEventArgs e) =>
        _ = (DataContext as ColorSchemeViewModel)?.ResetAsync();

    /// <summary>
    /// Cancel and the Application menu's Close, which are the same thing: put back whatever
    /// Apply changed, then close.
    /// </summary>
    private async void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ColorSchemeViewModel model)
            await model.RevertAsync();
        Close();
    }

    private void OnHelp(object? sender, RoutedEventArgs e) =>
        _ = MessageDialog.ShowAsync(this, DialogType.Information, HelpText,
            title: "Color Schemes Help", buttons: DialogButtons.Ok);

    private const string HelpText =
        "Pick a scheme to see it in the sample image, then press Apply to give it to the whole "
        + "desktop \u2014 the window frames, the desktop icons, the tray and every application, "
        + "at once and without restarting anything.\n\n"
        + "Reset goes back to the scheme that was in force when this window opened. Cancel does "
        + "the same and then closes.";

    /// <summary>
    /// Draw the sample image in <paramref name="scheme"/>, whatever the rest of the window is in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves have to be overridden, and only overriding the colors is the mistake worth
    /// naming: the brushes live once in <c>Brushes.axaml</c> as shared instances, each bound to
    /// its color with <c>DynamicResource</c>, and that binding resolves where the *brush* lives
    /// rather than where it is used. Redefining <c>WlrixFaceColor</c> in this subtree would
    /// therefore repaint nothing at all. So a brush is built per color here, under the key the
    /// control themes actually bind to.
    /// </para>
    /// <para>
    /// A fresh dictionary each time rather than an edited one: the keys come from the scheme
    /// file, and a scheme that dropped a role would otherwise leave the previous scheme's color
    /// behind under it, which is the sort of thing nobody notices for a year.
    /// </para>
    /// </remarks>
    private void ShowSample(WlrixScheme scheme)
    {
        var loaded = new ResourceInclude((Uri?)null) { Source = new Uri(scheme.ResourceUri) }.Loaded;
        var sample = new ResourceDictionary();

        foreach (var (key, value) in loaded)
        {
            sample[key] = value;
            if (key is string name && name.EndsWith(ColorSuffix, StringComparison.Ordinal)
                && value is Color color)
            {
                sample[name[..^ColorSuffix.Length]] = new SolidColorBrush(color);
            }
        }

        SampleImage.Resources = sample;
    }
}
