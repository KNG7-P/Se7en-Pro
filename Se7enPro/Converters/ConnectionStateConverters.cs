using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Se7enPro.Models;
using Se7enPro.Services;
using Se7enPro.ViewModels;

namespace Se7enPro.Converters;

public sealed class StateToButtonTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ConnectionState s
            ? s switch
            {
                ConnectionState.Connected => Loc.Of("Disconnect"),
                ConnectionState.Connecting => Loc.Of("Cancel"),
                ConnectionState.Disconnecting => Loc.Of("Stopping…"),
                _ => Loc.Of("Connect"),
            }
            : Loc.Of("Connect");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

internal static class StateBrushes
{
    public static readonly SolidColorBrush Green = MakeFrozen("#10B981");
    public static readonly SolidColorBrush BrightGreen = MakeFrozen("#22C55E");
    public static readonly SolidColorBrush Amber = MakeFrozen("#F59E0B");
    public static readonly SolidColorBrush Red = MakeFrozen("#EF4444");
    public static readonly SolidColorBrush Grey = MakeFrozen("#6B7280");
    public static readonly SolidColorBrush BrandPurple = MakeFrozen("#7C3AED");
    public static readonly SolidColorBrush Gray = MakeFrozen("Gray");

    private static SolidColorBrush MakeFrozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }
}

public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ConnectionState s
            ? s switch
            {
                ConnectionState.Connected => StateBrushes.Green,
                ConnectionState.Connecting or ConnectionState.Disconnecting => StateBrushes.Amber,
                ConnectionState.Error => StateBrushes.Red,
                _ => StateBrushes.Grey,
            }
            : (object)StateBrushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public sealed class StateToConnectButtonBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ConnectionState s
            ? s switch
            {
                ConnectionState.Connected => StateBrushes.BrightGreen,
                ConnectionState.Connecting or ConnectionState.Disconnecting => StateBrushes.Amber,
                ConnectionState.Error => StateBrushes.Red,
                _ => StateBrushes.BrandPurple,
            }
            : (object)StateBrushes.BrandPurple;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : (object)true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Convert(value, targetType, parameter, culture);
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public sealed class StateToBadgeBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBg = MakeFrozen("#2210B981");
    private static readonly SolidColorBrush AmberBg = MakeFrozen("#22F59E0B");
    private static readonly SolidColorBrush RedBg = MakeFrozen("#22EF4444");
    private static readonly SolidColorBrush GreyBg = MakeFrozen("#14FFFFFF");

    private static SolidColorBrush MakeFrozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ConnectionState s
            ? s switch
            {
                ConnectionState.Connected => GreenBg,
                ConnectionState.Connecting or ConnectionState.Disconnecting => AmberBg,
                ConnectionState.Error => RedBg,
                _ => GreyBg,
            }
            : GreyBg;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public sealed class StateToBadgeBorderConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBorder = MakeFrozen("#5510B981");
    private static readonly SolidColorBrush AmberBorder = MakeFrozen("#55F59E0B");
    private static readonly SolidColorBrush RedBorder = MakeFrozen("#55EF4444");
    private static readonly SolidColorBrush GreyBorder = MakeFrozen("#26FFFFFF");

    private static SolidColorBrush MakeFrozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ConnectionState s
            ? s switch
            {
                ConnectionState.Connected => GreenBorder,
                ConnectionState.Connecting or ConnectionState.Disconnecting => AmberBorder,
                ConnectionState.Error => RedBorder,
                _ => GreyBorder,
            }
            : GreyBorder;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        var p = parameter as string ?? "";
        return string.Equals(s, p, StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b)
        {
            return parameter as string ?? "";
        }
        return System.Windows.DependencyProperty.UnsetValue;
    }
}

public sealed class AutoRegionToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string code || string.IsNullOrWhiteSpace(code))
            return System.Windows.Visibility.Visible;

        return string.Equals(code, "auto", StringComparison.OrdinalIgnoreCase)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public sealed class NotAutoRegionToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string code || string.IsNullOrWhiteSpace(code))
            return System.Windows.Visibility.Collapsed;

        return string.Equals(code, "auto", StringComparison.OrdinalIgnoreCase)
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public sealed class LogChannelToBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var channel = values.Length > 0 ? values[0] as string : null;
        var dark = values.Length < 2 || values[1] is not bool isDark || isDark;
        return string.Equals(parameter as string, "bg", StringComparison.OrdinalIgnoreCase)
            ? LogPalette.ChipBackground(channel, dark)
            : LogPalette.ChipForeground(channel, dark);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public sealed class LogMessageToBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var dark = values.Length < 2 || values[1] is not bool isDark || isDark;
        var severity = values.Length > 0 && values[0] is LogSeverity s ? s : LogSeverity.Plain;
        if (severity != LogSeverity.Plain)
        {
            return LogPalette.Severity(severity, dark);
        }
        return LogPalette.Message(values.Length > 2 ? values[2] as string : null, dark);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

