using System.Windows;
using System.Windows.Controls;
using Se7enPro.ViewModels;

namespace Se7enPro.Views.Settings;

public partial class NetworkSettingsView : UserControl
{
    private bool _syncingLanPasswordFromVm;
    private PasswordBox? _lanPasswordBox;

    public NetworkSettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm) SyncLanPasswordBoxFromVm(vm);
        };
    }

    private void OnLanProxyPasswordBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box) return;
        _lanPasswordBox = box;
        box.PasswordChanged -= OnLanProxyPasswordChanged;
        box.PasswordChanged += OnLanProxyPasswordChanged;
        if (DataContext is SettingsViewModel vm) SyncLanPasswordBoxFromVm(vm);
    }

    private void OnLanProxyPasswordChanged(object? sender, RoutedEventArgs e)
    {
        if (_syncingLanPasswordFromVm) return;
        if (DataContext is SettingsViewModel vm && sender is PasswordBox box)
        {
            vm.LanProxyPassword = box.Password;
        }
    }

    internal void SyncLanPasswordBoxFromVm(SettingsViewModel vm)
    {
        if (_lanPasswordBox is null) return;
        _syncingLanPasswordFromVm = true;
        try
        {
            if (_lanPasswordBox.Password != vm.LanProxyPassword)
            {
                _lanPasswordBox.Password = vm.LanProxyPassword ?? string.Empty;
            }
        }
        finally
        {
            _syncingLanPasswordFromVm = false;
        }
    }
}
