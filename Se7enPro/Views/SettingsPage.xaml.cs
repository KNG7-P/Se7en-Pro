using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Se7enPro.ViewModels;
using Se7enPro.Views.Settings;

namespace Se7enPro.Views;

public partial class SettingsPage : UserControl
{
    private readonly Dictionary<SettingsTab, FrameworkElement> _tabViewCache = new();

    public SettingsPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                UpdateActiveTabContent();
                vm.PropertyChanged -= OnViewModelPropertyChanged;
                vm.PropertyChanged += OnViewModelPropertyChanged;
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                UpdateActiveTabContent();
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
    }

    private void UpdateActiveTabContent()
    {
        if (DataContext is not SettingsViewModel vm) return;
        var tab = vm.SelectedTab;

        if (!_tabViewCache.TryGetValue(tab, out var content))
        {
            content = tab switch
            {
                SettingsTab.General => new GeneralSettingsView(),
                SettingsTab.Shard => new ShardSettingsView(),
                SettingsTab.Chained => new ChainedSettingsView(),
                SettingsTab.Psiphon => new PsiphonSettingsView(),
                SettingsTab.Network => new NetworkSettingsView(),
                SettingsTab.Aether => new AetherSettingsView(),
                SettingsTab.Tor => new TorSettingsView(),
                SettingsTab.Foxy => new FoxySettingsView(),
                _ => new GeneralSettingsView()
            };
            content.DataContext = vm;
            _tabViewCache[tab] = content;
        }

        if (!ReferenceEquals(TabContentHost.Content, content))
        {
            TabContentHost.Content = content;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SettingsViewModel vm) return;
        if (e.PropertyName == nameof(SettingsViewModel.SelectedTab))
        {
            UpdateActiveTabContent();
        }
        else if (e.PropertyName == nameof(SettingsViewModel.ProxyPassword)
                 && _tabViewCache.TryGetValue(SettingsTab.Psiphon, out var psiphonView)
                 && psiphonView is PsiphonSettingsView psv)
        {
            psv.SyncPasswordBoxFromVm(vm);
        }
        else if (e.PropertyName == nameof(SettingsViewModel.LanProxyPassword)
                 && _tabViewCache.TryGetValue(SettingsTab.Network, out var networkView)
                 && networkView is NetworkSettingsView nsv)
        {
            nsv.SyncLanPasswordBoxFromVm(vm);
        }
    }

    private void OnSettingsTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { CommandParameter: string tab }) return;
        if (DataContext is not SettingsViewModel vm) return;
        if (Enum.TryParse<SettingsTab>(tab, true, out var parsed) && vm.SelectedTab == parsed) return;

        vm.SelectTabCommand.Execute(tab);
    }
}
