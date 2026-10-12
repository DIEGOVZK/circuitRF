using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CircuitRF.Design.Symbol;

namespace CircuitRF.Design.Schematic;

using Symbol = global::CircuitRF.Design.Symbol.Symbol;

/// <summary>A glyph's body box in symbol-LOCAL units (canonical orientation, before mirror and rotation).</summary>
public readonly record struct GlyphBodyBox(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width  => MaxX - MinX;
    public double Height => MaxY - MinY;
    public double CenterX => (MinX + MaxX) * 0.5;
    public double CenterY => (MinY + MaxY) * 0.5;
}

/// <summary>
/// The BODY of a symbol glyph: its drawing without the straight leads that end on its pins
/// (brief-iml-2 R-iml2-4, brief-iml-8 R-iml8-1).
///
/// <para><b>Why the leads are left out.</b> How long a part's leads are is a convention of the editor
/// that drew it, not a property of the part: one editor runs a resistor's leads a full grid step, another
/// stops them at the zig-zag. The body is what two editors' drawings of the same part have in common, so
/// it is what a picture's part size is measured with (IML-8) and what the training labels' <c>body</c>
/// box is (IML-2, IML-4's labelling guide). One definition, here, serves all three.</para>
///
/// <para><b>The rule.</b> A <see cref="LinePrimitive"/> with either end on a pin is a lead. A
/// <see cref="PolylinePrimitive"/> that starts or ends on a pin loses that first or last segment (the
/// resistor is ONE polyline, leads included). Text is not body — a polarity mark or a port name sits
/// beside the drawing. Everything else counts, bounded tightly: arcs and curves are sampled, not taken
/// as the conservative hull <see cref="SymbolGeometry.ComputeBb"/> uses, because an inductor's coils
/// bulge one way and their full circles would double its width. A glyph that is nothing BUT leads
/// falls back to its whole drawing.</para>
///
/// <para>The box is geometry: the pen's half width is not added. Computed once per kind and port count
/// and cached.</para>
/// </summary>
public static class GlyphBody
{
    private const double OnPin = 0.5;

    private static readonly ConcurrentDictionary<(SymbolKind, int), GlyphBodyBox> Cache = new();

    /// <summary>The body of a built-in kind's glyph, as <see cref="BuiltInSymbols.Primitives(SymbolKind, int)"/>
    /// draws it for <paramref name="portCount"/> ports.</summary>
    public static GlyphBodyBox Of(SymbolKind kind, int portCount)
        => Cache.GetOrAdd((kind, portCount), k => Of(BuiltInSymbols.Primitives(k.Item1, k.Item2)));

    /// <summary>The body of any symbol — a per-instance glyph (an SnP box, a filter's form) included.</summary>
    public static GlyphBodyBox Of(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var pins = new List<(double X, double Y)>(symbol.Pins.Count);
        foreach (var p in symbol.Pins) pins.Add((p.LocalX, p.LocalY));
        return Of(symbol.Primitives, pins);
    }

    /// <summary>The body of a primitive list whose pins are at <paramref name="pins"/>.</summary>
    public static GlyphBodyBox Of(IReadOnlyList<SymbolPrimitive> primitives, IReadOnlyList<(double X, double Y)> pins)
    {
        var body = new Bounds();
        var all  = new Bounds();

        bool IsPin(double x, double y)
        {
            foreach (var (px, py) in pins)
                if (Math.Abs(px - x) <= OnPin && Math.Abs(py - y) <= OnPin) return true;
            return false;
        }

        foreach (var prim in primitives)
        {
            switch (prim)
            {
                case TextPrimitive or BitmapPrimitive:
                    continue;

                case LinePrimitive l:
                    all.Add(l.X1, l.Y1);
                    all.Add(l.X2, l.Y2);
                    if (IsPin(l.X1, l.Y1) || IsPin(l.X2, l.Y2)) continue;
                    body.Add(l.X1, l.Y1);
                    body.Add(l.X2, l.Y2);
                    continue;

                case PolylinePrimitive pl:
                {
                    var pts = pl.Points;
                    if (pts.Count < 2) continue;
                    foreach (var pt in pts) all.Add(pt[0], pt[1]);
                    int first = IsPin(pts[0][0], pts[0][1]) ? 1 : 0;
                    int last  = IsPin(pts[^1][0], pts[^1][1]) ? pts.Count - 2 : pts.Count - 1;
                    for (int i = first; i <= last; i++) body.Add(pts[i][0], pts[i][1]);
                    continue;
                }
            }

            var shape = new Bounds();
            AddShape(prim, shape);
            body.Add(shape);
            all.Add(shape);
        }

        var b = body.Any ? body : all;
        return b.Any ? new GlyphBodyBox(b.MinX, b.MinY, b.MaxX, b.MaxY) : default;
    }

