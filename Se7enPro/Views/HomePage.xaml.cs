using System;
using System.Windows;
using System.Windows.Controls;

namespace Se7enPro.Views;

public partial class HomePage : UserControl
{
    private Window? _hostWindow;

    public HomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_hostWindow is null)
        {
            _hostWindow = Window.GetWindow(this);
            if (_hostWindow is not null)
            {
                _hostWindow.StateChanged += OnHostWindowStateChanged;
            }
        }

        if (_hostWindow is not null)
        {
            ApplyMaximizeLayout(_hostWindow.WindowState == WindowState.Maximized);
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null)
        {
            _hostWindow.StateChanged -= OnHostWindowStateChanged;
            _hostWindow = null;
        }
    }

    private void OnHostWindowStateChanged(object? sender, EventArgs e)
    {
        if (_hostWindow is not null)
        {
            ApplyMaximizeLayout(_hostWindow.WindowState == WindowState.Maximized);
        }
    }

    
    private void ApplyMaximizeLayout(bool maximize)
    {
        var rows = TelemetryGrid.RowDefinitions;
        rows[1].Height = new GridLength(1.0, GridUnitType.Star);
        rows[2].Height = GridLength.Auto;

        TilesGrid.ColumnDefinitions[1].Width = new GridLength(8);
        TilesGrid.RowDefinitions[1].Height = new GridLength(8);

        var radius = new CornerRadius(9);
        var border = new Thickness(1);
        ApplyTileChrome(SocksTile, radius, border);
        ApplyTileChrome(HttpTile, radius, border);
        ApplyTileChrome(EndpointTile, radius, border);
        ApplyTileChrome(CipherTile, radius, border);
    }

    private static void ApplyTileChrome(Border tile, CornerRadius radius, Thickness border)
    {
        tile.CornerRadius = radius;
        tile.BorderThickness = border;
    }
}
