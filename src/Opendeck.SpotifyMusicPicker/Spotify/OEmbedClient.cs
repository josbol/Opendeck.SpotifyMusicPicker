using System.Text.Json;
using Opendeck.SpotifyMusicPicker.Util;

namespace Opendeck.SpotifyMusicPicker.Spotify;

public sealed record OEmbedInfo(string Title, string? ThumbnailUrl);

/// <summary>
/// Spotify's public oEmbed endpoint (what web embeds use): no authentication, and it still describes the playlists
/// the Web API refuses to Development Mode apps — Daily Mix, Discover Weekly, daylist… — with today's cover.
/// </summary>
public sealed class OEmbedClient
{
    private readonly HttpClient _http;
    public string BaseUrl { get; set; } = "https://open.spotify.com/oembed";

    public OEmbedClient(HttpClient http) => _http = http;

    public async Task<Fetched<OEmbedInfo>> GetAsync(string uri, CancellationToken ct)
    {
        var parts = uri.Split(':');
        if (parts.Length < 3 || parts[0] != "spotify") return new(null, new ApiResponse(0, default, "not a Spotify URI"));
        var url = $"{BaseUrl}?url={Uri.EscapeDataString($"https://open.spotify.com/{parts[1]}/{parts[2]}")}";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            var status = (int)resp.StatusCode;
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Debug($"oembed {uri} → {status}");
                return new(null, new ApiResponse(status, default, text.Length > 0 && text.Length < 200 ? text : resp.ReasonPhrase));
            }
            var e = JsonDocument.Parse(text).RootElement.Clone();
            var title = e.Str("title");
            if (string.IsNullOrWhiteSpace(title)) return new(null, new ApiResponse(status, e, "no title"));
            return new(new OEmbedInfo(title.Trim(), e.Str("thumbnail_url")), new ApiResponse(status, e, null));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(null, new ApiResponse(0, default, "timeout")); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(null, new ApiResponse(0, default, ex.Message)); }
    }
}
