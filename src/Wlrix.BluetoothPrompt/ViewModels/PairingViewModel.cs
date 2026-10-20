using ReactiveUI;
using Wlrix.BluetoothPrompt.Localization;
using Wlrix.BluetoothPrompt.Protocol;

namespace Wlrix.BluetoothPrompt.ViewModels;

/// <summary>
/// One pairing question. <see cref="Completion"/> resolves with the answer, or with null for
/// Cancel, Deny, or a display dialog closed by hand.
/// </summary>
internal sealed class PairingViewModel : ViewModelBase
{
    private readonly TaskCompletionSource<PairingAnswer?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _entry = string.Empty;

    /// <summary>For the XAML designer.</summary>
    public PairingViewModel() : this(new PairingRequest(PairingMode.Confirm, "WH-1000XM4", "012345", null))
    {
    }

    public PairingViewModel(PairingRequest request) => Request = request;

    public PairingRequest Request { get; }

    public Task<PairingAnswer?> Completion => _completion.Task;

    public string Title => Strings.WindowTitle;

    public string Heading => Request.Mode == PairingMode.Authorize
        ? Strings.Catalog.Format("AuthorizeHeading", Request.Device)
        : Strings.Catalog.Format("PairHeading", Request.Device);

    public string Description => Request.Mode switch
    {
        PairingMode.Confirm => Strings.Catalog.Format("ConfirmDescription", Request.Device),
        PairingMode.Display => Strings.Catalog.Format("DisplayDescription", Request.Device),
        PairingMode.Pin => Strings.Catalog.Format("PinDescription", Request.Device),
        PairingMode.Passkey => Strings.Catalog.Format("PasskeyDescription", Request.Device),
        _ when Request.Service is { } service =>
            Strings.Catalog.Format("AuthorizeServiceDescription", Request.Device, Strings.ServiceName(service)),
        _ => Strings.Catalog.Format("AuthorizeDescription", Request.Device),
    };

    /// <summary>The number to compare or to type, large; empty when there is none.</summary>
    public string Code => Codes.Grouped(Request.Code);

    public bool ShowsCode => Request.Mode is PairingMode.Confirm or PairingMode.Display;

    public bool AsksForEntry => Request.Mode is PairingMode.Pin or PairingMode.Passkey;

    /// <summary>Whether there is a yes button at all: a code on display is only closed.</summary>
    public bool HasAccept => Request.Mode != PairingMode.Display;

    public string AcceptLabel => Request.Mode == PairingMode.Authorize ? Strings.Allow : Strings.Pair;

    public string CancelLabel => Request.Mode switch
    {
        PairingMode.Authorize => Strings.Deny,
        _ => Strings.Cancel,
    };

    public string Entry
    {
        get => _entry;
        set
        {
            this.RaiseAndSetIfChanged(ref _entry, value);
            this.RaisePropertyChanged(nameof(CanAccept));
        }
    }

    public bool CanAccept => Request.Mode switch
    {
        PairingMode.Pin => Codes.PinFits(Entry),
        PairingMode.Passkey => Codes.Passkey(Entry) is not null,
        PairingMode.Display => false,
        _ => true,
    };

    public void Accept()
    {
        if (!CanAccept)
            return;
        var answer = Request.Mode switch
        {
            PairingMode.Pin => new PairingAnswer(Pin: Entry),
            PairingMode.Passkey => new PairingAnswer(Passkey: Codes.Passkey(Entry)),
            _ => new PairingAnswer(Accept: true),
        };
        Entry = string.Empty;
        _completion.TrySetResult(answer);
    }

    public void Cancel()
    {
        Entry = string.Empty;
        _completion.TrySetResult(null);
    }
}
