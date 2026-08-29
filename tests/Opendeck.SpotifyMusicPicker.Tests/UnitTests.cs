using System.Text.Json;
using Opendeck.SpotifyMusicPicker.Actions;
using Opendeck.SpotifyMusicPicker.Deck;
using Opendeck.SpotifyMusicPicker.Picker;
using Opendeck.SpotifyMusicPicker.Spotify;
using Opendeck.SpotifyMusicPicker.Tests.Support;
using Xunit;

namespace Opendeck.SpotifyMusicPicker.Tests;

public class PkceTests
{
    [Fact]
    public void ChallengeMatchesRfc7636Vector()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void VerifierIsUrlSafeAndLongEnough()
    {
        var v = Pkce.NewVerifier();
        Assert.InRange(v.Length, 43, 128);
        Assert.DoesNotContain('+', v); Assert.DoesNotContain('/', v); Assert.DoesNotContain('=', v);
        Assert.NotEqual(v, Pkce.NewVerifier());
    }

    [Fact]
    public void AuthorizeUrlCarriesPkceAndLoopbackRedirect()
    {
        var auth = new SpotifyAuth(new HttpClient(), Path.Combine(TestData.TempDir(), "t.json"));
        var url = auth.BuildAuthorizeUrl("abc", "http://127.0.0.1:43118/callback", "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", "st");
        Assert.StartsWith("https://accounts.spotify.com/authorize?", url);
        Assert.Contains("code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A43118%2Fcallback", url);
        Assert.Contains("scope=user-read-playback-state", url);
        Assert.False(auth.IsConnected);
    }
}

public class HistoryTests
{
    private static PlayEntry Play(DateTimeOffset at, string track, string? album, string? context, string? image = null)
        => new(at, $"spotify:track:{track}", track, "Artist", album is null ? null : $"spotify:album:{album}", album, image ?? $"http://img/{album}", context, context?.Split(':')[1]);

    [Fact]
    public void IngestDeduplicatesAndPersists()
    {
        var file = Path.Combine(TestData.TempDir(), "h.json");
        var h = new PlayHistory(file);
        var t = DateTimeOffset.UtcNow;
        Assert.Equal(2, h.Ingest(new[] { Play(t, "a", "A", "spotify:album:A"), Play(t.AddMinutes(-3), "b", "A", "spotify:album:A") }));
        Assert.Equal(0, h.Ingest(new[] { Play(t, "a", "A", "spotify:album:A") }));
        var again = new PlayHistory(file);
        Assert.Equal(2, again.Count);
        Assert.Equal("spotify:album:A", again.Entries[0].ContextUri);
    }

    [Fact]
    public void RanksByRecencyWeightedPlaysAndFallsBackToAlbumContext()
    {
        var h = new PlayHistory(null);
        var now = DateTimeOffset.UtcNow;
        h.Ingest(new[]
        {
            Play(now.AddDays(-1), "a1", "A", "spotify:album:A"), Play(now.AddDays(-1), "a2", "A", "spotify:album:A"),
            Play(now.AddDays(-40), "p1", "X", "spotify:playlist:P"), Play(now.AddDays(-40), "p2", "X", "spotify:playlist:P"), Play(now.AddDays(-40), "p3", "X", "spotify:playlist:P"),
            Play(now.AddHours(-2), "q", "Q", null),                               // no context → counts for its album
            Play(now.AddHours(-1), "l", "L", "spotify:user:me:collection"),
            Play(now.AddHours(-1), "r", "R", "spotify:artist:Z"),                 // artist contexts are ignored
        }, now);
        var ranked = h.RankContexts(now, 90, 14);
        // two plays yesterday (≈0.95 each) > one play an hour ago > one two hours ago > three plays 40 days ago (≈0.14 each)
        Assert.Equal(new[] { "spotify:album:A", "spotify:user:me:collection", "spotify:album:Q", "spotify:playlist:P" }, ranked.Select(r => r.Uri));
        var a = ranked[0];
        Assert.Equal(2, a.Plays); Assert.Equal("A", a.SampleName); Assert.Equal(ItemKind.Album, a.Kind);
        Assert.Equal("Liked Songs", ranked[1].SampleName);
        Assert.Equal(3, ranked[3].Plays);
        Assert.Null(ranked[3].SampleName);            // playlists need metadata for a name
        Assert.Equal("http://img/X", ranked[3].SampleImage);
        Assert.DoesNotContain(h.RankContexts(now, 30, 14), r => r.Uri == "spotify:playlist:P");   // outside the window
    }

