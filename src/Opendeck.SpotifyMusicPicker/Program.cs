using System.Runtime.InteropServices;
using System.Text.Json;
using Opendeck.SpotifyMusicPicker.Actions;
using Opendeck.SpotifyMusicPicker.Deck;
using Opendeck.SpotifyMusicPicker.Picker;
using Opendeck.SpotifyMusicPicker.Rendering;
using Opendeck.SpotifyMusicPicker.Spotify;
using Opendeck.SpotifyMusicPicker.Util;

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("opendeck-spotifymusicpicker/" + (typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0"));

Curator NewCurator()
{
    var auth = new SpotifyAuth(http);
    var curator = new Curator(new SpotifyClient(auth, http), auth, new PlayHistory(Paths.HistoryFile), new MetadataCache(Paths.MetadataFile), new ArtCache(http, Paths.ArtDir));
    GlobalSettings.LoadFromDisk().ApplyTo(curator.Settings);
    return curator;
}

// ---- developer / diagnostic modes (no OpenDeck needed) -------------------------------------------
if (args.Length > 0 && args[0].StartsWith("--"))
{
    switch (args[0])
    {
        case "--login":
        {
            var settings = GlobalSettings.LoadFromDisk();
            var clientId = args.Length > 1 ? args[1] : settings.EffectiveClientId().Id;
            var port = args.Length > 2 && int.TryParse(args[2], out var pp) ? pp : settings.RedirectPort;
            if (clientId.Length == 0) { Console.WriteLine("usage: --login <client id> [port]   (or set the client id in the plugin settings first)"); return 1; }
            var auth = new SpotifyAuth(http);
            Console.WriteLine($"Redirect URI registered in the Spotify app must be: http://127.0.0.1:{port}/callback");
            var result = await auth.LoginAsync(clientId, port, url =>
            {
                Console.WriteLine($"Open this URL in your browser:\n{url}\n");
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", url) { UseShellExecute = false }); } catch { }
                return Task.CompletedTask;
            }, CancellationToken.None);
            Console.WriteLine(result.Ok ? $"Connected as {result.UserName}; tokens in {Paths.TokensFile}" : $"Login failed: {result.Error}");
            return result.Ok ? 0 : 1;
        }
        case "--logout":
            new SpotifyAuth(http).Logout();
            Console.WriteLine("Tokens removed");
            return 0;
        case "--dump":
        {
            using var curator = NewCurator();
            if (!curator.Auth.IsConnected) { Console.WriteLine("Not connected: run --login first"); return 1; }
            await curator.RefreshListsAsync(CancellationToken.None);
            await curator.RefreshPlaybackAsync(CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(curator.Describe(), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        case "--probe":
        {
            using var curator = NewCurator();
            if (!curator.Auth.IsConnected) { Console.WriteLine("Not connected: run --login first"); return 1; }
            Console.WriteLine($"Connected as {curator.Auth.UserName} (scopes: {curator.Auth.Scope})");
            foreach (var (name, path) in new[]
            {
                ("profile", "/me"), ("my playlists", "/me/playlists?limit=50"), ("recently played", "/me/player/recently-played?limit=5"),
                ("top artists", "/me/top/artists?limit=5"), ("top tracks", "/me/top/tracks?limit=5"), ("saved albums", "/me/albums?limit=5"),
                ("followed artists", "/me/following?type=artist&limit=5"), ("player", "/me/player"), ("devices", "/me/player/devices"),
            })
            {
                var r = await curator.Client.GetAsync(path, CancellationToken.None);
                var extra = "";
                if (name == "my playlists" && r.Ok)
                {
                    var items = r.Body.Arr("items").ToList();
                    var hidden = items.Count(i => i.ValueKind != JsonValueKind.Object);
                    var spotify = items.Count(i => i.Obj("owner")?.Str("id") == "spotify");
                    extra = $"  ({items.Count} on the first page, {hidden} hidden by Spotify, {spotify} Spotify-owned visible)";
                }
                if (name == "devices" && r.Ok) extra = "  " + string.Join(", ", r.Body.Arr("devices").Select(d => $"{d.Str("name")} [{d.Str("type")}{(d.Bool("is_active") == true ? ", active" : "")}]"));
                Console.WriteLine($"{name,-18} {r.Describe()}{extra}");
            }
            return 0;
        }
        case "--play":
        {
            if (args.Length < 2) { Console.WriteLine("usage: --play <spotify uri or share link>"); return 1; }
            using var curator = NewCurator();
            var uri = MadeForYou.ToUri(args[1]) ?? args[1];
            Console.WriteLine(await curator.PlayAsync(uri, CancellationToken.None) ? $"playing {uri}" : "failed (see log)");
            return 0;
        }
        case "--render":
        {
            var dir = args.Length > 1 ? args[1] : "render-out";
            Directory.CreateDirectory(dir);
            var r = new KeyRenderer();
            void Save(string name, string dataUrl) => File.WriteAllBytes(Path.Combine(dir, name + ".png"), Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..]));
            var art = SampleArt();
            var mix = new PickItem { Uri = "spotify:playlist:37i9dQZF1E39sample1", Kind = ItemKind.Playlist, Name = "Daily Mix 1", Subtitle = "Made for you" };
            var album = new PickItem { Uri = "spotify:album:sample2", Kind = ItemKind.Album, Name = "The Dark Side of the Moon", Subtitle = "Pink Floyd" };
            var liked = new PickItem { Uri = "spotify:user:me:collection", Kind = ItemKind.Collection, Name = "Liked Songs", Subtitle = "Your library" };
            var manual = new PickItem { Uri = "spotify:playlist:37i9dQZF1E3sample3", Kind = ItemKind.Playlist, Name = "Discover Weekly", Subtitle = "Made for you", MetadataMissing = true };
            Save("mix-art", r.CoverKey(mix, art, false, false, true));
            Save("mix-art-nolabel", r.CoverKey(mix, art, false, false, false));
            Save("album-art-playing", r.CoverKey(album, art, true, true, true));
            Save("album-art-paused", r.CoverKey(album, art, true, false, true));
            Save("album-tile", r.CoverKey(album, null, false, false, true));
            Save("liked-tile", r.CoverKey(liked, null, false, false, true));
            Save("manual-tile", r.CoverKey(manual, null, false, false, true));
            Save("empty", r.EmptyKey("most played", 4));
            Save("connect", r.ConnectKey(null));
            Save("error", r.ConnectKey("token refresh failed (400)"));
            Save("loading", r.MessageKey("Spotify", "loading…"));
            var pb = new PlaybackState { IsPlaying = true, TrackName = "The Great Gig in the Sky", Artists = "Pink Floyd", AlbumName = "The Dark Side of the Moon", ProgressMs = 95_000, DurationMs = 283_000, VolumePercent = 62, DeviceName = "laptop", At = DateTimeOffset.UtcNow };
            Save("nowplaying", r.NowPlayingKey(pb, art));
            Save("nowplaying-paused", r.NowPlayingKey(pb with { IsPlaying = false }, art));
            Save("nowplaying-none", r.NowPlayingKey(null, null));
            Save("nowplaying-wide", r.NowPlayingWideKey(pb, art));
            Save("nowplaying-wide-none", r.NowPlayingWideKey(null, null));
            Save("previous", r.TransportKey(false));
            Save("next", r.TransportKey(true));
            Console.WriteLine($"wrote {Directory.GetFiles(dir).Length} images to {dir}");
            return 0;
        }
        case "--version":
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3));
            return 0;
        default:
            Console.WriteLine("usage: opendeck-spotifymusicpicker [--login [clientId] [port] | --logout | --dump | --probe | --play <uri> | --render <dir> | --version] | -port N -pluginUUID id -registerEvent ev -info json");
            return 1;
    }
}

// ---- plugin mode --------------------------------------------------------------------------------
var deck = DeckClient.FromArgs(args);
if (deck is null)
{
    Console.Error.WriteLine("Missing -port/-pluginUUID; run with --render or --dump for diagnostics.");
    return 2;
}

Log.Info($"Spotify Music Picker starting (pid {Environment.ProcessId})");
using var cts = new CancellationTokenSource();
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; cts.Cancel(); });

using var cur = NewCurator();
await using var host = new PluginHost(deck, cur);
cur.Start();
try { await deck.RunAsync(cts.Token); }
catch (OperationCanceledException) { }
catch (Exception ex) { Log.Error("fatal", ex); return 1; }
Log.Info("exiting");
return 0;

/// <summary>A synthetic "album cover" so --render works without the network.</summary>
static byte[] SampleArt()
{
    using var s = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(300, 300));
    var c = s.Canvas; c.Clear(SkiaSharp.SKColor.Parse("#0B1F3A"));
    using var p = new SkiaSharp.SKPaint { IsAntialias = true };
    for (var i = 0; i < 7; i++) { p.Color = SkiaSharp.SKColor.FromHsl(200 + i * 22, 70, 45 + i * 4); c.DrawCircle(210 - i * 18, 110 + i * 16, 120 - i * 14, p); }
    p.Color = SkiaSharp.SKColors.White.WithAlpha(40); c.DrawRect(new SkiaSharp.SKRect(0, 220, 300, 300), p);
    using var img = s.Snapshot(); using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 85);
    return data.ToArray();
}

public partial class Program { }
