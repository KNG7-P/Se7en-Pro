using System.Windows;
using System.Windows.Controls;
using Se7enPro.ViewModels;

namespace Se7enPro.Views;

public partial class SplitTunnelPage : UserControl
{
    public SplitTunnelPage()
    {
        InitializeComponent();
    }

    
    
    
    private void OnSplitTunnelTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { CommandParameter: string tab }) return;
        if (DataContext is not SplitTunnelViewModel vm) return;

        var target = tab == "Apps" ? 1 : 0;
        if (vm.ActiveTab == target) return;
        vm.SetTab(target);
    }
}
