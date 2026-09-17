using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wlrix.Avalonia.Controls;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Files.Core.Operations;
using Wlrix.Files.Core.State;
using Wlrix.Files.Localization;
using Wlrix.Files.ViewModels;

namespace Wlrix.Files.Views;

/// <summary>The file manager window.</summary>
/// <remarks>
/// The window owns every Avalonia dialog type; the view model raises events saying what it
/// wants shown. That is the contract Wlrix.Archiver established, and what lets the view
/// models be tested with no dispatcher.
/// </remarks>
public partial class MainWindow : Window
{
    private MainWindowViewModel? _model;

    public MainWindow() => InitializeComponent();

    private void OnSortName(object? sender, RoutedEventArgs e) => SetSort(SortKey.Name);
    private void OnSortSize(object? sender, RoutedEventArgs e) => SetSort(SortKey.Size);
    private void OnSortKind(object? sender, RoutedEventArgs e) => SetSort(SortKey.Kind);
    private void OnSortDate(object? sender, RoutedEventArgs e) => SetSort(SortKey.Modified);

    private void OnClassicMode(object? sender, RoutedEventArgs e) => SetMode(NavigationMode.Classic);
    private void OnModernMode(object? sender, RoutedEventArgs e) => SetMode(NavigationMode.Modern);

    private void SetMode(NavigationMode mode)
    {
        if (DataContext is MainWindowViewModel model)
            _ = model.SetNavigationModeAsync(mode);
    }

    private void OnViewIcons(object? sender, RoutedEventArgs e) => SetView(ViewMode.Icons);
    private void OnViewDetails(object? sender, RoutedEventArgs e) => SetView(ViewMode.Details);

    private void SetView(ViewMode mode)
    {
        if (DataContext is MainWindowViewModel model)
            model.Pane.ViewMode = mode;
    }

    private void SetSort(SortKey key)
    {
        if (DataContext is MainWindowViewModel model)
            model.Pane.SortKey = key;
    }

    private void OnPathSelected(object? sender, PathSelectedEventArgs e)
    {
        if (DataContext is MainWindowViewModel model)
            _ = model.NavigateToPathAsync(e.Path);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not MainWindowViewModel model)
            return;

