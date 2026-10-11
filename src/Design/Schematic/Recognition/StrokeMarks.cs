// What is drawn on a schematic picture that is not wire — brief-imf-3-strokes-heavy-pens-dashes.md R-imf3-3 … R-imf3-6.
//
//   straight runs (WireGraph.Pieces) ─► arrows: a head at a shaft's end, classified ─► dashed lines, by role
//                                    ─► zig-zag chains ─► what to remove from the ink, what to add as wire
//
// Found before the wire graph is built, on the runs the final tolerances cut. An annotation — a dimension, current or
// voltage arrow, a dashed fixture box, a dashed line meeting no wire — has its ink removed, so it is neither wire nor
// symbol; a head on a wire (a current arrow on it, an arrow into a terminal) loses the head and keeps the wire through
// it. A dashed line in line with a wire is one wire, flagged Dashed. A zig-zag's legs are never wire, and never count
// towards the curved-lines refusal. Pure: everything is returned.

using Clipper2Lib;
using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>What an annotation stroke was read as (R-imf3-3, R-imf3-4).</summary>
public enum StrokeAnnotationKind
{
    /// <summary>A dimension: heads at both ends, or one head with its other end on a wire (a Vref arrow). Removed.</summary>
    DimensionArrow,

    /// <summary>A current arrow beside a wire, or on it with the head inline. Removed (an inline head alone).</summary>
    CurrentArrow,

    /// <summary>A voltage arrow: one head, no wire at either end. Removed; kept as evidence for a port pair.</summary>
    VoltageArrow,

    /// <summary>A wire ending in a head that points into a terminal: the head removed, the wire carried into it.</summary>
    TerminalArrow,

    /// <summary>A head at a wire's free end with a supply word beside it: a supply mark, left as drawn.</summary>
    SupplyArrow,

    /// <summary>A head on a shaft crossing a symbol at an angle — a variable part: left in its region.</summary>
    SymbolArrow,

    /// <summary>A dashed rectangle, or a dashed line meeting no wire in line: a fixture boundary. Removed.</summary>
    DashedBox,

    /// <summary>A dashed line in line with a wire: read as a wire (it abbreviates repeated sections).</summary>
    DashedWire,
}

/// <summary>One annotation stroke, in the read picture's pixels: <paramref name="Path"/> its line (an arrow's tail to its
/// tip, a dashed line's ends), <paramref name="Heads"/> its heads' apexes.</summary>
public sealed record StrokeAnnotation(StrokeAnnotationKind Kind, IReadOnlyList<PointD> Path, IReadOnlyList<PointD> Heads)
{
    /// <summary>Its ink — all of it, or a head on a wire — was removed before the wires were read.</summary>
    public bool Removed { get; init; }
}

/// <summary>The shape of a junction mark (R-imf3-6).</summary>
public enum JunctionShape { Round, Square, Diamond }

/// <summary>The annotation strokes of one reading and what they do to it.</summary>
internal sealed class StrokeMarks
{
    public List<StrokeAnnotation> Annotations { get; } = [];

    /// <summary>Ink to remove before the wires are read again; null when nothing is removed.</summary>
    public BinaryImage? Erase { get; private set; }

    /// <summary>Ink to read as line work again: the wire's own stripe through a head removed from it — a filled head
    /// was taken out as a fill, and the wire through it with it.</summary>
    public BinaryImage? Restore { get; private set; }

    /// <summary>The wires that stand for dashed lines (WirePiece.Dashed).</summary>
    public List<WirePiece> DashedWires { get; } = [];

    /// <summary>Zig-zag chains found, their legs set aside (R-imf3-5).</summary>
    public int ZigzagChains { get; private set; }

    /// <summary>Whether a run is a leg of a zig-zag chain or a dash a dashed wire replaces: never a wire.</summary>
    public bool Dropped(WirePiece p) => _dropped.Contains(p.Sources[0]);

    private readonly HashSet<int> _dropped = [];

    /// <summary>A head is no wider or longer than this many symbol scales (R-imf3-3); one sitting ON a wire — a current
    /// arrow inline, an arrow into a terminal — no longer than one: an amplifier drawn as a filled triangle between its
    /// leads is larger, and keeps its ink.</summary>
    internal const double MaxHead = 2.0, MaxInlineHead = 1.0;

    // ── Finding ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every arrow, dashed line and zig-zag chain among <paramref name="pieces"/> (indexed by their first
    /// source), on <paramref name="ink"/> — the line work with its fills, the words out.</summary>
    /// <param name="s">The symbol scale, read pixels.</param>
    /// <param name="h">The text height, read pixels.</param>
    /// <param name="kept">Words kept as symbol details: a glyph's strokes are no arrow.</param>
    public static StrokeMarks Find(BinaryImage ink, IReadOnlyList<WirePiece> pieces, double w, double s, double h,
                                   IReadOnlyList<TextRegion> words, IReadOnlyList<PixelBox> kept, SchematicImageOptions o)
    {
        var m = new StrokeMarks();
        double maxResidual = Math.Max(o.MaxResidual * w, 0.6);
        var erase = new BinaryImage(ink.Width, ink.Height);
        var restore = new BinaryImage(ink.Width, ink.Height);
        bool erased = false;
        m.Zigzags(pieces, w, s, maxResidual);
        erased |= m.Arrows(ink, pieces, w, s, h, words, kept, maxResidual, erase, restore);
        erased |= m.Dashes(ink, pieces, w, s, maxResidual, erase);
        if (erased) { m.Erase = erase; m.Restore = restore; }
        return m;
    }

