using System;
using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace Se7enPro.Services;

public sealed class ThemeService : IThemeService
{
    
    
    
    
    
    
    
    
    
    
    
    
    
    private string? _lastRequest;
    private BaseTheme? _applied;

    public void ApplyTheme(string theme)
    {
        var isSystem = string.Equals(theme, "system", StringComparison.OrdinalIgnoreCase);
        var baseTheme = isSystem
            ? GetSystemTheme()
            : (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? BaseTheme.Light : BaseTheme.Dark);

        if (_applied == baseTheme
            && string.Equals(_lastRequest, theme, StringComparison.OrdinalIgnoreCase))
        {
            
            
            
            return;
        }

        _lastRequest = theme;
        _applied = baseTheme;

        var helper = new PaletteHelper();
        var t = helper.GetTheme();

        t.SetBaseTheme(baseTheme);
        t.SetPrimaryColor(Color.FromRgb(0x7C, 0x3A, 0xED));
        t.SetSecondaryColor(Color.FromRgb(0x00, 0xD4, 0xFF));
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
            
            app.Resources["Surface.AppBg"] = Brush(0xFF, 0xEE, 0xF1, 0xF9);
            app.Resources["Surface.AppBgDeep"] = Brush(0xFF, 0xE3, 0xE8, 0xF4);
            app.Resources["Surface.SidebarBg"] = Brush(0xFF, 0xFF, 0xFF, 0xFF);
            app.Resources["Surface.TitleBarBg"] = Brush(0xFF, 0xF6, 0xF8, 0xFD);
            app.Resources["Surface.CardBg"] = Brush(0xFF, 0xFF, 0xFF, 0xFF);
            app.Resources["Surface.Card"] = Brush(0xFF, 0xFF, 0xFF, 0xFF);
            app.Resources["Surface.CardSecondaryBg"] = Brush(0xFF, 0xF7, 0xF9, 0xFE);
            app.Resources["Surface.CardElevated"] = Brush(0xFF, 0xF7, 0xF9, 0xFE);
            app.Resources["Brand.ScrimBrush"] = Brush(0x55, 0x2A, 0x33, 0x50);
            app.Resources["Surface.GlassBg"] = Brush(0xFF, 0xF9, 0xFA, 0xFE);
            app.Resources["Surface.GlassStrongBg"] = Brush(0xFF, 0xFD, 0xFE, 0xFF);
            app.Resources["Surface.GlassBorder"] = Brush(0x14, 0x10, 0x18, 0x28);
            app.Resources["Surface.CardBorder"] = Brush(0xFF, 0xE2, 0xE7, 0xF1);
            app.Resources["Surface.CardBorderStrong"] = Brush(0xFF, 0xCC, 0xD4, 0xE4);
            app.Resources["Surface.SubtleBg"] = Brush(0xFF, 0xED, 0xF1, 0xF8);
            app.Resources["Surface.SubtleBorder"] = Brush(0xFF, 0xE2, 0xE7, 0xF1);
            app.Resources["Surface.HoverBg"] = Brush(0xFF, 0xE9, 0xEF, 0xFA);
            app.Resources["Surface.SelectedBg"] = Brush(0xFF, 0xE5, 0xEC, 0xFB);
            app.Resources["Surface.LogBoxBg"] = Brush(0xFF, 0xF3, 0xF6, 0xFC);
            app.Resources["Surface.LogBoxBorder"] = Brush(0xFF, 0xD7, 0xDF, 0xED);
            app.Resources["Surface.ConnectCoreBg"] = Brush(0xFF, 0xE5, 0xEC, 0xFB);
            app.Resources["Surface.ConnectCoreHoverBg"] = Brush(0xFF, 0xD9, 0xE3, 0xFB);
            app.Resources["Surface.ConnectCorePressedBg"] = Brush(0xFF, 0xD1, 0xDC, 0xFA);
            app.Resources["TextOnConnectBrush"] = Brush(0xFF, 0x16, 0x1A, 0x28);
            app.Resources["Surface.NavSelectedBg"] = Brush(0xFF, 0x00, 0xD4, 0xFF);
            app.Resources["Surface.NavSelectedBorder"] = Brush(0xFF, 0x00, 0xD4, 0xFF);
            app.Resources["Nav.SelectedIcon"] = Brush(0xFF, 0xFF, 0xFF, 0xFF);
            app.Resources["Surface.BorderBrush"] = Brush(0xFF, 0xE2, 0xE7, 0xF1);

            app.Resources["Tab.SelectedBg"] = Brush(0xFF, 0x00, 0xD4, 0xFF);
            app.Resources["Tab.SelectedBorder"] = Brush(0xFF, 0x00, 0xD4, 0xFF);
            app.Resources["Tab.SelectedText"] = Brush(0xFF, 0xFF, 0xFF, 0xFF);
            app.Resources["Tab.SelectedIcon"] = Brush(0xFF, 0xFF, 0xFF, 0xFF);
            app.Resources["Tab.RestBg"] = Brush(0x73, 0xFF, 0xFF, 0xFF);
            app.Resources["Tab.RestBorder"] = Brush(0x66, 0xE2, 0xE7, 0xF1);
            app.Resources["Tab.HoverBg"] = Brush(0x14, 0x00, 0xD4, 0xFF);
            app.Resources["Tab.HoverBorder"] = Brush(0x4D, 0x00, 0xD4, 0xFF);
            app.Resources["Tab.HoverText"] = Brush(0xFF, 0x00, 0xD4, 0xFF);
            app.Resources["Switch.TrackOff"] = Brush(0xFF, 0xE2, 0xE8, 0xF0);
            app.Resources["Switch.ThumbOff"] = Brush(0xFF, 0x64, 0x74, 0x8B);
            app.Resources["Switch.TrackOffBorder"] = Brush(0xFF, 0xCB, 0xD5, 0xE1);
            app.Resources["Switch.TrackOffHover"] = Brush(0xFF, 0xC0, 0xE5, 0xF2);
            app.Resources["Menu.HoverBg"] = Brush(0x1F, 0x00, 0xD4, 0xFF);

            app.Resources["BgMain"] = Brush(0xFF, 0xEE, 0xF1, 0xF9);
            app.Resources["BgSidebar"] = Brush(0xFF, 0xFF, 0xFF, 0xFF);
            app.Resources["BgCard"] = Brush(0xFF, 0xFF, 0xFF, 0xFF);
            app.Resources["BgCardElevated"] = Brush(0xFF, 0xF7, 0xF9, 0xFE);
            app.Resources["BgInput"] = Brush(0xFF, 0xEF, 0xF2, 0xF9);
            app.Resources["BorderBrushDefault"] = Brush(0xFF, 0xE2, 0xE7, 0xF1);
            app.Resources["BorderBrushHover"] = Brush(0xFF, 0xCC, 0xD4, 0xE4);

            app.Resources["TextPrimaryBrush"] = Brush(0xFF, 0x16, 0x1A, 0x28);
            app.Resources["TextSecondaryBrush"] = Brush(0xFF, 0x56, 0x5D, 0x72);
            app.Resources["TextMutedBrush"] = Brush(0xFF, 0x8A, 0x92, 0xA6);
            app.Resources["TextPrimary"] = Brush(0xFF, 0x16, 0x1A, 0x28);
            app.Resources["TextSecondary"] = Brush(0xFF, 0x56, 0x5D, 0x72);
            app.Resources["TextMuted"] = Brush(0xFF, 0x8A, 0x92, 0xA6);

            app.Resources["WindowBackdrop"] = Brush(0xFF, 0xEE, 0xF1, 0xF9);
            app.Resources["Surface.AppBackground"] = BackgroundBrush(
                Color.FromRgb(0xEE, 0xF1, 0xF9),
                Color.FromRgb(0xE3, 0xE8, 0xF4));

            
            
            
            app.Resources["Btn.Primary.Text"] = Brush(0xFF, 0x00, 0x7E, 0x9B);
            app.Resources["Btn.Success.Solid"] = Brush(0xFF, 0x05, 0x96, 0x69);
            app.Resources["Btn.Success.SolidHover"] = Brush(0xFF, 0x04, 0x78, 0x57);
            app.Resources["Btn.Success.SolidPress"] = Brush(0xFF, 0x06, 0x5F, 0x46);
            app.Resources["Btn.Success.Text"] = Brush(0xFF, 0x08, 0x74, 0x43);
            app.Resources["Btn.Warning.Text"] = Brush(0xFF, 0xB2, 0x5E, 0x00);
            app.Resources["Btn.Danger.Solid"] = Brush(0xFF, 0xDC, 0x26, 0x26);
            app.Resources["Btn.Danger.SolidHover"] = Brush(0xFF, 0xB9, 0x1C, 0x1C);
            app.Resources["Btn.Danger.SolidPress"] = Brush(0xFF, 0x99, 0x1B, 0x1B);
            app.Resources["Btn.Danger.Text"] = Brush(0xFF, 0xC2, 0x26, 0x2B);
            app.Resources["Btn.Accent.Solid"] = Brush(0xFF, 0x7C, 0x3A, 0xED);
            app.Resources["Btn.Accent.SolidHover"] = Brush(0xFF, 0x6D, 0x28, 0xD9);
            app.Resources["Btn.Accent.SolidPress"] = Brush(0xFF, 0x5B, 0x21, 0xB6);
            app.Resources["Btn.Accent.Text"] = Brush(0xFF, 0x6D, 0x28, 0xD9);
            app.Resources["Btn.Neutral.Border"] = Brush(0x66, 0xCC, 0xD4, 0xE4);
            app.Resources["Btn.Neutral.BorderHover"] = Brush(0xAA, 0xB6, 0xC6, 0xDE);
            app.Resources["Btn.FocusRing"] = Brush(0xFF, 0x00, 0xA3, 0xCC);
        }
        else
        {
            
            app.Resources["Surface.AppBg"] = Brush(0xFF, 0x08, 0x0A, 0x12);
            app.Resources["Surface.AppBgDeep"] = Brush(0xFF, 0x04, 0x05, 0x0A);
            app.Resources["Surface.SidebarBg"] = Brush(0xFF, 0x0E, 0x10, 0x19);
            app.Resources["Surface.TitleBarBg"] = Brush(0xFF, 0x0B, 0x0D, 0x15);
            app.Resources["Surface.CardBg"] = Brush(0xFF, 0x14, 0x16, 0x1F);
            app.Resources["Surface.Card"] = Brush(0xFF, 0x14, 0x16, 0x1F);
            app.Resources["Surface.CardSecondaryBg"] = Brush(0xFF, 0x1B, 0x1D, 0x28);
            app.Resources["Surface.CardElevated"] = Brush(0xFF, 0x1B, 0x1D, 0x28);
            app.Resources["Brand.ScrimBrush"] = Brush(0xB3, 0x05, 0x06, 0x0C);
            app.Resources["Surface.GlassBg"] = Brush(0xFF, 0x14, 0x16, 0x1F);
            app.Resources["Surface.GlassStrongBg"] = Brush(0xFF, 0x1B, 0x1D, 0x28);
            app.Resources["Surface.GlassBorder"] = Brush(0x24, 0xFF, 0xFF, 0xFF);
            app.Resources["Surface.CardBorder"] = Brush(0xFF, 0x24, 0x26, 0x34);
            app.Resources["Surface.CardBorderStrong"] = Brush(0xFF, 0x36, 0x3A, 0x4E);
            app.Resources["Surface.SubtleBg"] = Brush(0xFF, 0x10, 0x12, 0x1B);
            app.Resources["Surface.SubtleBorder"] = Brush(0xFF, 0x24, 0x26, 0x34);
            app.Resources["Surface.HoverBg"] = Brush(0xFF, 0x1A, 0x1D, 0x28);
            app.Resources["Surface.SelectedBg"] = Brush(0xFF, 0x20, 0x23, 0x3A);
            app.Resources["Surface.LogBoxBg"] = Brush(0xFF, 0x14, 0x14, 0x1E);
            app.Resources["Surface.LogBoxBorder"] = Brush(0xFF, 0x24, 0x26, 0x34);
            app.Resources["Surface.ConnectCoreBg"] = Brush(0xFF, 0x1A, 0x1D, 0x2B);
            app.Resources["Surface.ConnectCoreHoverBg"] = Brush(0xFF, 0x23, 0x27, 0x3A);
            app.Resources["Surface.ConnectCorePressedBg"] = Brush(0xFF, 0x15, 0x18, 0x27);
            app.Resources["TextOnConnectBrush"] = Brush(0xFF, 0xFF, 0xFF, 0xFF);
            
            
            app.Resources["Surface.NavSelectedBg"] = Brush(0x33, 0x00, 0xD4, 0xFF);
            app.Resources["Surface.NavSelectedBorder"] = Brush(0x8C, 0x00, 0xD4, 0xFF);
            app.Resources["Nav.SelectedIcon"] = Brush(0xFF, 0x00, 0xD4, 0xFF);
            app.Resources["Surface.BorderBrush"] = Brush(0xFF, 0x24, 0x26, 0x34);

            app.Resources["Tab.SelectedBg"] = Brush(0x33, 0x00, 0xD4, 0xFF);
            app.Resources["Tab.SelectedBorder"] = Brush(0x8C, 0x00, 0xD4, 0xFF);
            app.Resources["Tab.SelectedText"] = Brush(0xFF, 0xFF, 0xFF, 0xFF);
            app.Resources["Tab.SelectedIcon"] = Brush(0xFF, 0x00, 0xD4, 0xFF);
            app.Resources["Tab.RestBg"] = Brush(0x73, 0x14, 0x16, 0x1F);
            app.Resources["Tab.RestBorder"] = Brush(0x66, 0x24, 0x26, 0x34);
            app.Resources["Tab.HoverBg"] = Brush(0xCC, 0x1B, 0x1D, 0x28);
            app.Resources["Tab.HoverBorder"] = Brush(0xCC, 0x24, 0x26, 0x34);
            app.Resources["Tab.HoverText"] = Brush(0xFF, 0xFF, 0xFF, 0xFF);
            app.Resources["Switch.TrackOff"] = Brush(0xFF, 0x1E, 0x29, 0x3B);
            app.Resources["Switch.ThumbOff"] = Brush(0xFF, 0x94, 0xA3, 0xB8);
            app.Resources["Switch.TrackOffBorder"] = Brush(0xFF, 0x47, 0x55, 0x69);
            app.Resources["Switch.TrackOffHover"] = Brush(0xFF, 0x1A, 0x43, 0x58);
            app.Resources["Menu.HoverBg"] = Brush(0x14, 0xFF, 0xFF, 0xFF);

            app.Resources["BgMain"] = Brush(0xFF, 0x08, 0x0A, 0x12);
            app.Resources["BgSidebar"] = Brush(0xFF, 0x0E, 0x10, 0x19);
            app.Resources["BgCard"] = Brush(0xFF, 0x14, 0x16, 0x1F);
            app.Resources["BgCardElevated"] = Brush(0xFF, 0x1B, 0x1D, 0x28);
            app.Resources["BgInput"] = Brush(0xFF, 0x0C, 0x0E, 0x16);
            app.Resources["BorderBrushDefault"] = Brush(0xFF, 0x24, 0x26, 0x34);
            app.Resources["BorderBrushHover"] = Brush(0xFF, 0x36, 0x3A, 0x4E);

            app.Resources["TextPrimaryBrush"] = Brush(0xFF, 0xF3, 0xF4, 0xFB);
            app.Resources["TextSecondaryBrush"] = Brush(0xFF, 0x9E, 0xA2, 0xB8);
            app.Resources["TextMutedBrush"] = Brush(0xFF, 0x66, 0x6B, 0x82);
            app.Resources["TextPrimary"] = Brush(0xFF, 0xF3, 0xF4, 0xFB);
            app.Resources["TextSecondary"] = Brush(0xFF, 0x9E, 0xA2, 0xB8);
            app.Resources["TextMuted"] = Brush(0xFF, 0x66, 0x6B, 0x82);

            app.Resources["WindowBackdrop"] = Brush(0xFF, 0x08, 0x0A, 0x12);
            app.Resources["Surface.AppBackground"] = BackgroundBrush(
                Color.FromRgb(0x08, 0x0A, 0x12),
                Color.FromRgb(0x04, 0x05, 0x0A));

            
            
            app.Resources["Btn.Primary.Text"] = Brush(0xFF, 0x00, 0xD4, 0xFF);
            app.Resources["Btn.Success.Solid"] = Brush(0xFF, 0x10, 0xB9, 0x81);
            app.Resources["Btn.Success.SolidHover"] = Brush(0xFF, 0x0E, 0xA2, 0x71);
            app.Resources["Btn.Success.SolidPress"] = Brush(0xFF, 0x0C, 0x8A, 0x61);
            app.Resources["Btn.Success.Text"] = Brush(0xFF, 0x34, 0xD3, 0x99);
            app.Resources["Btn.Warning.Text"] = Brush(0xFF, 0xFB, 0xBF, 0x24);
            app.Resources["Btn.Danger.Solid"] = Brush(0xFF, 0xEF, 0x44, 0x44);
            app.Resources["Btn.Danger.SolidHover"] = Brush(0xFF, 0xE6, 0x3B, 0x3B);
            app.Resources["Btn.Danger.SolidPress"] = Brush(0xFF, 0xD3, 0x2F, 0x2F);
            app.Resources["Btn.Danger.Text"] = Brush(0xFF, 0xF8, 0x71, 0x71);
            app.Resources["Btn.Accent.Solid"] = Brush(0xFF, 0x8B, 0x5C, 0xF6);
            app.Resources["Btn.Accent.SolidHover"] = Brush(0xFF, 0x7C, 0x3A, 0xED);
            app.Resources["Btn.Accent.SolidPress"] = Brush(0xFF, 0x6D, 0x28, 0xD9);
            app.Resources["Btn.Accent.Text"] = Brush(0xFF, 0xA7, 0x8B, 0xFA);
            app.Resources["Btn.Neutral.Border"] = Brush(0x45, 0x24, 0x26, 0x34);
            app.Resources["Btn.Neutral.BorderHover"] = Brush(0x8C, 0x36, 0x3A, 0x4E);
            app.Resources["Btn.FocusRing"] = Brush(0xFF, 0x00, 0xD4, 0xFF);
        }

