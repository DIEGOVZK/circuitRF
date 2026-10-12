using System.Globalization;
using System.Text.Json;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;

namespace CircuitRF.SchematicSynth;

/// <summary>One drawn piece of a glyph, flattened: a polyline (closed or not, filled or not) with a pen multiplier.</summary>
/// <param name="Ink"><c>symbol</c> for the symbol colour, <c>fill</c> for the palette's fill colour (a grey-filled
/// amplifier), <c>paper</c> for the paper colour (a hollow shape painted over a wire).</param>
public sealed record Stroke(IReadOnlyList<(double X, double Y)> Points, bool Closed, bool Fill, double Pen, float[]? Dash, string Ink);

/// <summary>
/// One drawing of a class in the stroke language (brief-iml3 R-iml3-1): lines, polylines, rectangles, circles, arcs,
/// filled polygons and a few generated shapes (zig-zag, coil, sine, arrow, plus, minus), in the class's canonical
/// orientation, in schematic units (a grid step is 100), with its pin points. A glyph's pins are where circuitRF puts
/// that class's pins, so the glyph drops onto a circuitRF placement; the renderer fits whatever differs and draws the
/// remaining gap as lead.
///
/// <para>A <c>"frame": "box"</c> glyph (the perimeter classes — a block, an IC, an op-amp, an analysis box) is drawn in
/// the unit square and stretched over the placed part's body, its pins being the placement's.</para>
///
/// <para><b>Its body is <see cref="GlyphBody"/>'s rule</b>, the one IML-2's labels and IML-8's size frame use: the
/// strokes are handed to <c>GlyphBody.Of</c> as the symbol primitives they are, so a straight lead ending on a pin is
/// not body, here as there.</para>
/// </summary>
public sealed class Glyph
{
    public required string Class { get; init; }
    public required string Name { get; init; }
    public string? Variant { get; init; }
    public bool BoxFrame { get; init; }
    /// <summary>A box glyph's share kept for its own drawing (an icon at the left of an analysis box): left, top, right,
    /// bottom, as fractions of the box; words go in the rest.</summary>
    public double[] Inset { get; init; } = [0, 0, 0, 0];
    public required IReadOnlyDictionary<string, (double X, double Y)> Pins { get; init; }
    public required IReadOnlyList<Stroke> Strokes { get; init; }
    public GlyphBodyBox Body { get; init; }

    public static Glyph Parse(string cls, string name, JsonElement e)
    {
        string where = $"glyph {name}";
        bool box = e.TryGetProperty("frame", out var fr) && fr.GetString() == "box";
        var pins = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        if (e.TryGetProperty("pins", out var ps))
            foreach (var p in ps.EnumerateObject()) pins[p.Name] = (p.Value[0].GetDouble(), p.Value[1].GetDouble());

        var strokes = new List<Stroke>();
        var prims = new List<SymbolPrimitive>();
        foreach (var s in e.GetProperty("strokes").EnumerateArray()) Add(s, strokes, prims, where);
        if (strokes.Count == 0) throw new FormatException($"{where}: no strokes");

        var body = GlyphBody.Of(prims, [.. pins.Values]);
        return new Glyph
        {
            Class = cls, Name = name, BoxFrame = box, Pins = pins, Strokes = strokes, Body = body,
            Variant = e.TryGetProperty("variant", out var v) ? v.GetString() : null,
            Inset = e.TryGetProperty("inset", out var ins) ? Nums(ins) : [0, 0, 0, 0],
        };
    }

