using System.ComponentModel;
using System.Windows.Controls;
using Se7enPro.ViewModels;

namespace Se7enPro.Views;

public partial class SettingsPage : UserControl
{
    private bool _syncingPasswordFromVm;

    public SettingsPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                SyncPasswordBoxFromVm(vm);
                vm.PropertyChanged -= OnViewModelPropertyChanged;
                vm.PropertyChanged += OnViewModelPropertyChanged;
            }
        };
        Unloaded += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.PropertyChanged -= OnViewModelPropertyChanged;
            }
        };

        ProxyPasswordBox.PasswordChanged += (_, _) =>
        {
            if (_syncingPasswordFromVm) return;
            if (DataContext is SettingsViewModel vm)
            {
                vm.ProxyPassword = ProxyPasswordBox.Password;
            }
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ProxyPassword) && sender is SettingsViewModel vm)
        {
            SyncPasswordBoxFromVm(vm);
        }
    }

    private void SyncPasswordBoxFromVm(SettingsViewModel vm)
    {
        var target = vm.ProxyPassword ?? "";
        if (ProxyPasswordBox.Password != target)
        {
            _syncingPasswordFromVm = true;
            try { ProxyPasswordBox.Password = target; }
            finally { _syncingPasswordFromVm = false; }
        }
    }
}
