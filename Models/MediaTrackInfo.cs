using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpotifyIsland.Models;

public class MediaTrackInfo
{
    public string Title { get; set; } = "No Track Playing";
    public string Artist { get; set; } = "Spotify";
    public string AlbumTitle { get; set; } = "";
    public BitmapSource? Thumbnail { get; set; }
    
    public TimeSpan Position { get; set; } = TimeSpan.Zero;
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    public bool IsPlaying { get; set; } = false;
    public bool HasTrack { get; set; } = false;

    public Color AccentColor { get; set; } = Color.FromRgb(30, 215, 96); // Spotify Green default
    public Color GlowColor { get; set; } = Color.FromArgb(120, 30, 215, 96);
    public Color DarkColor { get; set; } = Color.FromRgb(18, 18, 20);

    public string FormattedPosition => FormatTime(Position);
    public string FormattedDuration => FormatTime(Duration);

    public double ProgressPercentage
    {
        get
        {
            if (Duration.TotalSeconds <= 0) return 0;
            double pct = Position.TotalSeconds / Duration.TotalSeconds;
            return Math.Clamp(pct, 0.0, 1.0);
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
        return $"{time.Minutes:D2}:{time.Seconds:D2}";
    }
}