internal static class LogPalette
{
    private readonly record struct Ink(SolidColorBrush Dark, SolidColorBrush Light);

    private static Ink Pair(string darkHex, string lightHex) => new(Freeze(darkHex), Freeze(lightHex));

    
    
    private static readonly Dictionary<string, Ink> Channels = new(StringComparer.Ordinal)
    {
        [LogTags.Psiphon] = Pair("#67E8F9", "#0E7490"),
        [LogTags.Aether] = Pair("#C4B5FD", "#6D28D9"),
        [LogTags.Tor] = Pair("#D8B4FE", "#86198F"),
        [LogTags.Shard] = Pair("#7DD3FC", "#0369A1"),
        [LogTags.V2Ray] = Pair("#6EE7B7", "#047857"),
        [LogTags.Chain] = Pair("#A5B4FC", "#4338CA"),
        [LogTags.Tun] = Pair("#5EEAD4", "#0F766E"),
        [LogTags.App] = Pair("#CBD5E1", "#475569"),
    };

    private static readonly Ink Neutral = Pair("#94A3B8", "#64748B");
    private static readonly Ink Error = Pair("#F87171", "#B91C1C");
    private static readonly Ink Warning = Pair("#FBBF24", "#B45309");
    private static readonly Ink Success = Pair("#34D399", "#047857");
    private static readonly Ink Info = Pair("#38BDF8", "#0369A1");

    public static SolidColorBrush Severity(LogSeverity severity, bool dark) => severity switch
    {
        LogSeverity.Error => Pick(Error, dark),
        LogSeverity.Warning => Pick(Warning, dark),
        LogSeverity.Success => Pick(Success, dark),
        LogSeverity.Info => Pick(Info, dark),
        _ => Pick(Neutral, dark),
    };

    public static SolidColorBrush ChipForeground(string? channel, bool dark) =>
        Pick(Lookup(channel), dark);

    public static SolidColorBrush Message(string? channel, bool dark) =>
        Pick(Lookup(channel), dark);

    public static SolidColorBrush ChipBackground(string? channel, bool dark)
    {
        var key = (channel ?? "") + (dark ? 'd' : 'l');
        if (TintCache.TryGetValue(key, out var cached)) return cached;

        var baseBrush = Pick(Lookup(channel), dark);
        
        var tinted = Color.FromArgb((byte)(dark ? 0x33 : 0x1F), baseBrush.Color.R, baseBrush.Color.G, baseBrush.Color.B);
        var brush = Freeze(tinted);
        TintCache[key] = brush;
        return brush;
    }

    private static Ink Lookup(string? channel) =>
        channel is not null && Channels.TryGetValue(channel, out var ink) ? ink : Neutral;

    private static SolidColorBrush Pick(Ink ink, bool dark) => dark ? ink.Dark : ink.Light;

    
    private static readonly Dictionary<string, SolidColorBrush> TintCache = new(StringComparer.Ordinal);

    private static SolidColorBrush Freeze(string hex) =>
        Freeze((Color)ColorConverter.ConvertFromString(hex)!);

    private static SolidColorBrush Freeze(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }
}

public sealed class StateToAuraBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Emerald = MakeFrozen("#10B981");
    private static readonly SolidColorBrush Amber = MakeFrozen("#F59E0B");
    private static readonly SolidColorBrush Red = MakeFrozen("#EF4444");
    private static readonly SolidColorBrush Violet = MakeFrozen("#8B5CF6");

    private static SolidColorBrush MakeFrozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ConnectionState s
            ? s switch
            {
                ConnectionState.Connected => Emerald,
                ConnectionState.Connecting or ConnectionState.Disconnecting => Amber,
                ConnectionState.Error => Red,
                _ => Violet,
            }
            : Violet;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public sealed class StateToOrbActionTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ConnectionState s
            ? s switch
            {
                ConnectionState.Connected => Loc.Of("DISCONNECT"),
                ConnectionState.Connecting => Loc.Of("CANCEL"),
                ConnectionState.Disconnecting => Loc.Of("STOPPING"),
                ConnectionState.Error => Loc.Of("RETRY"),
                _ => Loc.Of("CONNECT"),
            }
            : Loc.Of("CONNECT");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public sealed class StateToOrbIconKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ConnectionState s
            ? s switch
            {
                ConnectionState.Connected => PackIconKind.Power,
                ConnectionState.Connecting => PackIconKind.Close,
                ConnectionState.Disconnecting => PackIconKind.Refresh,
                ConnectionState.Error => PackIconKind.AlertOutline,
                _ => PackIconKind.Power,
            }
            : PackIconKind.Power;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