    private static void AddShape(SymbolPrimitive prim, Bounds b)
    {
        switch (prim)
        {
            case RectPrimitive r:
                b.Add(r.Cx - r.W * 0.5, r.Cy - r.H * 0.5);
                b.Add(r.Cx + r.W * 0.5, r.Cy + r.H * 0.5);
                break;
            case RoundedRectPrimitive rr:
                b.Add(rr.Cx - rr.W * 0.5, rr.Cy - rr.H * 0.5);
                b.Add(rr.Cx + rr.W * 0.5, rr.Cy + rr.H * 0.5);
                break;
            case CirclePrimitive c:
                b.Add(c.Cx - c.R, c.Cy - c.R);
                b.Add(c.Cx + c.R, c.Cy + c.R);
                break;
            case EllipsePrimitive e:
                b.Add(e.Cx - e.Rx, e.Cy - e.Ry);
                b.Add(e.Cx + e.Rx, e.Cy + e.Ry);
                break;
            case ArcPrimitive a:
            {
                // Skia's angles: 0° along +x, positive sweep clockwise on a y-down screen — the
                // renderer's DrawArc of the same numbers.
                const int n = 64;
                for (int k = 0; k <= n; k++)
                {
                    double t = (a.StartDeg + a.SweepDeg * k / n) * Math.PI / 180.0;
                    b.Add(a.Cx + a.R * Math.Cos(t), a.Cy + a.R * Math.Sin(t));
                }
                break;
            }
            case PolygonPrimitive pg:
                foreach (var pt in pg.Points) if (pt.Length >= 2) b.Add(pt[0], pt[1]);
                break;
            case QuadCurvePrimitive q:
                for (int k = 0; k <= 32; k++)
                {
                    double t = k / 32.0, u = 1 - t;
                    b.Add(u * u * q.P0X + 2 * u * t * q.CtrlX + t * t * q.P2X,
                          u * u * q.P0Y + 2 * u * t * q.CtrlY + t * t * q.P2Y);
                }
                break;
            case CubicCurvePrimitive c:
                for (int k = 0; k <= 32; k++)
                {
                    double t = k / 32.0, u = 1 - t;
                    b.Add(u * u * u * c.P0X + 3 * u * u * t * c.C1X + 3 * u * t * t * c.C2X + t * t * t * c.P3X,
                          u * u * u * c.P0Y + 3 * u * u * t * c.C1Y + 3 * u * t * t * c.C2Y + t * t * t * c.P3Y);
                }
                break;
            case SinePrimitive s:
            {
                // The renderer's own sampling, so the box is the polyline that is drawn.
                int n = Math.Max((int)Math.Ceiling(s.Cycles * Math.Max(s.PtsPerCycle, 1)), 2);
                for (int k = 0; k <= n; k++)
                {
                    double t = (double)k / n;
                    double along = (t - 0.5) * s.Length, across = s.Amp * Math.Sin(2 * Math.PI * s.Cycles * t);
                    if (s.Axis == SineAxis.Horizontal) b.Add(s.Cx + along, s.Cy + across);
                    else                               b.Add(s.Cx + across, s.Cy + along);
                }
                break;
            }
            case ExponentialTaperPrimitive et:
            {
                double half = Math.Max(et.W1, et.W2) * 0.5;
                if (et.Axis == SineAxis.Horizontal) { b.Add(et.Cx - et.L * 0.5, et.Cy - half); b.Add(et.Cx + et.L * 0.5, et.Cy + half); }
                else                                { b.Add(et.Cx - half, et.Cy - et.L * 0.5); b.Add(et.Cx + half, et.Cy + et.L * 0.5); }
                break;
            }
        }
    }

    private sealed class Bounds
    {
        public double MinX = double.MaxValue, MinY = double.MaxValue, MaxX = double.MinValue, MaxY = double.MinValue;
        public bool Any => MinX <= MaxX;

        public void Add(double x, double y)
        {
            if (x < MinX) MinX = x;
            if (y < MinY) MinY = y;
            if (x > MaxX) MaxX = x;
            if (y > MaxY) MaxY = y;
        }

        public void Add(Bounds o)
        {
            if (!o.Any) return;
            Add(o.MinX, o.MinY);
            Add(o.MaxX, o.MaxY);
        }
    }
}
