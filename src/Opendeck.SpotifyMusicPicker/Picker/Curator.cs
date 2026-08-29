using Opendeck.SpotifyMusicPicker.Spotify;
using Opendeck.SpotifyMusicPicker.Util;

namespace Opendeck.SpotifyMusicPicker.Picker;

public sealed class CuratorSettings
{
    public int HistoryDays { get; set; } = 30;
    public double HalfLifeDays { get; set; } = 14;
    public int RefreshMinutes { get; set; } = 15;
    public int PlaybackSeconds { get; set; } = 5;
    public int IdlePlaybackSeconds { get; set; } = 60;
    public int HistoryMinutes { get; set; } = 3;
    public int VolumeStep { get; set; } = 5;
    public string MadeForYouLinks { get; set; } = "";
    public string PreferredDevice { get; set; } = "";
    public int RecommendedCount { get; set; } = 3;
    public int FrequentCount { get; set; } = 10;
    public int ExcludePlayedDays { get; set; } = 60;
    public int PoolHours { get; set; } = 6;
    public int PoolArtists { get; set; } = 12;
    public int SavedAlbumPages { get; set; } = 4;
}

/// <summary>Builds the three rows, tracks playback, plays things. Everything network-related lives here.</summary>
public sealed class Curator : IDisposable
{
    public SpotifyClient Client { get; }
    public SpotifyAuth Auth { get; }
    public PlayHistory History { get; }
    public MetadataCache Meta { get; }
    public ArtCache Art { get; }
    public CuratorSettings Settings { get; set; } = new();
    public Snapshot Current { get; private set; } = new() { At = DateTimeOffset.UtcNow };
    /// <summary>Picker keys are on screen: poll playback quickly.</summary>
    public bool KeypadVisible { get; set; }
    public Func<string> HostName { get; set; } = () => Environment.MachineName;

    public event Action<Snapshot>? Changed;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _listsGate = new(1, 1);
    private readonly SemaphoreSlim _listsSignal = new(0), _playbackSignal = new(0), _historySignal = new(0);
    private readonly object _publishLock = new();
    private List<PickItem> _pool = new();
    private DateTimeOffset _poolAt;
    private HashSet<string> _playedLately = new();
    private int _reroll, _madeOffset, _freqOffset;
    private int _pendingVolume = -1;
    private int _volumeSending;

    public Curator(SpotifyClient client, SpotifyAuth auth, PlayHistory history, MetadataCache meta, ArtCache art)
    {
        Client = client; Auth = auth; History = history; Meta = meta; Art = art;
        auth.Changed += () => { RequestLists(); RequestPlayback(); };
    }

    public void Start()
    {
        _ = Task.Run(() => LoopAsync("lists", _listsSignal, () => TimeSpan.FromMinutes(Math.Max(1, Settings.RefreshMinutes)), RefreshListsAsync));
        _ = Task.Run(() => LoopAsync("playback", _playbackSignal, () => TimeSpan.FromSeconds(Math.Max(2, KeypadVisible ? Settings.PlaybackSeconds : Settings.IdlePlaybackSeconds)), RefreshPlaybackAsync, runFirst: false));
        _ = Task.Run(() => LoopAsync("history", _historySignal, () => TimeSpan.FromMinutes(Math.Max(1, Settings.HistoryMinutes)), RefreshFrequentAsync, runFirst: false));
    }

