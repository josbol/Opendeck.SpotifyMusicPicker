using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Opendeck.SpotifyMusicPicker.Tests.Support;

/// <summary>A WebSocket server playing OpenDeck: accepts the plugin, records everything it sends, and lets a test send events.</summary>
public sealed class FakeOpenDeck : IDisposable
{
    private readonly TcpListener _tcp;
    private WebSocket? _ws;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Port { get; }
    public ConcurrentQueue<JsonElement> Received { get; } = new();
    public event Action<JsonElement>? Message;

    public FakeOpenDeck()
    {
        _tcp = new TcpListener(IPAddress.Loopback, 0); _tcp.Start();
        Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    public Task Connected => _connected.Task;

    private async Task AcceptAsync()
    {
        try
        {
            using var client = await _tcp.AcceptTcpClientAsync(_cts.Token);
            var stream = client.GetStream();
            var buf = new byte[16 * 1024]; var sb = new StringBuilder();
            while (!sb.ToString().Contains("\r\n\r\n"))
            {
                var n = await stream.ReadAsync(buf, _cts.Token);
                if (n == 0) return;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            }
            var key = Regex.Match(sb.ToString(), @"Sec-WebSocket-Key:\s*(\S+)", RegexOptions.IgnoreCase).Groups[1].Value;
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _cts.Token);
            _ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
            _connected.TrySetResult();
            var rbuf = new byte[512 * 1024]; var text = new StringBuilder();
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(rbuf, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                text.Append(Encoding.UTF8.GetString(rbuf, 0, r.Count));
                if (!r.EndOfMessage) continue;
                var el = JsonDocument.Parse(text.ToString()).RootElement.Clone(); text.Clear();
                Received.Enqueue(el); Message?.Invoke(el);
            }
        }
        catch (Exception) { /* test over */ }
    }

    public async Task SendAsync(object message)
    {
        if (_ws is null) throw new InvalidOperationException("plugin not connected");
        await _ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, _cts.Token);
    }

    public Task WillAppearAsync(string action, string context, string controller, object? settings = null, string device = "ulanzi-d200x")
        => SendAsync(new { @event = "willAppear", action, context, device, payload = new { controller, settings = settings ?? new { }, coordinates = new { row = 0, column = 0 }, isInMultiAction = false } });

    /// <summary>Waits until a message matching the predicate has arrived (including ones already received).</summary>
    public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> predicate, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < until)
        {
            var hit = Received.FirstOrDefault(predicate);
            if (hit.ValueKind != JsonValueKind.Undefined) return hit;
            await Task.Delay(50);
        }
        throw new TimeoutException("no matching message from the plugin; got: " + string.Join(", ", Received.Select(m => m.GetProperty("event").GetString() + "@" + (m.TryGetProperty("context", out var c) ? c.ToString() : "-")).Distinct()));
    }

    public void Dispose() { _cts.Cancel(); try { _tcp.Stop(); } catch { } }
}
