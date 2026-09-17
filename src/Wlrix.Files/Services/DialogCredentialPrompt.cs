using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Wlrix.Files.Core.Remote;
using Wlrix.Files.Localization;
using Wlrix.Files.Views;

namespace Wlrix.Files.Services;

/// <summary>Asks for a share's password with a dialog.</summary>
/// <remarks>
/// The application-side half of <see cref="ICredentialPrompt"/>. The ask arrives from a worker
/// thread — a mount is created wherever the first navigation to it happened — so this marshals
/// to the dispatcher and waits, the same shape the conflict resolver uses and for the same
/// reason.
/// </remarks>
public sealed class DialogCredentialPrompt(Func<Window?> owner) : ICredentialPrompt
{
    /// <inheritdoc/>
    public Task<CredentialAnswer?> AskAsync(
        ShareRef share, string? why, bool canRemember, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<CredentialAnswer?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (owner() is not { } window)
                {
                    // No window to hang a dialog on, which happens if the last one closed
                    // while a mount was being made. Answering null cancels the connection
                    // rather than leaving the worker waiting for a dialog nobody will see.
                    answer.TrySetResult(null);
                    return;
                }

                var request = await ConnectDialog
                    .ShowForAsync(window, Strings.ConnectAuthTitle, share, why, canRemember)
                    .ConfigureAwait(true);
                answer.TrySetResult(request is null
                    ? null
                    : new CredentialAnswer(request.Credentials, request.Remember));
            }
            catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
            {
                answer.TrySetResult(null);
            }
        });
        return answer.Task.WaitAsync(cancellationToken);
    }
}
