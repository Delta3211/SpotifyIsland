using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpotifyIsland.Services;

/// <summary>
/// Fetches time-synced lyrics from the lrclib.net public API (no key required).
/// Parses LRC timestamps into a list of <see cref="LyricLine"/> structs that the
/// UI can highlight in sync with <see cref="SpotifyMediaService.CurrentTrack.Position"/>.
/// </summary>
public class LyricsService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestHeaders = { { "User-Agent", "SpotifyIsland/1.0 (github.com/orbitraiders)" } }
    };

    private CancellationTokenSource? _cts;

    public IReadOnlyList<LyricLine> Lines { get; private set; } = Array.Empty<LyricLine>();
    public bool HasLyrics => Lines.Count > 0;

    /// <summary>Raised on the calling thread after lyrics load or fail.</summary>
    public event Action? LyricsChanged;

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Begin an async fetch for the given track. Any in-flight request is cancelled first.
    /// Clears current lyrics immediately so the UI shows a loading state.
    /// </summary>
    public void FetchAsync(string title, string artist, int durationSec)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        Lines = Array.Empty<LyricLine>();
        LyricsChanged?.Invoke();

        _ = DoFetchAsync(title, artist, durationSec, _cts.Token);
    }

    public void Clear()
    {
        _cts?.Cancel();
        Lines = Array.Empty<LyricLine>();
        LyricsChanged?.Invoke();
    }

    /// <summary>Returns the index of the line that should be highlighted at <paramref name="position"/>.</summary>
    public int GetCurrentLineIndex(TimeSpan position)
    {
        int idx = -1;
        for (int i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].Time <= position) idx = i;
            else break;
        }
        return idx;
    }

    // ── Private fetch logic ───────────────────────────────────────────────

    private async Task DoFetchAsync(string title, string artist, int durationSec, CancellationToken ct)
    {
        try
        {
            // lrclib search endpoint — returns a JSON array ranked by relevance
            string query = Uri.EscapeDataString($"{title} {artist}");
            string url   = $"https://lrclib.net/api/search?q={query}";

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return;

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            // Find the best match: prefer tracks whose duration is within ±10 s
            JsonElement? best = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (ct.IsCancellationRequested) return;

                string itemTitle  = item.GetStringOrEmpty("trackName");
                string itemArtist = item.GetStringOrEmpty("artistName");
                bool   hasSynced  = item.TryGetProperty("syncedLyrics", out var sl) && sl.ValueKind == JsonValueKind.String;

                if (!hasSynced) continue;

                // Fuzzy match on title + artist
                if (!FuzzyMatch(itemTitle, title) && !FuzzyMatch(itemArtist, artist)) continue;

                if (best == null)
                {
                    best = item;
                }
                else if (durationSec > 0 && item.TryGetProperty("duration", out var dur))
                {
                    double diff = Math.Abs(dur.GetDouble() - durationSec);
                    if (best.Value.TryGetProperty("duration", out var bestDur))
                    {
                        double bestDiff = Math.Abs(bestDur.GetDouble() - durationSec);
                        if (diff < bestDiff) best = item;
                    }
                }
            }

            if (best == null) return;
            if (!best.Value.TryGetProperty("syncedLyrics", out var lrcProp)) return;

            string lrc = lrcProp.GetString() ?? string.Empty;
            var lines  = ParseLrc(lrc);
            if (lines.Count == 0) return;

            Lines = lines;
            LyricsChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LyricsService] Fetch error: {ex.Message}");
        }
    }

    // ── LRC parser ────────────────────────────────────────────────────────

    // Matches [mm:ss.xx] or [mm:ss]
    private static readonly Regex _lrcTimestamp =
        new(@"^\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\](.*)$", RegexOptions.Compiled);

    private static List<LyricLine> ParseLrc(string lrc)
    {
        var result = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            string line = raw.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var m = _lrcTimestamp.Match(line);
            if (!m.Success) continue;

            int    min    = int.Parse(m.Groups[1].Value);
            int    sec    = int.Parse(m.Groups[2].Value);
            string msStr  = m.Groups[3].Value;
            int    ms     = msStr.Length > 0 ? int.Parse(msStr.PadRight(3, '0')) : 0;
            string text   = m.Groups[4].Value.Trim();

            // Skip instrumental / empty lines
            if (string.IsNullOrWhiteSpace(text)) continue;

            result.Add(new LyricLine(
                TimeSpan.FromMilliseconds(min * 60_000 + sec * 1000 + ms),
                text));
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    private static bool FuzzyMatch(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        return a.Contains(b) || b.Contains(a);
    }
}

// ── Extension helper ──────────────────────────────────────────────────────

internal static class JsonElementExtensions
{
    public static string GetStringOrEmpty(this JsonElement el, string property)
        => el.TryGetProperty(property, out var v) ? (v.GetString() ?? string.Empty) : string.Empty;
}

// ── Value type for one lyric line ─────────────────────────────────────────

public readonly struct LyricLine
{
    public TimeSpan Time { get; }
    public string   Text { get; }

    public LyricLine(TimeSpan time, string text)
    {
        Time = time;
        Text = text;
    }
}
