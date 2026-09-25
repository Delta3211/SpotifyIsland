namespace SpotifyIsland.Models;

/// <summary>
/// Project-local repeat mode enum — avoids exposing the WinRT
/// GlobalSystemMediaTransportControlsSessionPlaybackAutoRepeatMode type
/// across the public API surface.
/// </summary>
public enum IslandRepeatMode
{
    Off  = 0,
    All  = 1,
    One  = 2
}
