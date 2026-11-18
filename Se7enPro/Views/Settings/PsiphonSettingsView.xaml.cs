using System.Windows;
using System.Windows.Controls;
using Se7enPro.ViewModels;

namespace Se7enPro.Views.Settings;

public partial class PsiphonSettingsView : UserControl
{
    private bool _syncingPasswordFromVm;
    private PasswordBox? _proxyPasswordBox;

    public PsiphonSettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm) SyncPasswordBoxFromVm(vm);
        };
    }

    private void OnProxyPasswordBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box) return;
        _proxyPasswordBox = box;
        box.PasswordChanged -= OnProxyPasswordChanged;
        box.PasswordChanged += OnProxyPasswordChanged;
        if (DataContext is SettingsViewModel vm) SyncPasswordBoxFromVm(vm);
    }

    private void OnProxyPasswordChanged(object? sender, RoutedEventArgs e)
    {
        if (_syncingPasswordFromVm) return;
        if (DataContext is SettingsViewModel vm && sender is PasswordBox box)
        {
            vm.ProxyPassword = box.Password;
        }
    }

    internal void SyncPasswordBoxFromVm(SettingsViewModel vm)
    {
        if (_proxyPasswordBox is null) return;
        _syncingPasswordFromVm = true;
        try
        {
            if (_proxyPasswordBox.Password != vm.ProxyPassword)
            {
                _proxyPasswordBox.Password = vm.ProxyPassword ?? string.Empty;
            }
        }
        finally
        {
            _syncingPasswordFromVm = false;
        }
    }
}
