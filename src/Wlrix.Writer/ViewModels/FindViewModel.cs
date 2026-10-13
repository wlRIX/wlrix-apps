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

/// <summary>
/// Search ▸ Find: the dialog's field and options.
/// </summary>
/// <remarks>
/// One for the window's lifetime, so the options and the text survive closing and reopening the
/// dialog, as NEdit's did. The search itself is the window's: this asks it through
/// <see cref="Search"/> and reports how it went.
/// </remarks>
public sealed class FindViewModel : ViewModelBase
{
    private readonly SearchHistory _history;
    private readonly Func<SearchQuery, (FindOutcome Outcome, string? Detail)> _search;
    private readonly Action<IReadOnlyList<string>> _historyChanged;

    private string _searchText = "";
    private bool _regularExpression;
    private bool _caseSensitive;
    private bool _wholeWord;
    private bool _searchBackward;
    private bool _keepDialog;
    private string? _message;
    private bool _recalling;

    /// <param name="history">The strings searched for so far.</param>
    /// <param name="search">Runs a search in the active editor.</param>
    /// <param name="historyChanged">Told the new history after each search, to save it.</param>
    public FindViewModel(SearchHistory history, Func<SearchQuery, (FindOutcome, string?)> search,
        Action<IReadOnlyList<string>> historyChanged)
    {
        _history = history;
        _search = search;
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

    /// <summary>Whether the dialog stays open after a successful Find, so Find again finds the next one.</summary>
    public bool KeepDialog
    {
        get => _keepDialog;
        set => this.RaiseAndSetIfChanged(ref _keepDialog, value);
    }

    /// <summary>A line under the field saying why nothing was selected, or that the search wrapped.</summary>
    public string? Message
    {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    /// <summary>Clears the message, as reopening the dialog does.</summary>
    public void ClearMessage() => Message = null;

    /// <summary>Runs the search.</summary>
    /// <returns>Whether the dialog should close: something was found and Keep Dialog is off.</returns>
    public bool Search()
    {
        if (SearchText.Length == 0)
            return false;

        _history.Add(SearchText);
        _historyChanged(_history.Entries);

        var query = new SearchQuery(SearchText, RegularExpression, CaseSensitive, WholeWord, SearchBackward);
        var (outcome, detail) = _search(query);
        Message = outcome switch
        {
            FindOutcome.Found => null,
            FindOutcome.Wrapped => SearchBackward ? Strings.FindWrappedBackward : Strings.FindWrapped,
            FindOutcome.NotFound => Strings.FindNotFound,
            FindOutcome.Invalid => Strings.FindInvalid(detail ?? ""),
            _ => Strings.FindNoDocument,
        };

        return outcome is FindOutcome.Found or FindOutcome.Wrapped && !KeepDialog;
    }

    /// <summary>Up arrow: the previous string searched for.</summary>
    public void RecallOlder() => Recall(_history.Older(SearchText));

    /// <summary>Down arrow: back toward what was typed.</summary>
    public void RecallNewer() => Recall(_history.Newer());

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
