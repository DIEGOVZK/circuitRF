// The generator of brief-img-14-sparameter-plots-to-touchstone.md §4's committed plot pictures: plots drawn in
// conventions circuitRF's own plotter does not use, and the damage real pictures suffer. Run once
// (CRF_IM12_FIXTURES=1 dotnet test --filter ForeignPlotsGenerator) and the PNGs beside this file are rewritten; the gates
// read the committed bytes, never a fresh drawing, because a typeface rasterises a little differently from one platform
// to the next (overview D17). Every face is one circuitRF ships (src/Render/Assets/Fonts).
//
// EVERY PICTURE HAS ITS ANSWER IN THIS FILE: the curves are drawn from the series-LC band-pass below (R = 2 Ω, L = 10 nH,
// C = 0.4 pF between 50 Ω ports), so a test compares what was read against Answer.S21/S11 at the same frequency.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Imaging;
using CircuitRF.Ui.Tests.ImageAcceptance;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.PlotRecognition;

internal static class ForeignPlots
{
    public const string Folder = "tests/Ui.Tests/PlotRecognition/Fixtures";

    /// <summary>The band-pass every picture draws.</summary>
    public static class Answer
    {
        public const double R = 2, L = 10e-9, C = 0.4e-12;

        private static Complex Z(double f)
        {
            double w = 2 * Math.PI * f;
            return new Complex(R, w * L - 1 / (w * C));
        }

        public static Complex S11(double f) => Z(f) / (Z(f) + 100);

        public static Complex S21(double f) => 100 / (Z(f) + 100);

        public static double Db(Complex s) => 20 * Math.Log10(s.Magnitude);

        /// <summary>A series inductor alone (R = 2 Ω, L = 10 nH): a gentle low-pass whose S21 crosses the band-pass's
        /// once, at a good angle — the two-black-traces picture's dashed curve.</summary>
        public static Complex S21LowPass(double f) => 100 / (new Complex(R, 2 * Math.PI * f * L) + 100);
    }

    // ── drawing ──────────────────────────────────────────────────────────────────────────────────────────────

    private static SKTypeface Face(string fontsDir, string file) =>
        SKTypeface.FromFile(Path.Combine(fontsDir, file)) ?? throw new FileNotFoundException(file);

