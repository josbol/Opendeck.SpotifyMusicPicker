using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Opendeck.SpotifyMusicPicker.Util;

namespace Opendeck.SpotifyMusicPicker.Picker;

/// <summary>Downloads cover images once and keeps them in memory and on disk (~/.cache).</summary>
public sealed class ArtCache
{
    private readonly HttpClient _http;
    private readonly string? _dir;
    private readonly ConcurrentDictionary<string, byte[]> _mem = new();
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inflight = new();

    public int MaxFiles { get; set; } = 400;

    public ArtCache(HttpClient http, string? dir) { _http = http; _dir = dir; }

    private string? FileFor(string url) => _dir is null ? null : Path.Combine(_dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..24] + ".img");

    /// <summary>Memory / disk only: never touches the network (used while rendering).</summary>
    public byte[]? Peek(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (_mem.TryGetValue(url, out var b)) return b;
        var file = FileFor(url);
        if (file is null || !File.Exists(file)) return null;
        try { b = File.ReadAllBytes(file); _mem[url] = b; return b; } catch { return null; }
    }

    public Task<byte[]?> GetAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return Task.FromResult<byte[]?>(null);
        if (Peek(url) is { } b) return Task.FromResult<byte[]?>(b);
        return _inflight.GetOrAdd(url, u => DownloadAsync(u, ct).ContinueWith(t => { _inflight.TryRemove(u, out _); return t.IsCompletedSuccessfully ? t.Result : null; }, TaskScheduler.Default));
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            var bytes = await _http.GetByteArrayAsync(url, limit.Token);
            if (bytes.Length == 0) return null;
            _mem[url] = bytes;
            var file = FileFor(url);
            if (file is not null)
            {
                Directory.CreateDirectory(_dir!);
                await File.WriteAllBytesAsync(file, bytes, CancellationToken.None);
                Trim();
            }
            return bytes;
        }
        catch (Exception ex) { Log.Debug($"art {url}: {ex.Message}"); return null; }
    }

    private void Trim()
    {
        try
        {
            if (_dir is null) return;
            var files = new DirectoryInfo(_dir).GetFiles("*.img").OrderBy(f => f.LastWriteTimeUtc).ToList();
            foreach (var f in files.Take(Math.Max(0, files.Count - MaxFiles))) f.Delete();
        }
        catch { /* best effort */ }
    }
}
