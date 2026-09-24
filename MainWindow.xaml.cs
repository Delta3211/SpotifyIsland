using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SpotifyIsland.Models;
using SpotifyIsland.Services;

namespace SpotifyIsland;

public partial class MainWindow : Window
{
    private readonly SpotifyMediaService _mediaService;
    private readonly AudioCaptureService _audioCapture;
    private readonly SystemAudioService _systemAudio;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _bloomRetractTimer;
    private readonly DispatcherTimer _volumeHudTimer;
    private readonly DispatcherTimer _peekRetractTimer;
    private Storyboard? _spinStoryboard;

    private bool _isExpanded = false;
    private bool _isPinned = false;
    private bool _isUserSeeking = false;
    private bool _isAutoBloomEnabled = true;
    private string _lastTrackSignature = "";
    private IntPtr _windowHandle;
    private HwndSource? _hwndSource;

    private EdgeDockState _dockState = EdgeDockState.TopCenter;
    private bool _isPeekTucked = false;

    private readonly DispatcherTimer _spotifyMonitorTimer = new DispatcherTimer();
    private bool _lastSpotifyWasMinimized = false;
    private bool _lastSpotifyIsOpen = false;
    private bool _isFavorite = false;
    private bool _isShuffle = false;
    private int _repeatState = 0;

    private enum EdgeDockState
    {
        None,
        TopCenter,
        LeftPeek,
        RightPeek
    }

    private const int HOTKEY_ID_PLAYPAUSE = 9001;
    private const int HOTKEY_ID_NEXT = 9002;
    private const int HOTKEY_ID_PREV = 9003;
    private const int HOTKEY_ID_VOLUP = 9004;
    private const int HOTKEY_ID_VOLDOWN = 9005;

