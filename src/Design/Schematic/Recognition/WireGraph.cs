// The wires of a schematic picture as a graph — brief-img-7-schematic-wires-and-regions.md R-im7-4, R-im7-5 (D11).
//
//   skeleton (IM-1) ─► straight runs ─► wire candidates ─► symbol strokes set aside ─► nodes, crossings, dots, nets
//
// A run is a WIRE CANDIDATE when it is straight (RMS residual under MaxResidual w) and either orthogonal within the
// snap angle and longer than MinWireLength w, or diagonal and longer than MinDiagonalLength w.
//
// Two shapes of candidate are strokes of symbols, not wires, and are set aside before the graph is made — without
// them a capacitor's plates, a ground's bars and a resistor's box would all come back as wire:
//  * a straight line no longer than MaxSymbolStroke w whose BOTH ends stop in nothing (the skeleton ends there): a
//    plate, a ground or supply bar. A wire ends on a symbol, a junction or — at one end — a net label; one with two
//    bare ends that short is a stroke;
//  * every candidate on a CYCLE of candidates (a box, a triangle): a loop made only of wire is two wires shorting the
//    same two nodes, which nobody draws, while a symbol's outline is exactly that. At a four-way crossing the cycle is
//    looked for with the crossing's two straight pairs kept apart, so two wires crossing twice are not a loop.
// Once the symbol scale s is known (brief-imf-3, R-imf3-1, R-imf3-2) the first rule becomes the picture's: a line
// with two bare ends that ONE lead meets in its middle is a plate at any length up to 4 s (a heavy pen's plate is
// 19 w); a line meeting no wire is a stroke up to MaxSymbolStroke w or MaxSymbolStrokeScales s; two lines side by side
// across a clear strip, 1 w to 1.5 s apart, are a plate pair; a gently curved chain with a lead is a curved plate. A
// heavy box's sides too short to be wire close its cycle all the same (WirePiece.Side).
//
// Nodes are the candidates' ends clustered within 1.5 w (or within a junction dot), with an end that lands on another
// wire's interior splitting it (a T). A node with two collinear wires is dissolved — one wire. A degree-3 node always
// connects; a degree-4 node connects per the CrossingRule; a hop (found among the symbol regions, SymbolRegions.cs)
// joins its two halves across the other wire and never connects to it.

using Clipper2Lib;
using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Schematic.Recognition;

public enum WireOrientation { Horizontal, Vertical, Diagonal }

/// <summary>A node of the wire graph, in picture pixels.</summary>
/// <param name="Degree">How many wire segments end on it.</param>
/// <param name="Dot">A junction dot sits on it (degree 3 or more only).</param>
/// <param name="Free">Every wire end on it stops in bare paper — the skeleton ends there.</param>
public sealed record WireNode(int Id, double X, double Y, int Degree, bool Dot, bool Free)
{
    public bool IsEnd => Degree == 1;

    /// <summary>A filled dot sits on this free end: a datasheet's terminal (R-imf6-1).</summary>
    public bool Terminal { get; init; }
}

/// <summary>One straight wire segment, from node <paramref name="From"/> at <paramref name="A"/> to node
/// <paramref name="To"/> at <paramref name="B"/>.</summary>
/// <param name="Width">Its mean stroke width, pixels.</param>
/// <param name="Residual">The RMS distance of its skeleton pixels from it, pixels.</param>
public sealed record WireSegment(int Id, int From, int To, PointD A, PointD B, WireOrientation Orientation,
    double Width, double Residual)
{
    public double Length => Math.Sqrt((B.x - A.x) * (B.x - A.x) + (B.y - A.y) * (B.y - A.y));

    /// <summary>Drawn as a dashed line in line with a wire (R-imf3-4): it abbreviates a connection — repeated sections
    /// left out — and is read as an ordinary wire.</summary>
    public bool Dashed { get; init; }
}

public enum CrossingReading
{
    /// <summary>The four wires are one net.</summary>
    Connected,

    /// <summary>Each straight pair is its own net.</summary>
    NotConnected,

    /// <summary>One wire hops over the other: never connected, whatever the crossing rule.</summary>
    Hop,
}

/// <summary>A four-way crossing or a hop, and how it was read — what IM-10's overlay draws and lets the user flip.</summary>
/// <param name="Node">The degree-4 node; null for a hop, which has none.</param>
/// <param name="Segments">The wires crossing there.</param>
public sealed record WireCrossing(int Id, double X, double Y, int? Node, bool Dot, CrossingReading Reading,
    IReadOnlyList<int> Segments)
{
    public bool Connected => Reading == CrossingReading.Connected;

    /// <summary>The reading is the user's correction (R-im10-8), not the crossing rule's.</summary>
    public bool Overridden { get; init; }
}

/// <summary>The wires read off a schematic picture (R-im7-4, R-im7-5).</summary>
public sealed class WireGraph
{
    private readonly int[] _net;

    private WireGraph(List<WireNode> nodes, List<WireSegment> segments, List<WireCrossing> crossings, int[] net, int nets)
    {
        Nodes = nodes;
        Segments = segments;
        Crossings = crossings;
        _net = net;
        NetCount = nets;
    }

    public static WireGraph Empty { get; } = new([], [], [], [], 0);

    /// <summary>Top to bottom, then left to right.</summary>
    public IReadOnlyList<WireNode> Nodes { get; }

    public IReadOnlyList<WireSegment> Segments { get; }

    /// <summary>Every degree-4 node and every hop, with its reading.</summary>
    public IReadOnlyList<WireCrossing> Crossings { get; }

    /// <summary>How many separate nets the wires make.</summary>
    public int NetCount { get; }

    /// <summary>The net of segment <paramref name="segment"/>, 0 … <see cref="NetCount"/> − 1.</summary>
    public int Net(int segment) => _net[segment];

    public bool Connected(int a, int b) => _net[a] == _net[b];

    /// <summary>The nodes one wire ends on.</summary>
    public IEnumerable<WireNode> Ends => Nodes.Where(n => n.IsEnd);

    /// <summary>The segment ending on end node <paramref name="node"/>.</summary>
    public WireSegment SegmentAt(int node) => Segments.First(s => s.From == node || s.To == node);

    /// <summary>The unit direction a wire arrives at end node <paramref name="node"/> in — from its other end towards it.</summary>
    public (double X, double Y) Incoming(int node)
    {
        var s = SegmentAt(node);
        var (p, q) = s.To == node ? (s.A, s.B) : (s.B, s.A);
        double dx = q.x - p.x, dy = q.y - p.y, l = Math.Sqrt(dx * dx + dy * dy);
        return l < 1e-9 ? (0, 0) : (dx / l, dy / l);
    }

    // ── Runs and candidates ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every straight run of the skeleton, classified (R-im7-4).</summary>
    internal static List<WirePiece> Pieces(SkeletonGraph g, double w, SchematicImageOptions o)
    {
        double turn = Math.Max(o.SnapAngleDeg, 8.0) * Math.PI / 180;
        double snap = o.SnapAngleDeg * Math.PI / 180;
        // Never below 0.6 px: one pixel step in a run is an RMS of 0.5 px, and a wire a fraction of a degree off the
        // axis has one.
        double maxResidual = Math.Max(o.MaxResidual * w, 0.6);
        var r = new List<WirePiece>();
        foreach (var e in g.Edges)
        {
            // Cut where the chain leaves a straight line by more than a pixel — not at the skeleton's own 0.5 px
            // polyline, which keeps every step of a staircase: a wire leaning a fraction of a degree would come back in
            // level pieces joined by diagonal steps.
            var chain = e.Pixels.Select(q => new PointD(q.X + 0.5, q.Y + 0.5)).ToList();
            if (chain.Count < 2) continue;
            var idx = Polylines.SimplifyOpen(chain, 1.0);
            var poly = idx.Select(k => chain[k]).ToList();
            // A small ROUND loop — a terminal's circle, closed on the one node its wire meets — is a symbol: a flat run
            // of its pixels (a circle's bottom, rounded onto the grid) is never a wire. Kept to round loops no longer
            // than a 12 w circle, so a box's straight sides still make the candidate cycle that sets it aside.
            bool roundLoop = e.From == e.To && chain.Count <= Math.PI * 2 * o.MaxDot * w && poly.Count >= 6;
            int start = 0;
            for (int v = 1; v < poly.Count; v++)
            {
                bool last = v == poly.Count - 1;
                if (!last && Turn(poly[v - 1], poly[v], poly[v + 1]) <= turn) continue;
                var pts = new List<PointD>();
                for (int k = idx[start]; k <= idx[v]; k++) pts.Add(new PointD(e.Pixels[k].X + 0.5, e.Pixels[k].Y + 0.5));
                int nodeA = start == 0 ? e.From : -1, nodeB = last ? e.To : -1;
                start = v;
                if (pts.Count < 2) continue;

                var fit = Fit.Segment(pts);
                PointD a = fit.A, b = fit.B;
                if (Dist(a, pts[0]) > Dist(b, pts[0])) (a, b) = (b, a);
                if (nodeA >= 0) a = Project(fit, new PointD(g.Nodes[nodeA].X, g.Nodes[nodeA].Y));
                if (nodeB >= 0) b = Project(fit, new PointD(g.Nodes[nodeB].X, g.Nodes[nodeB].Y));
                bool loop = e.From == e.To;
                var p = new WirePiece
                {
                    A = a, B = b, Width = e.MeanWidth, Residual = fit.Residual, Angle = fit.Angle,
                    FreeA = !loop && nodeA >= 0 && g.Nodes[nodeA].Degree == 1,
                    FreeB = !loop && nodeB >= 0 && g.Nodes[nodeB].Degree == 1,
                };
                double ang = fit.Angle;
                double off0 = Math.Min(ang, Math.PI - ang), off90 = Math.Abs(ang - Math.PI / 2);
                p.Orientation = off0 <= snap ? WireOrientation.Horizontal
                              : off90 <= snap ? WireOrientation.Vertical : WireOrientation.Diagonal;
                if (p.Orientation == WireOrientation.Horizontal) { p.A = new PointD(p.A.x, fit.Cy); p.B = new PointD(p.B.x, fit.Cy); }
                if (p.Orientation == WireOrientation.Vertical) { p.A = new PointD(fit.Cx, p.A.y); p.B = new PointD(fit.Cx, p.B.y); }
                p.IsWire = !roundLoop && fit.Residual <= maxResidual &&
                           (p.Orientation == WireOrientation.Diagonal ? p.Length > o.MinDiagonalLength * w : p.Length >= o.MinWireLength * w);
                p.RoundLoop = roundLoop;
                p.Sources.Add(r.Count);
                r.Add(p);
            }
        }
        Square(r, w, o, maxResidual);
        Corners(r, w);
        Sides(r, w, maxResidual);
        AcuteCorners(r, w);
        return r;
    }

