using SkiaSharp;

namespace Opendeck.SpotifyMusicPicker.Tests.Support;

public static class TestData
{
    public static byte[] Png(byte r, byte g, byte b, int size = 64)
    {
        using var s = SKSurface.Create(new SKImageInfo(size, size));
        s.Canvas.Clear(new SKColor(r, g, b));
        using var img = s.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "smp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public static object Image(string url, int width = 300) => new { url, width, height = width };
    public static object Artist(string id, string name) => new { id, name, uri = $"spotify:artist:{id}", images = new[] { Image($"http://img/{id}") } };
    public static object Album(string id, string name, string artistId, string artistName, string? image = null)
        => new { id, name, uri = $"spotify:album:{id}", album_type = "album", release_date = "2020-01-01", total_tracks = 10, artists = new[] { new { id = artistId, name = artistName } }, images = new[] { Image(image ?? $"http://img/{id}") } };
    public static object Track(string id, string name, object album, string artistId = "ar", string artistName = "Artist")
        => new { id, name, uri = $"spotify:track:{id}", duration_ms = 200_000, artists = new[] { new { id = artistId, name = artistName } }, album };
    public static object Playlist(string id, string name, string ownerId, string? image = null)
        => new { id, name, uri = $"spotify:playlist:{id}", owner = new { id = ownerId, display_name = ownerId }, images = new[] { Image(image ?? $"http://img/{id}") }, description = "" };
    public static object Play(DateTimeOffset at, object track, string? contextUri, string? contextType = null)
        => new { played_at = at.ToString("o"), track, context = contextUri is null ? null : new { uri = contextUri, type = contextType ?? contextUri.Split(':')[1] } };
    public static object Paged(IEnumerable<object?> items, string? next = null) => new { items = items.ToArray(), next, total = items.Count(), limit = 50, offset = 0 };
}
