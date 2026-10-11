// The generator of brief-img-12-acceptance-docs-example.md R-im12-2's committed pictures: drawings in conventions
// circuitRF does not use, and the variants real pictures suffer. Run once (CRF_IM12_FIXTURES=1 dotnet test --filter
// ForeignPicturesGenerator) and the PNGs beside this file are rewritten; the gates read the committed bytes, never a
// fresh drawing, because a typeface rasterises a little differently from one platform to the next (D17).
//
// Every face is one circuitRF ships (src/Render/Assets/Fonts). Every picture has its answer beside it: a schematic its
// .cnl, the layout its shapes in pixels (dark-layout.expected.json).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Design.Imaging;
using CircuitRF.Render;
using CircuitRF.Ui.Tests.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

internal static class ForeignPictures
{
    public const string Folder = "tests/Ui.Tests/ImageRecognition/Fixtures";

    // ── an IEC schematic: boxes for R and L, a 4 px pen, words in the oblique and condensed faces ──────────────

    public const string SchematicCnl = """
        Port:P1  in   0  Num=1  Z=50 Ohm
        L:L1     in   mid  L=10 nH
        C:C1     mid  0  C=4.7 pF
        R:R1     mid  out  R=22 Ohm
        Port:P2  out  0  Num=2  Z=50 Ohm
        """;

    private const float Pen = 4;

    private static SKTypeface Face(string file) =>
        SKTypeface.FromFile(Path.Combine(FixturePaths.Require("src/Render/Assets/Fonts"), file))
        ?? throw new FileNotFoundException(file);

    /// <summary>A line of text, its ink's left edge at <paramref name="left"/>, a <paramref name="cap"/> px cap height.</summary>
    private static void Text(SKCanvas c, SKTypeface face, string text, float left, float baseline, float cap)
    {
        using var font = new SKFont(face, cap / 0.729f);   // DejaVu Sans: cap height 0.729 em, every width
        using var paint = Pictures.Fill(SKColors.Black);
        font.MeasureText(text, out var bounds);
        c.DrawText(text, left - bounds.Left, baseline, SKTextAlign.Left, font, paint);
    }

    public static RasterImage IecSchematic() => Pictures.Draw(960, 520, c =>
    {
        using var pen = Pictures.Stroke(SKColors.Black, Pen);
        using var solid = Pictures.Fill(SKColors.Black);
        using var oblique = Face("DejaVuSans-Oblique.ttf");
        using var condensed = Face("DejaVuSansCondensed.ttf");
        using var condensedOblique = Face("DejaVuSansCondensed-Oblique.ttf");
        const float y = 280, cap = 24;

        // P1, a terminal circle, and its lead.
        c.DrawCircle(80, y, 16, pen);
        Text(c, oblique, "P1", 62, y - 40, cap);
        c.DrawLine(96, y, 224, y, pen);
        // L1: the IEC inductor, a filled box three times as long as it is wide.
        c.DrawRect(SKRect.Create(224, y - 16, 96, 32), solid);
        Text(c, oblique, "L1", 248, y - 40, cap);
        Text(c, condensed, "10nH", 236, y + 64, cap);
        c.DrawLine(320, y, 584, y, pen);
        // C1 to ground at the node, two flat plates and a triangle.
        c.DrawLine(440, y, 440, 364, pen);
        c.DrawLine(410, 364, 470, 364, pen);
        c.DrawLine(410, 380, 470, 380, pen);
        c.DrawLine(440, 380, 440, 440, pen);
        using (var tri = new SKPath())
        {
            tri.MoveTo(410, 440);
            tri.LineTo(470, 440);
            tri.LineTo(440, 472);
            tri.Close();
            c.DrawPath(tri, pen);
        }
        Text(c, oblique, "C1", 492, 364, cap);
        Text(c, condensedOblique, "4.7pF", 492, 404, cap);
        // R1: the IEC resistor, a hollow box two and a half times as long as it is wide.
        c.DrawRect(SKRect.Create(584, y - 19, 96, 38), pen);
        Text(c, oblique, "R1", 608, y - 40, cap);
        Text(c, condensed, "22R", 604, y + 64, cap);
        c.DrawLine(680, y, 864, y, pen);
        // P2.
        c.DrawCircle(880, y, 16, pen);
        Text(c, oblique, "P2", 862, y - 40, cap);
    });

    // ── a layout as a dark viewer draws it: translucent layers over black, no outlines ───────────────────────

    public const double LayoutUmPerPx = 25;

    public static readonly SKColor Ground = new(0x10, 0x12, 0x16);
    public static readonly SKColor Top = new(0xe0, 0x30, 0x30, 0xa0);
    public static readonly SKColor Bottom = new(0x30, 0x70, 0xe0, 0xa0);

    /// <summary>Each layer's shapes, in pixels (x right, y down).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<(double X, double Y)[]>> LayoutShapes { get; } =
        new Dictionary<string, IReadOnlyList<(double X, double Y)[]>>
        {
            ["Top Copper"] =
            [
                Rect(40.5, 60.25, 300.5, 76.25),                                    // a line …
                [(300.5, 60.25), (360.5, 60.25), (400.5, 100.25), (400.5, 260.5),   // … turning 45° and down
                 (384.5, 260.5), (384.5, 106.88), (353.87, 76.25), (300.5, 76.25)],
                Rect(120.25, 120.5, 200.25, 200.5),                                 // a pad over the plane's edge
            ],
            ["Bottom Copper"] =
            [
                Rect(160.5, 160.25, 460.5, 300.25),                                 // a plane
                Rect(20.25, 220.5, 100.25, 280.5),                                  // and an island
            ],
        };

