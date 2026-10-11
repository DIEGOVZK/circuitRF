// A schematic picture's wiring drawn as the picture draws it.
//
// NetlistSchematic places the instances where the picture has them (ExactPlacement.PlaceOnly); this draws everything
// else from the reading itself rather than from the netlist: every wire segment the picture has — the one running off
// the edge to nothing included — with its corners where the picture puts them, a ground symbol where the picture draws
// one, and a net label only where the picture writes a net's name. Nothing is invented: a netlist redrawn by a router
// labels every net and adds a port at every loose end, which is a different picture of the same circuit, and a paste
// should look like what was pasted.
//
// The geometry is the wire graph's, made orthogonal on the grid without changing its topology: every node a vertical
// segment joins shares one x, every node a horizontal segment joins shares one y, and a node a part's pin stands on
// takes that pin's coordinate, so a wire meets its pin exactly. A node at a crossing that does not connect is passed
// straight through, so the two wires cross mid-segment and meet nowhere — as on the picture.

using System.Globalization;
using CircuitRF.Core.Design;

namespace CircuitRF.Design.Schematic.Recognition;

public static class SchematicImageDrawing
{
    private const double P = NetlistSchematic.Grid;

    private static double Snap(double v) => Math.Round(v / P) * P;

    /// <summary>
    /// Adds the picture's wires, ground symbols and net names to <paramref name="model"/>, whose instances are already
    /// placed. Returns what a reader should hear: a drawing that would join two nets the picture keeps apart, say.
    /// </summary>
    public static IReadOnlyList<string> Wire(SchematicEditModel model, SchematicImageRead read, SchematicImageCircuit circuit)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(circuit);
        var notes = new List<string>();
        var graph = read.Wires;
        var frame = circuit.Frame;
        int N = graph.Nodes.Count;
        (double X, double Y) At(int node)
        {
            var (sx, sy) = read.ToSource(graph.Nodes[node].X, graph.Nodes[node].Y);
            return frame.ToSchematic(sx, sy);
        }

        // ── the pins, where the placed parts put them ───────────────────────────────────────────────
        var pinAt = new Dictionary<int, List<(double X, double Y)>>();
        var unwired = new List<(double X, double Y, string Net, double OutX, double OutY)>();
        foreach (var inst in circuit.TestBench.Instances)
        {
            if (model.Components.FirstOrDefault(c => c.InstanceName == inst.InstanceName) is not { } comp) continue;
            if (NetlistSchematic.PinOffsets(inst, comp.Rotation, comp.MirrorX) is not { Count: > 0 } offsets) continue;
            var nodes = circuit.PinNodes.TryGetValue(inst.InstanceName, out var pn) ? pn : [];
            double cx = offsets.Average(o => o.X), cy = offsets.Average(o => o.Y);
            for (int k = 0; k < offsets.Count; k++)
            {
                var at = (X: comp.X + offsets[k].X, Y: comp.Y + offsets[k].Y);
                int node = k < nodes.Count ? nodes[k] : -1;
                if (node >= 0 && node < N)
                {
                    if (!pinAt.TryGetValue(node, out var l)) pinAt[node] = l = [];
                    l.Add(at);
                }
                else unwired.Add((at.X, at.Y, offsets[k].Net, offsets[k].X - cx, offsets[k].Y - cy));
            }
        }

        // ── the nodes, on the grid, each segment orthogonal ─────────────────────────────────────────
        var xs = new Dsu(N);
        var ys = new Dsu(N);
        var segments = graph.Segments.Where(s => !circuit.HiddenSegments.Contains(s.Id)).ToList();
        foreach (var s in segments)
            if (s.Orientation == WireOrientation.Vertical) xs.Union(s.From, s.To);
            else if (s.Orientation == WireOrientation.Horizontal) ys.Union(s.From, s.To);
        var raw = Enumerable.Range(0, N).Select(At).ToArray();
        var pos = new (double X, double Y)[N];
        foreach (var g in Enumerable.Range(0, N).GroupBy(xs.Find))
        {
            double x = g.FirstOrDefault(pinAt.ContainsKey, -1) is var pinned and >= 0 ? pinAt[pinned][0].X : Snap(g.Average(n => raw[n].X));
            foreach (int n in g) pos[n].X = x;
        }
        foreach (var g in Enumerable.Range(0, N).GroupBy(ys.Find))
        {
            double y = g.FirstOrDefault(pinAt.ContainsKey, -1) is var pinned and >= 0 ? pinAt[pinned][0].Y : Snap(g.Average(n => raw[n].Y));
            foreach (int n in g) pos[n].Y = y;
        }

