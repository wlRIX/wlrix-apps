// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reactive.Linq;
using Avalonia;
using AvaloniaEdit;
using ReactiveUI;
using Wlrix.Writer.Localization;
using Wlrix.Writer.Models;

namespace Wlrix.Writer.ViewModels;

/// <summary>One tab: a document as it is shown in one half of the window.</summary>
/// <remarks>
/// The tab control builds one editor and moves it from tab to tab as the selection changes, so the
/// caret, selection and scroll position are kept here while a tab is in the background and put
/// back when it comes forward.
/// </remarks>
public sealed class EditorTabViewModel : ViewModelBase, IDisposable
{
    private readonly IDisposable _headerSubscription;
    private readonly IDisposable _kindSubscription;
    private readonly Action<EditorTabViewModel, ViewMode>? _modeChosen;
    private string _header = "";
    private bool _hasSelection;
    private ViewMode _mode;

    /// <param name="defaultMode">
    /// The mode a kind of previewable document opens in. Asked again whenever the document
    /// becomes a different kind -- saved as <c>.md</c>, say, or switched to Markdown -- so it
    /// arrives in the mode that kind opens in rather than the one the old kind had.
    /// </param>
    /// <param name="modeChosen">What to do when the user picks a mode in this tab.</param>
    public EditorTabViewModel(DocumentViewModel document, EditorPreferences preferences,
        Func<PreviewKind, ViewMode>? defaultMode = null,
        Action<EditorTabViewModel, ViewMode>? modeChosen = null)
    {
        Document = document;
        _modeChosen = modeChosen;
        Preferences = preferences;
        document.Views++;

        _headerSubscription = document
            .WhenAnyValue(model => model.DisplayName, model => model.IsModified)
            .Subscribe(pair => Header = pair.Item2 ? Strings.TabModified(pair.Item1) : pair.Item1);
        _kindSubscription = document
            .WhenAnyValue(model => model.PreviewKind)
            .DistinctUntilChanged()
            .Subscribe(kind => Mode = defaultMode?.Invoke(kind) ?? ViewMode.Editor);
    }

    public DocumentViewModel Document { get; }

    public EditorPreferences Preferences { get; }

    /// <summary>The tab's label: the file name, with a star while there are unsaved changes.</summary>
    public string Header
    {
        get => _header;
        private set => this.RaiseAndSetIfChanged(ref _header, value);
    }

    /// <summary>Whether the editor has text selected, which Cut and Copy need.</summary>
    public bool HasSelection
    {
        get => _hasSelection;
        set => this.RaiseAndSetIfChanged(ref _hasSelection, value);
    }

    /// <summary>
    /// Text, preview or both. Only means anything for a document that has a preview; any other
    /// is always shown as text, whatever this says.
    /// </summary>
    public ViewMode Mode
    {
        get => _mode;
        set => this.RaiseAndSetIfChanged(ref _mode, value);
    }

    /// <summary>The user picked a mode with the buttons in this tab.</summary>
    /// <remarks>
    /// Through the window rather than straight to <see cref="Mode"/>, so the choice is also
    /// remembered for the next file of the kind. This tab and not the active one: the buttons
    /// can be clicked in the half that does not have the focus.
    /// </remarks>
    public void Choose(ViewMode mode)
    {
        if (_modeChosen is { } chosen)
            chosen(this, mode);
        else
            Mode = mode;
    }

    /// <summary>The editor showing this tab, while it is the selected one. Set by the view.</summary>
    public TextEditor? Editor { get; set; }

    // The view state of a tab in the background. See the class remarks.
    public int CaretOffset { get; set; }
    public int SelectionStart { get; set; }
    public int SelectionLength { get; set; }
    public Vector ScrollOffset { get; set; }

    /// <summary>Set when the editor should take keyboard focus as soon as it shows this tab.</summary>
    public bool WantsFocus { get; set; }

    /// <summary>Raised when <see cref="WantsFocus"/> is set while the tab may already be on screen.</summary>
    public event Action? FocusRequested;

    /// <summary>Puts the keyboard focus in this tab's editor, now or as soon as it exists.</summary>
    public void RequestFocus()
    {
        WantsFocus = true;
        FocusRequested?.Invoke();
    }

    public void Dispose()
    {
        _headerSubscription.Dispose();
        _kindSubscription.Dispose();
        Document.Views--;
        Editor = null;
    }
}