        ApplyMaterialResources(app, bt == BaseTheme.Light);
    }

    private static void ApplyMaterialResources(Application app, bool light)
    {
        var (background, surface, surfaceDim, surfaceBright, lowest, low, container, high, highest,
             onSurface, onVariant, outline, outlineVariant, scrim, textBody) = light
            ? (Color.FromRgb(0xEE, 0xF1, 0xF9), Color.FromRgb(0xFF, 0xFF, 0xFF),
               Color.FromRgb(0xE3, 0xE8, 0xF4), Color.FromRgb(0xF7, 0xF9, 0xFE),
               Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xF6, 0xF8, 0xFD),
               Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xF7, 0xF9, 0xFE),
               Color.FromRgb(0xE5, 0xEC, 0xFB), Color.FromRgb(0x16, 0x1A, 0x28),
               Color.FromRgb(0x56, 0x5D, 0x72), Color.FromRgb(0xE2, 0xE7, 0xF1),
               Color.FromRgb(0xCC, 0xD4, 0xE4), Color.FromArgb(0x55, 0x2A, 0x33, 0x50),
               Color.FromRgb(0x16, 0x1A, 0x28))
            : (Color.FromRgb(0x08, 0x0A, 0x12), Color.FromRgb(0x14, 0x16, 0x1F),
               Color.FromRgb(0x08, 0x0A, 0x12), Color.FromRgb(0x1B, 0x1D, 0x28),
               Color.FromRgb(0x04, 0x05, 0x0A), Color.FromRgb(0x08, 0x0A, 0x12),
               Color.FromRgb(0x14, 0x16, 0x1F), Color.FromRgb(0x1B, 0x1D, 0x28),
               Color.FromRgb(0x20, 0x23, 0x3A), Color.FromRgb(0xF3, 0xF4, 0xFB),
               Color.FromRgb(0x9E, 0xA2, 0xB8), Color.FromRgb(0x24, 0x26, 0x34),
               Color.FromRgb(0x36, 0x3A, 0x4E), Color.FromArgb(0xB3, 0x05, 0x06, 0x0C),
               Color.FromRgb(0xF3, 0xF4, 0xFB));

        app.Resources["MaterialDesignPaper"] = Brush(surface);
        app.Resources["MaterialDesignCardBackground"] = Brush(surface);
        app.Resources["MaterialDesignBody"] = Brush(onSurface);
        app.Resources["MaterialDesignBodyLight"] = Brush(onVariant);
        app.Resources["MaterialDesignColumnHeader"] = Brush(onVariant);
        app.Resources["MaterialDesignSelection"] = Brush(highest);
        app.Resources["MaterialDesignTextBoxBorder"] = Brush(outline);
        app.Resources["MaterialDesignDivider"] = Brush(outline);

        app.Resources["MaterialDesign.Brush.Background"] = Brush(background);
        app.Resources["MaterialDesign.Brush.Surface"] = Brush(surface);
        app.Resources["MaterialDesign.Brush.SurfaceDim"] = Brush(surfaceDim);
        app.Resources["MaterialDesign.Brush.SurfaceBright"] = Brush(surfaceBright);
        app.Resources["MaterialDesign.Brush.SurfaceContainerLowest"] = Brush(lowest);
        app.Resources["MaterialDesign.Brush.SurfaceContainerLow"] = Brush(low);
        app.Resources["MaterialDesign.Brush.SurfaceContainer"] = Brush(container);
        app.Resources["MaterialDesign.Brush.SurfaceContainerHigh"] = Brush(high);
        app.Resources["MaterialDesign.Brush.SurfaceContainerHighest"] = Brush(highest);
        app.Resources["MaterialDesign.Brush.OnSurface"] = Brush(onSurface);
        app.Resources["MaterialDesign.Brush.OnSurfaceVariant"] = Brush(onVariant);
        app.Resources["MaterialDesign.Brush.InverseSurface"] = Brush(onSurface);
        app.Resources["MaterialDesign.Brush.InverseOnSurface"] = Brush(surface);
        app.Resources["MaterialDesign.Brush.PrimaryContainer"] = Brush(light ? Color.FromRgb(0xE5, 0xEC, 0xFB) : Color.FromRgb(0x20, 0x23, 0x3A));
        app.Resources["MaterialDesign.Brush.OnPrimaryContainer"] = Brush(light ? Color.FromRgb(0x7C, 0x3A, 0xED) : Color.FromRgb(0x00, 0xD4, 0xFF));
        app.Resources["MaterialDesign.Brush.SecondaryContainer"] = Brush(light ? Color.FromRgb(0xE5, 0xEC, 0xFB) : Color.FromRgb(0x20, 0x23, 0x3A));
        app.Resources["MaterialDesign.Brush.OnSecondaryContainer"] = Brush(light ? Color.FromRgb(0x7C, 0x3A, 0xED) : Color.FromRgb(0xA7, 0x8B, 0xFA));
        app.Resources["MaterialDesign.Brush.TertiaryContainer"] = Brush(light ? Color.FromRgb(0xD1, 0xFA, 0xE5) : Color.FromRgb(0x1A, 0x33, 0x26));
        app.Resources["MaterialDesign.Brush.OnTertiaryContainer"] = Brush(light ? Color.FromRgb(0x04, 0x78, 0x57) : Color.FromRgb(0x34, 0xD3, 0x99));
        app.Resources["MaterialDesign.Brush.ErrorContainer"] = Brush(light ? Color.FromRgb(0xFE, 0xE2, 0xE2) : Color.FromRgb(0x38, 0x18, 0x18));
        app.Resources["MaterialDesign.Brush.OnErrorContainer"] = Brush(light ? Color.FromRgb(0xB9, 0x1C, 0x1C) : Color.FromRgb(0xFC, 0xA5, 0xA5));
        app.Resources["MaterialDesign.Brush.Outline"] = Brush(outline);
        app.Resources["MaterialDesign.Brush.OutlineVariant"] = Brush(outlineVariant);
        app.Resources["MaterialDesign.Brush.Scrim"] = Brush(scrim);
        app.Resources["MaterialDesign.Brush.Primary"] = Brush(Color.FromRgb(0x7C, 0x3A, 0xED));
        app.Resources["MaterialDesign.Brush.OnPrimary"] = Brush(Colors.White);
        app.Resources["MaterialDesign.Brush.Secondary"] = Brush(Color.FromRgb(0x00, 0xD4, 0xFF));
        app.Resources["MaterialDesign.Brush.OnSecondary"] = Brush(Colors.White);
        app.Resources["MaterialDesign.Brush.Tertiary"] = Brush(Color.FromRgb(0x10, 0xB9, 0x81));
        app.Resources["MaterialDesign.Brush.OnTertiary"] = Brush(Colors.White);
        app.Resources["MaterialDesign.Brush.Error"] = Brush(Color.FromRgb(0xEF, 0x44, 0x44));
        app.Resources["MaterialDesign.Brush.OnError"] = Brush(Colors.White);
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush BackgroundBrush(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(
            top,
            bottom,
            new System.Windows.Point(0, 0),
            new System.Windows.Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
