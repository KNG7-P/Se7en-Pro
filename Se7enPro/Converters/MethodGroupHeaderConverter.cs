using System;
using System.Globalization;
using System.Windows.Data;
using Se7enPro.Services;

namespace Se7enPro.Converters;

public sealed class MethodGroupHeaderConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ConnectionMethodExtensions.GroupOf(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