    /// <summary>A short straight side of a box drawn in a heavy pen (R-imf3-2): the corners fill in, the skeleton's runs
    /// stop short of each other, and a side cut in two by its lead's T leaves halves under MinWireLength — no candidate,
    /// so the box's cycle stays open and its long sides are taken for wire. An orthogonal run too short to be a wire,
    /// straight, with neither end in paper, whose ends both meet the end of an orthogonal run within 1.5 w — at a
    /// corner on at least one of them — is a <see cref="WirePiece.Side"/>: it closes the cycle the outline makes, and
    /// is never a wire.</summary>
    private static void Sides(List<WirePiece> pieces, double w, double maxResidual)
    {
        double reach = 1.5 * w + 0.5;
        bool Orthogonal(WirePiece q) => q.Orientation != WireOrientation.Diagonal;
        bool Short(WirePiece q) => !q.IsWire && !q.RoundLoop && Orthogonal(q) && !q.FreeA && !q.FreeB
                                   && q.Residual <= maxResidual && q.Length >= 0.75 * w;
        var shorts = pieces.Where(Short).ToList();
        if (shorts.Count == 0) return;
        // (meets, at a corner). In line, only another short run counts: a side's other half beyond its lead's T. A long
        // run in line is a wire, and its last pixels before a plate are no side.
        (bool Meets, bool Corner) At(WirePiece p, PointD end)
        {
            bool meets = false, corner = false;
            foreach (var q in pieces)
            {
                if (q == p || !Orthogonal(q) || !(q.IsWire || Short(q))) continue;
                if (Dist(q.A, end) > reach && Dist(q.B, end) > reach) continue;
                bool across = q.Orientation != p.Orientation;
                if (!across && !Short(q)) continue;
                meets = true;
                corner |= across;
            }
            return (meets, corner);
        }
        var sides = new List<WirePiece>();
        foreach (var p in shorts)
        {
            var (ma, ca) = At(p, p.A);
            var (mb, cb) = At(p, p.B);
            if (ma && mb && (ca || cb)) sides.Add(p);
        }
        foreach (var p in sides) p.Side = true;
    }

    /// <summary>The corner of a triangle drawn in a thin pen (R-imf8-2): where two sides meet at a sharp angle the
    /// skeleton breaks into a few short runs no candidate is made of, and the triangle's cycle stays open — its sides are
    /// taken for wire and its leads short through them. A chain of such runs, no longer than 3 w in all and no end of
    /// it in paper, joining the ends of two candidates that are not in line — one of them slanting — or of two slanting
    /// candidates in line (a side a lead's T cuts in two) is a
    /// <see cref="WirePiece.Side"/>: it closes the outline's cycle and is never a wire.</summary>
    private static void AcuteCorners(List<WirePiece> pieces, double w)
    {
        double reach = 1.5 * w + 0.5;
        var shorts = pieces.Where(p => !p.IsWire && !p.Side && !p.RoundLoop && !p.FreeA && !p.FreeB && p.Length <= 3 * w).ToList();
        if (shorts.Count == 0) return;
        // The chains: short runs end to end within a pixel and a half.
        var root = Enumerable.Range(0, shorts.Count).ToArray();
        int Find(int x) { while (root[x] != x) x = root[x] = root[root[x]]; return x; }
        static bool Touch(WirePiece a, WirePiece b) =>
            Dist(a.A, b.A) <= 1.5 || Dist(a.A, b.B) <= 1.5 || Dist(a.B, b.A) <= 1.5 || Dist(a.B, b.B) <= 1.5;
        for (int i = 0; i < shorts.Count; i++)
            for (int j = i + 1; j < shorts.Count; j++)
                if (Touch(shorts[i], shorts[j])) root[Find(i)] = Find(j);
        foreach (var chain in Enumerable.Range(0, shorts.Count).GroupBy(Find).Select(g => g.Select(i => shorts[i]).ToList()))
        {
            if (chain.Sum(p => p.Length) > 3 * w) continue;
            // Its outer ends: the run ends no other run of the chain touches.
            var ends = chain.SelectMany(p => new[] { p.A, p.B })
                            .Where(e => chain.Count(q => Dist(q.A, e) <= 1.5 || Dist(q.B, e) <= 1.5) == 1).ToList();
            if (ends.Count != 2) continue;
            WirePiece? At(PointD e) => pieces.Where(q => q.IsWire && (Dist(q.A, e) <= reach || Dist(q.B, e) <= reach))
                                             .OrderBy(q => Math.Min(Dist(q.A, e), Dist(q.B, e))).FirstOrDefault();
            if (At(ends[0]) is not { } a || At(ends[1]) is not { } b || a == b) continue;
            if (a.Orientation != WireOrientation.Diagonal && b.Orientation != WireOrientation.Diagonal) continue;
            // In line, both slanting: one side cut where a lead's T meets it.
            double turn = Math.Abs(a.Angle - b.Angle);
            if (Math.Min(turn, Math.PI - turn) < 20 * Math.PI / 180
                && (a.Orientation != WireOrientation.Diagonal || b.Orientation != WireOrientation.Diagonal)) continue;
            foreach (var p in chain) p.Side = true;
        }
    }

    /// <summary>A short run that is no wire candidate — curved, or a diagonal too short to be a wire — joining the end of
    /// a horizontal candidate to the end of a vertical one — and to nothing else — is the corner between them, rounded: a box drawn with round
    /// corners (circuitRF's own termination) otherwise reads as four loose sides, never the cycle that sets a symbol's
    /// outline aside, and its sides are taken for wire (IM-12). It joins as a diagonal candidate, so a wire bend drawn
    /// round stays one connected wire.</summary>
    private static void Corners(List<WirePiece> pieces, double w)
    {
        double reach = 1.5 * w + 0.5;
        bool EndsNear(WirePiece q, PointD p) => Dist(q.A, p) <= reach || Dist(q.B, p) <= reach;
        // One candidate of that axis ends there, and only one: where a wire carries on past the point, the run is no
        // corner of it — a hop's arc is cut in two by the wire it hops, and each half ends where that wire's two
        // pieces do.
        bool Meets(PointD end, WireOrientation axis) =>
            pieces.Count(q => q.IsWire && q.Orientation == axis && EndsNear(q, end)) == 1;
        var corners = pieces.Where(p => !p.IsWire && !p.RoundLoop && !p.FreeA && !p.FreeB && p.Length <= 3 * w
                                         && ((Meets(p.A, WireOrientation.Horizontal) && Meets(p.B, WireOrientation.Vertical))
                                             || (Meets(p.A, WireOrientation.Vertical) && Meets(p.B, WireOrientation.Horizontal))))
                            .ToList();
        foreach (var p in corners)
        {
            p.IsWire = true;
            p.Orientation = WireOrientation.Diagonal;
        }
    }

