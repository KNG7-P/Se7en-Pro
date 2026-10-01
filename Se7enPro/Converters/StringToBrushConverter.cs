using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Se7enPro.Converters;

public sealed class StringToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex))
            return Brushes.Transparent;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex.Trim());
            if (parameter is string alphaText &&
                double.TryParse(alphaText, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
            {
                color.A = (byte)Math.Clamp(alpha * 255d, 0d, 255d);
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}