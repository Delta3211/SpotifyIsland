using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SpotifyIsland.Controls;

/// <summary>
/// A TextBlock that smoothly scrolls its text horizontally when the content
/// is wider than the available width, like a marquee display.
/// Stops scrolling when the text fits comfortably without clipping.
/// </summary>
public partial class MarqueeTextBlock : UserControl
{
    // ── Dependency Properties ──────────────────────────────────────────────

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(MarqueeTextBlock),
            new PropertyMetadata(string.Empty, OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty TextFontSizeProperty =
        DependencyProperty.Register(nameof(TextFontSize), typeof(double), typeof(MarqueeTextBlock),
            new PropertyMetadata(13.0, OnStyleChanged));

    public double TextFontSize
    {
        get => (double)GetValue(TextFontSizeProperty);
        set => SetValue(TextFontSizeProperty, value);
    }

    public static readonly DependencyProperty TextFontWeightProperty =
        DependencyProperty.Register(nameof(TextFontWeight), typeof(FontWeight), typeof(MarqueeTextBlock),
            new PropertyMetadata(FontWeights.Normal, OnStyleChanged));

    public FontWeight TextFontWeight
    {
        get => (FontWeight)GetValue(TextFontWeightProperty);
        set => SetValue(TextFontWeightProperty, value);
    }

    public static readonly DependencyProperty TextForegroundProperty =
        DependencyProperty.Register(nameof(TextForeground), typeof(Brush), typeof(MarqueeTextBlock),
            new PropertyMetadata(Brushes.White, OnStyleChanged));

    public Brush TextForeground
    {
        get => (Brush)GetValue(TextForegroundProperty);
        set => SetValue(TextForegroundProperty, value);
    }

    // ── Private state ──────────────────────────────────────────────────────

    private readonly DispatcherTimer _startDelayTimer;
    private bool _isScrolling = false;
    private double _textWidth = 0;

    // px/sec scroll speed
    private const double ScrollSpeed = 38.0;
    // gap between the end of text and the start of the looped copy
    private const double LoopGap = 48.0;
    // minimum overflow before marquee kicks in (avoids jitter on barely-too-long text)
    private const double OverflowThreshold = 8.0;
    // pause at start before scrolling begins (ms)
    private const double StartDelayMs = 1800.0;

    public MarqueeTextBlock()
    {
        InitializeComponent();

        _startDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StartDelayMs) };
        _startDelayTimer.Tick += (s, e) =>
        {
            _startDelayTimer.Stop();
            StartScrolling();
        };
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyStyle();
        SizeChanged += OnSizeChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        StopScrolling();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Refresh();

    // ── Callbacks ──────────────────────────────────────────────────────────

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarqueeTextBlock m) m.Refresh();
    }

    private static void OnStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarqueeTextBlock m) { m.ApplyStyle(); m.Refresh(); }
    }

    // ── Core logic ─────────────────────────────────────────────────────────

    private void ApplyStyle()
    {
        foreach (var lbl in new[] { Label1, Label2 })
        {
            lbl.FontSize   = TextFontSize;
            lbl.FontWeight = TextFontWeight;
            lbl.Foreground = TextForeground;
            lbl.Text       = Text;
        }
    }

    private void Refresh()
    {
        StopScrolling();

        if (ActualWidth <= 0) return;

        Label1.Text = Text;
        Label2.Text = Text;

        // Measure natural text width
        Label1.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _textWidth = Label1.DesiredSize.Width;

        double available = ActualWidth;
        double overflow  = _textWidth - available;

        // Reset positions
        Canvas.SetLeft(Label1, 0);
        Canvas.SetLeft(Label2, _textWidth + LoopGap);
        Label2.Visibility = Visibility.Collapsed;

        MarqueeCanvas.Width  = available;
        MarqueeCanvas.Height = Label1.DesiredSize.Height > 0 ? Label1.DesiredSize.Height : ActualHeight;

        if (overflow > OverflowThreshold)
        {
            // Kick off scroll after a short pause
            _startDelayTimer.Stop();
            _startDelayTimer.Start();
        }
    }

    private void StartScrolling()
    {
        if (_isScrolling || ActualWidth <= 0 || _textWidth <= 0) return;
        _isScrolling = true;

        double loopDistance = _textWidth + LoopGap;
        double durationSecs = loopDistance / ScrollSpeed;

        Label2.Visibility = Visibility.Visible;

        // Both labels translate left by loopDistance, then snap back — seamless loop
        var anim1 = new DoubleAnimation
        {
            From           = 0,
            To             = -loopDistance,
            Duration       = TimeSpan.FromSeconds(durationSecs),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = null // linear
        };

        var anim2 = new DoubleAnimation
        {
            From           = loopDistance,
            To             = 0,
            Duration       = TimeSpan.FromSeconds(durationSecs),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = null
        };

        Label1.BeginAnimation(Canvas.LeftProperty, anim1);
        Label2.BeginAnimation(Canvas.LeftProperty, anim2);
    }

    private void StopScrolling()
    {
        _isScrolling = false;
        _startDelayTimer.Stop();
        Label1.BeginAnimation(Canvas.LeftProperty, null);
        Label2.BeginAnimation(Canvas.LeftProperty, null);
        Canvas.SetLeft(Label1, 0);
        Canvas.SetLeft(Label2, 0);
        Label2.Visibility = Visibility.Collapsed;
    }
}
