// SPDX-License-Identifier: GPL-3.0-or-later

using ReactiveUI;
using Wlrix.Writer.Localization;
using Wlrix.Writer.Models;

namespace Wlrix.Writer.ViewModels;

/// <summary>Search ▸ Replace: the Replace/Find dialog.</summary>
/// <remarks>
/// <para>
/// The bottom row is NEdit's:
/// </para>
/// <list type="bullet">
/// <item><description><b>Replace</b> replaces the selection if it is an occurrence, and otherwise
/// the next one.</description></item>
/// <item><description><b>Find</b> only selects the next occurrence.</description></item>
/// <item><description><b>Replace &amp; Find</b> replaces, then selects the next occurrence, so
/// pressing it repeatedly works through a document one at a time.</description></item>
/// </list>
/// <para>
/// Replace All has three scopes: Tab, Selection and All Tabs. All Tabs takes the place of
/// NEdit's Multiple Files, since here the files are tabs of one window.
/// </para>
/// </remarks>
public sealed class ReplaceViewModel : SearchDialogViewModel
{
    private string _replaceText = "";
    private bool _hasSelection;
    private bool _hasOtherDocuments;

    public ReplaceViewModel(ISearchTarget target, SearchHistory history, Action<IReadOnlyList<string>> historyChanged)
        : base(target, history, historyChanged)
    {
        this.WhenAnyValue(model => model.SearchText, model => model.HasSelection, model => model.HasOtherDocuments)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(CanSearch));
                this.RaisePropertyChanged(nameof(CanReplaceInSelection));
                this.RaisePropertyChanged(nameof(CanReplaceInAllTabs));
            });
    }

    /// <summary>Whether there is anything to look for: every button but Cancel waits on it.</summary>
    public bool CanSearch => SearchText.Length > 0;

    public bool CanReplaceInSelection => CanSearch && HasSelection;

    public bool CanReplaceInAllTabs => CanSearch && HasOtherDocuments;

    public string ReplaceText
    {
        get => _replaceText;
        set => this.RaiseAndSetIfChanged(ref _replaceText, value);
    }

    /// <summary>Whether the active editor has text selected, which Replace All in Selection needs. Kept by the window.</summary>
    public bool HasSelection
    {
        get => _hasSelection;
        set => this.RaiseAndSetIfChanged(ref _hasSelection, value);
    }

    /// <summary>Whether more than one document is open, which Replace All in All Tabs needs. Kept by the window.</summary>
    public bool HasOtherDocuments
    {
        get => _hasOtherDocuments;
        set => this.RaiseAndSetIfChanged(ref _hasOtherDocuments, value);
    }

    /// <summary>Replace.</summary>
    /// <returns>Whether the dialog should close.</returns>
    public bool Replace() => SearchText.Length > 0 && Report(Target.ReplaceNext(Record(), ReplaceText, thenFind: false));

    /// <summary>Find.</summary>
    /// <returns>Whether the dialog should close.</returns>
    public bool Find() => SearchText.Length > 0 && Report(Target.FindNext(Record()));

    /// <summary>Replace &amp; Find.</summary>
    /// <returns>Whether the dialog should close.</returns>
    public bool ReplaceAndFind() =>
        SearchText.Length > 0 && Report(Target.ReplaceNext(Record(), ReplaceText, thenFind: true));

    /// <summary>Replace all in: Tab, Selection or All Tabs.</summary>
    /// <returns>Whether the dialog should close.</returns>
    public bool ReplaceAll(ReplaceScope scope)
    {
        if (SearchText.Length == 0)
            return false;

        var result = Target.ReplaceAll(Record(), ReplaceText, scope);
        var summary = scope == ReplaceScope.AllTabs
            ? Strings.ReplacedInDocuments(result.Count, result.Documents)
            : Strings.Replaced(result.Count);
        return Report(result, summary);
    }
}
