using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Wlrix.SoftwareManager.Views.Panes;

public partial class InventoryPane : UserControl
{
    public InventoryPane() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
