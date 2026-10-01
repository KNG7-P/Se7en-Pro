using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Se7enPro.Views;

namespace Se7enPro.Services;

public sealed class WpfUiAdapter : IUiAdapter
{
    public bool CheckAccess() =>
        Application.Current?.Dispatcher.CheckAccess() ?? true;

    public void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) action();
        else dispatcher.BeginInvoke(action);
    }

    public void Invoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) action();
        else dispatcher.Invoke(action);
    }

    public void SetClipboard(string text)
    {
        
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    public Task<string?> GetClipboardTextAsync() =>
        Task.FromResult(Clipboard.ContainsText() ? Clipboard.GetText() : null);

    public Task<bool> ConfirmAsync(string title, string message) =>
        Task.FromResult(MessageBox.Show(message, title, MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes);

    public Task<(string Kind, string Value)?> AskRuleAsync()
    {
        var dialog = new AddSiteDialog { Owner = MainWindow };
        (string Kind, string Value)? result =
            dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResultValue)
                ? (dialog.ResultKind, dialog.ResultValue)
                : null;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<string>> PickApplicationsAsync()
    {
        var dialog = new AppPickerDialog { Owner = MainWindow };
        var accepted = dialog.ShowDialog() == true;
        return Task.FromResult<IReadOnlyList<string>>(
            accepted && dialog.SelectedValues.Count > 0 ? dialog.SelectedValues : Array.Empty<string>());
    }

    public Task<bool> ConfirmElevationAsync() => Task.FromResult(ElevationDialog.Confirm(MainWindow));

    public void MinimizeMainWindow()
    {
        if (MainWindow is { } win) win.WindowState = WindowState.Minimized;
    }

    public void ToggleMaximizeMainWindow()
    {
        if (MainWindow is { } win)
        {
            win.WindowState = win.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }

    public void RequestCloseMainWindow()
    {
        
        
        
        (MainWindow as Views.MainWindow)?.RequestClose();
    }

    public void ExitWithoutPrompt()
    {
        if (MainWindow is Views.MainWindow window)
        {
            window.ExitApplication();
            return;
        }

        try
        {
            Application.Current?.Shutdown();
        }
        catch
        {
        }
    }

    public void RequestRelaunchOnExit() => App.RequestRelaunchOnExit();

    public bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 1;
        }
        catch
        {
            return true;
        }
    }

    public object FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }

    public object CreateGroupedOptionsView(System.Collections.IList items, string groupName) =>
        new ListCollectionView(items)
        {
            GroupDescriptions = { new PropertyGroupDescription(groupName) },
        };

    private static Window? MainWindow
    {
        get
        {
            var owner = Application.Current?.MainWindow;
            
            
            return owner is { IsLoaded: true, IsVisible: true } ? owner : null;
        }
    }
}
