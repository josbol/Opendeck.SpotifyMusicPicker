using System.Text.Json;
using Opendeck.SpotifyMusicPicker.Picker;
using Opendeck.SpotifyMusicPicker.Spotify;

namespace Opendeck.SpotifyMusicPicker.Tests.Support;

/// <summary>A small Spotify account: two visible + two hidden Spotify playlists, a few plays, some saved albums and artists.</summary>
public static class Scenario
{
    public static readonly byte[] Red = TestData.Png(230, 30, 30), Green = TestData.Png(30, 200, 60), Blue = TestData.Png(30, 60, 230), Grey = TestData.Png(120, 120, 120);

    public static void SetupRoutes(FakeSpotify api, DateTimeOffset now)
    {
        api.Bytes("GET", "/img/red.png", Red, "image/png");
        api.Bytes("GET", "/img/green.png", Green, "image/png");
        api.Bytes("GET", "/img/blue.png", Blue, "image/png");
        api.Bytes("GET", "/img/grey.png", Grey, "image/png");
        string Img(string n) => api.ImageUrl(n + ".png");

        var albumA = TestData.Album("A", "Album A", "arA", "Artist A", Img("green"));
        var albumB = TestData.Album("B", "Album B", "arB", "Artist B", Img("grey"));
        var albumC = TestData.Album("C", "Album C", "arC", "Artist C", Img("grey"));
        var albumD = TestData.Album("D", "Album D", "arD", "Artist D", Img("grey"));
        var albumE = TestData.Album("E", "Album E", "arE", "Artist E", Img("grey"));

        api.Json("GET", "/v1/me", new { id = "jos", display_name = "Jos" });
        api.Json("GET", "/v1/me/playlists", TestData.Paged(new object?[]
        {
            null, TestData.Playlist("dm1", "Daily Mix 1", "spotify", Img("red")), TestData.Playlist("mine", "My Jams", "jos", Img("grey")), null, TestData.Playlist("dw", "Discover Weekly", "spotify", Img("grey")),
        }));
        api.Json("GET", "/v1/me/player/recently-played", TestData.Paged(new object?[]
        {
            TestData.Play(now.AddHours(-1), TestData.Track("t1", "Song 1", albumA, "arA", "Artist A"), "spotify:album:A"),
            TestData.Play(now.AddHours(-2), TestData.Track("t2", "Song 2", albumA, "arA", "Artist A"), "spotify:album:A"),
            TestData.Play(now.AddHours(-3), TestData.Track("t3", "Song 3", albumA, "arA", "Artist A"), "spotify:album:A"),
            TestData.Play(now.AddHours(-4), TestData.Track("t4", "Song 4", albumB, "arB", "Artist B"), "spotify:playlist:mine"),
            TestData.Play(now.AddHours(-5), TestData.Track("t5", "Song 5", albumB, "arB", "Artist B"), "spotify:playlist:mine"),
            TestData.Play(now.AddHours(-6), TestData.Track("t6", "Song 6", albumC, "arC", "Artist C"), "spotify:playlist:HID"),
        }));
        api.Json("GET", "/v1/albums/A", albumA); api.Json("GET", "/v1/albums/B", albumB); api.Json("GET", "/v1/albums/C", albumC); api.Json("GET", "/v1/albums/D", albumD); api.Json("GET", "/v1/albums/E", albumE);
        api.Json("GET", "/v1/playlists/mine", TestData.Playlist("mine", "My Jams", "jos", Img("grey")));
        api.Json("GET", "/v1/playlists/HID", FakeSpotify.SpotifyError(404, "Resource not found"), 404);
        api.Json("GET", "/v1/me/top/tracks", TestData.Paged(new object?[]
        {
            TestData.Track("d1", "D 1", albumD, "arD", "Artist D"), TestData.Track("d2", "D 2", albumD, "arD", "Artist D"), TestData.Track("d3", "D 3", albumD, "arD", "Artist D"), TestData.Track("e1", "E 1", albumE, "arE", "Artist E"),
        }));
        api.Json("GET", "/v1/me/albums", TestData.Paged(new object?[]
        {
            new { added_at = now.AddDays(-30).ToString("o"), album = TestData.Album("S1", "Saved One", "arS", "Artist S", Img("blue")) },
            new { added_at = now.AddDays(-10).ToString("o"), album = albumA },
        }));
        api.Json("GET", "/v1/me/top/artists", TestData.Paged(new object?[] { TestData.Artist("X", "Artist X") }));
        api.Json("GET", "/v1/me/following", new { artists = TestData.Paged(new object?[] { TestData.Artist("Y", "Artist Y") }) });
        api.Json("GET", "/v1/artists/X/albums", TestData.Paged(new object?[] { TestData.Album("X1", "X One", "X", "Artist X", Img("blue")), TestData.Album("X2", "X Two", "X", "Artist X", Img("blue")) }));
        api.Json("GET", "/v1/artists/Y/albums", TestData.Paged(new object?[] { TestData.Album("Y1", "Y One", "Y", "Artist Y", Img("blue")), albumA }));
        api.Json("GET", "/v1/me/player", new
        {
            is_playing = true, progress_ms = 30_000, shuffle_state = false, context = new { uri = "spotify:album:A", type = "album" },
            device = new { id = "dev1", name = "laptopjos", type = "Computer", is_active = true, volume_percent = 62 },
            item = TestData.Track("t1", "Song 1", albumA, "arA", "Artist A"),
        });
        api.Json("GET", "/v1/me/player/devices", new { devices = new[] { new { id = "dev1", name = "laptopjos", type = "Computer", is_active = false, volume_percent = 62 } } });
        api.Json("PUT", "/v1/me/player/play", (req, body) => req.QueryString["device_id"] is null ? (404, FakeSpotify.SpotifyError(404, "Player command failed: No active device found", "NO_ACTIVE_DEVICE")) : (204, null));
        api.Empty("PUT", "/v1/me/player/pause"); api.Empty("PUT", "/v1/me/player/volume"); api.Empty("PUT", "/v1/me/player");
        api.Empty("POST", "/v1/me/player/next"); api.Empty("POST", "/v1/me/player/previous");
    }

    public static (Curator Curator, string Dir) NewCurator(FakeSpotify api, HttpClient http)
    {
        LocalPlayer.Player = "no-such-player-for-tests";   // never touch a real Spotify client from the tests
        var dir = TestData.TempDir();
        var tokens = Path.Combine(dir, "tokens.json");
        File.WriteAllText(tokens, JsonSerializer.Serialize(new TokenSet { ClientId = "cid", AccessToken = "tok", RefreshToken = "ref", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), UserName = "Jos" }));
        var auth = new SpotifyAuth(http, tokens) { AccountsUrl = api.AccountsUrl, ApiUrl = api.ApiUrl };
        var client = new SpotifyClient(auth, http) { BaseUrl = api.ApiUrl };
        var curator = new Curator(client, auth, new PlayHistory(Path.Combine(dir, "history.json")), new MetadataCache(Path.Combine(dir, "meta.json")), new ArtCache(http, Path.Combine(dir, "art")))
        {
            HostName = () => "laptopjos",
        };
        curator.Settings.MadeForYouLinks = "spotify:playlist:HID | Daily Mix 2";
        return (curator, dir);
    }
}
