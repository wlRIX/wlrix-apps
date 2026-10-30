using Avalonia.Controls;
using Avalonia.Threading;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Files.Core.Remote;
using Wlrix.Files.Localization;

namespace Wlrix.Files.Services;

/// <summary>Asks whether to trust a server, with a dialog.</summary>
/// <remarks>
/// Marshals to the dispatcher for the reason <see cref="DialogCredentialPrompt"/> does. No window
/// to ask in, or a dialog closed without an answer, means no.
/// </remarks>
public sealed class DialogServerTrustPrompt(Func<Window?> owner) : IServerTrustPrompt
{
    /// <inheritdoc/>
    public Task<bool> AskAsync(ServerKey key, ServerKeyStanding standing, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (owner() is not { } window)
                {
                    answer.TrySetResult(false);
                    return;
                }

                var changed = standing == ServerKeyStanding.Changed;
                var result = await MessageDialog.ShowAsync(window,
                    DialogType.Warning,
                    changed
                        ? Strings.ServerChangedMessage(key.Server, key.Fingerprint)
                        : Strings.ServerUnknownMessage(key.Server, key.Fingerprint),
                    width: 460,
                    buttons: DialogButtons.OkCancel,
                    title: changed ? Strings.ServerChangedTitle : Strings.ServerUnknownTitle,
                    okText: Strings.ServerTrustButton,
                    cancelText: Strings.DialogCancel).ConfigureAwait(true);
                answer.TrySetResult(result == DialogResult.Ok);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
            {
                answer.TrySetResult(false);
            }
        });
        return answer.Task.WaitAsync(cancellationToken);
    }
}