    // ── Arrows (R-imf3-3) ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What lies past a head's apex: paper, a diode's bar, the shaft's wire carrying on, a wire running across
    /// the tip, anything else (a terminal).</summary>
    private enum Past { Paper, Bar, Line, Across, Blob }

    /// <param name="Base">Where the head widens, on the shaft's line.</param>
    /// <param name="Width">Its widest, across.</param>
    private readonly record struct Head(PointD Base, PointD Apex, double Width, Past Past)
    {
        public double Length => WireGraph.Dist(Base, Apex);
    }

    private bool Arrows(BinaryImage ink, IReadOnlyList<WirePiece> pieces, double w, double s, double h,
                        IReadOnlyList<TextRegion> words, IReadOnlyList<PixelBox> kept, double maxResidual, BinaryImage erase,
                        BinaryImage restore)
    {
        // A glyph's strokes — a word kept as a symbol detail, no longer than a text height or so — are no arrow.
        bool InWord(PointD pt) => kept.Any(b => b.Distance(pt.x, pt.y) <= w);
        bool any = false;
        var wires = pieces.Where(p => p.IsWire).ToList();
        foreach (var p in pieces)
        {
            if (p.RoundLoop || p.Side || p.Residual > maxResidual || p.Length < 2 * w || Dropped(p) || (p.Length <= 1.5 * h && InWord(p.A) && InWord(p.B))) continue;
            var ha = HeadAt(ink, p, false, w, s);
            var hb = HeadAt(ink, p, true, w, s);
            if (ha is null && hb is null) continue;
            var heads = new[] { ha, hb }.Where(x => x is not null).Select(x => x!.Value).ToList();
            // The other runs the arrow is not made of: a run lying in its heads is the head's own skeleton.
            bool Mine(WirePiece q) => q == p || heads.Any(hd => InHead(hd, q.A, w) && InHead(hd, q.B, w));
            var others = wires.Where(q => !Mine(q)).ToList();
            bool WireAt(PointD pt) => others.Any(q => WireGraph.DistToSegment(pt, q.A, q.B) <= 1.5 * w + 0.5);

            if (ha is not null && hb is not null)
            {
                if (p.Orientation == WireOrientation.Diagonal) continue;
                Add(StrokeAnnotationKind.DimensionArrow, ha.Value.Apex, hb.Value.Apex, heads, removed: true);
                Remove(ink, erase, restore, p, heads, others, w, wholeShaft: true);
                any = true;
                continue;
            }
            var head = (ha ?? hb)!.Value;
            var tail = ha is null ? p.A : p.B;
            bool tailFree = ha is null ? p.FreeA : p.FreeB;
            if (p.Orientation == WireOrientation.Diagonal)
            {
                if (head.Past != Past.Paper) continue;
                // A variable part's arrow crosses the symbol's ink; a transistor's emitter arrow crosses nothing.
                if (pieces.Any(q => q != p && !Mine(q) && Crosses(p, q, w))) Add(StrokeAnnotationKind.SymbolArrow, tail, head.Apex, heads);
                continue;
            }
            switch (head.Past)
            {
                case Past.Bar:
                    continue;   // a filled diode: the bar across its apex
                case Past.Across:
                    // Pointing into a wire's side: one end of a dimension (its shaft often broken by its own label).
                    Add(StrokeAnnotationKind.DimensionArrow, tail, head.Apex, heads, removed: true);
                    Remove(ink, erase, restore, p, heads, others, w, wholeShaft: true);
                    any = true;
                    continue;
                case Past.Line when head.Length <= MaxInlineHead * s:
                    Add(StrokeAnnotationKind.CurrentArrow, tail, head.Apex, heads, removed: true);
                    Remove(ink, erase, restore, p, heads, others, w, wholeShaft: false);
                    any = true;
                    continue;
                case Past.Blob when head.Length <= MaxInlineHead * s && (p.IsWire || WireAt(tail)):
                    Add(StrokeAnnotationKind.TerminalArrow, tail, head.Apex, heads, removed: true);
                    Remove(ink, erase, restore, p, heads, others, w, wholeShaft: false);
                    any = true;
                    continue;
                case Past.Paper:
                    break;
                default:
                    continue;
            }
            var (ux, uy) = Unit(tail, head.Apex);
            bool tailOnWire = WireAt(tail);
            if (tailOnWire)
            {
                if (WireAt(new PointD(head.Apex.x + ux * w, head.Apex.y + uy * w)))
                {
                    Add(StrokeAnnotationKind.DimensionArrow, tail, head.Apex, heads, removed: true);
                    Remove(ink, erase, restore, p, heads, others, w, wholeShaft: true);
                    any = true;
                }
                else if (words.Any(t => t.Box.Distance(head.Apex.x, head.Apex.y) <= 2 * h))
                    Add(StrokeAnnotationKind.SupplyArrow, tail, head.Apex, heads);
                continue;
            }
            // No wire at the tail. Stopping on a symbol — a lead's free end — it is a supply's, or nothing to read here.
            if (!tailFree)
            {
                if (words.Any(t => t.Box.Distance(head.Apex.x, head.Apex.y) <= 2 * h))
                    Add(StrokeAnnotationKind.SupplyArrow, tail, head.Apex, heads);
                continue;
            }
            // In paper: beside a wire, a current; spanning a gap, a voltage.
            bool beside = others.Any(q => Parallel(p, q, w, 2 * s));
            Add(beside ? StrokeAnnotationKind.CurrentArrow : StrokeAnnotationKind.VoltageArrow, tail, head.Apex, heads, removed: true);
            Remove(ink, erase, restore, p, heads, others, w, wholeShaft: true);
            any = true;
        }
        return any;

        void Add(StrokeAnnotationKind kind, PointD from, PointD to, List<Head> heads, bool removed = false) =>
            Annotations.Add(new StrokeAnnotation(kind, [from, to], [.. heads.Select(x => x.Apex)]) { Removed = removed });
    }

