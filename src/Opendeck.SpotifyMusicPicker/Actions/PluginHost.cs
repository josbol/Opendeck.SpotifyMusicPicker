using System.Diagnostics;
using System.Text.Json;
using Opendeck.SpotifyMusicPicker.Deck;
using Opendeck.SpotifyMusicPicker.Picker;
using Opendeck.SpotifyMusicPicker.Rendering;
using Opendeck.SpotifyMusicPicker.Spotify;
using Opendeck.SpotifyMusicPicker.Util;

namespace Opendeck.SpotifyMusicPicker.Actions;

/// <summary>Owns the live action instances, routes deck events to them and re-renders when the snapshot changes.</summary>
public sealed class PluginHost : IAsyncDisposable
{
    public const string UuidPrefix = "com.josbol.spotifymusicpicker.";

    public DeckClient Deck { get; }
    public Curator Curator { get; }
    public KeyRenderer Renderer { get; } = new();
    public GlobalSettings Settings { get; private set; } = new();
    /// <summary>Replaces the OpenDeck CLI call (tests).</summary>
    public Func<string, string, Task<bool>>? ProfileSwitcher { get; set; }

    private readonly Dictionary<string, DeckAction> _actions = new();
    /// <summary>Last image sent per context, kept across willDisappear/willAppear: OpenDeck stores it with the key and
    /// draws it itself when the layout comes back, so sending it again would only make its renderer work twice.</summary>
    private readonly Dictionary<string, string> _sent = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private int _renderPending;
    private Task? _login;

    public PluginHost(DeckClient deck, Curator curator)
    {
        Deck = deck; Curator = curator;
        deck.EventReceived += OnDeckEventAsync;
        curator.Changed += _ => RequestRender();
        curator.Auth.Changed += () => { RequestRender(); BroadcastStatus(); };
        _ = Task.Run(TickLoopAsync);
    }

    // ---- profile switching via the OpenDeck CLI (plugins may not send switchProfile themselves) ----

