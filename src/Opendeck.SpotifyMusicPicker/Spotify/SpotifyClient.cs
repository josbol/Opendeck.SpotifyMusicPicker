using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Opendeck.SpotifyMusicPicker.Util;

namespace Opendeck.SpotifyMusicPicker.Spotify;

public sealed record ApiResponse(int Status, JsonElement Body, string? Error)
{
    public bool Ok => Status is >= 200 and < 300;
    public bool NotFound => Status == 404;
    /// <summary>Spotify's "NO_ACTIVE_DEVICE" (404) / "no device" (403) answers to player commands.</summary>
    public bool NoDevice => (Status is 404 or 403) && (Error?.Contains("DEVICE", StringComparison.OrdinalIgnoreCase) ?? false);
    public string Describe() => Ok ? $"HTTP {Status}" : $"HTTP {Status}{(Error is null ? "" : ": " + Error)}";
}

public sealed record Fetched<T>(T? Value, ApiResponse Response) where T : class
{
    public bool Ok => Value is not null;
    /// <summary>Spotify definitively refused it (404 / 403) — worth remembering, unlike a network hiccup.</summary>
    public bool Refused => Response.Status is 404 or 403;
}

/// <summary>Thin typed wrapper over the Spotify Web API (only the endpoints still open to Development Mode apps in 2026).</summary>
public sealed class SpotifyClient
{
    private readonly SpotifyAuth _auth;
    private readonly HttpClient _http;

    public string BaseUrl { get; set; } = "https://api.spotify.com/v1";
    public int Requests { get; private set; }

    public SpotifyClient(SpotifyAuth auth, HttpClient http) { _auth = auth; _http = http; }

    // ---- transport ----------------------------------------------------------------------

    public Task<ApiResponse> GetAsync(string pathOrUrl, CancellationToken ct = default) => SendAsync(HttpMethod.Get, pathOrUrl, null, ct);