    /// <summary>Snaps a short straight run within 12° of an axis to it when BOTH its ends meet a run near the other
    /// axis — a side of a small box. Its fit leans on the pixel steps of the corners at its ends (a 3 w side of a box
    /// read 6.5°, a half side beside its lead's T 3–5°), and read as a diagonal it was no wire candidate and left the
    /// box's cycle open, so the box's other sides were taken for wire. A circle's flat bottom leans as little, but
    /// meets runs at 30° or more, never a square corner. So does one carrying straight on from a run on its axis.</summary>
    private static void Square(List<WirePiece> pieces, double w, SchematicImageOptions o, double maxResidual)
    {
        double tol = 12 * Math.PI / 180, reach = 1.5 * w + 0.5;
        int Near(WirePiece p) => p.Orientation switch
        {
            WireOrientation.Horizontal => 0,
            WireOrientation.Vertical => 1,
            _ => Math.Min(p.Angle, Math.PI - p.Angle) <= tol ? 0 : Math.Abs(p.Angle - Math.PI / 2) <= tol ? 1 : -1,
        };
        var snap = new List<(WirePiece P, int Axis)>();
        foreach (var p in pieces)
        {
            if (p.RoundLoop || p.Orientation != WireOrientation.Diagonal || p.Residual > maxResidual || p.Length > o.MaxSymbolStroke * w) continue;
            int axis = Near(p);
            if (axis < 0) continue;
            bool Corner(PointD end) =>
                pieces.Any(q => q != p && Near(q) == 1 - axis && (Dist(q.A, end) <= reach || Dist(q.B, end) <= reach));
            // Or carrying straight on from a run on its axis, end to end, to a bare end: a plate's half beside its
            // lead's T, which leans on the T's pixels as a box's side does on its corners (IMF-3).
            bool Continues(PointD end) =>
                (Dist(p.A, end) <= Dist(p.B, end) ? p.FreeB : p.FreeA)
                && pieces.Any(q => q != p && q.IsWire && (int)q.Orientation == axis
                                && ((Dist(q.A, end) <= reach && Math.Abs(Local(q.A, q.B, Far(p, end)).Perp) <= Math.Max(1.5, 0.5 * w))
                                    || (Dist(q.B, end) <= reach && Math.Abs(Local(q.A, q.B, Far(p, end)).Perp) <= Math.Max(1.5, 0.5 * w))));
            if ((Corner(p.A) && Corner(p.B)) || Continues(p.A) || Continues(p.B)) snap.Add((p, axis));
        }
        foreach (var (p, axis) in snap)
        {
            if (axis == 0)
            {
                double y = (p.A.y + p.B.y) / 2;
                (p.A, p.B, p.Orientation) = (new PointD(p.A.x, y), new PointD(p.B.x, y), WireOrientation.Horizontal);
            }
            else
            {
                double x = (p.A.x + p.B.x) / 2;
                (p.A, p.B, p.Orientation) = (new PointD(x, p.A.y), new PointD(x, p.B.y), WireOrientation.Vertical);
            }
            p.IsWire = p.Length >= o.MinWireLength * w;
        }
    }

    /// <summary>The edges of <paramref name="cycle"/> bounding a face that is an outline (R-imf4-2). The faces are traced
    /// on the cycle edges' plane embedding — at each vertex the next edge clockwise — and a bounded face (positive area,
    /// y up) is an outline unless a junction dot stands on it: a symbol's outline is never drawn through a dot, a loop of
    /// wires always meets at them.</summary>
    private static List<int> OutlineFaces(Assembled c, List<int> cycle, int nv, int[] nodeOf, Func<int, int, int> V, PlateRules plates)
    {
        var rot = new List<(int Edge, double Angle)>[nv];
        for (int v = 0; v < nv; v++) rot[v] = [];
        foreach (int e in cycle)
            foreach (int end in new[] { c.Edges[e].From, c.Edges[e].To })
            {
                var (dx, dy) = c.Away(e, end);
                rot[V(end, e)].Add((e, Math.Atan2(-dy, dx)));
            }
        foreach (var r in rot) r.Sort((a, b) => a.Angle.CompareTo(b.Angle));
        int Other(int v, int e) => V(c.Edges[e].From, e) == v ? V(c.Edges[e].To, e) : V(c.Edges[e].From, e);

        var seen = new HashSet<(int V, int E)>();
        var outline = new HashSet<int>();
        foreach (int start in cycle)
            foreach (int sv in new[] { V(c.Edges[start].From, start), V(c.Edges[start].To, start) })
            {
                if (seen.Contains((sv, start))) continue;
                var face = new List<(int V, int E)>();
                var (u, e) = (sv, start);
                while (seen.Add((u, e)) && face.Count <= 4 * cycle.Count)
                {
                    face.Add((u, e));
                    int v = Other(u, e);
                    var r = rot[v];
                    int i = r.FindIndex(x => x.Edge == e);
                    if (i < 0 || r.Count < 2) break;
                    (u, e) = (v, r[(i - 1 + r.Count) % r.Count].Edge);
                }
                double area = 0;
                foreach (var (fv, fe) in face)
                {
                    var (p, q) = (c.Nodes[nodeOf[fv]].P, c.Nodes[nodeOf[Other(fv, fe)]].P);
                    area += p.x * -q.y - q.x * -p.y;
                }
                if (area <= 0) continue;
                if (face.Any(f => c.Nodes[nodeOf[f.V]].Dot)) { plates.MeshFaces++; continue; }
                foreach (var (_, fe) in face) outline.Add(fe);
            }
        return [.. cycle.Where(outline.Contains)];
    }

    private static PointD Far(WirePiece p, PointD end) => Dist(p.A, end) <= Dist(p.B, end) ? p.B : p.A;

