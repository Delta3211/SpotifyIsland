using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

using SpotifyIsland.Services;

namespace SpotifyIsland.Controls;

public partial class VisualizerControl : UserControl
{
    public static AudioCaptureService? SharedAudioCapture { get; set; }

    private const int BarCount = 18;
    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _currentHeights = new double[BarCount];
    private readonly double[] _targetHeights = new double[BarCount];
    private readonly double[] _phaseOffsets = new double[BarCount];
    private readonly float[] _audioLevels = new float[BarCount];
    private readonly Random _rand = new();
    private double _time;
    private bool _isRendering = false;

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(VisualizerControl),
            new PropertyMetadata(false));

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(nameof(AccentColor), typeof(Color), typeof(VisualizerControl),
            new PropertyMetadata(Color.FromRgb(30, 215, 96), OnAccentColorChanged));

    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public VisualizerControl()
    {
        InitializeComponent();
        InitializeBars();
    }

    private void InitializeBars()
    {
        VisualizerCanvas.Children.Clear();
        for (int i = 0; i < BarCount; i++)
        {
            _phaseOffsets[i] = _rand.NextDouble() * Math.PI * 2;
            _currentHeights[i] = 4;
            _targetHeights[i] = 4;

            var rect = new Rectangle
            {
                RadiusX = 1.5,
                RadiusY = 1.5,
                Width = 3,
                Height = 4,
                Fill = new SolidColorBrush(AccentColor)
            };

            _bars[i] = rect;
            VisualizerCanvas.Children.Add(rect);
        }
        UpdateBarBrushes();
    }

    private static void OnAccentColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VisualizerControl vc)
        {
            vc.UpdateBarBrushes();
        }
    }

    private void UpdateBarBrushes()
    {
        var accent = AccentColor;
        var lighter = Color.FromRgb(
            (byte)Math.Clamp(accent.R + 40, 0, 255),
            (byte)Math.Clamp(accent.G + 40, 0, 255),
            (byte)Math.Clamp(accent.B + 40, 0, 255)
        );

        var brush = new LinearGradientBrush(accent, lighter, new Point(0, 1), new Point(0, 0));
        brush.Freeze();

        foreach (var bar in _bars)
        {
            if (bar != null)
            {
                bar.Fill = brush;
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isRendering)
        {
            _isRendering = true;
            CompositionTarget.Rendering += OnCompositionRendering;
        }
        ArrangeBars();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isRendering)
        {
            _isRendering = false;
            CompositionTarget.Rendering -= OnCompositionRendering;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ArrangeBars();
    }

    private void ArrangeBars()
    {
        double totalWidth = ActualWidth > 0 ? ActualWidth : 80;
        double totalHeight = ActualHeight > 0 ? ActualHeight : 24;

        double barWidth = 3;
        double spacing = (totalWidth - (BarCount * barWidth)) / Math.Max(1, BarCount - 1);
        spacing = Math.Clamp(spacing, 1.5, 6);

        for (int i = 0; i < BarCount; i++)
        {
            double x = i * (barWidth + spacing);
            Canvas.SetLeft(_bars[i], x);
            Canvas.SetBottom(_bars[i], 0);
            _bars[i].Width = barWidth;
        }
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        _time += 0.08;
        double maxHeight = ActualHeight > 0 ? ActualHeight : 24;
        bool playing = IsPlaying;

        bool hasLiveAudio = false;
        if (SharedAudioCapture != null && SharedAudioCapture.IsActive)
        {
            SharedAudioCapture.GetFrequencyLevels(_audioLevels);
            float sum = 0f;
            for (int j = 0; j < BarCount; j++) sum += _audioLevels[j];
            if (sum > 0.03f) hasLiveAudio = true;
        }

        for (int i = 0; i < BarCount; i++)
        {
            if (playing)
            {
                if (hasLiveAudio)
                {
                    // Live real-time audio spectrum
                    double target = Math.Clamp(_audioLevels[i] * maxHeight * 1.25, 4.0, maxHeight);
                    _targetHeights[i] = target;
                }
                else
                {
                    // Multi-frequency wave synthesis with randomized pulse (fallback)
                    double wave1 = Math.Sin(_time * 2.2 + _phaseOffsets[i]);
                    double wave2 = Math.Cos(_time * 3.8 + i * 0.5);
                    double wave3 = Math.Sin(_time * 1.2 + i * 0.8);

                    double rhythm = Math.Abs(wave1 * 0.5 + wave2 * 0.35 + wave3 * 0.25);
                    double bandWeight = 1.0 - (i / (double)BarCount) * 0.25;
                    if (i < 3) bandWeight *= 1.35;

                    double noise = (_rand.NextDouble() - 0.5) * 0.15;
                    double target = (rhythm * bandWeight + noise) * maxHeight;
                    _targetHeights[i] = Math.Clamp(target, 4, maxHeight);
                }
            }
            else
            {
                // Calm resting wave when paused
                double idleWave = (Math.Sin(_time * 0.8 + i * 0.4) + 1.0) * 0.5;
                _targetHeights[i] = 3.0 + idleWave * 2.0;
            }

            // Smooth spring lerp
            _currentHeights[i] += (_targetHeights[i] - _currentHeights[i]) * 0.32;
            _bars[i].Height = Math.Max(2, _currentHeights[i]);
            Canvas.SetBottom(_bars[i], 0);
        }
    }
}
