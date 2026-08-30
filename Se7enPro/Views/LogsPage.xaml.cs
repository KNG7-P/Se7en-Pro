using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Se7enPro.ViewModels;

namespace Se7enPro.Views;

public partial class LogsPage : UserControl
{
    public LogsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<LogsViewModel>();
    }
}
