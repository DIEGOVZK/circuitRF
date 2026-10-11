// A schematic picture's reading made into a circuit — brief-img-10-schematic-emit.md R-im10-2, R-im10-3, R-im10-4,
// R-im10-7 (overview D9, D10, D11, D12, D13).
//
// IM-7's wires, IM-8's symbols and IM-9's words become ONE TestBench — the type CnlReader produces, so the drawing
// (NetlistSchematic.Build) and the .cnl text are two spellings of the same circuit and neither is a second writer:
//
//   nets       the wire graph's nets (crossings per D11, the user's corrections over them), joined by equal net
//              labels and equal supply names; a ground symbol's net is 0; a dangling end the user dragged onto a pin
//              joins that pin's net;
//   parts      every R, L, C and `?` through the PARTS TABLE (pixel rows, R-im10-7) — so an edited CSV is laid over
//              them exactly as over artwork's — `?` generated as a C; an unread value a global variable
//              <Refdes>_<Param> at AS D10's transparent start with a tune entry;
//   lines      a transmission line TLIN from its line set: Z and E, or the physical form with L and the technology's
//              εeff when the set gives a length; a stub a TLIN to ground (short) or to nothing (open); each unread
//              parameter a variable like any other value;
//   ports      D13 in priority: port symbols, then port words naming a net, else dangling ends at the drawing's left
//              and right extremes; a cut-out device's wired pins after them (AS D9);
//   analysis   AS D15's SP1 when there is a port — none, and the report says so, when there is not.
//
// The drawing's EXACT hints come from the same pass (D12): each instance at the frame position of its pins, in the
// orientation that lays its symbol's pins on them, and each net along the picture's own wire segments.

using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>The source picture's pixels carried to schematic units, both y down: <c>x' = Scale·x + OffsetX</c>.</summary>
public readonly record struct SchematicFrame(double Scale, double OffsetX, double OffsetY)
{
    public (double X, double Y) ToSchematic(double x, double y) => (Scale * x + OffsetX, Scale * y + OffsetY);

    /// <summary>The frame a placed schematic bitmap defines (D14): its rect sets the scale and the offset.</summary>
    public static SchematicFrame Of(Imaging.SchematicBitmapPlacement placed, int pictureWidth) =>
        new(placed.Width / Math.Max(1, pictureWidth), placed.X - placed.Width / 2, placed.Y - placed.Height / 2);
}

/// <summary>How a port was found (D13), strongest first.</summary>
public enum PicturePortSource { Symbol, Name, DanglingEnd, CutOut }

/// <summary>One port of the circuit.</summary>
/// <param name="X">Where it is on the source picture, pixels.</param>
public sealed record PicturePort(int Number, string Name, string Net, PicturePortSource Source, double X, double Y);

/// <summary>What the emit may be told beyond the reading.</summary>
public sealed record SchematicEmitOptions
{
    /// <summary>The sweep and the significant figures — the artwork emit's own options (D13: AS D15's analysis).</summary>
    public RecognitionEmitOptions Emit { get; init; } = new();

    /// <summary>The technology a TLIN's εeff is read from, or null.</summary>
    public Technology? Technology { get; init; }

    /// <summary>Its file — the TestBench's technology reference.</summary>
    public string? TechnologyPath { get; init; }

    /// <summary>An edited parts table, as text (wins) or on disk.</summary>
    public string? PartsCsvText { get; init; }
    public string? PartsCsvPath { get; init; }

    /// <summary>The frame the drawing is made at; null: the one where the median pin span is the symbols' own.</summary>
    public SchematicFrame? Frame { get; init; }
}

/// <summary>The circuit a schematic picture states, and what its drawing needs beside it.</summary>
public sealed record SchematicImageCircuit(TestBench TestBench)
{
    /// <summary>The drawing as the picture draws it (D12).</summary>
    public ExactPlacement Exact { get; init; } = new();

    /// <summary>Source pixels → schematic units: where the picture lies under the drawing.</summary>
    public SchematicFrame Frame { get; init; }

    /// <summary>The parts table, pixel rows, with any edited table laid over it.</summary>
    public PartsTable Parts { get; init; } = PartsTable.Empty with { Pixels = true };

    public IReadOnlyList<PicturePort> Ports { get; init; } = [];

    /// <summary>The global variables made for unread values.</summary>
    public IReadOnlyList<string> Variables { get; init; } = [];

    /// <summary>Each instance's symbol on the picture, by its index among the reading's symbols.</summary>
    public IReadOnlyDictionary<string, int> SymbolOf { get; init; } = new Dictionary<string, int>();

    /// <summary>Each parts-table row's symbol, by the row's board designator — the overlay's cross-probe and Learn These
    /// Glyphs' word.</summary>
    public IReadOnlyDictionary<string, int> SymbolOfRow { get; init; } = new Dictionary<string, int>();

    /// <summary>Per instance, the wire node each of its drawn pins stands on (−1: none), in the symbol's pin order — what
    /// the drawing wires the picture's own wires to.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> PinNodes { get; init; } = new Dictionary<string, IReadOnlyList<int>>();

    /// <summary>Each wire node's net by its netlist name (<c>0</c> is ground); a node on wire no part touches is absent.</summary>
    public IReadOnlyDictionary<int, string> NodeNets { get; init; } = new Dictionary<int, string>();

    /// <summary>The picture's ground symbols: the wire node each stands on, and the way its stem points (picture axes).</summary>
    public IReadOnlyList<(int Node, double DirX, double DirY)> Grounds { get; init; } = [];

    /// <summary>Wire segments that are a ground symbol's bars, drawn by the ground symbol rather than as wire.</summary>
    public IReadOnlySet<int> HiddenSegments { get; init; } = new HashSet<int>();

    /// <summary>The wire nodes the picture names — a net label or a supply mark — with the net's name.</summary>
    public IReadOnlyList<(int Node, string Net)> NamedNodes { get; init; } = [];

    /// <summary>The <c>.cnl</c> text, its technology relative to <paramref name="cnlDirectory"/>.</summary>
    public string CnlText(string cnlDirectory, string? header = "recognised from a schematic picture") =>
        new RecognitionCircuit(TestBench).CnlText(cnlDirectory, header);
}

