// A synthetic photograph of a board for the IM-13 gates — brief-img-13-board-photos.md §4. Drawn in memory, never a
// committed file: a 60 × 40 mm board (green mask, a 50 Ω line under it with exposed gold pads at both ends, a silkscreen
// box, one part body) on a grey bench, seen through a KNOWN perspective written out by hand here — not solved by the
// code under test — then lit unevenly with a warm cast, given noise, and two specular highlights.

using System;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Image;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Imaging;

internal static class BoardPhotoPictures
{
    public const double WidthMm = 60, HeightMm = 40;

    /// <summary>The 50 Ω line under the mask (1.7 mm on the 30 mil laminate), along y = 20 mm.</summary>
    public const double LineY = 20, LineX0 = 8, LineX1 = 52, LineW = 1.7;

    /// <summary>The exposed pads at the line's ends, x0, x1 (mm), 3 mm tall about the line.</summary>
    public static readonly (double X0, double X1)[] Pads = [(4, 9), (51, 56)];

    /// <summary>The two highlights, board mm: one on the line, one on the right pad's edge.</summary>
    public static readonly (double X, double Y)[] Highlights = [(30, 20), (53, 19)];

    /// <summary>The highlights' radius, photo pixels.</summary>
    public const float HighlightRadiusPx = 13;

    // board mm → photo px: x' = (a x + b y + c) / w, y' = (d x + e y + f) / w, w = g x + h y + 1.
    private const double A = 12, B = 1.5, C = 120, D = -0.8, E = 11, F = 90, G = 0.004, H = -0.002;

    public static (double X, double Y) ToPhoto(double xMm, double yMm)
    {
        double w = G * xMm + H * yMm + 1;
        return ((A * xMm + B * yMm + C) / w, (D * xMm + E * yMm + F) / w);
    }

    /// <summary>The board's corners on the photo, clockwise from the top-left — what a user would drag the handles onto.</summary>
    public static BoardPhotoCorners Corners()
    {
        PixelPoint P(double x, double y) { var (px, py) = ToPhoto(x, y); return new PixelPoint(px, py); }
        return new BoardPhotoCorners([P(0, 0), P(WidthMm, 0), P(WidthMm, HeightMm), P(0, HeightMm)], "60 mm", "40 mm");
    }

    public static ImageSource Source() => TracePictures.Source(Photo(), "bench.jpg");

    public static RasterImage Photo()
    {
        const int w = 900, h = 650;
        var drawn = Pictures.Draw(w, h, c =>
        {
            c.SetMatrix(new SKMatrix((float)A, (float)B, (float)C, (float)D, (float)E, (float)F, (float)G, (float)H, 1));
            using var mask = Pictures.Fill(new SKColor(30, 95, 55));
            using var copperUnder = Pictures.Fill(new SKColor(70, 150, 85));
            using var gold = Pictures.Fill(new SKColor(200, 160, 60));
            using var silk = Pictures.Stroke(new SKColor(225, 225, 225), 0.2f);
            using var body = Pictures.Fill(new SKColor(25, 25, 25));
            c.DrawRect(SKRect.Create(0, 0, (float)WidthMm, (float)HeightMm), mask);
            c.DrawRect(SKRect.Create((float)LineX0, (float)(LineY - LineW / 2), (float)(LineX1 - LineX0), (float)LineW), copperUnder);
            foreach (var (x0, x1) in Pads) c.DrawRect(SKRect.Create((float)x0, (float)(LineY - 1.5), (float)(x1 - x0), 3f), gold);
            c.DrawRect(SKRect.Create(20, 28, 10, 6), silk);
            c.DrawRect(SKRect.Create(30, 6, 3.2f, 1.6f), body);
        }, new SKColor(140, 135, 128));

        // Uneven, warm light and sensor noise.
        var p = drawn.Rgba;
        var rng = new Random(13);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                double light = 0.8 + 0.35 * x / (w - 1);
                p[i] = Byte(p[i] * light * 1.06 + Noise(rng));
                p[i + 1] = Byte(p[i + 1] * light + Noise(rng));
                p[i + 2] = Byte(p[i + 2] * light * 0.92 + Noise(rng));
            }

        // The highlights: clipped white discs over everything, their edges anti-aliased.
        foreach (var (hx, hy) in Highlights)
        {
            var (cx, cy) = ToPhoto(hx, hy);
            for (int y = (int)(cy - HighlightRadiusPx - 2); y <= (int)(cy + HighlightRadiusPx + 2); y++)
                for (int x = (int)(cx - HighlightRadiusPx - 2); x <= (int)(cx + HighlightRadiusPx + 2); x++)
                {
                    double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                    double cover = Math.Clamp(HighlightRadiusPx + 0.5 - d, 0, 1);
                    int i = (y * w + x) * 4;
                    for (int ch = 0; ch < 3; ch++) p[i + ch] = Byte(p[i + ch] * (1 - cover) + 255 * cover);
                }
        }
        return new RasterImage(w, h, p);
    }

    private static double Noise(Random rng)
    {
        // Box–Muller, σ = 3 levels.
        double u = 1 - rng.NextDouble(), v = rng.NextDouble();
        return 3 * Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
    }

    private static byte Byte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}
