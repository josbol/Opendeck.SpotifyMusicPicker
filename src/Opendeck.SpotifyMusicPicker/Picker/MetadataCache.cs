using System.Text.Json;
using Opendeck.SpotifyMusicPicker.Spotify;
using Opendeck.SpotifyMusicPicker.Util;

namespace Opendeck.SpotifyMusicPicker.Picker;

/// <summary>Names / covers per URI. Spotify removed the batch endpoints, so every album or playlist costs a request: remember them.</summary>
public sealed class MetadataCache
{
    public sealed class Entry
    {
        public string Uri { get; set; } = "";
        public ItemKind Kind { get; set; }
        public string? Name { get; set; }
        public string? Subtitle { get; set; }
        public string? ImageUrl { get; set; }
        public DateTimeOffset FetchedAt { get; set; }
        /// <summary>Spotify refused it (404/403): an algorithmic playlist on a restricted app, or something deleted.</summary>
        public bool Missing { get; set; }
    }

    private readonly Dictionary<string, Entry> _map = new();
    private readonly string? _file;
    private readonly object _lock = new();
    private bool _dirty;

    public TimeSpan Ttl { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan MissingTtl { get; set; } = TimeSpan.FromDays(1);

    public MetadataCache(string? file)
    {
        _file = file;
        if (file is null || !File.Exists(file)) return;
        try
        {
            foreach (var e in JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(file), Json.Options) ?? new()) _map[e.Uri] = e;
        }
        catch (Exception ex) { Log.Warn($"metadata cache: {ex.Message}"); }
    }

    public Entry? Get(string uri, DateTimeOffset now)
    {
        lock (_lock)
            return _map.TryGetValue(uri, out var e) && now - e.FetchedAt < (e.Missing ? MissingTtl : Ttl) ? e : null;
    }

    /// <summary>Whatever is known, however old (when Spotify cannot be reached).</summary>
    public Entry? GetStale(string uri) { lock (_lock) return _map.GetValueOrDefault(uri); }

    public void Put(Entry e) { lock (_lock) { _map[e.Uri] = e; _dirty = true; } }

    public void Save()
    {
        if (_file is null) return;
        List<Entry> copy;
        lock (_lock) { if (!_dirty) return; copy = _map.Values.ToList(); _dirty = false; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(copy, Json.Options));
            File.Move(tmp, _file, true);
        }
        catch (Exception ex) { Log.Warn($"metadata save: {ex.Message}"); }
    }
}
