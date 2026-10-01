using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Se7enPro.ViewModels;

namespace Se7enPro.Views;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<AboutViewModel>();
    }
}
