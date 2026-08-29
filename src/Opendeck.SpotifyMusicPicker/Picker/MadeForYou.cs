using System.Text.RegularExpressions;
using Opendeck.SpotifyMusicPicker.Spotify;

namespace Opendeck.SpotifyMusicPicker.Picker;

public sealed record ManualLink(string Uri, string? Name);

/// <summary>The playlists Spotify generates for the user (Daily Mix, Discover Weekly, …).</summary>
public static partial class MadeForYou
{
    /// <summary>Display order; anything else Spotify-owned follows alphabetically.</summary>
    public static readonly string[] Order =
    {
        "Daily Mix 1", "Daily Mix 2", "Daily Mix 3", "Daily Mix 4", "Daily Mix 5", "Daily Mix 6",
        "daylist", "Discover Weekly", "Release Radar", "On Repeat", "Repeat Rewind", "Time Capsule",
        "Your Top Songs", "Your Summer Rewind", "New Music Friday",
    };

    public static bool IsSpotifyOwned(PlaylistInfo p) => string.Equals(p.OwnerId, "spotify", StringComparison.OrdinalIgnoreCase);

    public static int Rank(string? name)
    {
        if (string.IsNullOrEmpty(name)) return 1000;
        for (var i = 0; i < Order.Length; i++)
            if (name.StartsWith(Order[i], StringComparison.OrdinalIgnoreCase)) return i;
        return 500;
    }

    /// <summary>
    /// Spotify-owned playlists from the user's library (when the app may see them) followed by manually
    /// configured links, in the canonical order. <paramref name="learnedArt"/> gives stand-in covers for links.
    /// </summary>
    public static List<PickItem> Build(IEnumerable<PlaylistInfo> mine, IEnumerable<ManualLink> manual, IReadOnlyDictionary<string, string>? learnedArt = null)
    {
        var list = new List<PickItem>(); var seen = new HashSet<string>();
        foreach (var p in mine)
            if (IsSpotifyOwned(p) && seen.Add(p.Uri))
                list.Add(new PickItem { Uri = p.Uri, Kind = ItemKind.Playlist, Name = p.Name, Subtitle = "Made for you", ImageUrl = p.ImageUrl, Source = "Spotify-owned playlist in your library" });
        foreach (var m in manual)
            if (seen.Add(m.Uri))
                list.Add(new PickItem
                {
                    Uri = m.Uri, Kind = PickItem.KindOf(m.Uri), Name = m.Name ?? "Spotify mix", Subtitle = "Made for you",
                    ImageUrl = learnedArt is not null && learnedArt.TryGetValue(m.Uri, out var art) ? art : null,
                    Source = "configured link", MetadataMissing = true,
                });
        return Sort(list);
    }

    public static List<PickItem> Sort(IEnumerable<PickItem> items) => items.OrderBy(i => Rank(i.Name)).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Name for a playlist Spotify refuses to describe: an algorithmic mix played from the Spotify app.</summary>
    public const string UnnamedMix = "Spotify mix";

    /// <summary>
    /// One link per line: a share URL or URI, optionally followed by "|" (or a space) and a name.
    /// e.g. <c>https://open.spotify.com/playlist/37i9dQZF1E3…?si=x | Daily Mix 1</c>
    /// </summary>
    public static List<ManualLink> ParseLinks(string? text)
    {
        var result = new List<ManualLink>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string link, name;
            var bar = line.IndexOf('|');
            if (bar >= 0) { link = line[..bar].Trim(); name = line[(bar + 1)..].Trim(); }
            else
            {
                var sp = line.IndexOfAny(new[] { ' ', '\t' });
                if (sp >= 0) { link = line[..sp].Trim(); name = line[(sp + 1)..].Trim(); } else { link = line; name = ""; }
            }
            var uri = ToUri(link);
            if (uri is null) continue;
            result.Add(new ManualLink(uri, name.Length > 0 ? name : null));
        }
        return result;
    }

    [GeneratedRegex(@"open\.spotify\.com/(?:intl-[a-z]{2}/)?(playlist|album|artist|show|track)/([A-Za-z0-9]+)")]
    private static partial Regex ShareUrl();

    [GeneratedRegex(@"^spotify:(playlist|album|artist|show|track):([A-Za-z0-9]+)$")]
    private static partial Regex SpotifyUri();

    public static string? ToUri(string link)
    {
        link = link.Trim();
        var m = SpotifyUri().Match(link);
        if (m.Success) return $"spotify:{m.Groups[1].Value}:{m.Groups[2].Value}";
        m = ShareUrl().Match(link);
        return m.Success ? $"spotify:{m.Groups[1].Value}:{m.Groups[2].Value}" : null;
    }
}
