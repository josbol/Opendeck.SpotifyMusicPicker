using System.Diagnostics;
using System.Text.Json;
using Opendeck.SpotifyMusicPicker.Actions;
using Opendeck.SpotifyMusicPicker.Deck;
using Opendeck.SpotifyMusicPicker.Spotify;
using Opendeck.SpotifyMusicPicker.Tests.Support;
using SkiaSharp;
using Xunit;

namespace Opendeck.SpotifyMusicPicker.Tests;

public class CuratorIntegrationTests : IDisposable
{
    private readonly FakeSpotify _api = new();
    private readonly HttpClient _http = new();
    private readonly Picker.Curator _curator;

    public CuratorIntegrationTests()
    {
        Scenario.SetupRoutes(_api, DateTimeOffset.UtcNow);
        (_curator, _) = Scenario.NewCurator(_api, _http);
    }

    public void Dispose() { _curator.Dispose(); _api.Dispose(); }

    /// <summary>Waits until no request for the route has arrived for <paramref name="quietMs"/> (and at least one has).</summary>
    private async Task WaitForQuietAsync(string method, string path, int quietMs = 400)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        var last = _api.Count(method, path);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(quietMs);
            var n = _api.Count(method, path);
            if (n == last && n > 0) return;
            last = n;
        }
    }

    [Fact]
    public async Task VolumeStepsBuildOnTheLastTargetAndOutrankStalePolls()
    {
        _curator.VolumeHold = TimeSpan.FromMilliseconds(600);
        await _curator.RefreshPlaybackAsync(CancellationToken.None);
        Assert.Equal(62, _curator.Current.Playback!.VolumePercent);

        // a fast spin: 20 ticks in ~400 ms become a handful of requests, the last one carrying the final value
        for (var i = 0; i < 20; i++) { _curator.VolumeDelta(1); await Task.Delay(20); }
        await WaitForQuietAsync("PUT", "/v1/me/player/volume");
        var volumes = _api.Requests.Where(r => r.Path == "/v1/me/player/volume").ToList();
        Assert.InRange(volumes.Count, 1, 5);
        Assert.Contains("volume_percent=82", volumes[^1].Query);

        // Spotify still reports the old volume: the value the dial set wins while the hold lasts…
        await _curator.RefreshPlaybackAsync(CancellationToken.None);
        Assert.Equal(82, _curator.Current.Playback!.VolumePercent);
        _curator.VolumeDelta(-5);                                                          // …and the next step builds on it
        Assert.Equal(77, _curator.Current.Playback!.VolumePercent);
        await WaitForQuietAsync("PUT", "/v1/me/player/volume");
        Assert.Contains("volume_percent=77", _api.Requests.Last(r => r.Path == "/v1/me/player/volume").Query);

        // …until the hold expires; then the poll is the truth again
        await Task.Delay(700);
        await _curator.RefreshPlaybackAsync(CancellationToken.None);
        Assert.Equal(62, _curator.Current.Playback!.VolumePercent);
    }

    [Fact]
    public async Task RateLimitedVolumeDoesNotStallTheDial()
    {
        _api.Json("PUT", "/v1/me/player/volume", FakeSpotify.SpotifyError(429, "Too many requests", "QUOTA_EXCEEDED"), 429);
        _api.Header("PUT", "/v1/me/player/volume", "Retry-After", "20");
        await _curator.RefreshPlaybackAsync(CancellationToken.None);

        var r = await _curator.Client.VolumeAsync(50, CancellationToken.None);            // a 20 s Retry-After is not slept through
        Assert.Equal((429, TimeSpan.FromSeconds(20)), (r.Status, r.RetryAfter));
        Assert.Equal(1, _api.Count("PUT", "/v1/me/player/volume"));

        var sw = Stopwatch.StartNew();
        _curator.VolumeDelta(5);
        await WaitForQuietAsync("PUT", "/v1/me/player/volume");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"dial blocked for {sw.Elapsed}");
        Assert.Equal(2, _api.Count("PUT", "/v1/me/player/volume"));

        _curator.VolumeDelta(5);                                                            // the Web API is left alone for the Retry-After…
        await Task.Delay(600);
        Assert.Equal(2, _api.Count("PUT", "/v1/me/player/volume"));

        await _curator.RefreshPlaybackAsync(CancellationToken.None);                        // …and as nothing was applied, the poll is trusted
        Assert.Equal(62, _curator.Current.Playback!.VolumePercent);
    }

    [Fact]
    public async Task BuildsTheThreeRowsFromWhatSpotifyStillExposes()
    {
        await _curator.RefreshListsAsync(CancellationToken.None);
        var s = _curator.Current;
        Assert.True(s.Connected); Assert.Null(s.Error);
        Assert.Equal(2, s.HiddenPlaylists);

        Assert.Equal(new[] { "Daily Mix 1", "Daily Mix 2", "Daily Mix 9", "Discover Weekly" }, s.MadeForYou.Select(i => i.Name));
        var dm2 = s.MadeForYou[1];
        Assert.False(dm2.MetadataMissing);                                            // the API refused it, oEmbed described it
        Assert.Equal(_api.ImageUrl("blue.png"), dm2.ImageUrl);                       // today's cover from oEmbed, not the learned art
        Assert.False(s.MadeForYou[0].MetadataMissing);
        Assert.Equal("spotify:playlist:HID2", s.MadeForYou[2].Uri);                   // discovered from the play history, named by oEmbed
        Assert.Contains("played from the Spotify app", s.MadeForYou[2].Source);

        // album A (3 plays) > playlist (2) > artist (1); then top tracks fill up, the other edition of album A is skipped
        Assert.Equal(new[] { "spotify:album:A", "spotify:playlist:mine", "spotify:artist:arA", "spotify:album:D", "spotify:album:E" }, s.Frequent.Select(i => i.Uri));
        Assert.Equal(("Artist A", "Artist"), (s.Frequent[2].Name, s.Frequent[2].Subtitle));
        Assert.Equal("3 plays in 30 days", s.Frequent[0].Source); Assert.Equal("Artist A", s.Frequent[0].Subtitle);
        Assert.Equal(("My Jams", "by alex"), (s.Frequent[1].Name, s.Frequent[1].Subtitle));
        Assert.Equal("3 of your top tracks", s.Frequent[3].Source);

        Assert.Equal(3, s.Recommended.Count);
        Assert.All(s.Recommended, r => Assert.Contains(r.Uri, new[] { "spotify:album:S1", "spotify:album:X1", "spotify:album:X2", "spotify:album:Y1" }));
        Assert.Equal(3, s.Recommended.Select(r => r.Subtitle).Distinct().Count());
        Assert.NotNull(_curator.Art.Peek(_api.ImageUrl("red.png")));

        var first = s.Recommended.Select(r => r.Uri).ToList();
        await _curator.RefreshListsAsync(CancellationToken.None);
        Assert.Equal(first, _curator.Current.Recommended.Select(r => r.Uri));      // same day, same picks
        Assert.Equal(1, _api.Count("GET", "/v1/playlists/HID"));                     // the refusal is cached
        Assert.Equal(1, _api.Count("GET", "/v1/playlists/HID2"));
        Assert.Equal(2, _api.Count("GET", "/oembed"));                                // one per hidden mix, cached for hours
        Assert.Equal(1, _api.Count("GET", "/v1/albums/A"));

        _curator.Reroll();
        Assert.Equal(3, _curator.Current.Recommended.Count);
        Assert.Equal(8, s.HistoryPlays);
    }

    [Fact]
    public async Task PlaysOnTheBestDeviceWhenNoneIsActive()
    {
        Assert.True(await _curator.PlayAsync("spotify:album:A", CancellationToken.None));
        var plays = _api.Requests.Where(r => r.Method == "PUT" && r.Path == "/v1/me/player/play").ToList();
        Assert.Equal(2, plays.Count);
        Assert.Equal("", plays[0].Query);
        Assert.Contains("device_id=dev1", plays[1].Query);
        Assert.Contains("\"context_uri\":\"spotify:album:A\"", plays[1].Body);
        Assert.Equal(1, _api.Count("GET", "/v1/me/player/devices"));
    }

    [Fact]
    public async Task TracksPlaybackAndCoalescesVolumeChanges()
    {
        await _curator.RefreshPlaybackAsync(CancellationToken.None);
        var p = _curator.Current.Playback!;
        Assert.Equal(("Song 1", "Artist A", 62, "spotify:album:A"), (p.TrackName, p.Artists, p.VolumePercent, p.ContextUri));
        Assert.NotNull(_curator.Art.Peek(p.ImageUrl));

        _curator.VolumeDelta(5); _curator.VolumeDelta(5);
        Assert.Equal(72, _curator.Current.Playback!.VolumePercent);                    // optimistic
        await Task.Delay(700);
        var volumes = _api.Requests.Where(r => r.Path == "/v1/me/player/volume").ToList();
        Assert.Single(volumes);
        Assert.Contains("volume_percent=72", volumes[0].Query);

        Assert.True(await _curator.NextAsync(CancellationToken.None));
        Assert.True(await _curator.PreviousAsync(CancellationToken.None));
        Assert.True(await _curator.PlayPauseAsync(CancellationToken.None));            // playing → pause
        Assert.Equal(1, _api.Count("POST", "/v1/me/player/next")); Assert.Equal(1, _api.Count("POST", "/v1/me/player/previous")); Assert.Equal(1, _api.Count("PUT", "/v1/me/player/pause"));
    }

    [Fact]
    public async Task LikesAndUnlikesTheCurrentTrack()
    {
        await _curator.RefreshPlaybackAsync(CancellationToken.None);
        Assert.False(_curator.Current.Playback!.Liked);
        Assert.True(await _curator.ToggleLikeAsync(CancellationToken.None));
        Assert.True(_curator.Current.Playback!.Liked);
        Assert.Contains("uris=spotify%3Atrack%3At1", _api.Requests.Last(r => r.Method == "PUT" && r.Path == "/v1/me/library").Query);
        await _curator.RefreshPlaybackAsync(CancellationToken.None);                   // same track: no new contains call
        Assert.Equal(1, _api.Count("GET", "/v1/me/library/contains"));
        Assert.True(await _curator.ToggleLikeAsync(CancellationToken.None));
        Assert.False(_curator.Current.Playback!.Liked);
        Assert.Equal(1, _api.Count("DELETE", "/v1/me/library"));
        Assert.True(_curator.Auth.HasScope("user-library-modify"));
    }

    [Fact]
    public async Task RefusedTokenMeansNotConnected()
    {
        _api.Json("GET", "/v1/me/playlists", FakeSpotify.SpotifyError(401, "The access token expired"), 401);
        _api.Routes["POST /accounts/api/token"] = (_, _) => (400, System.Text.Encoding.UTF8.GetBytes("""{"error":"invalid_grant","error_description":"Refresh token revoked"}"""), "application/json");
        await _curator.RefreshListsAsync(CancellationToken.None);
        Assert.False(_curator.Auth.IsConnected);
        Assert.Contains("invalid_grant", _curator.Auth.Error);
    }
}

