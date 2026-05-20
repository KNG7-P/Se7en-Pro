using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PsiphonUI.ViewModels;

namespace PsiphonUI.Converters;

public sealed class IpStatusToBrushConverter : IValueConverter
{

    private static readonly SolidColorBrush HealthyBrush = MakeFrozen("#10B981");
    private static readonly SolidColorBrush FailedBrush = MakeFrozen("#EF4444");
    private static readonly SolidColorBrush PendingBrush = MakeFrozen("#6B7280");

    private static SolidColorBrush MakeFrozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value is IpRowStatus s ? s : IpRowStatus.Pending;
        return status switch
        {
            IpRowStatus.Healthy => HealthyBrush,
            IpRowStatus.Failed => FailedBrush,
            _ => PendingBrush,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class IpStatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value is IpRowStatus s ? s : IpRowStatus.Pending;
        return status switch
        {
            IpRowStatus.Healthy => "OK",
            IpRowStatus.Failed => "FAIL",
            _ => "—",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class NullableIntMsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i ? $"{i} ms" : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class NullableIntConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i ? i.ToString(culture) : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
