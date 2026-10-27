// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.TextMate;
using ReactiveUI;
using Wlrix.Avalonia;
using Wlrix.Writer.Models;
using Wlrix.Writer.Services;
using Wlrix.Writer.ViewModels;

namespace Wlrix.Writer.Views;

/// <summary>The editor under one tab strip.</summary>
/// <remarks>
/// <para>
/// A tab control builds this once and hands it each tab in turn as the selection moves, so it is
/// written to change documents: the outgoing tab's caret, selection and scroll position are saved
/// on it, and the incoming tab's are put back.
/// </para>
/// <para>
/// A Markdown or SVG document can also be shown rendered: beside the text, or in its place. The
/// tab's <see cref="EditorTabViewModel.Mode"/> says which, and the layout follows it. Beside the
/// text, a Markdown preview scrolls along with the editor.
/// </para>
/// <para>
/// Highlighting is a TextMate installation per editor. Its theme is rebuilt from the session's
/// color scheme whenever the scheme's text colors change, so the token colors follow a scheme
/// change along with everything else.
/// </para>
/// </remarks>
public partial class EditorView : UserControl
{
    private readonly SerialDisposable _documentSubscriptions = new();
    private EditorTabViewModel? _tab;
    private TextMate.Installation? _textMate;
    private (bool Dark, Color Foreground, Color Background)? _themeKey;
    private bool _schemePending;
    private bool _attached;

    public EditorView()
    {
        InitializeComponent();

        var options = Editor.Options;
        // A plain-text editor: no underlined URLs to ctrl-click, no current-line band, and no
        // scrolling past the last line, none of which NEdit did.
        options.EnableHyperlinks = false;
        options.EnableEmailHyperlinks = false;
        options.HighlightCurrentLine = false;
        options.AllowScrollBelowDocument = false;
        options.EnableTextDragDrop = true;

        // AvaloniaEdit installs its own search panel on Ctrl+F. Search is the Writer's own Find
        // dialog, still to come, and two different searches would be one too many.
        Editor.SearchPanel?.Uninstall();

        Editor.TextArea.SelectionChanged += (_, _) =>
        {
            if (_tab is { } tab)
                tab.HasSelection = !Editor.TextArea.Selection.IsEmpty;
        };

        // A scheme change swaps a dictionary deep in the application's styles, and the
        // notification reaches every control here, several times over. One look after they settle.
        ResourcesChanged += (_, _) => ScheduleScheme();

        TextOnlyButton.Click += (_, _) => Choose(ViewMode.Editor);
        SplitButton.Click += (_, _) => Choose(ViewMode.Split);
        PreviewOnlyButton.Click += (_, _) => Choose(ViewMode.Preview);
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => FollowEditor();
        Preview.Rendered += FollowEditor;
    }

    private void Choose(ViewMode mode)
    {
        _tab?.Choose(mode);
        // The buttons toggle themselves on a click; put every mark back where the mode is,
        // including the one just clicked if the mode did not change.
        ApplyMode();
    }

    /// <summary>The mode the tab is in, as it can be shown: text alone for a document with no preview.</summary>
    private ViewMode EffectiveMode =>
        _tab is { } tab && tab.Document.PreviewKind != PreviewKind.None ? tab.Mode : ViewMode.Editor;

    /// <summary>Lays the tab out for its mode: which wells show, and how the width is shared.</summary>
    private void ApplyMode()
    {
        var mode = EffectiveMode;
        var previewable = _tab?.Document.PreviewKind is { } kind && kind != PreviewKind.None;
        var columns = Layout.ColumnDefinitions;
        var showText = mode != ViewMode.Preview;
        var showPreview = mode != ViewMode.Editor;
        var wasPreviewShown = PreviewWell.IsVisible;

        columns[0].Width = showText ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        columns[1].Width = mode == ViewMode.Split ? GridLength.Auto : new GridLength(0);
        columns[2].Width = showPreview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TextWell.IsVisible = showText;
        PreviewSplitter.IsVisible = mode == ViewMode.Split;
        PreviewWell.IsVisible = showPreview;

        ModeButtons.IsVisible = previewable;
        TextOnlyButton.IsChecked = mode == ViewMode.Editor;
        SplitButton.IsChecked = mode == ViewMode.Split;
        PreviewOnlyButton.IsChecked = mode == ViewMode.Preview;

        // The preview does not render while hidden, so it is brought up to date as it appears.
        if (showPreview && !wasPreviewShown)
            Preview.Refresh();
    }

