using System.Globalization;
using CircuitRF.Design.Schematic;
using SkiaSharp;

namespace CircuitRF.SchematicSynth;

/// <summary>Which mask a piece of ink belongs to; <see cref="None"/> is background (paper, grid).</summary>
public enum Layer { Wire, Symbol, Text, Annotation, None }

/// <summary>A point in schematic (world) units.</summary>
public readonly record struct W(double X, double Y);

/// <summary>A polyline to draw: world points, a pen as a multiple of the picture's <paramref name="PenOf"/> pen
/// (<c>w</c>ire, <c>s</c>ymbol, <c>a</c>nnotation — resolved to device pixels once the view is known), an optional dash
/// in world units.</summary>
public sealed record Ink(Layer Layer, SKColor Colour, IReadOnlyList<W> Points, bool Closed, bool Fill, double Pen, char PenOf, float[]? Dash);

/// <summary>A mark drawn at a point at a size of its own — a junction dot, a pin marker: <paramref name="World"/> units
/// across, never under <paramref name="MinPx"/> device pixels, as an editor draws a dot a fixed few pixels wide.</summary>
public sealed record Marker(Layer Layer, SKColor Colour, W At, string Shape, double World, double MinPx, bool Fill);

/// <summary>A word as drawn: its string, role, face and size (world units), baseline origin and angle, and its quad.</summary>
public sealed class SceneText
{
    public required string String { get; init; }
    public required string Role { get; init; }
    public required SKTypeface Face { get; init; }
    public required double Size { get; init; }
    public required SKColor Colour { get; init; }
    public W Origin { get; set; }
    /// <summary>Degrees, counter-clockwise reading direction: 0 reads left to right, 90 reads bottom to top.</summary>
    public double Angle { get; set; }
    /// <summary>When above 0, the characters from here on are drawn as a subscript.</summary>
    public int SubscriptFrom { get; init; }
    public Layer Layer { get; init; } = Layer.Text;
    /// <summary>The part this word belongs to, when it is one's designator or value.</summary>
    public ScenePart? Owner { get; init; }
    /// <summary>Kept out of <c>texts</c> (chrome words): drawn, masked as annotation, never read.</summary>
    public bool Unlabelled { get; init; }
    public string Id { get; set; } = "";

    private (double Width, double Ascent, double Descent)? _metrics;

    /// <summary>Advance width, ascent (negative) and descent (positive), world units, at angle 0 — measured once.</summary>
    public (double Width, double Ascent, double Descent) Metrics() => _metrics ??= Measure();

    private (double Width, double Ascent, double Descent) Measure()
    {
        using var font = Font(Size);
        if (SubscriptFrom <= 0 || SubscriptFrom >= String.Length)
            return (font.MeasureText(String), font.Metrics.Ascent, font.Metrics.Descent);
        using var sub = Font(Size * SubScale);
        double w = font.MeasureText(String[..SubscriptFrom]) + sub.MeasureText(String[SubscriptFrom..]);
        return (w, font.Metrics.Ascent, Math.Max(font.Metrics.Descent, Size * SubDrop + sub.Metrics.Descent));
    }

    public const double SubScale = 0.7, SubDrop = 0.3;

    public SKFont Font(double size) => new(Face, (float)size) { Subpixel = true, Hinting = SKFontHinting.None, Edging = SKFontEdging.Antialias };

    /// <summary>The line box, ascent to descent and origin to advance, as four world corners in reading order.</summary>
    public W[] Quad()
    {
        var (w, a, d) = Metrics();
        double t = Angle * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t);
        // Reading direction (c, −s) on a y-down page; "down" in the text's frame is (s, c).
        W P(double along, double down) => new(Origin.X + along * c + down * s, Origin.Y - along * s + down * c);
        return [P(0, a), P(w, a), P(w, d), P(0, d)];
    }

    public (double X0, double Y0, double X1, double Y1) Box()
    {
        var q = Quad();
        return (q.Min(p => p.X), q.Min(p => p.Y), q.Max(p => p.X), q.Max(p => p.Y));
    }
}

