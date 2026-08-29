namespace Opendeck.SpotifyMusicPicker.Spotify;

/// <summary>Where the plugin keeps its own files (tokens, listening history, caches). Settable for tests.</summary>
public static class Paths
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Xdg(string variable, params string[] fallback)
    {
        var v = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(v) ? Path.Combine(new[] { Home }.Concat(fallback).ToArray()) : v;
    }

    public static string DataDir { get; set; } = Path.Combine(Xdg("XDG_DATA_HOME", ".local", "share"), "opendeck-spotifymusicpicker");
    public static string CacheDir { get; set; } = Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), "opendeck-spotifymusicpicker");
    public static string OpenDeckConfigDir { get; set; } = Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "opendeck");

    public static string TokensFile => Path.Combine(DataDir, "tokens.json");
    public static string HistoryFile => Path.Combine(DataDir, "history.json");
    public static string MetadataFile => Path.Combine(DataDir, "metadata.json");
    public static string ArtDir => Path.Combine(CacheDir, "art");

    /// <summary>OpenDeck's copy of this plugin's global settings (written by setGlobalSettings).</summary>
    public static string PluginSettingsFile => Path.Combine(OpenDeckConfigDir, "settings", "com.josbol.spotifymusicpicker.sdPlugin.json");
    /// <summary>Settings of the "Essentials for Spotify" plugin, whose (public) client id can be reused.</summary>
    public static string EssentialsSettingsFile => Path.Combine(OpenDeckConfigDir, "settings", "com.ntanis.essentials-for-spotify.sdPlugin.json");
}