    /// <summary>Scrolls the preview to what is at the top of the editor, while both are on screen.</summary>
    private void FollowEditor()
    {
        if (EffectiveMode != ViewMode.Split || _tab?.Document.PreviewKind != PreviewKind.Markdown)
            return;

        var textView = Editor.TextArea.TextView;
        var top = textView.ScrollOffset.Y;
        var atEnd = top > 0 && top + textView.Bounds.Height >= textView.DocumentHeight - 1;
        var line = textView.GetDocumentLineByVisualTop(top);
        Preview.FollowEditor(line.LineNumber - 1, atEnd);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Detach();
        if (DataContext is EditorTabViewModel tab && _attached)
            Attach(tab);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ApplyScheme();
        if (_tab is null && DataContext is EditorTabViewModel tab)
            Attach(tab);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Detach();
        _textMate?.Dispose();
        _textMate = null;
        _themeKey = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void Attach(EditorTabViewModel tab)
    {
        _tab = tab;
        tab.Editor = Editor;

        var document = tab.Document;
        Editor.Document = document.Text;

        var subscriptions = new CompositeDisposable
        {
            document.WhenAnyValue(model => model.LanguageId, model => model.HighlightSyntax)
                .Subscribe(_ => ApplyGrammar()),
            document.WhenAnyValue(model => model.Tabs).Subscribe(ApplyTabs),
            tab.WhenAnyValue(model => model.Mode).Subscribe(_ => ApplyMode()),
            // A document that becomes Markdown or SVG (Save As, or a change of language) has a
            // preview to draw, or a different one.
            document.WhenAnyValue(model => model.PreviewKind).Skip(1).Subscribe(_ =>
            {
                ApplyMode();
                Preview.Refresh();
            }),
        };
        Preview.Show(document);
        tab.FocusRequested += OnFocusRequested;
        subscriptions.Add(Disposable.Create(() => tab.FocusRequested -= OnFocusRequested));
        _documentSubscriptions.Disposable = subscriptions;

        // The saved view state, clamped: the document may have been edited in the other half,
        // or reloaded, while this tab was in the background.
        var length = document.Text.TextLength;
        var start = Math.Clamp(tab.SelectionStart, 0, length);
        var selected = Math.Clamp(tab.SelectionLength, 0, length - start);
        Editor.Select(start, selected);
        Editor.CaretOffset = Math.Clamp(tab.CaretOffset, 0, length);
        tab.HasSelection = selected > 0;

        // Scrolling has to wait for the new document to be laid out, or there is nothing yet to
        // scroll through.
        var offset = tab.ScrollOffset;
        Dispatcher.UIThread.Post(() =>
        {
            if (_tab != tab)
                return;
            Editor.ScrollToHorizontalOffset(offset.X);
            Editor.ScrollToVerticalOffset(offset.Y);
        }, DispatcherPriority.Loaded);

        OnFocusRequested();
    }

    private void Detach()
    {
        if (_tab is not { } tab)
            return;

        tab.CaretOffset = Editor.CaretOffset;
        tab.SelectionStart = Editor.SelectionStart;
        tab.SelectionLength = Editor.SelectionLength;
        tab.ScrollOffset = new Vector(Editor.HorizontalOffset, Editor.VerticalOffset);
        if (tab.Editor == Editor)
            tab.Editor = null;

        _documentSubscriptions.Disposable = null;
        Preview.Show(null);
        _tab = null;
    }

    private void OnFocusRequested()
    {
        if (_tab is not { WantsFocus: true } tab)
            return;

        // Posted: the request usually comes in the same breath as the tab being added, before
        // the editor is in the tree and able to take focus.
        Dispatcher.UIThread.Post(() =>
        {
            if (_tab != tab || !tab.WantsFocus)
                return;
            tab.WantsFocus = false;
            Editor.TextArea.Focus();
            Editor.TextArea.Caret.BringCaretToView();
        }, DispatcherPriority.Loaded);
    }

    private void ApplyTabs(TabSettings settings)
    {
        // IndentationSize is both how wide a tab character is drawn and how many spaces an
        // emulated tab inserts.
        Editor.Options.IndentationSize = settings.Width;
        Editor.Options.ConvertTabsToSpaces = settings.UseSpaces;
    }

    private void ApplyGrammar()
    {
        if (_textMate is null || _tab?.Document is not { } document)
            return;

        var scope = document.HighlightSyntax ? WriterTheme.Shared.Languages.Find(document.LanguageId)?.Scope : null;
        _textMate.SetGrammar(scope);
    }

    private void ScheduleScheme()
    {
        if (_schemePending)
            return;
        _schemePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _schemePending = false;
            ApplyScheme();
        }, DispatcherPriority.Background);
    }

    /// <summary>Brings the caret and the highlighting theme into line with the current scheme.</summary>
    private void ApplyScheme()
    {
        if (!_attached)
            return;

        var foreground = ColorOf("WlrixTextForeground", Colors.Black);
        var background = ColorOf("WlrixTextAreaBackground", Colors.Gray);

        // The caret is a direct property, which a style cannot set.
        Editor.TextArea.CaretBrush = new SolidColorBrush(foreground);

        var scheme = WlrixSchemes.ById(WlrixTheme.From(Application.Current)?.Scheme);
        var key = (scheme?.IsDark ?? false, foreground, background);
        if (_themeKey == key)
            return;
        _themeKey = key;

        var theme = WriterTheme.Shared.Build(key.Item1, foreground, background);
        if (_textMate is null)
        {
            _textMate = TextMate.InstallTextMate(Editor, WriterTheme.Shared.OptionsFor(theme));
            ApplyGrammar();
        }
        else
            _textMate.SetTheme(theme);
    }

    private Color ColorOf(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is ISolidColorBrush brush
            ? brush.Color
            : fallback;
}