    private async Task LoopAsync(string name, SemaphoreSlim signal, Func<TimeSpan> interval, Func<CancellationToken, Task> body, bool runFirst = true)
    {
        var first = runFirst;
        while (!_cts.IsCancellationRequested)
        {
            if (!first)
            {
                try { await signal.WaitAsync(interval(), _cts.Token); } catch (OperationCanceledException) { return; }
                while (signal.CurrentCount > 0) signal.Wait(0);   // coalesce
            }
            first = false;
            if (!Auth.IsConnected) { PublishDisconnected(); continue; }
            try { await body(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Error($"{name} loop", ex); }
        }
    }

    public void RequestLists() => _listsSignal.Release();
    public void RequestPlayback() => _playbackSignal.Release();
    public void RequestHistory() => _historySignal.Release();

    private void Publish(Snapshot s, bool notify = true)
    {
        lock (_publishLock) Current = s;
        if (notify) Changed?.Invoke(s);
    }

    private void PublishDisconnected()
    {
        if (!Current.Connected && Current.Error == (Auth.Error ?? "not connected")) return;
        Publish(new Snapshot { Connected = false, Error = Auth.Error ?? "not connected", At = DateTimeOffset.UtcNow });
    }

    // ---- the rows -----------------------------------------------------------------------

    public PickItem? ItemAt(string row, int slot)
    {
        var list = Row(row);
        if (slot < 1 || slot > list.Count) return null;
        var offset = row switch { "madeforyou" => _madeOffset, "frequent" => _freqOffset, _ => 0 };
        return list[(((slot - 1 + offset) % list.Count) + list.Count) % list.Count];
    }

    public IReadOnlyList<PickItem> Row(string row) => row switch
    {
        "madeforyou" => Current.MadeForYou,
        "frequent" => Current.Frequent,
        "recommended" => Current.Recommended,
        _ => Array.Empty<PickItem>(),
    };

    /// <summary>Browse dial: shifts a row by whole slots (wraps).</summary>
    public void Shift(string row, int ticks)
    {
        if (row is "madeforyou" or "both") _madeOffset += ticks;
        if (row is "frequent" or "both") _freqOffset += ticks;
        Changed?.Invoke(Current);
    }

    public void Reroll()
    {
        _reroll++;
        var rec = PickRecommended(DateTimeOffset.UtcNow, Current.MadeForYou, Current.Frequent);
        Publish(Current with { Recommended = rec, At = DateTimeOffset.UtcNow });
        _ = Task.Run(async () => { await PrefetchArtAsync(rec, _cts.Token); Changed?.Invoke(Current); });
    }

    public async Task RefreshListsAsync(CancellationToken ct)
    {
        if (!Auth.IsConnected) { PublishDisconnected(); return; }
        await _listsGate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var errors = new List<string>();

            // 1. Listening history first: it also supplies stand-in covers for playlists Spotify hides
            var (plays, perr) = await Client.RecentlyPlayedAsync(ct);
            if (perr is not null) errors.Add($"recently played: {perr}");
            History.Ingest(plays, now);
            _playedLately = History.UrisPlayedSince(now.AddDays(-Settings.ExcludePlayedDays));

            // 2. Made for you — Spotify-owned playlists in the library, then configured links
            var (mine, hidden, err) = await Client.MyPlaylistsAsync(ct);
            if (err is not null) errors.Add($"playlists: {err}");
            var manual = MadeForYou.ParseLinks(Settings.MadeForYouLinks);
            var made = MadeForYou.Build(mine, manual, History.LearnedArt());
            made = await ResolveManualAsync(made, now, ct);

            // 3. Most played
            var frequent = await BuildFrequentAsync(now, made.Select(i => i.Uri).ToHashSet(), ct);

            // 4. Recommendations from a pool that is rebuilt a few times a day
            if (_pool.Count == 0 || now - _poolAt > TimeSpan.FromHours(Math.Max(1, Settings.PoolHours)))
            {
                _pool = await BuildPoolAsync(now, ct);
                _poolAt = now;
            }
            var rec = PickRecommended(now, made, frequent);

            Meta.Save();
            await PrefetchArtAsync(made.Concat(frequent).Concat(rec), ct);
            Publish(Current with
            {
                MadeForYou = made, Frequent = frequent, Recommended = rec, HiddenPlaylists = hidden,
                Error = errors.Count > 0 ? string.Join("; ", errors) : null, Connected = true, UserName = Auth.UserName,
                HistoryPlays = History.Count, ListsAt = now, At = now,
            });
            Log.Info($"Lists: {made.Count} made-for-you ({hidden} hidden), {frequent.Count} frequent, {rec.Count}/{_pool.Count} recommended, {History.Count} plays in history, {Client.Requests} requests so far");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("refresh lists", ex);
            Publish(Current with { Error = ex.Message, Connected = Auth.IsConnected, At = DateTimeOffset.UtcNow });
        }
        finally { _listsGate.Release(); }
    }