        // ── the wires: runs through plain bends, and straight through a crossing that does not connect ──
        var through = new Dictionary<(int Node, int Seg), int>();
        foreach (var c in graph.Crossings.Where(c => !c.Connected && c.Node is not null))
        {
            var at = graph.Segments.Where(s => s.From == c.Node || s.To == c.Node).ToList();
            foreach (var a in at)
                foreach (var b in at)
                    if (a.Id < b.Id && a.Orientation == b.Orientation && a.Orientation != WireOrientation.Diagonal
                        && graph.Net(a.Id) == graph.Net(b.Id))
                    {
                        through[(c.Node!.Value, a.Id)] = b.Id;
                        through[(c.Node!.Value, b.Id)] = a.Id;
                    }
        }
        var stops = new HashSet<int>(pinAt.Keys);
        foreach (var (node, _) in circuit.NamedNodes) stops.Add(node);
        foreach (var (node, _, _) in circuit.Grounds) stops.Add(node);
        var segsAt = new List<int>[N];
        for (int n = 0; n < N; n++) segsAt[n] = [];
        foreach (var s in segments) { segsAt[s.From].Add(s.Id); segsAt[s.To].Add(s.Id); }
        int? Next(int node, int seg)
        {
            if (through.TryGetValue((node, seg), out int t)) return t;
            if (stops.Contains(node) || segsAt[node].Count != 2) return null;
            return segsAt[node][0] == seg ? segsAt[node][1] : segsAt[node][0];
        }

        var wires = new List<EditableWire>();
        var done = new bool[graph.Segments.Count];
        foreach (int h in circuit.HiddenSegments) if (h >= 0 && h < done.Length) done[h] = true;
        foreach (var start in segments)
        {
            if (done[start.Id]) continue;
            // Walk back to the run's first segment, then forward to its last.
            int seg = start.Id, node = start.From;
            for (int guard = 0; guard < graph.Segments.Count && Next(node, seg) is { } prev && prev != start.Id; guard++)
            {
                var ps = graph.Segments[prev];
                node = ps.From == node ? ps.To : ps.From;
                seg = prev;
            }
            var s0 = graph.Segments[seg];
            int from = s0.From == node ? s0.From : s0.To;
            var points = new List<(double X, double Y)> { pos[from] };
            int at = from;
            for (int guard = 0; guard <= graph.Segments.Count && seg >= 0 && !done[seg]; guard++)
            {
                done[seg] = true;
                var s = graph.Segments[seg];
                int to = s.From == at ? s.To : s.From;
                if (s.Orientation == WireOrientation.Diagonal && pos[at].X != pos[to].X && pos[at].Y != pos[to].Y)
                    points.Add((pos[to].X, pos[at].Y));
                points.Add(pos[to]);
                at = to;
                seg = Next(at, seg) is { } nx ? nx : -1;
            }
            if (Simplify(points) is { Count: >= 2 } run)
            {
                var w = new EditableWire();
                w.Points.AddRange(run);
                wires.Add(w);
            }
        }

        // A pin its node could not be put on (two pins on one node, or a run they pull two ways): an L from the node.
        foreach (var (node, pins) in pinAt)
            foreach (var pin in pins)
                if (pin != pos[node] && Simplify([pos[node], (pin.X, pos[node].Y), pin]) is { Count: >= 2 } leg)
                {
                    var w = new EditableWire();
                    w.Points.AddRange(leg);
                    wires.Add(w);
                }

