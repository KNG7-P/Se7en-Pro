using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

public sealed class NavPageItem : PageViewModelBase
{
    private readonly string _locKey;
    private readonly string _englishTitle;

    public NavPageItem(string locKey, string englishTitle, string route, string icon)
    {
        _locKey = locKey;
        _englishTitle = englishTitle;
        Route = route;
        Icon = icon;
    }

        public override string Title => Loc.T(_locKey, _englishTitle);
    public override string Route { get; }
    public override string Icon { get; }
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly ISettingsService _settings;
    private readonly IThemeService _themeService;

    private readonly NavPageItem _homeNav = new("navDashboard", "Dashboard", "home", "Home");
    private readonly NavPageItem _splitNav = new("navSplitTunnel", "Split Tunnel", "split", "CallSplit");
    private readonly NavPageItem _settingsNav = new("navSettings", "Settings", "settings", "CogOutline");
    private readonly NavPageItem _logsNav = new("navLogs", "Logs", "logs", "TextBoxOutline");
    private readonly NavPageItem _aboutNav = new("navAbout", "About", "about", "InformationOutline");

    public MainViewModel(
        INavigationService navigation,
        ISettingsService settings,
        IThemeService themeService)
    {
        _navigation = navigation;
        _settings = settings;
        _themeService = themeService;

        
        
        Loc.Changed += () =>
        {
            OnPropertyChanged(nameof(PageHeaderTitle));
            OnPropertyChanged(nameof(ClientVersionText));
        };

        _settings.SettingsChanged += (_, _) =>
        {
            
            
            
            
            
            Ui.Post(() => OnPropertyChanged(nameof(IsDarkTheme)));
        };

        _navigation.Navigated += (_, vm) =>
        {
            CurrentPage = vm;
            SelectedPage = Pages.FirstOrDefault(p => p.Route == vm.Route);
            OnPropertyChanged(nameof(PageHeaderTitle));
        };

        Pages.Add(_homeNav);
        Pages.Add(_splitNav);
        Pages.Add(_settingsNav);
        Pages.Add(_logsNav);
        Pages.Add(_aboutNav);

        _navigation.NavigateTo("home");
    }

    public ObservableCollection<PageViewModelBase> Pages { get; } = new();

    [ObservableProperty]
    private PageViewModelBase? _currentPage;

    partial void OnCurrentPageChanged(PageViewModelBase? value)
    {
        OnPropertyChanged(nameof(PageHeaderTitle));
    }

    [ObservableProperty]
    private PageViewModelBase? _selectedPage;

    partial void OnSelectedPageChanged(PageViewModelBase? value)
    {
        if (value is not null && value.Route != (_navigation.Current?.Route ?? ""))
        {
            _navigation.NavigateTo(value.Route);
        }
        OnPropertyChanged(nameof(PageHeaderTitle));
    }

    public string PageHeaderTitle => (SelectedPage?.Route ?? CurrentPage?.Route ?? "home") switch
    {
        "home" => Loc.T("titleDashboard", "VPN Dashboard"),
        "split" => Loc.T("titleSplitTunnel", "Split Tunnel Rules & Routing"),
        "logs" => Loc.T("titleLogs", "Connection Notices & Activity"),
        "settings" => Loc.T("titleSettings", "Advanced Preferences"),
        "about" => Loc.T("titleAbout", "About & Credits"),
        _ => SelectedPage?.Title ?? Loc.T("titleDashboard", "VPN Dashboard")
    };

    public bool IsDarkTheme => _settings.Settings.Theme switch
    {
        "light" => false,
        "system" => Ui.IsSystemDarkTheme(),
        _ => true,
    };

    [RelayCommand]
    private void ToggleTheme()
    {
        var next = IsDarkTheme ? "light" : "dark";
        _settings.Settings.Theme = next;
        _settings.Save();
        _themeService.ApplyTheme(next);
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    [RelayCommand]
    private static void MinimizeWindow() => Ui.MinimizeMainWindow();

    [RelayCommand]
    private static void MaximizeWindow() => Ui.ToggleMaximizeMainWindow();

    [RelayCommand]
    private static void CloseWindow() => Ui.RequestCloseMainWindow();

    public string ClientVersionText =>
        $"{Loc.T("clientVersion", "v1.0.6")} \u2022 {Loc.T("Loc_windowsEdition", "Windows Client")}";

    [RelayCommand]
    private static void OpenTelegramChannel()
    {
        const string url = "https://t.me/King_network7";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }
}
