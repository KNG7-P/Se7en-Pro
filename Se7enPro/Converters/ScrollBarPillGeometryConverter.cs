using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Se7enPro.Converters;

public sealed class ScrollBarPillGeometryConverter : IMultiValueConverter
{
        public bool Horizontal { get; set; }

        public double Inset { get; set; } = 2;

        public double Minimum { get; set; } = 30;

    public object Convert(object[]? values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 5) return new Thickness(Inset);

        double track = Num(values[0]);
        double viewport = Num(values[1]);
        double max = Num(values[2]);
        double value = Num(values[3]);
        double min = Num(values[4]);

        if (track <= 0) return new Thickness(Inset);

        double range = max - min;
        if (range <= 0)
        {
            
            return Horizontal ? new Thickness(0, Inset, track, Inset)
                              : new Thickness(Inset, 0, Inset, track);
        }

        
        double extent = range + Math.Max(viewport, 0);
        double slot = extent > 0 ? track * Math.Max(viewport, 0) / extent : track;

        double pill = Math.Min(track, Math.Max(Minimum, slot));
        double offset = Math.Clamp(value - min, 0d, range) / range * (track - pill);

        return Horizontal
            ? new Thickness(offset, Inset, track - offset - pill, Inset)
            : new Thickness(Inset, offset, Inset, track - offset - pill);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static double Num(object? v) => v is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? d : 0;
}
