using System;
using System.Collections.Generic;

namespace SpotifyIsland.Models;

public sealed class IslandSettings
{
    public int CollapseDelayMs { get; set; } = 1600;
    public bool AutoBloomEnabled { get; set; } = true;
    public bool IsPinned { get; set; }
    public bool IsFocusMode { get; set; }
    public bool UseCompactLyrics { get; set; }
    public string DockState { get; set; } = "TopCenter";
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; } = 10;
    public HotkeyBindings Hotkeys { get; set; } = new();
    public List<RecentTrack> RecentTracks { get; set; } = new();
}

public sealed class RecentTrack
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string AlbumTitle { get; set; } = "";
    public DateTime LastPlayedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class HotkeyBindings
{
    public string PlayPause { get; set; } = "Space";
    public string Next { get; set; } = "Right";
    public string Previous { get; set; } = "Left";
    public string VolumeUp { get; set; } = "Up";
    public string VolumeDown { get; set; } = "Down";

    public HotkeyBindings Copy() => new()
    {
        PlayPause = PlayPause,
        Next = Next,
        Previous = Previous,
        VolumeUp = VolumeUp,
        VolumeDown = VolumeDown,
    };
}
