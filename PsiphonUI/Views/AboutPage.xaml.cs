using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using PsiphonUI.ViewModels;

namespace PsiphonUI.Views;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<AboutViewModel>();
    }
}
