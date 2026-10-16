using Avalonia.Threading;
using Wlrix.Pinentry.ViewModels;
using Wlrix.Pinentry.Views;

namespace Wlrix.Pinentry.Services;

/// <summary>Each GETPIN, CONFIRM and MESSAGE as a <see cref="PinentryWindow"/>.</summary>
internal sealed class WindowUi : IPinentryUi
{
    public async Task<PinResult> GetPinAsync(DialogText text) =>
        await ShowAsync(text, DialogKind.Pin).ConfigureAwait(false);

    public async Task<DialogOutcome> ConfirmAsync(DialogText text, bool oneButton) =>
        (await ShowAsync(text, oneButton ? DialogKind.Message : DialogKind.Confirm).ConfigureAwait(false)).Outcome;

    private static async Task<PinResult> ShowAsync(DialogText text, DialogKind kind)
    {
        var model = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var model = new PinentryViewModel(text, kind);
            new PinentryWindow { DataContext = model }.Show();
            return model;
        });
        return await model.Completion.ConfigureAwait(false);
    }
}