public class EndToEndTests
{
    private static (byte R, byte G, byte B) Pixel(JsonElement setImage, int x, int y)
    {
        var url = setImage.GetProperty("payload").GetProperty("image").GetString()!;
        using var bmp = SKBitmap.Decode(Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]));
        Assert.Equal((144, 144), (bmp.Width, bmp.Height));
        var c = bmp.GetPixel(x, y);
        return (c.Red, c.Green, c.Blue);
    }

    private static bool IsSetImage(JsonElement m, string context) => m.GetProperty("event").GetString() == "setImage" && m.GetProperty("context").GetString() == context;

    [Fact]
    public async Task PluginRendersCoversPlaysSkipsAndSwitchesLayouts()
    {
        using var api = new FakeSpotify();
        Scenario.SetupRoutes(api, DateTimeOffset.UtcNow);
        using var http = new HttpClient();
        var (curator, _) = Scenario.NewCurator(api, http);
        using var opendeck = new FakeOpenDeck();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var deck = new DeckClient(opendeck.Port, "com.josbol.spotifymusicpicker.sdPlugin", "registerPlugin", JsonDocument.Parse("{}").RootElement);
        await using var host = new PluginHost(deck, curator);
        var switched = new List<(string Device, string Profile)>();
        host.ProfileSwitcher = (d, p) => { switched.Add((d, p)); return Task.FromResult(true); };
        curator.Start();
        var run = deck.RunAsync(cts.Token);
        await opendeck.Connected.WaitAsync(TimeSpan.FromSeconds(10));
        await opendeck.WaitForAsync(m => m.GetProperty("event").GetString() == "registerPlugin");

        const string slot = PluginHost.UuidPrefix + "slot";
        await opendeck.WillAppearAsync(slot, "Keypad.0.0", "Keypad", new { row = "madeforyou", slot = 1 });
        await opendeck.WillAppearAsync(slot, "Keypad.5.0", "Keypad", new { row = "frequent", slot = 1 });
        await opendeck.WillAppearAsync(slot, "Keypad.10.0", "Keypad", new { row = "recommended", slot = 1 });
        await opendeck.WillAppearAsync(slot, "Keypad.4.0", "Keypad", new { row = "madeforyou", slot = 5 });
        await opendeck.WillAppearAsync(PluginHost.UuidPrefix + "nowplaying", "Keypad.13.0", "Keypad", new { layout = "wide" });
        await opendeck.WillAppearAsync(PluginHost.UuidPrefix + "previous", "Keypad.15.0", "Keypad");
        await opendeck.WillAppearAsync(PluginHost.UuidPrefix + "volumedial", "Encoder.0.0", "Encoder", new { mode = "main" });
        await opendeck.WaitForAsync(m => m.GetProperty("event").GetString() == "getGlobalSettings");
        await opendeck.SendAsync(new { @event = "didReceiveGlobalSettings", payload = new { settings = new { mainProfile = "Main", pickerProfile = "Music", volumeStep = 5, showLabels = true } } });

        // covers: Daily Mix 1 is red, album A (most played) green, every recommended album blue; now playing wide = album A art on the left
        await opendeck.WaitForAsync(m => IsSetImage(m, "Keypad.0.0") && Pixel(m, 72, 40) is { R: > 180, G: < 80, B: < 80 });
        await opendeck.WaitForAsync(m => IsSetImage(m, "Keypad.5.0") && Pixel(m, 72, 40) is { R: < 80, G: > 150, B: < 100 });
        await opendeck.WaitForAsync(m => IsSetImage(m, "Keypad.10.0") && Pixel(m, 72, 40) is { R: < 80, G: < 100, B: > 180 });
        await opendeck.WaitForAsync(m => IsSetImage(m, "Keypad.13.0") && Pixel(m, 30, 60) is { R: < 80, G: > 150, B: < 100 });
        await opendeck.WaitForAsync(m => IsSetImage(m, "Keypad.15.0"));
        // only 4 made-for-you items: slot 5 ends up as the dark "empty" key (its first image may still be the connect placeholder)
        await opendeck.WaitForAsync(m => IsSetImage(m, "Keypad.4.0") && Pixel(m, 72, 40) is { R: < 60, G: < 60, B: < 70 });
        Assert.DoesNotContain(opendeck.Received, m => IsSetImage(m, "Encoder.0.0"));     // dials have no display on the D200X

        // press Daily Mix 1 → played on the laptop after the "no active device" answer
        await opendeck.SendAsync(new { @event = "keyUp", action = slot, context = "Keypad.0.0", device = "ulanzi-d200x", payload = new { controller = "Keypad", settings = new { row = "madeforyou", slot = 1 } } });
        await opendeck.WaitForAsync(m => m.GetProperty("event").GetString() == "showOk" && m.GetProperty("context").GetString() == "Keypad.0.0");
        var play = api.Requests.Last(r => r.Method == "PUT" && r.Path == "/v1/me/player/play");
        Assert.Contains("device_id=dev1", play.Query); Assert.Contains("spotify:playlist:dm1", play.Body);

        // side button → previous track
        await opendeck.SendAsync(new { @event = "keyUp", action = PluginHost.UuidPrefix + "previous", context = "Keypad.15.0", device = "ulanzi-d200x", payload = new { controller = "Keypad", settings = new { } } });
        await opendeck.WaitForAsync(m => m.GetProperty("event").GetString() == "showOk" && m.GetProperty("context").GetString() == "Keypad.15.0");
        Assert.Equal(1, api.Count("POST", "/v1/me/player/previous"));

        // dial: rotate = Spotify volume (62 + 2×5), press = back to the main layout
        await opendeck.SendAsync(new { @event = "dialRotate", action = PluginHost.UuidPrefix + "volumedial", context = "Encoder.0.0", device = "ulanzi-d200x", payload = new { controller = "Encoder", ticks = 2, settings = new { mode = "main" } } });
        var until = DateTime.UtcNow.AddSeconds(5);
        while (api.Count("PUT", "/v1/me/player/volume") == 0 && DateTime.UtcNow < until) await Task.Delay(50);
        Assert.Contains("volume_percent=72", api.Requests.Last(r => r.Path == "/v1/me/player/volume").Query);
        await opendeck.SendAsync(new { @event = "dialUp", action = PluginHost.UuidPrefix + "volumedial", context = "Encoder.0.0", device = "ulanzi-d200x", payload = new { controller = "Encoder", settings = new { mode = "main" } } });
        until = DateTime.UtcNow.AddSeconds(5);
        while (switched.Count == 0 && DateTime.UtcNow < until) await Task.Delay(50);
        Assert.Equal(("ulanzi-d200x", "Main"), Assert.Single(switched));

        // property inspector gets the status
        await opendeck.SendAsync(new { @event = "propertyInspectorDidAppear", action = slot, context = "Keypad.0.0", device = "ulanzi-d200x" });
        var pi = await opendeck.WaitForAsync(m => m.GetProperty("event").GetString() == "sendToPropertyInspector" && m.GetProperty("context").GetString() == "Keypad.0.0");
        Assert.True(pi.GetProperty("payload").GetProperty("status").GetProperty("connected").GetBoolean());
        Assert.Equal("Alex", pi.GetProperty("payload").GetProperty("status").GetProperty("user").GetString());

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
        curator.Dispose();
    }
}
