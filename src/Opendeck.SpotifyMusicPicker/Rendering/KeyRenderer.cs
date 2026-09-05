using SkiaSharp;
using Opendeck.SpotifyMusicPicker.Spotify;
using Opendeck.SpotifyMusicPicker.Util;

namespace Opendeck.SpotifyMusicPicker.Rendering;

/// <summary>
/// Draws key images as PNG data URLs. OpenDeck redraws every key on a 144×144 canvas before it reaches the
/// device, so that is the native size here; the wide "now playing" image is the one exception, sent at the
/// D200X wide screen's own 458×196 (see NowPlayingWideKey).
/// </summary>
public sealed class KeyRenderer
{
    public const int Size = 144;
    /// <summary>The D200X wide screen: two keys plus the gap between them.</summary>
    public const int WideWidth = 458, WideHeight = 196;

    static readonly SKColor Bg = SKColor.Parse("#121212");
    static readonly SKColor Card = SKColor.Parse("#1F1F24");
    static readonly SKColor Text = SKColor.Parse("#F3F4F6");
    static readonly SKColor Muted = SKColor.Parse("#A3A8B5");
    static readonly SKColor Dim = SKColor.Parse("#5B6170");
    static readonly SKColor Green = SKColor.Parse("#1DB954");
    static readonly SKColor Warn = SKColor.Parse("#F59E0B");

    private readonly SKTypeface _regular, _bold;
    private static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    public KeyRenderer() => (_regular, _bold) = LoadFonts();

