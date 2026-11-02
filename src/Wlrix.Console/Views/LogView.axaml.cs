using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Wlrix.Console.ViewModels;

namespace Wlrix.Console.Views;

/// <summary>
/// The body of one log tab. Besides the list itself it keeps the view following the end of the
/// log while it is scrolled to the bottom, the way a terminal does, and copies selected lines.
/// </summary>
public partial class LogView : UserControl
{
    private readonly ListBox _list;
    private LogLineBuffer? _lines;
    private bool _scrollPosted;

    public LogView()
    {
        InitializeComponent();
        _list = this.FindControl<ListBox>("List")!;
        _list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_lines is not null)
            _lines.CollectionChanged -= OnLinesChanged;

        // The tab control may hand this same view the other log when the tab changes; either way
        // a log is shown from its end, where the newest output is.
        _lines = (DataContext as LogViewModel)?.Lines;
        if (_lines is not null)
        {
            _lines.CollectionChanged += OnLinesChanged;
            PostScrollToEnd();
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // The log may have been given to this view before it had a scroll viewer to move.
        PostScrollToEnd();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Checked before the list has laid the change out, so the offsets still describe what
        // the user was looking at: follow the new lines only if that was the end.
        if (IsAtEnd())
            PostScrollToEnd();
    }

    // The list's own Scroll property stays null under the Wlrix theme, whose ListBox template
    // does not name its scroll viewer PART_ScrollViewer; it is the first one inside the list.
    private ScrollViewer? Scroller => _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private bool IsAtEnd()
    {
        if (Scroller is not { } scroll)
            return true;
        return scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 1;
    }

    private void PostScrollToEnd()
    {
        if (_scrollPosted)
            return;
        _scrollPosted = true;

        // After the list has taken in the new lines, or the new log.
        Dispatcher.UIThread.Post(() =>
        {
            _scrollPosted = false;
            if (_lines is not { Count: > 0 })
                return;

            // Down to the end of the extent the virtualizing panel estimated, which realizes the
            // last lines; then, once layout has measured them and settled the extent, the rest
            // of the way. Ending exactly at the end is what lets the next lines be followed.
            // (ScrollIntoView would line the last row up with the bottom edge, short of the
            // list's padding, so the view would not count as at the end.)
            if (Scroller is not { } scroll)
                return;
            scroll.ScrollToEnd();
            Dispatcher.UIThread.Post(scroll.ScrollToEnd, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        var copy = Application.Current?.PlatformSettings?.HotkeyConfiguration.Copy;
        if (copy is not null && copy.Any(gesture => gesture.Matches(e)))
        {
            CopySelection();
            e.Handled = true;
        }
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e) => CopySelection();

    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => _list.SelectAll();

    /// <summary>The selected lines, in log order whatever order they were picked in.</summary>
    internal string SelectedText()
    {
        if (_lines is not { } lines)
            return string.Empty;
        var indexes = _list.Selection.SelectedIndexes.Where(i => i < lines.Count).Order();
        return string.Join('\n', indexes.Select(i => lines[i]));
    }

    private void CopySelection()
    {
        var text = SelectedText();
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            _ = clipboard.SetTextAsync(text);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