    [Fact]
    public void LearnedArtAndPlayedSince()
    {
        var h = new PlayHistory(null);
        var now = DateTimeOffset.UtcNow;
        h.Ingest(new[] { Play(now.AddDays(-2), "a", "A", "spotify:playlist:P", "http://img/old"), Play(now.AddDays(-1), "b", "B", "spotify:playlist:P", "http://img/new") }, now);
        Assert.Equal("http://img/new", h.LearnedArt()["spotify:playlist:P"]);
        var since = h.UrisPlayedSince(now.AddDays(-1.5));
        Assert.Contains("spotify:playlist:P", since); Assert.Contains("spotify:album:B", since); Assert.DoesNotContain("spotify:album:A", since);
    }
}

public class MadeForYouTests
{
    [Fact]
    public void ParsesLinksInSeveralShapes()
    {
        var links = MadeForYou.ParseLinks("https://open.spotify.com/playlist/37i9dQZF1E39abc?si=xyz | Daily Mix 1\nspotify:playlist:37i9dQZF1E3def Discover Weekly\n# comment\nhttps://open.spotify.com/intl-pt/album/1AbC\n\nnot a link\n");
        Assert.Equal(3, links.Count);
        Assert.Equal(("spotify:playlist:37i9dQZF1E39abc", "Daily Mix 1"), (links[0].Uri, links[0].Name));
        Assert.Equal(("spotify:playlist:37i9dQZF1E3def", "Discover Weekly"), (links[1].Uri, links[1].Name));
        Assert.Equal(("spotify:album:1AbC", (string?)null), (links[2].Uri, links[2].Name));
    }

    [Fact]
    public void OrdersSpotifyOwnedFirstThenLinksAndDeduplicates()
    {
        var mine = new[]
        {
            new PlaylistInfo("spotify:playlist:dw", "dw", "Discover Weekly", "spotify", "Spotify", "http://img/dw", null),
            new PlaylistInfo("spotify:playlist:me", "me", "My Jams", "jos", "Jos", null, null),
            new PlaylistInfo("spotify:playlist:dm2", "dm2", "Daily Mix 2", "spotify", "Spotify", null, null),
            new PlaylistInfo("spotify:playlist:zz", "zz", "Zebra Mix", "spotify", "Spotify", null, null),
        };
        var manual = new[] { new ManualLink("spotify:playlist:dm1", "Daily Mix 1"), new ManualLink("spotify:playlist:dw", "dup"), new ManualLink("spotify:playlist:rr", null) };
        var learned = new Dictionary<string, string> { ["spotify:playlist:dm1"] = "http://img/learned" };
        var items = MadeForYou.Build(mine, manual, learned);
        Assert.Equal(new[] { "Daily Mix 1", "Daily Mix 2", "Discover Weekly", "Spotify mix", "Zebra Mix" }, items.Select(i => i.Name));
        Assert.True(items[0].MetadataMissing); Assert.Equal("http://img/learned", items[0].ImageUrl);
        Assert.False(items[2].MetadataMissing); Assert.Equal("http://img/dw", items[2].ImageUrl);
        Assert.DoesNotContain(items, i => i.Name == "My Jams");
    }

    [Theory]
    [InlineData("Daily Mix 1", 0)] [InlineData("daily mix 6", 5)] [InlineData("daylist • cosy evening", 6)] [InlineData("Release Radar", 8)] [InlineData("Something Else", 500)] [InlineData(null, 1000)]
    public void RankFollowsTheCanonicalOrder(string? name, int rank) => Assert.Equal(rank, MadeForYou.Rank(name));
}

public class RecommenderTests
{
    private static PickItem Album(string id, string artist, double score = 1) => new() { Uri = $"spotify:album:{id}", Kind = ItemKind.Album, Name = id, Subtitle = artist, Score = score };