    public async Task<bool> SwitchProfileAsync(string device, string profile)
    {
        if (ProfileSwitcher is not null) return await ProfileSwitcher(device, profile);
        var message = Json.Serialize(new { @event = "switchProfile", device, profile });
        // Fastest: hand the CLI arguments straight to the running OpenDeck through its single-instance D-Bus hook
        // (what `opendeck --process-message` does after ~300 ms of Tauri start-up). Then the CLI itself.
        var attempts = new List<(string File, string[] Args)>
        {
            ("busctl", new[] { "--user", "--", "call", "me.amankhanna.opendeck.SingleInstance", "/me/amankhanna/opendeck/SingleInstance", "org.SingleInstance.DBus", "ExecuteCallback", "ass", "3", "opendeck", "--process-message", message, Environment.CurrentDirectory }),
            ("opendeck", new[] { "--process-message", message }),
            ("/usr/bin/opendeck", new[] { "--process-message", message }),
            ("flatpak", new[] { "run", "me.amankhanna.opendeck", "--process-message", message }),
        };
        foreach (var (file, args) in attempts)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                var started = Stopwatch.StartNew();
                using var p = Process.Start(psi);
                if (p is null) continue;
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (p.ExitCode != 0) { Log.Debug($"{file} exit {p.ExitCode}: {(await p.StandardError.ReadToEndAsync()).Trim()}"); continue; }
                Log.Info($"switchProfile {device} → '{profile}' via {file} in {started.ElapsedMilliseconds} ms");
                return true;
            }
            catch (Exception ex) { Log.Debug($"{file}: {ex.Message}"); }
        }
        Log.Warn("Could not reach OpenDeck to switch profiles (D-Bus and CLI both failed)");
        return false;
    }

    // ---- login from the property inspector ---------------------------------------------------

    public void StartLogin(string? context)
    {
        if (_login is { IsCompleted: false }) { BroadcastStatus(); return; }
        var (clientId, source) = Settings.EffectiveClientId();
        if (clientId.Length == 0)
        {
            Log.Warn("Login requested without a client id");
            BroadcastStatus("Enter the client id of your Spotify app first (developer.spotify.com/dashboard).");
            return;
        }
        Log.Info($"Starting Spotify login with the client id from {source}");
        _login = Task.Run(async () =>
        {
            var result = await Curator.Auth.LoginAsync(clientId, Settings.RedirectPort, url =>
            {
                Deck.OpenUrl(url);
                BroadcastStatus();
                return Task.CompletedTask;
            }, _cts.Token);
            Log.Info(result.Ok ? $"Login ok: {result.UserName}" : $"Login failed: {result.Error}");
            BroadcastStatus(result.Ok ? null : result.Error);
            if (result.Ok) { Curator.RequestLists(); Curator.RequestPlayback(); }
        });
    }

    public object Status(string? notice = null)
    {
        var (id, source) = Settings.EffectiveClientId();
        return new
        {
            connected = Curator.Auth.IsConnected, user = Curator.Auth.UserName, error = notice ?? Curator.Auth.Error,
            authorizeUrl = Curator.Auth.PendingAuthorizeUrl, clientIdSource = source, clientIdPreview = id.Length > 6 ? id[..6] + "…" : id,
            redirectUri = $"http://127.0.0.1:{Settings.RedirectPort}/callback",
            lists = Curator.Describe(),
        };
    }

    private void BroadcastStatus(string? notice = null)
    {
        List<DeckAction> actions; lock (_lock) actions = _actions.Values.ToList();
        var status = Status(notice);
        foreach (var a in actions) Deck.SendToPropertyInspector(a.Context, new { status, globalSettings = Settings });
    }

    // ---- deck events ---------------------------------------------------------------------

    private async Task OnDeckEventAsync(DeckEvent e)
    {
        switch (e.Event)
        {
            case "willAppear":
            {
                if (e.Context is null || e.Action is null) return;
                var action = Create(e.Action, e.Context);
                if (action is null) { Log.Warn($"Unknown action {e.Action}"); return; }
                action.Device = e.Device; action.Controller = e.Controller; action.Settings = e.Settings.Clone();
                int count; lock (_lock) { _actions[e.Context] = action; count = _actions.Count; action.LastImage = _sent.GetValueOrDefault(e.Context); }
                Log.Info($"willAppear {e.Action} @ {e.Context} ({e.Controller})");
                UpdateVisibility();
                await action.OnAppearAsync();
                RenderOne(action, Curator.Current, DateTimeOffset.UtcNow);
                if (count == 1) Deck.GetGlobalSettings();
                if (Curator.Current.ListsAt == default && action.WantsPlayback) Curator.RequestLists();
                Curator.RequestPlayback();
                break;
            }
            case "willDisappear":
                if (e.Context is not null) lock (_lock) _actions.Remove(e.Context);
                UpdateVisibility();
                Log.Debug($"willDisappear {e.Context}");
                break;
            case "didReceiveSettings":
                if (e.Context is not null && Get(e.Context) is { } a1) { a1.Settings = e.Settings.Clone(); RenderOne(a1, Curator.Current, DateTimeOffset.UtcNow); }
                break;
            case "didReceiveGlobalSettings":
                ApplyGlobalSettings(e.Settings);
                break;
            case "keyDown": if (Get(e.Context) is { } kd) await kd.OnKeyDownAsync(e); break;
            case "keyUp": if (Get(e.Context) is { } ku) await ku.OnKeyUpAsync(e); break;
            case "dialRotate": if (Get(e.Context) is { } dr) await dr.OnDialRotateAsync(e.Ticks); break;
            case "dialDown": if (Get(e.Context) is { } dd) await dd.OnDialDownAsync(); break;
            case "dialUp": if (Get(e.Context) is { } du) await du.OnDialUpAsync(); break;
            case "touchTap": if (Get(e.Context) is { } tt) await tt.OnDialUpAsync(); break;
            case "sendToPlugin":
                switch (e.Payload.Str("command"))
                {
                    case "login": StartLogin(e.Context); break;
                    case "logout": Curator.Auth.Logout(); BroadcastStatus(); break;
                    case "refresh": Curator.RequestLists(); Curator.RequestPlayback(); break;
                    case "reroll": Curator.Reroll(); break;
                    case "status": if (e.Context is not null) Deck.SendToPropertyInspector(e.Context, new { status = Status(), globalSettings = Settings }); break;
                    case "setGlobalSettings":
                        if (e.Payload.Obj("settings") is { } gs) { Deck.SetGlobalSettings(JsonSerializer.Deserialize<JsonElement>(gs.GetRawText())); ApplyGlobalSettings(gs); }
                        break;
                }
                if (Get(e.Context) is { } sp) await sp.OnSendToPluginAsync(e.Payload);
                break;
            case "propertyInspectorDidAppear":
                if (e.Context is not null) Deck.SendToPropertyInspector(e.Context, new { status = Status(), globalSettings = Settings });
                break;
            case "systemDidWakeUp":
                Curator.RequestLists(); Curator.RequestPlayback();
                break;
        }
    }

    private void ApplyGlobalSettings(JsonElement settings)
    {
        Settings = GlobalSettings.From(settings);
        Settings.ApplyTo(Curator.Settings);
        Log.Info($"Global settings: {Json.Serialize(new { Settings.RedirectPort, Settings.PickerProfile, Settings.MainProfile, Settings.HistoryDays, Settings.RefreshMinutes, Settings.PlaybackSeconds, Settings.VolumeStep, Settings.PreferredDevice, Settings.ShowLabels, links = MadeForYou.ParseLinks(Settings.MadeForYouLinks).Count, clientId = Settings.EffectiveClientId().Source })}");
        Curator.RequestLists();
        RequestRender();
    }

    private void UpdateVisibility()
    {
        bool keypad; lock (_lock) keypad = _actions.Values.Any(a => a.WantsPlayback);
        Curator.KeypadVisible = keypad;
    }

    private DeckAction? Get(string? context)
    {
        if (context is null) return null;
        lock (_lock) return _actions.GetValueOrDefault(context);
    }

    private DeckAction? Create(string uuid, string context)
    {
        var name = uuid.StartsWith(UuidPrefix) ? uuid[UuidPrefix.Length..] : uuid;
        return name switch
        {
            "slot" => new SlotAction { Context = context, ActionUuid = uuid, Host = this },
            "nowplaying" => new NowPlayingAction { Context = context, ActionUuid = uuid, Host = this },
            "previous" => new TransportAction { Context = context, ActionUuid = uuid, Host = this, Next = false },
            "next" => new TransportAction { Context = context, ActionUuid = uuid, Host = this, Next = true },
            "volumedial" => new VolumeDialAction { Context = context, ActionUuid = uuid, Host = this },
            "browsedial" => new BrowseDialAction { Context = context, ActionUuid = uuid, Host = this },
            _ => null,
        };
    }

    // ---- rendering -----------------------------------------------------------------------

    public void RequestRender()
    {
        if (Interlocked.Exchange(ref _renderPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(120); // coalesce bursts
            Interlocked.Exchange(ref _renderPending, 0);
            RenderAll();
        });
    }

    private void RenderAll()
    {
        List<DeckAction> actions;
        lock (_lock) actions = _actions.Values.ToList();
        var snap = Curator.Current; var now = DateTimeOffset.UtcNow;
        foreach (var a in actions) RenderOne(a, snap, now);
    }

    private void RenderOne(DeckAction a, Snapshot snap, DateTimeOffset now)
    {
        try
        {
            var img = a.Render(snap, now);
            if (img is null || img == a.LastImage) return;
            a.LastImage = img;
            lock (_lock) _sent[a.Context] = img;
            Deck.SetImage(a.Context, img);
        }
        catch (Exception ex) { Log.Error($"render {a.ActionUuid} failed", ex); }
    }

    private async Task TickLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, Settings.TickSeconds)), _cts.Token); } catch { return; }
            RenderAll();
        }
    }

    public ValueTask DisposeAsync() { _cts.Cancel(); return ValueTask.CompletedTask; }
}