    private static RasterImage Draw(int w, int h, SKColor ground, Action<SKCanvas> draw)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(ground);
            draw(c);
        }
        return RasterImage.FromBitmap(bmp);
    }

    private static SKPaint Pen(SKColor colour, float width, float[]? dash = null) => new()
    {
        Color = colour, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width,
        PathEffect = dash is null ? null : SKPathEffect.CreateDash(dash, 0),
    };

    private enum Align { Left, Centre, Right }

    private static void Text(SKCanvas c, SKTypeface face, string text, float x, float baseline, float size, SKColor colour,
                             Align align = Align.Centre)
    {
        using var font = new SKFont(face, size);
        using var paint = new SKPaint { Color = colour, IsAntialias = true };
        float width = font.MeasureText(text);
        float left = align switch { Align.Centre => x - width / 2, Align.Right => x - width, _ => x };
        c.DrawText(text, left, baseline, SKTextAlign.Left, font, paint);
    }

    /// <summary>A rectangular plot's box, grid, ticks and labels; <paramref name="px"/> maps (x, y) values to pixels.</summary>
    private sealed record Frame(float Left, float Top, float Right, float Bottom, double X0, double X1, double Y0, double Y1, bool LogX)
    {
        public float Px(double x) => LogX
            ? Left + (float)((Math.Log10(x) - Math.Log10(X0)) / (Math.Log10(X1) - Math.Log10(X0)) * (Right - Left))
            : Left + (float)((x - X0) / (X1 - X0) * (Right - Left));

        public float Py(double y) => Bottom - (float)((y - Y0) / (Y1 - Y0) * (Bottom - Top));
    }

    /// <summary>A 1 px line's position on a pixel's centre, as plotting software draws its grid: on a pixel boundary it
    /// would split into two rows of half its ink.</summary>
    private static float Crisp(float v) => MathF.Floor(v) + 0.5f;

    private static void Axes(SKCanvas c, Frame f, SKTypeface face, IEnumerable<(double V, string Label)> xs, IEnumerable<(double V, string Label)> ys,
                             SKColor ink, SKColor grid, string? xTitle, string? title, IEnumerable<double>? minorX = null)
    {
        using var gridPen = Pen(grid, 1);
        using var framePen = Pen(ink, 1.5f);
        foreach (double v in minorX ?? []) c.DrawLine(Crisp(f.Px(v)), f.Top, Crisp(f.Px(v)), f.Bottom, gridPen);
        foreach (var (v, label) in xs)
        {
            float x = Crisp(f.Px(v));
            c.DrawLine(x, f.Top, x, f.Bottom, gridPen);
            c.DrawLine(x, f.Bottom, x, f.Bottom + 6, framePen);
            Text(c, face, label, x, f.Bottom + 26, 16, ink);
        }
        foreach (var (v, label) in ys)
        {
            float y = Crisp(f.Py(v));
            c.DrawLine(f.Left, y, f.Right, y, gridPen);
            c.DrawLine(f.Left - 6, y, f.Left, y, framePen);
            Text(c, face, label, f.Left - 12, y + 6, 16, ink, Align.Right);
        }
        c.DrawRect(SKRect.Create(f.Left, f.Top, f.Right - f.Left, f.Bottom - f.Top), framePen);
        if (xTitle is not null) Text(c, face, xTitle, (f.Left + f.Right) / 2, f.Bottom + 56, 16, ink);
        if (title is not null) Text(c, face, title, (f.Left + f.Right) / 2, f.Top - 16, 18, ink);
    }

    private static void Curve(SKCanvas c, Frame f, Func<double, double> y, SKPaint pen, int n = 600)
    {
        using var path = new SKPath();
        for (int i = 0; i <= n; i++)
        {
            double u = (double)i / n;
            double x = f.LogX ? f.X0 * Math.Pow(f.X1 / f.X0, u) : f.X0 + u * (f.X1 - f.X0);
            double v = Math.Clamp(y(x), f.Y0, f.Y1);
            if (i == 0) path.MoveTo(f.Px(x), f.Py(v));
            else path.LineTo(f.Px(x), f.Py(v));
        }
        c.DrawPath(path, pen);
    }

    private static IEnumerable<(double, string)> Range(double from, double to, double step, string format = "0") =>
        Enumerable.Range(0, (int)Math.Round((to - from) / step) + 1).Select(k => from + k * step)
                  .Select(v => (v, v.ToString(format, System.Globalization.CultureInfo.InvariantCulture)));

    private static Frame StandardFrame(bool logX = false) =>
        logX ? new Frame(110, 60, 750, 440, 1e8, 1e10, -40, 0, true) : new Frame(110, 60, 750, 440, 1e9, 5e9, -30, 0, false);

    // ── the pictures ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A dB(S21) plot on a dark background: light ink, a mid-grey grid, an amber trace.</summary>
    public static RasterImage DarkBackground(string fonts) => Draw(820, 520, new SKColor(0x1c, 0x1e, 0x22), c =>
    {
        using var face = Face(fonts, "DejaVuSans.ttf");
        var f = StandardFrame();
        var ink = new SKColor(0xe6, 0xe6, 0xe6);
        Axes(c, f, face, Range(1, 5, 1).Select(t => (t.Item1 * 1e9, t.Item2)), Range(-30, 0, 5), ink, new SKColor(0x4a, 0x4c, 0x52),
             "Frequency (GHz)", "S21 (dB)");
        using var trace = Pen(new SKColor(0xff, 0xb0, 0x20), 2.5f);
        Curve(c, f, x => Answer.Db(Answer.S21(x)), trace);
    });

    /// <summary>A dB(S21) plot against a logarithmic frequency axis labelled 1e8, 1e9, 1e10.</summary>
    public static RasterImage LogFrequency(string fonts) => Draw(820, 520, SKColors.White, c =>
    {
        using var face = Face(fonts, "DejaVuSans.ttf");
        var f = StandardFrame(logX: true);
        var minor = new List<double>();
        foreach (double d in new[] { 1e8, 1e9 })
            for (int k = 2; k <= 9; k++) minor.Add(k * d);
        Axes(c, f, face, [(1e8, "1e8"), (1e9, "1e9"), (1e10, "1e10")], Range(-40, 0, 10), SKColors.Black, new SKColor(0xd0, 0xd0, 0xd0),
             "Frequency (Hz)", "S21 (dB)", minor);
        using var trace = Pen(new SKColor(0x10, 0x60, 0xd0), 2.5f);
        Curve(c, f, x => Answer.Db(Answer.S21(x)), trace);
    });

    /// <summary>Two dB(S21) curves, both black — the band-pass solid, the low-pass dashed — crossing once.</summary>
    public static RasterImage TwoBlackTraces(string fonts) => Draw(820, 520, SKColors.White, c =>
    {
        using var face = Face(fonts, "DejaVuSans.ttf");
        var f = StandardFrame();
        Axes(c, f, face, Range(1, 5, 1).Select(t => (t.Item1 * 1e9, t.Item2)), Range(-30, 0, 5), SKColors.Black, new SKColor(0xd8, 0xd8, 0xd8),
             "Frequency (GHz)", "Response (dB)");
        using var solid = Pen(SKColors.Black, 2.5f);
        using var dashed = Pen(SKColors.Black, 2.5f, [10, 7]);
        Curve(c, f, x => Answer.Db(Answer.S21(x)), solid);
        Curve(c, f, x => Answer.Db(Answer.S21LowPass(x)), dashed);
    });

    /// <summary>dB(S11) red and dB(S21) blue, named by a legend box inside the plot.</summary>
    public static RasterImage LegendInside(string fonts) => Draw(820, 520, SKColors.White, c =>
    {
        using var face = Face(fonts, "DejaVuSans.ttf");
        var f = StandardFrame();
        Axes(c, f, face, Range(1, 5, 1).Select(t => (t.Item1 * 1e9, t.Item2)), Range(-30, 0, 5), SKColors.Black, new SKColor(0xd8, 0xd8, 0xd8),
             "Frequency (GHz)", "Response (dB)");
        var red = new SKColor(0xd0, 0x20, 0x20);
        var blue = new SKColor(0x10, 0x50, 0xc8);
        using var p11 = Pen(red, 2.5f);
        using var p21 = Pen(blue, 2.5f);
        Curve(c, f, x => Answer.Db(Answer.S11(x)), p11);
        Curve(c, f, x => Answer.Db(Answer.S21(x)), p21);
        // The legend: bottom-right inside the plot, where both curves leave room.
        var box = SKRect.Create(f.Right - 150, f.Bottom - 92, 132, 76);
        using var fill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
        using var edge = Pen(new SKColor(0x80, 0x80, 0x80), 1);
        c.DrawRect(box, fill);
        c.DrawRect(box, edge);
        c.DrawLine(box.Left + 12, box.Top + 24, box.Left + 52, box.Top + 24, p11);
        Text(c, face, "S11", box.Left + 64, box.Top + 30, 16, SKColors.Black, Align.Left);
        c.DrawLine(box.Left + 12, box.Top + 54, box.Left + 52, box.Top + 54, p21);
        Text(c, face, "S21", box.Left + 64, box.Top + 60, 16, SKColors.Black, Align.Left);
    });

    /// <summary>A Smith chart: <paramref name="admittance"/> draws the constant-g circles and constant-b arcs (tangent
    /// on the left); otherwise the constant-r circles and constant-x arcs. S11 of the band-pass, 1 to 5 GHz, in red.</summary>
    public static RasterImage Smith(string fonts, bool admittance) => Draw(640, 640, SKColors.White, c =>
    {
        using var face = Face(fonts, "DejaVuSans.ttf");
        const float cx = 320, cy = 320, r = 260;
        float sign = admittance ? -1 : 1;   // an admittance chart is the impedance chart turned half a turn
        using var grid = Pen(new SKColor(0xb4, 0xb4, 0xb4), 1);
        using var outer = Pen(new SKColor(0x40, 0x40, 0x40), 1.5f);
        c.Save();
        using (var clip = new SKPath())
        {
            clip.AddCircle(cx, cy, r);
            c.ClipPath(clip, antialias: true);
        }
        foreach (double v in new[] { 0.2, 0.5, 1, 2, 5 })
        {
            // constant r (or g): centre v/(1+v), radius 1/(1+v), on the real axis
            c.DrawCircle(cx + sign * (float)(v / (1 + v)) * r, cy, (float)(1 / (1 + v)) * r, grid);
            // constant x (or b): centre (1, ±1/v), radius 1/v
            foreach (int s in new[] { 1, -1 })
                c.DrawCircle(cx + sign * r, cy - s * (float)(1 / v) * r, (float)(1 / v) * r, grid);
        }
        c.DrawLine(cx - r, cy, cx + r, cy, grid);
        c.Restore();
        c.DrawCircle(cx, cy, r, outer);
        foreach (double v in new[] { 0.2, 0.5, 1, 2, 5 })
        {
            float x = cx + sign * (float)((v - 1) / (v + 1)) * r;
            Text(c, face, v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), x + 10, cy - 6, 13, new SKColor(0x70, 0x70, 0x70));
        }
        using var trace = Pen(new SKColor(0xd0, 0x10, 0x10), 2.5f);
        using var path = new SKPath();
        for (int i = 0; i <= 800; i++)
        {
            double fr = 1e9 + i * 4e9 / 800;
            var g = Answer.S11(fr);
            float x = cx + (float)g.Real * r, y = cy - (float)g.Imaginary * r;
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        c.DrawPath(path, trace);
        Text(c, face, "S11", cx, 30, 18, SKColors.Black);
        if (admittance) Text(c, face, "admittance chart", cx + r - 40, 30, 13, new SKColor(0x70, 0x70, 0x70));
    });

    /// <summary>Through a JPEG at <paramref name="quality"/> and back.</summary>
    public static RasterImage Jpeg(RasterImage img, int quality)
    {
        using var bmp = Bitmap(img);
        using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, quality);
        using var back = SKBitmap.Decode(data.ToArray()).Copy(SKColorType.Rgba8888);
        return RasterImage.FromBitmap(back);
    }

    /// <summary>A plain dB(S21) plot, for the JPEG.</summary>
    public static RasterImage Plain(string fonts) => Draw(820, 520, SKColors.White, c =>
    {
        using var face = Face(fonts, "DejaVuSans.ttf");
        var f = StandardFrame();
        Axes(c, f, face, Range(1, 5, 1).Select(t => (t.Item1 * 1e9, t.Item2)), Range(-30, 0, 5), SKColors.Black, new SKColor(0xd0, 0xd0, 0xd0),
             "Frequency (GHz)", "S21 (dB)");
        using var trace = Pen(new SKColor(0x10, 0x80, 0x30), 2.5f);
        Curve(c, f, x => Answer.Db(Answer.S21(x)), trace);
    });

    private static SKBitmap Bitmap(RasterImage img)
    {
        var bmp = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(img.Rgba, 0, bmp.GetPixels(), img.Rgba.Length);
        return bmp;
    }

    public static byte[] Png(RasterImage image)
    {
        using var bmp = Bitmap(image);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static IEnumerable<(string Name, RasterImage Picture)> All(string fonts)
    {
        yield return ("plot-dark.png", DarkBackground(fonts));
        yield return ("plot-log-1e9.png", LogFrequency(fonts));
        yield return ("plot-two-black.png", TwoBlackTraces(fonts));
        yield return ("plot-legend.png", LegendInside(fonts));
        yield return ("smith-z.png", Smith(fonts, admittance: false));
        yield return ("smith-y.png", Smith(fonts, admittance: true));
        yield return ("plot-jpeg60.png", Jpeg(Plain(fonts), 60));
        yield return ("plot-plain.png", Plain(fonts));
    }

    /// <summary>Writes every picture into <paramref name="dir"/>.</summary>
    public static void Write(string dir, string fonts)
    {
        foreach (var (name, picture) in All(fonts)) File.WriteAllBytes(Path.Combine(dir, name), Png(picture));
    }
}

public sealed class ForeignPlotsGenerator
{
    [RegenerateFact]
    public void WriteTheCommittedPictures() =>
        ForeignPlots.Write(FixturePaths.Require(ForeignPlots.Folder), FixturePaths.Require("src/Render/Assets/Fonts"));
}
