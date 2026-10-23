// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wlrix.Avalonia.Dialogs;
using Wlrix.IconCatalog.Controls;
using Wlrix.IconCatalog.Localization;
using Wlrix.IconCatalog.ViewModels;

namespace Wlrix.IconCatalog.Views;

public partial class MainWindow : Window
{
    /// <summary>How far the pointer moves from empty space before it is a band rather than a click.</summary>
    private const double BandThreshold = 4;

    private MainWindowViewModel? _model;
    private Point? _bandOrigin;
    private bool _banding;

    public MainWindow()
    {
        InitializeComponent();

        // Every page's list is made from the notebook's content template, so the handlers go
        // on the notebook and find the list an event came from.
        Notebook.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        Notebook.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        Notebook.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        Notebook.AddHandler(PointerExitedEvent, (_, _) => _model?.SetHint(null));
        Notebook.AddHandler(DoubleTappedEvent, OnDoubleTapped);
        Notebook.AddHandler(KeyDownEvent, OnKeyDown);
        Notebook.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Notebook.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is { } old)
        {
            old.ExitRequested -= Close;
            old.AboutRequested -= ShowAbout;
            old.ErrorRequested -= ShowError;
        }

        _model = DataContext as MainWindowViewModel;
        if (_model is not { } model)
            return;

        model.ExitRequested += Close;
        model.AboutRequested += ShowAbout;
        model.ErrorRequested += ShowError;
        model.Prompt = (title, prompt, initial) =>
            PromptDialog.ShowAsync(this, title, prompt, initial, selectExtension: true,
                Strings.DialogOk, Strings.DialogCancel);
        model.Confirm = async (title, question) =>
            await MessageDialog.ShowAsync(this, DialogType.Question, question,
                buttons: DialogButtons.OkCancel, title: title,
                okText: Strings.DialogOk, cancelText: Strings.DialogCancel) == DialogResult.Ok;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _model?.SaveWindow(Bounds.Width, Bounds.Height);
        base.OnClosing(e);
    }

    private void ShowAbout()
    {
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _ = MessageDialog.ShowAsync(this, DialogType.Information, Strings.About(version),
            title: Strings.AboutTitle, buttons: DialogButtons.Ok, okText: Strings.DialogOk);
    }

    private void ShowError(string message) =>
        _ = MessageDialog.ShowAsync(this, DialogType.Error, message,
            title: Strings.ErrorTitle, buttons: DialogButtons.Ok, okText: Strings.DialogOk);

    // --- finding things ---------------------------------------------------

    private static ListBox? ListOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBox>(includeSelf: true) is { Name: "IconView" } list ? list : null;

    private static IconViewModel? IconUnder(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as IconViewModel;

    private static StaggeredIconPanel? PanelOf(ListBox list) => list.ItemsPanelRoot as StaggeredIconPanel;

    private static Rectangle? BandOf(ListBox list) =>
        (list.Parent as Panel)?.Children.OfType<Canvas>().FirstOrDefault()?.Children.OfType<Rectangle>().FirstOrDefault();

    // --- opening ----------------------------------------------------------

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IconUnder(e.Source) is { } icon)
        {
            _model?.Open(icon);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && ListOf(e.Source) is not null && e.KeyModifiers == KeyModifiers.None)
        {
            _model?.OpenSelected.Execute().Subscribe();
            e.Handled = true;
        }
    }

    // --- hints and the rubber band ----------------------------------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _bandOrigin = null;
        _banding = false;

        if (ListOf(e.Source) is not { } list || PanelOf(list) is not { } panel
            || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
            return;

        var point = e.GetPosition(panel);
        // Only empty space starts a band; pressing an icon is a click. The gaps between cells
        // count as empty, which is what IndexAt's -1 is for.
        if (panel.IndexAt(point) >= 0)
            return;

        // A plain click on nothing means "nothing selected". Control and Shift add to a
        // selection rather than replace it, so they keep it.
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) == 0)
            list.SelectedItems?.Clear();
        list.Focus();
        _bandOrigin = point;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var list = ListOf(e.Source);

        // The status line says what the pointer is over: an icon's comment (or its name, for an
        // icon without one), or how the tabs work.
        if (!_banding)
        {
            var hint = IconUnder(e.Source) is { } icon
                ? icon.Comment ?? icon.Name
                : (e.Source as Visual)?.FindAncestorOfType<TabItem>(includeSelf: true) is not null
                    ? Strings.TabsHint
                    : null;
            _model?.SetHint(hint);
        }

        if (_bandOrigin is not { } origin || list is null || PanelOf(list) is not { } panel)
            return;

        if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
        {
            EndBand(list);
            return;
        }

        var point = e.GetPosition(panel);
        if (!_banding)
        {
            if (Math.Abs(point.X - origin.X) < BandThreshold && Math.Abs(point.Y - origin.Y) < BandThreshold)
                return;
            _banding = true;
            _model?.SetHint(null);
        }

        var rect = new Rect(origin, point).Normalize();
        if (BandOf(list) is { } band)
        {
            // The overlay does not scroll with the cells, so the scroll offset comes off.
            var offset = panel.FindAncestorOfType<ScrollViewer>()?.Offset ?? default;
            Canvas.SetLeft(band, rect.X - offset.X);
            Canvas.SetTop(band, rect.Y - offset.Y);
            band.Width = rect.Width;
            band.Height = rect.Height;
            band.IsVisible = true;
        }

        // Rebuilt wholesale on every motion: a page is a few dozen icons.
        if (list.SelectedItems is { } selected && list.ItemsSource is System.Collections.IList items)
        {
            selected.Clear();
            foreach (var index in panel.IndicesIn(rect))
            {
                if (index < items.Count)
                    selected.Add(items[index]);
            }
        }

        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_banding)
            e.Handled = true;
        if (ListOf(e.Source) is { } list)
            EndBand(list);
        _bandOrigin = null;
        _banding = false;
    }

    private void EndBand(ListBox list)
    {
        _bandOrigin = null;
        _banding = false;
        if (BandOf(list) is { } band)
            band.IsVisible = false;
    }

    // --- drops ------------------------------------------------------------

    private static IReadOnlyList<string> PathsOf(DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { } files)
            return [];
        return [.. files.Select(file => file.TryGetLocalPath()).OfType<string>()];
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // The page only keeps a reference to the file, which stays where it is -- a link, in
        // spirit. But Wayland drags know only copy, move and ask, and the backend turns Link into
        // no action at all, which the compositor takes as a refusal. Copy is the one that tells
        // the source to leave its file alone.
        e.DragEffects = ListOf(e.Source) is not null && PathsOf(e).Count > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = PathsOf(e);
        e.Handled = true;
        if (paths.Count == 0 || _model is not { } model)
            return;

        // Posted: the compositor wants the drop handler back before anything slow happens.
        Dispatcher.UIThread.Post(() => model.Drop(paths));
    }
}
