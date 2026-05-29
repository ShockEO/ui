using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ShockUI.Views;

public partial class PortSettingsView : UserControl
{
    public PortSettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}