    /// <summary>Cheap periodic pass: new plays → re-rank the frequent row without rebuilding everything.</summary>
    public async Task RefreshFrequentAsync(CancellationToken ct)
    {
        if (!Auth.IsConnected || Current.ListsAt == default) return;
        var (plays, err) = await Client.RecentlyPlayedAsync(ct);
        if (err is not null) { Log.Debug($"recently played: {err}"); return; }
        var now = DateTimeOffset.UtcNow;
        if (History.Ingest(plays, now) == 0) return;
        if (!await _listsGate.WaitAsync(0, ct)) return;
        try
        {
            _playedLately = History.UrisPlayedSince(now.AddDays(-Settings.ExcludePlayedDays));
            var frequent = await BuildFrequentAsync(now, Current.MadeForYou.Select(i => i.Uri).ToHashSet(), ct);
            var rec = Current.Recommended.Any(r => _playedLately.Contains(r.Uri)) ? PickRecommended(now, Current.MadeForYou, frequent) : Current.Recommended;
            Meta.Save();
            await PrefetchArtAsync(frequent.Concat(rec), ct);
            Publish(Current with { Frequent = frequent, Recommended = rec, HistoryPlays = History.Count, At = now });
        }
        finally { _listsGate.Release(); }
    }

    private async Task<List<PickItem>> ResolveManualAsync(List<PickItem> made, DateTimeOffset now, CancellationToken ct)
    {
        var result = new List<PickItem>(made.Count);
        foreach (var item in made)
        {
            if (!item.MetadataMissing) { result.Add(item); continue; }
            var meta = await ResolveMetaAsync(item.Uri, item.Kind, ct);
            if (meta is { Missing: false })
                result.Add(item with { Name = meta.Name ?? item.Name, ImageUrl = meta.ImageUrl ?? item.ImageUrl, Subtitle = item.Subtitle, MetadataMissing = false });
            else result.Add(item);
        }
        return result;
    }

    private async Task<List<PickItem>> BuildFrequentAsync(DateTimeOffset now, HashSet<string> exclude, CancellationToken ct)
    {
        var items = new List<PickItem>();
        var manualNames = MadeForYou.ParseLinks(Settings.MadeForYouLinks).Where(m => m.Name is not null).ToDictionary(m => m.Uri, m => m.Name!);
        foreach (var s in History.RankContexts(now, Settings.HistoryDays, Settings.HalfLifeDays))
        {
            if (exclude.Contains(s.Uri)) continue;
            var item = await ToItemAsync(s, manualNames, ct);
            if (item is not null) items.Add(item);
            if (items.Count >= Settings.FrequentCount) break;
        }
        if (items.Count < 5)
        {
            // cold start: the albums your top tracks come from
            var top = await Client.TopTracksAsync("short_term", 50, ct);
            if (top.Count == 0) top = await Client.TopTracksAsync("medium_term", 50, ct);
            foreach (var g in top.Where(t => t.AlbumUri is not null).GroupBy(t => t.AlbumUri!).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                if (exclude.Contains(g.Key) || items.Any(i => i.Uri == g.Key)) continue;
                var t = g.First();
                items.Add(new PickItem { Uri = g.Key, Kind = ItemKind.Album, Name = t.AlbumName ?? "Album", Subtitle = t.Artists, ImageUrl = t.AlbumImage, Source = $"{g.Count()} of your top tracks", Score = g.Count() });
                if (items.Count >= Settings.FrequentCount) break;
            }
        }
        return items;
    }