    public MainWindow()
    {
        InitializeComponent();

        _mediaService = new SpotifyMediaService();
        _mediaService.TrackUpdated += OnTrackUpdated;
        _mediaService.PositionUpdated += OnPositionUpdated;

        // Initialize real system audio spectrum capture
        _audioCapture = new AudioCaptureService();
        Controls.VisualizerControl.SharedAudioCapture = _audioCapture;
        _audioCapture.Start();

        // Initialize real system audio volume tracking
        _systemAudio = new SystemAudioService();
        _systemAudio.VolumeChanged += (pct, muted) =>
        {
            Dispatcher.Invoke(() => ShowVolumeHud(pct, muted));
        };

        _volumeHudTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1600)
        };
        _volumeHudTimer.Tick += (s, e) =>
        {
            _volumeHudTimer.Stop();
            HideVolumeHud();
        };

        _collapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1600)
        };
        _collapseTimer.Tick += (s, e) =>
        {
            _collapseTimer.Stop();
            if (!_isPinned && !IsMouseOver)
            {
                CollapseIsland();
            }
        };

        // Bloom notification timer (retracts after 3.8s)
        _bloomRetractTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(3800)
        };
        _bloomRetractTimer.Tick += (s, e) =>
        {
            _bloomRetractTimer.Stop();
            NowPlayingBadge.Visibility = Visibility.Collapsed;
            if (!_isPinned && !IsMouseOver)
            {
                CollapseIsland();
            }
        };

        // Smooth Edge Peek Retract Timer (debounces tucking into arrow tab)
        _peekRetractTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _peekRetractTimer.Tick += (s, e) =>
        {
            _peekRetractTimer.Stop();
            if (!_isPinned && !IsMouseOver && !_isUserSeeking)
            {
                if (_dockState == EdgeDockState.LeftPeek || _dockState == EdgeDockState.RightPeek)
                {
                    RetractIslandToPeek();
                }
            }
        };

        InitializeVinylSpin();

        // ── Spotify Window Monitor ────────────────────────────────────
        // Poll every 1.5 s to track whether Spotify is running / minimized.
        // When Spotify opens  → slide the Island in from the top.
        // When Spotify closes → slide the Island out.
        _spotifyMonitorTimer.Interval = TimeSpan.FromMilliseconds(1500);
        _spotifyMonitorTimer.Tick += OnSpotifyMonitorTick;
        _spotifyMonitorTimer.Start();
    }

    private void InitializeVinylSpin()
    {
        var spinAnim = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(3.5),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(spinAnim, MiniCoverImage);
        Storyboard.SetTargetProperty(spinAnim, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));

        _spinStoryboard = new Storyboard();
        _spinStoryboard.Children.Add(spinAnim);
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        SnapToTopCenter();
        await _mediaService.InitializeAsync();
    }

    private void ExportSnapshots()
    {
        try
        {
            string artifactDir = @"C:\Users\Delta\.gemini\antigravity-ide\brain\3aea434e-660f-4344-ac10-4d9457bf5f27";
            if (!System.IO.Directory.Exists(artifactDir))
            {
                System.IO.Directory.CreateDirectory(artifactDir);
            }

            // Update layout before rendering
            UpdateLayout();

            // 1. Render Compact Pill
            SaveVisualToPng(this, System.IO.Path.Combine(artifactDir, "spotify_island_compact.png"));

            // 2. Expand for snapshot
            ExpandIsland();
            UpdateLayout();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                try
                {
                    UpdateLayout();
                    SaveVisualToPng(this, System.IO.Path.Combine(artifactDir, "spotify_island_expanded.png"));
                }
                catch { }
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Snapshot error: {ex.Message}");
        }
    }

    private static void SaveVisualToPng(Visual visual, string filePath)
    {
        if (visual is Window win)
        {
            int w = (int)Math.Max(1, win.ActualWidth > 0 ? win.ActualWidth : 640);
            int h = (int)Math.Max(1, win.ActualHeight > 0 ? win.ActualHeight : 300);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(win);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(filePath);
            encoder.Save(fs);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowHandle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_windowHandle);
        _hwndSource?.AddHook(HwndHook);

        RegisterGlobalHotkeys();
    }

    private void RegisterGlobalHotkeys()
    {
        if (_windowHandle == IntPtr.Zero) return;

        try
        {
            // Ctrl + Alt + Space: Play/Pause
            NativeMethods.RegisterHotKey(_windowHandle, HOTKEY_ID_PLAYPAUSE,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, NativeMethods.VK_SPACE);

            // Ctrl + Alt + Right: Next
            NativeMethods.RegisterHotKey(_windowHandle, HOTKEY_ID_NEXT,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, NativeMethods.VK_RIGHT);

            // Ctrl + Alt + Left: Prev
            NativeMethods.RegisterHotKey(_windowHandle, HOTKEY_ID_PREV,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, NativeMethods.VK_LEFT);

            // Ctrl + Alt + Up: Volume Up
            NativeMethods.RegisterHotKey(_windowHandle, HOTKEY_ID_VOLUP,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, NativeMethods.VK_UP);

            // Ctrl + Alt + Down: Volume Down
            NativeMethods.RegisterHotKey(_windowHandle, HOTKEY_ID_VOLDOWN,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, NativeMethods.VK_DOWN);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to register hotkeys: {ex.Message}");
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int hotkeyId = wParam.ToInt32();
            switch (hotkeyId)
            {
                case HOTKEY_ID_PLAYPAUSE:
                    _ = _mediaService.TogglePlayPauseAsync();
                    handled = true;
                    break;
                case HOTKEY_ID_NEXT:
                    _ = _mediaService.SkipNextAsync();
                    handled = true;
                    break;
                case HOTKEY_ID_PREV:
                    _ = _mediaService.SkipPreviousAsync();
                    handled = true;
                    break;
                case HOTKEY_ID_VOLUP:
                    _systemAudio.StepVolumeUp(0.04f);
                    handled = true;
                    break;
                case HOTKEY_ID_VOLDOWN:
                    _systemAudio.StepVolumeDown(0.04f);
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void SnapToTopCenter()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = (screenWidth - Width) / 2.0;
        Top = 10;
        _dockState = EdgeDockState.TopCenter;
        _isPeekTucked = false;
        _peekRetractTimer.Stop();

        if (LeftPeekTab != null) LeftPeekTab.Visibility = Visibility.Collapsed;
        if (RightPeekTab != null) RightPeekTab.Visibility = Visibility.Collapsed;
        if (IslandContainer != null)
        {
            IslandContainer.Visibility = Visibility.Visible;
            IslandContainer.HorizontalAlignment = HorizontalAlignment.Center;
            IslandContainer.Margin = new Thickness(0, 0, 0, 0);
            IslandTransform.X = 0;
            IslandContainer.Opacity = 1;
        }
    }

    // ==========================================
    // Media State Updates
    // ==========================================

    private void OnTrackUpdated(MediaTrackInfo track)
    {
        Dispatcher.Invoke(() =>
        {
            PillTitleText.Text = track.Title;
            PillArtistText.Text = track.Artist;
            LargeTitleText.Text = track.Title;
            LargeArtistText.Text = track.Artist;
            LargeAlbumText.Text = string.IsNullOrWhiteSpace(track.AlbumTitle) ? "Single" : track.AlbumTitle;

            MiniCoverImage.Source = track.Thumbnail;
            LargeCoverImage.Source = track.Thumbnail;

            TotalTimeText.Text = track.FormattedDuration;
            CurrentTimeText.Text = track.FormattedPosition;

            if (!_isUserSeeking && track.Duration.TotalSeconds > 0)
            {
                SeekSlider.Value = track.ProgressPercentage;
            }

            // Visualizer & Play State
            PillVisualizer.IsPlaying = track.IsPlaying;
            LargeVisualizer.IsPlaying = track.IsPlaying;
            PillVisualizer.AccentColor = track.AccentColor;
            LargeVisualizer.AccentColor = track.AccentColor;

            // Vinyl spinning
            if (track.IsPlaying)
            {
                _spinStoryboard?.Resume(this);
            }
            else
            {
                _spinStoryboard?.Pause(this);
            }

            // Update Play/Pause Button Visuals
            UpdatePlayPauseButton(track.IsPlaying);

            // Animate dynamic color shifts
            AnimatePalette(track.AccentColor, track.GlowColor, track.DarkColor);

            // Detect Track Change for Auto-Bloom
            string signature = $"{track.Title} - {track.Artist}";
            bool isNewSong = !string.IsNullOrEmpty(track.Title) && _lastTrackSignature != "" && signature != _lastTrackSignature;
            _lastTrackSignature = signature;

            if (isNewSong && _isAutoBloomEnabled && !_isExpanded)
            {
                TriggerBloomNotification();
            }

            // Update status indicator
            if (_mediaService.IsDemoMode)
            {
                DemoBadge.Visibility = Visibility.Visible;
                AppStatusText.Text = "SPOTIFY PREVIEW";
            }
            else
            {
                DemoBadge.Visibility = Visibility.Collapsed;
                AppStatusText.Text = "SPOTIFY LIVE";
            }
        });
    }

    private void OnPositionUpdated(TimeSpan position)
    {
        Dispatcher.Invoke(() =>
        {
            CurrentTimeText.Text = FormatTime(position);
            if (!_isUserSeeking && _mediaService.CurrentTrack.Duration.TotalSeconds > 0)
            {
                SeekSlider.Value = position.TotalSeconds / _mediaService.CurrentTrack.Duration.TotalSeconds;
            }
        });
    }

    private void UpdatePlayPauseButton(bool isPlaying)
    {
        if (PlayPauseButton.Template != null)
        {
            var playIcon = PlayPauseButton.Template.FindName("PlayIcon", PlayPauseButton) as Path;
            var pauseIcon = PlayPauseButton.Template.FindName("PauseIcon", PlayPauseButton) as Path;

            if (playIcon != null && pauseIcon != null)
            {
                playIcon.Visibility = isPlaying ? Visibility.Collapsed : Visibility.Visible;
                pauseIcon.Visibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void AnimatePalette(Color accent, Color glow, Color dark)
    {
        var duration = TimeSpan.FromMilliseconds(400);

        // Backdrop Glow Stop
        var glowAnim = new ColorAnimation(Color.FromArgb(50, glow.R, glow.G, glow.B), duration);
        GlowColorStop.BeginAnimation(GradientStop.ColorProperty, glowAnim);

        // Border Accent Glow Stop
        var borderAnim = new ColorAnimation(Color.FromArgb(70, accent.R, accent.G, accent.B), duration);
        BorderMidStop.BeginAnimation(GradientStop.ColorProperty, borderAnim);

        // DropShadow Glow
        var shadowAnim = new ColorAnimation(glow, duration);
        IslandDropShadow.BeginAnimation(DropShadowEffect.ColorProperty, shadowAnim);
        AlbumCoverGlow.BeginAnimation(DropShadowEffect.ColorProperty, shadowAnim);
        StatusDot.Fill = new SolidColorBrush(accent);

        // Play/Pause button background
        if (PlayPauseButton.Template != null)
        {
            var playBorder = PlayPauseButton.Template.FindName("PlayPauseButtonBorder", PlayPauseButton) as Border;
            if (playBorder != null)
            {
                playBorder.Background = new SolidColorBrush(accent);
                var playGlow = PlayPauseButton.Template.FindName("PlayButtonGlow", PlayPauseButton) as DropShadowEffect;
                if (playGlow != null)
                {
                    playGlow.Color = accent;
                }
            }
        }

        // Peek Tabs Accent & Glow
        if (LeftTabGlow != null) LeftTabGlow.Color = accent;
        if (RightTabGlow != null) RightTabGlow.Color = accent;
        if (LeftTabDot != null) LeftTabDot.Fill = new SolidColorBrush(accent);
        if (RightTabDot != null) RightTabDot.Fill = new SolidColorBrush(accent);
        if (LeftTabBorderAccent != null) LeftTabBorderAccent.Color = Color.FromArgb(160, accent.R, accent.G, accent.B);
        if (RightTabBorderAccent != null) RightTabBorderAccent.Color = Color.FromArgb(160, accent.R, accent.G, accent.B);
    }

    // ==========================================
    // Dynamic Island Expand / Collapse Animations
    // ==========================================

    private void ExpandIsland()
    {
        if (_isExpanded) return;
        _isExpanded = true;
        _collapseTimer.Stop();

        double targetWidth = 560;
        double targetHeight = 220;
        var duration = TimeSpan.FromMilliseconds(280);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Position alignment
        if (_dockState == EdgeDockState.LeftPeek)
        {
            IslandContainer.HorizontalAlignment = HorizontalAlignment.Left;
            IslandContainer.Margin = new Thickness(14, 0, 0, 0);
        }
        else if (_dockState == EdgeDockState.RightPeek)
        {
            IslandContainer.HorizontalAlignment = HorizontalAlignment.Right;
            IslandContainer.Margin = new Thickness(0, 0, 14, 0);
        }
        else
        {
            IslandContainer.HorizontalAlignment = HorizontalAlignment.Center;
            IslandContainer.Margin = new Thickness(0, 0, 0, 0);
        }

        // Set rounded shape directly
        IslandContainer.CornerRadius = new CornerRadius(36);

        // Animate Container Dimensions
        var widthAnim = new DoubleAnimation(targetWidth, duration) { EasingFunction = ease };
        var heightAnim = new DoubleAnimation(targetHeight, duration) { EasingFunction = ease };

        IslandContainer.BeginAnimation(WidthProperty, widthAnim);
        IslandContainer.BeginAnimation(HeightProperty, heightAnim);

        // Fade out pill, fade in expanded view
        var fadeOutPill = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fadeOutPill.Completed += (s, e) =>
        {
            PillView.Visibility = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Visible;

            var fadeInExpanded = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            ExpandedView.BeginAnimation(OpacityProperty, fadeInExpanded);
        };
        PillView.BeginAnimation(OpacityProperty, fadeOutPill);
    }

    private void CollapseIsland()
    {
        if (!_isExpanded || _isPinned) return;
        _isExpanded = false;

        double targetWidth = 320;
        double targetHeight = 56;
        var duration = TimeSpan.FromMilliseconds(240);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (_dockState == EdgeDockState.LeftPeek)
        {
            IslandContainer.HorizontalAlignment = HorizontalAlignment.Left;
            IslandContainer.Margin = new Thickness(14, 0, 0, 0);
        }
        else if (_dockState == EdgeDockState.RightPeek)
        {
            IslandContainer.HorizontalAlignment = HorizontalAlignment.Right;
            IslandContainer.Margin = new Thickness(0, 0, 14, 0);
        }
        else
        {
            IslandContainer.HorizontalAlignment = HorizontalAlignment.Center;
            IslandContainer.Margin = new Thickness(0, 0, 0, 0);
        }

        // Set rounded shape directly
        IslandContainer.CornerRadius = new CornerRadius(28);

        var widthAnim = new DoubleAnimation(targetWidth, duration) { EasingFunction = ease };
        var heightAnim = new DoubleAnimation(targetHeight, duration) { EasingFunction = ease };

        IslandContainer.BeginAnimation(WidthProperty, widthAnim);
        IslandContainer.BeginAnimation(HeightProperty, heightAnim);

        // Fade out expanded, fade in pill
        var fadeOutExpanded = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
        fadeOutExpanded.Completed += (s, e) =>
        {
            ExpandedView.Visibility = Visibility.Collapsed;
            PillView.Visibility = Visibility.Visible;

            var fadeInPill = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
            PillView.BeginAnimation(OpacityProperty, fadeInPill);
        };
        ExpandedView.BeginAnimation(OpacityProperty, fadeOutExpanded);
    }

    // ==========================================
    // Mouse Interaction, Edge Docking & Arrow Peek Tabs
    // ==========================================

    private void OnPeekTabMouseEnter(object sender, MouseEventArgs e)
    {
        ShowIslandFromPeek();
    }

    private void OnPeekTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            ShowIslandFromPeek();
        }
    }

    private void ShowIslandFromPeek()
    {
        _isPeekTucked = false;
        _peekRetractTimer.Stop();

        if (LeftPeekTab != null) LeftPeekTab.Visibility = Visibility.Collapsed;
        if (RightPeekTab != null) RightPeekTab.Visibility = Visibility.Collapsed;

        IslandContainer.Visibility = Visibility.Visible;
        IslandContainer.Opacity = 0;

        if (_dockState == EdgeDockState.LeftPeek)
        {
            IslandContainer.HorizontalAlignment = HorizontalAlignment.Left;
            IslandContainer.Margin = new Thickness(14, 0, 0, 0);
            var slideAnim = new DoubleAnimation(-25, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            IslandTransform.BeginAnimation(TranslateTransform.XProperty, slideAnim);
        }
        else if (_dockState == EdgeDockState.RightPeek)
        {
            IslandContainer.HorizontalAlignment = HorizontalAlignment.Right;
            IslandContainer.Margin = new Thickness(0, 0, 14, 0);
            var slideAnim = new DoubleAnimation(25, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            IslandTransform.BeginAnimation(TranslateTransform.XProperty, slideAnim);
        }
        else
        {
            IslandContainer.HorizontalAlignment = HorizontalAlignment.Center;
            IslandContainer.Margin = new Thickness(0, 0, 0, 0);
            IslandTransform.X = 0;
        }

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        IslandContainer.BeginAnimation(OpacityProperty, fadeIn);

        if (!_isExpanded)
        {
            ExpandIsland();
        }
    }

    private void RetractIslandToPeek()
    {
        if (_dockState != EdgeDockState.LeftPeek && _dockState != EdgeDockState.RightPeek) return;
        _isPeekTucked = true;
        _peekRetractTimer.Stop();

        if (_isExpanded)
        {
            CollapseIsland();
        }

        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fadeOut.Completed += (s, e) =>
        {
            if (_isPeekTucked)
            {
                IslandContainer.Visibility = Visibility.Collapsed;
                if (_dockState == EdgeDockState.LeftPeek && LeftPeekTab != null)
                {
                    LeftPeekTab.Opacity = 0;
                    LeftPeekTab.Visibility = Visibility.Visible;
                    var fadeInTab = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
                    LeftPeekTab.BeginAnimation(OpacityProperty, fadeInTab);
                }
                else if (_dockState == EdgeDockState.RightPeek && RightPeekTab != null)
                {
                    RightPeekTab.Opacity = 0;
                    RightPeekTab.Visibility = Visibility.Visible;
                    var fadeInTab = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
                    RightPeekTab.BeginAnimation(OpacityProperty, fadeInTab);
                }
            }
        };
        IslandContainer.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            // Clear any active animation clocks so the window can be freely repositioned anywhere on screen
            _peekRetractTimer.Stop();
            _collapseTimer.Stop();

            double curLeft = Left;
            double curTop = Top;
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = curLeft;
            Top = curTop;

            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(_windowHandle, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HTCAPTION, 0);

            // Re-evaluate dock state after user releases drag
            bool moved = Math.Abs(Left - curLeft) > 6 || Math.Abs(Top - curTop) > 6;
            if (moved)
            {
                EvaluateEdgeDocking();
            }
            else
            {
                if (!_isExpanded)
                {
                    ExpandIsland();
                }
            }
        }
    }

    private void EvaluateEdgeDocking()
    {
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        double currentPillWidth = IslandContainer.ActualWidth > 0 ? IslandContainer.ActualWidth : (_isExpanded ? 560 : 320);
        double pillMargin = (Width - currentPillWidth) / 2.0;
        double pillScreenLeft = Left + pillMargin;
        double pillScreenRight = pillScreenLeft + currentPillWidth;

        // Snapping / Docking evaluation:
        // 1. Top-Center dock: Only if dragged very close to top screen border (Top <= 25) AND within the central region
        if (Top <= 25 && pillScreenLeft > 80 && pillScreenRight < screenWidth - 80)
        {
            _dockState = EdgeDockState.TopCenter;
            SnapToTopCenter();
        }
        // 2. Left Peek dock: Only if deliberately pushed into the left edge of monitor
        else if (pillScreenLeft < 40 || Left < 15)
        {
            _dockState = EdgeDockState.LeftPeek;
            BeginAnimation(LeftProperty, null);
            Left = 0;
            RetractIslandToPeek();
        }
        // 3. Right Peek dock: Only if deliberately pushed into the right edge of monitor
        else if (pillScreenRight > screenWidth - 40 || Left > screenWidth - Width - 15)
        {
            _dockState = EdgeDockState.RightPeek;
            BeginAnimation(LeftProperty, null);
            Left = screenWidth - Width;
            RetractIslandToPeek();
        }
        // 4. Free-floating (Center of screen, lower half, custom position):
        else
        {
            _dockState = EdgeDockState.None;
            _isPeekTucked = false;
            if (LeftPeekTab != null) LeftPeekTab.Visibility = Visibility.Collapsed;
            if (RightPeekTab != null) RightPeekTab.Visibility = Visibility.Collapsed;
            if (IslandContainer != null)
            {
                IslandContainer.Visibility = Visibility.Visible;
                IslandContainer.HorizontalAlignment = HorizontalAlignment.Center;
                IslandContainer.Margin = new Thickness(0, 0, 0, 0);
                IslandTransform.X = 0;
                IslandContainer.Opacity = 1;
            }
        }
    }

    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _bloomRetractTimer.Stop();
        _peekRetractTimer.Stop();
        NowPlayingBadge.Visibility = Visibility.Collapsed;

        if (_dockState == EdgeDockState.LeftPeek || _dockState == EdgeDockState.RightPeek)
        {
            if (_isPeekTucked)
            {
                ShowIslandFromPeek();
            }
        }
        else
        {
            if (!_isExpanded)
            {
                ExpandIsland();
            }
        }
    }

    private void TriggerBloomNotification()
    {
        NowPlayingBadge.Visibility = Visibility.Visible;
        if (_dockState == EdgeDockState.LeftPeek || _dockState == EdgeDockState.RightPeek)
        {
            ShowIslandFromPeek();
        }
        else
        {
            ExpandIsland();
        }
        _bloomRetractTimer.Stop();
        _bloomRetractTimer.Start();
    }

    private void OnBloomToggleClick(object sender, RoutedEventArgs e)
    {
        _isAutoBloomEnabled = !_isAutoBloomEnabled;
        BloomIconPath.Fill = _isAutoBloomEnabled 
            ? new SolidColorBrush(_mediaService.CurrentTrack.AccentColor) 
            : new SolidColorBrush(Color.FromRgb(120, 120, 120));
        BloomButton.ToolTip = _isAutoBloomEnabled 
            ? "Auto-Bloom: Expand on track change (Active)" 
            : "Auto-Bloom: Expand on track change (Disabled)";
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        _isFavorite = !_isFavorite;
        // Update the heart icon fill color
        if (FavoriteHeartPath != null)
        {
            FavoriteHeartPath.Fill = _isFavorite
                ? new SolidColorBrush(_mediaService.CurrentTrack.AccentColor)
                : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)); // transparent = outline only
            FavoriteHeartPath.Stroke = _isFavorite
                ? new SolidColorBrush(_mediaService.CurrentTrack.AccentColor)
                : new SolidColorBrush(Color.FromRgb(158, 158, 168));
        }
        if (FavoriteButton != null)
            FavoriteButton.ToolTip = _isFavorite ? "Remove from Liked Songs" : "Add to Liked Songs";
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e)
    {
        _isShuffle = !_isShuffle;
        if (ShuffleIconPath != null)
        {
            ShuffleIconPath.Fill = _isShuffle
                ? new SolidColorBrush(_mediaService.CurrentTrack.AccentColor)
                : new SolidColorBrush(Color.FromRgb(120, 120, 120));
        }
        if (ShuffleButton != null)
            ShuffleButton.ToolTip = _isShuffle ? "Shuffle: On" : "Shuffle: Off";
    }

    private void OnRepeatClick(object sender, RoutedEventArgs e)
    {
        _repeatState = (_repeatState + 1) % 3; // 0=Off 1=All 2=One
        string[] tips  = { "Repeat: Off", "Repeat: All", "Repeat: One" };
        Color[]  colors = {
            Color.FromRgb(120, 120, 120),
            _mediaService.CurrentTrack.AccentColor,
            _mediaService.CurrentTrack.AccentColor
        };
        if (RepeatIconPath != null)
            RepeatIconPath.Fill = new SolidColorBrush(colors[_repeatState]);
        if (RepeatButton != null)
            RepeatButton.ToolTip = tips[_repeatState];
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        if (_dockState == EdgeDockState.LeftPeek || _dockState == EdgeDockState.RightPeek)
        {
            if (!_isPinned && !_isUserSeeking)
            {
                // Debounce retract with timer so it never tweaks out!
                _peekRetractTimer.Stop();
                _peekRetractTimer.Start();
            }
        }
        else
        {
            // Free-floating or TopCenter mode
            if (_isExpanded && !_isPinned && !_isUserSeeking)
            {
                _collapseTimer.Stop();
                _collapseTimer.Start();
            }
        }
    }

    // ==========================================
    // Header & Control Buttons
    // ==========================================

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        PinIconPath.Fill = _isPinned 
            ? new SolidColorBrush(_mediaService.CurrentTrack.AccentColor) 
            : new SolidColorBrush(Color.FromRgb(136, 136, 136));
        PinButton.ToolTip = _isPinned ? "Unpin (Auto-Collapse on leave)" : "Pin (Stay Expanded)";
    }

    private void OnSnapTopClick(object sender, RoutedEventArgs e)
    {
        _dockState = EdgeDockState.TopCenter;
        SnapToTopCenter();
    }

    private void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        _isPinned = false;
        PinIconPath.Fill = new SolidColorBrush(Color.FromRgb(136, 136, 136));
        CollapseIsland();
    }

    private void OnFocusSpotifyClick(object sender, RoutedEventArgs e)
    {
        NativeMethods.FocusSpotifyWindow();
    }

    private void OnDemoClick(object sender, RoutedEventArgs e)
    {
        if (!_mediaService.IsDemoMode)
        {
            _mediaService.EnableDemoMode();
        }
        else
        {
            _mediaService.NextDemoTrack();
        }
    }

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        await _mediaService.TogglePlayPauseAsync();
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        await _mediaService.SkipNextAsync();
    }

    private async void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        await _mediaService.SkipPreviousAsync();
    }

    private async void OnRewind10Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.SkipRelativeAsync(-10);
    }

    private async void OnFastForward10Click(object sender, RoutedEventArgs e)
    {
        await _mediaService.SkipRelativeAsync(10);
    }

    private void OnVolumeDownClick(object sender, RoutedEventArgs e)
    {
        _systemAudio.StepVolumeDown(0.04f);
    }

    private void OnVolumeUpClick(object sender, RoutedEventArgs e)
    {
        _systemAudio.StepVolumeUp(0.04f);
    }

    // ==========================================
    // Volume HUD Overlay (Percentage & Bar)
    // ==========================================

    private void ShowVolumeHud(int percentage, bool isMuted)
    {
        VolumePercentText.Text = isMuted ? "MUTED" : $"{percentage}%";

        double totalWidth = IslandContainer.ActualWidth > 0 ? IslandContainer.ActualWidth : 320;
        double availableTrack = Math.Max(70, totalWidth - 130);
        double fillWidth = Math.Clamp((percentage / 100.0) * availableTrack, 8, availableTrack);
        VolumeFillBar.Width = isMuted ? 8 : fillWidth;

        var accent = _mediaService.CurrentTrack.AccentColor;
        VolumeFillBar.Background = new SolidColorBrush(accent);
        VolumeFillGlow.Color = accent;
        VolumeSpeakerIcon.Fill = new SolidColorBrush(accent);

        if (isMuted || percentage == 0)
        {
            VolumeSpeakerIcon.Data = Geometry.Parse("M2,6 L5,6 L9,2 L9,14 L5,10 L2,10 Z M12,6 L16,10 M16,6 L12,10");
        }
        else if (percentage < 33)
        {
            VolumeSpeakerIcon.Data = Geometry.Parse("M2,6 L5,6 L9,2 L9,14 L5,10 L2,10 Z M11,6 C11.8,7.2 11.8,8.8 11,10");
        }
        else if (percentage < 66)
        {
            VolumeSpeakerIcon.Data = Geometry.Parse("M2,6 L5,6 L9,2 L9,14 L5,10 L2,10 Z M11,5 C12.2,6.5 12.2,9.5 11,11");
        }
        else
        {
            VolumeSpeakerIcon.Data = Geometry.Parse("M2,6 L5,6 L9,2 L9,14 L5,10 L2,10 Z M11,5 C12.2,6.5 12.2,9.5 11,11 M13,3 C15,5 15,11 13,13");
        }

        if (!_isExpanded)
        {
            PillView.Visibility = Visibility.Collapsed;
        }
        else
        {
            ExpandedView.Opacity = 0.25;
        }

        VolumeHudView.Visibility = Visibility.Visible;
        var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(100));
        VolumeHudView.BeginAnimation(OpacityProperty, fadeIn);

        _volumeHudTimer.Stop();
        _volumeHudTimer.Start();
    }

    private void HideVolumeHud()
    {
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fadeOut.Completed += (s, e) =>
        {
            VolumeHudView.Visibility = Visibility.Collapsed;
            if (!_isExpanded)
            {
                PillView.Visibility = Visibility.Visible;
            }
            else
            {
                ExpandedView.Opacity = 1.0;
            }
        };
        VolumeHudView.BeginAnimation(OpacityProperty, fadeOut);
    }

    // ==========================================
    // Seek Slider Interaction
    // ==========================================

    private void OnSeekSliderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isUserSeeking = true;
    }

    private async void OnSeekSliderMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isUserSeeking = false;
        await _mediaService.SeekToPercentageAsync(SeekSlider.Value);
    }

    private void OnSeekSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUserSeeking && _mediaService.CurrentTrack.Duration.TotalSeconds > 0)
        {
            var previewTime = TimeSpan.FromSeconds(SeekSlider.Value * _mediaService.CurrentTrack.Duration.TotalSeconds);
            CurrentTimeText.Text = FormatTime(previewTime);
        }
    }

    // ==========================================
    // Spotify Window Monitor
    // ==========================================

    private void OnSpotifyMonitorTick(object? sender, EventArgs e)
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("Spotify");
            bool isOpen = procs.Length > 0;

            if (!isOpen)
            {
                // Spotify closed entirely
                if (_lastSpotifyIsOpen)
                {
                    _lastSpotifyIsOpen = false;
                    _lastSpotifyWasMinimized = false;
                    Dispatcher.InvokeAsync(HideIslandForSpotify);
                }
                return;
            }

            // Spotify is running — check visibility of its main window
            IntPtr hwnd = NativeMethods.FindSpotifyMainWindow();
            bool isMinimized = hwnd != IntPtr.Zero && NativeMethods.IsIconic(hwnd);
            bool isHidden    = hwnd == IntPtr.Zero || isMinimized;

            if (!_lastSpotifyIsOpen)
            {
                // Spotify just launched → show island
                _lastSpotifyIsOpen = true;
                _lastSpotifyWasMinimized = isMinimized;
                Dispatcher.InvokeAsync(ShowIslandForSpotify);
            }
            else if (isHidden && !_lastSpotifyWasMinimized)
            {
                // Spotify was visible, now minimized/hidden → show island
                _lastSpotifyWasMinimized = true;
                Dispatcher.InvokeAsync(ShowIslandForSpotify);
            }
            else if (!isHidden && _lastSpotifyWasMinimized)
            {
                // Spotify was minimized, now restored → collapse island gently
                _lastSpotifyWasMinimized = false;
                Dispatcher.InvokeAsync(() =>
                {
                    // Don't hide entirely, just collapse to pill if expanded
                    if (_isExpanded && !_isPinned)
                        CollapseIsland();
                });
            }
        }
        catch { /* ignore transient process query failures */ }
    }

    private void ShowIslandForSpotify()
    {
        // Make the window visible
        if (!IsVisible || Opacity < 0.1)
        {
            Opacity = 0;
            Show();
        }

        // Update tray tooltip
        if (System.Windows.Application.Current is App app)
            app.UpdateTrayText("Spotify Island — Now Playing 🎵");

        // Slide in from the top with a spring feel
        var slideIn = new DoubleAnimation
        {
            From     = -60,
            To       = 10,
            Duration = TimeSpan.FromMilliseconds(340),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
        };
        BeginAnimation(TopProperty, slideIn);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        BeginAnimation(OpacityProperty, fadeIn);

        // Snap to top-center if currently top-docked
        if (_dockState == EdgeDockState.TopCenter)
            SnapToTopCenter();

        // Auto-bloom so the user knows a track is playing
        if (!_isExpanded && _isAutoBloomEnabled)
            TriggerBloomNotification();
    }

    private void HideIslandForSpotify()
    {
        if (System.Windows.Application.Current is App app)
            app.UpdateTrayText("Spotify Island — Waiting for Spotify…");

        var slideOut = new DoubleAnimation
        {
            To       = -80,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fadeOut.Completed += (s, ev) => { Hide(); };

        BeginAnimation(TopProperty, slideOut);
        BeginAnimation(OpacityProperty, fadeOut);
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
        return $"{time.Minutes:D2}:{time.Seconds:D2}";
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _spotifyMonitorTimer.Stop();
        _systemAudio?.Dispose();
        _audioCapture?.Dispose();

        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, HOTKEY_ID_PLAYPAUSE);
            NativeMethods.UnregisterHotKey(_windowHandle, HOTKEY_ID_NEXT);
            NativeMethods.UnregisterHotKey(_windowHandle, HOTKEY_ID_PREV);
            NativeMethods.UnregisterHotKey(_windowHandle, HOTKEY_ID_VOLUP);
            NativeMethods.UnregisterHotKey(_windowHandle, HOTKEY_ID_VOLDOWN);
        }
    }
}