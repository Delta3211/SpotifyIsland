using System;
using System.IO;
using System.Text.Json;
using SpotifyIsland.Models;

namespace SpotifyIsland.Services;

public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpotifyIsland",
        "settings.json");

    public static IslandSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new IslandSettings();

            var settings = JsonSerializer.Deserialize<IslandSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? new IslandSettings();
            settings.CollapseDelayMs = Math.Clamp(settings.CollapseDelayMs, 800, 4000);
            settings.Hotkeys ??= new HotkeyBindings();
            return settings;
        }
        catch
        {
            return new IslandSettings();
        }
    }

    public static void Save(IslandSettings settings)
    {
        try
        {
            string? directory = Path.GetDirectoryName(SettingsPath);
            if (directory == null) return;

            Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Preference persistence must never prevent the media widget from running.
        }
    }
}
