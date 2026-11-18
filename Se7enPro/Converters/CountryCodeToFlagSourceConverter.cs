using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Se7enPro.Services;

namespace Se7enPro.Converters;

public sealed class CountryCodeToFlagSourceConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, ImageSource> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string codeRaw || string.IsNullOrWhiteSpace(codeRaw))
            return null;
        var code = codeRaw.Trim();
        var dash = code.IndexOf('-');
        var isoCode = dash > 0 ? code[..dash] : code;
        if (isoCode.Length != 2)
            return null;

        if (!CountryHelper.HasFlag(isoCode))
            return null;

        return _cache.GetOrAdd(isoCode, c =>
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
