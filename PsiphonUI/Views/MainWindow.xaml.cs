using System;
using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PsiphonUI.Services;
using PsiphonUI.ViewModels;

namespace PsiphonUI.Views;

public partial class MainWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly ITrayIconService _tray;
    private bool _forceExit;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();

        _settings = App.Services.GetRequiredService<ISettingsService>();
        _tray = App.Services.GetRequiredService<ITrayIconService>();

        _tray.Initialize();
        _tray.RequestShow += OnTrayRequestShow;
        _tray.RequestExit += OnTrayRequestExit;

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
        };

        Closing += OnMainWindowClosing;
    }

    private void OnTrayRequestShow(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() => _tray.ShowWindow());
    }

    private void OnTrayRequestExit(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(ExitApplication);
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_forceExit) return;

        var action = (_settings.Settings.OnCloseAction ?? "ask").ToLowerInvariant();

        switch (action)
        {
            case "exit":
                return;

            case "minimize":
                e.Cancel = true;
                _tray.HideToTray();
                return;

            default:
                e.Cancel = true;
                HandleAskAction();
                return;
        }
    }

    private void HandleAskAction()
    {
        var dialog = new CloseConfirmationDialog
        {
            Owner = this,
        };

        var ok = dialog.ShowDialog() == true;
        if (!ok || dialog.Result == CloseAction.Cancel)
        {
            return;
        }

        if (dialog.RememberChoice)
        {
            _settings.Settings.OnCloseAction = dialog.Result == CloseAction.Minimize ? "minimize" : "exit";
            _settings.Save();
        }

        if (dialog.Result == CloseAction.Minimize)
        {
            _tray.HideToTray();
        }
        else
        {
            ExitApplication();
        }
    }

    private void ExitApplication()
    {
        _forceExit = true;
        Application.Current?.Shutdown();
    }
}
