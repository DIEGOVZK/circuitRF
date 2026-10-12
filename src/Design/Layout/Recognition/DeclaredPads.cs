// The netlist a Gerber set declares — brief-artsch-12-declared-pads-netlist.md R-as12-1 … R-as12-6.
//
// A pad flash or region may carry `%TO.P,<refdes>,<pin>[,<pin name>]*%` and `%TO.N,<net>*%`; the import
// keeps both on the shape (LayoutShape.Pin = "C1,1" or "J1,1,In", LayoutShape.Net = "GND"). Pads grouped
// by the refdes their Pin names are one part each, with the declared pins as its terminals in pin order.
// The file says which pads form a part, which no geometry can match, and says nothing of kind or value —
// so it ranks after a placed instance and a placement row and ahead of a bill of materials, land patterns
// and the silkscreen.
//
// THE DECLARED NETS ARE A CHECK, NEVER THE CONNECTIVITY. The copper decides what joins what (AS-3); two
// declared nets on one island, or one net on two islands, is reported where it happens.

using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>One pad the artwork declares.</summary>
/// <param name="Refdes">The part it is a pad of.</param>
/// <param name="Pin">Its pin number, as declared.</param>
/// <param name="Name">Its pin name — the optional third field — or null.</param>
/// <param name="Net">The net it declares, or null.</param>
/// <param name="X">The centre of its copper, DBU.</param>
/// <param name="Y">DBU.</param>
/// <param name="Layer">The drawing layer of its copper.</param>
/// <param name="Bounds">Its copper's bounding box, DBU.</param>
public sealed record DeclaredPad(string Refdes, string Pin, string? Name, string? Net, long X, long Y, LayerKey Layer, Bbox Bounds)
{
    /// <summary>The pad as every other reader of pads takes one.</summary>
    public PlacedPin ToPlaced() => new(Refdes, Pin, Net, X, Y, PinSource.Artwork) { Layer = Layer };
}

/// <summary>One part the artwork declares: its pads in pin order.</summary>
public sealed record DeclaredPart(string Refdes, IReadOnlyList<DeclaredPad> Pads)
{
    /// <summary>The union of its pads' copper.</summary>
    public Bbox Body => Pads.Aggregate(Bbox.Empty, (b, p) => b.Union(p.Bounds));
}

/// <summary>R-as12-1: reads the parts a Gerber set's pad attributes declare.</summary>
public static class DeclaredPads
{
    /// <summary>
    /// Every part the copper's pad attributes declare, in natural refdes order, each with its pads in pin order.
    /// A pin declared on several copper layers (a through-hole pad) is its topmost layer's copper; shapes of one pin
    /// that touch on that layer are one pad, and ones that do not are pads of their own — a connector's ground tabs
    /// all declare one pin.
    /// </summary>
    public static IReadOnlyList<DeclaredPart> Read(IEnumerable<LayoutShape> shapes, Technology tech)
    {
        var rank = Conductors.Of(tech).SelectMany(c => c.DrawingLayers).Distinct()
                             .Select((l, r) => (l, r)).ToDictionary(t => t.l, t => t.r);
        var declared = new Dictionary<(string Refdes, string Pin), List<(string? Name, string? Net, LayerKey Layer, Bbox Box)>>();
        foreach (var shape in shapes)
        {
            if (shape is ViaShape or LabelShape or BitmapShape || !rank.ContainsKey(shape.Layer)) continue;
            if (Parse(shape.Pin, shape.Component) is not { } id) continue;
            var key = (id.Refdes, id.Pin);
            if (!declared.TryGetValue(key, out var list)) declared[key] = list = [];
            list.Add((id.Name, shape.Net, shape.Layer, LayoutGeometry.BboxOf(shape)));
        }

        var pads = new List<DeclaredPad>();
        foreach (var ((refdes, pin), list) in declared)
        {
            int top = list.Min(s => rank[s.Layer]);
            var clusters = new List<(string? Name, string? Net, LayerKey Layer, Bbox Box)>();
            foreach (var s in list.Where(s => rank[s.Layer] == top))
            {
                var joined = s;
                for (int k = clusters.Count - 1; k >= 0; k--)
                    if (clusters[k].Box.Intersects(joined.Box))
                    {
                        var c = clusters[k];
                        joined = (joined.Name ?? c.Name, joined.Net ?? c.Net, joined.Layer, joined.Box.Union(c.Box));
                        clusters.RemoveAt(k);
                    }
                clusters.Add(joined);
            }
            foreach (var c in clusters.OrderBy(c => c.Box.MinX).ThenByDescending(c => c.Box.MaxY))
                pads.Add(new DeclaredPad(refdes, pin, c.Name, c.Net, (c.Box.MinX + c.Box.MaxX) / 2, (c.Box.MinY + c.Box.MaxY) / 2,
                                         c.Layer, c.Box));
        }

        return [.. pads.GroupBy(p => p.Refdes, StringComparer.OrdinalIgnoreCase)
                       .OrderBy(g => g.Key, PartsTable.NaturalOrder)
                       .Select(g => new DeclaredPart(g.Key, [.. g.OrderBy(p => p.Pin, PartsTable.NaturalOrder)]))];
    }

