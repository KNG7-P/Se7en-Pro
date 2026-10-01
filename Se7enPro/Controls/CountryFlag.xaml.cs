using System;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Se7enPro.Controls;

public partial class CountryFlag : UserControl
{
    private static readonly ConcurrentDictionary<string, ImageSource> _imageCache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly DependencyProperty CountryCodeProperty =
        DependencyProperty.Register(
            nameof(CountryCode),
            typeof(string),
            typeof(CountryFlag),
            new PropertyMetadata(string.Empty, OnCountryCodeChanged));

    public static readonly DependencyProperty FlagWidthProperty =
        DependencyProperty.Register(
            nameof(FlagWidth),
            typeof(double),
            typeof(CountryFlag),
            new PropertyMetadata(26.0));

    public static readonly DependencyProperty FlagHeightProperty =
        DependencyProperty.Register(
            nameof(FlagHeight),
            typeof(double),
            typeof(CountryFlag),
            new PropertyMetadata(18.0));

    public static readonly DependencyProperty FlagCornerRadiusProperty =
        DependencyProperty.Register(
            nameof(FlagCornerRadius),
            typeof(CornerRadius),
            typeof(CountryFlag),
            new PropertyMetadata(new CornerRadius(4.0)));

    public string? CountryCode
    {
        get => (string?)GetValue(CountryCodeProperty);
        set => SetValue(CountryCodeProperty, value);
    }

    public double FlagWidth
    {
        get => (double)GetValue(FlagWidthProperty);
        set => SetValue(FlagWidthProperty, value);
    }

    public double FlagHeight
    {
        get => (double)GetValue(FlagHeightProperty);
        set => SetValue(FlagHeightProperty, value);
    }

    public CornerRadius FlagCornerRadius
    {
        get => (CornerRadius)GetValue(FlagCornerRadiusProperty);
        set => SetValue(FlagCornerRadiusProperty, value);
    }

    public CountryFlag()
    {
        InitializeComponent();
        UpdateVisuals();
    }

    private static void OnCountryCodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CountryFlag flag)
        {
            flag.UpdateVisuals();
        }
    }

    private void UpdateVisuals()
    {
        var code = CountryCode?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(code) || code == "auto")
        {
            AutoIcon.Visibility = Visibility.Visible;
            FlagBorder.ClearValue(Border.BackgroundProperty);
            return;
        }

        if (code.Length == 2)
        {
            try
            {
                var image = _imageCache.GetOrAdd(code, c =>
                {
                    var uri = new Uri($"pack://application:,,,/Resources/Flags/{c}.png", UriKind.Absolute);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = uri;
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                });

                var brush = new ImageBrush(image)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
                brush.Freeze();

                FlagBorder.Background = brush;
                AutoIcon.Visibility = Visibility.Collapsed;
                return;
            }
            catch
            {
                
            }
        }

        AutoIcon.Visibility = Visibility.Visible;
        FlagBorder.ClearValue(Border.BackgroundProperty);
    }
}
