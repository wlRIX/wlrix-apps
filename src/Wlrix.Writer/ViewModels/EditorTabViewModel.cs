// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reactive.Linq;
using Avalonia;
using AvaloniaEdit;
using ReactiveUI;
using Wlrix.Writer.Localization;

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
    private string _header = "";
    private bool _hasSelection;

    public EditorTabViewModel(DocumentViewModel document, EditorPreferences preferences)
    {
        Document = document;
        Preferences = preferences;
        document.Views++;

        _headerSubscription = document
            .WhenAnyValue(model => model.DisplayName, model => model.IsModified)
            .Subscribe(pair => Header = pair.Item2 ? Strings.TabModified(pair.Item1) : pair.Item1);
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
        Document.Views--;
        Editor = null;
    }
}
