using ReactiveUI;
using Wlrix.Pinentry.Localization;
using Wlrix.Pinentry.Services;

namespace Wlrix.Pinentry.ViewModels;

/// <summary>What a dialog is asking for.</summary>
internal enum DialogKind
{
    /// <summary>A passphrase or PIN (GETPIN).</summary>
    Pin,

    /// <summary>A question to answer (CONFIRM).</summary>
    Confirm,

    /// <summary>A notice to acknowledge (MESSAGE, CONFIRM --one-button).</summary>
    Message,
}

/// <summary>
/// One pinentry dialog. gpg-agent supplies the words -- the description of the key, the prompt,
/// the button labels in the user's language -- and this decides only what falls back to ours.
/// </summary>
internal sealed class PinentryViewModel : ViewModelBase
{
    private readonly DialogText _text;
    private readonly TaskCompletionSource<PinResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _pin = string.Empty;
    private string _repeatPin = string.Empty;
    private bool _showPin;
    private string? _error;

    /// <summary>For the XAML designer.</summary>
    public PinentryViewModel() : this(new DialogText
    {
        Description = "Please enter the passphrase to unlock the OpenPGP secret key:\n\"Example <user@example.com>\"\n255-bit EDDSA key, ID 0123456789ABCDEF,\ncreated 2023-06-06.",
        Prompt = "Passphrase:",
    }, DialogKind.Pin)
    {
    }

    public PinentryViewModel(DialogText text, DialogKind kind)
    {
        _text = text;
        Kind = kind;
        _error = text.Error;
        if (text.Timeout > 0)
            _ = TimeOutAsync(text.Timeout);
    }

    public DialogKind Kind { get; }

    /// <summary>Resolves when the dialog is answered.</summary>
    public Task<PinResult> Completion => _completion.Task;

    public string Title => _text.Title is { Length: > 0 } title ? title : Strings.WindowTitle;

    /// <summary>The bold line by the icon: gpg-agent's title when it gave one.</summary>
    public string Heading => _text.Title is { Length: > 0 } title ? title
        : Kind == DialogKind.Pin ? Strings.PinHeading : Strings.ConfirmHeading;

    public string Description => _text.Description ?? string.Empty;

    public bool HasDescription => Description.Length > 0;

    /// <summary>Why the last passphrase was not accepted ("Bad Passphrase (try 2 of 3)"), or a mismatch.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            this.RaiseAndSetIfChanged(ref _error, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public bool AsksForPin => Kind == DialogKind.Pin;

    public string Prompt => Label(_text.Prompt) ?? Strings.PinPrompt;

    public bool AsksTwice => AsksForPin && _text.Repeat is not null;

    public string RepeatPrompt => Label(_text.Repeat) ?? Strings.RepeatPrompt;

    public string Pin
    {
        get => _pin;
        set => this.RaiseAndSetIfChanged(ref _pin, value);
    }

    public string RepeatPin
    {
        get => _repeatPin;
        set => this.RaiseAndSetIfChanged(ref _repeatPin, value);
    }

    public bool ShowPin
    {
        get => _showPin;
        set
        {
            this.RaiseAndSetIfChanged(ref _showPin, value);
            this.RaisePropertyChanged(nameof(MaskChar));
        }
    }

    public char MaskChar => _showPin ? '\0' : '•';

    public string OkLabel => Label(_text.Ok) ?? Strings.Ok;

    public string CancelLabel => Label(_text.Cancel) ?? Strings.Cancel;

    public string NotOkLabel => Label(_text.NotOk) ?? string.Empty;

    /// <summary>A third button, only for a CONFIRM that named one (SETNOTOK).</summary>
    public bool ShowNotOk => Kind == DialogKind.Confirm && _text.NotOk is { Length: > 0 };

    /// <summary>A message has only the one button.</summary>
    public bool ShowCancel => Kind != DialogKind.Message;

    public void Ok()
    {
        if (AsksTwice && Pin != RepeatPin)
        {
            Error = Label(_text.RepeatError) ?? Strings.RepeatMismatch;
            Pin = string.Empty;
            RepeatPin = string.Empty;
            return;
        }

        var pin = Pin;
        Pin = string.Empty;
        RepeatPin = string.Empty;
        _completion.TrySetResult(new PinResult(DialogOutcome.Ok, AsksForPin ? pin : null, AsksTwice));
    }

    public void NotOk() => _completion.TrySetResult(new PinResult(DialogOutcome.NotOk));

    public void Cancel()
    {
        Pin = string.Empty;
        RepeatPin = string.Empty;
        _completion.TrySetResult(new PinResult(DialogOutcome.Canceled));
    }

    private async Task TimeOutAsync(int seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        _completion.TrySetResult(new PinResult(DialogOutcome.TimedOut));
    }

    /// <summary>
    /// A label as gpg-agent spells it, without its mnemonic: GTK-style <c>_OK</c> is shown as
    /// <c>OK</c>, and <c>__</c> as a literal underscore. Null when there is no label.
    /// </summary>
    internal static string? Label(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var result = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '_')
            {
                if (i + 1 < text.Length && text[i + 1] == '_')
                {
                    result.Append('_');
                    i++;
                }

                continue;
            }

            result.Append(text[i]);
        }

        return result.ToString();
    }
}
