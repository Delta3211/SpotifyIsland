using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using SpotifyIsland.Models;

namespace SpotifyIsland.Services;

public class SpotifyMediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private readonly DispatcherTimer _positionTimer;
    private bool _isDemoMode = false;
    private int _demoIndex = 0;

    public event Action<MediaTrackInfo>? TrackUpdated;
    public event Action<TimeSpan>? PositionUpdated;

    public MediaTrackInfo CurrentTrack { get; private set; } = new();
    public bool IsDemoMode => _isDemoMode;

    public SpotifyMediaService()
    {
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _positionTimer.Tick += OnPositionTimerTick;
    }

    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                _sessionManager.SessionsChanged += OnSessionsChanged;
                await RefreshCurrentSessionAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error initializing GSMTC: {ex.Message}");
        }

        // If no active session found on launch, start demo mode so the user sees a rich UI immediately
        if (!CurrentTrack.HasTrack)
        {
            EnableDemoMode();
        }

        _positionTimer.Start();
    }

    private async void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await RefreshCurrentSessionAsync();
        });
    }

    private async void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await RefreshCurrentSessionAsync();
        });
    }

    private async Task RefreshCurrentSessionAsync()
    {
        if (_sessionManager == null) return;

        try
        {
            var sessions = _sessionManager.GetSessions();
            // Prioritize Spotify session
            var spotifySession = sessions.FirstOrDefault(s =>
                s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));

            var targetSession = spotifySession ?? _sessionManager.GetCurrentSession();

            if (targetSession != _currentSession)
            {
                UnsubscribeSessionEvents(_currentSession);
                _currentSession = targetSession;
                SubscribeSessionEvents(_currentSession);
            }

            if (_currentSession != null)
            {
                _isDemoMode = false;
                await UpdateMediaInfoAsync(_currentSession);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error refreshing session: {ex.Message}");
        }
    }

    private void SubscribeSessionEvents(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session == null) return;
        session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
        session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
        session.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
    }

    private void UnsubscribeSessionEvents(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session == null) return;
        try
        {
            session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            session.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
        }
        catch { }
    }

    private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await UpdateMediaInfoAsync(sender);
        });
    }

    private async void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await UpdatePlaybackInfoAsync(sender);
        });
    }

    private async void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            UpdateTimelineInfo(sender);
        });
    }

    private async Task UpdateMediaInfoAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props == null) return;

            BitmapImage? bmp = null;
            if (props.Thumbnail != null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    if (stream != null && stream.Size > 0)
                    {
                        using var netStream = stream.AsStreamForRead();
                        bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = netStream;
                        bmp.EndInit();
                        bmp.Freeze();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load thumbnail: {ex.Message}");
                }
            }

            var palette = ColorExtractor.Extract(bmp);

            CurrentTrack.Title = string.IsNullOrWhiteSpace(props.Title) ? "Unknown Title" : props.Title;
            CurrentTrack.Artist = string.IsNullOrWhiteSpace(props.Artist) ? "Spotify" : props.Artist;
            CurrentTrack.AlbumTitle = props.AlbumTitle ?? "";
            CurrentTrack.Thumbnail = bmp;
            CurrentTrack.AccentColor = palette.AccentColor;
            CurrentTrack.GlowColor = palette.GlowColor;
            CurrentTrack.DarkColor = palette.DarkColor;
            CurrentTrack.HasTrack = true;

            await UpdatePlaybackInfoAsync(session);
            UpdateTimelineInfo(session);

            TrackUpdated?.Invoke(CurrentTrack);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error updating media info: {ex.Message}");
        }
    }

    private async Task UpdatePlaybackInfoAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo != null)
            {
                bool isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                CurrentTrack.IsPlaying = isPlaying;
                TrackUpdated?.Invoke(CurrentTrack);
            }
        }
        catch { }
        await Task.CompletedTask;
    }

    private void UpdateTimelineInfo(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline != null)
            {
                CurrentTrack.Position = timeline.Position;
                CurrentTrack.Duration = timeline.EndTime;
                PositionUpdated?.Invoke(CurrentTrack.Position);
            }
        }
        catch { }
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        if (CurrentTrack.IsPlaying && CurrentTrack.Duration.TotalSeconds > 0)
        {
            var nextPos = CurrentTrack.Position + TimeSpan.FromMilliseconds(150);
            if (nextPos <= CurrentTrack.Duration)
            {
                CurrentTrack.Position = nextPos;
            }
            else if (_isDemoMode)
            {
                NextDemoTrack();
                return;
            }
            PositionUpdated?.Invoke(CurrentTrack.Position);
        }
    }

    // ==========================================
    // Media Playback Controls
    // ==========================================

    public async Task TogglePlayPauseAsync()
    {
        if (_isDemoMode)
        {
            if (System.Diagnostics.Process.GetProcessesByName("Spotify").Length > 0)
            {
                NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PLAY_PAUSE);
            }
            CurrentTrack.IsPlaying = !CurrentTrack.IsPlaying;
            TrackUpdated?.Invoke(CurrentTrack);
            return;
        }

        bool sent = false;
        if (_currentSession != null)
        {
            try { sent = await _currentSession.TryTogglePlayPauseAsync(); }
            catch { }
        }
        // Fallback: simulate the Play/Pause media key so it always works
        if (!sent)
            NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PLAY_PAUSE);
    }

    public async Task SkipNextAsync()
    {
        if (_isDemoMode)
        {
            if (System.Diagnostics.Process.GetProcessesByName("Spotify").Length > 0)
            {
                NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_NEXT_TRACK);
            }
            NextDemoTrack();
            return;
        }

        bool sent = false;
        if (_currentSession != null)
        {
            try { sent = await _currentSession.TrySkipNextAsync(); }
            catch { }
        }
        if (!sent)
            NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_NEXT_TRACK);
    }

    public async Task SkipPreviousAsync()
    {
        if (_isDemoMode)
        {
            if (System.Diagnostics.Process.GetProcessesByName("Spotify").Length > 0)
            {
                NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PREV_TRACK);
            }
            PreviousDemoTrack();
            return;
        }

        bool sent = false;
        if (_currentSession != null)
        {
            try { sent = await _currentSession.TrySkipPreviousAsync(); }
            catch { }
        }
        if (!sent)
            NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PREV_TRACK);
    }

    public async Task SeekToPercentageAsync(double percentage)
    {
        if (CurrentTrack.Duration.TotalSeconds <= 0) return;

        double targetSeconds = Math.Clamp(percentage, 0.0, 1.0) * CurrentTrack.Duration.TotalSeconds;
        var targetTime = TimeSpan.FromSeconds(targetSeconds);
        CurrentTrack.Position = targetTime;
        PositionUpdated?.Invoke(targetTime);

        if (!_isDemoMode && _currentSession != null)
        {
            try
            {
                await _currentSession.TryChangePlaybackPositionAsync((long)targetTime.TotalMicroseconds * 10);
            }
            catch { }
        }
    }

    public async Task SkipRelativeAsync(int secondsDelta)
    {
        if (CurrentTrack.Duration.TotalSeconds <= 0) return;

        double newSeconds = Math.Clamp(CurrentTrack.Position.TotalSeconds + secondsDelta, 0.0, CurrentTrack.Duration.TotalSeconds);
        var targetTime = TimeSpan.FromSeconds(newSeconds);
        CurrentTrack.Position = targetTime;
        PositionUpdated?.Invoke(targetTime);

        if (!_isDemoMode && _currentSession != null)
        {
            try
            {
                await _currentSession.TryChangePlaybackPositionAsync((long)targetTime.TotalMicroseconds * 10);
            }
            catch { }
        }
    }

    // ==========================================
    // Demo Mode (For previewing when Spotify is idle)
    // ==========================================

    private static readonly (string Title, string Artist, string Album, Color Accent, Color Glow, Color Dark, int DurationSec)[] DemoTracks =
    [
        ("Blinding Lights", "The Weeknd", "After Hours", Color.FromRgb(255, 45, 85), Color.FromArgb(150, 255, 45, 85), Color.FromRgb(28, 12, 16), 200),
        ("Starboy", "The Weeknd, Daft Punk", "Starboy", Color.FromRgb(0, 195, 255), Color.FromArgb(150, 0, 195, 255), Color.FromRgb(10, 18, 28), 230),
        ("Levitating", "Dua Lipa", "Future Nostalgia", Color.FromRgb(215, 60, 255), Color.FromArgb(150, 215, 60, 255), Color.FromRgb(24, 10, 30), 203),
        ("Midnight City", "M83", "Hurry Up, We're Dreaming", Color.FromRgb(30, 215, 96), Color.FromArgb(150, 30, 215, 96), Color.FromRgb(12, 24, 16), 243)
    ];

    public void EnableDemoMode()
    {
        _isDemoMode = true;
        LoadDemoTrack(_demoIndex);
    }

    public void NextDemoTrack()
    {
        _demoIndex = (_demoIndex + 1) % DemoTracks.Length;
        LoadDemoTrack(_demoIndex);
    }

    public void PreviousDemoTrack()
    {
        _demoIndex = (_demoIndex - 1 + DemoTracks.Length) % DemoTracks.Length;
        LoadDemoTrack(_demoIndex);
    }

    private void LoadDemoTrack(int index)
    {
        var dt = DemoTracks[index];
        CurrentTrack.Title = dt.Title;
        CurrentTrack.Artist = dt.Artist;
        CurrentTrack.AlbumTitle = dt.Album;
        CurrentTrack.AccentColor = dt.Accent;
        CurrentTrack.GlowColor = dt.Glow;
        CurrentTrack.DarkColor = dt.Dark;
        CurrentTrack.Position = TimeSpan.FromSeconds(24);
        CurrentTrack.Duration = TimeSpan.FromSeconds(dt.DurationSec);
        CurrentTrack.IsPlaying = true;
        CurrentTrack.HasTrack = true;
        CurrentTrack.Thumbnail = GenerateProceduralCover(dt.Accent, dt.Title);

        TrackUpdated?.Invoke(CurrentTrack);
    }

    private static BitmapSource GenerateProceduralCover(Color accent, string text)
    {
        int size = 120;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var grad = new LinearGradientBrush(
                Color.FromRgb((byte)(accent.R * 0.4), (byte)(accent.G * 0.4), (byte)(accent.B * 0.4)),
                accent,
                45.0
            );
            dc.DrawRoundedRectangle(grad, null, new Rect(0, 0, size, size), 16, 16);

            // Vinyl circle inner detail
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), 1.5);
            dc.DrawEllipse(null, pen, new Point(size / 2.0, size / 2.0), 38, 38);
            dc.DrawEllipse(null, pen, new Point(size / 2.0, size / 2.0), 22, 22);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)), null, new Point(size / 2.0, size / 2.0), 10, 10);
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