public static class SchematicImageEmit
{
    /// <summary>The refusal when the picture holds no part, port or line at all (D16).</summary>
    public const string NothingRecognisedRefusal =
        "Nothing in the picture was recognised as a part, a port or a line: there is no circuit to make. " +
        "Check the overlay, or read the picture as a layout.";

    /// <summary>The report line of D13's no-port case.</summary>
    public const string NoPortLine = "No port found: add Port components to simulate.";

    private const string Ground = "0";

    private sealed class Proto
    {
        public required string Type { get; init; }
        public required string Name { get; set; }
        public required List<int> Roots { get; init; }          // net roots; -1 a private net (an open stub's end)
        public List<ParameterAssignment> Parameters { get; } = [];
        public List<(double X, double Y, double DirX, double DirY)> PicturePins { get; } = [];   // read pixels
        public List<int> PinNodes { get; } = [];   // the wire node of each picture pin, parallel to PicturePins
        public int? RefRoot { get; set; }          // an S-parameter block's reference pin's net
        public int Symbol { get; init; } = -1;
        public int PortNumber { get; init; }
    }

    /// <summary>
    /// The circuit of a reading. Null, with <paramref name="refusal"/> set, only when nothing was recognised or an
    /// edited parts table could not be read. Every finding goes to <paramref name="report"/>.
    /// </summary>
    public static SchematicImageCircuit? Build(SchematicImageRead read, SchematicSymbolsRead symbols, ImageTextLabels labels,
                                               IReadOnlyList<ConnectionOverride> overrides, SchematicEmitOptions options,
                                               RecognitionReport report, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(labels);
        refusal = null;
        int digits = options.Emit.Digits;
        var graph = read.Wires;
        double w = read.StrokeWidth;
        var syms = symbols.Symbols;
        var lab = new SymbolLabels?[syms.Count];
        foreach (var l in labels.Symbols) if (l.Symbol >= 0 && l.Symbol < syms.Count) lab[l.Symbol] = l;

        // ── nets: the wire graph's, joined ─────────────────────────────────────────────────────────
        int G = graph.NetCount;
        var parent = Enumerable.Range(0, G + 1).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a == b) return;
            if (b == G || (a != G && b < a)) (a, b) = (b, a);   // ground, else the lower, is the root
            parent[b] = a;
        }
        var nodeNet = new int[graph.Nodes.Count];
        Array.Fill(nodeNet, -1);
        foreach (var s in graph.Segments)
        {
            if (nodeNet[s.From] < 0) nodeNet[s.From] = graph.Net(s.Id);
            if (nodeNet[s.To] < 0) nodeNet[s.To] = graph.Net(s.Id);
        }
        int NetOf(int node) => node >= 0 && node < nodeNet.Length && nodeNet[node] >= 0 ? nodeNet[node] : -1;
        int RootOf(int node) => NetOf(node) is var n and >= 0 ? Find(n) : -1;

        foreach (var s in syms.Where(s => s.Disposition == SymbolDisposition.Recognised && s.Kind == ImageSymbolKind.Ground))
            foreach (var p in s.Pins)
                if (NetOf(p.Node) is var n and >= 0) Union(n, G);

        // Grounds drawn in strokes the wire reading took for something else: a bar across the foot of a wire with shorter
        // bars beneath it — what is left of a ground symbol a label is drawn over, as an editor's screenshot can have —
        // and a lone bar under a wire coming down to it that no supply name labels. Their bars are drawn by the ground
        // symbol, not as wire.
        var hidden = new HashSet<int>();
        var groundMarks = new List<(int Node, double DirX, double DirY)>();
        foreach (var (node, bars) in WireGrounds(graph, w))
        {
            foreach (int b in bars) { Union(graph.Net(b), G); hidden.Add(b); }
            groundMarks.Add((node, 0, 1));
        }
        foreach (var m in read.SupplyMarks.Where(m => m.Shape == SupplyShape.Bar && graph.Nodes[m.Node].IsEnd))
        {
            if (graph.Incoming(m.Node).Y < 0.7 || labels.Names.Any(n => n.Node == m.Node && SupplyName(n.Name))) continue;
            if (NetOf(m.Node) is var n and >= 0) Union(n, G);
            groundMarks.Add((m.Node, 0, 1));
        }

        // Equal names join (D11); a supply mark's net is a supply net.
        var supplyNodes = read.SupplyMarks.Select(m => m.Node).ToHashSet();
        foreach (var group in labels.Names.GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            int first = -1;
            foreach (var n in group)
                if (NetOf(n.Node) is var net and >= 0) { if (first < 0) first = net; else Union(first, net); }
        }
        if (labels.Names.Count > 0)
            report.Add(RecognitionFindingClass.PictureNets, labels.Names.Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                $"Net names read: {string.Join(", ", labels.Names.Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))}; " +
                "wires with the same name are one net.");

        // The user's joins (R-im10-8): a dangling end dragged onto a pin.
        int joined = 0;
        var joinsMissed = new List<string>();
        foreach (var o in overrides.Where(o => o.IsJoin))
        {
            var (fx, fy) = read.FromSource(o.X, o.Y);
            var (tx, ty) = read.FromSource(o.ToX!.Value, o.ToY!.Value);
            int from = Nearest(graph.Ends.Select(e => (e.Id, e.X, e.Y)), fx, fy, 4 * w + 4);
            int to = Nearest(syms.SelectMany(s => s.Pins).Select(p => (p.Node, p.X, p.Y)), tx, ty, 4 * w + 4);
            if (to < 0) to = Nearest(graph.Nodes.Select(n => (n.Id, n.X, n.Y)), tx, ty, 4 * w + 4);
            if (from < 0 || to < 0 || NetOf(from) < 0 || NetOf(to) < 0) { joinsMissed.Add(o.ToString()); continue; }
            Union(NetOf(from), NetOf(to));
            joined++;
        }
        int flipped = graph.Crossings.Count(c => c.Overridden);
        int unflipped = overrides.Count(o => !o.IsJoin) - flipped;
        if (joined + flipped > 0)
            report.Add(RecognitionFindingClass.PictureOverrides, joined + flipped,
                $"Corrected by hand: {Plural(flipped, "crossing", "crossings")} set, {Plural(joined, "wire end", "wire ends")} joined to a pin.");
        if (unflipped > 0 || joinsMissed.Count > 0)
            report.Add(RecognitionFindingClass.PictureOverrides, unflipped + joinsMissed.Count,
                $"{Plural(unflipped + joinsMissed.Count, "correction finds", "corrections find")} nothing at its point on this reading " +
                "and changed nothing.");

        // ── the parts table (R-im10-7) ─────────────────────────────────────────────────────────────
        var rows = new List<PartRow>();
        var symbolOfRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < syms.Count; i++)
        {
            var s = syms[i];
            bool twoPin = s.Pins.Count == 2 && s.Kind is ImageSymbolKind.Resistor or ImageSymbolKind.Inductor
                                              or ImageSymbolKind.Capacitor or ImageSymbolKind.Unknown
                          && s.Disposition is SymbolDisposition.Recognised or SymbolDisposition.Unknown;
            if (!twoPin && s.Disposition != SymbolDisposition.CutOut) continue;
            var l = lab[i];
            string refdes = Unique(l?.Designator ?? $"{LabelAssociation.Prefix(s.Kind) ?? "U"}{i + 1}", taken);
            var (x, y) = read.ToSource(s.Region.Box.CentreX, s.Region.Box.CentreY);
            var evidence = new Dictionary<PartField, PartEvidenceSource>
            {
                [PartField.Refdes] = l is { Generated: false } ? PartEvidenceSource.Picture : PartEvidenceSource.Generated,
            };
            var notes = new List<string>();
            if (l is { Contradicts: true }) notes.Add($"{refdes} names a different kind than its symbol is drawn as");
            var terminals = s.Pins.Select(p =>
            {
                var (px, py) = read.ToSource(p.X, p.Y);
                return new PartTerminal((long)Math.Round(px), (long)Math.Round(py), null, -1, RootOf(p.Node) == G);
            }).ToList();

            PartRow row;
            if (!twoPin)
                row = new PartRow
                {
                    Refdes = refdes, Kind = PartKind.MultiPin, Connection = PartConnection.MultiPin, PadCount = s.Pins.Count,
                    X = (long)Math.Round(x), Y = (long)Math.Round(y), Confidence = l?.Confidence ?? s.Confidence,
                    Evidence = evidence, Notes = notes, Terminals = terminals,
                };
            else
            {
                var kind = s.Kind switch
                {
                    ImageSymbolKind.Resistor => PartKind.R, ImageSymbolKind.Inductor => PartKind.L,
                    ImageSymbolKind.Capacitor => PartKind.C, _ => PartKind.Unknown,
                };
                if (kind != PartKind.Unknown) evidence[PartField.Kind] = PartEvidenceSource.Picture;
                int r0 = RootOf(s.Pins[0].Node), r1 = RootOf(s.Pins[1].Node);
                var connection = (r0 == G, r1 == G) switch
                {
                    (true, true) => PartConnection.Shorted,
                    (true, false) or (false, true) => PartConnection.Shunt,
                    _ => r0 == r1 ? PartConnection.Bridged : PartConnection.Series,
                };
                double? value = null;
                if (l?.Value is { } v)
                {
                    var dim = PartReading.DimensionOf(kind == PartKind.Unknown ? PartKind.C : kind);
                    if (v.NotFitted) kind = PartKind.Open;
                    else if (v.Fits(dim)) { value = v.Si; evidence[PartField.Value] = PartEvidenceSource.Picture; }
                    else notes.Add($"'{l.ValueWord!.Text}' is not a {dim.ToString().ToLowerInvariant()}, so the value was not applied");
                }
                row = new PartRow
                {
                    Refdes = refdes, Kind = kind, Connection = connection, Value = value, PadCount = 2,
                    X = (long)Math.Round(x), Y = (long)Math.Round(y), Confidence = l?.Confidence ?? s.Confidence,
                    Evidence = evidence, Notes = notes, Terminals = terminals,
                };
                if (row.IsModelled && row.TakesValue && row.Value is null) row = row with { Variable = row.DefaultVariable };
            }
            rows.Add(row);
            symbolOfRow[refdes] = i;
        }
        var parts = new PartsTable([.. rows.OrderBy(r => r.Refdes, PartsTable.NaturalOrder)], RailRf.RailLengthFormat.Dbu, null) { Pixels = true };
        if (options.PartsCsvText is not null || options.PartsCsvPath is not null)
        {
            var edited = options.PartsCsvText is { } text ? PartsTableCsv.Read(text, parts) : PartsTableCsv.ReadFile(options.PartsCsvPath!, parts);
            if (edited.Refusal is { } why) { refusal = why; return null; }
            parts = edited.Table!;
            report.Add(RecognitionFindingClass.PartsCsvNotes, edited.Notes.Count, string.Join(" ", edited.Notes));
            report.Add(RecognitionFindingClass.PartsCsvRefdesNotOnBoard, edited.NotOnBoard.Count,
                $"The parts table names {Plural(edited.NotOnBoard.Count, "part", "parts")} the picture does not have, and " +
                $"{(edited.NotOnBoard.Count == 1 ? "it was" : "they were")} ignored: {string.Join(", ", edited.NotOnBoard)}.");
        }
        int SymbolOf(PartRow r) => symbolOfRow.TryGetValue(r.BoardRefdes, out int i) ? i : -1;

        // A short in the table joins its two nets.
        foreach (var r in parts.Rows.Where(r => r.Kind == PartKind.Short && SymbolOf(r) >= 0))
        {
            var pins = syms[SymbolOf(r)].Pins;
            if (pins.Count == 2 && NetOf(pins[0].Node) >= 0 && NetOf(pins[1].Node) >= 0) Union(NetOf(pins[0].Node), NetOf(pins[1].Node));
        }

        // ── ports (D13) ─────────────────────────────────────────────────────────────────────────────
        var ports = new List<(string? Name, int Root, PicturePortSource Source, double X, double Y, double DirX, double DirY, int Symbol, int Node)>();
        bool Ported(int root) => ports.Any(p => p.Root == root);
        (double X, double Y) DirAt(SymbolRegion region, int node)
        {
            var a = region.Attachments.FirstOrDefault(t => t.Node == node);
            return a is null ? (0, 0) : (a.DirX, a.DirY);
        }
        for (int i = 0; i < syms.Count; i++)
        {
            var s = syms[i];
            if (s.Disposition != SymbolDisposition.Recognised || s.Kind != ImageSymbolKind.Port || s.Pins.Count == 0) continue;
            var pin = s.Pins.FirstOrDefault(p => RootOf(p.Node) is var r and >= 0 && r != G) ?? s.Pins[0];
            int root = RootOf(pin.Node);
            if (root < 0 || root == G || Ported(root)) continue;
            var (dx, dy) = DirAt(s.Region, pin.Node);
            ports.Add((lab[i] is { Generated: false } l ? l.Designator : null, root, PicturePortSource.Symbol, pin.X, pin.Y, dx, dy, i, pin.Node));
        }
        foreach (var n in labels.Names)
        {
            if (supplyNodes.Contains(n.Node) || n.Word.Reading?.IsPortWord != true) continue;
            int root = RootOf(n.Node);
            if (root < 0 || root == G) continue;
            int at = ports.FindIndex(p => p.Root == root);
            if (at >= 0)
            {
                // A port symbol on a net the picture also names takes the name.
                if (ports[at].Name is null) ports[at] = ports[at] with { Name = n.Name };
                continue;
            }
            var node = graph.Nodes[n.Node];
            var (ix, iy) = node.IsEnd ? graph.Incoming(n.Node) : (0, 0);
            ports.Add((n.Name, root, PicturePortSource.Name, node.X, node.Y, ix, iy, -1, n.Node));
        }
        // The drawing's own ports in order: by the numbers their names carry when every one carries one, else left to
        // right, top to bottom (a dangling end's order is already that).
        if (ports.Count > 0)
        {
            var numbers = ports.Select(p => TrailingNumber(p.Name)).ToList();
            ports = numbers.All(n => n is not null) && numbers.Distinct().Count() == numbers.Count
                ? [.. ports.Zip(numbers).OrderBy(t => t.Second).Select(t => t.First)]
                : [.. ports.OrderBy(p => Math.Round(p.X / (8 * w))).ThenBy(p => p.Y)];
        }
        // ── instances ───────────────────────────────────────────────────────────────────────────────
        var protos = new List<Proto>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var portInfo = new List<(Proto Proto, PicturePortSource Source, double X, double Y)>();
        for (int k = 0; k < ports.Count; k++)
        {
            var p = ports[k];
            string name = Unique(RecognitionEmit.Identifier(p.Name ?? $"P{k + 1}", "P"), names);
            var proto = new Proto { Type = "Port", Name = name, Roots = [p.Root, G], Symbol = p.Symbol, PortNumber = k + 1 };
            proto.Parameters.Add(new ParameterAssignment("Num", (k + 1).ToString(CultureInfo.InvariantCulture)));
            // The impedance printed under the port's designator, when one was read (IM-12); else 50 Ω.
            double? z = p.Symbol >= 0 && lab[p.Symbol]?.LineParameters is { } lp
                ? lp.FirstOrDefault(q => q.Key is "Z" or "Z0")?.Si : null;
            proto.Parameters.Add(z is { } zv
                ? new ParameterAssignment("Z", RecognitionEmit.Split(PartsTable.ValueText(zv, PartKind.R, digits)).Number, "Ohm")
                : new ParameterAssignment("Z", "50", "Ohm"));
            proto.PicturePins.Add((p.X, p.Y, p.DirX, p.DirY));
            proto.PinNodes.Add(p.Node);
            // A port symbol drawn with its minus wired to ground (a Term's box) is fitted, and wired, by both pins.
            if (p.Symbol >= 0 && syms[p.Symbol].Pins.FirstOrDefault(q => q.Node != p.Node && RootOf(q.Node) == G) is { } minus)
            {
                var (mx, my) = DirAt(syms[p.Symbol].Region, minus.Node);
                proto.PicturePins.Add((minus.X, minus.Y, mx, my));
                proto.PinNodes.Add(minus.Node);
            }
            protos.Add(proto);
            portInfo.Add((proto, p.Source, p.X, p.Y));
        }

        var variables = new List<(string Name, string Number, string? Unit, string Min, string Max)>();
        void Variable(string name, double value, PartKind kind)
        {
            if (variables.Any(v => v.Name == name)) return;
            var (number, unit) = RecognitionEmit.Split(PartsTable.ValueText(value, kind, digits));
            var tune = RecognitionEmit.TuneEntry(name, value, kind, digits);
            variables.Add((name, number, unit, tune.Min!, tune.Max!));
        }

        foreach (var row in parts.Rows.Where(r => r.IsModelled && SymbolOf(r) >= 0))
        {
            int i = SymbolOf(row);
            var s = syms[i];
            if (s.Pins.Count != 2) continue;
            string name = Unique(RecognitionEmit.Identifier(row.Refdes, row.ParameterName), names);
            var roots = s.Pins.Select(p => RootOf(p.Node)).ToList();
            Proto proto;
            if (row.Model == PartModelKind.SnP && parts.ResolveModelFile(row) is { } file)
            {
                proto = new Proto { Type = "SnP", Name = name, Roots = roots, Symbol = i };
                proto.Parameters.Add(new ParameterAssignment("NumPorts", "2"));
                proto.Parameters.Add(new ParameterAssignment("File", $"\"{file}\""));
            }
            else
            {
                proto = new Proto { Type = row.ParameterName, Name = name, Roots = roots, Symbol = i };
                if (row.Value is { } value)
                {
                    var (number, unit) = RecognitionEmit.Split(PartsTable.ValueText(value, row.GeneratedKind, digits));
                    proto.Parameters.Add(new ParameterAssignment(row.ParameterName, number, unit));
                }
                else
                {
                    string variable = RecognitionEmit.Identifier(row.Variable ?? row.DefaultVariable, "V");
                    proto.Parameters.Add(new ParameterAssignment(row.ParameterName, variable));
                    Variable(variable, row.TransparentValue, row.GeneratedKind);
                }
            }
            AddPins(proto, s);
            protos.Add(proto);
        }

        // A multi-pin symbol — a box, a transistor, an amplifier — is an S-parameter block with one port per wired pin
        // and a pin on ground its reference: the shape an RF schematic draws a black box in, and the part it is drawn as.
        var blocks = new List<(string Name, int Ports)>();
        foreach (var row in parts.Rows.Where(r => r.Kind is PartKind.MultiPin or PartKind.Connector && SymbolOf(r) >= 0)
                                      .OrderBy(r => r.X).ThenBy(r => r.Y))
        {
            int i = SymbolOf(row);
            var s = syms[i];
            var signal = s.Pins.Where(p => RootOf(p.Node) is var r and >= 0 && r != G)
                               .OrderBy(p => Math.Round(p.X / (4 * w))).ThenBy(p => p.Y).ToList();
            if (signal.Count == 0) continue;
            var reference = s.Pins.FirstOrDefault(p => RootOf(p.Node) == G);
            string name = Unique(RecognitionEmit.Identifier(row.Refdes, "S"), names);
            var proto = new Proto { Type = "SnP", Name = name, Roots = [.. signal.Select(p => RootOf(p.Node))], Symbol = i };
            proto.Parameters.Add(new ParameterAssignment("NumPorts", signal.Count.ToString(CultureInfo.InvariantCulture)));
            if (parts.ResolveModelFile(row) is { } file) proto.Parameters.Add(new ParameterAssignment("File", $"\"{file}\""));
            foreach (var p in reference is null ? signal : [.. signal, reference])
            {
                var (dx, dy) = DirAt(s.Region, p.Node);
                proto.PicturePins.Add((p.X, p.Y, dx, dy));
                proto.PinNodes.Add(p.Node);
            }
            if (reference is not null) proto.RefRoot = G;
            protos.Add(proto);
            blocks.Add((name, signal.Count));
        }

        var lineNotes = new List<string>();
        for (int i = 0; i < syms.Count; i++)
        {
            var s = syms[i];
            if (s.Disposition != SymbolDisposition.Recognised) continue;
            if (s.Kind is ImageSymbolKind.TransmissionLine or ImageSymbolKind.ShortStub or ImageSymbolKind.OpenStub)
            {
                if (s.Pins.Count == 0) continue;
                string name = Unique(RecognitionEmit.Identifier(lab[i]?.Designator ?? $"TL{i + 1}", "TL"), names);
                var roots = s.Pins.Take(2).Select(p => RootOf(p.Node)).ToList();
                if (s.Kind == ImageSymbolKind.ShortStub && roots.Count == 1) roots.Add(G);
                if (roots.Count == 1) roots.Add(-1);   // an open stub's far end: a net of its own
                var proto = new Proto { Type = "TLIN", Name = name, Roots = roots, Symbol = i };
                LineParameters(proto, lab[i]?.LineParameters ?? [], options.Technology, digits, (n, v, u, lo, hi) =>
                {
                    if (!variables.Any(x => x.Name == n)) variables.Add((n, v, u, lo, hi));
                }, lineNotes);
                AddPins(proto, s);
                protos.Add(proto);
            }
            else if (s.Kind == ImageSymbolKind.Diode && s.Pins.Count == 2)
            {
                string name = Unique(RecognitionEmit.Identifier(lab[i]?.Designator ?? $"D{i + 1}", "D"), names);
                var proto = new Proto { Type = "Diode", Name = name, Roots = [.. s.Pins.Select(p => RootOf(p.Node))], Symbol = i };
                AddPins(proto, s);
                protos.Add(proto);
            }
        }

        if (protos.Count == 0)
        {
            refusal = NothingRecognisedRefusal;
            return null;
        }

        // ── net names ───────────────────────────────────────────────────────────────────────────────
        var netName = new Dictionary<int, string> { [G] = Ground };
        var netsTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Ground };
        string Take(string bare)
        {
            string n = bare;
            for (int k = 2; !netsTaken.Add(n); k++) n = $"{bare}_{k}";
            return n;
        }
        var stated = new Dictionary<int, string>();
        foreach (var n in labels.Names)
            if (RootOf(n.Node) is var r and >= 0 && r != G && RecognitionEmit.Identifier(n.Name, "N") is var id
                && (!stated.TryGetValue(r, out var had) || string.CompareOrdinal(id, had) < 0))
                stated[r] = id;
        int supplies = 0;
        foreach (var m in read.SupplyMarks)
            if (RootOf(m.Node) is var r and >= 0 && r != G && !stated.ContainsKey(r)) stated[r] = $"Vsupply{++supplies}";
        var supplyNets = read.SupplyMarks.Select(m => RootOf(m.Node)).Where(r => r >= 0 && r != G).Distinct().ToList();

        foreach (var (proto, _, _, _) in portInfo)
            if (!netName.ContainsKey(proto.Roots[0]))
                netName[proto.Roots[0]] = Take(stated.TryGetValue(proto.Roots[0], out var st) ? st : proto.Name.ToLowerInvariant());
        int nk = 0;
        foreach (var proto in protos)
            for (int k = 0; k < proto.Roots.Count; k++)
            {
                int r = proto.Roots[k];
                if (r < 0 || netName.ContainsKey(r)) continue;
                if (stated.TryGetValue(r, out var st)) { netName[r] = Take(st); continue; }
                string n;
                do n = $"n{++nk}"; while (netsTaken.Contains(n));
                netName[r] = Take(n);
            }
        string NetName(Proto p, int k) => p.Roots[k] >= 0 ? netName[p.Roots[k]] : Take($"{p.Name}_open");

        // ── the TestBench ───────────────────────────────────────────────────────────────────────────
        var tb = new TestBench("tb") { Technology = options.TechnologyPath is { } tp ? Path.GetFullPath(tp) : null };
        foreach (var v in variables) tb.GlobalVariables.Add(new Variable(v.Name, v.Number, v.Unit));
        foreach (var p in protos)
            tb.Instances.Add(new Instance(p.Name, p.Type, [.. Enumerable.Range(0, p.Roots.Count).Select(k => NetName(p, k))], p.Parameters)
            {
                RefNetBinding = p.RefRoot is { } rr ? netName[rr] : null,
            });
        if (variables.Count > 0)
        {
            tb.Tuning = new TuningSetup();
            foreach (var v in variables) tb.Tuning.Variables.Add(new TunableEntry { Key = v.Name, Tune = true, Opt = false, Min = v.Min, Max = v.Max });
        }
        if (ports.Count > 0)
            tb.Analyses.Add(new SParameterAnalysis(RecognitionEmit.AnalysisName, options.Emit.Sweep ?? RecognitionEmitOptions.DefaultSweep));

        // ── the report ──────────────────────────────────────────────────────────────────────────────
        int netCount = protos.SelectMany(p => p.Roots).Where(r => r >= 0 && r != G).Distinct().Count();
        report.Add(RecognitionFindingClass.PictureNets, netCount,
            $"{Plural(netCount, "net", "nets")} besides ground, from {Plural(graph.Segments.Count, "wire segment", "wire segments")}.");
        var picturePorts = portInfo.Select(t => new PicturePort(t.Proto.PortNumber, t.Proto.Name, netName[t.Proto.Roots[0]], t.Source,
                                                                 read.ToSource(t.X, t.Y).X, read.ToSource(t.X, t.Y).Y)).ToList();
        foreach (var (source, what) in new (PicturePortSource, string)[]
                 {
                     (PicturePortSource.Symbol, "from port symbols"),
                     (PicturePortSource.Name, "from net names that name a port"),
                     (PicturePortSource.DanglingEnd, "at dangling wire ends on the drawing's left and right edges"),
                 })
        {
            var these = picturePorts.Where(p => p.Source == source).ToList();
            report.Add(RecognitionFindingClass.PicturePorts, these.Count,
                $"{Plural(these.Count, "port", "ports")} {what}: {string.Join(", ", these.Select(p => p.Name))}.",
                [.. these.Select(p => Anchor(p.X, p.Y))]);
        }
        foreach (var (block, n) in blocks)
            report.Add(RecognitionFindingClass.PictureEmitNotes, 1,
                $"{block} is drawn as a box with {Plural(n, "wired pin", "wired pins")}, so it is a {n}-port S-parameter block: " +
                "give it a Touchstone file to simulate it.");
        if (ports.Count == 0) report.Add(RecognitionFindingClass.PictureNoPort, 1, NoPortLine);
        var unsourced = supplyNets.Where(netName.ContainsKey).Select(r => netName[r]).Order(StringComparer.Ordinal).ToList();
        report.Add(RecognitionFindingClass.PictureSupplyNets, unsourced.Count,
            $"{Plural(unsourced.Count, "supply net has", "supply nets have")} no source in the circuit and " +
            $"{(unsourced.Count == 1 ? "is" : "are")} left as a named net: {string.Join(", ", unsourced)}.");
        report.Add(RecognitionFindingClass.PictureVariables, variables.Count,
            $"{Plural(variables.Count, "value was", "values were")} not read and {(variables.Count == 1 ? "is a global variable" : "are global variables")} " +
            $"with a tune entry, at a start that leaves the circuit as if the part were not there: {string.Join(", ", variables.Select(v => v.Name))}.");
        report.Add(RecognitionFindingClass.PictureEmitNotes, lineNotes.Count, string.Join(" ", lineNotes));

        // ── the drawing as drawn (D12) ──────────────────────────────────────────────────────────────
        var frame = options.Frame ?? AutoFrame(read, protos, tb);
        var exact = Exact(read, protos, tb, frame, Find, G, netName);
        var nodeNets = new Dictionary<int, string>();
        for (int node = 0; node < graph.Nodes.Count; node++)
            if (RootOf(node) is var r and >= 0 && netName.TryGetValue(r, out var net)) nodeNets[node] = net;
        var grounds = new List<(int Node, double DirX, double DirY)>(groundMarks);
        foreach (var s in syms.Where(s => s.Disposition == SymbolDisposition.Recognised && s.Kind == ImageSymbolKind.Ground))
            foreach (var p in s.Pins)
            {
                double dx = s.Region.Box.CentreX - p.X, dy = s.Region.Box.CentreY - p.Y;
                grounds.Add((p.Node, dx, dy));
            }
        var named = new List<(int Node, string Net)>();
        foreach (int node in labels.Names.Select(n => n.Node).Concat(read.SupplyMarks.Select(m => m.Node)).Distinct())
            if (RootOf(node) is var r and >= 0 && r != G && netName.TryGetValue(r, out var net)) named.Add((node, net));
        return new SchematicImageCircuit(tb)
        {
            PinNodes = protos.ToDictionary(p => p.Name, p => (IReadOnlyList<int>)p.PinNodes, StringComparer.Ordinal),
            NodeNets = nodeNets, Grounds = grounds, NamedNodes = named, HiddenSegments = hidden,
            Exact = exact, Frame = frame, Parts = parts, Ports = picturePorts, Variables = [.. variables.Select(v => v.Name)],
            SymbolOf = protos.Where(p => p.Symbol >= 0).ToDictionary(p => p.Name, p => p.Symbol, StringComparer.Ordinal),
            SymbolOfRow = symbolOfRow,
        };

        void AddPins(Proto proto, RecognisedSymbol s)
        {
            foreach (var p in s.Pins)
            {
                var (dx, dy) = DirAt(s.Region, p.Node);
                proto.PicturePins.Add((p.X, p.Y, dx, dy));
                proto.PinNodes.Add(p.Node);
            }
        }
    }

    // ── grounds the wire reading took for wire ──────────────────────────────────────────────────────

    /// <summary>A ground symbol whose bars were read as wire: a tee at the foot of a wire coming down, its two arms short and
    /// ending bare or on a label, with at least one shorter bar beneath — the node it stands on and the bars' segments.</summary>
    private static IEnumerable<(int Node, List<int> Bars)> WireGrounds(WireGraph graph, double w)
    {
        foreach (var n in graph.Nodes.Where(n => n.Degree == 3))
        {
            var at = graph.Segments.Where(s => s.From == n.Id || s.To == n.Id).ToList();
            var stems = at.Where(s => s.Orientation == WireOrientation.Vertical).ToList();
            var arms = at.Where(s => s.Orientation == WireOrientation.Horizontal).ToList();
            if (stems.Count != 1 || arms.Count != 2) continue;
            int Far(WireSegment s) => s.From == n.Id ? s.To : s.From;
            if (graph.Nodes[Far(stems[0])].Y >= n.Y || arms.Any(a => graph.Nodes[Far(a)].Degree != 1)) continue;
            double left = arms.Min(a => graph.Nodes[Far(a)].X), right = arms.Max(a => graph.Nodes[Far(a)].X);
            double len = right - left;
            if (len < 3 * w || len > 20 * w || left > n.X - w || right < n.X + w) continue;
            var under = graph.Segments
                .Where(s => s.Orientation == WireOrientation.Horizontal && graph.Nodes[s.From].IsEnd && graph.Nodes[s.To].IsEnd
                            && Math.Min(s.A.y, s.B.y) > n.Y + 0.5 * w && Math.Max(s.A.y, s.B.y) <= n.Y + 6 * w
                            && s.Length < len + w && Math.Min(s.A.x, s.B.x) >= left - 2 * w && Math.Max(s.A.x, s.B.x) <= right + 2 * w)
                .Select(s => s.Id).ToList();
            if (under.Count == 0) continue;
            yield return (n.Id, [.. arms.Select(a => a.Id), .. under]);
        }
    }

    /// <summary>Whether a word naming a supply mark's net names a supply — a voltage or a V… rail — rather than ground.</summary>
    private static bool SupplyName(string name)
    {
        string t = name.Trim().TrimStart('+', '-', '−');
        return t.Length > 0 && (char.ToUpperInvariant(t[0]) == 'V' || char.IsAsciiDigit(t[0]))
            && !t.Contains("GND", StringComparison.OrdinalIgnoreCase);
    }

    // ── lines ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A TLIN's parameters from its line set: Z and E at F, or Z and L with εeff when the set gives a length.
    /// Each one not read is a variable named after the line.</summary>
    private static void LineParameters(Proto p, IReadOnlyList<LineParameter> set, Technology? tech, int digits,
                                       Action<string, string, string?, string, string> variable, List<string> notes)
    {
        double? Get(params string[] keys) =>
            set.FirstOrDefault(q => keys.Any(k => string.Equals(q.Key, k, StringComparison.OrdinalIgnoreCase)))?.Si;
        string Num(double v) => RecognitionEmitOptions.Spell(v, digits);
        double? z = Get("Z", "Z0"), e = Get("E", "θ"), l = Get("L"), f = Get("F");

        void Param(string key, double? value, string? unit, string start, string min, string max)
        {
            if (value is { } v) { p.Parameters.Add(new ParameterAssignment(key, Num(v), unit)); return; }
            string name = $"{RecognitionEmit.Identifier(p.Name, "TL")}_{key}";
            p.Parameters.Add(new ParameterAssignment(key, name));
            variable(name, start, unit, unit is null ? min : $"{min} {unit}", unit is null ? max : $"{max} {unit}");
        }

        Param("Z", z, "Ohm", "50", "20", "150");
        if (l is { } length)
        {
            Param("L", length * 1e3, "mm", "", "", "");
            double? eeff = tech is null ? null : TechnologyEeff(tech, z ?? 50);
            if (eeff is null) Param("Eeff", null, null, "1", "1", "12");
            else
            {
                Param("Eeff", eeff, null, "", "", "");
                notes.Add($"{p.Name}'s εeff is the technology's, for a {Num(z ?? 50)} Ω microstrip on its top dielectric.");
            }
            if (f is { } ff) Param("F", ff / 1e9, "GHz", "", "", "");
            return;
        }
        Param("E", e, "deg", "90", "1", "180");
        if (f is { } freq) p.Parameters.Add(new ParameterAssignment("F", Num(freq / 1e9), "GHz"));
        else p.Parameters.Add(new ParameterAssignment("F", "1", "GHz"));
    }

    /// <summary>The εeff of a <paramref name="z"/> Ω microstrip on the technology's top conductor over the dielectric
    /// under it (Hammerstad–Jensen), or null when the stackup has no such pair.</summary>
    internal static double? TechnologyEeff(Technology tech, double z)
    {
        var layers = tech.Stackup.Layers;
        int top = layers.FindIndex(x => x.Kind == StackupKind.Conductor);
        if (top < 0 || top + 1 >= layers.Count || layers[top + 1].Kind != StackupKind.Dielectric) return null;
        double h = layers[top + 1].ThicknessDbu * 1e-9, t = layers[top].ThicknessDbu * 1e-9, er = layers[top + 1].Epsr;
        if (!(h > 0) || !(er >= 1)) return null;
        try
        {
            double width = HammerstadJensen.SynthesizeWidth(z, h, t, er, new MicrostripValidityReporter("picture line"));
            return Math.Round(HammerstadJensen.StaticEeff(width / h, er), 4);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    // ── the drawing (D12) ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The frame the drawing is made at (R-im10-4). A picture showing a drawing grid — an editor's screenshot — is drawn
    /// one of its grid pitches to one schematic grid step, its grid lines on the schematic's; the symbols decide only when
    /// they say the picture's grid is a coarser or finer one than that. Without a grid, the frame is the one where the
    /// median recognised pin span equals the symbols' own.
    /// </summary>
    private static SchematicFrame AutoFrame(SchematicImageRead read, List<Proto> protos, TestBench tb)
    {
        var ratios = new List<double>();
        foreach (var p in protos.Where(p => p.PicturePins.Count == 2 && p.Type != "Port"))
        {
            var inst = tb.Instances.First(i => i.InstanceName == p.Name);
            if (NetlistSchematic.PinOffsets(inst, SymbolRotation.R0, false) is not { Count: >= 2 } pins) continue;
            double own = Math.Sqrt(Math.Pow(pins[0].X - pins[1].X, 2) + Math.Pow(pins[0].Y - pins[1].Y, 2));
            var (ax, ay) = read.ToSource(p.PicturePins[0].X, p.PicturePins[0].Y);
            var (bx, by) = read.ToSource(p.PicturePins[1].X, p.PicturePins[1].Y);
            double seen = Math.Sqrt(Math.Pow(ax - bx, 2) + Math.Pow(ay - by, 2));
            if (own > 0 && seen > 1) ratios.Add(own / seen);
        }
        ratios.Sort();
        double? symbols = ratios.Count == 0 ? null
                        : ratios.Count % 2 == 1 ? ratios[ratios.Count / 2] : 0.5 * (ratios[ratios.Count / 2 - 1] + ratios[ratios.Count / 2]);
        if (read.Grid is { } grid)
        {
            double k = NetlistSchematic.Grid / grid.Pitch;
            // A symbol's pins are read where its wires reach its glyph, inside the ends of its leads, so the symbols'
            // own estimate runs high; only a factor of 2.5 says the grid read is a finer or coarser one.
            if (symbols is { } s && (s / k > 2.5 || s / k < 0.4))
                k *= new[] { 0.5, 2, 4, 5, 10 }.MinBy(m => Math.Abs(Math.Log(s / (k * m))));
            return new SchematicFrame(k, -k * grid.PhaseX, -k * grid.PhaseY);
        }
        return new SchematicFrame(symbols ?? NetlistSchematic.Grid / Math.Max(1, 4 * read.StrokeWidth), 0, 0);
    }

    private static ExactPlacement Exact(SchematicImageRead read, List<Proto> protos, TestBench tb, SchematicFrame frame,
                                        Func<int, int> find, int ground, Dictionary<int, string> netName)
    {
        (double X, double Y) At(double x, double y) { var (sx, sy) = read.ToSource(x, y); return frame.ToSchematic(sx, sy); }

        var instances = new Dictionary<string, ExactInstance>(StringComparer.Ordinal);
        foreach (var p in protos)
        {
            if (p.PicturePins.Count == 0) continue;
            var inst = tb.Instances.First(i => i.InstanceName == p.Name);
            var want = p.PicturePins.Select(q => At(q.X, q.Y)).ToList();
            ExactInstance? best = null;
            double bestCost = double.MaxValue;
            foreach (var o in SymbolOrientation.All)
            {
                if (NetlistSchematic.PinOffsets(inst, o.Rotation, o.MirrorX) is not { Count: > 0 } pins) continue;
                int n = Math.Min(pins.Count, want.Count);
                double ox = 0, oy = 0;
                for (int i = 0; i < n; i++) { ox += want[i].X - pins[i].X; oy += want[i].Y - pins[i].Y; }
                ox /= n; oy /= n;
                double cost = 0;
                for (int i = 0; i < n; i++) cost += Math.Pow(want[i].X - ox - pins[i].X, 2) + Math.Pow(want[i].Y - oy - pins[i].Y, 2);
                if (n == 1)
                {
                    // One pin seen: its lead points back along the wire it is on, away from the symbol's body.
                    var (dx, dy) = (-p.PicturePins[0].DirX, -p.PicturePins[0].DirY);
                    var (bx, by) = pins.Count > 1 ? (pins[0].X - pins[1].X, pins[0].Y - pins[1].Y) : (pins[0].X, pins[0].Y);
                    double len = Math.Sqrt(bx * bx + by * by);
                    cost = len > 0 ? -(bx * dx + by * dy) / len : 0;
                }
                if (cost < bestCost - 1e-9) { bestCost = cost; best = new ExactInstance(ox, oy, o.Rotation, o.MirrorX); }
            }
            if (best is { } b) instances[p.Name] = b;
        }

        // The wires: the picture's segments, a straight pair through a crossing that does not connect made one run, so
        // the two nets cross in the drawing as they do on the picture and meet nowhere.
        var graph = read.Wires;
        var segParent = Enumerable.Range(0, graph.Segments.Count).ToArray();
        int Find(int x) { while (segParent[x] != x) x = segParent[x] = segParent[segParent[x]]; return x; }
        foreach (var c in graph.Crossings.Where(c => c.Reading == CrossingReading.NotConnected && c.Node is not null))
            foreach (int a in c.Segments)
                foreach (int b in c.Segments)
                    if (a < b && graph.Net(a) == graph.Net(b) && Collinear(graph.Segments[a], graph.Segments[b])) segParent[Find(b)] = Find(a);
        var used = protos.SelectMany(p => p.Roots).ToHashSet();
        var routes = new Dictionary<string, List<IReadOnlyList<(double X, double Y)>>>(StringComparer.Ordinal);
        foreach (var group in Enumerable.Range(0, graph.Segments.Count).GroupBy(Find).OrderBy(g => g.Key))
        {
            int root = find(graph.Net(group.Key));
            if (root == ground || !used.Contains(root) || !netName.TryGetValue(root, out var net)) continue;
            var points = group.SelectMany(s => new[] { graph.Segments[s].A, graph.Segments[s].B }).ToList();
            var (a, b) = (points[0], points[0]);
            double far = -1;
            foreach (var p in points)
                foreach (var q in points)
                    if (Math.Pow(p.x - q.x, 2) + Math.Pow(p.y - q.y, 2) is var d && d > far) { far = d; (a, b) = (p, q); }
            if (!routes.TryGetValue(net, out var list)) routes[net] = list = [];
            list.Add([At(a.x, a.y), At(b.x, b.y)]);
        }
        return new ExactPlacement
        {
            Instances = instances,
            Routes = routes.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<IReadOnlyList<(double X, double Y)>>)kv.Value, StringComparer.Ordinal),
        };
    }

    private static bool Collinear(WireSegment a, WireSegment b) =>
        a.Orientation == b.Orientation && a.Orientation != WireOrientation.Diagonal;

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static int Nearest(IEnumerable<(int Id, double X, double Y)> points, double x, double y, double reach)
    {
        int best = -1;
        double bd = reach;
        foreach (var (id, px, py) in points)
            if (Math.Sqrt((px - x) * (px - x) + (py - y) * (py - y)) is var d && d <= bd) { bd = d; best = id; }
        return best;
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        string n = name;
        for (int k = 2; !taken.Add(n); k++) n = $"{name}_{k}";
        return n;
    }

    private static int? TrailingNumber(string? name)
    {
        if (name is null) return null;
        int i = name.Length;
        while (i > 0 && char.IsAsciiDigit(name[i - 1])) i--;
        return i < name.Length && int.TryParse(name[i..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    private static RecognitionAnchor Anchor(double x, double y) => new((long)Math.Round(x), (long)Math.Round(y));

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