    /// <summary>
    /// <c>C1,1</c> or <c>J1,1,In</c> as refdes, pin and name; a bare pin with the shape's <c>%TO.C</c> as its refdes.
    /// Null where nothing usable is declared.
    /// </summary>
    internal static (string Refdes, string Pin, string? Name)? Parse(string? pin, string? component)
    {
        if (pin is null) return null;
        var fields = pin.Split(',').Select(f => f.Trim()).ToArray();
        if (fields.Length == 1 && component is { Length: > 0 } c && fields[0].Length > 0) return (c.Trim(), fields[0], null);
        if (fields.Length < 2 || fields[0].Length == 0 || fields[1].Length == 0) return null;
        return (fields[0], fields[1], fields.Length > 2 && fields[2].Length > 0 ? string.Join(",", fields[2..]) : null);
    }

    /// <summary>
    /// R-as12-5: each declared pad's net against the copper it lands on. Ground copper and every island in
    /// <paramref name="onGround"/> — joined to it by a via — are one node. A node carrying two declared nets is a short (or an import artefact); a net on two
    /// nodes is an open. Agreement is one line.
    /// </summary>
    internal static void CrossCheck(IReadOnlyList<DeclaredPad> pads, BoardCopper board, int[] islandOf, bool[] onGround,
                                    RecognitionReport report, RailLengthFormat fmt)
    {
        var netsOn = new Dictionary<int, List<DeclaredPad>>();
        var nodesOf = new Dictionary<string, Dictionary<int, DeclaredPad>>(StringComparer.Ordinal);
        int checkedPads = 0;
        foreach (var pad in pads)
        {
            if (pad.Net is not { Length: > 0 } net) continue;
            int piece = board.ConductorAt(pad.X, pad.Y, pad.Layer);
            if (piece < 0) continue;
            int island = piece < islandOf.Length ? islandOf[piece] : -1;
            int node = island < 0 || onGround[island] ? -1 : island;
            checkedPads++;
            if (!netsOn.TryGetValue(node, out var on)) netsOn[node] = on = [];
            on.Add(pad);
            if (!nodesOf.TryGetValue(net, out var nodes)) nodesOf[net] = nodes = [];
            nodes.TryAdd(node, pad);
        }
        if (checkedPads == 0) return;

        var shorts = new List<(string Sentence, RecognitionAnchor[] At)>();
        foreach (var (node, on) in netsOn)
        {
            var nets = on.Select(p => p.Net!).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (nets.Count < 2) continue;
            var first = nets.Select(n => on.First(q => q.Net == n)).ToList();
            string where = node < 0 ? "ground copper" : "one copper island";
            shorts.Add(($"{string.Join(" and ", nets)} meet on {where} ({string.Join(", ", first.Select(p => $"{p.Refdes}.{p.Pin} at {fmt.Point(p.X, p.Y)}"))})",
                        [.. first.Select(At)]));
        }
        var opens = new List<(string Sentence, RecognitionAnchor[] At)>();
        foreach (var (net, nodes) in nodesOf.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (nodes.Count < 2) continue;
            var at = nodes.Values.ToList();
            opens.Add(($"{net} is on {nodes.Count} separate pieces of copper ({string.Join(", ", at.Select(p => $"{p.Refdes}.{p.Pin} at {fmt.Point(p.X, p.Y)}"))})",
                       [.. at.Select(At)]));
        }

        report.Add(RecognitionFindingClass.DeclaredNetsShorted, shorts.Count,
            $"{Plural(shorts.Count, "copper node carries", "copper nodes carry")} two nets the artwork declares — a short, or an import " +
            $"artefact; the copper's connection was used: {string.Join("; ", shorts.Take(8).Select(s => s.Sentence))}{(shorts.Count > 8 ? "; …" : "")}.",
            [.. shorts.SelectMany(s => s.At)]);
        report.Add(RecognitionFindingClass.DeclaredNetsOpen, opens.Count,
            $"{Plural(opens.Count, "net the artwork declares is", "nets the artwork declares are")} split across copper that does not " +
            $"join — an open; the copper's connection was used: {string.Join("; ", opens.Take(8).Select(s => s.Sentence))}{(opens.Count > 8 ? "; …" : "")}.",
            [.. opens.SelectMany(s => s.At)]);
        if (shorts.Count == 0 && opens.Count == 0)
            report.Add(RecognitionFindingClass.DeclaredNetsAgree, nodesOf.Count,
                $"The {Plural(nodesOf.Count, "net", "nets")} the artwork's pads declare agree with the copper ({Plural(checkedPads, "pad", "pads")} checked).");

        static RecognitionAnchor At(DeclaredPad p) => new(p.X, p.Y, p.Layer);
    }

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