    /// <summary>The indices of the wire candidates that are strokes of symbols — short lines with two bare ends, and
    /// every candidate on a cycle of candidates.</summary>
    /// <param name="sample">When given, collects the symbol scale's samples (PictureProfile, R-imf2-1): the length of
    /// every orthogonal line with two bare ends that carries a lead or stands in a stack of them (a plate, a bar), and
    /// of every orthogonal side of a closed outline, no longer than
    /// <paramref name="sampleMax"/> pixels across — whatever MaxSymbolStroke says. A line's length is its ink's: the
    /// skeleton stops half a pen short of each bare end. An end stopping on a word kept as a symbol detail (a label
    /// touching a plate) is bare for the sample, and one stopping on a fill is not: <paramref name="endAt"/> says which,
    /// or null for the skeleton's own verdict.</param>
    /// <param name="plates">When given, the rules of R-imf3-1 and R-imf3-2 apply, in its symbol scale: a plate at any
    /// length, a parallel pair, a curved plate, a heavy box's short sides in the cycle search — and it counts them.
    /// Without it (the symbol scale's own measurement, before s is known) the strokes are IM-7's.</param>
    internal static HashSet<int> SymbolStrokes(IReadOnlyList<WirePiece> candidates, IReadOnlyList<JunctionBlob> blobs,
                                               double w, SchematicImageOptions o, List<double>? sample = null, double sampleMax = 0,
                                               Func<PointD, bool?>? endAt = null, PlateRules? plates = null)
    {
        var a = Assemble(candidates, [], blobs, w, o, dissolve: false);
        var reject = new HashSet<int>();
        double cosSnap = Math.Cos(Math.Max(o.SnapAngleDeg, 3.0) * Math.PI / 180);

        // Lines: segments paired straight through a node.
        int ne = a.Edges.Count;
        var (pairsAt, paired, root) = StraightPairs(a, cosSnap);
        int Find(int x) => root[x];
        var freeLines = new List<(PointD A, PointD B, double Length, bool Tee)>();
        foreach (var line in Enumerable.Range(0, ne).GroupBy(Find))
        {
            var ends = new List<int>();
            foreach (int e in line)
            {
                var (f, t) = (a.Edges[e].From, a.Edges[e].To);
                if (!paired.Contains((e, f))) ends.Add(f);
                if (!paired.Contains((e, t))) ends.Add(t);
            }
            if (ends.Count != 2) continue;
            var (n0, n1) = (a.Nodes[ends[0]], a.Nodes[ends[1]]);
            if (a.Degree(ends[0]) != 1 || a.Degree(ends[1]) != 1) continue;
            bool Bare(ANode node) => endAt?.Invoke(node.P) ?? node.Free;
            if (sample is not null && Bare(n0) && Bare(n1)
                && Dist(n0.P, n1.P) + w <= sampleMax && line.All(e => a.Edges[e].Piece.Orientation != WireOrientation.Diagonal))
                freeLines.Add((n0.P, n1.P, Dist(n0.P, n1.P) + w,
                               line.SelectMany(e => new[] { a.Edges[e].From, a.Edges[e].To })
                                   .Any(nd => nd != ends[0] && nd != ends[1] && a.Degree(nd) >= 3)));
            if (plates is not null)
            {
                bool BareEnd(ANode node) => plates.Bare?.Invoke(node.P) ?? node.Free;
                bool bare0 = BareEnd(n0), bare1 = BareEnd(n1);
                if (!bare0 && !bare1) continue;
                double len = Dist(n0.P, n1.P);
                if (plates.Labelled is { } labelled)
                {
                    var mid = new PointD((n0.P.x + n1.P.x) / 2, (n0.P.y + n1.P.y) / 2);
                    if (labelled(n0.P, mid) && labelled(n1.P, mid)) continue;
                }
                var (tees, crossings, teeAt) = Tees(a, line, ends);
                // One bare end — the other running into an unread glyph — with a lead meeting it is a plate only beside
                // another (a pair).
                if (!bare0 || !bare1)
                {
                    if (tees == 1 && crossings == 0 && len <= MaxPlate * plates.Symbol)
                        plates.Lines.Add(new PlateLine(n0.P, n1.P, [.. line.SelectMany(e => a.Edges[e].Piece.Sources)], false) { OneBare = true });
                    continue;
                }
                bool plate = len <= o.MaxSymbolStroke * w
                             || (tees == 0 && crossings == 0 && len <= o.MaxSymbolStrokeScales * plates.Symbol)
                             || (tees == 1 && crossings == 0 && len <= MaxPlate * plates.Symbol
                                 && Local(n0.P, n1.P, teeAt).Along is var t && t >= 0.2 * len && t <= 0.8 * len);
                if (plate)
                {
                    if (tees == 1) plates.Plates++;
                    foreach (int e in line) foreach (int s in a.Edges[e].Piece.Sources) reject.Add(s);
                }
                if (tees <= 1 && crossings == 0 && len <= MaxPlate * plates.Symbol)
                    plates.Lines.Add(new PlateLine(n0.P, n1.P, [.. line.SelectMany(e => a.Edges[e].Piece.Sources)], plate));
                continue;
            }
            if (!n0.Free || !n1.Free) continue;
            if (Dist(n0.P, n1.P) > o.MaxSymbolStroke * w) continue;
            foreach (int e in line) foreach (int s in a.Edges[e].Piece.Sources) reject.Add(s);
        }
        if (plates is not null) PairsAndCurves(a, paired, pairsAt, plates, w, reject);

        // Cycles — on the candidates and a heavy box's short sides (R-imf3-2), which close its outline but are no
        // line of their own.
        var c = plates is null || plates.Sides.Count == 0 ? a : Assemble([.. candidates, .. plates.Sides], [], blobs, w, o, dissolve: false);
        var cPairs = c == a ? pairsAt : StraightPairs(c, cosSnap).PairsAt;
        int cne = c.Edges.Count;
        // Cycles: every edge that is not a bridge, with a four-way crossing's two straight pairs kept apart.
        var vertexOf = new Dictionary<(int Node, int Edge), int>();
        int nv = c.Nodes.Count;
        for (int n = 0; n < c.Nodes.Count; n++)
            if (c.Degree(n) == 4 && cPairs[n].Count == 2)
            {
                vertexOf[(n, cPairs[n][1].A)] = nv;
                vertexOf[(n, cPairs[n][1].B)] = nv;
                nv++;
            }
        int V(int node, int edge) => vertexOf.TryGetValue((node, edge), out int v) ? v : node;
        var adj = new List<(int To, int Edge)>[nv];
        for (int v = 0; v < nv; v++) adj[v] = [];
        for (int e = 0; e < cne; e++)
        {
            int u = V(c.Edges[e].From, e), v = V(c.Edges[e].To, e);
            if (u == v) { foreach (int s in c.Edges[e].Piece.Sources) reject.Add(s); continue; }
            adj[u].Add((v, e));
            adj[v].Add((u, e));
        }
        var disc = new int[nv];
        var low = new int[nv];
        Array.Fill(disc, -1);
        int time = 0;
        var bridge = new bool[cne];
        void Dfs(int u, int viaEdge)
        {
            disc[u] = low[u] = time++;
            foreach (var (v, e) in adj[u])
            {
                if (e == viaEdge) continue;
                if (disc[v] < 0)
                {
                    Dfs(v, e);
                    low[u] = Math.Min(low[u], low[v]);
                    if (low[v] > disc[u]) bridge[e] = true;
                }
                else low[u] = Math.Min(low[u], disc[v]);
            }
        }
        for (int v = 0; v < nv; v++) if (disc[v] < 0) Dfs(v, -1);
        var cycle = Enumerable.Range(0, cne).Where(e => !bridge[e] && V(c.Edges[e].From, e) != V(c.Edges[e].To, e)).ToList();
        // Which cycles are outlines (R-imf4-2): with the final rules, only the edges of a FACE no junction dot stands on.
        // A box's pin leads joined outside it — two GND pins tied by one wire — close a face with the box's side, and that
        // face runs through the dot where the wires meet: the leads are wire, the box's side is the box's.
        if (plates is not null)
        {
            var nodeOf = new int[nv];
            for (int n = 0; n < c.Nodes.Count; n++) nodeOf[n] = n;
            foreach (var ((n, _), v) in vertexOf) nodeOf[v] = n;
            cycle = OutlineFaces(c, cycle, nv, nodeOf, V, plates);
        }
        foreach (int e in cycle) foreach (int s in c.Edges[e].Piece.Sources) reject.Add(s);

        // Inside an outline: a candidate lying wholly within the box of a connected cycle is drawn in the symbol — a
        // termination's lead carried on through the top of its box to the zig-zag (IM-12). Its length inside depends on
        // the zoom; where the outline is crossed does not.
        var outline = new Dictionary<int, int>();
        foreach (int e in cycle) { outline[c.Edges[e].From] = c.Edges[e].From; outline[c.Edges[e].To] = c.Edges[e].To; }
        int Root(int x) { while (outline[x] != x) x = outline[x] = outline[outline[x]]; return x; }
        foreach (int e in cycle) outline[Root(c.Edges[e].From)] = Root(c.Edges[e].To);
        var boxes = cycle.GroupBy(e => Root(c.Edges[e].From)).Select(g =>
        {
            var ps = g.SelectMany(e => new[] { c.Edges[e].Piece.A, c.Edges[e].Piece.B }).ToList();
            return (X0: ps.Min(p => p.x), Y0: ps.Min(p => p.y), X1: ps.Max(p => p.x), Y1: ps.Max(p => p.y));
        }).ToList();
        // A plate or a bar carries a lead (a T in it) or stands in a stack of bare lines (a capacitor's other plate, a
        // ground's lower bars); an arrow's shaft does neither. A dash is no plate: three or more bare lines on one line,
        // each within its own length of the next, are a dashed line (two side by side are two grounds' bars).
        if (sample is not null)
        {
            var chain = Enumerable.Range(0, freeLines.Count).ToArray();
            int Chain(int x) { while (chain[x] != x) x = chain[x] = chain[chain[x]]; return x; }
            var stacked = new bool[freeLines.Count];
            for (int i = 0; i < freeLines.Count; i++)
                for (int j = i + 1; j < freeLines.Count; j++)
                {
                    var (p0, p1, len, _) = freeLines[i];
                    var (q0, q1, qLen, _) = freeLines[j];
                    double perp0 = Local(p0, p1, q0).Perp, perp1 = Local(p0, p1, q1).Perp;
                    if (Math.Abs(perp0) <= w && Math.Abs(perp1) <= w)
                    {
                        double gap = Math.Min(Math.Min(Dist(p0, q0), Dist(p0, q1)), Math.Min(Dist(p1, q0), Dist(p1, q1)));
                        if (gap <= Math.Max(len, qLen) + w) chain[Chain(i)] = Chain(j);
                        continue;
                    }
                    // Parallel, a short way off, side by side along their length.
                    if (Math.Abs(perp0 - perp1) > w || Math.Abs(perp0) > Math.Max(len, qLen)) continue;
                    double a0 = Local(p0, p1, q0).Along, a1 = Local(p0, p1, q1).Along;
                    double overlap = Math.Min(Dist(p0, p1), Math.Max(a0, a1)) - Math.Max(0, Math.Min(a0, a1));
                    if (overlap >= 0.3 * Math.Min(len, qLen)) stacked[i] = stacked[j] = true;
                }
            var size = Enumerable.Range(0, freeLines.Count).GroupBy(Chain).ToDictionary(g => g.Key, g => g.Count());
            for (int i = 0; i < freeLines.Count; i++)
                if (size[Chain(i)] < 3 && (freeLines[i].Tee || stacked[i])) sample.Add(freeLines[i].Length);
        }
        if (sample is not null)
            foreach (var g in cycle.GroupBy(e => Root(c.Edges[e].From)))
            {
                var ps = g.SelectMany(e => new[] { c.Edges[e].Piece.A, c.Edges[e].Piece.B }).ToList();
                if (ps.Max(p => p.x) - ps.Min(p => p.x) > sampleMax || ps.Max(p => p.y) - ps.Min(p => p.y) > sampleMax) continue;
                // A side is the run of its straight pieces: a lead's T cuts one into two halves.
                foreach (var side in g.GroupBy(Find))
                {
                    var pieces = side.Select(e => c.Edges[e].Piece).ToList();
                    if (pieces.Any(p => p.Orientation == WireOrientation.Diagonal)) continue;
                    var ends = pieces.SelectMany(p => new[] { p.A, p.B }).ToList();
                    sample.Add(pieces[0].Orientation == WireOrientation.Horizontal
                        ? ends.Max(p => p.x) - ends.Min(p => p.x) : ends.Max(p => p.y) - ends.Min(p => p.y));
                }
            }
        if (plates is not null)
        {
            plates.Boxes = boxes.Count;
            foreach (var g in cycle.GroupBy(e => Root(c.Edges[e].From)))
            {
                var ps = g.SelectMany(e => new[] { c.Edges[e].Piece.A, c.Edges[e].Piece.B }).ToList();
                plates.Outlines.Add(new OutlineBox(ps.Min(p => p.x), ps.Min(p => p.y), ps.Max(p => p.x), ps.Max(p => p.y),
                                                   g.All(e => c.Edges[e].Piece.Orientation != WireOrientation.Diagonal)));
            }
        }
        double tol = w;
        bool In(PointD p, (double X0, double Y0, double X1, double Y1) b) =>
            p.x >= b.X0 - tol && p.x <= b.X1 + tol && p.y >= b.Y0 - tol && p.y <= b.Y1 + tol;
        for (int e = 0; e < cne; e++)
        {
            var p = c.Edges[e].Piece;
            if (p.Sources.Any(reject.Contains)) continue;
            if (boxes.Any(b => In(p.A, b) && In(p.B, b))) foreach (int s in p.Sources) reject.Add(s);
        }
        return reject;
    }

