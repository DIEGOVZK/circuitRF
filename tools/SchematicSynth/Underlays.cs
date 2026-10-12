using SkiaSharp;

namespace CircuitRF.SchematicSynth;

/// <summary>
/// Pictures to place UNDER a schematic, as a user does with Paste Image: a handful drawn from the batch seed into the
/// batch folder's <c>.underlay/</c> (so a run is reproducible and the renderer reads them through its own bitmap
/// cache, by path, as it reads any placed picture). Soft gradients, blotches and strokes — busy enough to be clutter
/// under the drawing, and drawn by this code, so no third-party picture is involved.
/// </summary>
public sealed class Underlays
{
    private readonly IReadOnlyList<string> _paths;

    private Underlays(IReadOnlyList<string> paths) => _paths = paths;

    public string Pick(Random r) => _paths[r.Next(_paths.Count)];

    public static Underlays Create(string folder, int seed, int count = 6)
    {
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        for (int k = 0; k < count; k++)
        {
            string path = Path.Combine(folder, $"u{k}.png");
            if (!File.Exists(path)) File.WriteAllBytes(path, Draw(new Random(unchecked(seed * 31 + k * 977 + 5))));
            paths.Add(Path.GetFullPath(path));
        }
        return new Underlays(paths);
    }

    private static byte[] Draw(Random r)
    {
        int w = r.Next(320, 900), h = r.Next(240, 700);
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            SKColor Rnd() => new((byte)r.Next(256), (byte)r.Next(256), (byte)r.Next(256));
            using var grad = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h), [Rnd(), Rnd()], SKShaderTileMode.Clamp);
            using var fill = new SKPaint { Shader = grad };
            c.DrawRect(0, 0, w, h, fill);
            for (int i = 0; i < r.Next(4, 16); i++)
            {
                using var blob = new SKPaint { Color = Rnd().WithAlpha((byte)r.Next(40, 160)), IsAntialias = true };
                c.DrawOval(r.Next(w), r.Next(h), r.Next(10, w / 3), r.Next(10, h / 3), blob);
            }
            for (int i = 0; i < r.Next(3, 20); i++)
            {
                using var line = new SKPaint { Color = Rnd(), StrokeWidth = r.Next(1, 6), IsAntialias = true, Style = SKPaintStyle.Stroke };
                c.DrawLine(r.Next(w), r.Next(h), r.Next(w), r.Next(h), line);
            }
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
