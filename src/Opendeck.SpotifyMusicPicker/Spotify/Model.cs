namespace Opendeck.SpotifyMusicPicker.Spotify;

public enum ItemKind { Album, Playlist, Collection, Artist, Show, Track, Unknown }

/// <summary>Something that can be put on a key and played: an album, a playlist, Liked Songs…</summary>
public sealed record PickItem
{
    public required string Uri { get; init; }
    public required ItemKind Kind { get; init; }
    public required string Name { get; init; }
    public string? Subtitle { get; init; }
    public string? ImageUrl { get; init; }
    /// <summary>Why it is here (shown in the property inspector and by --dump).</summary>
    public string Source { get; init; } = "";
    public double Score { get; init; }
    /// <summary>Spotify refused the metadata (algorithmic playlist on an app without the old quota extension).</summary>
    public bool MetadataMissing { get; init; }

    public string Id => Uri[(Uri.LastIndexOf(':') + 1)..];

    public static ItemKind KindOf(string? uri)
    {
        if (uri is null) return ItemKind.Unknown;
        if (uri.EndsWith(":collection", StringComparison.Ordinal)) return ItemKind.Collection;
        var parts = uri.Split(':');
        if (parts.Length < 3 || parts[0] != "spotify") return ItemKind.Unknown;
        return parts[1] switch
        {
            "album" => ItemKind.Album,
            "playlist" => ItemKind.Playlist,
            "artist" => ItemKind.Artist,
            "show" => ItemKind.Show,
            "track" => ItemKind.Track,
            _ => ItemKind.Unknown,
        };
    }
}

public sealed record PlaylistInfo(string Uri, string Id, string Name, string? OwnerId, string? OwnerName, string? ImageUrl, string? Description);
public sealed record AlbumInfo(string Uri, string Id, string Name, string Artists, string? ArtistId, string? ImageUrl, string? ReleaseDate, int? TotalTracks);
public sealed record ArtistInfo(string Uri, string Id, string Name, string? ImageUrl);
public sealed record TrackInfo(string Uri, string Id, string Name, string Artists, string? ArtistId, string? AlbumUri, string? AlbumName, string? AlbumImage, int DurationMs);

/// <summary>One play from /me/player/recently-played (kept in the local history).</summary>
public sealed record PlayEntry(DateTimeOffset At, string TrackUri, string TrackName, string Artists, string? AlbumUri, string? AlbumName, string? AlbumImage, string? ContextUri, string? ContextType);

public sealed record Device(string Id, string Name, string Type, bool IsActive, int? VolumePercent);

public sealed record PlaybackState
{
    public bool IsPlaying { get; init; }
    public string? ContextUri { get; init; }
    public string? TrackUri { get; init; }
    public string? TrackName { get; init; }
    public string? Artists { get; init; }
    public string? AlbumName { get; init; }
    public string? AlbumUri { get; init; }
    public string? ImageUrl { get; init; }
    public int ProgressMs { get; init; }
    public int DurationMs { get; init; }
    public int? VolumePercent { get; init; }
    public string? DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public bool Shuffle { get; init; }
    /// <summary>The current track is in the user's library (Liked Songs); null while unknown.</summary>
    public bool? Liked { get; init; }
    public DateTimeOffset At { get; init; }

    /// <summary>Progress as shown on a key (2 % steps), so the picture only changes when the bar would.</summary>
    public int ProgressStep => DurationMs <= 0 ? 0 : (int)Math.Round(50.0 * Math.Clamp(ProgressMs, 0, DurationMs) / DurationMs);

    /// <summary>Progress quantised to the given number of steps (a coarse bar repaints the key less often).</summary>
    public float ProgressFraction(int steps) => DurationMs <= 0 || steps <= 0 ? 0 : (float)Math.Floor(steps * (double)Math.Clamp(ProgressMs, 0, DurationMs) / DurationMs) / steps;

    public bool LooksLike(PlaybackState? o) => o is not null && o.IsPlaying == IsPlaying && o.ContextUri == ContextUri && o.TrackUri == TrackUri
        && o.VolumePercent == VolumePercent && o.DeviceId == DeviceId && o.ProgressStep == ProgressStep && o.ImageUrl == ImageUrl && o.Liked == Liked;
}

/// <summary>Everything the keys are rendered from. Replaced as a whole on every change.</summary>
public sealed record Snapshot
{
    public IReadOnlyList<PickItem> MadeForYou { get; init; } = Array.Empty<PickItem>();
    public IReadOnlyList<PickItem> Frequent { get; init; } = Array.Empty<PickItem>();
    public IReadOnlyList<PickItem> Recommended { get; init; } = Array.Empty<PickItem>();
    public PlaybackState? Playback { get; init; }
    public bool Connected { get; init; }
    public string? UserName { get; init; }
    public string? Error { get; init; }
    /// <summary>Playlists /me/playlists returned as null: Spotify-owned ones hidden from this app.</summary>
    public int HiddenPlaylists { get; init; }
    public int HistoryPlays { get; init; }
    public DateTimeOffset ListsAt { get; init; }
    public DateTimeOffset At { get; init; }
    public bool Loading => Connected && ListsAt == default;
}
