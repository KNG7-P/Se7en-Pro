using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Se7enPro.Models;

namespace Se7enPro.Converters;

public sealed class StateToButtonTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ConnectionState s
            ? s switch
            {
                ConnectionState.Connected => "Disconnect",
                ConnectionState.Connecting => "Cancel",
                ConnectionState.Disconnecting => "Stopping…",
                _ => "Connect",
            }
            : "Connect";

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

public sealed class LogLineToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush RedBrush = MakeFrozen("#F87171");
    private static readonly SolidColorBrush AmberBrush = MakeFrozen("#FBBF24");
    private static readonly SolidColorBrush GreenBrush = MakeFrozen("#34D399");
    private static readonly SolidColorBrush CyanBrush = MakeFrozen("#38BDF8");
    private static readonly SolidColorBrush DefaultBrush = MakeFrozen("#CBD5E1");

    private static SolidColorBrush MakeFrozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string line) return DefaultBrush;

        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("fatal", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("panic", StringComparison.OrdinalIgnoreCase))
        {
            return RedBrush;
        }

        if (line.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            return AmberBrush;
        }

        if (line.Contains("connected", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("established", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("listening", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ready", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("success", StringComparison.OrdinalIgnoreCase))
        {
            return GreenBrush;
        }

        if (line.Contains("notice", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("info", StringComparison.OrdinalIgnoreCase))
        {
            return CyanBrush;
        }

        return DefaultBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
