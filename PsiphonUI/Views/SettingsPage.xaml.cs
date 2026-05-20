using System.ComponentModel;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using PsiphonUI.ViewModels;

namespace PsiphonUI.Views;

public partial class SettingsPage : UserControl
{
    private bool _syncingPasswordFromVm;

    public SettingsPage()
    {
        InitializeComponent();
        var vm = App.Services.GetRequiredService<SettingsViewModel>();
        DataContext = vm;

        Loaded += (_, _) =>
        {
            SyncPasswordBoxFromVm(vm);
            vm.PropertyChanged += OnViewModelPropertyChanged;
        };
        Unloaded += (_, _) => vm.PropertyChanged -= OnViewModelPropertyChanged;

        ProxyPasswordBox.PasswordChanged += (_, _) =>
        {
            if (_syncingPasswordFromVm) return;
            vm.ProxyPassword = ProxyPasswordBox.Password ?? "";
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.ProxyPassword)) return;
        if (DataContext is SettingsViewModel vm) SyncPasswordBoxFromVm(vm);
    }

    private void SyncPasswordBoxFromVm(SettingsViewModel vm)
    {
        var newValue = vm.ProxyPassword ?? "";
        if (ProxyPasswordBox.Password == newValue) return;
        _syncingPasswordFromVm = true;
        try { ProxyPasswordBox.Password = newValue; }
        finally { _syncingPasswordFromVm = false; }
    }
}
