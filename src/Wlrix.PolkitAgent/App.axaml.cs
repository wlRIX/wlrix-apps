using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;
using Wlrix.Common;
using Wlrix.Common.Localization;
using Wlrix.PolkitAgent.Localization;
using Wlrix.PolkitAgent.Services;
using Wlrix.Theme;
using ZLogger;

namespace Wlrix.PolkitAgent;

/// <summary>
/// A windowless process for the length of the session: it registers with polkitd, and opens an
/// <see cref="Views.AuthenticationWindow"/> only while somebody needs to type a password.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;
    private DBusConnection? _bus;
    private AgentRegistration? _registration;

    public override void Initialize()
    {
        // Before any XAML is loaded: {loc:Tr} in the window resolves against this.
        TrExtension.Catalog = Strings.Catalog;
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _ = SessionScheme.FollowAsync(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddZLogger("polkit-agent");
            _services = services.BuildServiceProvider();

            desktop.ShutdownRequested += (_, _) => _ = StopAsync();
            _ = RunAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var logger = _services!.GetRequiredService<ILogger<App>>();
        try
        {
            _bus = new DBusConnection(DBusAddress.System!);
            await _bus.ConnectAsync().ConfigureAwait(false);
            _bus.AddMethodHandler(new AuthenticationAgent(
                new DialogPresenter(new LibcAccounts()),
                _services!.GetRequiredService<ILogger<AuthenticationAgent>>()));

            _registration = new AgentRegistration(_bus);
            var session = await _registration.SessionIdAsync().ConfigureAwait(false);
            if (await _registration.RegisterAsync(session).ConfigureAwait(false) == RegistrationResult.AlreadyRegistered)
            {
                // Somebody's agent is already answering for this session: nothing left to do.
                logger.ZLogInformation($"Session {session} already has an authentication agent; exiting.");
                Dispatcher.UIThread.Post(() => desktop.Shutdown(0));
                return;
            }

            logger.ZLogInformation($"Registered as the authentication agent for session {session}.");
        }
        catch (Exception ex)
        {
            logger.ZLogError(ex, $"Could not register with polkit.");
            Dispatcher.UIThread.Post(() => desktop.Shutdown(1));
        }
    }

    private async Task StopAsync()
    {
        if (_registration is { } registration)
            await registration.DisposeAsync().ConfigureAwait(false);
        _bus?.Dispose();
        _services?.Dispose();
    }
}