        _model = model;
        model.ErrorRaised += OnError;
        model.StatusRaised += OnStatus;
        model.AboutRequested += OnAbout;
        model.CloseRequested += Close;
        model.PromptRequested += OnPrompt;
        model.ConfirmRequested += OnConfirm;
        model.ConflictRequested += OnConflict;
        model.ConnectRequested += OnConnectRequested;
        model.PropertiesRequested += OnPropertiesRequested;
        SavedShares.Click += OnSavedShareClicked;
        // On the parent only. A submenu's own SubmenuOpened cannot fill the list that decides
        // whether that submenu may be opened at all -- and since the event bubbles, hooking
        // the children as well would refresh a second time and rebuild the items while the
        // user is already looking at them.
        SelectedMenu.SubmenuOpened += OnOpenWithOpening;
        OpenWithMenu.Click += OnOpenWithClicked;
        AlwaysOpenWithMenu.Click += OnAlwaysOpenWithClicked;
        _ = model.StartAsync();
    }

    /// <summary>
    /// Suspends polling while the window is not being looked at.
    /// </summary>
    /// <remarks>
    /// Only affects a remote directory, which is re-read on a timer; a local one is
    /// event-driven and costs nothing while idle. A file manager left open on a share for a
    /// week should not spend the week talking to the server.
    /// </remarks>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty && DataContext is MainWindowViewModel model)
            model.Suspended = !IsActive;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_model is { } model)
        {
            model.ErrorRaised -= OnError;
        model.StatusRaised -= OnStatus;
            model.AboutRequested -= OnAbout;
            model.CloseRequested -= Close;
            model.PromptRequested -= OnPrompt;
            model.ConfirmRequested -= OnConfirm;
            model.ConflictRequested -= OnConflict;
            model.ConnectRequested -= OnConnectRequested;
        model.PropertiesRequested -= OnPropertiesRequested;
            SavedShares.Click -= OnSavedShareClicked;
            SelectedMenu.SubmenuOpened -= OnOpenWithOpening;
            OpenWithMenu.Click -= OnOpenWithClicked;
            AlwaysOpenWithMenu.Click -= OnAlwaysOpenWithClicked;
            _model = null;
        }
        base.OnClosed(e);
    }

    private void OnError(string message) =>
        _ = MessageDialog.ShowAsync(this, DialogType.Error, message, title: Strings.ErrorTitle);

    private void OnCancelOperation(object? sender, RoutedEventArgs e) =>
        (DataContext as MainWindowViewModel)?.CancelOperation();

    private Task<string?> OnPrompt(string title, string prompt, string initial, bool selectExtension) =>
        PromptDialog.ShowAsync(this, title, prompt, initial, selectExtension);

    private async Task<bool> OnConfirm(string title, string message) =>
        await MessageDialog.ShowAsync(this, DialogType.Question, message,
            buttons: DialogButtons.OkCancel, title: title) == DialogResult.Ok;

    private Task<ConflictDecision> OnConflict(ConflictContext context) =>
        ConflictDialog.ShowAsync(this, context);

    /// <summary>Something worked and is worth saying so — an unmount, so far.</summary>
    /// <remarks>
    /// An information dialog rather than the status bar, because unmounting is the one action
    /// here whose whole result is invisible: the row simply stops saying where the disk is, and
    /// somebody about to pull the stick out wants to be told it is safe rather than left to
    /// infer it.
    /// </remarks>
    private void OnStatus(string message) =>
        _ = MessageDialog.ShowAsync(this, DialogType.Information, message, title: Strings.Files);

    private void OnPropertiesRequested(PropertiesViewModel model) => PropertiesDialog.Show(this, model);

    private Task<ConnectRequest?> OnConnectRequested() =>
        ConnectDialog.ShowAsync(this, Strings.ConnectTitle, _canRemember);

    /// <summary>
    /// Whether a saved password would outlive the application.
    /// </summary>
    /// <remarks>
    /// Set by the application from the credential store it actually built. The dialog says so
    /// plainly when it would not, which on wlRIX today is always — nothing in the session
    /// starts a keyring and there is no prompter to unlock one.
    /// </remarks>
    public bool CanRememberPasswords
    {
        get => _canRemember;
        set => _canRemember = value;
    }

    private bool _canRemember;

    /// <summary>Fills the Open With list just before it is shown.</summary>
    private void OnOpenWithOpening(object? sender, RoutedEventArgs e) =>
        // Not awaited: the submenu is opening now and the list arrives a moment later, which
        // for a local file is the same frame. Awaiting would mean holding the menu closed
        // while a share answers.
        _ = (DataContext as MainWindowViewModel)?.RefreshHandlersAsync();

    /// <summary>An application picked from Open With: run it, and change nothing.</summary>
    private void OnOpenWithClicked(object? sender, RoutedEventArgs e) => Picked(e, makeDefault: false);

    /// <summary>
    /// An application picked from Always Open With: run it, and make it the default.
    /// </summary>
    /// <remarks>
    /// Its own submenu rather than a modifier on the first one. A held key is invisible, and
    /// worse, it cannot be read reliably here: the menu popup takes keyboard focus, so the
    /// window may never see the key event at all.
    /// </remarks>
    private void OnAlwaysOpenWithClicked(object? sender, RoutedEventArgs e) => Picked(e, makeDefault: true);

    private void Picked(RoutedEventArgs e, bool makeDefault)
    {
        if (DataContext is MainWindowViewModel model
            && (e.Source as MenuItem)?.DataContext is HandlerViewModel handler)
        {
            model.OpenWithHandler(handler, makeDefault);
        }
    }

    /// <summary>A saved share picked from the Internet menu.</summary>
    /// <remarks>
    /// Click on the parent rather than a command per item: the items are built from a
    /// collection and a MenuItem generated from one has no command of its own to bind.
    /// </remarks>
    private void OnSavedShareClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel model
            && (e.Source as MenuItem)?.DataContext is PlaceViewModel share)
        {
            _ = model.OpenShareAsync(share);
        }
    }

    private void OnAbout() =>
        _ = MessageDialog.ShowAsync(this, DialogType.Information, Strings.AboutText, title: Strings.AboutTitle);
}
