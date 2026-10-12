using System.Runtime.InteropServices;
using SkiaSharp;

namespace CircuitRF.SchematicSynth;

/// <summary>
/// The medium's conditions on a rendered foreign picture (brief-iml3 R-iml3-3, R-iml3-4), on top of IML-2's own:
/// <list type="bullet">
/// <item><b>screen</b> — generic window chrome drawn as plain shapes and words (a title bar, a menu of ordinary words, a
/// toolbar, a side palette, scroll bars, a status line with cursor coordinates, now and then a task bar): no real
/// application's artwork, icons or name. Each band is an <c>annotation</c> of kind <c>chrome</c>;</item>
/// <item><b>paper</b> — a printed page scanned: grain, blotches and uneven light, a yellowed or grey sheet; the slight
/// turn is the view's, so the labels carry it exactly;</item>
/// <item>both — a rescale by a random filter, blur, JPEG (heavy now and then) or PNG, posterised sometimes; aliased
/// pens are the look's.</item>
/// </list>
/// Geometry (chrome, rescale) is applied to the masks too; pixel conditions are the picture's alone.
/// </summary>
public static class ForeignShot
{
    private static readonly string[] Menu = ["File", "Edit", "View", "Place", "Insert", "Inspect", "Tools", "Options", "Simulate", "Window", "Help", "Layout", "Hierarchy"];

    public static Shot Take(SKBitmap canvas, IReadOnlyList<byte[]> masks, Look look, View view, Random r)
    {
        int pw = canvas.Width, ph = canvas.Height;
        double dpr = view.Dpr;
        bool paper = look.Pack.Medium == "paper", screen = look.Pack.Medium == "screen";
        bool light = look.Paper.Red + look.Paper.Green + look.Paper.Blue > 3 * 110;
        var conditions = new List<string>();

        // ── chrome ───────────────────────────────────────────────────────────
        int title = 0, menu = 0, tools = 0, status = 0, task = 0, palette = 0, vscroll = 0, hscroll = 0;
        if (screen && r.NextDouble() < look.Pack.Chance("chrome"))
        {
            if (r.NextDouble() < 0.6) title = (int)(r.Next(20, 30) * dpr);
            if (r.NextDouble() < 0.8) menu = (int)(r.Next(18, 24) * dpr);
            if (r.NextDouble() < 0.8) tools = (int)(r.Next(24, 40) * dpr);
            if (r.NextDouble() < 0.7) status = (int)(r.Next(16, 24) * dpr);
            if (r.NextDouble() < 0.15) task = (int)(r.Next(26, 36) * dpr);
            if (r.NextDouble() < 0.5) palette = (int)(r.Next(24, 110) * dpr);
            if (r.NextDouble() < 0.6) vscroll = (int)(r.Next(9, 17) * dpr);
            if (r.NextDouble() < 0.4) hscroll = (int)(r.Next(9, 17) * dpr);
            conditions.Add("chrome");
        }
        int top = title + menu + tools, left = palette, right = vscroll, bottom = hscroll + status + task;
        int tw = left + pw + right, th = top + ph + bottom;

        var composed = new SKBitmap(new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Premul));
        var bands = new List<SKRectI>();
        using (var c = new SKCanvas(composed))
        {
            int shade = light ? r.Next(222, 248) : r.Next(36, 66);
            c.Clear(new SKColor((byte)shade, (byte)shade, (byte)Math.Min(255, shade + r.Next(0, 6))));
            c.DrawBitmap(canvas, left, top);
            using var font = new SKFont(look.UiFace, (float)(11 * dpr)) { Edging = SKFontEdging.Antialias };
            var ink = light ? new SKColor(30, 30, 34) : new SKColor(220, 220, 224);
            int y = 0;
            if (title > 0) { var b = new SKRectI(0, y, tw, y + title); bands.Add(b); TitleBar(c, b, light, font, ink, dpr, r); y += title; }
            if (menu > 0) { var b = new SKRectI(0, y, tw, y + menu); bands.Add(b); MenuBar(c, b, light, font, ink, dpr, r); y += menu; }
            if (tools > 0) { var b = new SKRectI(0, y, tw, y + tools); bands.Add(b); Screenshot.Toolbar(c, b, light, dpr, r); }
            if (palette > 0) { var b = new SKRectI(0, top, left, top + ph + hscroll); bands.Add(b); Palette(c, b, light, dpr, r); }
            if (vscroll > 0) { var b = new SKRectI(left + pw, top, tw, top + ph); bands.Add(b); Screenshot.ScrollBar(c, b, light, vertical: true, r); }
            if (hscroll > 0) { var b = new SKRectI(left, top + ph, left + pw, top + ph + hscroll); bands.Add(b); Screenshot.ScrollBar(c, b, light, vertical: false, r); }
            int sy = top + ph + hscroll;
            if (status > 0) { var b = new SKRectI(0, sy, tw, sy + status); bands.Add(b); StatusLine(c, b, light, font, ink, dpr, r); sy += status; }
            if (task > 0) { var b = new SKRectI(0, sy, tw, sy + task); bands.Add(b); TaskBar(c, b, dpr, r); }
        }

