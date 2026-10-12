using CircuitRF.Render;
using SkiaSharp;

namespace CircuitRF.SchematicSynth;

/// <summary>What the screenshot conditions made of a rendered picture, and how its pixels map to the canvas's.</summary>
/// <param name="OffsetX">Where the canvas sits in the picture before any rescale, device pixels.</param>
/// <param name="ScaleX">Final pixels per device pixel.</param>
/// <param name="Masks">The masks, in the order given, at the final size, as 8-bit grey PNGs.</param>
/// <param name="Chrome">The window-chrome bands, final pixels: [x0, y0, x1, y1].</param>
public sealed record Shot(
    byte[] Image, string Extension, int Width, int Height, double OffsetX, double OffsetY, double ScaleX, double ScaleY,
    IReadOnlyList<byte[]> Masks, IReadOnlyList<double[]> Chrome, string Conditions)
{
    /// <summary>The same masks, one byte a pixel (0 or 255), at the final size.</summary>
    public IReadOnlyList<byte[]> RawMasks { get; init; } = [];
}

/// <summary>
/// Screenshot conditions (brief-iml-2 R-iml2-3): window-chrome strips — a toolbar band, a status bar, scroll bars, a
/// side panel, drawn generically as flat bands of rectangles with no real application's artwork and no text — Retina
/// 2× or 1× (decided before rendering), a downscale by a random factor with a random filter, a slight blur, JPEG at
/// quality 40–95 or PNG with a reduced palette. Geometry changes (chrome, rescale) are applied to the masks too, so a
/// mask stays the picture's; pixel changes (blur, compression, palette) are the picture's alone.
/// </summary>
public static class Screenshot
{
    public static Shot Take(SKBitmap canvas, IReadOnlyList<byte[]> masks, double dpr, double shrink, ColorVariant variant, Random r)
    {
        int pw = canvas.Width, ph = canvas.Height;
        var conditions = new List<string> { dpr > 1 ? "2x" : "1x" };

        // ── chrome ───────────────────────────────────────────────────────────
        int top = 0, bottom = 0, right = 0, left = 0, hscroll = 0;
        if (r.NextDouble() < 0.5)
        {
            if (r.NextDouble() < 0.75) top = (int)(r.Next(24, 46) * dpr);
            if (r.NextDouble() < 0.6) bottom = (int)(r.Next(16, 27) * dpr);
            if (r.NextDouble() < 0.5) right = (int)(r.Next(9, 17) * dpr);
            if (r.NextDouble() < 0.25) hscroll = (int)(r.Next(9, 17) * dpr);
            if (r.NextDouble() < 0.15) left = (int)(r.Next(40, 121) * dpr);
            if (top + bottom + right + left + hscroll > 0) conditions.Add("chrome");
        }
        int tw = left + pw + right, th = top + ph + hscroll + bottom;
        bool light = variant == ColorVariant.Light;

        var composed = new SKBitmap(new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Premul));
        var bands = new List<SKRectI>();
        using (var c = new SKCanvas(composed))
        {
            int shade = light ? r.Next(222, 248) : r.Next(36, 66);
            var bg = new SKColor((byte)shade, (byte)shade, (byte)Math.Min(255, shade + r.Next(0, 6)));
            c.Clear(bg);
            c.DrawBitmap(canvas, left, top);
            if (top > 0) { var b = new SKRectI(0, 0, tw, top); bands.Add(b); Toolbar(c, b, light, dpr, r); }
            if (bottom > 0) { var b = new SKRectI(0, th - bottom, tw, th); bands.Add(b); StatusBar(c, b, light, dpr, r); }
            if (right > 0) { var b = new SKRectI(left + pw, top, tw, top + ph); bands.Add(b); ScrollBar(c, b, light, vertical: true, r); }
            if (hscroll > 0) { var b = new SKRectI(left, top + ph, left + pw, top + ph + hscroll); bands.Add(b); ScrollBar(c, b, light, vertical: false, r); }
            if (left > 0) { var b = new SKRectI(0, top, left, top + ph + hscroll); bands.Add(b); SidePanel(c, b, light, dpr, r); }
        }

        var full = masks.Select(m => Pad(m, pw, ph, tw, th, left, top)).ToList();
        foreach (var b in bands)
            for (int y = b.Top; y < b.Bottom; y++)
                Array.Fill(full[^1], (byte)255, y * tw + b.Left, b.Width);