    private static double[] Nums(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetDouble())];

    private static void Add(JsonElement s, List<Stroke> strokes, List<SymbolPrimitive> prims, string where)
    {
        bool fill = s.TryGetProperty("fill", out var f) && f.GetBoolean();
        double pen = s.TryGetProperty("pen", out var pm) ? pm.GetDouble() : 1;
        float[]? dash = s.TryGetProperty("dash", out var d) ? [.. d.EnumerateArray().Select(x => (float)x.GetDouble())] : null;
        string ink = s.TryGetProperty("ink", out var ik) ? ik.GetString()! : "symbol";
        if (ink is not ("symbol" or "fill" or "paper")) throw new FormatException($"{where}: ink is symbol, fill or paper, not '{ink}'");
        const SymbolColorRole role = SymbolColorRole.SymbolLine;
        const SymbolStrokeTier tier = SymbolStrokeTier.Normal;

        void Poly(List<(double X, double Y)> pts, bool closed, bool filled)
        {
            strokes.Add(new Stroke(pts, closed, filled, pen, dash, ink));
            if (closed) prims.Add(new PolygonPrimitive { ColorRole = role, StrokeTier = tier, Filled = filled, Points = [.. pts.Select(p => new[] { p.X, p.Y })] });
            else prims.Add(new PolylinePrimitive { ColorRole = role, StrokeTier = tier, Points = [.. pts.Select(p => new[] { p.X, p.Y })] });
        }

        if (s.TryGetProperty("line", out var l))
        {
            var a = Nums(l);
            strokes.Add(new Stroke([(a[0], a[1]), (a[2], a[3])], false, false, pen, dash, ink));
            prims.Add(new LinePrimitive(role, tier, a[0], a[1], a[2], a[3]));
        }
        else if (s.TryGetProperty("poly", out var pl))
        {
            var a = Nums(pl);
            if (a.Length < 4 || a.Length % 2 != 0) throw new FormatException($"{where}: a poly is x,y pairs, two at least");
            bool closed = fill || (s.TryGetProperty("close", out var c) && c.GetBoolean());
            Poly([.. Enumerable.Range(0, a.Length / 2).Select(k => (a[2 * k], a[2 * k + 1]))], closed, fill);
        }
        else if (s.TryGetProperty("rect", out var rc))
        {
            var a = Nums(rc);
            Poly([(a[0], a[1]), (a[2], a[1]), (a[2], a[3]), (a[0], a[3])], true, fill);
        }
        else if (s.TryGetProperty("circle", out var ci))
        {
            var a = Nums(ci);
            strokes.Add(new Stroke(Arc(a[0], a[1], a[2], 0, 360), true, fill, pen, dash, ink));
            prims.Add(new CirclePrimitive { ColorRole = role, StrokeTier = tier, Filled = fill, Cx = a[0], Cy = a[1], R = a[2] });
        }
        else if (s.TryGetProperty("arc", out var ar))
        {
            var a = Nums(ar);
            strokes.Add(new Stroke(Arc(a[0], a[1], a[2], a[3], a[4]), false, false, pen, dash, ink));
            prims.Add(new ArcPrimitive { ColorRole = role, StrokeTier = tier, Cx = a[0], Cy = a[1], R = a[2], StartDeg = a[3], SweepDeg = a[4] });
        }
        else if (s.TryGetProperty("zigzag", out var zz))
        {
            var a = Nums(zz);
            int peaks = s.TryGetProperty("peaks", out var pk) ? pk.GetInt32() : 6;
            double amp = s.TryGetProperty("amp", out var am) ? am.GetDouble() : 30;
            Poly(Zigzag(a[0], a[1], a[2], a[3], peaks, amp), false, false);
        }
        else if (s.TryGetProperty("coil", out var co))
        {
            var a = Nums(co);
            int turns = s.TryGetProperty("turns", out var tu) ? tu.GetInt32() : 4;
            double amp = s.TryGetProperty("amp", out var am) ? am.GetDouble() : 25;
            double loop = s.TryGetProperty("loop", out var lo) ? lo.GetDouble() : 0;
            Poly(Coil(a[0], a[1], a[2], a[3], turns, amp, loop), false, false);
        }
        else if (s.TryGetProperty("sine", out var si))
        {
            var a = Nums(si);   // cx, cy, length, amplitude
            double cycles = s.TryGetProperty("cycles", out var cy) ? cy.GetDouble() : 1;
            bool vertical = s.TryGetProperty("vertical", out var ve) && ve.GetBoolean();
            var pts = new List<(double, double)>();
            for (int k = 0; k <= 48; k++)
            {
                double t = k / 48.0, along = (t - 0.5) * a[2], across = -a[3] * Math.Sin(2 * Math.PI * cycles * t);
                pts.Add(vertical ? (a[0] + across, a[1] + along) : (a[0] + along, a[1] + across));
            }
            Poly(pts, false, false);
        }
        else if (s.TryGetProperty("arrow", out var aw))
        {
            var a = Nums(aw);
            double head = s.TryGetProperty("head", out var h) ? h.GetDouble() : 20;
            bool open = s.TryGetProperty("open", out var op) && op.GetBoolean();
            strokes.Add(new Stroke([(a[0], a[1]), (a[2], a[3])], false, false, pen, dash, ink));
            prims.Add(new LinePrimitive(role, tier, a[0], a[1], a[2], a[3]));
            var tri = Head(a[0], a[1], a[2], a[3], head);
            if (open) Poly([tri[1], tri[0], tri[2]], false, false);
            else Poly(tri, true, true);
        }
        else if (s.TryGetProperty("plus", out var pp))
        {
            var a = Nums(pp);
            double h = a[2] / 2;
            strokes.Add(new Stroke([(a[0] - h, a[1]), (a[0] + h, a[1])], false, false, pen, null, ink));
            strokes.Add(new Stroke([(a[0], a[1] - h), (a[0], a[1] + h)], false, false, pen, null, ink));
            prims.Add(new LinePrimitive(role, tier, a[0] - h, a[1], a[0] + h, a[1]));
            prims.Add(new LinePrimitive(role, tier, a[0], a[1] - h, a[0], a[1] + h));
        }
        else if (s.TryGetProperty("minus", out var mi))
        {
            var a = Nums(mi);
            double h = a[2] / 2;
            strokes.Add(new Stroke([(a[0] - h, a[1]), (a[0] + h, a[1])], false, false, pen, null, ink));
            prims.Add(new LinePrimitive(role, tier, a[0] - h, a[1], a[0] + h, a[1]));
        }
        else throw new FormatException($"{where}: unknown stroke {s.GetRawText()}");
    }

    // ── generated shapes ─────────────────────────────────────────────────────

    /// <summary>An arc, Skia's angles: 0° along +x, positive sweep clockwise on a y-down page.</summary>
    internal static List<(double X, double Y)> Arc(double cx, double cy, double r, double start, double sweep)
    {
        int n = Math.Max(8, (int)Math.Ceiling(Math.Abs(sweep) / 6));
        var pts = new List<(double, double)>(n + 1);
        for (int k = 0; k <= n; k++)
        {
            double t = (start + sweep * k / n) * Math.PI / 180;
            pts.Add((cx + r * Math.Cos(t), cy + r * Math.Sin(t)));
        }
        if (Math.Abs(sweep) >= 360) pts.RemoveAt(pts.Count - 1);
        return pts;
    }

    /// <summary>A zig-zag from (x1,y1) to (x2,y2): <paramref name="peaks"/> half-waves of <paramref name="amp"/>, a
    /// quarter wave at each end so it starts and ends on the axis.</summary>
    private static List<(double X, double Y)> Zigzag(double x1, double y1, double x2, double y2, int peaks, double amp)
    {
        double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt(dx * dx + dy * dy);
        double nx = dy / len, ny = -dx / len;
        var pts = new List<(double, double)> { (x1, y1) };
        for (int k = 0; k < peaks; k++)
        {
            double t = (k + 0.5) / peaks, side = k % 2 == 0 ? 1 : -1;
            pts.Add((x1 + dx * t + nx * amp * side, y1 + dy * t + ny * amp * side));
        }
        pts.Add((x2, y2));
        return pts;
    }

    /// <summary>A coil from (x1,y1) to (x2,y2), bulging to the right of the direction of travel: semicircle bumps when
    /// <paramref name="loop"/> is 0, a prolate cycloid with loops that cross back over themselves when it is above 0.</summary>
    private static List<(double X, double Y)> Coil(double x1, double y1, double x2, double y2, int turns, double amp, double loop)
    {
        double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt(dx * dx + dy * dy);
        // Bumps on the +x side of a coil drawn downward, as circuitRF's own inductor bulges.
        double ux = dx / len, uy = dy / len, nx = uy, ny = -ux;
        var pts = new List<(double, double)>();
        int per = 24;
        for (int k = 0; k <= turns * per; k++)
        {
            double u = k / (double)(turns * per), th = 2 * Math.PI * turns * u;
            double along, across;
            if (loop <= 0)
            {
                // Half-ellipses: within each turn the point runs half an ellipse len / turns long, amp high.
                int turn = (int)Math.Floor(Math.Min(u * turns, turns - 1e-9));
                double r = len / turns / 2, local = (u * turns - turn) * Math.PI;
                along = turn * 2 * r + r - r * Math.Cos(local);
                across = amp * Math.Sin(local);
            }
            else
            {
                double lambda = 1 + loop, r = len / (2 * Math.PI * turns);
                along = r * (th - lambda * Math.Sin(th)) + 0;
                across = amp * (1 - Math.Cos(th)) / 2;
            }
            pts.Add((x1 + ux * along + nx * across, y1 + uy * along + ny * across));
        }
        pts[^1] = (x2, y2);
        return pts;
    }

    /// <summary>An arrowhead at (x2,y2) pointing along the line: tip, then the two barbs.</summary>
    internal static List<(double X, double Y)> Head(double x1, double y1, double x2, double y2, double size)
    {
        double dx = x2 - x1, dy = y2 - y1, len = Math.Max(1e-9, Math.Sqrt(dx * dx + dy * dy));
        double ux = dx / len, uy = dy / len;
        double bx = x2 - ux * size, by = y2 - uy * size, w = size * 0.4;
        return [(x2, y2), (bx - uy * w, by + ux * w), (bx + uy * w, by - ux * w)];
    }

    public override string ToString() => Name;

    internal static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
