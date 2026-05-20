using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using PsiphonUI.ViewModels;

namespace PsiphonUI.Views;

public partial class LogsPage : UserControl
{
    public LogsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<LogsViewModel>();
    }
}
