using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Desks.Localization;
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
        Strings.CompositorUnreachable, buttons: DialogButtons.Ok);

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


    // Rename opens the editor on the selected tile; the template only realizes the field when
    // IsEditing flips, so focusing it waits for that layout pass.
    private void OnRenameSelected(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || vm.SelectedDesk is not { } desk)
            return;

        vm.BeginRenameSelected();
        Dispatcher.UIThread.Post(() => FocusNameEditor(desk), DispatcherPriority.Loaded);
    }

    private void FocusNameEditor(DeskViewModel desk)
    {
        var editor = this.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(t => t.Name == "NameEditor" && ReferenceEquals(t.DataContext, desk));
        if (editor is null)
            return;

        editor.Focus();
        editor.SelectAll();
    }

    // Enter commits, Escape abandons the draft. Clicking away commits too (LostFocus), which is
    // how IRIX's inline edits behaved.
    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm ||
            sender is not TextBox { DataContext: DeskViewModel desk })
            return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = vm.CommitRenameAsync(desk);
                DeskList.Focus();
                break;
            case Key.Escape:
                e.Handled = true;
                vm.CancelRename(desk);
                DeskList.Focus();
                break;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm &&
            sender is TextBox { DataContext: DeskViewModel desk })
            _ = vm.CommitRenameAsync(desk);
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
            DeskItemFrom(e.Source)?.DataContext is DeskViewModel { IsEditing: false } desk)
        {
            vm.SelectedDesk = desk;
            await vm.GotoSelectedAsync();
        }
    }

    // The desk tile (ListBoxItem) an event originated from, or null when it was empty space.
    private static ListBoxItem? DeskItemFrom(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);

    // No hand-written `InitializeComponent()` here on purpose. The generated one
    // (InitializeComponent(bool loadXaml = true)) loads the XAML *and* assigns the x:Name
    // fields; a parameterless override wins overload resolution, leaving DeskList null.
}
