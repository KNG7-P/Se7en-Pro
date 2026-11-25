using System.Windows.Controls;
using System.Windows.Input;
using Se7enPro.ViewModels;

namespace Se7enPro.Views;

public partial class SplitTunnelPage : UserControl
{
    public SplitTunnelPage()
    {
        InitializeComponent();
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SplitTunnelViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            vm.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnNewProfileKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not SplitTunnelViewModel vm) return;

        vm.CreateProfileCommand.Execute(null);
        e.Handled = true;
    }

    private void OnCustomRuleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not SplitTunnelViewModel vm) return;

        vm.AddCustomRuleCommand.Execute(null);
        e.Handled = true;
    }
}
