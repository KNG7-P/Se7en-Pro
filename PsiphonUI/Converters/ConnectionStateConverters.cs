using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PsiphonUI.Models;

namespace PsiphonUI.Converters;

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
