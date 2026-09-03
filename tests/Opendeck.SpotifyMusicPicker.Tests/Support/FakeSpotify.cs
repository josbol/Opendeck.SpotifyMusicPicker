using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Opendeck.SpotifyMusicPicker.Tests.Support;

/// <summary>A tiny HTTP server standing in for api.spotify.com (and accounts.spotify.com) with canned JSON routes.</summary>
public sealed class FakeSpotify : IDisposable
{
    private readonly HttpListener _listener = new();
    public int Port { get; }
    public string ApiUrl => $"http://127.0.0.1:{Port}/v1";
    public string AccountsUrl => $"http://127.0.0.1:{Port}/accounts";
    public string ImageUrl(string name) => $"http://127.0.0.1:{Port}/img/{name}";

    public ConcurrentQueue<(string Method, string Path, string Query, string Body)> Requests { get; } = new();
    /// <summary>Key: "GET /v1/me/playlists" (path without query). Value: status + body (JSON or bytes).</summary>
    public ConcurrentDictionary<string, Func<HttpListenerRequest, string, (int Status, byte[] Body, string ContentType)>> Routes { get; } = new();
    /// <summary>Extra response headers per route key, e.g. Retry-After on a 429.</summary>
    public ConcurrentDictionary<string, Dictionary<string, string>> Headers { get; } = new();

    public FakeSpotify()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        return p;
    }

    public void Json(string method, string path, object body, int status = 200) => Routes[$"{method} {path}"] = (_, _) => (status, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)), "application/json");
    public void Json(string method, string path, Func<HttpListenerRequest, string, (int Status, object? Body)> handler) => Routes[$"{method} {path}"] = (req, body) =>
    {
        var (status, obj) = handler(req, body);
        return (status, Encoding.UTF8.GetBytes(obj is null ? "" : JsonSerializer.Serialize(obj)), "application/json");
    };
    public void Bytes(string method, string path, byte[] data, string contentType) => Routes[$"{method} {path}"] = (_, _) => (200, data, contentType);
    public void Empty(string method, string path, int status = 204) => Routes[$"{method} {path}"] = (_, _) => (status, Array.Empty<byte>(), "application/json");
    public void Header(string method, string path, string name, string value) => Headers.GetOrAdd($"{method} {path}", _ => new())[name] = value;
    public static object SpotifyError(int status, string message, string? reason = null) => new { error = new { status, message, reason } };

    public int Count(string method, string path) => Requests.Count(r => r.Method == method && r.Path == path);

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(async () =>
            {
                try
                {
                    var req = ctx.Request;
                    string body;
                    using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
                    var path = req.Url!.AbsolutePath;
                    Requests.Enqueue((req.HttpMethod, path, req.Url.Query, body));
                    int status; byte[] data; string type;
                    if (Routes.TryGetValue($"{req.HttpMethod} {path}", out var h)) (status, data, type) = h(req, body);
                    else (status, data, type) = (404, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(SpotifyError(404, $"no route for {req.HttpMethod} {path}"))), "application/json");
                    if (Headers.TryGetValue($"{req.HttpMethod} {path}", out var extra)) foreach (var (k, v) in extra) ctx.Response.AddHeader(k, v);
                    ctx.Response.StatusCode = status; ctx.Response.ContentType = type; ctx.Response.ContentLength64 = data.Length;
                    await ctx.Response.OutputStream.WriteAsync(data);
                    ctx.Response.Close();
                }
                catch { try { ctx.Response.Abort(); } catch { } }
            });
        }
    }

    public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
}
