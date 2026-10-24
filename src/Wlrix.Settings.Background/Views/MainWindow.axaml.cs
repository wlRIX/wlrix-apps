using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Settings.Background.Localization;
using Wlrix.Settings.Background.Services;
using Wlrix.Settings.Background.ViewModels;

namespace Wlrix.Settings.Background.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PictureList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        PictureList.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private BackgroundViewModel? Model => DataContext as BackgroundViewModel;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Model?.Dispose();
    }

    /// <summary>OK keeps everything, which has already been applied, and closes.</summary>
    private void OnOk(object? sender, RoutedEventArgs e) => Close();

    private void OnReset(object? sender, RoutedEventArgs e) => _ = Model?.ResetAsync();

    /// <summary>
    /// Cancel and the Application menu's Exit, which are the same thing: put back whatever this
    /// window changed, then close.
    /// </summary>
    private async void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model)
            await model.RevertAsync();
        Close();
    }

    private void OnHelp(object? sender, RoutedEventArgs e) =>
        _ = MessageDialog.ShowAsync(this, DialogType.Information, Strings.HelpText,
            title: Strings.HelpTitle, buttons: DialogButtons.Ok);

    private async void OnColor(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;
        if (await new ColorDialog(model.Color).ShowDialog<Color?>(this) is { } color)
            await model.SetColorAsync(color);
    }

    private async void OnAdd(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.AddTitle,
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.FilterImages) { Patterns = Wallpapers.Patterns },
            ],
        });
        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            Model?.Add(paths);
    }

    private void OnRemove(object? sender, RoutedEventArgs e) => _ = Model?.RemoveSelectedAsync();

    // --- drops ------------------------------------------------------------

    private static IReadOnlyList<string> PathsOf(DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { } files)
            return [];
        return [.. files.Select(file => file.TryGetLocalPath()).OfType<string>()];
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Copy is also what happens: the picture is copied into the wallpaper folder and the
        // original left where it was. Wayland drags know only copy, move and ask in any case.
        e.DragEffects = PathsOf(e).Any(Wallpapers.IsSupported)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = PathsOf(e);
        e.Handled = true;
        if (paths.Count == 0 || Model is not { } model)
            return;

        // Posted: the compositor wants the drop handler back before anything slow happens, and
        // copying a large photo is slow.
        Dispatcher.UIThread.Post(() => model.Add(paths));
    }
}