    /// <summary>The straight pairs at every node of <paramref name="a"/> — the two edges leaving it most nearly opposite,
    /// repeatedly — which edges are paired at which node, and each edge's line (the union-find root of its pairs).</summary>
    private static (List<(int A, int B)>[] PairsAt, HashSet<(int Edge, int Node)> Paired, int[] Line) StraightPairs(Assembled a, double cosSnap)
    {
        int ne = a.Edges.Count;
        var parent = Enumerable.Range(0, ne).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var paired = new HashSet<(int Edge, int Node)>();
        var pairsAt = new List<(int A, int B)>[a.Nodes.Count];
        for (int n = 0; n < a.Nodes.Count; n++)
        {
            pairsAt[n] = [];
            var at = a.EdgesAt(n);
            var cand = new List<(double Cos, int A, int B)>();
            for (int i = 0; i < at.Count; i++)
                for (int j = i + 1; j < at.Count; j++)
                {
                    if (at[i] == at[j] || !a.Straight(at[i], at[j], n, cosSnap)) continue;
                    var (ax, ay) = a.Away(at[i], n);
                    var (bx, by) = a.Away(at[j], n);
                    cand.Add((ax * bx + ay * by, at[i], at[j]));
                }
            var used = new HashSet<int>();
            foreach (var (_, ea, eb) in cand.OrderBy(t => t.Cos).ThenBy(t => t.A).ThenBy(t => t.B))
            {
                if (used.Contains(ea) || used.Contains(eb)) continue;
                used.Add(ea); used.Add(eb);
                pairsAt[n].Add((ea, eb));
                paired.Add((ea, n)); paired.Add((eb, n));
                parent[Find(ea)] = Find(eb);
            }
        }
        return (pairsAt, paired, Enumerable.Range(0, ne).Select(Find).ToArray());
    }

    /// <summary>A plate is never longer than this many symbol scales, whatever else it meets: the heavy IEC drawing's
    /// plates are 2.4 s.</summary>
    internal const double MaxPlate = 4.0;

    /// <summary>The Ts on a line: its interior nodes of degree 3 (a lead meeting it from the side), of degree 4 or more
    /// (a wire crossing it), and where the last T is.</summary>
    private static (int Tees, int Crossings, PointD At) Tees(Assembled a, IEnumerable<int> line, List<int> ends)
    {
        var interior = line.SelectMany(e => new[] { a.Edges[e].From, a.Edges[e].To }).Where(n => !ends.Contains(n)).Distinct().ToList();
        int tees = 0, crossings = 0;
        PointD at = default;
        foreach (int n in interior)
        {
            int d = a.Degree(n);
            if (d == 3) { tees++; at = a.Nodes[n].P; }
            else if (d >= 4) crossings++;
        }
        return (tees, crossings, at);
    }

    /// <summary>R-imf3-1's other two plates. A pair: two lines with bare ends side by side, 1 w to 1.5 s apart, the strip
    /// between them clear of ink — a datasheet's hairline plates set far apart, whose lead may stop short — or a line
    /// with one bare end (the other running into a glyph that touches it) beside a plate. A curved
    /// plate: a chain of runs bending gently (each turn under 45°) between two bare ends, its chord no longer than
    /// 1.5 s, with one lead meeting it, or lying within 45° of a plate's direction across a clear strip.</summary>
    private static void PairsAndCurves(Assembled a, HashSet<(int Edge, int Node)> paired, List<(int A, int B)>[] pairsAt,
                                       PlateRules plates, double w, HashSet<int> reject)
    {
        double s = plates.Symbol;
        if (s <= 0) return;
        bool Clear(PointD p0, PointD p1, PointD q0, PointD q1)
        {
            if (plates.Ink is null) return true;
            // The midline between the two, over their common length, a pen in from each end.
            for (int k = 1; k < 8; k++)
            {
                double f = k / 8.0;
                var m = new PointD((p0.x + (p1.x - p0.x) * f + q0.x + (q1.x - q0.x) * f) / 2,
                                   (p0.y + (p1.y - p0.y) * f + q0.y + (q1.y - q0.y) * f) / 2);
                if (plates.Ink(m)) return false;
            }
            return true;
        }
        // Side by side: q's ends projected on p, over at least half the shorter, 1 w … 1.5 s across, within 45°.
        bool Facing(PointD p0, PointD p1, PointD q0, PointD q1, double maxTurnDeg, out PointD r0, out PointD r1)
        {
            r0 = q0; r1 = q1;
            double lp = Dist(p0, p1), lq = Dist(q0, q1);
            if (lp < 1e-9 || lq < 1e-9) return false;
            double cos = Math.Abs(((p1.x - p0.x) * (q1.x - q0.x) + (p1.y - p0.y) * (q1.y - q0.y)) / (lp * lq));
            if (cos < Math.Cos(maxTurnDeg * Math.PI / 180)) return false;
            var (a0, d0) = Local(p0, p1, q0);
            var (a1, d1) = Local(p0, p1, q1);
            double across = Math.Abs((d0 + d1) / 2);
            if (across < w || across > 1.5 * s) return false;
            double overlap = Math.Min(lp, Math.Max(a0, a1)) - Math.Max(0, Math.Min(a0, a1));
            if (overlap < 0.5 * Math.Min(lp, lq)) return false;
            if (a0 > a1) (r0, r1) = (q1, q0);
            return Math.Max(lp, lq) <= 2.5 * Math.Min(lp, lq);
        }

        var lines = plates.Lines;
        for (int i = 0; i < lines.Count; i++)
            for (int j = i + 1; j < lines.Count; j++)
            {
                var (p, q) = (lines[i], lines[j]);
                if (p.Plate && q.Plate) continue;
                if ((p.OneBare || q.OneBare) && !p.Plate && !q.Plate) continue;
                if (!Facing(p.A, p.B, q.A, q.B, 10, out var r0, out var r1) || !Clear(p.A, p.B, r0, r1)) continue;
                plates.PlatePairs++;
                foreach (int x in p.Sources.Concat(q.Sources)) reject.Add(x);
                lines[i] = p with { Plate = true };
                lines[j] = q with { Plate = true };
            }

        // A line with one bare end and no partner among the candidates (the other plate dropped as a glyph's stroke of
        // a label touching it): a stroke of ink as long, parallel, across a clear strip.
        if (plates.Ink is { } ink)
            for (int i = 0; i < lines.Count; i++)
            {
                var p = lines[i];
                if (p.Plate || !p.OneBare) continue;
                double lp = Dist(p.A, p.B);
                double nx = -(p.B.y - p.A.y) / lp, ny = (p.B.x - p.A.x) / lp;
                bool found = false;
                foreach (int side in new[] { 1, -1 })
                    for (double d = Math.Max(w, 2); d <= 1.5 * s && !found; d += 0.5)
                    {
                        PointD At(PointD q, double off) => new(q.x + side * nx * off, q.y + side * ny * off);
                        int hits = 0;
                        for (int k = 1; k < 8; k++)
                        {
                            double f = k / 8.0;
                            if (ink(At(new PointD(p.A.x + (p.B.x - p.A.x) * f, p.A.y + (p.B.y - p.A.y) * f), d))) hits++;
                        }
                        if (hits < 7) continue;
                        found = Clear(At(p.A, w), At(p.B, w), At(p.A, d - w), At(p.B, d - w));
                        break;   // the nearest stroke beside it decides
                    }
                if (!found) continue;
                plates.PlatePairs++;
                foreach (int x in p.Sources) reject.Add(x);
                lines[i] = p with { Plate = true };
            }

        // Curves: edges joined at a node where they turn gently and are no straight pair.
        int ne = a.Edges.Count;
        var parent = Enumerable.Range(0, ne).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var joined = new HashSet<(int Edge, int Node)>();
        bool bent = false;
        var bentGroups = new HashSet<int>();
        for (int n = 0; n < a.Nodes.Count; n++)
        {
            var at = a.EdgesAt(n);
            foreach (var (ea, eb) in pairsAt[n]) { parent[Find(ea)] = Find(eb); joined.Add((ea, n)); joined.Add((eb, n)); }
            if (at.Count is < 2 or > 3) continue;
            var free = at.Where(e => !paired.Contains((e, n))).ToList();
            (int A, int B, double Turn)? best = null;
            for (int i = 0; i < free.Count; i++)
                for (int j = i + 1; j < free.Count; j++)
                {
                    if (free[i] == free[j]) continue;
                    var (ax, ay) = a.Away(free[i], n);
                    var (bx, by) = a.Away(free[j], n);
                    double turn = Math.PI - Math.Acos(Math.Clamp(ax * bx + ay * by, -1, 1));
                    if (turn <= Math.PI / 4 && (best is null || turn < best.Value.Turn)) best = (free[i], free[j], turn);
                }
            if (best is not { } b) continue;
            parent[Find(b.A)] = Find(b.B);
            joined.Add((b.A, n)); joined.Add((b.B, n));
            bent = true;
            bentGroups.Add(b.A);
        }
        if (!bent) return;
        var groupsBent = bentGroups.Select(Find).ToHashSet();
        foreach (var g in Enumerable.Range(0, ne).GroupBy(Find))
        {
            if (!groupsBent.Contains(g.Key)) continue;
            var ends = new List<int>();
            foreach (int e in g)
            {
                var (f, t) = (a.Edges[e].From, a.Edges[e].To);
                if (!joined.Contains((e, f))) ends.Add(f);
                if (!joined.Contains((e, t))) ends.Add(t);
            }
            if (ends.Count != 2 || a.Degree(ends[0]) != 1 || a.Degree(ends[1]) != 1) continue;
            var (n0, n1) = (a.Nodes[ends[0]], a.Nodes[ends[1]]);
            if (!(plates.Bare?.Invoke(n0.P) ?? n0.Free) || !(plates.Bare?.Invoke(n1.P) ?? n1.Free)) continue;
            double chord = Dist(n0.P, n1.P);
            if (chord > 1.5 * s || chord < 2 * w) continue;
            var nodes = g.SelectMany(e => new[] { a.Edges[e].From, a.Edges[e].To }).Distinct().ToList();
            double sag = nodes.Max(n => Math.Abs(Local(n0.P, n1.P, a.Nodes[n].P).Perp));
            if (sag > 0.35 * chord) continue;
            var (tees, crossings, _) = Tees(a, g, ends);
            if (crossings > 0 || tees > 1) continue;
            bool plate = tees == 1 || lines.Any(l => l.Plate && Facing(l.A, l.B, n0.P, n1.P, 45, out var r0, out var r1) && Clear(l.A, l.B, r0, r1));
            if (!plate) continue;
            plates.CurvedPlates++;
            foreach (int e in g) foreach (int x in a.Edges[e].Piece.Sources) reject.Add(x);
        }
    }

