using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Se7enPro.Controls;

public sealed class TrafficChartControl : FrameworkElement
{
    private const int SampleCapacity = 50;
    private const double BaselineHeightRatio = 0.78;
    private const double BaselineTopGap = 4.0;

    private readonly double[] _downHistory = new double[SampleCapacity];
    private readonly double[] _upHistory = new double[SampleCapacity];

    
    
    
    
    private readonly Point[] _downPoints = new Point[SampleCapacity];
    private readonly Point[] _upPoints = new Point[SampleCapacity];

    private readonly DispatcherTimer _sampleTimer;
    private bool _idleRendered;
    private Window? _hostWindow;

    
    
    private static readonly Color DownColor = Color.FromRgb(0x00, 0xD4, 0xFF);
    private static readonly Color UpColor = Color.FromRgb(0x7C, 0x3A, 0xED);
    private static readonly Brush WhiteDotBrush = MakeFrozenBrush(Colors.White);

    private static readonly Brush DownAreaBrush = MakeCurveAreaBrush(DownColor, 0x38);
    private static readonly Brush UpAreaBrush = MakeCurveAreaBrush(UpColor, 0x29);
    private static readonly Pen DownLinePen = MakeFrozenPen(MakeFrozenBrush(DownColor), 1.8);
    private static readonly Pen UpLinePen = MakeFrozenPen(MakeFrozenBrush(UpColor), 1.5);
    private static readonly Brush DownDotGlow = MakeFrozenBrush(DownColor, 0.25);
    private static readonly Brush DownDotMid = MakeFrozenBrush(DownColor, 0.65);
    private static readonly Brush UpDotGlow = MakeFrozenBrush(UpColor, 0.25);
    private static readonly Brush UpDotMid = MakeFrozenBrush(UpColor, 0.65);

    
    private Pen? _gridPen;
    private Brush? _labelBrush;

    
    
    
    
    private Typeface? _labelTypeface;
    private FontFamily? _labelFontFamily;

    private static readonly string[] SpeedUnits = { "KB/s", "MB/s", "GB/s" };

    private static Brush MakeFrozenBrush(Color color, double opacity = 1.0)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

