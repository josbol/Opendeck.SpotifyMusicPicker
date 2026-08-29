using System.Text.Json;
using Opendeck.SpotifyMusicPicker.Deck;
using Opendeck.SpotifyMusicPicker.Spotify;
using Opendeck.SpotifyMusicPicker.Util;

namespace Opendeck.SpotifyMusicPicker.Actions;

/// <summary>One placed instance of an action on the deck.</summary>
public abstract class DeckAction
{
    public required string Context { get; init; }
    public required string ActionUuid { get; init; }
    public required PluginHost Host { get; init; }
    public string? Device { get; set; }
    public string? Controller { get; set; }
    public JsonElement Settings { get; set; }
    public string? LastImage { get; set; }

    /// <summary>Keypad actions want the fast playback poll while on screen; dials do not.</summary>
    public virtual bool WantsPlayback => true;

    public virtual Task OnAppearAsync() => Task.CompletedTask;
    public virtual Task OnKeyDownAsync(DeckEvent e) => Task.CompletedTask;
    public virtual Task OnKeyUpAsync(DeckEvent e) => Task.CompletedTask;
    public virtual Task OnDialRotateAsync(int ticks) => Task.CompletedTask;
    public virtual Task OnDialDownAsync() => Task.CompletedTask;
    public virtual Task OnDialUpAsync() => Task.CompletedTask;
    public virtual Task OnSendToPluginAsync(JsonElement payload) => Task.CompletedTask;

    /// <summary>Returns the image (data URL) for the current snapshot, or null to leave the key alone.</summary>
    public abstract string? Render(Snapshot snapshot, DateTimeOffset now);

    protected string SettingString(string name, string fallback) => Settings.Str(name) is { Length: > 0 } s ? s : fallback;
    protected int SettingInt(string name, int fallback) => (int)(Settings.Long(name) ?? fallback);

    protected void Feedback(bool ok) { if (ok) Host.Deck.ShowOk(Context); else Host.Deck.ShowAlert(Context); }
}

/// <summary>One slot of a row: made-for-you playlist, frequently played item or recommended album. Press → play it.</summary>
public sealed class SlotAction : DeckAction
{
    private PickItem? _shown;

    public string Row => SettingString("row", "madeforyou") switch { "frequent" => "frequent", "recommended" => "recommended", _ => "madeforyou" };
    public int Slot => Math.Max(1, SettingInt("slot", 1));

    public override string? Render(Snapshot s, DateTimeOffset now)
    {
        if (!s.Connected) return Host.Renderer.ConnectKey(s.Error is { } e && e != "not connected" ? e : null);
        _shown = Host.Curator.ItemAt(Row, Slot);
        if (_shown is null)
            return s.Loading ? Host.Renderer.MessageKey("Spotify", "loading…") : Host.Renderer.EmptyKey(RowLabel(Row), Slot);
        var current = s.Playback?.ContextUri is { } ctx && ctx == _shown.Uri;
        return Host.Renderer.CoverKey(_shown, Host.Curator.Art.Peek(_shown.ImageUrl), current, s.Playback?.IsPlaying ?? false, Host.Settings.ShowLabels);
    }

    public static string RowLabel(string row) => row switch { "frequent" => "most played", "recommended" => "for you", _ => "made for you" };

    public override async Task OnKeyUpAsync(DeckEvent e)
    {
        if (!Host.Curator.Auth.IsConnected) { Host.Deck.ShowAlert(Context); return; }
        if (_shown is null) { Host.Curator.RequestLists(); Host.Deck.ShowAlert(Context); return; }
        Feedback(await Host.Curator.PlayAsync(_shown.Uri, CancellationToken.None));
    }
}

/// <summary>Now playing: art, track, progress. Press → play / pause. "wide" layout for the D200X double screen.</summary>
public sealed class NowPlayingAction : DeckAction
{
    public override string? Render(Snapshot s, DateTimeOffset now)
    {
        if (!s.Connected) return Host.Renderer.ConnectKey(null);
        var art = Host.Curator.Art.Peek(s.Playback?.ImageUrl);
        return SettingString("layout", "square") == "wide" ? Host.Renderer.NowPlayingWideKey(s.Playback, art) : Host.Renderer.NowPlayingKey(s.Playback, art);
    }

    public override async Task OnKeyUpAsync(DeckEvent e) => Feedback(await Host.Curator.PlayPauseAsync(CancellationToken.None));
}

/// <summary>Previous / next track (whole song). Meant for the side buttons; draws a glyph if placed on a display key.</summary>
public sealed class TransportAction : DeckAction
{
    public required bool Next { get; init; }
    public override string? Render(Snapshot s, DateTimeOffset now) => Host.Renderer.TransportKey(Next);
    public override async Task OnKeyUpAsync(DeckEvent e) => Feedback(Next ? await Host.Curator.NextAsync(CancellationToken.None) : await Host.Curator.PreviousAsync(CancellationToken.None));
}

/// <summary>Dial: rotate = Spotify volume, press = switch between the main and the picker profile (or play/pause when mode = none).</summary>
public sealed class VolumeDialAction : DeckAction
{
    public override bool WantsPlayback => false;
    public override string? Render(Snapshot s, DateTimeOffset now) => Controller == "Encoder" ? null : Host.Renderer.MessageKey("Volume", "dial: volume · press: switch layout");

    private string Mode => SettingString("mode", "picker");

    public override Task OnDialRotateAsync(int ticks)
    {
        var step = Math.Max(1, SettingInt("step", Host.Settings.VolumeStep));
        Host.Curator.VolumeDelta(ticks * step);
        return Task.CompletedTask;
    }

    public override async Task OnDialUpAsync()
    {
        if (Mode == "none") { await Host.Curator.PlayPauseAsync(CancellationToken.None); return; }
        var profile = SettingString("profile", Mode == "main" ? Host.Settings.MainProfile : Host.Settings.PickerProfile);
        var device = Device;
        if (device is null) { Log.Warn("dial press: no device id"); return; }
        if (!await Host.SwitchProfileAsync(device, profile)) Host.Deck.ShowAlert(Context);
    }

    public override Task OnKeyUpAsync(DeckEvent e) => OnDialUpAsync();
}

/// <summary>Dial: rotate = scroll the made-for-you / most-played rows, press = new recommendations.</summary>
public sealed class BrowseDialAction : DeckAction
{
    public override bool WantsPlayback => false;
    public override string? Render(Snapshot s, DateTimeOffset now) => Controller == "Encoder" ? null : Host.Renderer.MessageKey("Browse", "dial: scroll rows · press: new picks");

    public override Task OnDialRotateAsync(int ticks)
    {
        Host.Curator.Shift(SettingString("row", "both"), ticks);
        return Task.CompletedTask;
    }

    public override Task OnDialUpAsync() { Host.Curator.Reroll(); return Task.CompletedTask; }
    public override Task OnKeyUpAsync(DeckEvent e) => OnDialUpAsync();
}
