using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsiphonUI.Services;

namespace PsiphonUI.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly ISettingsService _settings;
    private readonly IThemeService _themeService;

    public MainViewModel(
        INavigationService navigation,
        ISettingsService settings,
        IThemeService themeService,
        HomeViewModel home,
        SettingsViewModel settingsVm,
        IpScannerViewModel ipScanner,
        LogsViewModel logs,
        AboutViewModel about)
    {
        _navigation = navigation;
        _settings = settings;
        _themeService = themeService;

        Pages = new ObservableCollection<PageViewModelBase> { home, settingsVm, ipScanner, logs, about };

        _navigation.Navigated += (_, vm) =>
        {
            CurrentPage = vm;
            SelectedPage = Pages.FirstOrDefault(p => p.Route == vm.Route);
        };

        _navigation.NavigateTo("home");
    }

    public ObservableCollection<PageViewModelBase> Pages { get; }

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
        var window = Application.Current?.MainWindow;
        if (window is not null) window.WindowState = WindowState.Minimized;
    }

    [RelayCommand]
    private static void MaximizeWindow()
    {
        var window = Application.Current?.MainWindow;
        if (window is null) return;
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    [RelayCommand]
    private static void CloseWindow() => Application.Current?.MainWindow?.Close();

    [RelayCommand]
    private static void OpenTelegramChannel()
    {
        const string url = "https://t.me/King_Network7";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {

        }
    }
}
