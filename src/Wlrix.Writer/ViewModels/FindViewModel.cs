// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reactive.Linq;
using ReactiveUI;
using Wlrix.Writer.Localization;
using Wlrix.Writer.Models;

namespace Wlrix.Writer.ViewModels;

/// <summary>How a search went.</summary>
public enum FindOutcome
{
    Found,

    /// <summary>Found, after going past the end of the document (or the start, going backward).</summary>
    Wrapped,

    NotFound,

    /// <summary>The pattern is not a valid regular expression, or took too long to match.</summary>
    Invalid,

    /// <summary>There is no editor to search in.</summary>
    NoDocument,
}

/// <summary>What a search or replacement did.</summary>
/// <param name="Detail">Why the pattern is invalid, for <see cref="FindOutcome.Invalid"/>.</param>
/// <param name="Count">For Replace All: how many occurrences were replaced.</param>
/// <param name="Documents">For Replace All: in how many documents.</param>
public sealed record SearchResult(FindOutcome Outcome, string? Detail = null, int Count = 0, int Documents = 0);

/// <summary>Where Replace All works.</summary>
public enum ReplaceScope
{
    /// <summary>The whole of the active tab's document.</summary>
    Tab,

    /// <summary>Only the selected text in the active tab.</summary>
    Selection,

    /// <summary>Every open document.</summary>
    AllTabs,
}

/// <summary>The window, as the search dialogs see it: something to search and replace in.</summary>
public interface ISearchTarget
{
    /// <summary>Selects the next match in the active editor.</summary>
    SearchResult FindNext(SearchQuery query);

    /// <summary>
    /// Replaces the selection if it is a match, otherwise the next match; then, with
    /// <paramref name="thenFind"/>, selects the match after it.
    /// </summary>
    SearchResult ReplaceNext(SearchQuery query, string replacement, bool thenFind);

    /// <summary>Replaces every match in <paramref name="scope"/>.</summary>
    SearchResult ReplaceAll(SearchQuery query, string replacement, ReplaceScope scope);
}

/// <summary>
/// What the Find and Replace/Find dialogs have in common: the string to find, the options, the
/// up-arrow history and the line that reports how the last search went.
/// </summary>
/// <remarks>
/// One of each dialog's model lives as long as the window, so the text and the options are still
/// there when a dialog is opened again, as NEdit kept them. Both dialogs share one
/// <see cref="SearchHistory"/>.
/// </remarks>
public abstract class SearchDialogViewModel : ViewModelBase
{
    private readonly SearchHistory _history;
    private readonly Action<IReadOnlyList<string>> _historyChanged;

    private string _searchText = "";
    private bool _regularExpression;
    private bool _caseSensitive;
    private bool _wholeWord;
    private bool _searchBackward;
    private bool _keepDialog;
    private string? _message;
    private bool _recalling;

    /// <param name="target">Where the searching happens.</param>
    /// <param name="history">The strings searched for so far.</param>
    /// <param name="historyChanged">Told the new history after each search, to save it.</param>
    protected SearchDialogViewModel(ISearchTarget target, SearchHistory history, Action<IReadOnlyList<string>> historyChanged)
    {
        Target = target;
        _history = history;
        _historyChanged = historyChanged;

        // Typing starts a new search, so whatever the last one said no longer applies, and the
        // next Up starts from the newest entry again.
        this.WhenAnyValue(model => model.SearchText).Skip(1).Subscribe(_ =>
        {
            Message = null;
            if (!_recalling)
                _history.ResetRecall();
        });
    }

    protected ISearchTarget Target { get; }

    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    public bool RegularExpression
    {
        get => _regularExpression;
        set => this.RaiseAndSetIfChanged(ref _regularExpression, value);
    }

    public bool CaseSensitive
    {
        get => _caseSensitive;
        set => this.RaiseAndSetIfChanged(ref _caseSensitive, value);
    }

    public bool WholeWord
    {
        get => _wholeWord;
        set => this.RaiseAndSetIfChanged(ref _wholeWord, value);
    }

    public bool SearchBackward
    {
        get => _searchBackward;
        set => this.RaiseAndSetIfChanged(ref _searchBackward, value);
    }

    /// <summary>Whether the dialog stays open after it has done something, so it can do it again.</summary>
    public bool KeepDialog
    {
        get => _keepDialog;
        set => this.RaiseAndSetIfChanged(ref _keepDialog, value);
    }

    /// <summary>A line under the options saying why nothing happened, or what did.</summary>
    public string? Message
    {
        get => _message;
        protected set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    /// <summary>Clears the message, as reopening the dialog does.</summary>
    public void ClearMessage() => Message = null;

    /// <summary>Up arrow: the previous string searched for.</summary>
    public void RecallOlder() => Recall(_history.Older(SearchText));

    /// <summary>Down arrow: back toward what was typed.</summary>
    public void RecallNewer() => Recall(_history.Newer());

    /// <summary>The query the dialog describes, with the search string added to the history.</summary>
    protected SearchQuery Record()
    {
        _history.Add(SearchText);
        _historyChanged(_history.Entries);
        return new SearchQuery(SearchText, RegularExpression, CaseSensitive, WholeWord, SearchBackward);
    }

    /// <summary>Puts the message for <paramref name="result"/> under the options.</summary>
    /// <returns>Whether the dialog should close: the action did something and Keep Dialog is off.</returns>
    protected bool Report(SearchResult result, string? success = null)
    {
        Message = result.Outcome switch
        {
            FindOutcome.Found => success,
            FindOutcome.Wrapped => success ?? (SearchBackward ? Strings.FindWrappedBackward : Strings.FindWrapped),
            FindOutcome.NotFound => Strings.FindNotFound,
            FindOutcome.Invalid => Strings.FindInvalid(result.Detail ?? ""),
            _ => Strings.FindNoDocument,
        };

        return result.Outcome is FindOutcome.Found or FindOutcome.Wrapped && !KeepDialog;
    }

    private void Recall(string? text)
    {
        if (text is null)
            return;
        _recalling = true;
        try
        {
            SearchText = text;
        }
        finally
        {
            _recalling = false;
        }
    }
}

/// <summary>Search ▸ Find.</summary>
public sealed class FindViewModel(ISearchTarget target, SearchHistory history, Action<IReadOnlyList<string>> historyChanged)
    : SearchDialogViewModel(target, history, historyChanged)
{
    /// <summary>Runs the search.</summary>
    /// <returns>Whether the dialog should close: something was found and Keep Dialog is off.</returns>
    public bool Search() => SearchText.Length > 0 && Report(Target.FindNext(Record()));
}