    private async Task<PickItem?> ToItemAsync(ContextStat s, IReadOnlyDictionary<string, string> manualNames, CancellationToken ct)
    {
        var source = $"{s.Plays} play{(s.Plays == 1 ? "" : "s")} in {Settings.HistoryDays} days";
        switch (s.Kind)
        {
            case ItemKind.Collection:
                return new PickItem { Uri = s.Uri, Kind = s.Kind, Name = "Liked Songs", Subtitle = "Your library", ImageUrl = s.SampleImage, Source = source, Score = s.Score };
            case ItemKind.Album:
            case ItemKind.Playlist:
            {
                var meta = await ResolveMetaAsync(s.Uri, s.Kind, ct);
                var missing = meta is null or { Missing: true };
                var name = meta is { Missing: false, Name: { } n } ? n : s.SampleName ?? manualNames.GetValueOrDefault(s.Uri) ?? (s.Kind == ItemKind.Playlist ? "Spotify mix" : "Album");
                return new PickItem
                {
                    Uri = s.Uri, Kind = s.Kind, Name = name,
                    Subtitle = meta is { Missing: false } ? meta.Subtitle ?? s.SampleSubtitle : s.SampleSubtitle ?? (s.Kind == ItemKind.Playlist ? "Playlist" : null),
                    ImageUrl = (meta is { Missing: false } ? meta.ImageUrl : null) ?? s.SampleImage,
                    Source = source, Score = s.Score, MetadataMissing = missing,
                };
            }
            default: return null;
        }
    }

    /// <summary>Name/cover for a URI from the cache or Spotify; a refusal is cached so we do not keep asking.</summary>
    public async Task<MetadataCache.Entry?> ResolveMetaAsync(string uri, ItemKind kind, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (Meta.Get(uri, now) is { } cached) return cached;
        var id = uri[(uri.LastIndexOf(':') + 1)..];
        MetadataCache.Entry? entry = null; var refused = false; var failed = false;
        switch (kind)
        {
            case ItemKind.Album:
            {
                var f = await Client.AlbumAsync(id, ct);
                if (f.Value is { } a) entry = new() { Uri = uri, Kind = kind, Name = a.Name, Subtitle = a.Artists, ImageUrl = a.ImageUrl, FetchedAt = now };
                else if (f.Refused) refused = true; else failed = true;
                break;
            }
            case ItemKind.Playlist:
            {
                var f = await Client.PlaylistAsync(id, ct);
                if (f.Value is { } p) entry = new() { Uri = uri, Kind = kind, Name = p.Name, Subtitle = MadeForYou.IsSpotifyOwned(p) ? "Made for you" : p.OwnerName is { Length: > 0 } o ? $"by {o}" : "Playlist", ImageUrl = p.ImageUrl, FetchedAt = now };
                else if (f.Refused) refused = true; else failed = true;
                break;
            }
            default: return null;
        }
        if (failed) return Meta.GetStale(uri);
        entry ??= new() { Uri = uri, Kind = kind, FetchedAt = now, Missing = refused };
        Meta.Put(entry);
        return entry;
    }

