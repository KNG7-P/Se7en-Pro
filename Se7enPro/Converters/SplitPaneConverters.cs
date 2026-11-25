using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Se7enPro.Converters;

// Two-way equality binding: IsChecked <-> CommandParameter/key. Used by the segmented
// controls on the split-tunnel page so a radio button can both reflect state and set it.
public sealed class EqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        return string.Equals(values[0] as string ?? "", values[1] as string ?? "", StringComparison.Ordinal);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
    {
        return new object[] { Binding.DoNothing, Binding.DoNothing };
    }
}

// Shows an element only when the bound string differs from the parameter.
public sealed class NotEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        var p = parameter as string ?? "";
        return string.Equals(s, p, StringComparison.Ordinal) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
