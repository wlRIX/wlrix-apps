using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Wlrix.SoftwareManager.ViewModels;

namespace Wlrix.SoftwareManager.Views.Panes;

public partial class AvailableSoftwarePane : UserControl
{
    public AvailableSoftwarePane() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    // Dropping onto the pocket only fills the field -- it does not start a lookup, let alone an
    // installation -- so a mis-drop costs the user nothing but a re-type.
    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        // The first file only: the field holds one path, and silently taking one of several
        // dropped files is clearer than concatenating them into something unparseable.
        if (e.DataTransfer.TryGetFile()?.TryGetLocalPath() is { } path)
            viewModel.SourceText = path;
    }
}