    private async Task<List<PickItem>> BuildPoolAsync(DateTimeOffset now, CancellationToken ct)
    {
        var pool = new List<PickItem>();
        var saved = await Client.SavedAlbumsAsync(Settings.SavedAlbumPages, ct);
        var savedUris = saved.Select(s => s.Album.Uri).ToHashSet();
        foreach (var (a, _) in saved)
            pool.Add(new PickItem { Uri = a.Uri, Kind = ItemKind.Album, Name = a.Name, Subtitle = a.Artists, ImageUrl = a.ImageUrl, Source = "in your library, not played lately", Score = 3 });

        var artists = new Dictionary<string, ArtistInfo>();
        foreach (var range in new[] { "short_term", "medium_term", "long_term" })
            foreach (var a in await Client.TopArtistsAsync(range, 20, ct)) artists.TryAdd(a.Id, a);
        foreach (var a in await Client.FollowedArtistsAsync(ct)) artists.TryAdd(a.Id, a);

        var rnd = new Random(Recommender.DailySeed(now, 0));
        foreach (var artist in artists.Values.OrderBy(_ => rnd.Next()).Take(Math.Max(1, Settings.PoolArtists)))
        {
            foreach (var al in await Client.ArtistAlbumsAsync(artist.Id, ct))
            {
                if (savedUris.Contains(al.Uri)) continue;
                pool.Add(new PickItem { Uri = al.Uri, Kind = ItemKind.Album, Name = al.Name, Subtitle = al.Artists.Length > 0 ? al.Artists : artist.Name, ImageUrl = al.ImageUrl, Source = $"by {artist.Name}, not in your recent plays", Score = 1 });
            }
        }
        Log.Info($"Recommendation pool: {pool.Count} albums ({saved.Count} saved, {artists.Count} artists)");
        return pool;
    }

    private List<PickItem> PickRecommended(DateTimeOffset now, IEnumerable<PickItem> made, IEnumerable<PickItem> frequent)
    {
        var exclude = new HashSet<string>(_playedLately);
        foreach (var i in made) exclude.Add(i.Uri);
        foreach (var i in frequent) exclude.Add(i.Uri);
        return Recommender.Pick(_pool, exclude, Settings.RecommendedCount, Recommender.DailySeed(now, _reroll));
    }

    private async Task PrefetchArtAsync(IEnumerable<PickItem> items, CancellationToken ct)
    {
        var urls = items.Select(i => i.ImageUrl).Where(u => u is not null).Distinct().ToList();
        await Task.WhenAll(urls.Select(u => Art.GetAsync(u, ct)));
    }

    // ---- playback -----------------------------------------------------------------------

    public async Task RefreshPlaybackAsync(CancellationToken ct)
    {
        if (!Auth.IsConnected) return;
        var now = DateTimeOffset.UtcNow;
        var (state, r) = await Client.PlayerAsync(now, ct);
        if (r.Status is not (200 or 204)) { Log.Debug($"player: {r.Describe()}"); return; }
        if (state?.ImageUrl is { } url) await Art.GetAsync(url, ct);
        var changed = state is null ? Current.Playback is not null : !state.LooksLike(Current.Playback);
        Publish(Current with { Playback = state, At = now }, notify: changed);
    }

    private void PokePlaybackSoon(int ms = 700) => _ = Task.Run(async () => { await Task.Delay(ms); RequestPlayback(); });

    public async Task<Device?> ChooseDeviceAsync(CancellationToken ct)
    {
        var devices = await Client.DevicesAsync(ct);
        if (devices.Count == 0) return null;
        var pref = Settings.PreferredDevice.Trim();
        if (pref.Length > 0 && devices.FirstOrDefault(d => d.Name.Contains(pref, StringComparison.OrdinalIgnoreCase)) is { } p) return p;
        if (devices.FirstOrDefault(d => d.IsActive) is { } active) return active;
        var host = HostName();
        if (devices.FirstOrDefault(d => string.Equals(d.Name, host, StringComparison.OrdinalIgnoreCase)) is { } mine) return mine;
        return devices.FirstOrDefault(d => d.Type.Equals("Computer", StringComparison.OrdinalIgnoreCase)) ?? devices[0];
    }

    /// <summary>Starts a context (album / playlist / Liked Songs) on the active device, else on the best available one, else via MPRIS.</summary>
    public async Task<bool> PlayAsync(string uri, CancellationToken ct)
    {
        var r = await Client.PlayAsync(uri, null, ct);
        if (!r.Ok && r.NoDevice)
        {
            var dev = await ChooseDeviceAsync(ct);
            if (dev is not null) r = await Client.PlayAsync(uri, dev.Id, ct);
        }
        var ok = r.Ok;
        if (!ok)
        {
            Log.Warn($"play {uri}: {r.Describe()}");
            ok = await LocalPlayer.OpenAsync(uri);
        }
        if (ok) { Log.Info($"Playing {uri}"); PokePlaybackSoon(); }
        return ok;
    }