        var full = masks.Select(m => Screenshot.Pad(m, pw, ph, tw, th, left, top)).ToList();
        foreach (var b in bands)
            for (int yy = b.Top; yy < b.Bottom; yy++)
                Array.Fill(full[^1], (byte)255, yy * tw + b.Left, b.Width);

        // ── paper ────────────────────────────────────────────────────────────
        if (paper) Print(composed, r, conditions);

        // ── rescale ──────────────────────────────────────────────────────────
        SKBitmap final = composed;
        int fw = tw, fh = th;
        double shrink = view.Mode.StartsWith("large", StringComparison.Ordinal) || r.NextDouble() < 0.55 ? 1 : 0.5 + r.NextDouble() * 0.45;
        if (shrink < 0.999 && tw * shrink >= 100 && th * shrink >= 70)
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
            using (var img = SKImage.FromBitmap(composed))
            using (var c = new SKCanvas(final))
                c.DrawImage(img, new SKRect(0, 0, tw, th), new SKRect(0, 0, fw, fh), sampling, null);
            full = [.. full.Select(m => Screenshot.Shrink(m, tw, th, fw, fh))];
            composed.Dispose();
        }

        // ── pixel conditions ─────────────────────────────────────────────────
        if (r.NextDouble() < (paper ? 0.3 : 0.12))
        {
            float sigma = (float)(0.3 + r.NextDouble() * (paper ? 0.9 : 0.6));
            conditions.Add($"blur{ValueText.F(sigma)}");
            var blurred = new SKBitmap(final.Info);
            using (var c = new SKCanvas(blurred))
            using (var p = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma) })
            {
                c.Clear(look.Paper);
                c.DrawBitmap(final, 0, 0, p);
            }
            final.Dispose();
            final = blurred;
        }

        byte[] image;
        string ext;
        if (r.NextDouble() < (paper ? 0.6 : 0.45))
        {
            int q = r.NextDouble() < 0.12 ? r.Next(15, 40) : r.Next(40, 96);
            conditions.Add($"jpeg{q}");
            image = Screenshot.Encode(final, SKEncodedImageFormat.Jpeg, q);
            ext = ".jpg";
        }
        else
        {
            if (r.NextDouble() < 0.1)
            {
                int levels = new[] { 6, 8, 16, 32 }[r.Next(4)];
                conditions.Add($"palette{levels}");
                Screenshot.Posterize(final, levels);
            }
            image = Screenshot.Encode(final, SKEncodedImageFormat.Png, 100);
            ext = ".png";
        }
        final.Dispose();

        double sx = fw / (double)tw, sy2 = fh / (double)th;
        var chrome = bands.Select(b => new[] { b.Left * sx, b.Top * sy2, b.Right * sx, b.Bottom * sy2 }).ToList();
        return new Shot(image, ext, fw, fh, left, top, sx, sy2, [.. full.Select(m => Screenshot.GreyPng(m, fw, fh))], chrome,
                        string.Join(",", conditions.DefaultIfEmpty("clean"))) { RawMasks = full };
    }

    // ── a scanned page ───────────────────────────────────────────────────────

    /// <summary>Grain, a few soft blotches, light falling off across the sheet and a tint: the page, not the ink.</summary>
    private static void Print(SKBitmap bmp, Random r, List<string> conditions)
    {
        int w = bmp.Width, h = bmp.Height;
        var px = new byte[w * h * 4];
        Marshal.Copy(bmp.GetPixels(), px, 0, px.Length);
        double grain = r.NextDouble() < 0.7 ? 2 + r.NextDouble() * 7 : 0;
        double gx = (r.NextDouble() - 0.5) * 0.25, gy = (r.NextDouble() - 0.5) * 0.25, floor = 0.82 + r.NextDouble() * 0.18;
        bool uneven = r.NextDouble() < 0.6;
        var tint = r.NextDouble() < 0.4 ? (1.0, 0.985 - r.NextDouble() * 0.02, 0.95 - r.NextDouble() * 0.05) : (1.0, 1.0, 1.0);
        var blotches = Enumerable.Range(0, r.NextDouble() < 0.4 ? r.Next(1, 5) : 0)
            .Select(_ => (X: r.NextDouble() * w, Y: r.NextDouble() * h, R: (0.04 + r.NextDouble() * 0.12) * Math.Max(w, h), D: 0.015 + r.NextDouble() * 0.035)).ToList();
        var noise = new Random(r.Next());
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double f = 1;
                if (uneven) f *= Math.Clamp(1 + gx * (x / (double)w - 0.5) * 2 + gy * (y / (double)h - 0.5) * 2, floor, 1.05);
                foreach (var b in blotches)
                {
                    double d2 = ((x - b.X) * (x - b.X) + (y - b.Y) * (y - b.Y)) / (b.R * b.R);
                    if (d2 < 1) f *= 1 - b.D * (1 - d2);
                }
                double n = grain > 0 ? (noise.NextDouble() - 0.5) * 2 * grain : 0;
                int i = (y * w + x) * 4;
                px[i] = (byte)Math.Clamp(px[i] * f * tint.Item1 + n, 0, 255);
                px[i + 1] = (byte)Math.Clamp(px[i + 1] * f * tint.Item2 + n, 0, 255);
                px[i + 2] = (byte)Math.Clamp(px[i + 2] * f * tint.Item3 + n, 0, 255);
            }
        Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        conditions.Add("print" + (grain > 0 ? "-grain" : "") + (uneven ? "-light" : "") + (blotches.Count > 0 ? "-blotch" : ""));
    }

    // ── generic chrome, words included ───────────────────────────────────────

    private static void Words(SKCanvas c, SKRectI band, IEnumerable<string> words, SKFont font, SKColor ink, float x, float gap)
    {
        using var paint = new SKPaint { Color = ink, IsAntialias = true };
        float y = band.MidY + font.Size * 0.35f;
        foreach (string w in words)
        {
            float len = font.MeasureText(w);
            if (x + len > band.Right - 4) break;
            c.DrawText(w, x, y, SKTextAlign.Left, font, paint);
            x += len + gap;
        }
    }

    private static void TitleBar(SKCanvas c, SKRectI b, bool light, SKFont font, SKColor ink, double dpr, Random r)
    {
        using var fill = new SKPaint { Color = light ? new SKColor(250, 250, 252) : new SKColor(40, 42, 46) };
        c.DrawRect(b, fill);
        string[] names = ["untitled", "schematic", "amp_stage", "filter_v2", "test_bench", "cell_2", "lna_match"];
        string title = $"{names[r.Next(names.Length)]} — Schematic{(r.NextDouble() < 0.5 ? " Editor" : "")}";
        Words(c, b, [title], font, ink, (float)(r.NextDouble() < 0.5 ? 8 * dpr : b.Width / 2 - 80 * dpr), 0);
        // Three window buttons at one end, as plain shapes.
        using var btn = new SKPaint { Color = ink.WithAlpha(150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)dpr };
        float s = b.Height * 0.3f, x = b.Right - (float)(14 * dpr);
        for (int k = 0; k < 3; k++, x -= (float)(26 * dpr)) c.DrawRect(x - s, b.MidY - s / 2, s, s, btn);
    }

    private static void MenuBar(SKCanvas c, SKRectI b, bool light, SKFont font, SKColor ink, double dpr, Random r)
    {
        using var fill = new SKPaint { Color = light ? new SKColor(244, 244, 246) : new SKColor(48, 50, 54) };
        c.DrawRect(b, fill);
        var words = Menu.Take(4).Concat(Menu.Skip(4).Where(_ => r.NextDouble() < 0.5)).Append("Help").Distinct();
        Words(c, b, words, font, ink, (float)(8 * dpr), (float)(14 * dpr));
    }

    private static void StatusLine(SKCanvas c, SKRectI b, bool light, SKFont font, SKColor ink, double dpr, Random r)
    {
        using var fill = new SKPaint { Color = light ? new SKColor(232, 232, 236) : new SKColor(44, 46, 50) };
        c.DrawRect(b, fill);
        string F(double v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var words = new List<string> { $"Z {F(r.NextDouble() * 4)}", $"X {F(r.Next(0, 9000))}  Y {F(r.Next(0, 9000))}", $"dx {F(r.Next(0, 900))}  dy {F(r.Next(0, 900))}" };
        if (r.NextDouble() < 0.5) words.Add($"grid {new[] { 50, 25, 100, 10 }[r.Next(4)]}");
        if (r.NextDouble() < 0.5) words.Insert(0, new[] { "Select: Click and drag to select.", "Ready", "Add wire", "0 items" }[r.Next(4)]);
        Words(c, b, words, font, ink, (float)(8 * dpr), (float)(40 * dpr));
    }

    private static void Palette(SKCanvas c, SKRectI b, bool light, double dpr, Random r)
    {
        if (r.NextDouble() < 0.5) { Screenshot.SidePanel(c, b, light, dpr, r); return; }
        using var fill = new SKPaint { Color = light ? new SKColor(236, 236, 240) : new SKColor(46, 48, 52) };
        c.DrawRect(b, fill);
        // A grid of small tool squares, each a box with a stroke or two inside: no recognisable artwork.
        float s = (float)(18 * dpr), pad = (float)(4 * dpr);
        int cols = Math.Max(1, (int)((b.Width - pad) / (s + pad)));
        using var box = new SKPaint { Color = light ? new SKColor(200, 200, 206) : new SKColor(80, 82, 88), Style = SKPaintStyle.Stroke, StrokeWidth = (float)dpr, IsAntialias = true };
        using var mark = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = (float)dpr, IsAntialias = true };
        for (float y = b.Top + pad; y + s < b.Bottom - pad; y += s + pad)
            for (int k = 0; k < cols; k++)
            {
                float x = b.Left + pad + k * (s + pad);
                c.DrawRect(x, y, s, s, box);
                mark.Color = Screenshot.Muted(r, light, 60);
                if (r.Next(2) == 0) c.DrawLine(x + 4, y + s / 2, x + s - 4, y + s / 2, mark);
                else c.DrawCircle(x + s / 2, y + s / 2, s / 4, mark);
            }
    }

    private static void TaskBar(SKCanvas c, SKRectI b, double dpr, Random r)
    {
        using var fill = new SKPaint { Color = new SKColor(24, 26, 30) };
        c.DrawRect(b, fill);
        float s = b.Height * 0.55f, x = (float)(10 * dpr);
        while (x + s < b.Right * 0.6f)
        {
            using var icon = new SKPaint { Color = Screenshot.Muted(r, false, 70), IsAntialias = true };
            c.DrawRoundRect(x, b.MidY - s / 2, s, s, 3, 3, icon);
            x += s + (float)(r.Next(6, 14) * dpr);
        }
    }
}