/// <summary>A part as a style pack draws it: its class, ink, body box and pins in world units.</summary>
public sealed class ScenePart
{
    public required string Id { get; init; }
    public required string Class { get; init; }
    public string? Variant { get; init; }
    public int Turn { get; init; }
    public bool Mirror { get; init; }
    public required (double X0, double Y0, double X1, double Y1) Body { get; init; }
    public required IReadOnlyList<(string Name, W At)> Pins { get; init; }
    public List<Ink> Inks { get; } = [];
    public SceneText? Designator { get; set; }
    public List<SceneText> Values { get; } = [];
    public Dictionary<string, string> Params { get; } = new(StringComparer.Ordinal);
}

/// <summary>An annotation: ink the reader must learn to drop, with its box (world) and kind.</summary>
public sealed record SceneAnnotation((double X0, double Y0, double X1, double Y1) Box, string Kind);

/// <summary>Everything one foreign picture draws, in world units, before the view.</summary>
public sealed class Scene
{
    public List<ScenePart> Parts { get; } = [];
    public List<Ink> Wires { get; } = [];
    /// <summary>The wires as labelled: straight polylines, hops and all drawn ink being the wire's.</summary>
    public List<IReadOnlyList<W>> WirePaths { get; } = [];
    public List<W> Junctions { get; } = [];
    public List<W> Crossings { get; } = [];
    public List<SceneText> Texts { get; } = [];
    /// <summary>Annotation ink drawn under the circuit: frames, arrows, a page border.</summary>
    public List<Ink> Annotations { get; } = [];
    /// <summary>Annotation ink drawn over everything: a cursor, a selection rectangle.</summary>
    public List<Ink> Overlays { get; } = [];
    /// <summary>Each drawn circuit's extent — two when the picture holds two circuits.</summary>
    public List<(double X0, double Y0, double X1, double Y1)> Circuits { get; } = [];
    public List<SceneAnnotation> AnnotationBoxes { get; } = [];
    public List<Ink> Background { get; } = [];
    public List<Marker> Markers { get; } = [];

    /// <summary>The bounds of every drawn thing.</summary>
    public (double X0, double Y0, double X1, double Y1) Extent(bool withAnnotations = true)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        void Add(double x, double y) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        foreach (var p in Parts)
        {
            Add(p.Body.X0, p.Body.Y0); Add(p.Body.X1, p.Body.Y1);
            foreach (var (_, at) in p.Pins) Add(at.X, at.Y);
            foreach (var i in p.Inks) foreach (var q in i.Points) Add(q.X, q.Y);
        }
        foreach (var w in Wires) foreach (var q in w.Points) Add(q.X, q.Y);
        foreach (var t in Texts) { var b = t.Box(); Add(b.X0, b.Y0); Add(b.X1, b.Y1); }
        if (withAnnotations)
            foreach (var a in AnnotationBoxes) { Add(a.Box.X0, a.Box.Y0); Add(a.Box.X1, a.Box.Y1); }
        return x0 <= x1 ? (x0, y0, x1, y1) : (0, 0, 100, 100);
    }

    /// <summary>Whether a world box is clear of every part's body and every placed word.</summary>
    public bool Clear((double X0, double Y0, double X1, double Y1) b, ScenePart? except = null, double air = 10)
    {
        bool Hit((double X0, double Y0, double X1, double Y1) o) =>
            b.X0 - air < o.X1 && o.X0 < b.X1 + air && b.Y0 - air < o.Y1 && o.Y0 < b.Y1 + air;
        foreach (var p in Parts) if (!ReferenceEquals(p, except) && Hit(p.Body)) return false;
        foreach (var t in Texts) if (Hit(t.Box())) return false;
        foreach (var a in AnnotationBoxes) if (Hit(a.Box)) return false;
        return true;
    }

    /// <summary>Whether a world box crosses any wire.</summary>
    public bool TouchesWire((double X0, double Y0, double X1, double Y1) b)
    {
        foreach (var w in WirePaths)
            for (int i = 0; i + 1 < w.Count; i++)
            {
                var (a, c) = (w[i], w[i + 1]);
                if (Math.Max(a.X, c.X) >= b.X0 && Math.Min(a.X, c.X) <= b.X1 && Math.Max(a.Y, c.Y) >= b.Y0 && Math.Min(a.Y, c.Y) <= b.Y1)
                    return true;
            }
        return false;
    }

    internal static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
