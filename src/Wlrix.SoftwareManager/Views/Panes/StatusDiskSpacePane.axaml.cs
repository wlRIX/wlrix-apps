using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Wlrix.SoftwareManager.Views.Panes;

public partial class StatusDiskSpacePane : UserControl
{
    public StatusDiskSpacePane() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
