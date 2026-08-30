using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Se7enPro.ViewModels;

namespace Se7enPro.Converters;

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

public sealed class LatencyToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Excellent = MakeFrozen("#10B981");
    private static readonly SolidColorBrush Good      = MakeFrozen("#84CC16");
    private static readonly SolidColorBrush Fair      = MakeFrozen("#F59E0B");
    private static readonly SolidColorBrush Slow      = MakeFrozen("#F97316");
    private static readonly SolidColorBrush Bad       = MakeFrozen("#EF4444");
    private static readonly SolidColorBrush Unknown   = MakeFrozen("#6B7280");

    private static SolidColorBrush MakeFrozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int ms) return Unknown;
        if (ms < 100) return Excellent;
        if (ms < 200) return Good;
        if (ms < 400) return Fair;
        if (ms < 700) return Slow;
        return Bad;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class LatencyToSoftBrushConverter : IValueConverter
{

    private static readonly SolidColorBrush Excellent = MakeFrozen("#2910B981");
    private static readonly SolidColorBrush Good      = MakeFrozen("#2984CC16");
    private static readonly SolidColorBrush Fair      = MakeFrozen("#29F59E0B");
    private static readonly SolidColorBrush Slow      = MakeFrozen("#29F97316");
    private static readonly SolidColorBrush Bad       = MakeFrozen("#29EF4444");
    private static readonly SolidColorBrush Unknown   = MakeFrozen("#296B7280");

    private static SolidColorBrush MakeFrozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int ms) return Unknown;
        if (ms < 100) return Excellent;
        if (ms < 200) return Good;
        if (ms < 400) return Fair;
        if (ms < 700) return Slow;
        return Bad;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public sealed class LongConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            long l => l.ToString(culture),
            int i => i.ToString(culture),
            _ => value?.ToString() ?? "",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return 0L;
        var s = value.ToString();
        if (string.IsNullOrWhiteSpace(s)) return 0L;
        s = s!.Replace(",", "").Replace("_", "").Trim();
        return long.TryParse(s, NumberStyles.Integer, culture, out var l) ? l : 0L;
    }
}