    [Fact]
    public void ExcludesPlayedPrefersDistinctArtistsAndIsDeterministic()
    {
        var pool = new[] { Album("a1", "A"), Album("a2", "A"), Album("a3", "A"), Album("b1", "B"), Album("c1", "C"), Album("x", "X") };
        var exclude = new HashSet<string> { "spotify:album:x" };
        var first = Recommender.Pick(pool, exclude, 3, 42);
        var second = Recommender.Pick(pool, exclude, 3, 42);
        Assert.Equal(3, first.Count);
        Assert.Equal(first.Select(p => p.Uri), second.Select(p => p.Uri));
        Assert.DoesNotContain(first, p => p.Uri == "spotify:album:x");
        Assert.Equal(3, first.Select(p => p.Subtitle).Distinct().Count());
        Assert.NotEqual(first.Select(p => p.Uri), Recommender.Pick(pool, exclude, 3, 43).Select(p => p.Uri).ToList().Count == 3 && Recommender.Pick(pool, exclude, 3, 7).Select(p => p.Uri).SequenceEqual(first.Select(p => p.Uri)) && Recommender.Pick(pool, exclude, 3, 8).Select(p => p.Uri).SequenceEqual(first.Select(p => p.Uri)) ? first.Select(p => p.Uri) : Array.Empty<string>());
    }

    [Fact]
    public void FillsFromTheSameArtistWhenNothingElseIsLeft()
    {
        var pool = new[] { Album("a1", "A"), Album("a2", "A"), Album("a3", "A") };
        Assert.Equal(3, Recommender.Pick(pool, new HashSet<string>(), 3, 1).Count);
        Assert.Empty(Recommender.Pick(Array.Empty<PickItem>(), new HashSet<string>(), 3, 1));
    }

    [Fact]
    public void DailySeedChangesWithDayAndReroll()
    {
        var d = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(Recommender.DailySeed(d, 0), Recommender.DailySeed(d.AddHours(3), 0));
        Assert.NotEqual(Recommender.DailySeed(d, 0), Recommender.DailySeed(d.AddDays(1), 0));
        Assert.NotEqual(Recommender.DailySeed(d, 0), Recommender.DailySeed(d, 1));
    }
}

public class ModelAndParsingTests
{
    [Theory]
    [InlineData("spotify:album:1", ItemKind.Album)] [InlineData("spotify:playlist:1", ItemKind.Playlist)] [InlineData("spotify:user:jos:collection", ItemKind.Collection)]
    [InlineData("spotify:artist:1", ItemKind.Artist)] [InlineData("garbage", ItemKind.Unknown)] [InlineData(null, ItemKind.Unknown)]
    public void KindOfUri(string? uri, ItemKind kind) => Assert.Equal(kind, PickItem.KindOf(uri));

    [Fact]
    public void ImageOfPrefersSmallestAtLeast250()
    {
        var e = JsonDocument.Parse("""{"images":[{"url":"640","width":640},{"url":"300","width":300},{"url":"64","width":64}]}""").RootElement;
        Assert.Equal("300", SpotifyClient.ImageOf(e));
        var mosaic = JsonDocument.Parse("""{"images":[{"url":"m","width":null,"height":null}]}""").RootElement;
        Assert.Equal("m", SpotifyClient.ImageOf(mosaic));
        Assert.Null(SpotifyClient.ImageOf(JsonDocument.Parse("{}").RootElement));
    }

    [Fact]
    public void ParsesPlaylistsPlaysAndPlaybackTolerantly()
    {
        Assert.Null(SpotifyClient.ParsePlaylist(JsonDocument.Parse("null").RootElement));
        var p = SpotifyClient.ParsePlaylist(JsonDocument.Parse(JsonSerializer.Serialize(TestData.Playlist("dm1", "Daily Mix 1", "spotify"))).RootElement)!;
        Assert.Equal(("spotify:playlist:dm1", "spotify"), (p.Uri, p.OwnerId));

        var play = SpotifyClient.ParsePlay(JsonDocument.Parse(JsonSerializer.Serialize(TestData.Play(new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero), TestData.Track("t1", "Song", TestData.Album("al", "Album", "ar", "Artist")), "spotify:playlist:pl"))).RootElement)!;
        Assert.Equal("spotify:playlist:pl", play.ContextUri); Assert.Equal("playlist", play.ContextType); Assert.Equal("spotify:album:al", play.AlbumUri); Assert.Equal("http://img/al", play.AlbumImage);
        Assert.Equal(10, play.At.Hour);

