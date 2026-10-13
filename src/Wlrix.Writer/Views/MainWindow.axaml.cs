// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using ReactiveUI;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Writer.Localization;
using Wlrix.Writer.Models;
using Wlrix.Writer.ViewModels;

namespace Wlrix.Writer.Views;

public partial class MainWindow : Window
{
    private IReadOnlyList<string> _openOnStartup = [];
    private IDisposable? _splitSubscription;
    private FindDialog? _findDialog;
    private ReplaceDialog? _replaceDialog;

    /// <summary>Set once every unsaved document has been dealt with, so the second close goes through.</summary>
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();

        // Whichever half last had the focus is the one the menus act on. GotFocus bubbles, so a
        // click anywhere in a half, the editor or its tabs, lands here.
        PrimaryHost.AddHandler(GotFocusEvent, (_, _) =>
        {
            if (Model is { } model)
                model.ActiveGroup = model.PrimaryGroup;
        });
        SecondaryHost.AddHandler(GotFocusEvent, (_, _) =>
        {
            if (Model is { SecondaryGroup: { } secondary } model)
                model.ActiveGroup = secondary;
        });

        PrimaryView.CloseTabRequested += OnCloseTabRequested;
        SecondaryView.CloseTabRequested += OnCloseTabRequested;

        FileMenu.SubmenuOpened += (_, _) => Model?.RefreshRecent();
        PreferencesMenu.SubmenuOpened += (_, _) => Model?.RefreshLanguages();
        EditMenu.SubmenuOpened += async (_, _) => await RefreshClipboardAsync();
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    /// <summary>Opens <paramref name="paths"/> once the window is up.</summary>
    /// <remarks>
    /// Deferred rather than done here: errors reach the user through events this window only
    /// subscribes to in <see cref="OnOpened"/>.
    /// </remarks>
    public void OpenOnStartup(IReadOnlyList<string> paths) => _openOnStartup = paths;

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Model is not { } model)
            return;

        model.OpenFilesRequested += OnOpenFilesRequested;
        model.SaveFileRequested += OnSaveFileRequested;
        model.ConfirmSaveRequested += OnConfirmSaveRequested;
        model.ReloadRequested += OnReloadRequested;
        model.DeletedNoticeRequested += OnDeletedNoticeRequested;
        model.TabsDialogRequested += OnTabsDialogRequested;
        model.FindDialogRequested += OnFindDialogRequested;
        model.ReplaceDialogRequested += OnReplaceDialogRequested;
        model.ErrorRaised += OnErrorRaised;
        model.InformationRaised += OnInformationRaised;
        model.AboutRequested += OnAboutRequested;
        model.ExitRequested += Close;
        model.ActivateRequested += Activate;

        _splitSubscription = model.WhenAnyValue(m => m.SecondaryGroup).Subscribe(group => LayOutGroups(group is not null));

        var paths = _openOnStartup;
        _openOnStartup = [];
        foreach (var path in paths)
            await model.OpenPathAsync(path, createIfMissing: true);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || Model is not { HasUnsavedChanges: true } model)
            return;

        // Closing cannot wait for answers, so this close is refused and a second one is made
        // once every document has been saved or let go.
        e.Cancel = true;
        _ = ConfirmCloseAsync(model);
    }

    private async Task ConfirmCloseAsync(MainWindowViewModel model)
    {
        if (!await model.ConfirmCloseAllAsync())
            return;
        _closeConfirmed = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _splitSubscription?.Dispose();
        if (Model is { } model)
        {
            model.OpenFilesRequested -= OnOpenFilesRequested;
            model.SaveFileRequested -= OnSaveFileRequested;
            model.ConfirmSaveRequested -= OnConfirmSaveRequested;
            model.ReloadRequested -= OnReloadRequested;
            model.DeletedNoticeRequested -= OnDeletedNoticeRequested;
            model.TabsDialogRequested -= OnTabsDialogRequested;
        model.FindDialogRequested -= OnFindDialogRequested;
        model.ReplaceDialogRequested -= OnReplaceDialogRequested;
            model.ErrorRaised -= OnErrorRaised;
            model.InformationRaised -= OnInformationRaised;
            model.AboutRequested -= OnAboutRequested;
            model.ExitRequested -= Close;
            model.ActivateRequested -= Activate;
            model.Dispose();
        }

        base.OnClosed(e);
    }

    protected override void OnPropertyChanged(global::Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // A file changed on disk is asked about when the user comes back to the window, not in
        // the middle of whatever they were doing in another one.
        if (change.Property == IsActiveProperty && Model is { } model)
            model.IsWindowActive = IsActive;
    }

    /// <summary>Shows one half or two, and resets the widths a splitter drag left behind.</summary>
    private void LayOutGroups(bool split)
    {
        GroupGrid.ColumnDefinitions = split
            ? new ColumnDefinitions("*,Auto,*")
            : new ColumnDefinitions("*,0,0");
        GroupGrid.Classes.Set("split", split);
        Splitter.IsVisible = split;
        SecondaryHost.IsVisible = split;
    }

    private async void OnCloseTabRequested(EditorTabViewModel tab)
    {
        if (Model is { } model)
            await model.CloseTabAsync(tab);
    }

    private async Task RefreshClipboardAsync()
    {
        if (Model is not { } model)
            return;
        try
        {
            model.ClipboardHasText = Clipboard is { } clipboard
                                     && !string.IsNullOrEmpty(await clipboard.TryGetTextAsync());
        }
        catch (Exception)
        {
            // A clipboard owner that went away mid-read. Paste stays as it was.
        }
    }

    private async Task<IReadOnlyList<string>> OnOpenFilesRequested()
    {
        var folder = Model?.ActiveDocument?.Path is { } current
            ? await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(current)!)
            : null;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.OpenTitle,
            AllowMultiple = true,
            SuggestedStartLocation = folder,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.FilterAllFiles) { Patterns = ["*"] },
                new FilePickerFileType(Strings.FilterText) { MimeTypes = ["text/*"], Patterns = ["*.txt"] },
            ],
        });

        return [.. files.Select(file => file.TryGetLocalPath()).OfType<string>()];
    }

    private async Task<string?> OnSaveFileRequested(string suggestedName, string? folder)
    {
        var start = folder is null ? null : await StorageProvider.TryGetFolderFromPathAsync(folder);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.SaveAsTitle,
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = start,
            ShowOverwritePrompt = true,
        });

        return file?.TryGetLocalPath();
    }

    private Task<SaveChoice> OnConfirmSaveRequested(string name) =>
        ChoiceDialog.ShowAsync(this, Strings.SaveChangesTitle, "Question", Strings.SaveChanges(name), SaveChoice.Cancel,
            new ChoiceDialog.Choice<SaveChoice>(Strings.Save, SaveChoice.Save, IsDefault: true),
            new ChoiceDialog.Choice<SaveChoice>(Strings.DontSave, SaveChoice.Discard),
            new ChoiceDialog.Choice<SaveChoice>(Strings.Cancel, SaveChoice.Cancel, IsCancel: true));

    private Task<bool> OnReloadRequested(string name, bool modified) =>
        ChoiceDialog.ShowAsync(this, Strings.ReloadTitle, modified ? "Warning" : "Question",
            Strings.Reload(name, modified), false,
            // With unsaved changes at stake the safe answer is the default one.
            new ChoiceDialog.Choice<bool>(Strings.ReloadButton, true, IsDefault: !modified),
            new ChoiceDialog.Choice<bool>(Strings.KeepButton, false, IsDefault: modified, IsCancel: true));

    private Task OnDeletedNoticeRequested(string name) =>
        MessageDialog.ShowAsync(this, DialogType.Warning, Strings.Deleted(name),
            buttons: DialogButtons.Ok, title: Strings.DeletedTitle);

    /// <summary>Shows the Find dialog, or brings it forward with its field ready if it is already open.</summary>
    /// <remarks>
    /// Modeless and owned by this window, so it stays above the text and the text can still be
    /// clicked in, and Find then searches whichever editor was used last.
    /// </remarks>
    private void OnFindDialogRequested()
    {
        if (Model is not { } model)
            return;

        if (_findDialog is { } open)
        {
            open.Activate();
            open.FocusField();
            return;
        }

        model.FindOptions.ClearMessage();
        var dialog = new FindDialog { DataContext = model.FindOptions };
        dialog.Closed += (_, _) =>
        {
            _findDialog = null;
            // Back to the text, with the match selected and the caret at it.
            Activate();
            Model?.ActiveTab?.RequestFocus();
        };
        _findDialog = dialog;
        dialog.Show(this);
    }

    /// <summary>Shows the Replace/Find dialog, or brings it forward. Modeless, as Find is.</summary>
    private void OnReplaceDialogRequested()
    {
        if (Model is not { } model)
            return;

        if (_replaceDialog is { } open)
        {
            open.Activate();
            open.FocusField();
            return;
        }

        model.ReplaceOptions.ClearMessage();
        var dialog = new ReplaceDialog { DataContext = model.ReplaceOptions };
        dialog.Closed += (_, _) =>
        {
            _replaceDialog = null;
            Activate();
            Model?.ActiveTab?.RequestFocus();
        };
        _replaceDialog = dialog;
        dialog.Show(this);
    }

    private Task<TabsDialogResult?> OnTabsDialogRequested(TabSettings current) => TabsDialog.ShowAsync(this, current);

    private void OnErrorRaised(string message) => _ = MessageDialog.ShowAsync(this,
        DialogType.Error, message, buttons: DialogButtons.Ok, title: Strings.ErrorTitle);

    private void OnInformationRaised(string message) => _ = MessageDialog.ShowAsync(this,
        DialogType.Information, message, buttons: DialogButtons.Ok, title: Strings.InfoTitle);

    private void OnAboutRequested()
    {
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _ = MessageDialog.ShowAsync(this, DialogType.Information, Strings.About(version),
            buttons: DialogButtons.Ok, title: Strings.AboutTitle);
    }
}
