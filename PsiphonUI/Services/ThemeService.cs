using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace PsiphonUI.Services;

public sealed class ThemeService : IThemeService
{
    public void ApplyTheme(string theme)
    {
        var helper = new PaletteHelper();
        var t = helper.GetTheme();

        var baseTheme = theme switch
        {
            "light" => BaseTheme.Light,
            "system" => GetSystemTheme(),
            _ => BaseTheme.Dark,
        };

        t.SetBaseTheme(baseTheme);
        helper.SetTheme(t);

        UpdateSurfaceBrushes(baseTheme);
    }

    private static BaseTheme GetSystemTheme()
    {
        try
        {

            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i && i == 1)
            {
                return BaseTheme.Light;
            }
        }
        catch
        {

        }

        return BaseTheme.Dark;
    }

    private static void UpdateSurfaceBrushes(BaseTheme bt)
    {
        var app = Application.Current;
        if (app == null) return;

        if (bt == BaseTheme.Light)
        {

            app.Resources["Surface.SubtleBg"] = Brush(0x14, 0x00, 0x00, 0x00);
            app.Resources["Surface.SubtleBorder"] = Brush(0x24, 0x00, 0x00, 0x00);
            app.Resources["Surface.HoverBg"] = Brush(0x0F, 0x00, 0x00, 0x00);
            app.Resources["Surface.SelectedBg"] = Brush(0x1A, 0x00, 0x00, 0x00);
        }
        else
        {

            app.Resources["Surface.SubtleBg"] = Brush(0x14, 0xFF, 0xFF, 0xFF);
            app.Resources["Surface.SubtleBorder"] = Brush(0x22, 0xFF, 0xFF, 0xFF);
            app.Resources["Surface.HoverBg"] = Brush(0x14, 0xFF, 0xFF, 0xFF);
            app.Resources["Surface.SelectedBg"] = Brush(0x1F, 0xFF, 0xFF, 0xFF);
        }
    }

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
