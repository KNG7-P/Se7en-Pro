using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Se7enPro.Controls;

public sealed class EqualizerSpectrumControl : FrameworkElement
{
    private readonly DispatcherTimer _animationTimer;
    private double _phase;

    
    
    
    private static readonly SolidColorBrush CyanBrush = MakeFrozen("#FF00D4FF");
    private static readonly SolidColorBrush CyanSoftBrush = MakeFrozen("#5500D4FF");
    private static readonly SolidColorBrush PurpleBrush = MakeFrozen("#FFA78BFA");
    private static readonly SolidColorBrush PurpleSoftBrush = MakeFrozen("#55A78BFA");

    private static SolidColorBrush MakeFrozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }

    public static readonly DependencyProperty IsConnectedProperty =
        DependencyProperty.Register(
            nameof(IsConnected),
            typeof(bool),
            typeof(EqualizerSpectrumControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsConnectedChanged));

    private static void OnIsConnectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is EqualizerSpectrumControl control)
        {
            control.UpdateAnimationState();
        }
    }

    public bool IsConnected
    {
        get => (bool)GetValue(IsConnectedProperty);
        set => SetValue(IsConnectedProperty, value);
    }

    public static readonly DependencyProperty DownSpeedProperty =
        DependencyProperty.Register(
            nameof(DownSpeed),
            typeof(double),
            typeof(EqualizerSpectrumControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double DownSpeed
    {
        get => (double)GetValue(DownSpeedProperty);
        set => SetValue(DownSpeedProperty, value);
    }

    public static readonly DependencyProperty UpSpeedProperty =
        DependencyProperty.Register(
            nameof(UpSpeed),
            typeof(double),
            typeof(EqualizerSpectrumControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double UpSpeed
    {
        get => (double)GetValue(UpSpeedProperty);
        set => SetValue(UpSpeedProperty, value);
    }

    public static readonly DependencyProperty PeakSpeedProperty =
        DependencyProperty.Register(
            nameof(PeakSpeed),
            typeof(double),
            typeof(EqualizerSpectrumControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PeakSpeed
    {
        get => (double)GetValue(PeakSpeedProperty);
        set => SetValue(PeakSpeedProperty, value);
    }

    private const double BasePhaseStep = 0.08; 

    private static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(50); 
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(120);  

    private Window? _hostWindow;
    private bool _lifetimeHooksAttached;
    private double _phaseStep = BasePhaseStep;
    private Pen? _gridPen;

    public EqualizerSpectrumControl()
    {
        
        
        _animationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = IdleInterval
        };
        _animationTimer.Tick += OnAnimationTick;

        Loaded += (_, _) => AttachLifetimeHooks();
        Unloaded += (_, _) => DetachLifetimeHooks();
    }

        private void UpdateAnimationState()
    {
        var renderable = IsVisible && _hostWindow is { WindowState: not WindowState.Minimized };
        if (!renderable)
        {
            _animationTimer.Stop();
            return;
        }

        
        
        var interval = IsConnected ? ActiveInterval : IdleInterval;
        _phaseStep = BasePhaseStep * (interval.TotalMilliseconds / ActiveInterval.TotalMilliseconds);

        if (_animationTimer.IsEnabled && _animationTimer.Interval == interval)
            return;

        _animationTimer.Interval = interval;
        _animationTimer.Start();
    }

    private void AttachLifetimeHooks()
    {
        if (_lifetimeHooksAttached) return;
        _lifetimeHooksAttached = true;

        IsVisibleChanged += OnIsVisibleChanged;
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.StateChanged += OnHostWindowStateChanged;
        }

        UpdateAnimationState();
    }

    private void DetachLifetimeHooks()
    {
        if (!_lifetimeHooksAttached) return;
        _lifetimeHooksAttached = false;

        IsVisibleChanged -= OnIsVisibleChanged;
        if (_hostWindow is not null)
        {
            _hostWindow.StateChanged -= OnHostWindowStateChanged;
            _hostWindow = null;
        }

        _animationTimer.Stop();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => UpdateAnimationState();

    private void OnHostWindowStateChanged(object? sender, EventArgs e)
        => UpdateAnimationState();

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            
            UpdateAnimationState();
            return;
        }

        _phase = (_phase + _phaseStep) % (2 * Math.PI);
        InvalidateVisual();
    }

        private Pen GetGridPen()
    {
        var resource = Application.Current?.TryFindResource("Surface.CardBorder") as SolidColorBrush;
        var target = resource?.Color ?? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
        if (_gridPen is null || ((SolidColorBrush)_gridPen.Brush).Color != target)
        {
            var brush = new SolidColorBrush(target);
            brush.Freeze();
            var pen = new Pen(brush, 1.0);
            pen.Freeze();
            _gridPen = pen;
        }

        return _gridPen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 10 || h <= 10) return;

        
        var gridPen = GetGridPen();
        dc.DrawLine(gridPen, new Point(0, h * 0.33), new Point(w, h * 0.33));
        dc.DrawLine(gridPen, new Point(0, h * 0.66), new Point(w, h * 0.66));

        const int barCount = 24;
        const double padX = 6.0;
        var slotW = (w - padX * 2) / barCount;
        if (slotW <= 1) return;

        if (!IsConnected)
        {
            
            var barW = Math.Clamp(slotW * 0.65, 2.5, 8.0);
            for (var i = 0; i < barCount; i++)
            {
                var x = padX + i * slotW + (slotW - barW) / 2.0;
                var wave = Math.Sin((i / (double)barCount) * 3 * Math.PI + _phase) * 0.5 + 0.5;
                var barH = (h * 0.12) + wave * (h * 0.28);
                var y = h - barH - 2;

                var brush = (i % 2 == 0) ? CyanSoftBrush : PurpleSoftBrush;
                dc.DrawRoundedRectangle(brush, null, new Rect(x, y, barW, barH), 2, 2);
            }
            return;
        }

        
        var effectivePeak = Math.Max(PeakSpeed, Math.Max(DownSpeed, Math.Max(UpSpeed, 1024.0 * 256)));
        var dlRatio = Math.Clamp(DownSpeed / effectivePeak, 0.0, 1.0);
        var ulRatio = Math.Clamp(UpSpeed / effectivePeak, 0.0, 1.0);

        var dlSpeedFactor = DownSpeed <= 0 ? 0.08 : Math.Pow(dlRatio, 0.35);
        var ulSpeedFactor = UpSpeed <= 0 ? 0.08 : Math.Pow(ulRatio, 0.35);

        var barTotalW = Math.Clamp(slotW * 0.78, 3.0, 12.0);
        var subBarW = Math.Clamp((barTotalW - 1.2) / 2.0, 1.2, 5.5);

        for (var i = 0; i < barCount; i++)
        {
            var slotStartX = padX + i * slotW + (slotW - barTotalW) / 2.0;
            var normX = (i + 0.5) / barCount;
            var bell = Math.Sin(normX * Math.PI);

            
            var h1 = Math.Sin(_phase * 3.6 + i * 0.45);
            var h2 = Math.Cos(_phase * 2.2 - i * 0.70);
            var dlHarmonic = Math.Clamp(0.50 + 0.30 * h1 + 0.12 * h2, 0.20, 1.0);

            var uh1 = Math.Sin(-_phase * 3.2 + i * 0.50);
            var uh2 = Math.Cos(_phase * 1.9 + i * 0.60);
            var ulHarmonic = Math.Clamp(0.50 + 0.30 * uh1 + 0.15 * uh2, 0.20, 1.0);

            var dlH = Math.Clamp(h * 0.85 * (bell * 0.45 + 0.55) * dlHarmonic * dlSpeedFactor, 3.0, h - 4);
            var ulH = Math.Clamp(h * 0.85 * (bell * 0.45 + 0.55) * ulHarmonic * ulSpeedFactor, 3.0, h - 4);

            
            var dlX = slotStartX;
            var dlY = h - dlH - 2;
            dc.DrawRoundedRectangle(CyanBrush, null, new Rect(dlX, dlY, subBarW, dlH), 1.5, 1.5);

            
            var ulX = slotStartX + subBarW + 1.2;
            var ulY = h - ulH - 2;
            dc.DrawRoundedRectangle(PurpleBrush, null, new Rect(ulX, ulY, subBarW, ulH), 1.5, 1.5);
        }
    }
}
