using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Se7enPro.ViewModels;

namespace Se7enPro.Views.Settings;

public partial class FoxySettingsView : UserControl
{
    private bool _syncingPasswordFromVm;
    private PasswordBox? _proxyPasswordBox;
    private FoxySettingsViewModel? _subscribedFoxy;

    public FoxySettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => HookAndSync();
        DataContextChanged += (_, _) => HookAndSync();
        Unloaded += (_, _) => Unhook();
    }

    private void OnFoxyProxyPasswordBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box) return;
        _proxyPasswordBox = box;
        box.PasswordChanged -= OnFoxyProxyPasswordChanged;
        box.PasswordChanged += OnFoxyProxyPasswordChanged;
        if (DataContext is SettingsViewModel vm) SyncPasswordBoxFromVm(vm.Foxy);
    }

    private void OnFoxyProxyPasswordChanged(object? sender, RoutedEventArgs e)
    {
        if (_syncingPasswordFromVm) return;
        if (DataContext is SettingsViewModel vm && sender is PasswordBox box)
        {
            vm.Foxy.CustomProxyPassword = box.Password;
        }
    }

    private void HookAndSync()
    {
        var foxy = (DataContext as SettingsViewModel)?.Foxy;
        if (!ReferenceEquals(_subscribedFoxy, foxy))
        {
            Unhook();
            _subscribedFoxy = foxy;
            if (_subscribedFoxy is not null)
            {
                _subscribedFoxy.PropertyChanged += OnFoxyPropertyChanged;
            }
        }
        if (foxy is not null) SyncPasswordBoxFromVm(foxy);
    }

    private void Unhook()
    {
        if (_subscribedFoxy is not null)
        {
            _subscribedFoxy.PropertyChanged -= OnFoxyPropertyChanged;
            _subscribedFoxy = null;
        }
    }

    private void OnFoxyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FoxySettingsViewModel.CustomProxyPassword) &&
            sender is FoxySettingsViewModel foxy)
        {
            SyncPasswordBoxFromVm(foxy);
        }
    }

    private void SyncPasswordBoxFromVm(FoxySettingsViewModel foxy)
    {
        if (_proxyPasswordBox is null) return;
        _syncingPasswordFromVm = true;
        try
        {
            var expected = foxy.CustomProxyPassword ?? string.Empty;
            if (_proxyPasswordBox.Password != expected)
            {
                _proxyPasswordBox.Password = expected;
            }
        }
        finally
        {
            _syncingPasswordFromVm = false;
        }
    }
}
