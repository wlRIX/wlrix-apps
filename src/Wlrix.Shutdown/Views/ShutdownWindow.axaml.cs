using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Icons;
using Wlrix.Shutdown.Localization;
using Wlrix.Shutdown.ViewModels;

namespace Wlrix.Shutdown.Views;

public partial class ShutdownWindow : Window
{
    /// <summary>The picture's size in the banner, in logical pixels: its well's, less the border.</summary>
    private const int PictureSize = 46;

    public ShutdownWindow()
    {
        InitializeComponent();
        // Drawn again whenever the output's scale changes, so the picture is never a 1x render
        // stretched over a 1.5x screen.
        ScalingChanged += (_, _) => DrawPicture();
        DrawPicture();
    }

    /// <summary>
    /// Draws IRIX's task picture at the window's own scale.
    /// </summary>
    /// <remarks>
    /// From the SVG every time rather than a PNG made once, so it is sharp at whatever scale the
    /// output has. The drawing is an IRIX export, which <see cref="LegacySvg"/> knows what to do
    /// with. A failure leaves the well empty, which is no reason not to shut down.
    /// </remarks>
    private void DrawPicture()
    {
        // Found by name: this window loads its XAML itself, which leaves the generated field unset.
        if (this.FindControl<Image>("Picture") is not { } picture)
            return;
        using var stream = typeof(ShutdownWindow).Assembly.GetManifestResourceStream("Shutdown.task.svg");
        if (stream is null)
            return;
        using var reader = new StreamReader(stream);
        var pixels = (int)Math.Ceiling(PictureSize * RenderScaling);
        if (LegacySvg.Render(reader.ReadToEnd(), pixels) is not { } png)
            return;
        using var memory = new MemoryStream(png);
        var previous = picture.Source as Bitmap;
        picture.Source = new Bitmap(memory);
        previous?.Dispose();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not ShutdownViewModel vm)
            return;

        // The title carries this machine's hostname, which a .resx value cannot interpolate at
        // XAML load, so it is set here rather than in the markup.
        Title = vm.Title;

        vm.ShowError += OnShowError;

        // Shown first, and told what it may offer when logind answers. Asking the system bus is
        // not something to block a window's first paint on.
        _ = vm.InitializeAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is ShutdownViewModel vm)
            vm.ShowError -= OnShowError;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// OK acts on the checkboxes. The window is deliberately not closed on the way: if the
    /// machine is going down, closing it changes nothing, and if it is not, the reason has to
    /// appear over a window that is still there.
    /// </summary>
    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShutdownViewModel vm)
            _ = vm.ConfirmAsync();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnShowError(string message) => _ = MessageDialog.ShowAsync(this, DialogType.Error,
        message, buttons: DialogButtons.Ok, title: Strings.ErrorTitle);
}
