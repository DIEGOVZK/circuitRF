using System.Text.Json;
using SkiaSharp;

namespace CircuitRF.SchematicSynth;

/// <summary>
/// A contact sheet of labelled pictures (brief-iml-2 R-iml2-5 <c>--preview</c>): each picture reduced to a tile with its
/// label drawn over it — body boxes coloured by class and named, pins as dots, wires as thin lines, junctions filled,
/// crossings as crosses, word quads outlined by role, chrome bands hatched — for a human to look at before training on
/// a batch. Drawn from the label FILES, so it shows what a trainer would read.
/// </summary>
public static class ContactSheet
{
    private const int TileW = 560, TileH = 420, Caption = 30, Columns = 4, Gap = 8;

    public static void Write(string dir, IReadOnlyList<string> ids, string path)
    {
        int rows = Math.Max(1, (ids.Count + Columns - 1) / Columns);
        int sw = Columns * (TileW + Gap) + Gap, sh = rows * (TileH + Caption + Gap) + Gap;
        using var sheet = new SKBitmap(sw, sh);
        using (var c = new SKCanvas(sheet))
        {
            c.Clear(new SKColor(30, 30, 34));
            for (int k = 0; k < ids.Count; k++)
            {
                float x = Gap + k % Columns * (TileW + Gap), y = Gap + k / Columns * (TileH + Caption + Gap);
                Tile(c, dir, ids[k], new SKRect(x, y, x + TileW, y + TileH));
            }
        }
        using var img = SKImage.FromBitmap(sheet);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static void Tile(SKCanvas c, string dir, string id, SKRect cell)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, id + ".mllabel.json")));
        var root = doc.RootElement;
        using var bmp = SKBitmap.Decode(Path.Combine(dir, root.GetProperty("image").GetString()!));
        float s = Math.Min(cell.Width / bmp.Width, cell.Height / bmp.Height);
        float ox = cell.Left, oy = cell.Top;
        SKPoint P(JsonElement pt) => new(ox + (float)pt[0].GetDouble() * s, oy + (float)pt[1].GetDouble() * s);

        using (var img = SKImage.FromBitmap(bmp))
            c.DrawImage(img, new SKRect(ox, oy, ox + bmp.Width * s, oy + bmp.Height * s), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

        using var thin = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var font = new SKFont(SKTypeface.Default, 10);
        using var ink = new SKPaint { IsAntialias = true, Color = SKColors.White };

        foreach (var a in root.GetProperty("annotations").EnumerateArray())
        {
            var b = a.GetProperty("box");
            thin.Color = new SKColor(150, 150, 150, 200);
            thin.PathEffect = SKPathEffect.CreateDash([4, 3], 0);
            c.DrawRect(new SKRect(ox + (float)b[0].GetDouble() * s, oy + (float)b[1].GetDouble() * s,
                                  ox + (float)b[2].GetDouble() * s, oy + (float)b[3].GetDouble() * s), thin);
            thin.PathEffect = null;
        }

        thin.Color = new SKColor(255, 0, 200, 170);
        foreach (var w in root.GetProperty("wires").EnumerateArray())
        {
            var pts = w.GetProperty("points").EnumerateArray().Select(P).ToList();
            for (int i = 0; i + 1 < pts.Count; i++) c.DrawLine(pts[i], pts[i + 1], thin);
        }
        fill.Color = new SKColor(255, 0, 200);
        foreach (var j in root.GetProperty("junctions").EnumerateArray()) c.DrawCircle(P(j), 3, fill);
        thin.Color = new SKColor(255, 140, 0);
        foreach (var x in root.GetProperty("crossings").EnumerateArray())
        {
            var p = P(x);
            c.DrawLine(p.X - 4, p.Y - 4, p.X + 4, p.Y + 4, thin);
            c.DrawLine(p.X - 4, p.Y + 4, p.X + 4, p.Y - 4, thin);
        }

        foreach (var t in root.GetProperty("texts").EnumerateArray())
        {
            thin.Color = t.GetProperty("role").GetString() switch
            {
                "designator" => new SKColor(0, 200, 255), "value" => new SKColor(0, 230, 120), "net" => new SKColor(255, 220, 0),
                _ => new SKColor(170, 170, 255),
            };
            var q = t.GetProperty("quad").EnumerateArray().Select(P).ToArray();
            using var path = new SKPath();
            path.AddPoly(q, close: true);
            c.DrawPath(path, thin);
        }

        foreach (var p in root.GetProperty("parts").EnumerateArray())
        {
            string cls = p.GetProperty("class").GetString()!;
            var colour = ClassColour(cls);
            var b = p.GetProperty("body");
            var rect = new SKRect(ox + (float)b[0].GetDouble() * s, oy + (float)b[1].GetDouble() * s,
                                  ox + (float)b[2].GetDouble() * s, oy + (float)b[3].GetDouble() * s);
            thin.Color = colour;
            thin.StrokeWidth = 1.6f;
            if (p.TryGetProperty("cut", out var cut) && cut.GetBoolean()) thin.PathEffect = SKPathEffect.CreateDash([3, 2], 0);
            c.DrawRect(rect, thin);
            thin.PathEffect = null;
            thin.StrokeWidth = 1.2f;
            fill.Color = colour;
            foreach (var pin in p.GetProperty("pins").EnumerateArray())
                c.DrawCircle(ox + (float)pin.GetProperty("x").GetDouble() * s, oy + (float)pin.GetProperty("y").GetDouble() * s, 2.5f, fill);
            int turn = p.GetProperty("orientation").GetProperty("turn").GetInt32();
            bool mirror = p.GetProperty("orientation").GetProperty("mirror").GetBoolean();
            string tag = cls + (turn != 0 ? " " + turn : "") + (mirror ? " m" : "");
            fill.Color = colour.WithAlpha(210);
            float tw = font.MeasureText(tag);
            c.DrawRect(rect.Left, rect.Top - 11, tw + 4, 11, fill);
            ink.Color = SKColors.Black;
            c.DrawText(tag, rect.Left + 2, rect.Top - 2, SKTextAlign.Left, font, ink);
        }

        ink.Color = new SKColor(220, 220, 220);
        using var capFont = new SKFont(SKTypeface.Default, 11);
        string conditions = root.TryGetProperty("style", out var st) ? st.GetString() ?? "" : "";
        c.DrawText($"{id}  {bmp.Width}×{bmp.Height}  {conditions}", cell.Left, cell.Bottom + 13, SKTextAlign.Left, capFont, ink);
        int nParts = root.GetProperty("parts").GetArrayLength(), nTexts = root.GetProperty("texts").GetArrayLength();
        c.DrawText($"{nParts} parts, {root.GetProperty("wires").GetArrayLength()} wires, {nTexts} texts, {root.GetProperty("junctions").GetArrayLength()} dots, {root.GetProperty("crossings").GetArrayLength()} crossings",
                   cell.Left, cell.Bottom + 26, SKTextAlign.Left, capFont, ink);
    }

    private static SKColor ClassColour(string cls)
    {
        uint h = 2166136261;
        foreach (char ch in cls) h = (h ^ ch) * 16777619;
        return SKColor.FromHsv(h % 360, 85, 100);
    }
}