    public async Task<bool> PlayPauseAsync(CancellationToken ct)
    {
        var playing = Current.Playback?.IsPlaying ?? false;
        var r = playing ? await Client.PauseAsync(ct) : await Client.PlayAsync(null, null, ct);
        if (!r.Ok && !playing && r.NoDevice)
        {
            var dev = await ChooseDeviceAsync(ct);
            if (dev is not null) r = await Client.TransferAsync(dev.Id, true, ct);
        }
        var ok = r.Ok || await LocalPlayer.PlayPauseAsync();
        if (ok) PokePlaybackSoon();
        return ok;
    }

    public async Task<bool> NextAsync(CancellationToken ct)
    {
        var r = await Client.NextAsync(ct);
        var ok = r.Ok || await LocalPlayer.NextAsync();
        if (ok) PokePlaybackSoon(1200);
        return ok;
    }

    public async Task<bool> PreviousAsync(CancellationToken ct)
    {
        var r = await Client.PreviousAsync(ct);
        var ok = r.Ok || await LocalPlayer.PreviousAsync();
        if (ok) PokePlaybackSoon(1200);
        return ok;
    }

    /// <summary>Volume by a relative step; sends are coalesced so a quick spin is one request.</summary>
    public void VolumeDelta(int delta)
    {
        var cur = Interlocked.CompareExchange(ref _pendingVolume, 0, 0);
        if (cur < 0) cur = Current.Playback?.VolumePercent ?? 50;
        var target = Math.Clamp(cur + delta, 0, 100);
        Interlocked.Exchange(ref _pendingVolume, target);
        if (Current.Playback is { } p) Publish(Current with { Playback = p with { VolumePercent = target } }, notify: false);
        if (Interlocked.CompareExchange(ref _volumeSending, 1, 0) == 0) _ = Task.Run(SendVolumeAsync);
    }

    private async Task SendVolumeAsync()
    {
        try
        {
            await Task.Delay(150);
            int last;
            do
            {
                last = Interlocked.CompareExchange(ref _pendingVolume, 0, 0);
                var r = await Client.VolumeAsync(last, _cts.Token);
                if (!r.Ok) { Log.Debug($"volume: {r.Describe()}"); await LocalPlayer.VolumeAsync(last); }
                await Task.Delay(100);
            } while (Interlocked.CompareExchange(ref _pendingVolume, 0, 0) != last);
        }
        catch (Exception ex) { Log.Debug($"volume: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _volumeSending, 0); Interlocked.Exchange(ref _pendingVolume, -1); PokePlaybackSoon(1500); }
    }

    public object Describe() => new
    {
        connected = Current.Connected, user = Current.UserName, error = Current.Error, hidden = Current.HiddenPlaylists, plays = Current.HistoryPlays,
        listsAt = Current.ListsAt == default ? null : Current.ListsAt.ToLocalTime().ToString("HH:mm"),
        madeForYou = Current.MadeForYou.Select(i => new { i.Name, i.Uri, i.Source, missing = i.MetadataMissing }),
        frequent = Current.Frequent.Select(i => new { i.Name, i.Subtitle, i.Uri, i.Source }),
        recommended = Current.Recommended.Select(i => new { i.Name, i.Subtitle, i.Uri, i.Source }),
        playback = Current.Playback is { } p ? new { p.TrackName, p.Artists, p.IsPlaying, p.DeviceName, p.VolumePercent, p.ContextUri } : null,
        requests = Client.Requests,
    };

    public void Dispose() { _cts.Cancel(); Meta.Save(); History.Save(); }
}
