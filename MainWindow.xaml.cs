using System;
using System.Diagnostics;
using System.Linq;
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
    // ── Services ──────────────────────────────────────────────────────────
    private readonly SpotifyMediaService _mediaService;
    private readonly AudioCaptureService _audioCapture;
    private readonly SystemAudioService  _systemAudio;
    private readonly LyricsService       _lyricsService;
    private readonly ProcessWatcherService _processWatcher;

    // ── Timers ────────────────────────────────────────────────────────────
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _bloomRetractTimer;
    private readonly DispatcherTimer _volumeHudTimer;
    private readonly DispatcherTimer _peekRetractTimer;
    private readonly DispatcherTimer _lyricsUpdateTimer;
    // Fallback poll timer — used only if WMI watcher fails to start
    private readonly DispatcherTimer _spotifyMonitorTimer = new();

    // ── Animation ─────────────────────────────────────────────────────────
    private Storyboard? _spinStoryboard;

    // ── State ─────────────────────────────────────────────────────────────
    private bool   _isExpanded          = false;
    private bool   _isPinned            = false;
    private bool   _isUserSeeking       = false;
    private bool   _isAutoBloomEnabled  = true;
    private bool   _isLyricsVisible     = false;
    private bool   _isSettingsVisible   = false;
    private string _lastTrackSignature  = "";
    private IntPtr _windowHandle;
    private IslandSettings _settings = new();
    private bool _isApplyingSettings;
    private bool _isSettingsReady;
    private bool _isRefreshingDevicePicker;
    private int _compactLyricsStartIndex = -1;

    private EdgeDockState _dockState    = EdgeDockState.TopCenter;
    private bool          _isPeekTucked = false;

    // Spotify process monitor state (fallback only)
    private bool _lastSpotifyWasMinimized = false;
    private bool _lastSpotifyIsOpen       = false;
    private bool _wmiWatcherActive        = false;

    // UI-only toggles (GSMTC-backed now for shuffle/repeat)
    private bool _isFavorite  = false;

    private KeyboardHookService? _keyboardHook;

    private enum EdgeDockState { None, TopCenter, LeftPeek, RightPeek }

    // =====================================================================
    // Constructor
    // =====================================================================

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Load();

        // ── Media service ─────────────────────────────────────────────
        _mediaService = new SpotifyMediaService();
        _mediaService.TrackUpdated    += OnTrackUpdated;
        _mediaService.PositionUpdated += OnPositionUpdated;

        // ── Audio volume ──────────────────────────────────────────────
        _systemAudio = new SystemAudioService();
        _systemAudio.VolumeChanged += (pct, muted) => Dispatcher.Invoke(() => ShowVolumeHud(pct, muted));
        _systemAudio.DeviceChanged += ()            => Dispatcher.Invoke(UpdateAudioDeviceLabel);

        // ── Lyrics ────────────────────────────────────────────────────
        _lyricsService = new LyricsService();
        _lyricsService.LyricsChanged += () => Dispatcher.Invoke(OnLyricsChanged);

        // ── Process watcher (WMI, zero-poll) ─────────────────────────
        _processWatcher = new ProcessWatcherService("Spotify");
        _processWatcher.ProcessStarted += () => Dispatcher.InvokeAsync(ShowIslandForSpotify);
        _processWatcher.ProcessStopped += () => Dispatcher.InvokeAsync(HideIslandForSpotify);
        try
        {
            _processWatcher.Start();
            _wmiWatcherActive = true;
        }
        catch
        {
            // WMI unavailable — fall back to polling timer
            _wmiWatcherActive = false;
        }

        // Fallback polling timer (1.5 s) — only actually used if WMI failed
        _spotifyMonitorTimer.Interval = TimeSpan.FromMilliseconds(1500);
        _spotifyMonitorTimer.Tick    += OnSpotifyMonitorTick;
        if (!_wmiWatcherActive) _spotifyMonitorTimer.Start();

        // ── Global keyboard hook ──────────────────────────────────────
        _keyboardHook = new KeyboardHookService(_settings.Hotkeys);
        _keyboardHook.PlayPause  += () => Dispatcher.InvokeAsync(async () =>
        {
            EnsureIslandVisible();
            await _mediaService.TogglePlayPauseAsync();
            if (!_isExpanded && _isAutoBloomEnabled) TriggerBloomNotification();
        });
        _keyboardHook.Next       += () => Dispatcher.InvokeAsync(async () =>
        {
            EnsureIslandVisible();
            await _mediaService.SkipNextAsync();
            if (!_isExpanded && _isAutoBloomEnabled) TriggerBloomNotification();
        });
        _keyboardHook.Previous   += () => Dispatcher.InvokeAsync(async () =>
        {
            EnsureIslandVisible();
            await _mediaService.SkipPreviousAsync();
            if (!_isExpanded && _isAutoBloomEnabled) TriggerBloomNotification();
        });
        _keyboardHook.VolumeUp   += () => Dispatcher.InvokeAsync(() => { EnsureIslandVisible(); _systemAudio.StepVolumeUp(0.04f); });
        _keyboardHook.VolumeDown += () => Dispatcher.InvokeAsync(() => { EnsureIslandVisible(); _systemAudio.StepVolumeDown(0.04f); });

        // ── WASAPI loopback capture ───────────────────────────────────
        _audioCapture = new AudioCaptureService();
        Controls.VisualizerControl.SharedAudioCapture = _audioCapture;
        _audioCapture.Start();

        // ── UI timers ─────────────────────────────────────────────────
        _volumeHudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _volumeHudTimer.Tick += (s, e) => { _volumeHudTimer.Stop(); HideVolumeHud(); };

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _collapseTimer.Tick += (s, e) => { _collapseTimer.Stop(); if (!_isPinned && !IsMouseOver) CollapseIsland(); };

        _bloomRetractTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3800) };
        _bloomRetractTimer.Tick += (s, e) =>
        {
            _bloomRetractTimer.Stop();
            NowPlayingBadge.Visibility = Visibility.Collapsed;
            if (!_isPinned && !IsMouseOver) CollapseIsland();
        };

        _peekRetractTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _peekRetractTimer.Tick += (s, e) =>
        {
            _peekRetractTimer.Stop();
            if (!_isPinned && !IsMouseOver && !_isUserSeeking)
                if (_dockState == EdgeDockState.LeftPeek || _dockState == EdgeDockState.RightPeek)
                    RetractIslandToPeek();
        };

        // Lyrics line highlighter — ticks every 300 ms while lyrics are visible
        _lyricsUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _lyricsUpdateTimer.Tick += (s, e) => HighlightCurrentLyricLine();

        ApplySettings();
        _isSettingsReady = true;
        InitializeVinylSpin();
    }

    // =====================================================================
    // Initialisation
    // =====================================================================

    private void InitializeVinylSpin()
    {
        var spinAnim = new DoubleAnimation
        {
            From           = 0,
            To             = 360,
            Duration       = TimeSpan.FromSeconds(3.5),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(spinAnim, MiniCoverImage);
        Storyboard.SetTargetProperty(spinAnim, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));

        _spinStoryboard = new Storyboard();
        _spinStoryboard.Children.Add(spinAnim);
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowPlacement();
        UpdateAudioDeviceLabel();

        // Seed the Spotify open/minimized state for the fallback timer
        _lastSpotifyIsOpen       = _processWatcher.IsRunning();
        _lastSpotifyWasMinimized = false;

        await _mediaService.InitializeAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowHandle = new WindowInteropHelper(this).Handle;
        // Note: do NOT call HwndSource.FromHwnd here — it triggers WPF window
        // subclassing (HwndSubclass) which crashes on Windows 11 24H2+ with
        // AllowsTransparency=True due to a DllNotFoundException in WindowsBase.
    }

    // =====================================================================
    // Audio device label
    // =====================================================================

    private void UpdateAudioDeviceLabel()
    {
        if (AudioDeviceText == null) return;
        string raw   = _systemAudio.ActiveDeviceName;
        string label = raw.Length > 20 ? raw[..20].TrimEnd() + "…" : raw;
        AudioDeviceText.Text = label.ToUpperInvariant();
        RefreshAudioDevicePicker();
    }

    // =====================================================================
    // Media state updates
    // =====================================================================

    private void OnTrackUpdated(MediaTrackInfo track)
    {
        Dispatcher.Invoke(() =>
        {
            // ── Pill view ──────────────────────────────────────────────
            PillTitleMarquee.Text    = track.Title;
            PillArtistMarquee.Text   = track.Artist;

            // ── Expanded view text ────────────────────────────────────
            LargeTitleMarquee.Text   = track.Title;
            LargeArtistMarquee.Text  = track.Artist;
            LargeAlbumText.Text      = string.IsNullOrWhiteSpace(track.AlbumTitle) ? "Single" : track.AlbumTitle;

            // ── Images ────────────────────────────────────────────────
            // Task 8: cross-fade album art on track change
            string signature = $"{track.Title} - {track.Artist}";
            bool isNewSong   = !string.IsNullOrEmpty(track.Title) && _lastTrackSignature != "" && signature != _lastTrackSignature;
            _lastTrackSignature = signature;

            if (isNewSong)
            {
                CrossFadeAlbumArt(track.Thumbnail);
            }
            else
            {
                MiniCoverImage.Source  = track.Thumbnail;
                LargeCoverImage.Source = track.Thumbnail;
            }

            // ── Times & seek ──────────────────────────────────────────
            TotalTimeText.Text   = track.FormattedDuration;
            CurrentTimeText.Text = track.FormattedPosition;

            if (!_isUserSeeking && track.Duration.TotalSeconds > 0)
                SeekSlider.Value = track.ProgressPercentage;

            // ── Visualizer & play state ───────────────────────────────
            PillVisualizer.IsPlaying    = track.IsPlaying;
            LargeVisualizer.IsPlaying   = track.IsPlaying;
            PillVisualizer.AccentColor  = track.AccentColor;
            LargeVisualizer.AccentColor = track.AccentColor;

            if (track.IsPlaying) _spinStoryboard?.Resume(this);
            else                 _spinStoryboard?.Pause(this);

            UpdatePlayPauseButton(track.IsPlaying);

            // ── Palette ───────────────────────────────────────────────
            // Task 10: pass SecondaryAccent for seek fill & visualizer gradient
            AnimatePalette(track.AccentColor, track.GlowColor, track.DarkColor, track.SecondaryAccent);

            // ── GSMTC shuffle/repeat sync ─────────────────────────────
            UpdateShuffleRepeatUI();

            // ── Auto-bloom ────────────────────────────────────────────
            if (isNewSong && _isAutoBloomEnabled && !_isExpanded)
                TriggerBloomNotification();

            // ── Status badges ─────────────────────────────────────────
            if (_mediaService.IsDemoMode)
            {
                DemoBadge.Visibility  = Visibility.Visible;
                AppStatusText.Text    = "SPOTIFY OFFLINE";
                OpenSpotifyButton.Visibility = Visibility.Visible;
            }
            else
            {
                DemoBadge.Visibility  = Visibility.Collapsed;
                AppStatusText.Text    = "SPOTIFY LIVE";
                OpenSpotifyButton.Visibility = Visibility.Collapsed;
            }

            // ── Lyrics: fetch on new song ─────────────────────────────
            if (isNewSong && !_mediaService.IsDemoMode)
            {
                _lyricsService.FetchAsync(
                    track.Title,
                    track.Artist,
                    (int)track.Duration.TotalSeconds);
            }
            else if (isNewSong)
            {
                _lyricsService.Clear();
            }
        });
    }

    // ── Task 8: cross-fade album art ────────────────────────────────────

    private void CrossFadeAlbumArt(System.Windows.Media.ImageSource? newArt)
    {
        // Fade out both images, swap source, fade in
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fadeOut.Completed += (s, e) =>
        {
            MiniCoverImage.Source  = newArt;
            LargeCoverImage.Source = newArt;
            var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(220));
            MiniCoverImage.BeginAnimation(OpacityProperty, fadeIn);
            LargeCoverImage.BeginAnimation(OpacityProperty, fadeIn);
        };
        MiniCoverImage.BeginAnimation(OpacityProperty, fadeOut);
        LargeCoverImage.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void OnPositionUpdated(TimeSpan position)
    {
        Dispatcher.Invoke(() =>
        {
            CurrentTimeText.Text = FormatTime(position);
            if (!_isUserSeeking && _mediaService.CurrentTrack.Duration.TotalSeconds > 0)
                SeekSlider.Value = position.TotalSeconds / _mediaService.CurrentTrack.Duration.TotalSeconds;
        });
    }

    private void UpdatePlayPauseButton(bool isPlaying)
    {
        if (PlayPauseButton.Template == null) return;
        var playIcon  = PlayPauseButton.Template.FindName("PlayIcon",  PlayPauseButton) as Path;
        var pauseIcon = PlayPauseButton.Template.FindName("PauseIcon", PlayPauseButton) as Path;
        if (playIcon == null || pauseIcon == null) return;
        playIcon.Visibility  = isPlaying ? Visibility.Collapsed : Visibility.Visible;
        pauseIcon.Visibility = isPlaying ? Visibility.Visible   : Visibility.Collapsed;
    }

    // ── Task 10: use SecondaryAccent in seek fill & UI accents ──────────

    private void AnimatePalette(Color accent, Color glow, Color dark, Color secondary)
    {
        var duration = TimeSpan.FromMilliseconds(400);

        GlowColorStop.BeginAnimation(GradientStop.ColorProperty,
            new ColorAnimation(Color.FromArgb(55, glow.R, glow.G, glow.B), duration));

        BorderMidStop.BeginAnimation(GradientStop.ColorProperty,
            new ColorAnimation(Color.FromArgb(80, accent.R, accent.G, accent.B), duration));

        var shadowAnim = new ColorAnimation(glow, duration);
        IslandDropShadow.BeginAnimation(DropShadowEffect.ColorProperty, shadowAnim);
        AlbumCoverGlow.BeginAnimation(DropShadowEffect.ColorProperty,   shadowAnim);

        StatusDot.Fill = new SolidColorBrush(accent);

        // Seek slider active fill uses secondary accent for a two-tone look
        if (SeekSlider != null)
        {
            // Update the slider's foreground so the bound fill color changes
            SeekSlider.Tag = new SolidColorBrush(secondary);
        }

        // Play button
        if (PlayPauseButton.Template != null)
        {
            var playBorder = PlayPauseButton.Template.FindName("PlayPauseButtonBorder", PlayPauseButton) as Border;
            if (playBorder != null)
            {
                playBorder.Background = new SolidColorBrush(accent);
                var playGlow = PlayPauseButton.Template.FindName("PlayButtonGlow", PlayPauseButton) as DropShadowEffect;
                if (playGlow != null) playGlow.Color = accent;
            }
        }

        // Peek tabs
        if (LeftTabGlow  != null) LeftTabGlow.Color  = accent;
        if (RightTabGlow != null) RightTabGlow.Color = accent;
        if (LeftTabDot   != null) LeftTabDot.Fill    = new SolidColorBrush(accent);
        if (RightTabDot  != null) RightTabDot.Fill   = new SolidColorBrush(accent);
        if (LeftTabBorderAccent  != null) LeftTabBorderAccent.Color  = Color.FromArgb(160, accent.R, accent.G, accent.B);
        if (RightTabBorderAccent != null) RightTabBorderAccent.Color = Color.FromArgb(160, accent.R, accent.G, accent.B);
    }

    // ── Task 2: sync shuffle/repeat icons with actual GSMTC state ───────

    private void UpdateShuffleRepeatUI()
    {
        var accent = _mediaService.CurrentTrack.AccentColor;
        var grey   = Color.FromRgb(120, 120, 120);

        // Shuffle
        if (ShuffleIconPath != null)
            ShuffleIconPath.Fill = new SolidColorBrush(_mediaService.IsShuffle ? accent : grey);
        if (ShuffleButton != null)
            ShuffleButton.ToolTip = _mediaService.IsShuffle ? "Shuffle: On" : "Shuffle: Off";

        // Repeat
        var rm = _mediaService.RepeatMode;
        string repeatTip;
        Color  repeatColor;

        switch (rm)
        {
            case IslandRepeatMode.All:
                repeatTip = "Repeat: All"; repeatColor = accent; break;
            case IslandRepeatMode.One:
                repeatTip = "Repeat: One"; repeatColor = accent; break;
            default:
                repeatTip = "Repeat: Off"; repeatColor = grey;   break;
        }

        if (RepeatIconPath  != null) RepeatIconPath.Fill  = new SolidColorBrush(repeatColor);
        if (RepeatButton    != null) RepeatButton.ToolTip = repeatTip;
        if (RepeatBadgeText != null)
            RepeatBadgeText.Visibility = rm == IslandRepeatMode.One
                ? Visibility.Visible : Visibility.Collapsed;
    }

    // =====================================================================
    // Expand / Collapse animations
    // =====================================================================

    private void ExpandIsland()
    {
        if (_isExpanded) return;
        _isExpanded = true;
        _collapseTimer.Stop();

        AlignIslandContainer();
        IslandContainer.CornerRadius = new CornerRadius(36);

        double targetWidth  = _isLyricsVisible ? 580 : 560;
        double targetHeight = _isLyricsVisible ? LyricsExpandedHeight : 220;
        var duration = TimeSpan.FromMilliseconds(280);
        var ease     = new CubicEase { EasingMode = EasingMode.EaseOut };

        IslandContainer.BeginAnimation(WidthProperty,  new DoubleAnimation(targetWidth,  duration) { EasingFunction = ease });
        IslandContainer.BeginAnimation(HeightProperty, new DoubleAnimation(targetHeight, duration) { EasingFunction = ease });

        var fadeOutPill = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        fadeOutPill.Completed += (s, e) =>
        {
            PillView.Visibility     = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Visible;
            ExpandedView.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        };
        PillView.BeginAnimation(OpacityProperty, fadeOutPill);

        if (_isLyricsVisible) _lyricsUpdateTimer.Start();
    }

    private void CollapseIsland()
    {
        if (!_isExpanded || _isPinned) return;
        _isExpanded = false;
        _lyricsUpdateTimer.Stop();

        AlignIslandContainer();
        IslandContainer.CornerRadius = new CornerRadius(28);

        var duration = TimeSpan.FromMilliseconds(240);
        var ease     = new CubicEase { EasingMode = EasingMode.EaseOut };

        IslandContainer.BeginAnimation(WidthProperty,  new DoubleAnimation(320, duration) { EasingFunction = ease });
        IslandContainer.BeginAnimation(HeightProperty, new DoubleAnimation(56,  duration) { EasingFunction = ease });

        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
        fadeOut.Completed += (s, e) =>
        {
            ExpandedView.Visibility = Visibility.Collapsed;
            PillView.Visibility     = Visibility.Visible;
            PillView.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        };
        ExpandedView.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void AlignIslandContainer()
    {
        switch (_dockState)
        {
            case EdgeDockState.LeftPeek:
                IslandContainer.HorizontalAlignment = HorizontalAlignment.Left;
                IslandContainer.Margin = new Thickness(14, 0, 0, 0);
                break;
            case EdgeDockState.RightPeek:
                IslandContainer.HorizontalAlignment = HorizontalAlignment.Right;
                IslandContainer.Margin = new Thickness(0, 0, 14, 0);
                break;
            default:
                IslandContainer.HorizontalAlignment = HorizontalAlignment.Center;
                IslandContainer.Margin = new Thickness(0);
                break;
        }
    }

    // =====================================================================
    // Mouse interaction, edge docking & peek tabs
    // =====================================================================

    private void OnPeekTabMouseEnter(object sender, MouseEventArgs e) => ShowIslandFromPeek();
    private void OnPeekTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) ShowIslandFromPeek();
    }

    private void ShowIslandFromPeek()
    {
        _isPeekTucked = false;
        _peekRetractTimer.Stop();

        if (LeftPeekTab  != null) LeftPeekTab.Visibility  = Visibility.Collapsed;
        if (RightPeekTab != null) RightPeekTab.Visibility = Visibility.Collapsed;

        IslandContainer.Visibility = Visibility.Visible;
        IslandContainer.Opacity    = 0;

        if (_dockState == EdgeDockState.LeftPeek)
        {
            IslandContainer.HorizontalAlignment = HorizontalAlignment.Left;
            IslandContainer.Margin = new Thickness(14, 0, 0, 0);
            IslandTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                new DoubleAnimation(-25, 0, TimeSpan.FromMilliseconds(220))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else if (_dockState == EdgeDockState.RightPeek)
        {
            IslandContainer.HorizontalAlignment = HorizontalAlignment.Right;
            IslandContainer.Margin = new Thickness(0, 0, 14, 0);
            IslandTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                new DoubleAnimation(25, 0, TimeSpan.FromMilliseconds(220))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            IslandContainer.HorizontalAlignment = HorizontalAlignment.Center;
            IslandContainer.Margin = new Thickness(0);
            IslandTransform.X = 0;
        }

        IslandContainer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

        if (!_isExpanded) ExpandIsland();
    }

    private void RetractIslandToPeek()
    {
        if (_dockState != EdgeDockState.LeftPeek && _dockState != EdgeDockState.RightPeek) return;
        _isPeekTucked = true;
        _peekRetractTimer.Stop();

        if (_isExpanded) CollapseIsland();

        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fadeOut.Completed += (s, e) =>
        {
            if (!_isPeekTucked) return;
            IslandContainer.Visibility = Visibility.Collapsed;
            Border? tab = _dockState == EdgeDockState.LeftPeek ? LeftPeekTab : RightPeekTab;
            if (tab == null) return;
            tab.Opacity    = 0;
            tab.Visibility = Visibility.Visible;
            tab.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        };
        IslandContainer.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _peekRetractTimer.Stop();
        _collapseTimer.Stop();

        double curLeft = Left;
        double curTop  = Top;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty,  null);
        Left = curLeft;
        Top  = curTop;

        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(_windowHandle, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HTCAPTION, 0);

        bool moved = Math.Abs(Left - curLeft) > 6 || Math.Abs(Top - curTop) > 6;
        if (moved) EvaluateEdgeDocking();
        else if (!_isExpanded) ExpandIsland();
    }

    private void EvaluateEdgeDocking()
    {
        double screenWidth     = SystemParameters.PrimaryScreenWidth;
        double pillW           = IslandContainer.ActualWidth > 0 ? IslandContainer.ActualWidth : (_isExpanded ? 560 : 320);
        double pillMargin      = (Width - pillW) / 2.0;
        double pillScreenLeft  = Left + pillMargin;
        double pillScreenRight = pillScreenLeft + pillW;

        if (Top <= 25 && pillScreenLeft > 80 && pillScreenRight < screenWidth - 80)
        {
            _dockState = EdgeDockState.TopCenter;
            SnapToTopCenter();
        }
        else if (pillScreenLeft < 40 || Left < 15)
        {
            _dockState = EdgeDockState.LeftPeek;
            BeginAnimation(LeftProperty, null);
            Left = 0;
            RetractIslandToPeek();
        }
        else if (pillScreenRight > screenWidth - 40 || Left > screenWidth - Width - 15)
        {
            _dockState = EdgeDockState.RightPeek;
            BeginAnimation(LeftProperty, null);
            Left = screenWidth - Width;
            RetractIslandToPeek();
        }
        else
        {
            _dockState    = EdgeDockState.None;
            _isPeekTucked = false;
            if (LeftPeekTab  != null) LeftPeekTab.Visibility  = Visibility.Collapsed;
            if (RightPeekTab != null) RightPeekTab.Visibility = Visibility.Collapsed;
            IslandContainer.Visibility            = Visibility.Visible;
            IslandContainer.HorizontalAlignment   = HorizontalAlignment.Center;
            IslandContainer.Margin                = new Thickness(0);
            IslandTransform.X                     = 0;
            IslandContainer.Opacity               = 1;
        }

        PersistSettings();
    }

    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _bloomRetractTimer.Stop();
        _peekRetractTimer.Stop();
        NowPlayingBadge.Visibility = Visibility.Collapsed;

        if ((_dockState == EdgeDockState.LeftPeek || _dockState == EdgeDockState.RightPeek) && _isPeekTucked)
            ShowIslandFromPeek();
        else if (!_isExpanded)
            ExpandIsland();
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        if (_dockState == EdgeDockState.LeftPeek || _dockState == EdgeDockState.RightPeek)
        {
            if (!_isPinned && !_isUserSeeking)
            {
                _peekRetractTimer.Stop();
                _peekRetractTimer.Start();
            }
        }
        else if (_isExpanded && !_isPinned && !_isUserSeeking)
        {
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }
    }

    private void SnapToTopCenter()
    {
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty,  null);
        Left = (screenWidth - Width) / 2.0;
        Top  = 10;
        _dockState    = EdgeDockState.TopCenter;
        _isPeekTucked = false;
        _peekRetractTimer.Stop();

        if (LeftPeekTab  != null) LeftPeekTab.Visibility  = Visibility.Collapsed;
        if (RightPeekTab != null) RightPeekTab.Visibility = Visibility.Collapsed;
        if (IslandContainer != null)
        {
            IslandContainer.Visibility          = Visibility.Visible;
            IslandContainer.HorizontalAlignment = HorizontalAlignment.Center;
            IslandContainer.Margin              = new Thickness(0);
            IslandTransform.X                   = 0;
            IslandContainer.Opacity             = 1;
        }

        PersistSettings();
    }

    // =====================================================================
    // Bloom notification
    // =====================================================================

    private void TriggerBloomNotification()
    {
        NowPlayingBadge.Visibility = Visibility.Visible;
        if (_dockState == EdgeDockState.LeftPeek || _dockState == EdgeDockState.RightPeek)
            ShowIslandFromPeek();
        else
            ExpandIsland();
        _bloomRetractTimer.Stop();
        _bloomRetractTimer.Start();
    }

    // =====================================================================
    // Lyrics overlay (Task 11)
    // =====================================================================

    private void OnLyricsChanged()
    {
        // Show/hide lyrics toggle button hint depending on availability
        if (LyricsButton != null)
            LyricsButton.Opacity = _lyricsService.HasLyrics ? 1.0 : 0.35;

        if (_isLyricsVisible && !_lyricsService.HasLyrics)
        {
            HideLyricsView();
        }
        else if (_isLyricsVisible && _lyricsService.HasLyrics)
        {
            PopulateLyricsPanel();
        }
    }

    private void OnLyricsToggleClick(object sender, RoutedEventArgs e)
    {
        if (_isLyricsVisible) HideLyricsView();
        else ShowLyricsView();
    }

    private void ShowLyricsView()
    {
        if (!_lyricsService.HasLyrics) return;
        _isLyricsVisible = true;

        PopulateLyricsPanel();
        LyricsView.MaxHeight = _settings.UseCompactLyrics ? 52 : 90;
        LyricsView.Visibility = Visibility.Visible;
        LyricsView.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));

        // Expand island taller to fit lyrics
        if (_isExpanded)
        {
            IslandContainer.BeginAnimation(HeightProperty,
                new DoubleAnimation(LyricsExpandedHeight, TimeSpan.FromMilliseconds(220))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        _lyricsUpdateTimer.Start();
    }

    private void HideLyricsView()
    {
        _isLyricsVisible = false;
        _lyricsUpdateTimer.Stop();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (s, e) => LyricsView.Visibility = Visibility.Collapsed;
        LyricsView.BeginAnimation(OpacityProperty, fade);

        if (_isExpanded)
        {
            IslandContainer.BeginAnimation(HeightProperty,
                new DoubleAnimation(220, TimeSpan.FromMilliseconds(220))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void PopulateLyricsPanel()
    {
        if (LyricsPanel == null) return;
        LyricsPanel.Children.Clear();

        int currentIndex = _lyricsService.GetCurrentLineIndex(_mediaService.CurrentTrack.Position);
        var lines = _lyricsService.Lines;
        int firstLine = _settings.UseCompactLyrics ? Math.Max(0, currentIndex) : 0;
        int lastLine = _settings.UseCompactLyrics ? Math.Min(lines.Count, firstLine + 2) : lines.Count;
        _compactLyricsStartIndex = _settings.UseCompactLyrics ? firstLine : -1;

        for (int i = firstLine; i < lastLine; i++)
        {
            var line = lines[i];
            LyricsPanel.Children.Add(new TextBlock
            {
                Text              = line.Text,
                FontSize          = 13,
                Foreground        = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                Margin            = new Thickness(0, 3, 0, 3),
                TextWrapping      = TextWrapping.Wrap,
                FontWeight        = FontWeights.Normal,
                Tag               = line.Time  // store timestamp for highlighting
            });
        }

        HighlightCurrentLyricLine();
    }

    private void HighlightCurrentLyricLine()
    {
        if (LyricsPanel == null || !_isLyricsVisible) return;

        int idx = _lyricsService.GetCurrentLineIndex(_mediaService.CurrentTrack.Position);
        if (_settings.UseCompactLyrics && idx >= 0 && idx != _compactLyricsStartIndex)
        {
            PopulateLyricsPanel();
            return;
        }
        TimeSpan? activeTime = idx >= 0 && idx < _lyricsService.Lines.Count
            ? _lyricsService.Lines[idx].Time
            : null;
        for (int i = 0; i < LyricsPanel.Children.Count; i++)
        {
            if (LyricsPanel.Children[i] is TextBlock tb)
            {
                bool active = activeTime.HasValue && tb.Tag is TimeSpan timestamp && timestamp == activeTime.Value;
                tb.Foreground  = new SolidColorBrush(active
                    ? Colors.White
                    : Color.FromArgb(130, 200, 200, 210));
                tb.FontSize    = active ? 14.5 : 13;
                tb.FontWeight  = active ? FontWeights.SemiBold : FontWeights.Normal;

                // Scroll active line into view
                if (active && !_settings.UseCompactLyrics && LyricsScrollViewer != null)
                    tb.BringIntoView();
            }
        }
    }

    private double LyricsExpandedHeight => _settings.UseCompactLyrics ? 265 : 310;

    // =====================================================================
    // Album art hover zoom (Task 9)
    // =====================================================================

    private void OnAlbumCoverMouseEnter(object sender, MouseEventArgs e)
    {
        var scale = new ScaleTransform(1.0, 1.0);
        LargeCoverImage.RenderTransformOrigin = new Point(0.5, 0.5);
        LargeCoverImage.RenderTransform       = scale;

        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1.0, 1.06, TimeSpan.FromMilliseconds(200))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1.0, 1.06, TimeSpan.FromMilliseconds(200))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void OnAlbumCoverMouseLeave(object sender, MouseEventArgs e)
    {
        if (LargeCoverImage.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1.06, 1.0, TimeSpan.FromMilliseconds(180))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.06, 1.0, TimeSpan.FromMilliseconds(180))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        }
    }

    // =====================================================================
    // Right-click context menu on album cover (Task 12)
    // =====================================================================

    private void OnAlbumCoverRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { Background = new SolidColorBrush(Color.FromArgb(240, 18, 18, 22)) };

        menu.Items.Add(MakeMenuItem("🎵  Copy Track Info", () =>
        {
            var t = _mediaService.CurrentTrack;
            System.Windows.Clipboard.SetText($"{t.Title} — {t.Artist} — {t.AlbumTitle}");
        }));

        menu.Items.Add(MakeMenuItem("▶  Open in Spotify", () =>
        {
            NativeMethods.FocusSpotifyWindow();
        }));

        menu.Items.Add(MakeMenuItem("🌐  Open Web Player", () =>
        {
            var t = _mediaService.CurrentTrack;
            OpenSpotifySearch($"{t.Title} {t.Artist}");
        }));

        menu.Items.Add(MakeMenuItem("👤  Open Artist", () =>
            OpenSpotifySearch(_mediaService.CurrentTrack.Artist)));

        menu.Items.Add(MakeMenuItem("▣  Open Album", () =>
        {
            var t = _mediaService.CurrentTrack;
            OpenSpotifySearch($"{t.AlbumTitle} {t.Artist}");
        }));

        menu.Items.Add(new Separator());

        menu.Items.Add(MakeMenuItem("📋  Copy Spotify Search Link", () =>
        {
            var t = _mediaService.CurrentTrack;
            string q = Uri.EscapeDataString($"{t.Title} {t.Artist}");
            System.Windows.Clipboard.SetText($"https://open.spotify.com/search/{q}");
        }));

        menu.PlacementTarget = LargeCoverImage;
        menu.IsOpen = true;
        e.Handled   = true;
    }

    private static MenuItem MakeMenuItem(string header, Action action)
    {
        var item = new MenuItem
        {
            Header     = header,
            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 228)),
            Background = System.Windows.Media.Brushes.Transparent,
            FontSize   = 12
        };
        item.Click += (s, e) => action();
        return item;
    }

    private static void OpenSpotifySearch(string query)
    {
        string encodedQuery = Uri.EscapeDataString(query);
        Process.Start(new ProcessStartInfo($"https://open.spotify.com/search/{encodedQuery}") { UseShellExecute = true });
    }

    // =====================================================================
    // Persistent preferences
    // =====================================================================

    private void ApplySettings()
    {
        _isApplyingSettings = true;
        try
        {
            _isAutoBloomEnabled = _settings.AutoBloomEnabled;
            _isPinned = _settings.IsPinned;
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.CollapseDelayMs);
            _bloomRetractTimer.Interval = TimeSpan.FromMilliseconds(_settings.CollapseDelayMs + 2200);

            CollapseDelaySlider.Value = _settings.CollapseDelayMs;
            FocusModeToggle.IsChecked = _settings.IsFocusMode;
            CompactLyricsToggle.IsChecked = _settings.UseCompactLyrics;

            SelectHotkey(PlayPauseHotkeyPicker, _settings.Hotkeys.PlayPause);
            SelectHotkey(NextHotkeyPicker, _settings.Hotkeys.Next);
            SelectHotkey(PreviousHotkeyPicker, _settings.Hotkeys.Previous);
            SelectHotkey(VolumeUpHotkeyPicker, _settings.Hotkeys.VolumeUp);
            SelectHotkey(VolumeDownHotkeyPicker, _settings.Hotkeys.VolumeDown);

            UpdateBloomUi();
            UpdatePinUi();
            ApplyFocusMode();
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private void RestoreWindowPlacement()
    {
        double maxLeft = Math.Max(0, SystemParameters.PrimaryScreenWidth - Width);
        double maxTop = Math.Max(0, SystemParameters.WorkArea.Height - 80);
        double savedTop = Math.Clamp(_settings.WindowTop, 0, maxTop);

        if (!Enum.TryParse(_settings.DockState, true, out EdgeDockState savedDock))
            savedDock = EdgeDockState.TopCenter;

        switch (savedDock)
        {
            case EdgeDockState.LeftPeek:
                _dockState = EdgeDockState.LeftPeek;
                Left = 0;
                Top = savedTop;
                Dispatcher.BeginInvoke(RetractIslandToPeek);
                break;
            case EdgeDockState.RightPeek:
                _dockState = EdgeDockState.RightPeek;
                Left = maxLeft;
                Top = savedTop;
                Dispatcher.BeginInvoke(RetractIslandToPeek);
                break;
            case EdgeDockState.None:
                _dockState = EdgeDockState.None;
                Left = Math.Clamp(_settings.WindowLeft, 0, maxLeft);
                Top = savedTop;
                break;
            default:
                SnapToTopCenter();
                break;
        }
    }

    private void PersistSettings()
    {
        if (!_isSettingsReady) return;

        _settings.AutoBloomEnabled = _isAutoBloomEnabled;
        _settings.IsPinned = _isPinned;
        _settings.CollapseDelayMs = (int)Math.Round(CollapseDelaySlider.Value);
        _settings.DockState = _dockState.ToString();
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        SettingsService.Save(_settings);
    }

    private static void SelectHotkey(ComboBox picker, string key)
    {
        foreach (var item in picker.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), key, StringComparison.OrdinalIgnoreCase))
            {
                picker.SelectedItem = item;
                return;
            }
        }
    }

    private static string SelectedHotkey(ComboBox picker, string fallback)
        => (picker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    private void OnHotkeySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isSettingsReady || _isApplyingSettings || _keyboardHook == null) return;

        _settings.Hotkeys.PlayPause = SelectedHotkey(PlayPauseHotkeyPicker, "Space");
        _settings.Hotkeys.Next = SelectedHotkey(NextHotkeyPicker, "Right");
        _settings.Hotkeys.Previous = SelectedHotkey(PreviousHotkeyPicker, "Left");
        _settings.Hotkeys.VolumeUp = SelectedHotkey(VolumeUpHotkeyPicker, "Up");
        _settings.Hotkeys.VolumeDown = SelectedHotkey(VolumeDownHotkeyPicker, "Down");
        _keyboardHook.UpdateBindings(_settings.Hotkeys);
        PersistSettings();
    }

    private void OnFocusModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_isSettingsReady || _isApplyingSettings) return;
        _settings.IsFocusMode = FocusModeToggle.IsChecked == true;
        ApplyFocusMode();
        PersistSettings();
    }

    private void ApplyFocusMode()
    {
        var visibility = _settings.IsFocusMode ? Visibility.Collapsed : Visibility.Visible;
        PillVisualizer.Visibility = visibility;
        LargeVisualizer.Visibility = visibility;
    }

    private void OnCompactLyricsChanged(object sender, RoutedEventArgs e)
    {
        if (!_isSettingsReady || _isApplyingSettings) return;
        _settings.UseCompactLyrics = CompactLyricsToggle.IsChecked == true;
        if (_isLyricsVisible) PopulateLyricsPanel();
        PersistSettings();
    }

    private void RefreshAudioDevicePicker()
    {
        if (AudioDevicePicker == null) return;

        _isRefreshingDevicePicker = true;
        try
        {
            var devices = _systemAudio.GetOutputDevices();
            AudioDevicePicker.ItemsSource = devices;
            AudioDevicePicker.SelectedItem = devices.FirstOrDefault(device => device.IsDefault);
        }
        finally
        {
            _isRefreshingDevicePicker = false;
        }
    }

    private void OnAudioDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingDevicePicker || AudioDevicePicker.SelectedItem is not AudioOutputDevice device || device.IsDefault)
            return;

        if (_systemAudio.SetDefaultOutputDevice(device.Id))
        {
            UpdateAudioDeviceLabel();
        }
        else
        {
            AudioDevicePicker.ToolTip = "Windows could not change the default output device.";
            RefreshAudioDevicePicker();
        }
    }

    // =====================================================================
    // Settings panel (Task 14)
    // =====================================================================

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_isSettingsVisible) HideSettings();
        else                    ShowSettings();
    }

    private void ShowSettings()
    {
        _isSettingsVisible = true;
        SettingsPanel.Visibility = Visibility.Visible;
        SettingsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void HideSettings()
    {
        _isSettingsVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (s, e) => SettingsPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.BeginAnimation(OpacityProperty, fade);
    }

    private void OnCollapseDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int ms = (int)e.NewValue;

        // XAML raises ValueChanged while InitializeComponent is still creating
        // the controls, before the constructor has created these timers.
        if (_collapseTimer != null)
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(ms);
        if (_bloomRetractTimer != null)
            _bloomRetractTimer.Interval = TimeSpan.FromMilliseconds(ms + 2200);
        if (CollapseDelayLabel != null)
            CollapseDelayLabel.Text = $"{ms / 1000.0:F1}s";
        if (_isSettingsReady && !_isApplyingSettings)
            PersistSettings();
    }

    // =====================================================================
    // Header button handlers
    // =====================================================================

    private void OnBloomToggleClick(object sender, RoutedEventArgs e)
    {
        _isAutoBloomEnabled = !_isAutoBloomEnabled;
        UpdateBloomUi();
        PersistSettings();
    }

    private void UpdateBloomUi()
    {
        var accent = _mediaService.CurrentTrack.AccentColor;
        var color = new SolidColorBrush(_isAutoBloomEnabled ? accent : Color.FromRgb(120, 120, 120));
        BloomIconPath.Fill = color;
        SettingsBloomPath.Fill = color;
        BloomButton.ToolTip = _isAutoBloomEnabled
            ? "Auto-Bloom: Expand on track change (Active)"
            : "Auto-Bloom: Expand on track change (Disabled)";
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        _isFavorite = !_isFavorite;
        var accent = _mediaService.CurrentTrack.AccentColor;
        if (FavoriteHeartPath != null)
        {
            FavoriteHeartPath.Fill   = _isFavorite ? new SolidColorBrush(accent) : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            FavoriteHeartPath.Stroke = _isFavorite ? new SolidColorBrush(accent) : new SolidColorBrush(Color.FromRgb(158, 158, 168));
        }
        if (FavoriteButton != null)
            FavoriteButton.ToolTip = _isFavorite ? "Remove from Liked Songs" : "Add to Liked Songs";
    }

    private async void OnShuffleClick(object sender, RoutedEventArgs e)
    {
        await _mediaService.ToggleShuffleAsync();
        UpdateShuffleRepeatUI();
    }

    private async void OnRepeatClick(object sender, RoutedEventArgs e)
    {
        await _mediaService.CycleRepeatAsync();
        UpdateShuffleRepeatUI();
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        UpdatePinUi();
        PersistSettings();
    }

    private void UpdatePinUi()
    {
        PinIconPath.Fill = new SolidColorBrush(_isPinned
            ? _mediaService.CurrentTrack.AccentColor
            : Color.FromRgb(136, 136, 136));
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
        UpdatePinUi();
        PersistSettings();
        CollapseIsland();
    }

    private void OnFocusSpotifyClick(object sender, RoutedEventArgs e) => NativeMethods.FocusSpotifyWindow();

    private void OnOpenSpotifyClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); }
        catch { }
    }

    private void OnDemoClick(object sender, RoutedEventArgs e)
    {
        if (!_mediaService.IsDemoMode) _mediaService.EnableDemoMode();
        else                           _mediaService.NextDemoTrack();
    }

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e) => await _mediaService.TogglePlayPauseAsync();
    private async void OnNextClick(object sender, RoutedEventArgs e)      => await _mediaService.SkipNextAsync();
    private async void OnPreviousClick(object sender, RoutedEventArgs e)  => await _mediaService.SkipPreviousAsync();
    private async void OnRewind10Click(object sender, RoutedEventArgs e)      => await _mediaService.SkipRelativeAsync(-10);
    private async void OnFastForward10Click(object sender, RoutedEventArgs e) => await _mediaService.SkipRelativeAsync(10);
    private void OnVolumeDownClick(object sender, RoutedEventArgs e) => _systemAudio.StepVolumeDown(0.04f);
    private void OnVolumeUpClick(object sender, RoutedEventArgs e)   => _systemAudio.StepVolumeUp(0.04f);

    // =====================================================================
    // Volume HUD overlay
    // =====================================================================

    private void ShowVolumeHud(int percentage, bool isMuted)
    {
        VolumePercentText.Text = isMuted ? "MUTED" : $"{percentage}%";

        double totalWidth    = IslandContainer.ActualWidth > 0 ? IslandContainer.ActualWidth : 320;
        double availTrack    = Math.Max(70, totalWidth - 130);
        double fillWidth     = Math.Clamp((percentage / 100.0) * availTrack, 8, availTrack);
        VolumeFillBar.Width  = isMuted ? 8 : fillWidth;

        var accent = _mediaService.CurrentTrack.AccentColor;
        VolumeFillBar.Background = new SolidColorBrush(accent);
        VolumeFillGlow.Color     = accent;
        VolumeSpeakerIcon.Fill   = new SolidColorBrush(accent);

        VolumeSpeakerIcon.Data = (isMuted || percentage == 0)
            ? Geometry.Parse("M2,6 L5,6 L9,2 L9,14 L5,10 L2,10 Z M12,6 L16,10 M16,6 L12,10")
            : percentage < 33
                ? Geometry.Parse("M2,6 L5,6 L9,2 L9,14 L5,10 L2,10 Z M11,6 C11.8,7.2 11.8,8.8 11,10")
                : percentage < 66
                    ? Geometry.Parse("M2,6 L5,6 L9,2 L9,14 L5,10 L2,10 Z M11,5 C12.2,6.5 12.2,9.5 11,11")
                    : Geometry.Parse("M2,6 L5,6 L9,2 L9,14 L5,10 L2,10 Z M11,5 C12.2,6.5 12.2,9.5 11,11 M13,3 C15,5 15,11 13,13");

        if (!_isExpanded) PillView.Visibility = Visibility.Collapsed;
        else              ExpandedView.Opacity = 0.25;

        VolumeHudView.Visibility = Visibility.Visible;
        VolumeHudView.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));

        _volumeHudTimer.Stop();
        _volumeHudTimer.Start();
    }

    private void HideVolumeHud()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (s, e) =>
        {
            VolumeHudView.Visibility = Visibility.Collapsed;
            if (!_isExpanded) PillView.Visibility = Visibility.Visible;
            else              ExpandedView.Opacity = 1.0;
        };
        VolumeHudView.BeginAnimation(OpacityProperty, fade);
    }

    // =====================================================================
    // Seek slider
    // =====================================================================

    private void OnSeekSliderMouseDown(object sender, MouseButtonEventArgs e) => _isUserSeeking = true;

    private async void OnSeekSliderMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isUserSeeking = false;
        await _mediaService.SeekToPercentageAsync(SeekSlider.Value);
    }

    private void OnSeekSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUserSeeking && _mediaService.CurrentTrack.Duration.TotalSeconds > 0)
            CurrentTimeText.Text = FormatTime(
                TimeSpan.FromSeconds(SeekSlider.Value * _mediaService.CurrentTrack.Duration.TotalSeconds));
    }

    // =====================================================================
    // Spotify window monitor (Task 15: WMI replaces this, kept as fallback)
    // =====================================================================

    private void OnSpotifyMonitorTick(object? sender, EventArgs e)
    {
        try
        {
            var procs  = System.Diagnostics.Process.GetProcessesByName("Spotify");
            bool isOpen = procs.Length > 0;

            if (!isOpen)
            {
                if (_lastSpotifyIsOpen)
                {
                    _lastSpotifyIsOpen       = false;
                    _lastSpotifyWasMinimized = false;
                    Dispatcher.InvokeAsync(HideIslandForSpotify);
                }
                return;
            }

            IntPtr hwnd        = NativeMethods.FindSpotifyMainWindow();
            bool   isMinimized = hwnd != IntPtr.Zero && NativeMethods.IsIconic(hwnd);
            bool   isHidden    = hwnd == IntPtr.Zero || isMinimized;

            if (!_lastSpotifyIsOpen)
            {
                _lastSpotifyIsOpen       = true;
                _lastSpotifyWasMinimized = isMinimized;
                Dispatcher.InvokeAsync(ShowIslandForSpotify);
            }
            else if (isHidden && !_lastSpotifyWasMinimized)
            {
                _lastSpotifyWasMinimized = true;
                Dispatcher.InvokeAsync(ShowIslandForSpotify);
            }
            else if (!isHidden && _lastSpotifyWasMinimized)
            {
                _lastSpotifyWasMinimized = false;
                Dispatcher.InvokeAsync(() => { if (_isExpanded && !_isPinned) CollapseIsland(); });
            }
        }
        catch { }
    }

    public void EnsureIslandVisible()
    {
        if (!IsVisible || Opacity < 0.1) ShowIslandForSpotify();
    }

    private void ShowIslandForSpotify()
    {
        if (!IsVisible || Opacity < 0.1) { Opacity = 0; Show(); }

        if (System.Windows.Application.Current is App app)
            app.UpdateTrayText("Spotify Island — Now Playing 🎵");

        BeginAnimation(TopProperty, new DoubleAnimation
        {
            From           = -60,
            To             = 10,
            Duration       = TimeSpan.FromMilliseconds(340),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
        });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));

        if (_dockState == EdgeDockState.TopCenter) SnapToTopCenter();
        if (!_isExpanded && _isAutoBloomEnabled)   TriggerBloomNotification();
    }

    private void HideIslandForSpotify()
    {
        if (System.Windows.Application.Current is App app)
            app.UpdateTrayText("Spotify Island — Waiting for Spotify…");

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (s, ev) => Hide();

        BeginAnimation(TopProperty, new DoubleAnimation
        {
            To             = -80,
            Duration       = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        BeginAnimation(OpacityProperty, fade);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Cancel the close — the window should hide, not close.
        // The app only truly exits via the tray Quit menu.
        e.Cancel = true;
        Hide();
    }

    public void OnWindowDispose()
    {
        // Called explicitly by App.QuitApp before Shutdown()
        PersistSettings();
        _spotifyMonitorTimer.Stop();
        _lyricsUpdateTimer.Stop();
        _processWatcher.Dispose();
        _keyboardHook?.Dispose();
        _systemAudio?.Dispose();
        _audioCapture?.Dispose();
    }

    // Snapshot export (dev tool — kept for debugging)
    private static void SaveVisualToPng(Visual visual, string filePath)
    {
        if (visual is not Window win) return;
        int w   = (int)Math.Max(1, win.ActualWidth  > 0 ? win.ActualWidth  : 640);
        int h   = (int)Math.Max(1, win.ActualHeight > 0 ? win.ActualHeight : 300);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(win);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(filePath);
        enc.Save(fs);
    }
}
