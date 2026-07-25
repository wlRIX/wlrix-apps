using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Desks.ViewModels;

namespace Wlrix.Desks.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.FeedUnavailable += OnFeedUnavailable;
        vm.CommandFailed += OnCommandFailed;
        // The previews are scaled against the screen layout, which the compositor owns and
        // Avalonia already knows; keep it in step with the desks feed.
        UpdateWorld();
        if (Screens is { } screens)
            screens.Changed += OnScreensChanged;
        vm.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (Screens is { } screens)
            screens.Changed -= OnScreensChanged;
        if (DataContext is MainWindowViewModel vm)
        {
            vm.FeedUnavailable -= OnFeedUnavailable;
            vm.CommandFailed -= OnCommandFailed;
            vm.Dispose();
        }
    }

    private void OnScreensChanged(object? sender, EventArgs e) => UpdateWorld();

    // The bounding box of every screen, in Avalonia's coordinate space (which matches the
    // compositor-logical geometry the wlrix-desks feed reports).
    private void UpdateWorld()
    {
        if (DataContext is not MainWindowViewModel vm || Screens?.All is not { Count: > 0 } all)
            return;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var screen in all)
        {
            var bounds = screen.Bounds;
            minX = Math.Min(minX, bounds.X);
            minY = Math.Min(minY, bounds.Y);
            maxX = Math.Max(maxX, bounds.X + bounds.Width);
            maxY = Math.Max(maxY, bounds.Y + bounds.Height);
        }

        vm.World = new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    private void OnFeedUnavailable() => _ = MessageDialog.ShowAsync(this, DialogType.Error,
        "Could not reach the wlRIX compositor.\nThe compositor may not be running.",
        buttons: DialogButtons.Ok);

    private void OnCommandFailed(string message) =>
        _ = MessageDialog.ShowAsync(this, DialogType.Error, message, buttons: DialogButtons.Ok);

    private void OnToggleGlobalDesk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ShowGlobalDesk = !vm.ShowGlobalDesk;
    }

    private void OnToggleSnapshots(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ShowSnapshots = !vm.ShowSnapshots;
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private async void OnNewDesk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.NewDeskAsync();
    }

    private async void OnGotoSelected(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.GotoSelectedAsync();
    }

    private async void OnDeleteSelected(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.DeleteSelectedAsync();
    }

    // Clicking empty space in the desk strip (not on a tile, not the scrollbar) clears the selection.
    private void OnDeskListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || e.Source is not Visual source)
            return;

        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null &&
            source.FindAncestorOfType<ScrollBar>(includeSelf: true) is null)
            vm.SelectedDesk = null;
    }

    // Double-clicking a desk tile selects it and switches to it.
    private async void OnDeskDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm &&
            DeskItemFrom(e.Source)?.DataContext is DeskViewModel desk)
        {
            vm.SelectedDesk = desk;
            await vm.GotoSelectedAsync();
        }
    }

    // The desk tile (ListBoxItem) an event originated from, or null when it was empty space.
    private static ListBoxItem? DeskItemFrom(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