    /// <summary>The head at one end of <paramref name="p"/>, read off the ink's width across the shaft's line: a pen-wide
    /// shaft, then — within half a symbol scale of the run's end — an abrupt widening to at least 1.8 w, narrowing
    /// steadily to its apex, its widest within the first 40 % of it. A dot widens and narrows symmetrically, a plate
    /// or a ground bar is a pen deep, a diamond is widest in its middle: none is a head.</summary>
    private static Head? HeadAt(BinaryImage ink, WirePiece p, bool atB, double w, double s)
    {
        var (e, other) = atB ? (p.B, p.A) : (p.A, p.B);
        var (ux, uy) = Unit(other, e);
        double nx = -uy, ny = ux, len = p.Length;
        double maxAcross = 2.5 * s;
        double Across(double t)
        {
            double cx = e.x + ux * t, cy = e.y + uy * t;
            if (!Ink(ink, cx, cy)) return 0;
            double l = 0, r = 0;
            while (l < maxAcross && Ink(ink, cx + nx * (l + 0.5), cy + ny * (l + 0.5))) l += 0.5;
            while (r < maxAcross && Ink(ink, cx - nx * (r + 0.5), cy - ny * (r + 0.5))) r += 0.5;
            return l + r + 0.5;
        }
        const double step = 0.5;
        double t0 = -Math.Min(0.6 * len, s), t1 = MaxHead * s + 2 * w;
        int n = (int)((t1 - t0) / step) + 1;
        var width = new double[n];
        for (int k = 0; k < n; k++) width[k] = Across(t0 + k * step);
        double T(int k) => t0 + k * step;

        // The shaft, before anything: a pen wide.
        int shaftEnd = Math.Min(n - 1, (int)(Math.Min(1.5 * w, 0.3 * len) / step));
        var shaft = width.Take(shaftEnd + 1).Where(x => x > 0).OrderBy(x => x).ToList();
        if (shaft.Count < 2) return null;
        double pen = shaft[shaft.Count / 2];
        if (pen > 1.6 * w) return null;
        double wide = Math.Max(1.8 * w, pen + 0.8 * w);

        // Where it widens: within half a symbol scale either side of the run's end.
        int b = -1;
        for (int k = shaftEnd; k < n; k++)
        {
            if (width[k] == 0) return null;          // the stroke stopped first
            if (width[k] >= wide) { b = k; break; }
            if (T(k) > 0.5 * s) return null;
        }
        if (b < 0) return null;
        // The base: back to where the widening starts.
        while (b > shaftEnd && width[b - 1] > pen + 0.5 * w) b--;

        // To the apex: paper, or the narrowest point before the ink widens again — a wire across the tip, a terminal,
        // the shaft carrying on. The widest is looked for only while the head is still widening.
        int top = b, apex = -1, lowest = b;
        double max = width[b];
        bool narrowing = false;
        var past = Past.Paper;
        for (int k = b + 1; k < n; k++)
        {
            if (width[k] == 0) { apex = k - 1; break; }
            if (!narrowing)
            {
                if (width[k] > max) { max = width[k]; top = k; }
                else if (width[k] < max - Math.Max(1.0, 0.5 * w)) { narrowing = true; lowest = k; }
                continue;
            }
            if (width[k] < width[lowest]) lowest = k;
            // Down to a pen and carrying on a pen wide for 2 w: the wire the head sits on.
            if (width[k] <= 1.3 * pen + 0.5)
            {
                int run = 0, need = (int)(2 * w / step);
                for (int j = k; j < n && run < need && width[j] > 0 && width[j] <= 1.3 * pen + 0.5; j++) run++;
                if (run >= need) { apex = k; past = Past.Line; break; }
            }
            if (width[k] >= width[lowest] + Math.Max(w, 0.25 * max))
            {
                apex = lowest;
                past = PastOf(width, lowest, max, pen, w, step);
                break;
            }
        }
        if (apex < 0) return null;
        double length = (apex - b) * step;
        if (length < 2 * w || length > MaxHead * s || max > MaxHead * s || max < Math.Max(wide, 2.2 * w)) return null;
        if (max / length < 0.3 || max / length > 1.8) return null;
        if ((top - b) * step > 0.4 * length) return null;
        double mid = width[b + (apex - b) / 2];
        if (mid < 0.25 * max || mid > 0.85 * max) return null;
        // Straight sides: from its widest to its apex, within a third of the width of a straight taper.
        for (int q = 1; q <= 3; q++)
        {
            int k = top + (apex - top) * q / 4;
            double expected = max * (apex - k) / Math.Max(1, apex - top);
            if (Math.Abs(width[k] - expected) > 0.3 * max + 1) return null;
        }
        // Narrowing steadily: no rise of more than a pen on the way down.
        for (int k = top + 1, least = top; k <= apex; k++)
        {
            if (width[k] < width[least]) least = k;
            if (width[k] > width[least] + Math.Max(1.0, 0.5 * w)) return null;
        }
        var bp = new PointD(e.x + ux * T(b), e.y + uy * T(b));
        var ap = new PointD(e.x + ux * T(apex), e.y + uy * T(apex));
        // Solid: the triangle is ink, a pixel in from its sides — two strokes meeting at an angle (a glyph's corner)
        // leave paper between them.
        int inside = 0, inked = 0;
        for (int i = 1; i < 8; i++)
            for (int j = -3; j <= 3; j++)
            {
                double f = i / 8.0, half = Math.Max(0, max / 2 * (1 - f) - 1.0) * j / 3.0;
                double cx = bp.x + (ap.x - bp.x) * f + nx * half, cy = bp.y + (ap.y - bp.y) * f + ny * half;
                inside++;
                if (Ink(ink, cx, cy)) inked++;
            }
        if (inked < 0.9 * inside) return null;
        return new Head(bp, ap, max, past);
    }

