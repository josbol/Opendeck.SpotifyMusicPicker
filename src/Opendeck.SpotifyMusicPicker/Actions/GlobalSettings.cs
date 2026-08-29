using System.Text.Json;
using Opendeck.SpotifyMusicPicker.Picker;
using Opendeck.SpotifyMusicPicker.Spotify;
using Opendeck.SpotifyMusicPicker.Util;

namespace Opendeck.SpotifyMusicPicker.Actions;

/// <summary>Plugin-wide settings (stored by OpenDeck through setGlobalSettings; edited in any property inspector).</summary>
public sealed class GlobalSettings
{
    public string ClientId { get; set; } = "";
    public int RedirectPort { get; set; } = 43118;
    public string PickerProfile { get; set; } = "Spotify";
    public string MainProfile { get; set; } = "Default";
    public int HistoryDays { get; set; } = 30;
    public int RefreshMinutes { get; set; } = 15;
    public int PlaybackSeconds { get; set; } = 5;
    public int VolumeStep { get; set; } = 5;
    public string PreferredDevice { get; set; } = "";
    public string MadeForYouLinks { get; set; } = "";
    public bool ShowLabels { get; set; } = true;
    public int TickSeconds { get; set; } = 30;

    public static GlobalSettings From(JsonElement e)
    {
        var s = new GlobalSettings();
        if (e.ValueKind != JsonValueKind.Object) return s;
        s.ClientId = e.Str("clientId")?.Trim() ?? s.ClientId;
        s.RedirectPort = (int)(e.Long("redirectPort") ?? s.RedirectPort);
        s.PickerProfile = e.Str("pickerProfile") is { Length: > 0 } pp ? pp : s.PickerProfile;
        s.MainProfile = e.Str("mainProfile") is { Length: > 0 } mp ? mp : s.MainProfile;
        s.HistoryDays = (int)(e.Long("historyDays") ?? s.HistoryDays);
        s.RefreshMinutes = (int)(e.Long("refreshMinutes") ?? s.RefreshMinutes);
        s.PlaybackSeconds = (int)(e.Long("playbackSeconds") ?? s.PlaybackSeconds);
        s.VolumeStep = (int)(e.Long("volumeStep") ?? s.VolumeStep);
        s.PreferredDevice = e.Str("preferredDevice") ?? s.PreferredDevice;
        s.MadeForYouLinks = e.Str("madeForYouLinks") ?? s.MadeForYouLinks;
        s.ShowLabels = e.Bool("showLabels") ?? s.ShowLabels;
        s.TickSeconds = (int)(e.Long("tickSeconds") ?? s.TickSeconds);
        return s;
    }

    /// <summary>Reads the settings OpenDeck persisted for this plugin (for the CLI modes, which have no socket).</summary>
    public static GlobalSettings LoadFromDisk()
    {
        try { if (File.Exists(Paths.PluginSettingsFile)) return From(JsonDocument.Parse(File.ReadAllText(Paths.PluginSettingsFile)).RootElement); }
        catch (Exception ex) { Log.Warn($"settings: {ex.Message}"); }
        return new GlobalSettings();
    }

    /// <summary>The client id to use: the configured one, else the (public) id of the "Essentials for Spotify" plugin if installed.</summary>
    public (string Id, string Source) EffectiveClientId()
    {
        if (ClientId.Length > 0) return (ClientId, "settings");
        var borrowed = EssentialsClientId();
        return borrowed is null ? ("", "none") : (borrowed, "Essentials for Spotify");
    }

    public static string? EssentialsClientId()
    {
        try
        {
            if (!File.Exists(Paths.EssentialsSettingsFile)) return null;
            var id = JsonDocument.Parse(File.ReadAllText(Paths.EssentialsSettingsFile)).RootElement.Str("clientId")?.Trim();
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch { return null; }
    }

    public void ApplyTo(CuratorSettings c)
    {
        c.HistoryDays = Math.Max(1, HistoryDays);
        c.RefreshMinutes = Math.Max(1, RefreshMinutes);
        c.PlaybackSeconds = Math.Max(2, PlaybackSeconds);
        c.VolumeStep = Math.Clamp(VolumeStep, 1, 50);
        c.PreferredDevice = PreferredDevice;
        c.MadeForYouLinks = MadeForYouLinks;
    }
}