    private static (double X, double Y)[] Rect(double x0, double y0, double x1, double y1) => [(x0, y0), (x1, y0), (x1, y1), (x0, y1)];

    public static RasterImage DarkLayout() => Pictures.Draw(480, 320, c =>
    {
        // Each layer drawn as ONE path, so its own shapes do not darken where they meet; the top over the bottom.
        foreach (var (layer, colour) in new[] { ("Bottom Copper", Bottom), ("Top Copper", Top) })
        {
            using var paint = Pictures.Fill(colour);
            using var path = new SKPath();
            foreach (var poly in LayoutShapes[layer])
            {
                path.MoveTo((float)poly[0].X, (float)poly[0].Y);
                foreach (var (x, y) in poly.Skip(1)) path.LineTo((float)x, (float)y);
                path.Close();
            }
            c.DrawPath(path, paint);
        }
    }, Ground);

    // ── what real pictures suffer ────────────────────────────────────────────────────────────────────────────

    /// <summary>Through a JPEG at <paramref name="quality"/> and back.</summary>
    public static RasterImage Jpeg(RasterImage img, int quality)
    {
        using var bmp = Bitmap(img);
        using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, quality);
        using var back = SKBitmap.Decode(data.ToArray()).Copy(SKColorType.Rgba8888);
        return RasterImage.FromBitmap(back);
    }

    /// <summary>Turned <paramref name="degrees"/> about its centre, on white, the size kept.</summary>
    public static RasterImage Rotated(RasterImage img, float degrees)
    {
        using var src = SKImage.FromBitmap(Bitmap(img));
        return Pictures.Draw(img.Width, img.Height, c =>
        {
            c.RotateDegrees(degrees, img.Width / 2f, img.Height / 2f);
            c.DrawImage(src, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        });
    }

    /// <summary>Scaled by <paramref name="factor"/>, linear filtering.</summary>
    public static RasterImage Scaled(RasterImage img, double factor)
    {
        int w = (int)Math.Round(img.Width * factor), h = (int)Math.Round(img.Height * factor);
        using var src = SKImage.FromBitmap(Bitmap(img));
        return Pictures.Draw(w, h, c => c.DrawImage(src, SKRect.Create(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)));
    }

    /// <summary>A <paramref name="share"/> of the pixels set to black or white at random (seeded).</summary>
    public static RasterImage Salt(RasterImage img, double share, int seed)
    {
        var rgba = (byte[])img.Rgba.Clone();
        var rng = new Random(seed);
        int n = (int)(share * img.Width * img.Height);
        for (int k = 0; k < n; k++)
        {
            int i = 4 * rng.Next(img.Width * img.Height);
            byte v = rng.Next(2) == 0 ? (byte)0 : (byte)255;
            rgba[i] = rgba[i + 1] = rgba[i + 2] = v;
            rgba[i + 3] = 255;
        }
        return new RasterImage(img.Width, img.Height, rgba);
    }

    private static SKBitmap Bitmap(RasterImage img)
    {
        var bmp = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(img.Rgba, 0, bmp.GetPixels(), img.Rgba.Length);
        return bmp;
    }

    /// <summary>Every committed picture, by file name.</summary>
    public static IEnumerable<(string Name, RasterImage Picture)> All()
    {
        var schematic = IecSchematic();
        yield return ("iec-schematic.png", schematic);
        yield return ("iec-schematic-jpeg60.png", Jpeg(schematic, 60));
        yield return ("iec-schematic-rotated.png", Rotated(schematic, 1.5f));
        yield return ("iec-schematic-half.png", Scaled(schematic, 0.5));
        yield return ("iec-schematic-salt.png", Salt(schematic, 0.002, 12));
        var layout = DarkLayout();
        yield return ("dark-layout.png", layout);
        yield return ("dark-layout-jpeg60.png", Jpeg(layout, 60));
    }

    /// <summary>Writes every picture and its answer into <paramref name="dir"/>.</summary>
    public static void Write(string dir)
    {
        foreach (var (name, picture) in All()) File.WriteAllBytes(Path.Combine(dir, name), Pictures.Png(picture));
        File.WriteAllText(Path.Combine(dir, "iec-schematic.cnl"), SchematicCnl.Replace("\r\n", "\n") + "\n");
        var expected = new
        {
            umPerPx = LayoutUmPerPx,
            layers = LayoutShapes.ToDictionary(kv => kv.Key, kv => kv.Value.Select(p => p.Select(q => new[] { q.X, q.Y }).ToArray()).ToArray()),
        };
        File.WriteAllText(Path.Combine(dir, "dark-layout.expected.json"),
                          JsonSerializer.Serialize(expected, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}

/// <summary>Runs only when CRF_IM12_FIXTURES is set; otherwise skipped, saying how.</summary>
public sealed class RegenerateFactAttribute : FactAttribute
{
    public RegenerateFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CRF_IM12_FIXTURES") is not { Length: > 0 })
            Skip = "Rewrites the committed foreign pictures; set CRF_IM12_FIXTURES=1 to run it.";
    }
}

public sealed class ForeignPicturesGenerator
{
    [RegenerateFact]
    public void WriteTheCommittedPictures() => ForeignPictures.Write(FixturePaths.Require(ForeignPictures.Folder));
}
