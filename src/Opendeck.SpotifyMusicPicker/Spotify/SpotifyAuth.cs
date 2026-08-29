using System.Net;
using System.Text;
using System.Text.Json;
using Opendeck.SpotifyMusicPicker.Util;

namespace Opendeck.SpotifyMusicPicker.Spotify;

public sealed class TokenSet
{
    public string ClientId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string Scope { get; set; } = "";
    public string? UserName { get; set; }
    public string? UserId { get; set; }
}

public sealed record LoginResult(bool Ok, string? UserName, string? Error);

/// <summary>
/// Spotify OAuth (authorization code + PKCE) with a loopback redirect, plus the token file and refresh logic.
/// Register <c>http://127.0.0.1:PORT/callback</c> as a redirect URI of the app (Spotify no longer accepts "localhost").
/// </summary>
public sealed class SpotifyAuth
{
    public const string Scopes = "user-read-playback-state user-modify-playback-state user-read-currently-playing user-read-recently-played user-top-read user-library-read user-follow-read playlist-read-private playlist-read-collaborative user-read-private";

    public string AccountsUrl { get; set; } = "https://accounts.spotify.com";
    public string ApiUrl { get; set; } = "https://api.spotify.com/v1";

    private readonly HttpClient _http;
    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TokenSet? _tokens;

    public string? Error { get; private set; }
    /// <summary>The authorize URL of a login in progress (so a property inspector can show it as a link).</summary>
    public string? PendingAuthorizeUrl { get; private set; }
    public event Action? Changed;

    public SpotifyAuth(HttpClient http, string? file = null)
    {
        _http = http; _file = file ?? Paths.TokensFile;
        _tokens = Load(_file);
    }

    public bool IsConnected => _tokens is { RefreshToken.Length: > 0 };
    public string? UserName => _tokens?.UserName;
    public string? ClientId => _tokens?.ClientId;
    public string? Scope => _tokens?.Scope;

    // ---- token file -----------------------------------------------------------------------

