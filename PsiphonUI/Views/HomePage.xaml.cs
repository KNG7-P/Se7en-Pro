using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using PsiphonUI.ViewModels;

namespace PsiphonUI.Views;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<HomeViewModel>();
    }
}
