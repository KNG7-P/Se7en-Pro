using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PsiphonUI.Services;

namespace PsiphonUI.Converters;

public sealed class CountryCodeToFlagSourceConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, ImageSource> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string code || string.IsNullOrWhiteSpace(code) || code.Length != 2)
            return null;

        if (!CountryHelper.HasFlag(code))
            return null;

        return _cache.GetOrAdd(code, c =>
        {

            var uri = new Uri(
                $"pack://application:,,,/Resources/Flags/{c.ToLowerInvariant()}.png",
                UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = uri;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        });
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
