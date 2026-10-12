// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Wlrix.Avalonia.Dialogs;
using Wlrix.ImageView.Localization;
using Wlrix.ImageView.Models;
using Wlrix.ImageView.ViewModels;

namespace Wlrix.ImageView.Views;

public partial class MainWindow : Window
{
    /// <summary>The palette's dialogs that are open, so asking again brings one forward instead of opening another.</summary>
    private readonly Dictionary<ToolWindow, Window> _toolWindows = [];

    private string? _openOnStartup;

    /// <summary>Set once the user has agreed to close with unsaved work, so the second close goes through.</summary>
    private bool _closeConfirmed;

    public MainWindow()
    {
        // The generated InitializeComponent loads the XAML and assigns the x:Name fields.
        InitializeComponent();

        Canvas.TransformCommitted += (_, _) => (DataContext as MainWindowViewModel)?.CommitTransform();
        Canvas.PropertyChanged += (_, change) =>
        {
            if (change.Property == BoundsProperty && DataContext is MainWindowViewModel model)
                model.ViewportSize = Canvas.Bounds.Size;
        };
    }

    /// <summary>Opens <paramref name="path"/> once the window is up.</summary>
    /// <remarks>
    /// Deferred rather than done here: errors reach the user through an event this window only
    /// subscribes to in <see cref="OnOpened"/>, and the fitted zoom needs the well's real size.
    /// </remarks>
    public void OpenOnStartup(string path) => _openOnStartup = path;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not MainWindowViewModel model)
            return;

        model.OpenFileRequested += OnOpenFileRequested;
        model.SaveFileRequested += OnSaveFileRequested;
        model.ConfirmRequested += OnConfirmRequested;
        model.ErrorRaised += OnErrorRaised;
        model.AboutRequested += OnAboutRequested;
        model.ToolWindowRequested += OnToolWindowRequested;
        model.ExitRequested += Close;
        model.ViewportSize = Canvas.Bounds.Size;

        if (_openOnStartup is { } path)
        {
            _openOnStartup = null;
            _ = model.OpenAsync(path);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || DataContext is not MainWindowViewModel { IsModified: true } model)
            return;

        // Closing cannot wait for an answer, so this close is refused and a second one is made
        // once the user has said yes.
        e.Cancel = true;
        _ = ConfirmCloseAsync(model);
    }

    private async Task ConfirmCloseAsync(MainWindowViewModel model)
    {
        if (!await model.ConfirmDiscardAsync())
            return;
        _closeConfirmed = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainWindowViewModel model)
        {
            model.OpenFileRequested -= OnOpenFileRequested;
            model.SaveFileRequested -= OnSaveFileRequested;
            model.ConfirmRequested -= OnConfirmRequested;
            model.ErrorRaised -= OnErrorRaised;
            model.AboutRequested -= OnAboutRequested;
            model.ToolWindowRequested -= OnToolWindowRequested;
            model.ExitRequested -= Close;
            model.Dispose();
        }

        base.OnClosed(e);
    }

    private async Task<string?> OnOpenFileRequested()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.OpenTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.FilterImages) { Patterns = [.. ImageFormats.ReadablePatterns] },
                new FilePickerFileType(Strings.FilterAllFiles) { Patterns = ["*"] },
            ],
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    /// <summary>Asks where to save. The type list decides the format, through the extension it appends.</summary>
    private async Task<string?> OnSaveFileRequested(string suggestedName, ImageFormat offered)
    {
        // The offered format first, so Save As on a JPEG suggests a JPEG.
        var formats = ImageFormats.Writable.OrderBy(format => format == offered ? 0 : 1);
        var folder = DataContext is MainWindowViewModel { Document: { } document }
            ? await StorageProvider.TryGetFolderFromPathAsync(document.Folder)
            : null;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.SaveAsTitle,
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = folder,
            DefaultExtension = ImageFormats.DefaultExtension(offered),
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                .. formats.Select(format => new FilePickerFileType(ImageFormats.DisplayName(format))
                {
                    Patterns = [.. ImageFormats.Patterns(format)],
                }),
            ],
        });

        return file?.TryGetLocalPath();
    }

    private async Task<bool> OnConfirmRequested(string message)
    {
        var answer = await MessageDialog.ShowAsync(this, DialogType.Question, message,
            buttons: DialogButtons.OkCancel, title: Strings.ConfirmTitle);
        return answer == DialogResult.Ok;
    }

    private void OnErrorRaised(string message) => _ = MessageDialog.ShowAsync(this,
        DialogType.Error, message, buttons: DialogButtons.Ok, title: Strings.ErrorTitle);

    private void OnAboutRequested()
    {
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _ = MessageDialog.ShowAsync(this, DialogType.Information, Strings.About(version),
            buttons: DialogButtons.Ok, title: Strings.AboutTitle);
    }

    /// <summary>Opens one of the palette's dialogs, or brings it forward if it is already open.</summary>
    /// <remarks>
    /// Owned by this window, so they stay above it and close with it, and modeless, so the
    /// image can still be dragged while they are up. They share this window's view model and
    /// follow whatever image is open.
    /// </remarks>
    private void OnToolWindowRequested(ToolWindow kind)
    {
        if (_toolWindows.TryGetValue(kind, out var open))
        {
            open.Activate();
            return;
        }

        Window window = kind switch
        {
            ToolWindow.Histogram => new HistogramWindow(),
            ToolWindow.Info => new InfoWindow(),
            ToolWindow.Detail => new PlaceholderWindow { Title = Strings.Catalog.Get("DetailTitle") },
            _ => new PlaceholderWindow { Title = Strings.Catalog.Get("PropertiesTitle") },
        };
        window.DataContext = DataContext;
        // The accelerators work from the dialogs too. Otherwise Ctrl+H with Info in front
        // does nothing, and the user has to click back into the main window first.
        foreach (var binding in KeyBindings)
            window.KeyBindings.Add(new KeyBinding { Gesture = binding.Gesture, Command = binding.Command });
        window.Closed += (_, _) => _toolWindows.Remove(kind);
        _toolWindows[kind] = window;
        window.Show(this);
    }
}