        // ── the picture's grounds, and a ground on every grounded pin no wire reaches ───────────────
        int grounds = 0;
        var grounded = new HashSet<(double, double)>();
        foreach (var (node, dx, dy) in circuit.Grounds)
        {
            if (node < 0 || node >= N || !grounded.Add(pos[node])) continue;
            var (ux, uy) = Axis(dx, dy);
            model.Components.Add(NetlistSchematic.NewGround(pos[node].X, pos[node].Y, ux, uy, ++grounds));
        }
        var nodesOf = Enumerable.Range(0, N).Where(circuit.NodeNets.ContainsKey).GroupBy(n => circuit.NodeNets[n])
                                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var (x, y, net, ox, oy) in unwired)
        {
            if (net == "0")
            {
                if (!grounded.Add((x, y))) continue;
                var (ux, uy) = Axis(ox, oy);
                model.Components.Add(NetlistSchematic.NewGround(x, y, ux, uy, ++grounds));
            }
            else if (nodesOf.TryGetValue(net, out var nodes))
            {
                var near = pos[nodes.MinBy(n => Math.Abs(pos[n].X - x) + Math.Abs(pos[n].Y - y))];
                if (Simplify([(x, y), (near.X, y), near]) is { Count: >= 2 } leg)
                {
                    var w = new EditableWire();
                    w.Points.AddRange(leg);
                    wires.Add(w);
                }
            }
        }
        model.Wires.AddRange(wires);

        // ── the names the picture writes ────────────────────────────────────────────────────────────
        foreach (var (node, net) in circuit.NamedNodes)
        {
            if (node < 0 || node >= N) continue;
            var label = new EditableNetLabel { Name = net, X = pos[node].X, Y = pos[node].Y };
            if (wires.FirstOrDefault(w => OnPolyline(w.Points, pos[node])) is { } owner) label.AnchorToWire(owner, pos[node].X, pos[node].Y);
            model.NetLabels.Add(label);
        }

        // ── the drawing is the circuit, or the report says where it is not ──────────────────────────
        if (Disagreement(model, circuit.TestBench) is { Count: > 0 } wrong)
            notes.Add($"Drawn on the grid, {string.Join(", ", wrong)} {(wrong.Count == 1 ? "touches" : "touch")} a wire of another net " +
                      "where the picture keeps them apart; check the drawing there.");
        return notes;
    }

    /// <summary>The instances whose pins the drawing connects differently from the circuit.</summary>
    private static List<string> Disagreement(SchematicEditModel model, TestBench want)
    {
        TestBench got;
        try { got = NetExtractor.Extract(model).TestBench; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return []; }
        static IReadOnlyList<string> Pins(Instance i) => i.RefNetBinding is { } r ? [.. i.NetBindings, r] : i.NetBindings;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var back = new Dictionary<string, string>(StringComparer.Ordinal);
        var wrong = new List<string>();
        foreach (var w in want.Instances)
        {
            if (got.Instances.FirstOrDefault(i => i.InstanceName == w.InstanceName) is not { } g) continue;
            var a = Pins(w);
            var b = Pins(g);
            for (int k = 0; k < Math.Min(a.Count, b.Count); k++)
            {
                bool ok = (map.TryGetValue(a[k], out var m) ? m == b[k] : !back.ContainsKey(b[k]))
                       && (back.TryGetValue(b[k], out var n) ? n == a[k] : true);
                if (!ok) { if (!wrong.Contains(w.InstanceName)) wrong.Add(w.InstanceName); continue; }
                map[a[k]] = b[k];
                back[b[k]] = a[k];
            }
        }
        return wrong;
    }

    /// <summary>Repeated points and straight-through vertices dropped.</summary>
    private static List<(double X, double Y)> Simplify(List<(double X, double Y)> points)
    {
        var r = new List<(double X, double Y)>();
        foreach (var p in points)
        {
            if (r.Count > 0 && r[^1] == p) continue;
            if (r.Count >= 2)
            {
                var (a, b) = (r[^2], r[^1]);
                bool collinear = (a.X == b.X && b.X == p.X) || (a.Y == b.Y && b.Y == p.Y);
                bool forward = (b.X - a.X) * (p.X - b.X) + (b.Y - a.Y) * (p.Y - b.Y) > 0;
                if (collinear && forward) r[^1] = p;
                else r.Add(p);
                continue;
            }
            r.Add(p);
        }
        return r;
    }

    private static bool OnPolyline(IReadOnlyList<(double X, double Y)> pts, (double X, double Y) p)
    {
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var (a, b) = (pts[i], pts[i + 1]);
            if (a.X == b.X && p.X == a.X && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y)) return true;
            if (a.Y == b.Y && p.Y == a.Y && p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X)) return true;
        }
        return false;
    }

    private static (int X, int Y) Axis(double dx, double dy) =>
        Math.Abs(dx) > Math.Abs(dy) ? (Math.Sign(dx), 0) : (0, dy < 0 ? -1 : 1);

    private sealed class Dsu(int n)
    {
        private readonly int[] _p = Enumerable.Range(0, n).ToArray();

        public int Find(int x)
        {
            while (_p[x] != x) x = _p[x] = _p[_p[x]];
            return x;
        }

        public void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a != b) _p[Math.Max(a, b)] = Math.Min(a, b);
        }
    }
}