        // ── rescale ──────────────────────────────────────────────────────────
        SKBitmap final = composed;
        int fw = tw, fh = th;
        if (shrink < 0.999)
        {
            fw = Math.Max(1, (int)Math.Round(tw * shrink));
            fh = Math.Max(1, (int)Math.Round(th * shrink));
            var (sampling, name) = r.Next(4) switch
            {
                0 => (new SKSamplingOptions(SKFilterMode.Nearest), "nearest"),
                1 => (new SKSamplingOptions(SKFilterMode.Linear), "linear"),
                2 => (new SKSamplingOptions(SKCubicResampler.Mitchell), "cubic"),
                _ => (new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), "area"),
            };
            conditions.Add($"down{ValueText.F(shrink)}-{name}");
            final = new SKBitmap(new SKImageInfo(fw, fh, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var img = SKImage.FromBitmap(composed);
            using var c = new SKCanvas(final);
            c.DrawImage(img, new SKRect(0, 0, tw, th), new SKRect(0, 0, fw, fh), sampling, null);
            full = [.. full.Select(m => Shrink(m, tw, th, fw, fh))];
            composed.Dispose();
        }

        // ── pixel conditions ─────────────────────────────────────────────────
        if (r.NextDouble() < 0.15)
        {
            float sigma = (float)(0.3 + r.NextDouble() * 0.6);
            conditions.Add($"blur{ValueText.F(sigma)}");
            var blurred = new SKBitmap(final.Info);
            using (var c = new SKCanvas(blurred))
            using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma) })
            {
                c.Clear(SKColors.White);
                c.DrawBitmap(final, 0, 0, paint);
            }
            final.Dispose();
            final = blurred;
        }

        byte[] image;
        string ext;
        if (r.NextDouble() < 0.45)
        {
            int q = r.Next(40, 96);
            conditions.Add($"jpeg{q}");
            image = Encode(final, SKEncodedImageFormat.Jpeg, q);
            ext = ".jpg";
        }
        else
        {
            if (r.NextDouble() < 0.12)
            {
                int levels = new[] { 6, 8, 16, 32 }[r.Next(4)];
                conditions.Add($"palette{levels}");
                Posterize(final, levels);
            }
            image = Encode(final, SKEncodedImageFormat.Png, 100);
            ext = ".png";
        }
        final.Dispose();

        double sx = fw / (double)tw, sy = fh / (double)th;
        var chrome = bands.Select(b => new[] { b.Left * sx, b.Top * sy, b.Right * sx, b.Bottom * sy }).ToList();
        return new Shot(image, ext, fw, fh, left, top, sx, sy, [.. full.Select(m => GreyPng(m, fw, fh))], chrome,
                        string.Join(",", conditions)) { RawMasks = full };
    }

    // ── chrome, drawn generically ────────────────────────────────────────────

    private static SKColor Muted(Random r, bool light, int spread = 40)
    {
        int b = light ? r.Next(150, 215) : r.Next(70, 130);
        return new SKColor((byte)Math.Clamp(b + r.Next(-spread, spread), 0, 255), (byte)Math.Clamp(b + r.Next(-spread, spread), 0, 255),
                           (byte)Math.Clamp(b + r.Next(-spread, spread), 0, 255));
    }

    private static void Toolbar(SKCanvas c, SKRectI band, bool light, double dpr, Random r)
    {
        using var fill = new SKPaint { Color = light ? new SKColor(240, 240, 242) : new SKColor(50, 52, 56), IsAntialias = true };
        c.DrawRect(band, fill);
        using var line = new SKPaint { Color = light ? new SKColor(200, 200, 204) : new SKColor(30, 30, 32), StrokeWidth = (float)dpr };
        c.DrawLine(band.Left, band.Bottom - 0.5f, band.Right, band.Bottom - 0.5f, line);
        float size = band.Height * 0.55f, x = (float)(8 * dpr), y = band.MidY - size / 2;
        while (x + size < band.Right - 8 * dpr)
        {
            using var icon = new SKPaint { Color = Muted(r, light), IsAntialias = true, Style = r.Next(3) == 0 ? SKPaintStyle.Stroke : SKPaintStyle.Fill, StrokeWidth = (float)dpr };
            if (r.Next(2) == 0) c.DrawRoundRect(x, y, size, size, size * 0.2f, size * 0.2f, icon);
            else c.DrawCircle(x + size / 2, y + size / 2, size / 2, icon);
            x += size + (float)(r.Next(4, 12) * dpr);
            if (r.NextDouble() < 0.12)
            {
                c.DrawLine(x, band.Top + 4, x, band.Bottom - 4, line);
                x += (float)(8 * dpr);
            }
        }
    }

    private static void StatusBar(SKCanvas c, SKRectI band, bool light, double dpr, Random r)
    {
        using var fill = new SKPaint { Color = light ? new SKColor(232, 232, 236) : new SKColor(44, 46, 50) };
        c.DrawRect(band, fill);
        float x = (float)(10 * dpr);
        while (x < band.Right - 40 * dpr && r.NextDouble() < 0.85)
        {
            float len = (float)(r.Next(30, 140) * dpr);
            using var bar = new SKPaint { Color = Muted(r, light, 10), IsAntialias = true };
            c.DrawRoundRect(x, band.MidY - band.Height * 0.18f, len, band.Height * 0.36f, 2, 2, bar);
            x += len + (float)(r.Next(20, 60) * dpr);
        }
    }

    private static void ScrollBar(SKCanvas c, SKRectI band, bool light, bool vertical, Random r)
    {
        using var track = new SKPaint { Color = light ? new SKColor(246, 246, 246) : new SKColor(40, 40, 42) };
        c.DrawRect(band, track);
        using var thumb = new SKPaint { Color = light ? new SKColor(188, 188, 192) : new SKColor(96, 98, 104), IsAntialias = true };
        float along = vertical ? band.Height : band.Width, across = vertical ? band.Width : band.Height;
        float len = along * (float)(0.15 + r.NextDouble() * 0.6), at = (float)r.NextDouble() * (along - len);
        float pad = across * 0.2f;
        var rect = vertical
            ? new SKRect(band.Left + pad, band.Top + at, band.Right - pad, band.Top + at + len)
            : new SKRect(band.Left + at, band.Top + pad, band.Left + at + len, band.Bottom - pad);
        c.DrawRoundRect(rect, across * 0.3f, across * 0.3f, thumb);
    }

    private static void SidePanel(SKCanvas c, SKRectI band, bool light, double dpr, Random r)
    {
        using var fill = new SKPaint { Color = light ? new SKColor(236, 236, 240) : new SKColor(46, 48, 52) };
        c.DrawRect(band, fill);
        float y = band.Top + (float)(10 * dpr);
        while (y < band.Bottom - 20 * dpr)
        {
            using var row = new SKPaint { Color = Muted(r, light, 12), IsAntialias = true };
            float len = (float)(band.Width * (0.3 + r.NextDouble() * 0.55));
            c.DrawRoundRect(band.Left + (float)(8 * dpr), y, len, (float)(6 * dpr), 2, 2, row);
            y += (float)(r.Next(14, 26) * dpr);
        }
    }

    // ── mask geometry ────────────────────────────────────────────────────────

    private static byte[] Pad(byte[] m, int pw, int ph, int tw, int th, int left, int top)
    {
        if (tw == pw && th == ph) return (byte[])m.Clone();
        var o = new byte[tw * th];
        for (int y = 0; y < ph; y++) Array.Copy(m, y * pw, o, (y + top) * tw + left, pw);
        return o;
    }

    /// <summary>A mask downscaled by coverage: a final pixel is on when a quarter of its footprint was.</summary>
    private static byte[] Shrink(byte[] m, int w, int h, int fw, int fh)
    {
        var o = new byte[fw * fh];
        double sx = w / (double)fw, sy = h / (double)fh;
        const int n = 4;
        for (int y = 0; y < fh; y++)
            for (int x = 0; x < fw; x++)
            {
                int on = 0;
                for (int j = 0; j < n; j++)
                {
                    int yy = Math.Min(h - 1, (int)((y + (j + 0.5) / n) * sy));
                    for (int i = 0; i < n; i++)
                    {
                        int xx = Math.Min(w - 1, (int)((x + (i + 0.5) / n) * sx));
                        if (m[yy * w + xx] != 0) on++;
                    }
                }
                o[y * fw + x] = on * 4 >= n * n ? (byte)255 : (byte)0;
            }
        return o;
    }

    // ── encoding ─────────────────────────────────────────────────────────────

    private static void Posterize(SKBitmap bmp, int levels)
    {
        var span = bmp.GetPixelSpan();
        var px = new byte[span.Length];
        span.CopyTo(px);
        double step = 255.0 / (levels - 1);
        for (int i = 0; i < px.Length; i += 4)
            for (int k = 0; k < 3; k++)
                px[i + k] = (byte)Math.Round(Math.Round(px[i + k] / step) * step);
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
    }

    private static byte[] Encode(SKBitmap bmp, SKEncodedImageFormat format, int quality)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(format, quality);
        return data.ToArray();
    }

    internal static byte[] GreyPng(byte[] mask, int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(mask, 0, bmp.GetPixels(), mask.Length);
        return Encode(bmp, SKEncodedImageFormat.Png, 100);
    }
}
