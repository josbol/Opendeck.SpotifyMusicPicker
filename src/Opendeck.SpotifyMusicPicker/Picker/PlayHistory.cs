using System.Text.Json;
using Opendeck.SpotifyMusicPicker.Spotify;
using Opendeck.SpotifyMusicPicker.Util;

namespace Opendeck.SpotifyMusicPicker.Picker;

public sealed record ContextStat(string Uri, ItemKind Kind, double Score, int Plays, DateTimeOffset LastPlayed, string? SampleName, string? SampleSubtitle, string? SampleImage);

/// <summary>
/// Local listening history, fed from /me/player/recently-played (Spotify keeps only the last 50 plays and has no
/// play counts, so the plugin accumulates them itself). Persisted as JSON; deduplicated by (played_at, track).
/// </summary>
public sealed class PlayHistory
{
    private readonly string? _file;
    private readonly List<PlayEntry> _entries = new();
    private readonly HashSet<string> _keys = new();
    private readonly object _lock = new();

    public int MaxDays { get; set; } = 180;
    public int MaxEntries { get; set; } = 20000;

    public PlayHistory(string? file) { _file = file; Load(); }

    public int Count { get { lock (_lock) return _entries.Count; } }
    public IReadOnlyList<PlayEntry> Entries { get { lock (_lock) return _entries.ToList(); } }

    private static string KeyOf(PlayEntry e) => $"{e.At.ToUnixTimeSeconds()}:{e.TrackUri}";

    /// <summary>Adds plays not seen before; returns how many were new.</summary>
    public int Ingest(IEnumerable<PlayEntry> plays, DateTimeOffset? now = null)
    {
        var added = 0;
        lock (_lock)
        {
            foreach (var p in plays) if (_keys.Add(KeyOf(p))) { _entries.Add(p); added++; }
            if (added > 0) { _entries.Sort((a, b) => a.At.CompareTo(b.At)); Prune(now ?? DateTimeOffset.UtcNow); }
        }
        if (added > 0) Save();
        return added;
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-MaxDays);
        var removed = _entries.RemoveAll(e => e.At < cutoff);
        if (_entries.Count > MaxEntries) { _entries.RemoveRange(0, _entries.Count - MaxEntries); removed++; }
        if (removed > 0) { _keys.Clear(); foreach (var e in _entries) _keys.Add(KeyOf(e)); }
    }

    /// <summary>A track played without a context (search, queue) counts for its album.</summary>
    public static string? ContextOf(PlayEntry e) => e.ContextUri ?? e.AlbumUri;

    /// <summary>Albums / playlists / artists / Liked Songs ranked by recency-weighted plays (half-life in days).</summary>
    public List<ContextStat> RankContexts(DateTimeOffset now, int days, double halfLifeDays)
    {
        var since = now.AddDays(-days);
        var acc = new Dictionary<string, (double Score, int Plays, PlayEntry Last)>();
        lock (_lock)
        {
            foreach (var e in _entries)
            {
                if (e.At < since || e.At > now.AddMinutes(5)) continue;
                var uri = ContextOf(e); if (uri is null) continue;
                var kind = PickItem.KindOf(uri);
                if (kind is not (ItemKind.Album or ItemKind.Playlist or ItemKind.Collection or ItemKind.Artist)) continue;
                var w = Math.Pow(0.5, Math.Max(0, (now - e.At).TotalDays) / Math.Max(0.1, halfLifeDays));
                acc[uri] = acc.TryGetValue(uri, out var cur) ? (cur.Score + w, cur.Plays + 1, e.At >= cur.Last.At ? e : cur.Last) : (w, 1, e);
            }
        }
        return acc.Select(kv =>
        {
            var kind = PickItem.KindOf(kv.Key); var last = kv.Value.Last;
            var name = kind switch { ItemKind.Album => last.AlbumName, ItemKind.Collection => "Liked Songs", _ => null };
            var sub = kind switch { ItemKind.Album => last.Artists, ItemKind.Collection => "Your library", _ => null };
            return new ContextStat(kv.Key, kind, kv.Value.Score, kv.Value.Plays, last.At, name, sub, last.AlbumImage);
        }).OrderByDescending(s => s.Score).ThenByDescending(s => s.LastPlayed).ToList();
    }

    /// <summary>Context and album URIs with at least one play since the given time (for "not played lately").</summary>
    public HashSet<string> UrisPlayedSince(DateTimeOffset since)
    {
        var set = new HashSet<string>();
        lock (_lock)
            foreach (var e in _entries)
            {
                if (e.At < since) continue;
                if (e.ContextUri is not null) set.Add(e.ContextUri);
                if (e.AlbumUri is not null) set.Add(e.AlbumUri);
            }
        return set;
    }

    /// <summary>Latest album art seen while a context was playing: a stand-in cover for playlists whose metadata Spotify hides.</summary>
    public Dictionary<string, string> LearnedArt()
    {
        var map = new Dictionary<string, string>();
        lock (_lock)
            foreach (var e in _entries)    // sorted by time → the last one wins
                if (e.ContextUri is not null && e.AlbumImage is not null) map[e.ContextUri] = e.AlbumImage;
        return map;
    }

    private void Load()
    {
        if (_file is null || !File.Exists(_file)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<PlayEntry>>(File.ReadAllText(_file), Json.Options) ?? new();
            lock (_lock)
            {
                foreach (var e in list) if (_keys.Add(KeyOf(e))) _entries.Add(e);
                _entries.Sort((a, b) => a.At.CompareTo(b.At));
            }
            Log.Info($"History: {list.Count} plays loaded");
        }
        catch (Exception ex) { Log.Warn($"history: {ex.Message}"); }
    }

    public void Save()
    {
        if (_file is null) return;
        try
        {
            List<PlayEntry> copy; lock (_lock) copy = _entries.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(copy, Json.Options));
            File.Move(tmp, _file, true);
        }
        catch (Exception ex) { Log.Warn($"history save: {ex.Message}"); }
    }
}