    /// <summary>What follows a head's apex at index <paramref name="lo"/>: a bar across it (a diode's) — as wide as half
    /// the head again within half a pen, narrow again within 1.6 w — a line carrying on a pen wide for 2 w (a wire), or
    /// anything else (a terminal's dot or circle).</summary>
    private static Past PastOf(double[] width, int lo, double max, double pen, double w, double step)
    {
        int reach = (int)(0.6 * w / step) + 1, thin = (int)(1.6 * w / step) + 1, line = (int)(2 * w / step);
        int k = lo + 1;
        // A wire across the tip runs on far past the head's own width; a diode's bar is as long as its base. A blunt
        // tip may stop a pen or so short of it.
        int across = (int)(1.5 * w / step) + 1;
        for (int j = k; j < width.Length && j <= lo + across && width[j] > 0; j++)
            if (width[j] >= 2 * max) return Past.Across;
        int wideAt = -1;
        for (int j = k; j < width.Length && j <= lo + reach; j++)
            if (width[j] >= 0.5 * max) { wideAt = j; break; }
        if (wideAt >= 0)
        {
            for (int j = wideAt; j < width.Length && j <= lo + thin + reach; j++)
                if (width[j] <= 1.6 * pen) return Past.Bar;
            return Past.Blob;
        }
        int run = 0;
        for (int j = k; j < width.Length && width[j] > 0 && width[j] <= Math.Max(1.6 * pen, pen + 1); j++) run++;
        return run >= line ? Past.Line : Past.Blob;
    }

    /// <summary>Removes the shaft (when <paramref name="wholeShaft"/>) and the heads of an arrow from the ink — a head on a
    /// wire keeps the wire's own stripe through it — sparing every other wire.</summary>
    private static void Remove(BinaryImage ink, BinaryImage erase, BinaryImage restore, WirePiece p, List<Head> heads,
                               List<WirePiece> others, double w, bool wholeShaft)
    {
        double rad = Math.Max(w, p.Width) / 2 + 1.5;
        bool Spared(double x, double y) =>
            others.Any(q => WireGraph.DistToSegment(new PointD(x, y), q.A, q.B) <= Math.Max(w, q.Width) / 2 + 1);
        if (wholeShaft) Stripe(ink, erase, p.A, p.B, rad, rad, Spared);
        foreach (var hd in heads)
        {
            var (ux, uy) = Unit(hd.Base, hd.Apex);
            double half = hd.Width / 2 + 1.5, len = hd.Length;
            double stripe = wholeShaft ? -1 : Math.Max(w, p.Width) / 2 + 0.5;
            Box(ink, hd.Base, hd.Apex, half + len, (x, y) =>
            {
                double dx = x - hd.Base.x, dy = y - hd.Base.y;
                double along = dx * ux + dy * uy, across = Math.Abs(-dx * uy + dy * ux);
                if (along < -1.5 || along > len + 1.5) return false;
                // Inside the triangle, grown by a pixel and a half.
                double halfAt = half * Math.Clamp(1 - along / Math.Max(len, 1e-9), 0, 1) + 1.5;
                if (across > halfAt) return false;
                if (across <= stripe)
                {
                    restore.Pixels[(int)y * ink.Width + (int)x] = 1;
                    return false;
                }
                return !Spared(x, y);
            }, erase);
        }
    }

