using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace PsiphonUI.Services;

public sealed class TrayIconService : ITrayIconService
{
    private NotifyIcon? _notifyIcon;
    private bool _disposed;

    public event EventHandler? RequestShow;
    public event EventHandler? RequestExit;

    public bool IsHidden { get; private set; }

    public void Initialize()
    {
        if (_notifyIcon is not null) return;

        var icon = ResolveAppIcon() ?? SystemIcons.Application;

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "PsiphonUI",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Show PsiphonUI");
        showItem.Click += (_, _) => RequestShow?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(showItem);
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
                "PsiphonUI",
                "Still running in the system tray. Double-click the icon to restore.",
                ToolTipIcon.Info);
        }
        catch
        {
        }
    }

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
    }
}