        var state = SpotifyClient.ParsePlayback(JsonDocument.Parse("""{"is_playing":true,"progress_ms":50000,"context":{"uri":"spotify:album:al","type":"album"},"device":{"id":"d","name":"laptop","volume_percent":40},"item":{"id":"t","uri":"spotify:track:t","name":"Song","duration_ms":100000,"artists":[{"id":"a","name":"Artist"}],"album":{"id":"al","uri":"spotify:album:al","name":"Album","images":[{"url":"u","width":300}]}}}""").RootElement, DateTimeOffset.UtcNow)!;
        Assert.True(state.IsPlaying); Assert.Equal(25, state.ProgressStep); Assert.Equal(40, state.VolumePercent); Assert.Equal("u", state.ImageUrl); Assert.Equal("laptop", state.DeviceName);
        Assert.True(state.LooksLike(state with { ProgressMs = 50400 }));     // same 2 % step
        Assert.False(state.LooksLike(state with { ProgressMs = 80000 }));
        Assert.False(state.LooksLike(null));

        var episode = SpotifyClient.ParsePlayback(JsonDocument.Parse("""{"is_playing":false,"item":{"id":"e","uri":"spotify:episode:e","name":"Ep","duration_ms":1000,"images":[{"url":"ep","width":300}],"show":{"name":"Show"}}}""").RootElement, DateTimeOffset.UtcNow)!;
        Assert.Equal(("Ep", "Show", "ep"), (episode.TrackName, episode.Artists, episode.ImageUrl));
    }
}

public class DeckEventTests
{
    [Fact]
    public void ParsesTheWireFormat()
    {
        var e = DeckEvent.Parse("""{"event":"dialRotate","action":"com.josbol.spotifymusicpicker.volumedial","context":"Encoder.0.0","device":"ulanzi-d200x","payload":{"controller":"Encoder","ticks":-2,"settings":{"mode":"main"},"coordinates":{"row":0,"column":3}}}""");
        Assert.Equal("dialRotate", e.Event); Assert.Equal(-2, e.Ticks); Assert.Equal("Encoder", e.Controller);
        Assert.Equal((0, 3), e.Coordinates); Assert.Equal("main", e.Settings.GetProperty("mode").GetString());
        var bare = DeckEvent.Parse("""{"event":"systemDidWakeUp"}""");
        Assert.Null(bare.Context); Assert.Null(bare.Coordinates); Assert.Equal(0, bare.Ticks);
    }
}

[Collection("paths")]
public class GlobalSettingsTests
{
    [Fact]
    public void ReadsPartialJsonAndBorrowsTheEssentialsClientId()
    {
        var s = GlobalSettings.From(JsonDocument.Parse("""{"redirectPort":5000,"pickerProfile":"","madeForYouLinks":"x","showLabels":false}""").RootElement);
        Assert.Equal(5000, s.RedirectPort); Assert.Equal("Spotify", s.PickerProfile); Assert.Equal("x", s.MadeForYouLinks); Assert.False(s.ShowLabels); Assert.Equal(30, s.HistoryDays);

        var cfg = TestData.TempDir(); var old = Paths.OpenDeckConfigDir;
        try
        {
            Paths.OpenDeckConfigDir = cfg;
            Assert.Equal(("", "none"), s.EffectiveClientId());
            Directory.CreateDirectory(Path.Combine(cfg, "settings"));
            File.WriteAllText(Paths.EssentialsSettingsFile, """{"clientId":"borrowed123","clientSecret":"never-read"}""");
            Assert.Equal(("borrowed123", "Essentials for Spotify"), s.EffectiveClientId());
            s.ClientId = "mine";
            Assert.Equal(("mine", "settings"), s.EffectiveClientId());
        }
        finally { Paths.OpenDeckConfigDir = old; }
    }
}