    /// <summary>The graph of <paramref name="wires"/>, with each hop's two halves joined across the wire it hops.</summary>
    /// <param name="flips">The user's crossing corrections (R-im10-8), in these pixels: the crossing or hop within
    /// 2 w of each point reads as it says, whatever the rule.</param>
    internal static WireGraph Build(IReadOnlyList<WirePiece> wires, IReadOnlyList<HopJoin> hops,
                                    IReadOnlyList<JunctionBlob> blobs, double w, SchematicImageOptions o,
                                    IReadOnlyList<(double X, double Y, bool Connect)>? flips = null)
    {
        double reach = Math.Max(2 * w, 4);
        bool? Flip(PointD p)
        {
            foreach (var f in flips ?? [])
                if (Math.Abs(f.X - p.x) <= reach && Math.Abs(f.Y - p.y) <= reach) return f.Connect;
            return null;
        }

        var a = Assemble(wires, hops, blobs, w, o, dissolve: true);
        if (a.Edges.Count == 0) return Empty;

        // Reading order for nodes; segments by their first node, then their second.
        var nodeOrder = Enumerable.Range(0, a.Nodes.Count)
            .OrderBy(n => Math.Round(a.Nodes[n].P.y, 6)).ThenBy(n => Math.Round(a.Nodes[n].P.x, 6)).ToList();
        var nodeId = new int[a.Nodes.Count];
        for (int k = 0; k < nodeOrder.Count; k++) nodeId[nodeOrder[k]] = k;
        var nodes = nodeOrder.Select((n, k) =>
        {
            var an = a.Nodes[n];
            int deg = a.Degree(n);
            return new WireNode(k, an.P.x, an.P.y, deg, deg >= 3 && an.Dot, an.Free) { Terminal = deg == 1 && an.Dot };
        }).ToList();

        var segOrder = Enumerable.Range(0, a.Edges.Count)
            .OrderBy(e => Math.Min(nodeId[a.Edges[e].From], nodeId[a.Edges[e].To]))
            .ThenBy(e => Math.Max(nodeId[a.Edges[e].From], nodeId[a.Edges[e].To]))
            .ThenBy(e => e).ToList();
        var segId = new int[a.Edges.Count];
        var segments = new List<WireSegment>(segOrder.Count);
        foreach (int e in segOrder)
        {
            var ed = a.Edges[e];
            int f = nodeId[ed.From], t = nodeId[ed.To];
            var (pa, pb) = (a.Nodes[ed.From].P, a.Nodes[ed.To].P);
            if (f > t) { (f, t) = (t, f); (pa, pb) = (pb, pa); }
            segId[e] = segments.Count;
            segments.Add(new WireSegment(segments.Count, f, t, pa, pb, ed.Piece.Orientation, ed.Piece.Width, ed.Piece.Residual)
                         { Dashed = ed.Piece.Dashed });
        }

        // Crossings and nets.
        var parent = Enumerable.Range(0, segments.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Union(int x, int y) { x = Find(x); y = Find(y); if (x != y) parent[Math.Max(x, y)] = Math.Min(x, y); }
        double cosSnap = Math.Cos(Math.Max(o.SnapAngleDeg, 3.0) * Math.PI / 180);
        var crossings = new List<WireCrossing>();
        foreach (int n in nodeOrder)
        {
            var at = a.EdgesAt(n);
            if (at.Count == 4)
            {
                var pairs = new List<(int A, int B)>();
                var left = at.ToList();
                while (left.Count > 0)
                {
                    int x = left[0];
                    var (ax, ay) = a.Away(x, n);
                    int best = -1;
                    double bc = -cosSnap;
                    foreach (int y in left.Skip(1))
                    {
                        if (!a.Straight(x, y, n, cosSnap)) continue;
                        var (bx, by) = a.Away(y, n);
                        double c = ax * bx + ay * by;
                        if (best < 0 || c < bc) { bc = c; best = y; }
                    }
                    if (best < 0) break;
                    pairs.Add((x, best));
                    left.Remove(x);
                    left.Remove(best);
                }
                bool straight = pairs.Count == 2;
                bool dot = a.Nodes[n].Dot;
                var reading = !straight ? CrossingReading.Connected : o.Crossings switch
                {
                    CrossingRule.Never => CrossingReading.NotConnected,
                    CrossingRule.Always => CrossingReading.Connected,
                    _ => dot ? CrossingReading.Connected : CrossingReading.NotConnected,
                };
                bool? flip = straight ? Flip(a.Nodes[n].P) : null;
                if (flip is { } connect) reading = connect ? CrossingReading.Connected : CrossingReading.NotConnected;
                crossings.Add(new WireCrossing(0, a.Nodes[n].P.x, a.Nodes[n].P.y, nodeId[n], dot, reading,
                    [.. at.Select(e => segId[e]).OrderBy(s => s)]) { Overridden = flip is not null });
                if (reading == CrossingReading.NotConnected)
                {
                    foreach (var (x, y) in pairs) Union(segId[x], segId[y]);
                    continue;
                }
            }
            for (int k = 1; k < at.Count; k++) Union(segId[at[0]], segId[at[k]]);
        }
        foreach (var hop in a.Hops)
        {
            var through = segments.Where(s => DistToSegment(hop.At, s.A, s.B) <= Math.Max(w, 1.5)).Select(s => s.Id).ToList();
            bool joined = Flip(hop.At) == true;
            if (joined) foreach (int t in through.Skip(1)) Union(through[0], t);
            crossings.Add(new WireCrossing(0, hop.At.x, hop.At.y, null, false, joined ? CrossingReading.Connected : CrossingReading.Hop,
                                           through) { Overridden = joined });
        }
        crossings = crossings.Select((c, i) => c with { Id = i }).ToList();

        var net = new int[segments.Count];
        var netOf = new Dictionary<int, int>();
        for (int s = 0; s < segments.Count; s++)
        {
            int r = Find(s);
            if (!netOf.TryGetValue(r, out int id)) netOf[r] = id = netOf.Count;
            net[s] = id;
        }
        return new WireGraph(nodes, segments, crossings, net, netOf.Count);
    }

    /// <summary>The filled blobs on the wires no bigger than a junction dot: where the stroke is at least
    /// 0.9·MinDot w across and at most 1.15·MaxDot w — the slack is the distance transform's own quantisation, which
    /// reads a small disc a little narrow.</summary>
    internal static List<JunctionBlob> Blobs(BinaryImage mask, float[] dt, double w, SchematicImageOptions o)
    {
        double thr = (0.9 * o.MinDot * w + 1) / 2;
        var core = new BinaryImage(mask.Width, mask.Height);
        for (int i = 0; i < dt.Length; i++) core.Pixels[i] = dt[i] >= thr ? (byte)1 : (byte)0;
        var lab = Components.Label(core);
        int n = lab.Components.Count;
        var max = new double[n + 1];
        var sx = new double[n + 1];
        var sy = new double[n + 1];
        var sw = new double[n + 1];
        for (int i = 0; i < dt.Length; i++)
        {
            int c = lab.Labels[i];
            if (c == 0) continue;
            double d = dt[i];
            max[c] = Math.Max(max[c], d);
            sx[c] += d * (i % mask.Width + 0.5);
            sy[c] += d * (i / mask.Width + 0.5);
            sw[c] += d;
        }
        var r = new List<JunctionBlob>();
        for (int c = 1; c <= n; c++)
        {
            if (2 * max[c] - 1 > 1.15 * o.MaxDot * w) continue;     // a filled area, not a dot
            r.Add(new JunctionBlob(new PointD(sx[c] / sw[c], sy[c] / sw[c]), max[c] - 0.5));
        }
        return r;
    }

    // ── Assembly: ends clustered into nodes ─────────────────────────────────────────────────────────────────────

    private sealed class ANode
    {
        public PointD P;
        public bool Free = true;
        public bool Dot;
    }

    private sealed record AEdge(int From, int To, WirePiece Piece);

    private sealed class Assembled
    {
        public readonly List<ANode> Nodes = [];
        public readonly List<AEdge> Edges = [];
        public readonly List<HopJoin> Hops = [];

        public List<int> EdgesAt(int n)
        {
            var r = new List<int>();
            for (int e = 0; e < Edges.Count; e++)
            {
                if (Edges[e].From == n) r.Add(e);
                if (Edges[e].To == n) r.Add(e);
            }
            return r;
        }

        public int Degree(int n) => EdgesAt(n).Count;

        /// <summary>Edges <paramref name="e1"/> and <paramref name="e2"/> leave node <paramref name="n"/> in opposite
        /// directions on one line. Two wires both read horizontal (or both vertical) are on one line whatever their
        /// ends' rounding says — a 7-pixel stub a pixel off is 8° off by angle alone.</summary>
        public bool Straight(int e1, int e2, int n, double cosSnap)
        {
            var (ax, ay) = Away(e1, n);
            var (bx, by) = Away(e2, n);
            double c = ax * bx + ay * by;
            var (o1, o2) = (Edges[e1].Piece.Orientation, Edges[e2].Piece.Orientation);
            if (o1 == o2 && o1 != WireOrientation.Diagonal) return c < 0;
            return c <= -cosSnap;
        }

        /// <summary>The unit direction of edge <paramref name="e"/> leaving node <paramref name="n"/>.</summary>
        public (double X, double Y) Away(int e, int n)
        {
            var ed = Edges[e];
            var p = Nodes[n].P;
            var q = Nodes[ed.From == n ? ed.To : ed.From].P;
            double dx = q.x - p.x, dy = q.y - p.y, l = Math.Sqrt(dx * dx + dy * dy);
            return l < 1e-9 ? (0, 0) : (dx / l, dy / l);
        }
    }

    private static Assembled Assemble(IReadOnlyList<WirePiece> input, IReadOnlyList<HopJoin> hops,
                                      IReadOnlyList<JunctionBlob> blobs, double w, SchematicImageOptions o, bool dissolve)
    {
        var segs = input.Select(p => p.Copy()).ToList();
        var result = new Assembled();

        // Hops: the two halves are one wire across the wire hopped.
        foreach (var hop in hops)
        {
            var e1 = NearestEnd(segs, hop.End1, 2 * w + 1);
            var e2 = NearestEnd(segs, hop.End2, 2 * w + 1);
            if (e1 is not { } x || e2 is not { } y || x.Seg == y.Seg) continue;
            var (s1, s2) = (segs[x.Seg], segs[y.Seg]);
            var m = new WirePiece
            {
                A = x.End == 0 ? s1.B : s1.A, FreeA = x.End == 0 ? s1.FreeB : s1.FreeA,
                B = y.End == 0 ? s2.B : s2.A, FreeB = y.End == 0 ? s2.FreeB : s2.FreeA,
                Width = (s1.Width + s2.Width) / 2, Residual = Math.Max(s1.Residual, s2.Residual),
                Orientation = s1.Orientation, IsWire = true,
            };
            m.Sources.AddRange(s1.Sources.Concat(s2.Sources).Distinct().OrderBy(s => s));
            segs.RemoveAt(Math.Max(x.Seg, y.Seg));
            segs.RemoveAt(Math.Min(x.Seg, y.Seg));
            segs.Add(m);
            if (!hop.Marker) result.Hops.Add(hop);
        }

        // A T: an end on another wire's interior splits that wire.
        double tol = 1.5 * w;
        var split = new List<WirePiece>();
        for (int j = 0; j < segs.Count; j++)
        {
            var s = segs[j];
            double len = s.Length;
            var at = new List<double>();
            for (int i = 0; i < segs.Count; i++)
            {
                if (i == j) continue;
                foreach (var p in new[] { segs[i].A, segs[i].B })
                {
                    var (along, perp) = Local(s.A, s.B, p);
                    if (Math.Abs(perp) <= Math.Max(w, 1.5) && along >= tol && along <= len - tol) at.Add(along);
                }
            }
            if (at.Count == 0) { split.Add(s); continue; }
            at.Sort();
            double ux = (s.B.x - s.A.x) / len, uy = (s.B.y - s.A.y) / len;
            var prev = s.A;
            bool prevFree = s.FreeA;
            foreach (double t in at.Distinct())
            {
                var q = new PointD(s.A.x + t * ux, s.A.y + t * uy);
                var piece = s.Copy();
                piece.A = prev; piece.FreeA = prevFree; piece.B = q; piece.FreeB = false;
                split.Add(piece);
                prev = q;
                prevFree = false;
            }
            var lastPiece = s.Copy();
            lastPiece.A = prev; lastPiece.FreeA = prevFree;
            split.Add(lastPiece);
        }
        segs = split;

        // Ends clustered: within 1.5 w, or within one junction dot.
        var ends = new List<(int Seg, int End, PointD P)>();
        for (int i = 0; i < segs.Count; i++)
        {
            ends.Add((i, 0, segs[i].A));
            ends.Add((i, 1, segs[i].B));
        }
        var parent = Enumerable.Range(0, ends.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Union(int x, int y) { x = Find(x); y = Find(y); if (x != y) parent[Math.Max(x, y)] = Math.Min(x, y); }
        var byX = Enumerable.Range(0, ends.Count).OrderBy(k => ends[k].P.x).ThenBy(k => k).ToArray();
        for (int i = 0; i < byX.Length; i++)
            for (int j = i + 1; j < byX.Length && ends[byX[j]].P.x - ends[byX[i]].P.x <= tol; j++)
                if (Dist(ends[byX[i]].P, ends[byX[j]].P) <= tol) Union(byX[i], byX[j]);
        foreach (var b in blobs)
        {
            int first = -1;
            for (int k = 0; k < ends.Count; k++)
            {
                if (Dist(ends[k].P, b.Centre) > b.Radius + w) continue;
                if (first < 0) first = k; else Union(first, k);
            }
        }

        var nodeOfRoot = new Dictionary<int, int>();
        var members = new List<List<int>>();
        for (int k = 0; k < ends.Count; k++)
        {
            int r = Find(k);
            if (!nodeOfRoot.TryGetValue(r, out int n))
            {
                nodeOfRoot[r] = n = members.Count;
                members.Add([]);
            }
            members[n].Add(k);
        }
        var from = new int[segs.Count];
        var to = new int[segs.Count];
        foreach (var m in members)
        {
            var node = new ANode { P = Meet(m.Select(k => (segs[ends[k].Seg], ends[k].P)).ToList()) };
            foreach (int k in m)
            {
                var s = segs[ends[k].Seg];
                node.Free &= ends[k].End == 0 ? s.FreeA : s.FreeB;
                if (ends[k].End == 0) from[ends[k].Seg] = result.Nodes.Count; else to[ends[k].Seg] = result.Nodes.Count;
            }
            double reach = Math.Max(o.DotCentre * w, 1.5);
            node.Dot = blobs.Any(b => Dist(b.Centre, node.P) <= reach + b.Radius * 0.5);
            result.Nodes.Add(node);
        }
        for (int i = 0; i < segs.Count; i++)
            if (from[i] != to[i] || segs[i].Length > tol) result.Edges.Add(new AEdge(from[i], to[i], segs[i]));

        if (dissolve) Dissolve(result, o);
        return result;
    }

    /// <summary>Joins the two wires of every node that has exactly two, collinear.</summary>
    private static void Dissolve(Assembled a, SchematicImageOptions o)
    {
        double cosSnap = Math.Cos(Math.Max(o.SnapAngleDeg, 3.0) * Math.PI / 180);
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int n = 0; n < a.Nodes.Count; n++)
            {
                var at = a.EdgesAt(n);
                if (at.Count != 2 || at[0] == at[1]) continue;
                if (!a.Straight(at[0], at[1], n, cosSnap)) continue;
                var (e1, e2) = (a.Edges[at[0]], a.Edges[at[1]]);
                int f = e1.From == n ? e1.To : e1.From, t = e2.From == n ? e2.To : e2.From;
                if (f == t) continue;
                var p = e1.Piece.Copy();
                p.Width = (e1.Piece.Width + e2.Piece.Width) / 2;
                p.Residual = Math.Max(e1.Piece.Residual, e2.Piece.Residual);
                if (e1.Piece.Orientation != e2.Piece.Orientation) p.Orientation = WireOrientation.Diagonal;
                p.Dashed = e1.Piece.Dashed || e2.Piece.Dashed;
                p.Sources = [.. e1.Piece.Sources.Concat(e2.Piece.Sources).Distinct().OrderBy(s => s)];
                a.Edges.RemoveAt(Math.Max(at[0], at[1]));
                a.Edges.RemoveAt(Math.Min(at[0], at[1]));
                a.Edges.Add(new AEdge(f, t, p));
                changed = true;
            }
        }
        // Drop the nodes nothing ends on any more, keeping the order of the rest.
        var keep = new int[a.Nodes.Count];
        Array.Fill(keep, -1);
        var nodes = new List<ANode>();
        for (int n = 0; n < a.Nodes.Count; n++)
            if (a.Edges.Any(e => e.From == n || e.To == n)) { keep[n] = nodes.Count; nodes.Add(a.Nodes[n]); }
        var edges = a.Edges.Select(e => e with { From = keep[e.From], To = keep[e.To] }).ToList();
        a.Nodes.Clear(); a.Nodes.AddRange(nodes);
        a.Edges.Clear(); a.Edges.AddRange(edges);
    }

