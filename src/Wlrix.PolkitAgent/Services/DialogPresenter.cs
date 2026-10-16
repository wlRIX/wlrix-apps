using Avalonia.Threading;
using Wlrix.PolkitAgent.ViewModels;
using Wlrix.PolkitAgent.Views;

namespace Wlrix.PolkitAgent.Services;

/// <summary>Puts each request on screen as an <see cref="AuthenticationWindow"/>.</summary>
internal sealed class DialogPresenter(IAccountLookup accounts) : IAuthenticationPresenter
{
    public async Task<bool> AuthenticateAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        var (users, selected) = Identities.Resolve(request.Identities, accounts);
        var model = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var model = new AuthenticationViewModel(request, users, selected, HelperSession.StartAsync);
            new AuthenticationWindow { DataContext = model }.Show();
            return model;
        });

        await using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(model.Cancel));
        return await model.Completion.ConfigureAwait(false);
    }
}
