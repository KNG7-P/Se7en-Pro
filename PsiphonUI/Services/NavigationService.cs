using System;
using Microsoft.Extensions.DependencyInjection;
using PsiphonUI.ViewModels;

namespace PsiphonUI.Services;

public sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _provider;

    public NavigationService(IServiceProvider provider) => _provider = provider;

    public PageViewModelBase? Current { get; private set; }
    public event EventHandler<PageViewModelBase>? Navigated;

    public void NavigateTo(string route)
    {
        PageViewModelBase vm = route switch
        {
            "home" => _provider.GetRequiredService<HomeViewModel>(),
            "settings" => _provider.GetRequiredService<SettingsViewModel>(),
            "ipscanner" => _provider.GetRequiredService<IpScannerViewModel>(),
            "logs" => _provider.GetRequiredService<LogsViewModel>(),
            "about" => _provider.GetRequiredService<AboutViewModel>(),
            _ => _provider.GetRequiredService<HomeViewModel>(),
        };

        Current = vm;
        Navigated?.Invoke(this, vm);
    }
}