    private static (SKTypeface, SKTypeface) LoadFonts()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var dir in new[] { Path.Combine(baseDir, "fonts"), Path.Combine(baseDir, "..", "..", "assets", "fonts"), Path.Combine(baseDir, "..", "assets", "fonts"), Path.Combine(baseDir, "assets", "fonts") })
        {
            var r = Path.Combine(dir, "DejaVuSans.ttf"); var b = Path.Combine(dir, "DejaVuSans-Bold.ttf");
            if (File.Exists(r) && File.Exists(b))
            {
                var tr = SKTypeface.FromFile(r); var tb = SKTypeface.FromFile(b);
                if (tr is not null && tb is not null) { Log.Info($"Fonts loaded from {dir}"); return (tr, tb); }
            }
        }
        foreach (var family in new[] { "DejaVu Sans", "Noto Sans", "Liberation Sans", "Cantarell", "sans-serif" })
        {
            var tr = SKTypeface.FromFamilyName(family, SKFontStyle.Normal);
            var tb = SKTypeface.FromFamilyName(family, SKFontStyle.Bold);
            if (tr is not null && tb is not null && tr.FamilyName != "") { Log.Info($"Using system font {tr.FamilyName}"); return (tr, tb); }
        }
        Log.Warn("No font found; using SkiaSharp default");
        return (SKTypeface.Default, SKTypeface.Default);
    }

    // ---- keys -----------------------------------------------------------------------------

    /// <summary>Cover art (or a generated tile) with the name at the bottom; a green frame while it is playing.</summary>
    public string CoverKey(PickItem item, byte[]? art, bool current, bool playing, bool showLabel)
    {
        using var s = NewSurface(); var c = s.Canvas;
        var drewArt = DrawArt(c, art, new SKRect(0, 0, Size, Size));
        if (!drewArt) DrawTile(c, item, new SKRect(0, 0, Size, Size));
        if (showLabel || !drewArt)
        {
            var subtitle = item.Kind is ItemKind.Album or ItemKind.Artist ? item.Subtitle : null;
            var lines = Wrap(item.Name, 15, Size - 16, 2, bold: true).Count;
            var top = Size - (lines * 19 + (subtitle is null ? 0 : 15) + 12);
            Gradient(c, new SKRect(0, top - 30, Size, Size), SKColors.Transparent, SKColors.Black.WithAlpha(215));
            var used = DrawWrapped(c, item.Name, 8, top + 13, 15, Text, Size - 16, 2, bold: true);
            if (subtitle is not null) DrawFitted(c, subtitle, 8, top + 13 + used * 19 + 1, 11, Muted, Size - 16, SKTextAlign.Left);
        }
        if (item.MetadataMissing && !showLabel && !drewArt) DrawText(c, "?", Size - 12, 20, 12, Muted, align: SKTextAlign.Right);
        if (current)
        {
            Border(c, Green, 6);
            Circle(c, new SKPoint(Size - 20, 20), 13, Green);
            if (playing) Triangle(c, new SKPoint(Size - 20, 20), 7, SKColors.Black); else Pause(c, new SKPoint(Size - 20, 20), 6, SKColors.Black);
        }
        return Encode(s, photo: drewArt);
    }

    public string EmptyKey(string rowLabel, int slot)
    {
        using var s = NewSurface(); var c = s.Canvas;
        FillRound(c, new SKRect(10, 10, Size - 10, Size - 10), 10, Card);
        DrawText(c, rowLabel.ToUpperInvariant(), Size / 2f, 60, 11, Dim, bold: true, align: SKTextAlign.Center);
        DrawText(c, $"slot {slot}", Size / 2f, 80, 11, Dim, align: SKTextAlign.Center);
        DrawText(c, "—", Size / 2f, 102, 16, Dim, align: SKTextAlign.Center);
        return Encode(s);
    }

    public string MessageKey(string title, string body, SKColor? accent = null)
    {
        using var s = NewSurface(); var c = s.Canvas;
        Circle(c, new SKPoint(Size / 2f, 44), 18, accent ?? Green);
        DrawText(c, "♪", Size / 2f, 53, 24, SKColors.Black, bold: true, align: SKTextAlign.Center);
        DrawFitted(c, title, Size / 2f, 88, 14, Text, Size - 12, SKTextAlign.Center, bold: true);
        DrawWrapped(c, body, 8, 108, 10, Muted, Size - 16, 2, center: true);
        return Encode(s);
    }

    public string ConnectKey(string? error) => MessageKey("Spotify", error is null ? "connect in the key settings" : error, error is null ? Green : Warn);

    /// <summary>How much detail a now-playing key shows about the position in the song. Every change repaints the
    /// key, and on the D200X repainting the wide screen flashes the firmware's gauges, so the wide layout defaults to coarse.</summary>
    public enum Progress { Off, Coarse, Fine }

    private static int Steps(Progress progress) => progress switch { Progress.Fine => 50, Progress.Coarse => 10, _ => 0 };

    /// <summary>Now playing on a square key.</summary>
    public string NowPlayingKey(PlaybackState? p, byte[]? art, Progress progress = Progress.Fine)
    {
        using var s = NewSurface(); var c = s.Canvas;
        if (p is null || p.TrackName is null)
        {
            Circle(c, new SKPoint(Size / 2f, 58), 26, Card);
            Triangle(c, new SKPoint(Size / 2f + 2, 58), 13, Dim);
            DrawText(c, "nothing playing", Size / 2f, 108, 11, Muted, align: SKTextAlign.Center);
            DrawText(c, "press to resume", Size / 2f, 124, 9, Dim, align: SKTextAlign.Center);
            return Encode(s);
        }
        if (!DrawArt(c, art, new SKRect(0, 0, Size, Size))) Fill(c, new SKRect(0, 0, Size, Size), Card);
        Gradient(c, new SKRect(0, Size - 78, Size, Size), SKColors.Transparent, SKColors.Black.WithAlpha(225));
        var used = DrawWrapped(c, p.TrackName, 8, Size - 44, 13, Text, Size - 16, 2, bold: true);
        DrawFitted(c, p.Artists ?? "", 8, Size - 44 + used * 17 - 1, 10, Muted, Size - 16, SKTextAlign.Left);
        if (progress != Progress.Off) ProgressBar(c, new SKRect(8, Size - 9, Size - 8, Size - 5), p.ProgressFraction(Steps(progress)));
        Circle(c, new SKPoint(Size - 20, 20), 13, p.IsPlaying ? Green : Card);
        if (p.IsPlaying) Pause(c, new SKPoint(Size - 20, 20), 6, SKColors.Black); else Triangle(c, new SKPoint(Size - 19, 20), 7, Text);
        return Encode(s, photo: art is not null);
    }

    /// <summary>Now playing for the D200X wide screen, 458×196: laid out in the 144-tall key space at 336 wide (the same
    /// 458:196) and rasterised at full size. Sent as it is: stock OpenDeck squeezes it into its 144 square, which the D200X
    /// plugin's "stretch" fit expands again, and an OpenDeck that renders the wide key at 458×196 draws it one to one.
    /// Nothing that ticks (elapsed time, volume) is drawn: each change would repaint the screen.</summary>
    public string NowPlayingWideKey(PlaybackState? p, byte[]? art, Progress progress = Progress.Coarse)
    {
        const int W = Size * WideWidth / WideHeight;   // 336
        using var wide = SKSurface.Create(new SKImageInfo(WideWidth, WideHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = wide.Canvas; c.Clear(Bg); c.Scale(WideHeight / (float)Size);
        if (p is null || p.TrackName is null)
        {
            Circle(c, new SKPoint(72, 72), 30, Card);
            Triangle(c, new SKPoint(75, 72), 15, Dim);
            DrawText(c, "Nothing playing", 130, 66, 16, Text, bold: true);
            DrawText(c, "press to resume · pick something on the keys above", 130, 88, 10, Muted);
        }
        else
        {
            if (!DrawArt(c, art, new SKRect(0, 0, Size, Size))) DrawTile(c, new PickItem { Uri = p.AlbumUri ?? p.TrackUri ?? "x", Kind = ItemKind.Album, Name = p.AlbumName ?? p.TrackName }, new SKRect(0, 0, Size, Size));
            const float x = Size + 12; var maxW = W - x - 10;
            var used = DrawWrapped(c, p.TrackName, x, 34, 16, Text, maxW, 2, bold: true);
            var y = 34 + used * 20;
            DrawFitted(c, p.Artists ?? "", x, y + 2, 12, Muted, maxW, SKTextAlign.Left);
            if (p.AlbumName is not null && used < 2) DrawFitted(c, p.AlbumName, x, y + 20, 11, Dim, maxW, SKTextAlign.Left);
            var foot = string.Join("  ·  ", new[] { p.DurationMs > 0 ? Clock(p.DurationMs) : null, p.DeviceName }.Where(t => !string.IsNullOrEmpty(t)));
            DrawFitted(c, foot, x, Size - (progress == Progress.Off ? 14 : 22), 10, Muted, maxW, SKTextAlign.Left);
            if (progress != Progress.Off) ProgressBar(c, new SKRect(x, Size - 12, W - 10, Size - 7), p.ProgressFraction(Steps(progress)));
            Circle(c, new SKPoint(W - 24, 22), 14, p.IsPlaying ? Green : Card);
            if (p.IsPlaying) Pause(c, new SKPoint(W - 24, 22), 6, SKColors.Black); else Triangle(c, new SKPoint(W - 23, 22), 8, Text);
        }
        return Encode(wide, photo: art is not null);
    }

    /// <summary>Like / unlike the current track: a heart, filled when the track is in Liked Songs, over the dimmed cover.</summary>
    public string LikeKey(PlaybackState? p, byte[]? art, bool canModify)
    {
        using var s = NewSurface(); var c = s.Canvas;
        var haveTrack = p is not null && (p.TrackUri is not null || p.TrackName is not null);
        if (haveTrack && DrawArt(c, art, new SKRect(0, 0, Size, Size))) Fill(c, new SKRect(0, 0, Size, Size), SKColors.Black.WithAlpha(150));
        var liked = p?.Liked == true;
        var heart = liked ? "♥" : "♡";
        var color = !haveTrack ? Dim : liked ? Green : Text;
        DrawText(c, heart, Size / 2f, 88, 74, color, bold: true, align: SKTextAlign.Center);
        string label;
        if (!haveTrack) label = "nothing playing";
        else if (!canModify) label = "connect again to allow saving";
        else label = liked ? "Liked · press to remove" : "press to like";
        DrawFitted(c, label, Size / 2f, 124, 10, haveTrack ? Muted : Dim, Size - 12, SKTextAlign.Center);
        if (haveTrack) DrawFitted(c, p!.TrackName ?? "", Size / 2f, 18, 10, Muted, Size - 12, SKTextAlign.Center);
        return Encode(s, photo: haveTrack && art is not null);
    }

    /// <summary>Previous / next track glyphs (for when the transport actions sit on keys with a display).</summary>
    public string TransportKey(bool next)
    {
        using var s = NewSurface(); var c = s.Canvas;
        var cx = Size / 2f; var cy = Size / 2f - 6;
        using var paint = new SKPaint { Color = Text, IsAntialias = true };
        var d = next ? 1 : -1;   // mirror for "previous"
        var b = new SKPathBuilder();
        b.MoveTo(cx - 30 * d, cy - 22); b.LineTo(cx + 2 * d, cy); b.LineTo(cx - 30 * d, cy + 22); b.Close();
        b.MoveTo(cx + 2 * d, cy - 22); b.LineTo(cx + 34 * d, cy); b.LineTo(cx + 2 * d, cy + 22); b.Close();
        using var path = b.Detach();
        c.DrawPath(path, paint);
        c.DrawRect(next ? new SKRect(cx + 34, cy - 22, cx + 40, cy + 22) : new SKRect(cx - 40, cy - 22, cx - 34, cy + 22), paint);
        DrawText(c, next ? "next" : "previous", Size / 2f, Size - 22, 12, Muted, align: SKTextAlign.Center);
        return Encode(s);
    }

    // ---- drawing helpers ------------------------------------------------------------------

    private static SKSurface NewSurface()
    {
        var s = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        s.Canvas.Clear(Bg);
        return s;
    }

    private static string Encode(SKSurface s, bool photo = false)
    {
        using var img = s.Snapshot();
        // OpenDeck redraws every key on its own canvas and hands the device a JPEG, so a photo key may travel as
        // JPEG (a fifth of the bytes, faster to encode and decode); flat graphics stay PNG for crisp edges
        using var data = photo ? img.Encode(SKEncodedImageFormat.Jpeg, 92) : img.Encode(SKEncodedImageFormat.Png, 90);
        return (photo ? "data:image/jpeg;base64," : "data:image/png;base64,") + Convert.ToBase64String(data.AsSpan());
    }

    /// <summary>Center-crops the image into the rect. False when the bytes cannot be decoded.</summary>
    private static bool DrawArt(SKCanvas c, byte[]? art, SKRect dest)
    {
        if (art is null || art.Length == 0) return false;
        using var img = SKImage.FromEncodedData(art);
        if (img is null) return false;
        var side = Math.Min(img.Width, img.Height);
        var src = new SKRect((img.Width - side) / 2f, (img.Height - side) / 2f, (img.Width + side) / 2f, (img.Height + side) / 2f);
        c.DrawImage(img, src, dest, Sampling);
        return true;
    }

    /// <summary>A generated cover: two-tone background from the URI's hash and a glyph for the kind.</summary>
    private void DrawTile(SKCanvas c, PickItem item, SKRect r)
    {
        var hash = 0; foreach (var ch in item.Uri) hash = unchecked(hash * 31 + ch);
        var hue = Math.Abs(hash) % 360;
        var a = SKColor.FromHsl(hue, 48, 34); var b = SKColor.FromHsl((hue + 40) % 360, 55, 22);
        using (var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Bottom), new[] { a, b }, null, SKShaderTileMode.Clamp) })
            c.DrawRect(r, paint);
        var glyph = item.Kind switch { ItemKind.Collection => "♥", ItemKind.Playlist => "♫", _ => "♪" };
        DrawText(c, glyph, r.MidX, r.MidY - 6, 52, SKColors.White.WithAlpha(70), bold: true, align: SKTextAlign.Center);
    }

    private static void Gradient(SKCanvas c, SKRect r, SKColor from, SKColor to)
    {
        using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(r.Left, r.Top), new SKPoint(r.Left, r.Bottom), new[] { from, to }, null, SKShaderTileMode.Clamp) };
        c.DrawRect(r, paint);
    }

    private static void ProgressBar(SKCanvas c, SKRect r, float fraction)
    {
        FillRound(c, r, r.Height / 2, SKColors.White.WithAlpha(60));
        var w = Math.Max(r.Height, r.Width * Math.Clamp(fraction, 0, 1));
        FillRound(c, new SKRect(r.Left, r.Top, r.Left + w, r.Bottom), r.Height / 2, Green);
    }

    private static void Fill(SKCanvas c, SKRect r, SKColor color) { using var p = new SKPaint { Color = color }; c.DrawRect(r, p); }
    private static void FillRound(SKCanvas c, SKRect r, float radius, SKColor color) { using var p = new SKPaint { Color = color, IsAntialias = true }; c.DrawRoundRect(r, radius, radius, p); }
    private static void Circle(SKCanvas c, SKPoint center, float radius, SKColor color) { using var p = new SKPaint { Color = color, IsAntialias = true }; c.DrawCircle(center, radius, p); }

    private static void Triangle(SKCanvas c, SKPoint center, float size, SKColor color)
    {
        using var p = new SKPaint { Color = color, IsAntialias = true };
        var b = new SKPathBuilder();
        b.MoveTo(center.X - size * 0.6f, center.Y - size); b.LineTo(center.X + size * 0.9f, center.Y); b.LineTo(center.X - size * 0.6f, center.Y + size); b.Close();
        using var path = b.Detach();
        c.DrawPath(path, p);
    }

    private static void Pause(SKCanvas c, SKPoint center, float size, SKColor color)
    {
        using var p = new SKPaint { Color = color, IsAntialias = true };
        c.DrawRect(new SKRect(center.X - size * 0.8f, center.Y - size, center.X - size * 0.2f, center.Y + size), p);
        c.DrawRect(new SKRect(center.X + size * 0.2f, center.Y - size, center.X + size * 0.8f, center.Y + size), p);
    }

    private static void Border(SKCanvas c, SKColor color, float width)
    {
        using var p = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width };
        c.DrawRoundRect(new SKRect(width / 2, width / 2, Size - width / 2, Size - width / 2), 8, 8, p);
    }

    private void DrawText(SKCanvas c, string text, float x, float y, float size, SKColor color, bool bold = false, SKTextAlign align = SKTextAlign.Left)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var font = new SKFont(bold ? _bold : _regular, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        c.DrawText(text, x, y, align, font, paint);
    }

    /// <summary>One line, shrunk a little then ellipsised to fit.</summary>
    private void DrawFitted(SKCanvas c, string text, float x, float y, float size, SKColor color, float maxWidth, SKTextAlign align, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var font = new SKFont(bold ? _bold : _regular, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        var t = text;
        while (font.Size > size * 0.85f && font.MeasureText(t) > maxWidth) font.Size -= 1;
        while (t.Length > 1 && font.MeasureText(t + "…") > maxWidth && font.MeasureText(t) > maxWidth) t = t[..^1];
        if (t.Length < text.Length) t = t.TrimEnd() + "…";
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        c.DrawText(t, x, y, align, font, paint);
    }

    /// <summary>Word-wrapped text; returns the number of lines drawn. The last allowed line gets an ellipsis.</summary>
    private int DrawWrapped(SKCanvas c, string text, float x, float baseline, float size, SKColor color, float maxWidth, int maxLines, bool bold = false, bool center = false)
    {
        var lines = Wrap(text, size, maxWidth, maxLines, bold);
        if (lines.Count == 0) return 0;
        using var font = new SKFont(bold ? _bold : _regular, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        var lineHeight = size * 1.27f;
        for (var i = 0; i < lines.Count; i++)
            c.DrawText(lines[i], center ? x + maxWidth / 2 : x, baseline + i * lineHeight, center ? SKTextAlign.Center : SKTextAlign.Left, font, paint);
        return lines.Count;
    }

    /// <summary>Word-wraps text into at most maxLines; the last line gets an ellipsis when text remains.</summary>
    private List<string> Wrap(string text, float size, float maxWidth, int maxLines, bool bold)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();
        using var font = new SKFont(bold ? _bold : _regular, size);
        var words = text.Replace("\r", "").Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>(); var cur = "";
        foreach (var w in words)
        {
            if (lines.Count > maxLines) break;
            var candidate = cur.Length == 0 ? w : cur + " " + w;
            if (font.MeasureText(candidate) <= maxWidth) { cur = candidate; continue; }
            if (cur.Length > 0) { lines.Add(cur); cur = ""; }
            var piece = w;
            while (font.MeasureText(piece) > maxWidth && piece.Length > 1)
            {
                var cut = piece.Length;
                while (cut > 1 && font.MeasureText(piece[..cut]) > maxWidth) cut--;
                lines.Add(piece[..cut]); piece = piece[cut..];
            }
            cur = piece;
        }
        if (cur.Length > 0) lines.Add(cur);
        var truncated = lines.Count > maxLines;
        if (truncated) lines = lines.Take(maxLines).ToList();
        if (truncated && lines.Count > 0)
        {
            var last = lines[^1];
            while (last.Length > 1 && font.MeasureText(last + "…") > maxWidth) last = last[..^1];
            lines[^1] = last + "…";
        }
        return lines;
    }

    private static string Clock(int ms) { var t = TimeSpan.FromMilliseconds(Math.Max(0, ms)); return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}"; }
}