    /// <summary>Where the wires ending at one node meet: the least-squares point of their lines, or — when they are all
    /// parallel — the mean of the ends projected onto them.</summary>
    private static PointD Meet(List<(WirePiece S, PointD End)> ends)
    {
        double mx = ends.Average(e => e.End.x), my = ends.Average(e => e.End.y);
        double sxx = 0, sxy = 0, syy = 0, bx = 0, by = 0;
        foreach (var (s, _) in ends)
        {
            double dx = s.B.x - s.A.x, dy = s.B.y - s.A.y, l = Math.Sqrt(dx * dx + dy * dy);
            if (l < 1e-9) continue;
            double nx = -dy / l, ny = dx / l, c = nx * (s.A.x - mx) + ny * (s.A.y - my);
            sxx += nx * nx; sxy += nx * ny; syy += ny * ny;
            bx += nx * c; by += ny * c;
        }
        double det = sxx * syy - sxy * sxy, tr = sxx + syy;
        if (tr > 1e-12 && det > 1e-3 * tr * tr)
            return new PointD(mx + (syy * bx - sxy * by) / det, my + (sxx * by - sxy * bx) / det);
        // Parallel: move the mean along the one normal they share.
        if (tr > 1e-12)
        {
            double nx = Math.Sqrt(sxx / tr), ny = Math.Sqrt(syy / tr) * (sxy < 0 ? -1 : 1);
            double c = (nx * bx + ny * by) / tr;
            return new PointD(mx + c * nx, my + c * ny);
        }
        return new PointD(mx, my);
    }

