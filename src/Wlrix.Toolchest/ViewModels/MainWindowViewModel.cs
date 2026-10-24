using System.Collections.ObjectModel;
using Avalonia.Threading;
using Wlrix.Toolchest.Localization;
using Wlrix.Toolchest.Services;

namespace Wlrix.Toolchest.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    /// <summary>The installed name of the Desks Overview, launched by Desktop → Extra Desks.</summary>
    private const string DesksProgram = "wlrix-desks";

    /// <summary>The installed name of the Audio Panel, launched by Desktop → Control Audio.</summary>
    private const string AudioProgram = "wlrix-settings-audio";

    /// <summary>
    /// The installed name of the System &amp; Desktop Sounds panel, launched by Desktop → Customize
    /// → Sounds.
    /// </summary>
    private const string SoundProgram = "wlrix-settings-sound";

    /// <summary>
    /// The installed name of the clipboard history popup, launched by Desktop → Clipboard
    /// History. The same program <c>Super+V</c> starts.
    /// </summary>
    private const string ClipboardHistoryProgram = "wlrix-clipboard-history";

    /// <summary>
    /// The installed name of the Icon Catalog, launched by Find → Icon Catalog. It replaced the
    /// Applications menu: its pages follow installs and removals, and an application can be on
    /// more than one of them.
    /// </summary>
    private const string IconCatalogProgram = "wlrix-icon-catalog";

    /// <summary>The installed name of the Software Manager, launched by System → Manage Software.</summary>
    private const string SoftwareManagerProgram = "wlrix-software-manager";

    // The rest of the desktop's settings panels, by installed name. Desktop → Customize holds the
    // ones about the person's own desktop; System holds the ones about the machine, which ask for
    // an administrator's password (Login, User Accounts) or affect every monitor (Display).
    private const string WindowsProgram = "wlrix-settings-windows";
    private const string BackgroundProgram = "wlrix-settings-background";
    private const string SchemesProgram = "wlrix-settings-schemes";
    private const string KeyboardProgram = "wlrix-settings-keyboard";
    private const string InternationalProgram = "wlrix-settings-international";
    private const string MouseProgram = "wlrix-settings-mouse";
    private const string DisplaysProgram = "wlrix-settings-displays";
    private const string NetworkProgram = "wlrix-settings-network";
    private const string PowerProgram = "wlrix-settings-power";
    private const string BluetoothProgram = "wlrix-settings-bluetooth";
    private const string UsersProgram = "wlrix-settings-users";
    private const string LoginProgram = "wlrix-settings-login";

    /// <summary>
    /// The installed name of the Shut Down System dialog, behind both of the System menu's
    /// machine-wide items.
    /// </summary>
    private const string ShutdownProgram = "wlrix-shutdown";

    /// <summary>
    /// What tells the dialog it was reached through Restart System rather than Shut Down
    /// System. It only presets the checkbox; the choice is still the user's until they press
    /// OK, which is the whole reason these two items open a window instead of acting.
    /// </summary>
    private const string RestartFlag = "--restart";

    private readonly IAppLauncher _launcher;
    private readonly ISessionService _session;

    public MainWindowViewModel(IAppLauncher launcher, ISessionService session)
    {
        _launcher = launcher;
        _session = session;
        _launcher.LaunchFailed += OnLaunchFailed;
        BuildTopLevel();
    }

    /// <summary>Design-time constructor, for the previewer and the menu tests.</summary>
    public MainWindowViewModel()
    {
        _launcher = null!;
        _session = null!;
        BuildTopLevel();
    }

    /// <summary>Raised (UI thread) to show the About dialog with the given message.</summary>
    public event Action<string>? ShowAbout;

    /// <summary>Raised (UI thread) to show an error dialog with the given message.</summary>
    public event Action<string>? ShowError;

    /// <summary>
    /// Raised (UI thread) to ask the user to confirm ending the session; the handler answers
    /// whether they did. Nothing is signaled until it says yes — with no handler attached,
    /// Log Out does nothing rather than logging out unasked.
    /// </summary>
    public event Func<Task<bool>>? ConfirmLogOut;

    public ObservableCollection<MenuNode> TopLevel { get; } = [];

    public string Title => Strings.Toolchest;

    /// <summary>A menu item that starts <paramref name="program"/>, named <paramref name="label"/>.</summary>
    private MenuNode Launch(string label, string program) =>
        new(label, command: new RelayCommand(() => _launcher.Run(label, program)));

    private void BuildTopLevel()
    {
        TopLevel.Clear();
        TopLevel.Add(new MenuNode(Strings.Desktop,
        [
            // IRIX's Desktop → Customize, where the desktop's own settings panels live: how
            // windows and sounds behave, then how everything looks, then the mouse and keyboard.
            new MenuNode(Strings.Customize,
            [
                Launch(Strings.Windows, WindowsProgram),
                Launch(Strings.Sounds, SoundProgram),
                MenuNode.Separator(),
                Launch(Strings.Background, BackgroundProgram),
                Launch(Strings.ColorSchemes, SchemesProgram),
                MenuNode.Separator(),
                Launch(Strings.Mouse, MouseProgram),
                Launch(Strings.Keyboard, KeyboardProgram),
                Launch(Strings.Language, InternationalProgram),
            ]),
            new MenuNode(Strings.ExtraDesks,
                command: new RelayCommand(() => _launcher.Run(Strings.ExtraDesks, DesksProgram))),
            new MenuNode(Strings.ControlAudio,
                command: new RelayCommand(() => _launcher.Run(Strings.ControlAudio, AudioProgram))),
            new MenuNode(Strings.ClipboardHistory,
                command: new RelayCommand(() => _launcher.Run(Strings.ClipboardHistory, ClipboardHistoryProgram))),
            MenuNode.Separator(),
            new MenuNode(Strings.OpenTerminal, command: new RelayCommand(() => _launcher.OpenTerminal())),
            MenuNode.Separator(),
            new MenuNode(Strings.LogOut, command: new RelayCommand(() => _ = LogOutAsync())),
        ]));
        // IRIX's Find menu, where the Icon Catalog lived. The catalog is what the Applications
        // menu used to be, and more: a page per category, control panels, the user's own.
        TopLevel.Add(new MenuNode(Strings.Find, [Launch(Strings.IconCatalog, IconCatalogProgram)]));
        TopLevel.Add(new MenuNode(Strings.System,
        [
            Launch(Strings.ManageSoftware, SoftwareManagerProgram),
            MenuNode.Separator(),
            // The machine's own settings, as IRIX's System menu had them.
            Launch(Strings.DisplayProperties, DisplaysProgram),
            Launch(Strings.NetworkProperties, NetworkProgram),
            Launch(Strings.BluetoothProperties, BluetoothProgram),
            Launch(Strings.PowerProperties, PowerProgram),
            Launch(Strings.UserAccounts, UsersProgram),
            Launch(Strings.LoginProperties, LoginProgram),
            MenuNode.Separator(),
            // Neither acts: both open the Shut Down System dialog, which is where the machine
            // actually gets asked to go down. That indirection is the point -- a menu item that
            // powered the box off on a mis-click would be unrecoverable in a way no other item
            // here is -- and it is why these two stopped being grayed out.
            new MenuNode(Strings.RestartSystem,
                command: new RelayCommand(() =>
                    _launcher.Run(Strings.RestartSystem, ShutdownProgram, RestartFlag))),
            new MenuNode(Strings.ShutDownSystem,
                command: new RelayCommand(() => _launcher.Run(Strings.ShutDownSystem, ShutdownProgram))),
        ]));
        TopLevel.Add(new MenuNode(Strings.Help,
            [new MenuNode(Strings.AboutToolchest, command: new RelayCommand(RaiseAbout))]));
    }

    /// <summary>
    /// Ends the session, once the user has confirmed it. Losing every open application to a
    /// mis-click on a menu is not recoverable, so this one asks first — which the Toolchest can
    /// do and the desktop's own Log Out cannot, having nowhere to put a dialog.
    /// </summary>
    private async Task LogOutAsync()
    {
        if (ConfirmLogOut is not { } confirm || !await confirm())
            return;

        if (_session.LogOut() is { } why)
            ShowError?.Invoke(why);
    }

    private void RaiseAbout()
    {
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        ShowAbout?.Invoke(Strings.AboutMessage(version));
    }

    private void OnLaunchFailed(string message) => Dispatcher.UIThread.Post(() => ShowError?.Invoke(message));
}
