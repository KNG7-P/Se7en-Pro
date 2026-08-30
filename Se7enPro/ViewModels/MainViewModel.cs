using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Se7enPro.Services;

namespace Se7enPro.ViewModels;

public sealed class NavPageItem : PageViewModelBase
{
    public NavPageItem(string title, string route, string icon)
    {
        Title = title;
        Route = route;
        Icon = icon;
    }

    public override string Title { get; }
    public override string Route { get; }
    public override string Icon { get; }
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly ISettingsService _settings;
    private readonly IThemeService _themeService;

    private readonly NavPageItem _homeNav = new("Home", "home", "Home");
    private readonly NavPageItem _splitNav = new("Split Tunnel", "split", "CallSplit");
    private readonly NavPageItem _settingsNav = new("Settings", "settings", "CogOutline");
    private readonly NavPageItem _scannerNav = new("IP Scanner", "ipscanner", "Radar");
    private readonly NavPageItem _logsNav = new("Logs", "logs", "TextBoxOutline");
    private readonly NavPageItem _aboutNav = new("About", "about", "InformationOutline");

    private bool? _scannerShown;

    public MainViewModel(
        INavigationService navigation,
        ISettingsService settings,
        IThemeService themeService)
    {
        _navigation = navigation;
        _settings = settings;
        _themeService = themeService;

        _settings.SettingsChanged += (_, _) => RebuildPages();

        _navigation.Navigated += (_, vm) =>
        {
            CurrentPage = vm;
            SelectedPage = Pages.FirstOrDefault(p => p.Route == vm.Route);
        };

        RebuildPages();
        _navigation.NavigateTo("home");
    }

    public ObservableCollection<PageViewModelBase> Pages { get; } = new();

    private void RebuildPages()
    {
        var showScanner = _settings.Settings.IpScannerEnabled;
        if (_scannerShown == showScanner) return;
        _scannerShown = showScanner;

        Pages.Clear();
        Pages.Add(_homeNav);
        Pages.Add(_splitNav);
        Pages.Add(_settingsNav);
        if (showScanner) Pages.Add(_scannerNav);
        Pages.Add(_logsNav);
        Pages.Add(_aboutNav);

        if (!showScanner && _navigation.Current?.Route == "ipscanner")
        {
            _navigation.NavigateTo("home");
        }
    }

    [ObservableProperty]
    private PageViewModelBase? _currentPage;

    [ObservableProperty]
    private PageViewModelBase? _selectedPage;

    partial void OnSelectedPageChanged(PageViewModelBase? value)
    {
        if (value is not null && value.Route != (_navigation.Current?.Route ?? ""))
        {
            _navigation.NavigateTo(value.Route);
        }
    }

    public bool IsDarkTheme => _settings.Settings.Theme != "light";

    [RelayCommand]
    private void ToggleTheme()
    {
        var next = _settings.Settings.Theme == "dark" ? "light" : "dark";
        _settings.Settings.Theme = next;
        _settings.Save();
        _themeService.ApplyTheme(next);
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    [RelayCommand]
    private static void MinimizeWindow()
    {
        if (Application.Current?.MainWindow is { } win)
        {
            win.WindowState = WindowState.Minimized;
        }
    }

    [RelayCommand]
    private static void MaximizeWindow()
    {
        if (Application.Current?.MainWindow is { } win)
        {
            win.WindowState = win.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }

    [RelayCommand]
    private static void CloseWindow() => Application.Current?.MainWindow?.Close();

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