        private static Brush MakeCurveAreaBrush(Color color, byte alpha)
    {
        var brush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(alpha, color.R, color.G, color.B), 0.0),
                new(Color.FromArgb(0x00, color.R, color.G, color.B), 1.0)
            },
            new Point(0, 0),
            new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private static Pen MakeFrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    public static readonly DependencyProperty DownSpeedProperty =
        DependencyProperty.Register(
            nameof(DownSpeed),
            typeof(double),
            typeof(TrafficChartControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnSpeedChanged));

    public double DownSpeed
    {
        get => (double)GetValue(DownSpeedProperty);
        set => SetValue(DownSpeedProperty, value);
    }

    public static readonly DependencyProperty UpSpeedProperty =
        DependencyProperty.Register(
            nameof(UpSpeed),
            typeof(double),
            typeof(TrafficChartControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnSpeedChanged));

    public double UpSpeed
    {
        get => (double)GetValue(UpSpeedProperty);
        set => SetValue(UpSpeedProperty, value);
    }

    public static readonly DependencyProperty PeakSpeedProperty =
        DependencyProperty.Register(
            nameof(PeakSpeed),
            typeof(double),
            typeof(TrafficChartControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PeakSpeed
    {
        get => (double)GetValue(PeakSpeedProperty);
        set => SetValue(PeakSpeedProperty, value);
    }

    public static readonly DependencyProperty IsConnectedProperty =
        DependencyProperty.Register(
            nameof(IsConnected),
            typeof(bool),
            typeof(TrafficChartControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsConnectedChanged));

    public bool IsConnected
    {
        get => (bool)GetValue(IsConnectedProperty);
        set => SetValue(IsConnectedProperty, value);
    }

    private static void OnSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrafficChartControl control)
        {
            control.InvalidateVisual();
        }
    }

    private static void OnIsConnectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrafficChartControl control)
        {
            if (!(bool)e.NewValue)
            {
                control.ResetHistory();
            }
            control._idleRendered = false;
            control.InvalidateVisual();
        }
    }

    public TrafficChartControl()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _sampleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1) 
        };
        _sampleTimer.Tick += OnSampleTimerTick;

        Loaded += (_, _) => _sampleTimer.Start();
        Unloaded += (_, _) => _sampleTimer.Stop();
    }

    private void OnSampleTimerTick(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        _hostWindow ??= Window.GetWindow(this);
        if (_hostWindow is { WindowState: WindowState.Minimized } or { IsVisible: false }) return;

        
        Array.Copy(_downHistory, 1, _downHistory, 0, SampleCapacity - 1);
        Array.Copy(_upHistory, 1, _upHistory, 0, SampleCapacity - 1);

        if (IsConnected)
        {
            _downHistory[SampleCapacity - 1] = DownSpeed;
            _upHistory[SampleCapacity - 1] = UpSpeed;
        }
        else
        {
            _downHistory[SampleCapacity - 1] = 0.0;
            _upHistory[SampleCapacity - 1] = 0.0;
        }

        
        
        
        if (!IsConnected && !ContainsAnySignal(_downHistory) && !ContainsAnySignal(_upHistory))
        {
            if (_idleRendered) return;
            _idleRendered = true;
        }
        else
        {
            _idleRendered = false;
        }

        InvalidateVisual();
    }

    private static bool ContainsAnySignal(double[] samples)
    {
        foreach (var sample in samples)
        {
            if (sample > 0) return true;
        }
        return false;
    }

    private void ResetHistory()
    {
        Array.Clear(_downHistory, 0, SampleCapacity);
        Array.Clear(_upHistory, 0, SampleCapacity);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        
        dc.DrawLine(GetGridPen(), new Point(0, height * 0.33), new Point(width, height * 0.33));
        dc.DrawLine(GetGridPen(), new Point(0, height * 0.66), new Point(width, height * 0.66));

        
        
        
        
        double maxRate = Math.Max(PeakSpeed, Math.Max(DownSpeed, UpSpeed));
        for (int i = 0; i < SampleCapacity; i++)
        {
            if (_downHistory[i] > maxRate) maxRate = _downHistory[i];
            if (_upHistory[i] > maxRate) maxRate = _upHistory[i];
        }
        if (maxRate < 1024.0) maxRate = 1024.0;

        
        if (IsConnected && maxRate > 1024.0)
        {
            
            
            double pixelsPerDip;
            try
            {
                pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            }
            catch
            {
                pixelsPerDip = 1.0;
            }

            DrawGridLabel(dc, FormatSpeed(maxRate * 0.66), height * 0.33, width, pixelsPerDip);
            DrawGridLabel(dc, FormatSpeed(maxRate * 0.33), height * 0.66, width, pixelsPerDip);
        }

        PaintCurve(dc, width, height, _downHistory, _downPoints, maxRate, isDownload: true);
        PaintCurve(dc, width, height, _upHistory, _upPoints, maxRate, isDownload: false);
    }

        private void PaintCurve(DrawingContext dc, double width, double height, double[] data, Point[] points, double maxVal, bool isDownload)
    {
        double waveSpan = height * BaselineHeightRatio;

        for (int i = 0; i < SampleCapacity; i++)
        {
            double x = i / (double)(SampleCapacity - 1) * width;
            double val = data[i];

            double ratio = Math.Clamp(val / maxVal, 0.0, 1.0);
            double powerScaled = Math.Pow(ratio, 0.45);

            
            double carrierPhase = i / (double)(SampleCapacity - 1) * 3 * Math.PI;
            double baselinePulse = 0.06 + 0.03 * Math.Sin(carrierPhase);

            double normalized = val > 0
                ? Math.Clamp(powerScaled * 0.88 + 0.06, 0.06, 0.95)
                : Math.Clamp(baselinePulse, 0.03, 0.12);

            double y = height - (normalized * waveSpan) - BaselineTopGap;
            points[i] = new Point(x, y);
        }

        
        var lineGeo = new StreamGeometry();
        using (var ctx = lineGeo.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            AppendSmoothSegments(ctx, points);
        }
        lineGeo.Freeze();

        
        var areaGeo = new StreamGeometry();
        using (var ctx = areaGeo.Open())
        {
            ctx.BeginFigure(new Point(0, height), true, true);
            ctx.LineTo(points[0], true, false);
            AppendSmoothSegments(ctx, points);
            ctx.LineTo(new Point(width, height), true, false);
        }
        areaGeo.Freeze();

        dc.DrawGeometry(isDownload ? DownAreaBrush : UpAreaBrush, null, areaGeo);
        dc.DrawGeometry(null, isDownload ? DownLinePen : UpLinePen, lineGeo);

        
        var last = points[^1];
        dc.DrawEllipse(isDownload ? DownDotGlow : UpDotGlow, null, last, 5.0, 5.0);
        dc.DrawEllipse(isDownload ? DownDotMid : UpDotMid, null, last, 3.0, 3.0);
        dc.DrawEllipse(WhiteDotBrush, null, last, 1.8, 1.8);
    }

    private static void AppendSmoothSegments(StreamGeometryContext ctx, Point[] points)
    {
        for (int i = 0; i < points.Length - 1; i++)
        {
            double cpx = (points[i].X + points[i + 1].X) / 2;
            ctx.BezierTo(
                new Point(cpx, points[i].Y),
                new Point(cpx, points[i + 1].Y),
                points[i + 1],
                true, false);
        }
    }

    private Pen GetGridPen()
    {
        var color = ResolveResourceColor("Surface.CardBorder") ?? Colors.White;
        var target = Color.FromArgb((byte)(0xFF * 0.18), color.R, color.G, color.B);
        if (_gridPen is null || ((SolidColorBrush)_gridPen.Brush).Color != target)
        {
            _gridPen = MakeFrozenPen(MakeFrozenBrush(target), 1.0);
        }
        return _gridPen;
    }

    private void DrawGridLabel(DrawingContext dc, string text, double y, double width, double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            GetLabelTypeface(),
            8.0,
            GetLabelBrush(),
            pixelsPerDip);

        dc.DrawText(formatted, new Point(width - formatted.Width - 6, y - formatted.Height - 2));
    }

        private Typeface GetLabelTypeface()
    {
        var family = Application.Current?.TryFindResource("UI.MonoFontFamily") as FontFamily
                     ?? new FontFamily("Consolas");

        if (_labelTypeface is null || !ReferenceEquals(_labelFontFamily, family))
        {
            _labelFontFamily = family;
            _labelTypeface = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        }
        return _labelTypeface;
    }

    private Brush GetLabelBrush()
    {
        var color = ResolveResourceColor("TextMutedBrush") ?? Color.FromRgb(0x88, 0x88, 0x88);
        var target = Color.FromArgb((byte)(0xFF * 0.60), color.R, color.G, color.B);
        if (_labelBrush is null || ((SolidColorBrush)_labelBrush).Color != target)
        {
            var brush = new SolidColorBrush(target);
            brush.Freeze();
            _labelBrush = brush;
        }
        return _labelBrush;
    }

    private static Color? ResolveResourceColor(string key)
    {
        var value = Application.Current?.TryFindResource(key);
        if (value is Color color) return color;
        if (value is SolidColorBrush brush) return brush.Color;
        return null;
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "0 B/s";
        if (bytesPerSec < 1024) return $"{bytesPerSec:0} B/s";
        double v = bytesPerSec;
        int i = -1;
        do { v /= 1024.0; i++; } while (v >= 1024.0 && i < SpeedUnits.Length - 1);
        return v >= 100 ? $"{v:0} {SpeedUnits[i]}" : $"{v:0.0} {SpeedUnits[i]}";
    }
}
