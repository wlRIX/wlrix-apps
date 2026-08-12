using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Wlrix.SoftwareManager.ViewModels;

namespace Wlrix.SoftwareManager.Views.Dialogs;

/// <summary>AppImages the user has installed for themselves.</summary>
public partial class UserSoftwareDialog : Window
{
    public UserSoftwareDialog() => InitializeComponent();

    /// <summary>Shows the dialog over <paramref name="owner"/>.</summary>
    public static Task ShowAsync(Window owner, UserSoftwareViewModel viewModel)
    {
        var dialog = new UserSoftwareDialog { DataContext = viewModel };
        return dialog.ShowDialog(owner);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    // Dropping fills the field; installing is still a button press. There is no file chooser to
    // reach for -- wlRIX ships no FileChooser portal -- so this and typing a path are the two
    // ways in.
    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is UserSoftwareViewModel viewModel
            && e.DataTransfer.TryGetFile()?.TryGetLocalPath() is { } path)
            viewModel.Path = path;
    }
}