    private static TokenSet? Load(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var t = JsonSerializer.Deserialize<TokenSet>(File.ReadAllText(file));
            return t is { RefreshToken.Length: > 0 } ? t : null;
        }
        catch (Exception ex) { Log.Warn($"Could not read {file}: {ex.Message}"); return null; }
    }

    private void Save(TokenSet t)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows()) { try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* read-only fs */ } }
        File.Move(tmp, _file, true);
    }

    public void Logout()
    {
        _tokens = null; Error = null;
        try { File.Delete(_file); } catch { }
        Changed?.Invoke();
    }

    // ---- access tokens --------------------------------------------------------------------

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        var t = _tokens;
        if (t is null || t.RefreshToken.Length == 0) return null;
        if (t.AccessToken.Length > 0 && t.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60)) return t.AccessToken;
        await _gate.WaitAsync(ct);
        try
        {
            t = _tokens;
            if (t is null) return null;
            if (t.AccessToken.Length > 0 && t.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60)) return t.AccessToken;
            return await RefreshLockedAsync(t, ct) ? _tokens?.AccessToken : null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Refreshes even if the token does not look expired (after a 401).</summary>
    public async Task<bool> ForceRefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return _tokens is { } t && await RefreshLockedAsync(t, ct); }
        finally { _gate.Release(); }
    }

    private async Task<bool> RefreshLockedAsync(TokenSet t, CancellationToken ct)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = t.RefreshToken, ["client_id"] = t.ClientId };
        string body; int status;
        try
        {
            using var resp = await _http.PostAsync($"{AccountsUrl}/api/token", new FormUrlEncodedContent(form), ct);
            status = (int)resp.StatusCode; body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Error = $"token refresh failed: {ex.Message}"; Log.Warn(Error); return false; }
        if (status is < 200 or >= 300)
        {
            Error = $"token refresh failed ({status}): {Truncate(body)}";
            Log.Warn(Error);
            if (status == 400 && body.Contains("invalid_grant")) { _tokens = null; try { File.Delete(_file); } catch { } Changed?.Invoke(); }
            return false;
        }
        Apply(t, body);
        _tokens = t; Save(t); Error = null;
        Log.Info("Spotify token refreshed");
        Changed?.Invoke();
        return true;
    }

    private static void Apply(TokenSet t, string json)
    {
        var e = JsonDocument.Parse(json).RootElement;
        t.AccessToken = e.Str("access_token") ?? "";
        if (e.Str("refresh_token") is { Length: > 0 } r) t.RefreshToken = r;
        t.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(e.Long("expires_in") ?? 3600);
        if (e.Str("scope") is { } s) t.Scope = s;
    }

    // ---- login ------------------------------------------------------------------------------

    public string BuildAuthorizeUrl(string clientId, string redirectUri, string verifier, string state)
    {
        var q = new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = redirectUri, ["state"] = state,
            ["scope"] = Scopes, ["code_challenge_method"] = "S256", ["code_challenge"] = Pkce.Challenge(verifier),
        };
        return $"{AccountsUrl}/authorize?" + string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<TokenSet> ExchangeCodeAsync(string clientId, string code, string redirectUri, string verifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri, ["client_id"] = clientId, ["code_verifier"] = verifier,
        };
        using var resp = await _http.PostAsync($"{AccountsUrl}/api/token", new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"token exchange failed ({(int)resp.StatusCode}): {Truncate(body)}");
        var t = new TokenSet { ClientId = clientId };
        Apply(t, body);
        if (t.RefreshToken.Length == 0) throw new InvalidOperationException("Spotify returned no refresh token");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiUrl}/me");
            req.Headers.Authorization = new("Bearer", t.AccessToken);
            using var me = await _http.SendAsync(req, ct);
            if (me.IsSuccessStatusCode)
            {
                var e = JsonDocument.Parse(await me.Content.ReadAsStringAsync(ct)).RootElement;
                t.UserName = e.Str("display_name") ?? e.Str("id"); t.UserId = e.Str("id");
            }
        }
        catch (Exception ex) { Log.Debug($"/me after login: {ex.Message}"); }
        _tokens = t; Save(t); Error = null;
        Log.Info($"Connected to Spotify as {t.UserName ?? "?"}");
        Changed?.Invoke();
        return t;
    }

    /// <summary>
    /// Runs the whole browser flow: listens on 127.0.0.1:<paramref name="port"/>, hands the authorize URL to
    /// <paramref name="openUrl"/> (browser / property inspector), waits for the redirect and stores the tokens.
    /// </summary>
    public async Task<LoginResult> LoginAsync(string clientId, int port, Func<string, Task>? openUrl, CancellationToken ct, TimeSpan? timeout = null)
    {
        clientId = clientId.Trim();
        if (clientId.Length == 0) return new(false, null, "no Spotify client id configured");
        var redirect = $"http://127.0.0.1:{port}/callback";
        var verifier = Pkce.NewVerifier(); var state = Pkce.NewState();
        var url = BuildAuthorizeUrl(clientId, redirect, verifier, state);

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { listener.Start(); }
        catch (Exception ex) { Error = $"cannot listen on 127.0.0.1:{port}: {ex.Message}"; return new(false, null, Error); }
        PendingAuthorizeUrl = url; Error = null; Changed?.Invoke();
        try
        {
            if (openUrl is not null) await openUrl(url);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().WaitAsync(limit.Token); }
                catch (OperationCanceledException) { Error = "timed out waiting for the browser"; return new(false, null, Error); }
                var req = ctx.Request; var q = req.QueryString;
                if (req.Url?.AbsolutePath != "/callback") { await ReplyAsync(ctx, 404, "Not found"); continue; }
                if (q["state"] != state) { await ReplyAsync(ctx, 400, "State mismatch — start the login again from OpenDeck."); continue; }
                if (q["error"] is { } err) { await ReplyAsync(ctx, 200, $"Spotify said: {err}. You can close this tab."); Error = err; return new(false, null, err); }
                var code = q["code"];
                if (string.IsNullOrEmpty(code)) { await ReplyAsync(ctx, 400, "Missing code"); continue; }
                try
                {
                    var t = await ExchangeCodeAsync(clientId, code, redirect, verifier, ct);
                    await ReplyAsync(ctx, 200, $"Connected to Spotify as {t.UserName ?? "?"}. You can close this tab and go back to the deck.");
                    return new(true, t.UserName, null);
                }
                catch (Exception ex) { Error = ex.Message; await ReplyAsync(ctx, 500, ex.Message); return new(false, null, ex.Message); }
            }
        }
        finally
        {
            PendingAuthorizeUrl = null;
            try { listener.Stop(); listener.Close(); } catch { }
            Changed?.Invoke();
        }
    }

    private static async Task ReplyAsync(HttpListenerContext ctx, int status, string text)
    {
        try
        {
            var html = $"<!doctype html><meta charset=utf-8><title>OpenDeck · Spotify Music Picker</title><body style=\"font:16px system-ui;background:#121212;color:#eee;display:grid;place-items:center;height:100vh;margin:0\"><div style=\"text-align:center;max-width:32em\"><div style=\"font-size:40px\">{(status == 200 ? "🎵" : "⚠️")}</div><p>{WebUtility.HtmlEncode(text)}</p></div></body>";
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.StatusCode = status; ctx.Response.ContentType = "text/html; charset=utf-8"; ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch { /* browser went away */ }
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