    public async Task<ApiResponse> SendAsync(HttpMethod method, string pathOrUrl, object? body, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var token = await _auth.GetAccessTokenAsync(ct);
            if (token is null) return new(401, default, _auth.Error ?? "not connected to Spotify");
            using var req = new HttpRequestMessage(method, pathOrUrl.StartsWith("http", StringComparison.Ordinal) ? pathOrUrl : BaseUrl + pathOrUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) req.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try { Requests++; resp = await _http.SendAsync(req, ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(0, default, "timeout"); }
            catch (HttpRequestException ex) { return new(0, default, ex.Message); }
            using (resp)
            {
                var status = (int)resp.StatusCode;
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (status == 401 && attempt == 0) { if (await _auth.ForceRefreshAsync(ct)) continue; return new(401, default, _auth.Error ?? "unauthorized"); }
                if (status == 429 && attempt < 2)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    if (wait <= TimeSpan.FromSeconds(30)) { Log.Warn($"rate limited, waiting {wait.TotalSeconds:0}s"); await Task.Delay(wait + TimeSpan.FromMilliseconds(250), ct); continue; }
                }
                if (status >= 500 && attempt == 0) { await Task.Delay(1000, ct); continue; }
                JsonElement el = default; string? error = null;
                if (text.Length > 0) { try { el = JsonDocument.Parse(text).RootElement.Clone(); } catch { /* not JSON */ } }
                if (status >= 400)
                {
                    error = el.Obj("error") is { } e ? $"{e.Str("reason") ?? ""} {e.Str("message") ?? ""}".Trim() : text;
                    if (error.Length == 0) error = resp.ReasonPhrase;
                    Log.Debug($"{method} {pathOrUrl} → {status} {error}");
                }
                else Log.Debug($"{method} {pathOrUrl} → {status}");
                return new(status, el, error);
            }
        }
        return new(0, default, "gave up");
    }

    // ---- library & personalisation ------------------------------------------------------

    /// <summary>All of the user's playlists. Spotify-owned (algorithmic) ones come back as null for most apps: counted as Hidden.</summary>
    public async Task<(List<PlaylistInfo> Items, int Hidden, string? Error)> MyPlaylistsAsync(CancellationToken ct)
    {
        var items = new List<PlaylistInfo>(); var hidden = 0; string? next = "/me/playlists?limit=50";
        for (var page = 0; next is not null && page < 10; page++)
        {
            var r = await GetAsync(next, ct);
            if (!r.Ok) return (items, hidden, r.Describe());
            foreach (var it in r.Body.Arr("items"))
            {
                var p = ParsePlaylist(it);
                if (p is null) hidden++; else items.Add(p);
            }
            next = r.Body.Str("next");
        }
        return (items, hidden, null);
    }

    public async Task<Fetched<PlaylistInfo>> PlaylistAsync(string id, CancellationToken ct)
    {
        var r = await GetAsync($"/playlists/{id}?fields=uri,id,name,description,images,owner(id,display_name)", ct);
        return new(r.Ok ? ParsePlaylist(r.Body) : null, r);
    }

    public async Task<Fetched<AlbumInfo>> AlbumAsync(string id, CancellationToken ct)
    {
        var r = await GetAsync($"/albums/{id}", ct);
        return new(r.Ok ? ParseAlbum(r.Body) : null, r);
    }

    public async Task<List<AlbumInfo>> ArtistAlbumsAsync(string artistId, CancellationToken ct, int limit = 50)
    {
        var r = await GetAsync($"/artists/{artistId}/albums?include_groups=album&limit={limit}", ct);
        return r.Ok ? r.Body.Arr("items").Select(ParseAlbum).OfType<AlbumInfo>().ToList() : new();
    }

    public async Task<List<ArtistInfo>> TopArtistsAsync(string timeRange, int limit, CancellationToken ct)
    {
        var r = await GetAsync($"/me/top/artists?time_range={timeRange}&limit={limit}", ct);
        return r.Ok ? r.Body.Arr("items").Select(ParseArtist).OfType<ArtistInfo>().ToList() : new();
    }

    public async Task<List<TrackInfo>> TopTracksAsync(string timeRange, int limit, CancellationToken ct)
    {
        var r = await GetAsync($"/me/top/tracks?time_range={timeRange}&limit={limit}", ct);
        return r.Ok ? r.Body.Arr("items").Select(ParseTrack).OfType<TrackInfo>().ToList() : new();
    }

    public async Task<(List<PlayEntry> Items, string? Error)> RecentlyPlayedAsync(CancellationToken ct)
    {
        var r = await GetAsync("/me/player/recently-played?limit=50", ct);
        if (!r.Ok) return (new(), r.Describe());
        return (r.Body.Arr("items").Select(ParsePlay).OfType<PlayEntry>().ToList(), null);
    }

    public async Task<List<(AlbumInfo Album, DateTimeOffset AddedAt)>> SavedAlbumsAsync(int maxPages, CancellationToken ct)
    {
        var list = new List<(AlbumInfo, DateTimeOffset)>(); string? next = "/me/albums?limit=50";
        for (var page = 0; next is not null && page < maxPages; page++)
        {
            var r = await GetAsync(next, ct);
            if (!r.Ok) break;
            foreach (var it in r.Body.Arr("items"))
            {
                var a = it.Obj("album") is { } al ? ParseAlbum(al) : null;
                if (a is null) continue;
                DateTimeOffset.TryParse(it.Str("added_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var added);
                list.Add((a, added));
            }
            next = r.Body.Str("next");
        }
        return list;
    }

    public async Task<List<ArtistInfo>> FollowedArtistsAsync(CancellationToken ct)
    {
        var r = await GetAsync("/me/following?type=artist&limit=50", ct);
        return r.Ok && r.Body.Obj("artists") is { } a ? a.Arr("items").Select(ParseArtist).OfType<ArtistInfo>().ToList() : new();
    }

    public async Task<(string? Id, string? Name)> MeAsync(CancellationToken ct)
    {
        var r = await GetAsync("/me", ct);
        return r.Ok ? (r.Body.Str("id"), r.Body.Str("display_name") ?? r.Body.Str("id")) : (null, null);
    }

    // ---- player -------------------------------------------------------------------------

    /// <summary>Current playback; 204 (nothing playing / no device) gives a null state with an Ok response.</summary>
    public async Task<(PlaybackState? State, ApiResponse Response)> PlayerAsync(DateTimeOffset now, CancellationToken ct)
    {
        var r = await GetAsync("/me/player?additional_types=track,episode", ct);
        return (r.Status == 200 ? ParsePlayback(r.Body, now) : null, r);
    }

    public async Task<List<Device>> DevicesAsync(CancellationToken ct)
    {
        var r = await GetAsync("/me/player/devices", ct);
        return r.Ok
            ? r.Body.Arr("devices").Select(d => new Device(d.Str("id") ?? "", d.Str("name") ?? "", d.Str("type") ?? "", d.Bool("is_active") ?? false, (int?)d.Long("volume_percent"))).Where(d => d.Id.Length > 0).ToList()
            : new();
    }

    public Task<ApiResponse> PlayAsync(string? contextUri, string? deviceId, CancellationToken ct)
    {
        var path = "/me/player/play" + (deviceId is null ? "" : "?device_id=" + Uri.EscapeDataString(deviceId));
        return SendAsync(HttpMethod.Put, path, contextUri is null ? null : new { context_uri = contextUri }, ct);
    }

    public Task<ApiResponse> PauseAsync(CancellationToken ct) => SendAsync(HttpMethod.Put, "/me/player/pause", null, ct);
    public Task<ApiResponse> NextAsync(CancellationToken ct) => SendAsync(HttpMethod.Post, "/me/player/next", null, ct);
    public Task<ApiResponse> PreviousAsync(CancellationToken ct) => SendAsync(HttpMethod.Post, "/me/player/previous", null, ct);
    public Task<ApiResponse> VolumeAsync(int percent, CancellationToken ct) => SendAsync(HttpMethod.Put, $"/me/player/volume?volume_percent={Math.Clamp(percent, 0, 100)}", null, ct);
    public Task<ApiResponse> TransferAsync(string deviceId, bool play, CancellationToken ct) => SendAsync(HttpMethod.Put, "/me/player", new { device_ids = new[] { deviceId }, play }, ct);

    // ---- parsing (tolerant: Spotify keeps removing fields) --------------------------------

    /// <summary>Picks the smallest image that is still ≥ 250 px (keys are 144 px), else the largest.</summary>
    internal static string? ImageOf(JsonElement e, string property = "images")
    {
        string? best = null, largest = null; long bestW = long.MaxValue, largestW = -1;
        foreach (var img in e.Arr(property))
        {
            var url = img.Str("url"); if (url is null) continue;
            var w = img.Long("width") ?? 0;
            if (w > largestW) { largestW = w; largest = url; }
            if (w >= 250 && w < bestW) { bestW = w; best = url; }
        }
        return best ?? largest;
    }

    private static string IdFromUri(string uri) => uri[(uri.LastIndexOf(':') + 1)..];

    internal static PlaylistInfo? ParsePlaylist(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var uri = e.Str("uri"); var id = e.Str("id") ?? (uri is null ? null : IdFromUri(uri));
        if (id is null) return null;
        uri ??= $"spotify:playlist:{id}";
        var owner = e.Obj("owner");
        return new(uri, id, e.Str("name") ?? "Playlist", owner?.Str("id"), owner?.Str("display_name"), ImageOf(e), e.Str("description"));
    }

    internal static AlbumInfo? ParseAlbum(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var uri = e.Str("uri"); var id = e.Str("id") ?? (uri is null ? null : IdFromUri(uri));
        if (id is null) return null;
        uri ??= $"spotify:album:{id}";
        var artists = string.Join(", ", e.Arr("artists").Select(a => a.Str("name")).Where(n => n is not null));
        var artistId = e.Arr("artists").Select(a => a.Str("id")).FirstOrDefault(x => x is not null);
        return new(uri, id, e.Str("name") ?? "Album", artists, artistId, ImageOf(e), e.Str("release_date"), (int?)e.Long("total_tracks"));
    }

    internal static ArtistInfo? ParseArtist(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var uri = e.Str("uri"); var id = e.Str("id") ?? (uri is null ? null : IdFromUri(uri));
        if (id is null) return null;
        return new(uri ?? $"spotify:artist:{id}", id, e.Str("name") ?? "Artist", ImageOf(e));
    }

    internal static TrackInfo? ParseTrack(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var uri = e.Str("uri"); var id = e.Str("id") ?? (uri is null ? null : IdFromUri(uri));
        if (id is null || uri is null) return null;
        var album = e.Obj("album");
        var artists = string.Join(", ", e.Arr("artists").Select(a => a.Str("name")).Where(n => n is not null));
        var artistId = e.Arr("artists").Select(a => a.Str("id")).FirstOrDefault(x => x is not null);
        return new(uri, id, e.Str("name") ?? "Track", artists, artistId, album?.Str("uri"), album?.Str("name"), album is null ? null : ImageOf(album.Value), (int)(e.Long("duration_ms") ?? 0));
    }

    internal static PlayEntry? ParsePlay(JsonElement item)
    {
        if (item.Obj("track") is not { } tr) return null;
        var t = ParseTrack(tr);
        if (t is null) return null;
        if (!DateTimeOffset.TryParse(item.Str("played_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return null;
        var ctx = item.Obj("context");
        return new(at, t.Uri, t.Name, t.Artists, t.AlbumUri, t.AlbumName, t.AlbumImage, ctx?.Str("uri"), ctx?.Str("type"));
    }

    internal static PlaybackState? ParsePlayback(JsonElement e, DateTimeOffset now)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var item = e.Obj("item"); var dev = e.Obj("device"); var ctx = e.Obj("context");
        var t = item is null ? null : ParseTrack(item.Value);
        var image = t?.AlbumImage ?? (item is null ? null : ImageOf(item.Value));   // episodes carry their own images
        if (image is null && item?.Obj("show") is { } show) image = ImageOf(show);
        return new PlaybackState
        {
            IsPlaying = e.Bool("is_playing") ?? false,
            ContextUri = ctx?.Str("uri"),
            TrackUri = t?.Uri ?? item?.Str("uri"),
            TrackName = t?.Name ?? item?.Str("name"),
            Artists = t?.Artists is { Length: > 0 } artists ? artists : item?.Obj("show")?.Str("name"),   // episodes: the show
            AlbumName = t?.AlbumName,
            AlbumUri = t?.AlbumUri,
            ImageUrl = image,
            ProgressMs = (int)(e.Long("progress_ms") ?? 0),
            DurationMs = (int)(item?.Long("duration_ms") ?? 0),
            VolumePercent = (int?)dev?.Long("volume_percent"),
            DeviceId = dev?.Str("id"),
            DeviceName = dev?.Str("name"),
            Shuffle = e.Bool("shuffle_state") ?? false,
            At = now,
        };
    }
}