    /// <summary>Marks every ink pixel within <paramref name="rad"/> of segment a–b (and <paramref name="cap"/> past its
    /// ends) that is not spared.</summary>
    private static void Stripe(BinaryImage ink, BinaryImage erase, PointD a, PointD b, double rad, double cap, Func<double, double, bool> spared)
    {
        double len = WireGraph.Dist(a, b);
        Box(ink, a, b, rad + cap, (x, y) =>
        {
            var (along, perp) = WireGraph.Local(a, b, new PointD(x, y));
            return along >= -cap && along <= len + cap && Math.Abs(perp) <= rad && !spared(x, y);
        }, erase);
    }

    private static void Box(BinaryImage ink, PointD a, PointD b, double pad, Func<double, double, bool> take, BinaryImage erase)
    {
        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.x, b.x) - pad)), x1 = Math.Min(ink.Width - 1, (int)Math.Ceiling(Math.Max(a.x, b.x) + pad));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.y, b.y) - pad)), y1 = Math.Min(ink.Height - 1, (int)Math.Ceiling(Math.Max(a.y, b.y) + pad));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (ink.Pixels[y * ink.Width + x] != 0 && take(x + 0.5, y + 0.5)) erase.Pixels[y * ink.Width + x] = 1;
    }

    private static bool InHead(Head hd, PointD p, double w)
    {
        var (along, perp) = WireGraph.Local(hd.Base, hd.Apex, p);
        return along >= -w && along <= hd.Length + w && Math.Abs(perp) <= hd.Width / 2 + w;
    }

    /// <summary>Two runs side by side: within 10°, <paramref name="lo"/> … <paramref name="hi"/> apart, overlapping along
    /// at least half the shorter.</summary>
    private static bool Parallel(WirePiece p, WirePiece q, double lo, double hi)
    {
        double lp = p.Length, lq = q.Length;
        if (lp < 1e-9 || lq < 1e-9) return false;
        double cos = Math.Abs(((p.B.x - p.A.x) * (q.B.x - q.A.x) + (p.B.y - p.A.y) * (q.B.y - q.A.y)) / (lp * lq));
        if (cos < Math.Cos(10 * Math.PI / 180)) return false;
        var (a0, d0) = WireGraph.Local(p.A, p.B, q.A);
        var (a1, d1) = WireGraph.Local(p.A, p.B, q.B);
        double across = Math.Abs((d0 + d1) / 2);
        if (across < lo || across > hi) return false;
        double overlap = Math.Min(lp, Math.Max(a0, a1)) - Math.Max(0, Math.Min(a0, a1));
        return overlap >= 0.5 * Math.Min(lp, lq);
    }

    /// <summary>Whether run <paramref name="q"/> crosses <paramref name="p"/>'s interior, away from its ends.</summary>
    private static bool Crosses(WirePiece p, WirePiece q, double w)
    {
        double len = p.Length;
        var (a0, d0) = WireGraph.Local(p.A, p.B, q.A);
        var (a1, d1) = WireGraph.Local(p.A, p.B, q.B);
        if (Math.Sign(d0) == Math.Sign(d1) && Math.Min(Math.Abs(d0), Math.Abs(d1)) > w) return false;
        double t = Math.Abs(d0 - d1) < 1e-9 ? a0 : a0 + (a1 - a0) * d0 / (d0 - d1);
        return t > 2 * w && t < len - 2 * w;
    }

    // ── Dashed lines (R-imf3-4) ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Dash(WirePiece P, double Across, double From, double To)
    {
        public double Length => To - From;
    }

    private sealed record DashedLine(bool Horizontal, double Across, List<Dash> Dashes)
    {
        public double From => Dashes[0].From;
        public double To => Dashes[^1].To;
        public double Gap => Dashes.Count < 2 ? 0 : Dashes.Zip(Dashes.Skip(1), (x, y) => y.From - x.To).Max();
        public double Dash => Dashes.Max(d => d.Length);

        public PointD At(double along) => Horizontal ? new PointD(along, Across) : new PointD(Across, along);
    }

    private bool Dashes(BinaryImage ink, IReadOnlyList<WirePiece> pieces, double w, double s, double maxResidual, BinaryImage erase)
    {
        var wires = pieces.Where(p => p.IsWire && !Dropped(p)).ToList();
        // A dash stops in paper at both ends — or on a wire crossing it: a coil's humps, cut into runs, line up as
        // evenly, but each run carries on into the next.
        bool Stops(PointD end, bool free, WireOrientation along) =>
            free || wires.Any(q => q.Orientation != along && q.Orientation != WireOrientation.Diagonal
                                   && WireGraph.DistToSegment(end, q.A, q.B) <= 1.5 * w);
        var dashes = pieces
            .Where(p => !p.RoundLoop && !p.Side && !Dropped(p) && p.Orientation != WireOrientation.Diagonal
                        && p.Residual <= maxResidual && p.Length >= 0.75 * w && p.Length <= 3 * s
                        && Stops(p.A, p.FreeA, p.Orientation) && Stops(p.B, p.FreeB, p.Orientation))
            .Select(p => p.Orientation == WireOrientation.Horizontal
                ? new Dash(p, (p.A.y + p.B.y) / 2, Math.Min(p.A.x, p.B.x), Math.Max(p.A.x, p.B.x))
                : new Dash(p, (p.A.x + p.B.x) / 2, Math.Min(p.A.y, p.B.y), Math.Max(p.A.y, p.B.y)))
            .ToList();
        var lines = new List<DashedLine>();
        foreach (bool horizontal in new[] { true, false })
        {
            var axis = dashes.Where(d => (d.P.Orientation == WireOrientation.Horizontal) == horizontal)
                             .OrderBy(d => d.Across).ThenBy(d => d.From).ToList();
            // Rows: dashes on one line, within half a pen across.
            var rows = new List<List<Dash>>();
            foreach (var d in axis)
            {
                var row = rows.FirstOrDefault(r => Math.Abs(r[0].Across - d.Across) <= Math.Max(1.0, 0.5 * w));
                if (row is null) rows.Add([d]); else row.Add(d);
            }
            foreach (var row in rows)
            {
                row.Sort((x, y) => x.From.CompareTo(y.From));
                var run = new List<Dash> { row[0] };
                double lastGap = -1;
                for (int k = 1; k <= row.Count; k++)
                {
                    bool joins = false;
                    if (k < row.Count)
                    {
                        var (prev, d) = (run[^1], row[k]);
                        double gap = d.From - prev.To;
                        joins = gap >= 0.5 * w && gap <= 3 * Math.Max(prev.Length, d.Length) && gap <= 1.5 * s
                                && Similar(prev.Length, d.Length) && (lastGap < 0 || Similar(lastGap, gap))
                                && Clear(ink, horizontal, d.Across, prev.To + w + 1, d.From - w - 1, wires, w);
                        if (joins) lastGap = gap;
                    }
                    if (joins) { run.Add(row[k]); continue; }
                    if (run.Count >= 3) lines.Add(new DashedLine(horizontal, run.Average(x => x.Across), run));
                    if (k < row.Count) { run = [row[k]]; lastGap = -1; }
                }
            }
        }
        if (lines.Count == 0) return false;

        // Rectangles: dashed lines meeting at corners.
        var group = Enumerable.Range(0, lines.Count).ToArray();
        int Find(int x) { while (group[x] != x) x = group[x] = group[group[x]]; return x; }
        for (int i = 0; i < lines.Count; i++)
            for (int j = i + 1; j < lines.Count; j++)
            {
                if (lines[i].Horizontal == lines[j].Horizontal) continue;
                // A corner's own dashes bend round it and drop out of both lines: up to a dash and a gap short of it.
                double reach = Math.Max(lines[i].Gap, lines[j].Gap) + Math.Max(lines[i].Dash, lines[j].Dash) + 2 * w;
                bool corner = new[] { lines[i].At(lines[i].From), lines[i].At(lines[i].To) }
                    .Any(p => new[] { lines[j].At(lines[j].From), lines[j].At(lines[j].To) }.Any(q => WireGraph.Dist(p, q) <= reach * 1.5));
                if (corner) group[Find(i)] = Find(j);
            }
        var size = Enumerable.Range(0, lines.Count).GroupBy(Find).ToDictionary(g => g.Key, g => g.Count());

        bool any = false;
        var dashPieces = lines.SelectMany(l => l.Dashes.Select(d => d.P)).ToHashSet();
        var others = wires.Where(q => !dashPieces.Contains(q)).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            double reach = 1.5 * line.Gap + 2 * w;
            // A wire in line: collinear, its end just beyond one of the dashed line's ends.
            PointD? WireEnd(bool atFrom)
            {
                double edge = atFrom ? line.From : line.To;
                PointD? best = null;
                double bd = reach;
                foreach (var q in others)
                {
                    if ((q.Orientation == WireOrientation.Horizontal) != line.Horizontal || q.Orientation == WireOrientation.Diagonal) continue;
                    if (q.Length <= 1.5 * line.Dash) continue;   // a dash of its own, bent round a corner or cut by a crossing
                    foreach (var end in new[] { q.A, q.B })
                    {
                        double across = line.Horizontal ? end.y : end.x, along = line.Horizontal ? end.x : end.y;
                        if (Math.Abs(across - line.Across) > w) continue;
                        double d = atFrom ? edge - along : along - edge;
                        if (d < -0.5 * w || d > bd) continue;
                        bd = d;
                        best = line.At(along);
                    }
                }
                return best;
            }
            var from = size[Find(i)] >= 2 ? null : WireEnd(true);
            var to = size[Find(i)] >= 2 ? null : WireEnd(false);
            if (from is null && to is null)
            {
                Annotations.Add(new StrokeAnnotation(StrokeAnnotationKind.DashedBox, [line.At(line.From), line.At(line.To)], []) { Removed = true });
                // The line and a dash and a gap past each end: a corner's own dash, bent round it, belongs to no line
                // and is no wire either. Every other wire is spared.
                double rad = Math.Max(w, line.Dashes.Max(d => d.P.Width)) / 2 + 1.5, ext = line.Gap + line.Dash;
                var (e0, e1) = (line.At(line.From - ext), line.At(line.To + ext));
                var spare = others.Where(q => WireGraph.DistToSegment(q.A, e0, e1) > rad + 1 || WireGraph.DistToSegment(q.B, e0, e1) > rad + 1).ToList();
                Stripe(ink, erase, e0, e1, rad, 0.5 * w + 1.5,
                       (x, y) => spare.Any(q => WireGraph.DistToSegment(new PointD(x, y), q.A, q.B) <= Math.Max(w, q.Width) / 2 + 1));
                any = true;
                continue;
            }
            var a = from ?? line.At(line.From);
            var b = to ?? line.At(line.To);
            var wire = new WirePiece
            {
                A = a, B = b, Orientation = line.Horizontal ? WireOrientation.Horizontal : WireOrientation.Vertical,
                Width = line.Dashes.Average(d => d.P.Width), Residual = line.Dashes.Max(d => d.P.Residual),
                Angle = line.Horizontal ? 0 : Math.PI / 2, IsWire = true, Dashed = true,
            };
            wire.Sources.AddRange(line.Dashes.Select(d => d.P.Sources[0]));
            DashedWires.Add(wire);
            foreach (var d in line.Dashes) _dropped.Add(d.P.Sources[0]);
            Annotations.Add(new StrokeAnnotation(StrokeAnnotationKind.DashedWire, [a, b], []));
        }
        return any;
    }

    private static bool Similar(double a, double b) => Math.Max(a, b) <= 1.5 * Math.Min(a, b) + 1;

    /// <summary>The line between two dashes is paper — but for a wire crossing it.</summary>
    private static bool Clear(BinaryImage ink, bool horizontal, double across, double from, double to, List<WirePiece> wires, double w)
    {
        for (double t = from; t <= to; t += 1)
        {
            double x = horizontal ? t : across, y = horizontal ? across : t;
            if (!Ink(ink, x, y)) continue;
            var p = new PointD(x, y);
            bool crossing = wires.Any(q => (q.Orientation == WireOrientation.Horizontal) != horizontal
                                           && WireGraph.DistToSegment(p, q.A, q.B) <= Math.Max(w, q.Width) / 2 + 1);
            if (!crossing) return false;
        }
        return true;
    }

    // ── Zig-zag chains (R-imf3-5) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Three or more slanted runs alternating in direction — each a mirror of the next about the chain's
    /// axis, turning at least 40° at the peak between them, all at one slope either way (a coil's arcs, cut into runs,
    /// lean every way), end to end within 3 w, crossing the line of the two collinear orthogonal leads they lie between:
    /// a resistor, whatever its legs measure.
    /// Its legs are never wire.</summary>
    private void Zigzags(IReadOnlyList<WirePiece> pieces, double w, double s, double maxResidual)
    {
        var legs = pieces.Where(p => p.Orientation == WireOrientation.Diagonal && !p.RoundLoop && p.Residual <= 2 * maxResidual
                                     && p.Length >= 1.5 * w && p.Length <= 1.5 * s).ToList();
        if (legs.Count < 3) return;
        double reach = 3 * w;
        bool Near(WirePiece p, WirePiece q) =>
            WireGraph.Dist(p.A, q.A) <= reach || WireGraph.Dist(p.A, q.B) <= reach || WireGraph.Dist(p.B, q.A) <= reach || WireGraph.Dist(p.B, q.B) <= reach;
        double Fold(WirePiece p) => Math.Min(p.Angle, Math.PI - p.Angle);
        bool Mirror(WirePiece p, WirePiece q) =>
            Math.Abs(Fold(p) - Fold(q)) <= 15 * Math.PI / 180 && Math.Abs(p.Angle - q.Angle) >= 40 * Math.PI / 180;
        // A leg cut in two by the skeleton: the same slope, one piece carrying on from the other.
        bool Continues(WirePiece p, WirePiece q) =>
            Math.Abs(p.Angle - q.Angle) <= 12 * Math.PI / 180
            && Math.Min(Math.Min(WireGraph.Dist(p.A, q.A), WireGraph.Dist(p.A, q.B)), Math.Min(WireGraph.Dist(p.B, q.A), WireGraph.Dist(p.B, q.B))) <= 1.5 * w;
        var parent = Enumerable.Range(0, legs.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < legs.Count; i++)
            for (int j = i + 1; j < legs.Count; j++)
                if (Near(legs[i], legs[j]) && (Mirror(legs[i], legs[j]) || Continues(legs[i], legs[j]))) parent[Find(i)] = Find(j);
        var leads = pieces.Where(p => p.Orientation != WireOrientation.Diagonal && p.Length >= 1.5 * w).ToList();
        foreach (var chain in Enumerable.Range(0, legs.Count).GroupBy(Find).Where(g => g.Count() >= 3))
        {
            var ends = chain.SelectMany(k => new[] { legs[k].A, legs[k].B }).ToList();
            // The chain's two extreme ends.
            var (e0, e1, far) = (ends[0], ends[0], -1.0);
            for (int i = 0; i < ends.Count; i++)
                for (int j = i + 1; j < ends.Count; j++)
                    if (WireGraph.Dist(ends[i], ends[j]) > far) (e0, e1, far) = (ends[i], ends[j], WireGraph.Dist(ends[i], ends[j]));
            WirePiece? LeadAt(PointD e) => leads.FirstOrDefault(q => WireGraph.Dist(q.A, e) <= reach || WireGraph.Dist(q.B, e) <= reach);
            if (LeadAt(e0) is not { } l0 || LeadAt(e1) is not { } l1 || l0 == l1 || l0.Orientation != l1.Orientation) continue;
            bool horizontal = l0.Orientation == WireOrientation.Horizontal;
            double off = horizontal ? Math.Abs(l0.A.y - l1.A.y) : Math.Abs(l0.A.x - l1.A.x);
            if (off > 2 * w) continue;
            double ax = Math.Abs(e1.x - e0.x), ay = Math.Abs(e1.y - e0.y);
            if (horizontal ? ay > 0.27 * ax : ax > 0.27 * ay) continue;   // the chain runs along its leads (within 15°)
            // Two slopes only, taken in turn: a coil's arcs, cut into runs, lean every way between.
            var folded = chain.Select(k => Math.Min(legs[k].Angle, Math.PI - legs[k].Angle)).ToList();
            if (folded.Max() - folded.Min() > 20 * Math.PI / 180) continue;
            // Its legs cross the leads' line, peak to peak: a triangle's sides only meet it at the apex.
            double axisAt = horizontal ? (l0.A.y + l1.A.y) / 2 : (l0.A.x + l1.A.x) / 2;
            double Off(PointD q) => (horizontal ? q.y : q.x) - axisAt;
            if (!chain.Any(k => Off(legs[k].A) * Off(legs[k].B) < 0 && Math.Min(Math.Abs(Off(legs[k].A)), Math.Abs(Off(legs[k].B))) >= w)) continue;
            double rising = chain.Where(k => legs[k].Angle < Math.PI / 2).Sum(k => legs[k].Length);
            double all = chain.Sum(k => legs[k].Length);
            if (rising < 0.25 * all || rising > 0.75 * all) continue;
            ZigzagChains++;
            foreach (int k in chain) _dropped.Add(legs[k].Sources[0]);
        }
    }

    // ── Junction marks (R-imf3-6) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The shape of the filled mark at <paramref name="centre"/>, <paramref name="radius"/> its inscribed
    /// radius: read along rays from its centre. Along the diagonals — where no orthogonal wire leaves — a square
    /// reaches √2 times its inscribed radius and a disc or a diamond once; along an axis no wire leaves by, a diamond
    /// reaches √2 times and a disc or a square once. A four-way crossing's diamond has no free axis and reads round.</summary>
    public static JunctionShape ShapeAt(BinaryImage ink, PointD centre, double radius)
    {
        // Too few pixels across to tell a corner from a curve.
        if (radius < 4) return JunctionShape.Round;
        double cap = 3 * radius;
        double Reach(double dx, double dy)
        {
            double t = 0;
            while (t < cap && Ink(ink, centre.x + dx * (t + 0.5), centre.y + dy * (t + 0.5))) t += 0.5;
            return t + 0.5;
        }
        double d = Math.Sqrt(0.5);
        double diag = new[] { Reach(d, d), Reach(d, -d), Reach(-d, d), Reach(-d, -d) }.Average();
        var free = new[] { Reach(1, 0), Reach(-1, 0), Reach(0, 1), Reach(0, -1) }.Where(t => t < 2.2 * radius).ToList();
        if (free.Count == 0) return diag >= 1.3 * radius ? JunctionShape.Square : JunctionShape.Round;
        double axis = free.Average();
        return diag >= 1.25 * axis ? JunctionShape.Square : axis >= 1.25 * diag ? JunctionShape.Diamond : JunctionShape.Round;
    }

    // ── Geometry ────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool Ink(BinaryImage ink, double x, double y) => ink.At((int)Math.Floor(x), (int)Math.Floor(y));

    private static (double X, double Y) Unit(PointD from, PointD to)
    {
        double dx = to.x - from.x, dy = to.y - from.y, l = Math.Sqrt(dx * dx + dy * dy);
        return l < 1e-9 ? (1, 0) : (dx / l, dy / l);
    }
}
