using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Wlrix.Settings.Users.Localization;
using Wlrix.Settings.Users.Services;
using Wlrix.Settings.Users.ViewModels;

namespace Wlrix.Settings.Users.Views;

/// <summary>A stock picture, loaded small for the grid.</summary>
public sealed record StockPicture(string File, Bitmap? Image);

public abstract class PictureDialogBase : DialogShell<PictureChoice?>;

/// <summary>Choose a Picture: a stock one, a file through the picker, or Remove.</summary>
public partial class PictureDialog : PictureDialogBase
{
    public PictureDialog()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ItemsControl>("Stock")!.ItemsSource = Pictures.Stock()
            .Select(file => new StockPicture(file, AccountRow.LoadPicture(file)))
            .Where(picture => picture.Image is not null)
            .ToList();
    }

    private void OnStock(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string file)
            Close(new PictureChoice(file, Remove: false));
    }

    private void OnRemove(object? sender, RoutedEventArgs e) => Close(new PictureChoice(null, Remove: true));

    private async void OnFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Catalog.Get("PictureTitle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.Catalog.Get("FilterImages"))
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"],
                },
            ],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            Close(new PictureChoice(path, Remove: false));
    }
}
