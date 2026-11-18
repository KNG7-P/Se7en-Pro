using System.Windows;
using System.Windows.Controls;

namespace Se7enPro.Views.Dialogs;

public partial class ConfigModalsHost : UserControl
{
    public ConfigModalsHost()
    {
        InitializeComponent();
    }

    private void AddEditConfigOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) AddEditConfigScroll.ScrollToHome();
    }
}