    private static (int Seg, int End)? NearestEnd(List<WirePiece> segs, PointD p, double within)
    {
        (int, int)? best = null;
        double bd = within;
        for (int i = 0; i < segs.Count; i++)
        {
            double da = Dist(segs[i].A, p), db = Dist(segs[i].B, p);
            if (da <= bd) { bd = da; best = (i, 0); }
            if (db < bd) { bd = db; best = (i, 1); }
        }
        return best;
    }

    // ── Geometry ────────────────────────────────────────────────────────────────────────────────────────────────

    internal static double Dist(PointD a, PointD b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

    /// <summary><paramref name="p"/> in the frame of segment a→b: distance along from a, and signed distance across.</summary>
    internal static (double Along, double Perp) Local(PointD a, PointD b, PointD p)
    {
        double ux = b.x - a.x, uy = b.y - a.y, l = Math.Sqrt(ux * ux + uy * uy);
        if (l < 1e-12) return (0, Dist(a, p));
        ux /= l; uy /= l;
        double dx = p.x - a.x, dy = p.y - a.y;
        return (dx * ux + dy * uy, -dx * uy + dy * ux);
    }

    internal static double DistToSegment(PointD p, PointD a, PointD b)
    {
        double l = Dist(a, b);
        if (l < 1e-12) return Dist(a, p);
        var (along, perp) = Local(a, b, p);
        if (along < 0) return Dist(a, p);
        if (along > l) return Dist(b, p);
        return Math.Abs(perp);
    }

    private static PointD Project(SegmentFit f, PointD p)
    {
        double ux = f.B.x - f.A.x, uy = f.B.y - f.A.y, l = Math.Sqrt(ux * ux + uy * uy);
        if (l < 1e-12) return f.A;
        ux /= l; uy /= l;
        double t = (p.x - f.Cx) * ux + (p.y - f.Cy) * uy;
        return new PointD(f.Cx + t * ux, f.Cy + t * uy);
    }

    private static double Turn(PointD a, PointD b, PointD c)
    {
        double a1 = Math.Atan2(b.y - a.y, b.x - a.x), a2 = Math.Atan2(c.y - b.y, c.x - b.x);
        double d = Math.Abs(a2 - a1);
        return d > Math.PI ? 2 * Math.PI - d : d;
    }
}

/// <summary>A straight run of the skeleton while the wires are being read.</summary>
internal sealed class WirePiece
{
    public PointD A, B;

    /// <summary>The skeleton ends at that end — the stroke stops in bare paper.</summary>
    public bool FreeA, FreeB;

    public double Width, Residual;

    /// <summary>The fitted direction before any snapping, 0 … π, y down.</summary>
    public double Angle;

    public WireOrientation Orientation;
    public bool IsWire;

    /// <summary>Cut from a small round loop of the skeleton — a terminal's circle — and so never a wire.</summary>
    public bool RoundLoop;

    /// <summary>A short side of a box (R-imf3-2): looked at by the cycle search only, never a wire.</summary>
    public bool Side;

    /// <summary>Stands for a dashed line in line with a wire (R-imf3-4).</summary>
    public bool Dashed;

    /// <summary>The runs it was made from (indices into the first list of pieces).</summary>
    public List<int> Sources = [];

    public double Length => WireGraph.Dist(A, B);

    public WirePiece Copy()
    {
        var c = (WirePiece)MemberwiseClone();
        c.Sources = [.. Sources];
        return c;
    }
}

/// <summary>A filled blob on the wires — a junction dot when it sits on a node of three wires or more.</summary>
internal sealed record JunctionBlob(PointD Centre, double Radius);

/// <summary>A hop: the wire ends at <paramref name="End1"/> and <paramref name="End2"/> are one wire, crossing the
/// other at <paramref name="At"/> without connecting to it.</summary>
internal sealed record HopJoin(PointD End1, PointD End2, PointD At)
{
    /// <summary>A pin marker's two halves (R-imf8-4): one wire through an open circle on a lead, crossing nothing.</summary>
    public bool Marker { get; init; }
}

/// <summary>A closed outline's box, read pixels; <paramref name="Orthogonal"/>: drawn only across and down (a box, not a
/// triangle or a diamond).</summary>
internal sealed record OutlineBox(double X0, double Y0, double X1, double Y1, bool Orthogonal);

/// <summary>A line with two bare ends, at most one lead and no crossing: a plate, or one of a pair (R-imf3-1).</summary>
internal sealed record PlateLine(PointD A, PointD B, IReadOnlyList<int> Sources, bool Plate)
{
    /// <summary>Only one of its ends is bare: a plate only beside a plate.</summary>
    public bool OneBare { get; init; }
}

/// <summary>What the stroke rules of R-imf3-1 and R-imf3-2 need — the symbol scale, whether an end is bare and where
/// the ink is — and what they found, for the report (R-imf3-7).</summary>
internal sealed class PlateRules
{
    /// <summary>The symbol scale s, read pixels.</summary>
    public double Symbol { get; init; }

    /// <summary>Whether a line's end at a point stops in bare paper; null for the skeleton's verdict.</summary>
    public Func<PointD, bool?>? Bare { get; init; }

    /// <summary>Whether a line's end (the first point) stops at a word clear of its middle (the second): one with a word
    /// off both ends is a labelled wire. A word lying along the line — a ground's lower bars read as type — is not.</summary>
    public Func<PointD, PointD, bool>? Labelled { get; init; }

    /// <summary>Whether a point is ink — a pair's strip must be clear.</summary>
    public Func<PointD, bool>? Ink { get; init; }

    /// <summary>The short sides of heavy boxes (WirePiece.Side): in the cycle search, never wire.</summary>
    public IReadOnlyList<WirePiece> Sides { get; init; } = [];

    public int Plates, PlatePairs, CurvedPlates, Boxes;

    /// <summary>Loops of wire candidates read as wire, not an outline: a junction dot stands on them (R-imf4-2).</summary>
    public int MeshFaces;

    /// <summary>The closed outlines set aside as symbols, read pixels (R-imf4-2).</summary>
    internal readonly List<OutlineBox> Outlines = [];

    internal readonly List<PlateLine> Lines = [];
}
