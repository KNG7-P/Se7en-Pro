using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using Se7enPro.Models;
using Application = System.Windows.Application;

namespace Se7enPro.Services;

public sealed class TrayIconService : ITrayIconService
{
    private NotifyIcon? _notifyIcon;
    private ToolStripMenuItem? _toggleConnectionItem;
    private bool _disposed;

    private Icon? _iconDisconnected;
    private Icon? _iconConnecting;
    private Icon? _iconConnected;
    private Icon? _iconDefault;

    private ConnectionState _state = ConnectionState.Disconnected;

    public event EventHandler? RequestShow;
    public event EventHandler? RequestExit;
    public event EventHandler? RequestToggleConnection;

    public bool IsHidden { get; private set; }

    public void Initialize()
    {
        if (_notifyIcon is not null) return;

        _iconDefault = ResolveAppIcon();
        _iconDisconnected = LoadEmbeddedIcon("tray-disconnected.ico") ?? _iconDefault;
        _iconConnecting = LoadEmbeddedIcon("tray-connecting.ico") ?? _iconDefault;
        _iconConnected = LoadEmbeddedIcon("tray-connected.ico") ?? _iconDefault;

        _notifyIcon = new NotifyIcon
        {
            Icon = ResolveIconForState(_state) ?? SystemIcons.Application,
            Text = BuildTooltip(_state),
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Show Se7en Pro");
        showItem.Click += (_, _) => RequestShow?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(showItem);

        _toggleConnectionItem = new ToolStripMenuItem(BuildToggleConnectionLabel(_state));
        _toggleConnectionItem.Click += (_, _) => RequestToggleConnection?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_toggleConnectionItem);

        menu.Items.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => RequestExit?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                RequestShow?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public void UpdateConnectionState(ConnectionState state)
    {
        _state = state;
        if (_notifyIcon is null) return;

        var newIcon = ResolveIconForState(state);
        if (newIcon is not null)
        {
            _notifyIcon.Icon = newIcon;
        }

        _notifyIcon.Text = BuildTooltip(state);

        if (_toggleConnectionItem is not null)
        {
            _toggleConnectionItem.Text = BuildToggleConnectionLabel(state);
            _toggleConnectionItem.Enabled =
                state is ConnectionState.Connected
                or ConnectionState.Connecting
                or ConnectionState.Disconnected
                or ConnectionState.Error;
        }
    }

    public void ShowWindow()
    {
        var window = Application.Current?.MainWindow;
        if (window is null) return;

        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.ShowInTaskbar = true;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
        IsHidden = false;
    }

    public void HideToTray()
    {
        var window = Application.Current?.MainWindow;
        if (window is null) return;

        window.Hide();
        window.ShowInTaskbar = false;
        IsHidden = true;

        try
        {
            _notifyIcon?.ShowBalloonTip(
                2000,
                "Se7en Pro",
                "Still running in the system tray. Double-click the icon to restore.",
                ToolTipIcon.Info);
        }
        catch
        {
        }
    }

    private Icon? ResolveIconForState(ConnectionState state) => state switch
    {
        ConnectionState.Connected => _iconConnected ?? _iconDefault,
        ConnectionState.Connecting or ConnectionState.Disconnecting => _iconConnecting ?? _iconDefault,
        ConnectionState.Disconnected or ConnectionState.Error => _iconDisconnected ?? _iconDefault,
        _ => _iconDefault,
    };

    private static string BuildTooltip(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "Se7en Pro — Connected",
        ConnectionState.Connecting => "Se7en Pro — Connecting…",
        ConnectionState.Disconnecting => "Se7en Pro — Disconnecting…",
        ConnectionState.Error => "Se7en Pro — Error",
        _ => "Se7en Pro — Disconnected",
    };

    private static string BuildToggleConnectionLabel(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "Disconnect",
        ConnectionState.Connecting => "Cancel connection",
        ConnectionState.Disconnecting => "Disconnecting…",
        _ => "Connect",
    };

    private static Icon? ResolveAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch
        {
        }
        return null;
    }

    private static Icon? LoadEmbeddedIcon(string relativeAssetPath)
    {

        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/{relativeAssetPath}", UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            if (info?.Stream is { } stream)
            {
                using (stream)
                {
                    return new Icon(stream);
                }
            }
        }
        catch
        {
        }

        try
        {
            var baseDir =
                Path.GetDirectoryName(Environment.ProcessPath)
                ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                ?? AppContext.BaseDirectory;
            var diskPath = Path.Combine(baseDir, "Assets", relativeAssetPath);
            if (File.Exists(diskPath))
            {
                return new Icon(diskPath);
            }
        }
        catch
        {
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_notifyIcon is not null)
        {
            try { _notifyIcon.Visible = false; } catch { }
            try { _notifyIcon.ContextMenuStrip?.Dispose(); } catch { }
            try { _notifyIcon.Dispose(); } catch { }
            _notifyIcon = null;
        }

        try { _iconDisconnected?.Dispose(); } catch { }
        try { _iconConnecting?.Dispose(); } catch { }
        try { _iconConnected?.Dispose(); } catch { }
        try { _iconDefault?.Dispose(); } catch { }
    }
}
