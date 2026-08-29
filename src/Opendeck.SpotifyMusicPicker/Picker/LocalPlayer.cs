using System.Diagnostics;
using System.Globalization;
using Opendeck.SpotifyMusicPicker.Util;

namespace Opendeck.SpotifyMusicPicker.Picker;

/// <summary>Fallback control of the local Spotify client over MPRIS (playerctl) when the Web API has no active device.</summary>
public static class LocalPlayer
{
    public static string Player { get; set; } = "spotify";

    public static async Task<bool> RunAsync(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("playerctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(Player);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
            Log.Debug($"playerctl {string.Join(' ', args)} → {p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Log.Debug($"playerctl: {ex.Message}"); return false; }
    }

    public static Task<bool> OpenAsync(string uri) => RunAsync("open", uri);
    public static Task<bool> NextAsync() => RunAsync("next");
    public static Task<bool> PreviousAsync() => RunAsync("previous");
    public static Task<bool> PlayPauseAsync() => RunAsync("play-pause");
    public static Task<bool> VolumeAsync(int percent) => RunAsync("volume", (Math.Clamp(percent, 0, 100) / 100.0).ToString("0.00", CultureInfo.InvariantCulture));
}
