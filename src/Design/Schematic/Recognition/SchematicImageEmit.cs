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
//              and right extremes; a cut-out device's wired pins after them (AS D9). A datasheet's or a textbook's
//              terminals (IMF-6): a named terminal is a port, a pair of terminals one port, the return rail ground when
//              no ground is drawn, a name beyond an open end a port; inputs numbered first. A part of no known kind is
//              a 0 Ω `?n` (O3);
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

/// <summary>How a port was found (D13, R-imf6-8), strongest first.</summary>
public enum PicturePortSource
{
    /// <summary>A port symbol: a Term box, a pin flag.</summary>
    Symbol,
    Name, DanglingEnd, CutOut,

    /// <summary>A terminal with a word beside it (R-imf6-1).</summary>
    NamedTerminal,

    /// <summary>Two terminals stacked at the drawing's edge, or one of them on the return rail (R-imf6-2).</summary>
    TerminalPair,

    /// <summary>Two terminals a voltage arrow spans (R-imf6-2).</summary>
    ArrowPair,

    /// <summary>A name standing beyond an open end (R-imf6-4).</summary>
    FarName,

    /// <summary>A terminal alone, unnamed: a port to ground.</summary>
    Terminal,

    /// <summary>An AC source, a P1Tone (O2).</summary>
    Tone,
}

/// <summary>One pin of an IC drawn as a box (R-imf8-1): the number and the name the picture prints at it, either null
/// where none was read.</summary>
public sealed record IcPin(int? Number, string? Name);

/// <summary>An IC's pins as the picture draws them (R-imf8-1).</summary>
/// <param name="Ports">Its S-parameter ports in order: by pin number when every one has a number, else in package
/// order.</param>
/// <param name="ByNumber">The ports follow the pin numbers read.</param>
/// <param name="Reference">The pins on ground or named GND: its reference.</param>
/// <param name="Open">The pins wired to nothing, or named NC: left out.</param>
public sealed record IcReading(IReadOnlyList<IcPin> Ports, bool ByNumber, IReadOnlyList<IcPin> Reference, IReadOnlyList<IcPin> Open);

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

    /// <summary>Every word read off the picture: a source's value, a transformer's k, a box marked BEAD are read from the
    /// words beside the part (IMF-5).</summary>
    public IReadOnlyList<ImageWord>? Words { get; init; }

    /// <summary>What a two-pin part of no known kind is made (O3): a 0 Ω resistor designated <c>?n</c>, on a picture.</summary>
    public UnknownPart UnknownAs { get; init; } = UnknownPart.ZeroOhm;
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

    /// <summary>Per port made from a pair of terminals, the wire node of its reference terminal: its minus is wired there
    /// as drawn (R-imf6-7).</summary>
    public IReadOnlyDictionary<string, int> PortReferences { get; init; } = new Dictionary<string, int>();

    /// <summary>The parts of no known kind, made 0 Ω resistors (O3, R-imf6-6).</summary>
    public IReadOnlyList<string> UnknownParts { get; init; } = [];

    /// <summary>Per IC drawn as a box (R-imf8-1), its pins as read: each port's pin number and pin name, then the pins
    /// that are its reference and those wired to nothing.</summary>
    public IReadOnlyDictionary<string, IcReading> Ics { get; init; } = new Dictionary<string, IcReading>();

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

    /// <summary>A port found on the picture, before it is numbered.</summary>
    /// <param name="RefRoot">The net its minus is on: ground, or a pair's reference terminal's net.</param>
    private sealed record PortFound(string? Name, int Root, int RefRoot, PicturePortSource Source, double X, double Y,
                                    double DirX, double DirY, int Symbol, int Node)
    {
        /// <summary>The wire node of a pair's reference terminal, or −1.</summary>
        public int RefNode { get; init; } = -1;
    }

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

        // ── terminals, their pairs and the return rail (R-imf6-1 … R-imf6-3) ───────────────────────────
        double s0 = read.SymbolScale > 0 ? read.SymbolScale : TemplateSymbolClassifier.FallbackSymbolInW * w;
        var terminals = PictureTerminals.Find(read, symbols);
        var termPair = Enumerable.Repeat(-1, terminals.Count).ToArray();
        var pairs = new List<(int Plus, int Ref, PicturePortSource Source)>();
        int TRoot(int t) => RootOf(terminals[t].Node);
        double TDist(int a, int b) => Math.Abs(terminals[a].X - terminals[b].X) + Math.Abs(terminals[a].Y - terminals[b].Y);
        void Pair(int plus, int reference, PicturePortSource source)
        {
            pairs.Add((plus, reference, source));
            termPair[plus] = reference;
            termPair[reference] = plus;
        }
        bool Pairable(int a, int b) => a != b && termPair[a] < 0 && termPair[b] < 0 && TRoot(a) >= 0 && TRoot(b) >= 0 && TRoot(a) != TRoot(b);
        // The upper terminal of a pair is its +, or the left one of a pair side by side.
        (int, int) Ordered(int a, int b)
        {
            var (ta, tb) = (terminals[a], terminals[b]);
            bool aFirst = Math.Abs(ta.Y - tb.Y) >= Math.Abs(ta.X - tb.X) ? ta.Y < tb.Y : ta.X < tb.X;
            return aFirst ? (a, b) : (b, a);
        }
        // A voltage arrow spanning two terminals: each end within three quarters of a symbol of one.
        foreach (var arrow in read.Annotations.Where(a => a.Kind == StrokeAnnotationKind.VoltageArrow && a.Path.Count >= 2))
        {
            int NearestTo(double x, double y, int not) => Enumerable.Range(0, terminals.Count)
                .Where(t => t != not && termPair[t] < 0)
                .Select(t => (T: t, D: Math.Sqrt(Math.Pow(terminals[t].X - x, 2) + Math.Pow(terminals[t].Y - y, 2))))
                .Where(c => c.D <= 0.75 * s0).OrderBy(c => c.D).Select(c => c.T).DefaultIfEmpty(-1).First();
            int a0 = NearestTo(arrow.Path[0].x, arrow.Path[0].y, -1);
            int a1 = a0 < 0 ? -1 : NearestTo(arrow.Path[^1].x, arrow.Path[^1].y, a0);
            if (a1 >= 0 && Pairable(a0, a1)) { var (pl, rf) = Ordered(a0, a1); Pair(pl, rf, PicturePortSource.ArrowPair); }
        }
        // Two terminals stacked at the drawing's left or right edge, within half a symbol of each other across.
        if (terminals.Count >= 2)
        {
            var xs = graph.Nodes.Select(n => n.X).Concat(terminals.Select(t => t.X)).ToList();
            foreach (double edge in new[] { xs.Min(), xs.Max() })
            {
                var at = Enumerable.Range(0, terminals.Count).Where(t => termPair[t] < 0 && Math.Abs(terminals[t].X - edge) <= 0.5 * s0)
                                   .OrderBy(t => terminals[t].Y).ToList();
                for (int k = 0; k + 1 < at.Count; k++)
                    if (Pairable(at[k], at[k + 1]) && Math.Abs(terminals[at[k]].X - terminals[at[k + 1]].X) <= 0.5 * s0)
                    {
                        Pair(at[k], at[k + 1], PicturePortSource.TerminalPair);
                        k++;
                    }
            }
        }
        // The return rail (R-imf6-3): with no ground drawn anywhere, a net holding the reference terminals of two ports, or
        // of one port and a source's minus, is ground — or, when no net does, the first port's reference, since a circuit
        // with no ground cannot be simulated. A terminal on it pairs with the nearest terminal off it.
        bool groundDrawn = groundMarks.Count > 0
                           || syms.Any(s => s.Disposition == SymbolDisposition.Recognised && s.Kind == ImageSymbolKind.Ground);
        var rails = new List<int>();
        if (!groundDrawn && pairs.Count > 0)
        {
            var sourceMinus = syms.Where(s => s.Disposition == SymbolDisposition.Recognised && s.Kind == ImageSymbolKind.Source && s.Pins.Count == 2)
                                  .Select(s => RootOf((s.Pins.FirstOrDefault(p => p.Name.Contains('-') || p.Name.Contains('−')) ?? s.Pins[1]).Node))
                                  .ToHashSet();
            rails = [.. pairs.GroupBy(p => TRoot(p.Ref)).Where(g => g.Key >= 0 && (g.Count() >= 2 || sourceMinus.Contains(g.Key))).Select(g => g.Key)];
            // Nothing else would give the circuit a ground at all: the leftmost port's reference is its return.
            if (rails.Count == 0) rails = [TRoot(pairs.MinBy(p => terminals[p.Plus].X).Ref)];
            foreach (int t in Enumerable.Range(0, terminals.Count).Where(t => termPair[t] < 0 && rails.Contains(TRoot(t))).ToList())
            {
                int u = Enumerable.Range(0, terminals.Count)
                    .Where(u => Pairable(t, u) && !rails.Contains(TRoot(u))
                                && (Math.Abs(terminals[u].X - terminals[t].X) <= 0.5 * s0 || Math.Abs(terminals[u].Y - terminals[t].Y) <= 0.5 * s0))
                    .OrderBy(u => TDist(t, u)).DefaultIfEmpty(-1).First();
                if (u >= 0) Pair(u, t, PicturePortSource.TerminalPair);
            }
            foreach (int rail in rails)
            {
                // One ground symbol at the rail's left end, the only thing added to the drawing.
                int leftEnd = Enumerable.Range(0, graph.Nodes.Count).Where(n => RootOf(n) == rail)
                                        .OrderBy(n => graph.Nodes[n].X).ThenByDescending(n => graph.Nodes[n].Y).First();
                Union(rail, G);
                groundMarks.Add((leftEnd, 0, 1));
            }
        }
        if (rails.Count > 0)
            report.Add(RecognitionFindingClass.PictureEmitNotes, rails.Count,
                "No ground is drawn, so the common return was taken as ground: " +
                $"{(rails.Count == 1 ? "the net" : "the nets")} holding the ports' reference terminals, with a ground symbol at " +
                $"{(rails.Count == 1 ? "its" : "each one's")} left end.");

        // ── the parts table (R-im10-7) ─────────────────────────────────────────────────────────────
        var rows = new List<PartRow>();
        var symbolOfRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rowPins = new Dictionary<string, IReadOnlyList<RecognisedPin>>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = options.Words ?? [];
        bool zeroOhm = options.UnknownAs == UnknownPart.ZeroOhm;
        int unknownCount = 0;
        // A supply net (R-imf5-4): one a supply mark ends, or a word naming a V… rail names.
        var supplyRoots = read.SupplyMarks.Select(m => RootOf(m.Node))
                              .Concat(labels.Names.Where(n => SupplyName(n.Name)).Select(n => RootOf(n.Node)))
                              .Where(r => r >= 0 && r != G).ToHashSet();
        bool IsBead(RecognisedSymbol s)
        {
            if (s.Disposition != SymbolDisposition.Recognised || s.Pins.Count != 2) return false;
            if (s.Kind is ImageSymbolKind.Resistor or ImageSymbolKind.Inductor
                && words.Any(t => string.Equals(t.Text, "BEAD", StringComparison.OrdinalIgnoreCase) && s.Region.Box.Distance(t.Box.CentreX, t.Box.CentreY) <= w))
                return true;
            return s.Template?.Fill == TemplateFill.Solid && s.Kind == ImageSymbolKind.Inductor && s.Pins.Any(p => supplyRoots.Contains(RootOf(p.Node)));
        }
        PartConnection Connection(IReadOnlyList<RecognisedPin> pins)
        {
            if (pins.Count == 1) return RootOf(pins[0].Node) == G ? PartConnection.Shorted : PartConnection.Shunt;
            int r0 = RootOf(pins[0].Node), r1 = RootOf(pins[1].Node);
            return (r0 == G, r1 == G) switch
            {
                (true, true) => PartConnection.Shorted,
                (true, false) or (false, true) => PartConnection.Shunt,
                _ => r0 == r1 ? PartConnection.Bridged : PartConnection.Series,
            };
        }
        // A block-diagram symbol (R-imf8-7): the System component it is and its pins in that component's order — the shape
        // readers' own, or an attenuator or a balun by the word written in its box (a transformer's beside it).
        IEnumerable<ImageWord> In(RecognisedSymbol s) => words.Where(t => s.Region.Box.Distance(t.Box.CentreX, t.Box.CentreY) == 0);
        (string Block, IReadOnlyList<RecognisedPin> Pins)? BlockOf(RecognisedSymbol s)
        {
            if (s.Kind == ImageSymbolKind.Block && s.Variant is { } b) return (b, s.Pins);
            if (s.Pins.Count == 2 && s.Disposition is SymbolDisposition.Recognised or SymbolDisposition.Unknown
                && s.Kind is ImageSymbolKind.Resistor or ImageSymbolKind.Unknown or ImageSymbolKind.Inductor && In(s).Any(t => AttenWord.IsMatch(t.Text.Trim())))
                return (DatasheetSymbols.AttenBlock, [.. s.Pins.OrderBy(p => Math.Round(p.X / (4 * w))).ThenBy(p => p.Y)]);
            bool balun = s.Kind == ImageSymbolKind.Ic && s.Disposition == SymbolDisposition.CutOut && In(s).Any(IsBalun)
                         || s.Kind == ImageSymbolKind.Transformer && s.Disposition == SymbolDisposition.Recognised && NearWords(s, words, s0).Any(IsBalun);
            return balun && BalunPins(s) is { } pins ? (DatasheetSymbols.BalunBlock, pins) : null;
        }
        // A balun's unbalanced lead, then its balanced pair: a box's one signal pin on one side and two on the side facing
        // it; a transformer's coil with one end on ground, then the other coil's two ends.
        IReadOnlyList<RecognisedPin>? BalunPins(RecognisedSymbol s)
        {
            bool Signal(RecognisedPin p) => RootOf(p.Node) is var r and >= 0 && r != G;
            if (s.Kind == ImageSymbolKind.Transformer)
            {
                var p = s.Pins;
                foreach (var (a, b) in new[] { ((p[0], p[1]), (p[2], p[3])), ((p[2], p[3]), (p[0], p[1])) })
                    if (Signal(a.Item1) != Signal(a.Item2) && Signal(b.Item1) && Signal(b.Item2))
                        return [Signal(a.Item1) ? a.Item1 : a.Item2, b.Item1, b.Item2];
                return null;
            }
            var box = s.Region.Box;
            int Side(RecognisedPin q)
            {
                double nx = (q.X - box.CentreX) / Math.Max(1, box.Width / 2.0), ny = (q.Y - box.CentreY) / Math.Max(1, box.Height / 2.0);
                return Math.Abs(nx) >= Math.Abs(ny) ? (nx < 0 ? 0 : 2) : (ny > 0 ? 1 : 3);
            }
            var sides = s.Pins.Where(Signal).GroupBy(Side).ToList();
            if (sides.Count != 2 || sides.Single(g => g.Count() == 1) is not { } one || sides.FirstOrDefault(g => g.Count() == 2) is not { } two
                || Math.Abs(one.Key - two.Key) != 2) return null;
            return [one.Single(), .. two.OrderBy(q => q.Y).ThenBy(q => q.X)];
        }
        var notesOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var renamed = new List<(string Read, string Written)>();
        for (int i = 0; i < syms.Count; i++)
        {
            var s = syms[i];
            bool bead = IsBead(s);
            var block = BlockOf(s);
            PartKind? device = block is not null ? PartKind.Block : s.Disposition != SymbolDisposition.Recognised ? null : s.Kind switch
            {
                _ when bead => PartKind.Bead,
                ImageSymbolKind.Diode when s.Pins.Count == 2 => PartKind.D,
                ImageSymbolKind.Transistor when s.Pins.Count == 3 => PartKind.Q,
                ImageSymbolKind.Source when s.Variant == "current" && s.Pins.Count == 2 => PartKind.I,
                ImageSymbolKind.Source => PartKind.V,
                ImageSymbolKind.Transformer when s.Pins.Count >= 4 => PartKind.K,
                ImageSymbolKind.Amplifier when s.Pins.Count >= 2 => PartKind.Amp,
                _ => null,
            };
            bool twoPin = device is null && s.Pins.Count == 2 && s.Kind is ImageSymbolKind.Resistor or ImageSymbolKind.Inductor
                                              or ImageSymbolKind.Capacitor or ImageSymbolKind.Unknown
                          && s.Disposition is SymbolDisposition.Recognised or SymbolDisposition.Unknown;
            if (!twoPin && device is null && s.Disposition != SymbolDisposition.CutOut) continue;
            var l = lab[i];
            string prefix = device switch
            {
                PartKind.Bead => "FB", PartKind.I => "I", PartKind.V => "V", PartKind.K => "T",
                PartKind.Block => block!.Value.Block switch
                {
                    DatasheetSymbols.FilterBlock => "FL", DatasheetSymbols.AttenBlock => "AT", DatasheetSymbols.SwitchBlock => "SW",
                    DatasheetSymbols.BalunBlock => "T", _ => "U",
                },
                _ => LabelAssociation.Prefix(s.Kind) ?? "U",
            };
            // A part of no known kind, unnamed on the picture, is ?n (O3).
            bool unknown = zeroOhm && twoPin && s.Kind == ImageSymbolKind.Unknown;
            // A designator as read is made legal and unique (R-imf7-3, R-imf7-10): Ln-1 is Ln_1, a second Cblock Cblock_2.
            string? asRead = l is { Generated: false } ? l.Designator : null;
            string refdes = Unique(unknown && l is null or { Generated: true } ? $"?{++unknownCount}"
                                   : l?.Designator is { } d && !(block is not null && l.Generated) ? ValueGrammar.LegalDesignator(d) : $"{prefix}{i + 1}", taken);
            if (asRead is not null && refdes != asRead) renamed.Add((asRead, refdes));
            var (x, y) = read.ToSource(s.Region.Box.CentreX, s.Region.Box.CentreY);
            var evidence = new Dictionary<PartField, PartEvidenceSource>
            {
                [PartField.Refdes] = l is { Generated: false } ? PartEvidenceSource.Picture : PartEvidenceSource.Generated,
            };
            var notes = new List<string>();
            if (l is { Contradicts: true }) notes.Add($"{refdes} names a different kind than its symbol is drawn as");
            if (asRead is not null && refdes != asRead) notes.Add($"read as '{asRead}'");
            foreach (var c in l?.Comments ?? []) notes.Add($"'{c.Text}' is written beside it");
            List<PartTerminal> Terminals(IEnumerable<RecognisedPin> pins) => [.. pins.Select(p =>
            {
                var (px, py) = read.ToSource(p.X, p.Y);
                return new PartTerminal((long)Math.Round(px), (long)Math.Round(py), null, -1, RootOf(p.Node) == G);
            })];
            PartRow Row(string name, PartKind kind, IReadOnlyList<RecognisedPin> pins, PartConnection connection) => new()
            {
                Refdes = name, Kind = kind, Connection = connection, PadCount = pins.Count,
                X = (long)Math.Round(x), Y = (long)Math.Round(y), Confidence = l?.Confidence ?? s.Confidence,
                Evidence = new Dictionary<PartField, PartEvidenceSource>(evidence) { [PartField.Kind] = PartEvidenceSource.Picture },
                Notes = notes, Terminals = Terminals(pins),
            };
            void Add(PartRow row, IReadOnlyList<RecognisedPin> pins)
            {
                if (row.IsModelled && row.TakesValue && row.Value is null && row.Variable is null && !(zeroOhm && row.Kind == PartKind.Unknown))
                    row = row with { Variable = row.DefaultVariable };
                rows.Add(row);
                symbolOfRow[row.Refdes] = i;
                rowPins[row.Refdes] = pins;
            }

            if (device == PartKind.K)
            {
                // A transformer (R-imf5-5): its coils as drawn, each an L on its own two pins — a centre tap splitting its
                // coil in two — and one coupling row whose value is k.
                var p = s.Pins;
                var coils = p.Count >= 5 ? new[] { (p[0], p[1]), (p[2], p[4]), (p[4], p[3]) } : new[] { (p[0], p[1]), (p[2], p[3]) };
                for (int k = 0; k < coils.Length; k++)
                {
                    var pair = new[] { coils[k].Item1, coils[k].Item2 };
                    Add(Row(Unique($"{refdes}_L{k + 1}", taken), PartKind.L, pair, Connection(pair)), pair);
                }
                double? kv = NearWords(s, words, s0).Select(t => ReadCoupling(t.Text)).FirstOrDefault(v => v is not null);
                Add(Row(refdes, PartKind.K, [], PartConnection.Series) with
                {
                    Value = kv, PartNumber = PartNumberOf(l),
                    Evidence = kv is null ? evidence : new Dictionary<PartField, PartEvidenceSource>(evidence) { [PartField.Value] = PartEvidenceSource.Picture },
                }, []);
                continue;
            }
            if (block is { } bk)
            {
                Add(Row(refdes, PartKind.Block, bk.Pins, PartConnection.MultiPin) with { Variant = bk.Block }, bk.Pins);
                continue;
            }
            if (device is { } dk)
            {
                var row = Row(refdes, dk, s.Pins, dk is PartKind.Q or PartKind.Amp ? PartConnection.MultiPin : Connection(s.Pins));
                row = dk switch
                {
                    PartKind.Q => row with { Variant = s.Variant?.ToUpperInvariant(), PartNumber = PartNumberOf(l) },
                    PartKind.D => row with { PartNumber = PartNumberOf(l) },
                    PartKind.V => row with { Variant = s.Variant == "ac" ? "AC" : "DC" },
                    _ => row,
                };
                // A source's value: the word after or under its designator (V₁12V, R-imf7-11), else the nearest that is one —
                // as read, or as its glyphs' bests spell it: a volt is no value of a passive's, so the grammar reads an
                // italic 12V as 12U, 12 µ, whose V was the glyph's best.
                double? ValueOf(ImageWord t) => SourceValue(t.Text, dk) ?? SourceValue(t.Seen, dk);
                if (dk is PartKind.V or PartKind.I && row.TakesValue
                    && ((l?.ValueWord is { } vw ? ValueOf(vw) : null)
                        ?? NearWords(s, words, s0).Select(ValueOf).FirstOrDefault(v => v is not null)) is { } sv)
                    row = row with { Value = sv, Evidence = new Dictionary<PartField, PartEvidenceSource>(row.Evidence) { [PartField.Value] = PartEvidenceSource.Picture } };
                if (dk == PartKind.V && s.Pins.Count == 1) row = row with { Connection = PartConnection.Shunt };
                Add(row, s.Pins);
                continue;
            }

            // A polarised capacitor (R-imf5-1): a + beside one plate makes that plate's lead pin 1.
            var pinsDrawn = s.Pins;
            if (s.Kind == ImageSymbolKind.Capacitor && s.Pins.Count == 2
                && NearWords(s, words, s0).FirstOrDefault(t => t.Text.Trim() == "+") is { } plus)
            {
                double D(RecognisedPin p) => Math.Pow(p.X - plus.Box.CentreX, 2) + Math.Pow(p.Y - plus.Box.CentreY, 2);
                if (D(s.Pins[1]) < D(s.Pins[0])) pinsDrawn = [s.Pins[1], s.Pins[0]];
            }

            PartRow made;
            if (!twoPin)
                made = new PartRow
                {
                    Refdes = refdes, Kind = s.Kind == ImageSymbolKind.Ic ? PartKind.IC : PartKind.MultiPin,
                    Connection = PartConnection.MultiPin, PadCount = s.Pins.Count, PartNumber = PartNumberOf(l),
                    X = (long)Math.Round(x), Y = (long)Math.Round(y), Confidence = l?.Confidence ?? s.Confidence,
                    Evidence = evidence, Notes = notes, Terminals = Terminals(s.Pins),
                };
            else
            {
                var kind = s.Kind switch
                {
                    ImageSymbolKind.Resistor => PartKind.R, ImageSymbolKind.Inductor => PartKind.L,
                    ImageSymbolKind.Capacitor => PartKind.C, _ => PartKind.Unknown,
                };
                if (kind != PartKind.Unknown) evidence[PartField.Kind] = PartEvidenceSource.Picture;
                if (s.Variable) notes.Add($"{refdes} is a variable resistor: its value is one setting");
                double? value = null;
                if (l?.Value is { } v)
                {
                    var dim = PartReading.DimensionOf(kind == PartKind.Unknown ? PartKind.C : kind);
                    if (v.NotFitted) kind = PartKind.Open;
                    else if (v.Fits(dim)) { value = v.Si; evidence[PartField.Value] = PartEvidenceSource.Picture; }
                    else notes.Add($"'{l.ValueWord!.Text}' is not a {dim.ToString().ToLowerInvariant()}, so the value was not applied");
                }
                made = new PartRow
                {
                    Refdes = refdes, Kind = kind, Connection = Connection(pinsDrawn), Value = value, PadCount = 2,
                    X = (long)Math.Round(x), Y = (long)Math.Round(y), Confidence = l?.Confidence ?? s.Confidence,
                    Evidence = evidence, Notes = notes, Terminals = Terminals(pinsDrawn),
                };
            }
            Add(made, pinsDrawn);
        }
        if (renamed.Count > 0)
            report.Add(RecognitionFindingClass.PictureEmitNotes, renamed.Count,
                       "Designators written as the schematic takes them: " + string.Join(", ", renamed.Select(r => $"{r.Read} as {r.Written}")) + ".");
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

        IReadOnlyList<RecognisedPin> PinsOf(PartRow r) => rowPins.TryGetValue(r.BoardRefdes, out var p) ? p : syms[SymbolOf(r)].Pins;

        // A short in the table joins its two nets.
        foreach (var r in parts.Rows.Where(r => r.Kind == PartKind.Short && SymbolOf(r) >= 0))
        {
            var pins = PinsOf(r);
            if (pins.Count == 2 && NetOf(pins[0].Node) >= 0 && NetOf(pins[1].Node) >= 0) Union(NetOf(pins[0].Node), NetOf(pins[1].Node));
        }

        // ── ports (D13, R-imf6-1 … R-imf6-5) ────────────────────────────────────────────────────────
        var ports = new List<PortFound>();
        bool Ported(int root) => ports.Any(p => p.Root == root);
        (double X, double Y) DirAt(SymbolRegion region, int node)
        {
            var a = region.Attachments.FirstOrDefault(t => t.Node == node);
            return a is null ? (0, 0) : (a.DirX, a.DirY);
        }
        // A lead ending in paper that a name beyond it calls a port is a net of its own (R-imf6-4).
        var leadRoot = new Dictionary<(int Symbol, int Pin), int>();
        int PinRoot(int symbol, RecognisedPin p) =>
            p.Node >= 0 ? RootOf(p.Node) : leadRoot.TryGetValue((symbol, p.Index), out int r) ? r : -1;

        // Port symbols that are no terminal: a Term box, a pin flag.
        for (int i = 0; i < syms.Count; i++)
        {
            var s = syms[i];
            if (s.Disposition != SymbolDisposition.Recognised || s.Kind != ImageSymbolKind.Port || s.Pins.Count == 0
                || PictureTerminals.IsTerminal(s)) continue;
            var pin = s.Pins.FirstOrDefault(p => RootOf(p.Node) is var r and >= 0 && r != G) ?? s.Pins[0];
            int root = RootOf(pin.Node);
            if (root < 0 || root == G || Ported(root)) continue;
            var (dx, dy) = DirAt(s.Region, pin.Node);
            ports.Add(new PortFound(lab[i] is { Generated: false } l ? l.Designator : null, root, G, PicturePortSource.Symbol,
                                    pin.X, pin.Y, dx, dy, i, pin.Node));
        }
        // Terminals: a pair is one port, its reference the other terminal's net; one alone, named or not, a port to ground.
        string? TerminalName(int t)
        {
            var n = labels.Names.FirstOrDefault(n => n.AtTerminal && n.Node == terminals[t].Node);
            if (n is null && terminals[t].Symbol >= 0 && lab[terminals[t].Symbol] is { Generated: false } l) return l.Designator;
            return n?.Name;
        }
        PortFound AtTerminal(int t, string? name, int refRoot, PicturePortSource source)
        {
            var term = terminals[t];
            int node = term.Node;
            var (x, y) = (graph.Nodes[node].X, graph.Nodes[node].Y);
            var (dx, dy) = term.Symbol >= 0 ? DirAt(syms[term.Symbol].Region, node) : graph.Incoming(node);
            return new PortFound(name, TRoot(t), refRoot, source, x, y, dx, dy, term.Symbol, node);
        }
        foreach (var (plus, reference, source) in pairs)
        {
            if (TRoot(plus) == G || Ported(TRoot(plus))) continue;
            string? name = TerminalName(plus) ?? TerminalName(reference);
            ports.Add(AtTerminal(plus, name is not null && PortWords.IsSupply(name) ? null : name, TRoot(reference), source)
                      with { RefNode = terminals[reference].Node });
        }
        for (int t = 0; t < terminals.Count; t++)
        {
            if (termPair[t] >= 0 || TRoot(t) < 0 || TRoot(t) == G || Ported(TRoot(t))) continue;
            string? name = TerminalName(t);
            if (name is not null && PortWords.IsSupply(name)) continue;   // a supply terminal: a named net (IMF-8)
            ports.Add(AtTerminal(t, name, G, name is null ? PicturePortSource.Terminal : PicturePortSource.NamedTerminal));
        }
        // Names beyond open ends (R-imf6-4).
        foreach (var n in labels.Names.Where(n => n.Far && n.Port))
        {
            if (n.Lead is { } lead)
            {
                var pin = syms[lead.Symbol].Pins.FirstOrDefault(p => p.Index == lead.Pin);
                var a = syms[lead.Symbol].Region.Attachments.FirstOrDefault(a => a.Node < 0 && pin is not null && a.X == pin.X && a.Y == pin.Y);
                if (pin is null || a is null || leadRoot.ContainsKey(lead)) continue;
                int root = G + 1 + leadRoot.Count;
                leadRoot[lead] = root;
                ports.Add(new PortFound(n.Name, root, G, PicturePortSource.FarName, pin.X, pin.Y, -a.DirX, -a.DirY, -1, -1));
                continue;
            }
            int r = RootOf(n.Node);
            if (r < 0 || r == G) continue;
            if (ports.FindIndex(p => p.Root == r) is var at and >= 0)
            {
                if (ports[at].Name is null) ports[at] = ports[at] with { Name = n.Name };
                continue;
            }
            var node = graph.Nodes[n.Node];
            var (ix, iy) = graph.Incoming(n.Node);
            ports.Add(new PortFound(n.Name, r, G, PicturePortSource.FarName, node.X, node.Y, ix, iy, -1, n.Node));
        }
        // Net names that name a port (D13).
        foreach (var n in labels.Names)
        {
            if (supplyNodes.Contains(n.Node) || n.AtTerminal || n.Far) continue;
            if (!(n.Word.Reading?.IsPortWord == true || PortWords.IsInput(n.Name) || PortWords.IsOutput(n.Name))) continue;
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
            ports.Add(new PortFound(n.Name, root, G, PicturePortSource.Name, node.X, node.Y, ix, iy, -1, n.Node));
        }
        // The drawing's own ports in order: by the numbers their names carry when every one carries one; else an input
        // first, an output next, then the rest left to right, top to bottom (R-imf6-5).
        if (ports.Count > 0)
        {
            var numbers = ports.Select(p => TrailingNumber(p.Name)).ToList();
            ports = numbers.All(n => n is not null) && numbers.Distinct().Count() == numbers.Count
                ? [.. ports.Zip(numbers).OrderBy(t => t.Second).Select(t => t.First)]
                : [.. ports.OrderBy(p => PortWords.Rank(p.Name)).ThenBy(p => Math.Round(p.X / (8 * w))).ThenBy(p => p.Y)];
        }
        // ── instances ───────────────────────────────────────────────────────────────────────────────
        var protos = new List<Proto>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var portInfo = new List<(Proto Proto, PicturePortSource Source, double X, double Y)>();
        var portReferences = new Dictionary<string, int>(StringComparer.Ordinal);
        // An AC source is a P1Tone (O2): the circuit's input, numbered first among the ports.
        var tones = parts.Rows.Where(r => r.Kind == PartKind.V && r.Variant == "AC" && SymbolOf(r) >= 0 && PinsOf(r).Count == 2
                                          && r.Connection is PartConnection.Series or PartConnection.Shunt)
                              .OrderBy(r => r.X).ThenBy(r => r.Y).ToList();
        for (int k = 0; k < ports.Count; k++)
        {
            var p = ports[k];
            int number = k + 1 + tones.Count;
            string name = Unique(RecognitionEmit.Identifier(p.Name ?? $"P{number}", "P"), names);
            var proto = new Proto { Type = "Port", Name = name, Roots = [p.Root, p.RefRoot], Symbol = p.Symbol, PortNumber = number };
            proto.Parameters.Add(new ParameterAssignment("Num", number.ToString(CultureInfo.InvariantCulture)));
            // The impedance printed under the port's designator, when one was read (IM-12); else 50 Ω.
            double? z = p.Symbol >= 0 && lab[p.Symbol]?.LineParameters is { } lp
                ? lp.FirstOrDefault(q => q.Key is "Z" or "Z0")?.Si : null;
            proto.Parameters.Add(z is { } zv
                ? new ParameterAssignment("Z", RecognitionEmit.Split(PartsTable.ValueText(zv, PartKind.R, digits)).Number, "Ohm")
                : new ParameterAssignment("Z", "50", "Ohm"));
            proto.PicturePins.Add((p.X, p.Y, p.DirX, p.DirY));
            proto.PinNodes.Add(p.Node);
            if (p.RefNode >= 0) portReferences[name] = p.RefNode;
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
        var instanceOfRow = new Dictionary<string, Proto>(StringComparer.OrdinalIgnoreCase);
        var unknownParts = new List<string>();
        void Variable(string name, double value, PartKind kind)
        {
            if (variables.Any(v => v.Name == name)) return;
            var (number, unit) = RecognitionEmit.Split(PartsTable.ValueText(value, kind, digits));
            var tune = RecognitionEmit.TuneEntry(name, value, kind, digits);
            variables.Add((name, number, unit, tune.Min!, tune.Max!));
        }

        foreach (var row in parts.Rows.Where(r => r.IsModelled && r.IsPassive && SymbolOf(r) >= 0))
        {
            int i = SymbolOf(row);
            var s = syms[i];
            var rp = PinsOf(row);
            if (rp.Count != 2) continue;
            var roots = rp.Select(p => PinRoot(i, p)).ToList();
            Proto proto;
            if (zeroOhm && row.Kind == PartKind.Unknown)
            {
                // A part of no known kind (O3): 0 Ω, the circuit as drawn, its designator ?n kept as the picture's.
                proto = new Proto { Type = "R", Name = Unique(row.Refdes, names), Roots = roots, Symbol = i };
                proto.Parameters.Add(new ParameterAssignment("R", "0", "Ohm"));
                unknownParts.Add(proto.Name);
                AddPins(proto, s, rp);
                protos.Add(proto);
                instanceOfRow[row.Refdes] = proto;
                continue;
            }
            string name = Unique(RecognitionEmit.Identifier(row.Refdes, row.ParameterName), names);
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
            AddPins(proto, s, rp);
            protos.Add(proto);
            instanceOfRow[row.Refdes] = proto;
        }

        // ── the picture's own devices (IMF-5) ───────────────────────────────────────────────────────
        var deviceNotes = new List<string>();
        void Raw(string name, string number, string? unit, string min, string max)
        {
            if (!variables.Any(v => v.Name == name)) variables.Add((name, number, unit, min, max));
        }
        string Spell(double v) => RecognitionEmitOptions.Spell(v, digits);
        foreach (var row in parts.Rows.Where(r => r.Kind is PartKind.Q or PartKind.V or PartKind.I or PartKind.D or PartKind.Bead or PartKind.Amp
                                                                 or PartKind.Block
                                                  && SymbolOf(r) >= 0))
        {
            int i = SymbolOf(row);
            var s = syms[i];
            var rp = PinsOf(row);
            var roots = rp.Select(p => PinRoot(i, p)).ToList();
            string id = RecognitionEmit.Identifier(row.Refdes, row.Kind.ToString());
            string name = Unique(id, names);
            Proto proto;
            switch (row.Kind)
            {
                case PartKind.Q:
                {
                    // O1: circuitRF's own transistor, pins in its own order — the template's — every parameter its default.
                    string type = row.Variant switch { "PNP" => "BJT_PNP", "NFET" => "FET_Curtice", "PFET" => "PFET_Curtice", _ => "BJT_NPN" };
                    proto = new Proto { Type = type, Name = name, Roots = roots, Symbol = i };
                    string what = row.Variant switch { "PNP" => "a p-n-p BJT", "NFET" => "an n-channel FET", "PFET" => "a p-channel FET", _ => "an n-p-n BJT" };
                    deviceNotes.Add($"{name} is {what} with the default model — set its parameters.");
                    break;
                }
                case PartKind.D:
                    proto = new Proto { Type = "Diode", Name = name, Roots = roots, Symbol = i };
                    break;
                case PartKind.Amp:
                {
                    // O5: the System Amp — in+ the input lead, out+ the output lead, in− and out− on ground. A lead on its
                    // other sides to ground is its reference; one to anything else (a supply) ends at its body.
                    if (rp.Count < 2) continue;
                    proto = new Proto { Type = "Amp", Name = name, Roots = [roots[0], G, roots[1], G], Symbol = i };
                    Raw($"{id}_Gain", "20", null, "0", "40");
                    proto.Parameters.Add(new ParameterAssignment("Gain", $"{id}_Gain"));
                    var fed = rp.Skip(2).Where(p => PinRoot(i, p) is var r && r >= 0 && r != G).ToList();
                    deviceNotes.Add($"{name} is an amplifier triangle: an ideal Amp, its gain the global {id}_Gain — set it." +
                                    (fed.Count > 0 ? $" {Plural(fed.Count, "lead", "leads")} to its supply end at its body: the supply pin of an ideal amplifier carries no current." : ""));
                    rp = [rp[0], rp[1]];
                    break;
                }
                case PartKind.Block:
                {
                    // R-imf8-7: the System component, each port's minus on ground, every parameter at its default but
                    // what the drawing settles — a filter's form, a switch's throws and position.
                    if (row.Variant is not { } type) continue;
                    proto = new Proto { Type = type, Name = name, Roots = [.. roots.SelectMany(r => new[] { r, G })], Symbol = i };
                    foreach (var key in new[] { "Form", "Throws", "State" })
                        if (s.Settings.TryGetValue(key, out var v)) proto.Parameters.Add(new ParameterAssignment(key, v));
                    deviceNotes.Add(BlockNote(name, type, s.Settings));
                    rp = [.. rp.Where(p => !double.IsNaN(p.X))];
                    break;
                }
                case PartKind.Bead:
                    proto = new Proto { Type = "Bead", Name = name, Roots = roots, Symbol = i };
                    proto.Parameters.Add(new ParameterAssignment("Rdc", "0.01", "Ohm"));
                    proto.Parameters.Add(new ParameterAssignment("L", "0", "uH"));
                    proto.Parameters.Add(new ParameterAssignment("Rp", "0", "Ohm"));
                    proto.Parameters.Add(new ParameterAssignment("Cp", "0", "pF"));
                    deviceNotes.Add($"{name} is a ferrite bead with the default model — set its parameters.");
                    break;
                case PartKind.V when row.Variant == "AC":
                {
                    if (rp.Count != 2 || !tones.Contains(row)) continue;
                    // P1Tone (O2): + is the pin not on ground; its tone and power from a word beside it, else globals.
                    if (roots[0] == G && roots[1] != G) { roots.Reverse(); rp = [rp[1], rp[0]]; }
                    int number = tones.IndexOf(row) + 1;
                    proto = new Proto { Type = "P1Tone", Name = name, Roots = roots, Symbol = i, PortNumber = number };
                    proto.Parameters.Add(new ParameterAssignment("Num", number.ToString(CultureInfo.InvariantCulture)));
                    var near = NearWords(s, words, s0).ToList();
                    double? f = near.Select(t => ReadFrequency(t.Text)).FirstOrDefault(v => v is not null);
                    double? pw = near.Select(t => ReadPower(t.Text)).FirstOrDefault(v => v is not null);
                    if (pw is { } pv) proto.Parameters.Add(new ParameterAssignment("Pavl", Spell(pv), "dBm"));
                    else { Raw($"{id}_Pavl", "0", "dBm", "-30 dBm", "30 dBm"); proto.Parameters.Add(new ParameterAssignment("Pavl", $"{id}_Pavl")); }
                    proto.Parameters.Add(new ParameterAssignment("Z", "50", "Ohm"));
                    if (f is { } fv) proto.Parameters.Add(new ParameterAssignment("Freq", Spell(fv / 1e9), "GHz"));
                    else { Raw($"{id}_Freq", "1", "GHz", "0.1 GHz", "10 GHz"); proto.Parameters.Add(new ParameterAssignment("Freq", $"{id}_Freq")); }
                    portInfo.Add((proto, PicturePortSource.Tone, rp[0].X, rp[0].Y));
                    break;
                }
                default:
                {
                    // Vdc (O2) or a DC current source: + first, the minus on ground where the picture grounds it; a supply
                    // circle on one lead is drawn over its ground.
                    if (rp.Count == 2 && roots[0] == G && roots[1] != G && !s.Pins[0].Name.Contains('+')) { roots.Reverse(); rp = [rp[1], rp[0]]; }
                    if (rp.Count == 1) roots.Add(G);
                    bool current = row.Kind == PartKind.I;
                    proto = new Proto { Type = current ? "I_1Tone" : "Vdc", Name = name, Roots = roots, Symbol = i };
                    string key = current ? "Idc" : "Vdc", unit = current ? "mA" : "V";
                    string value;
                    if (row.Value is { } dc) value = current ? Spell(dc * 1e3) : Spell(dc);
                    else
                    {
                        value = RecognitionEmit.Identifier(row.Variable ?? row.DefaultVariable, "V");
                        Raw(value, "0", unit, current ? "-100 mA" : "-30 V", current ? "100 mA" : "30 V");
                    }
                    if (current)
                    {
                        proto.Parameters.Add(new ParameterAssignment("I", "0", "mA"));
                        proto.Parameters.Add(new ParameterAssignment("Freq", "1", "GHz"));
                    }
                    proto.Parameters.Add(row.Value is null ? new ParameterAssignment(key, value) : new ParameterAssignment(key, value, unit));
                    break;
                }
            }
            AddPins(proto, s, rp);
            protos.Add(proto);
            instanceOfRow[row.Refdes] = proto;
        }
        // A transformer (R-imf5-5): its coils are L rows above; each pair of them coupled by one Mutual whose M is k√(L₁L₂).
        foreach (var row in parts.Rows.Where(r => r.Kind == PartKind.K && SymbolOf(r) >= 0))
        {
            var coils = parts.Rows.Where(r => r.Kind == PartKind.L && SymbolOf(r) == SymbolOf(row) && instanceOfRow.ContainsKey(r.Refdes))
                                  .Select(r => instanceOfRow[r.Refdes]).ToList();
            if (coils.Count < 2) continue;
            string id = RecognitionEmit.Identifier(row.Refdes, "K");
            string k;
            if (row.Value is { } kv) k = Spell(kv);
            else { k = RecognitionEmit.Identifier(row.Variable ?? row.DefaultVariable, "K"); Raw(k, "0.99", null, "0.5", "1"); }
            string L(Proto p) => p.Parameters.First(q => q.Name == "L") is var a && a.Unit is { } u ? $"({a.Expression}*{UnitScale(u)})" : a.Expression;
            for (int a = 0; a < coils.Count; a++)
                for (int b = a + 1; b < coils.Count; b++)
                {
                    var proto = new Proto { Type = "Mutual", Name = Unique(coils.Count == 2 ? id : $"{id}_{a + 1}{b + 1}", names), Roots = [], Symbol = SymbolOf(row) };
                    proto.Parameters.Add(new ParameterAssignment("Inductor1", $"\"{coils[a].Name}\""));
                    proto.Parameters.Add(new ParameterAssignment("Inductor2", $"\"{coils[b].Name}\""));
                    proto.Parameters.Add(new ParameterAssignment("M", $"{k}*sqrt({L(coils[a])}*{L(coils[b])})"));
                    protos.Add(proto);
                }
            deviceNotes.Add($"{row.Refdes} is a transformer: {string.Join(" and ", coils.Select(c => c.Name))} coupled by k = {k}.");
        }

        // A multi-pin symbol — a box, a transistor, an amplifier — is an S-parameter block with one port per wired pin
        // and a pin on ground its reference: the shape an RF schematic draws a black box in, and the part it is drawn as.
        var blocks = new List<(string Name, int Ports)>();
        var ics = new Dictionary<string, IcReading>(StringComparer.Ordinal);
        var icNotes = new List<string>();
        foreach (var row in parts.Rows.Where(r => r.Kind is PartKind.MultiPin or PartKind.Connector or PartKind.IC && SymbolOf(r) >= 0)
                                      .OrderBy(r => r.X).ThenBy(r => r.Y))
        {
            int i = SymbolOf(row);
            var s = syms[i];
            List<RecognisedPin> signal;
            RecognisedPin? reference;
            IcReading? ic = null;
            if (row.Kind == PartKind.IC)
            {
                // R-imf8-1: the pins read by number and name; the grounded ones its reference, the ones wired to nothing
                // left out, the rest its ports in pin-number order.
                var read1 = IcPins.Read(s, words, PictureTerminals.TextHeight(read));
                bool Elsewhere(RecognisedPin p, int root) =>
                    syms.Where((_, k) => k != i).SelectMany(o => o.Pins).Any(q => RootOf(q.Node) == root)
                    || s.Pins.Any(q => q != p && RootOf(q.Node) == root)
                    || ports.Any(o => o.Root == root) || labels.Names.Any(n => RootOf(n.Node) == root) || supplyRoots.Contains(root);
                var refs = new List<(RecognisedPin Pin, IcPin Read)>();
                var open = new List<(RecognisedPin Pin, IcPin Read)>();
                var sig = new List<(RecognisedPin Pin, IcPin Read)>();
                foreach (var p in s.Pins)
                {
                    var pr = read1[p.Index];
                    int root = RootOf(p.Node);
                    if (root == G || pr.Name is { } n0 && IcPins.IsGround(n0)) refs.Add((p, pr));
                    else if (root < 0 || pr.Name is { } n1 && IcPins.IsNoConnect(n1) || !Elsewhere(p, root)) open.Add((p, pr));
                    else sig.Add((p, pr));
                }
                bool byNumber = sig.Count > 0 && sig.All(t => t.Read.Number is not null) && sig.Select(t => t.Read.Number).Distinct().Count() == sig.Count;
                if (byNumber) sig = [.. sig.OrderBy(t => t.Read.Number)];
                signal = [.. sig.Select(t => t.Pin)];
                reference = refs.Select(t => t.Pin).FirstOrDefault(p => RootOf(p.Node) == G);
                ic = new IcReading([.. sig.Select(t => t.Read)], byNumber, [.. refs.Select(t => t.Read)], [.. open.Select(t => t.Read)]);
            }
            else
            {
                signal = [.. s.Pins.Where(p => RootOf(p.Node) is var r and >= 0 && r != G)
                                   .OrderBy(p => Math.Round(p.X / (4 * w))).ThenBy(p => p.Y)];
                reference = s.Pins.FirstOrDefault(p => RootOf(p.Node) == G);
            }
            if (signal.Count == 0) continue;
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
            if (ic is null) { blocks.Add((name, signal.Count)); continue; }
            ics[name] = ic;
            static string PinText(IcPin p) => p.Number is { } n ? (p.Name is { } nm ? $"{n} {nm}" : $"{n}") : p.Name ?? "?";
            icNotes.Add($"{name} is an IC drawn as a box with {Plural(s.Pins.Count, "pin", "pins")}: " +
                        $"{Plural(ic.Ports.Count, "port", "ports")} {(ic.ByNumber ? "by pin number" : "in the order a package numbers its pins")} " +
                        $"({string.Join(", ", ic.Ports.Select(PinText))})" +
                        (ic.Reference.Count > 0 ? $", {Plural(ic.Reference.Count, "pin", "pins")} its reference ({string.Join(", ", ic.Reference.Select(PinText))})" : "") +
                        (ic.Open.Count > 0 ? $", {Plural(ic.Open.Count, "pin", "pins")} wired to nothing left out ({string.Join(", ", ic.Open.Select(PinText))})" : "") +
                        ". Give it a Touchstone file whose ports follow its pin numbers.");
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
                var roots = s.Pins.Take(2).Select(p => PinRoot(i, p)).ToList();
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
        if (ports.Count + tones.Count > 0)
            tb.Analyses.Add(new SParameterAnalysis(RecognitionEmit.AnalysisName, options.Emit.Sweep ?? RecognitionEmitOptions.DefaultSweep));

        // ── the report ──────────────────────────────────────────────────────────────────────────────
        int netCount = protos.SelectMany(p => p.Roots).Where(r => r >= 0 && r != G).Distinct().Count();
        report.Add(RecognitionFindingClass.PictureNets, netCount,
            $"{Plural(netCount, "net", "nets")} besides ground, from {Plural(graph.Segments.Count, "wire segment", "wire segments")}.");
        var picturePorts = portInfo.Select(t => new PicturePort(t.Proto.PortNumber, t.Proto.Name, netName[t.Proto.Roots[0]], t.Source,
                                                                 read.ToSource(t.X, t.Y).X, read.ToSource(t.X, t.Y).Y)).ToList();
        foreach (var (source, what) in new (PicturePortSource, string)[]
                 {
                     (PicturePortSource.Tone, "from AC sources (P1Tone)"),
                     (PicturePortSource.Symbol, "from port symbols"),
                     (PicturePortSource.NamedTerminal, "from named terminals"),
                     (PicturePortSource.TerminalPair, "from terminal pairs"),
                     (PicturePortSource.ArrowPair, "from terminal pairs a voltage arrow spans"),
                     (PicturePortSource.FarName, "from names beyond open wire ends"),
                     (PicturePortSource.Terminal, "from single terminals, to ground"),
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
        if (ports.Count + tones.Count == 0) report.Add(RecognitionFindingClass.PictureNoPort, 1, NoPortLine);
        report.Add(RecognitionFindingClass.PictureEmitNotes, unknownParts.Count,
            $"{string.Join(", ", unknownParts)} {(unknownParts.Count == 1 ? "is a part" : "are parts")} of no known kind, " +
            $"{(unknownParts.Count == 1 ? "made a 0 Ω resistor" : "each made a 0 Ω resistor")} so the circuit stays as drawn: " +
            "set the kind in the parts table.");
        report.Add(RecognitionFindingClass.PictureEmitNotes, deviceNotes.Count, string.Join(" ", deviceNotes));
        report.Add(RecognitionFindingClass.PictureEmitNotes, icNotes.Count, string.Join(" ", icNotes));
        var settings = parts.Rows.Where(r => r.Kind == PartKind.R && SymbolOf(r) >= 0 && syms[SymbolOf(r)].Variable).Select(r => r.Refdes).ToList();
        report.Add(RecognitionFindingClass.PictureEmitNotes, settings.Count,
            $"{string.Join(", ", settings)} {(settings.Count == 1 ? "is a variable resistor" : "are variable resistors")}: " +
            $"{(settings.Count == 1 ? "its value is" : "each value is")} one setting.");
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
        // A name a port was made of is the port's name, not a label beside it (R-imf6-7).
        var portNamed = labels.Names.Where(n => (n.Far || n.AtTerminal) && n.Port).Select(n => n.Node).ToHashSet();
        foreach (int node in labels.Names.Select(n => n.Node).Where(n => !portNamed.Contains(n))
                                   .Concat(read.SupplyMarks.Select(m => m.Node)).Distinct())
            if (RootOf(node) is var r and >= 0 && r != G && netName.TryGetValue(r, out var net)) named.Add((node, net));
        return new SchematicImageCircuit(tb)
        {
            PinNodes = protos.ToDictionary(p => p.Name, p => (IReadOnlyList<int>)p.PinNodes, StringComparer.Ordinal),
            NodeNets = nodeNets, Grounds = grounds, NamedNodes = named, HiddenSegments = hidden,
            PortReferences = portReferences, UnknownParts = unknownParts, Ics = ics,
            Exact = exact, Frame = frame, Parts = parts, Ports = picturePorts, Variables = [.. variables.Select(v => v.Name)],
            SymbolOf = protos.Where(p => p.Symbol >= 0).ToDictionary(p => p.Name, p => p.Symbol, StringComparer.Ordinal),
            SymbolOfRow = symbolOfRow,
        };

        void AddPins(Proto proto, RecognisedSymbol s, IReadOnlyList<RecognisedPin>? pins = null)
        {
            foreach (var p in pins ?? s.Pins)
            {
                var (dx, dy) = DirAt(s.Region, p.Node);
                proto.PicturePins.Add((p.X, p.Y, dx, dy));
                proto.PinNodes.Add(p.Node);
            }
        }
    }

    // ── block-diagram symbols (R-imf8-7) ───────────────────────────────────────────────────────────

    /// <summary>A word that marks a box an attenuator: <c>dB</c> with or without a number (an 8 is the B the matcher
    /// confuses with it), <c>ATT</c>, <c>PAD</c>.</summary>
    private static readonly System.Text.RegularExpressions.Regex AttenWord =
        new(@"^(?:[-−]?\d+(?:\.\d+)?\s*)?d[B8]$|^(?:ATT|ATTN|ATTEN|PAD)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsBalun(ImageWord t) => t.Text.Contains("BALUN", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the report says of a block symbol: which System component it is, what its drawing settled, and that
    /// its parameters are the defaults.</summary>
    private static string BlockNote(string name, string block, IReadOnlyDictionary<string, string> settings)
    {
        string what = block switch
        {
            DatasheetSymbols.FilterBlock => "a filter box" + (settings.TryGetValue("Form", out var f) ? f switch
            {
                "Lowpass" => " (low-pass)", "Highpass" => " (high-pass)", _ => " (band-pass)",
            } : settings.ContainsKey(DatasheetSymbols.BandStop) ? " drawn as a band-stop, which the Filter has no form for: left band-pass" : ""),
            DatasheetSymbols.AttenBlock => "an attenuator",
            DatasheetSymbols.CirculatorBlock => "a circulator, " + (settings.ContainsKey(DatasheetSymbols.ArrowUnread)
                ? "its arrow's direction not read: ports numbered clockwise — check them"
                : "its ports numbered the way its arrow turns"),
            DatasheetSymbols.SwitchBlock => settings.GetValueOrDefault("Throws") == "1"
                ? $"a switch, {(settings.GetValueOrDefault("State") == "0" ? "open" : "closed")}"
                : $"a switch with {settings.GetValueOrDefault("Throws")} throws, set to throw {settings.GetValueOrDefault("State")}",
            DatasheetSymbols.CouplerBlock => "a coupler, its coupled port the arm's lead nearer the input" +
                (settings.ContainsKey(DatasheetSymbols.IsolatedInside) ? "; its isolated port is not drawn and is left open — terminate it" : ""),
            _ => "a balun",
        };
        return $"{name} is {what}: the System {block} with default parameters — set them.";
    }

    // ── words beside a part (IMF-5) ─────────────────────────────────────────────────────────────────

    /// <summary>The words within a symbol scale of a part's region, nearest first.</summary>
    private static IEnumerable<ImageWord> NearWords(RecognisedSymbol s, IReadOnlyList<ImageWord> words, double sScale) =>
        words.Select(t => (Word: t, Gap: s.Region.Box.Distance(t.Box))).Where(t => t.Gap <= sScale).OrderBy(t => t.Gap).Select(t => t.Word);

    /// <summary>A device's part number: the word its designator's block or run gives it (a diode's <c>1N4002</c>,
    /// R-imf5-2; <c>Q₁2N2222</c>, R-imf7-11), whatever it reads as — 1N4002 is also a value's spelling.</summary>
    private static string? PartNumberOf(SymbolLabels? l) => l?.ValueWord is { } v && v.Reading is not null ? v.Text : null;

    private static readonly System.Text.RegularExpressions.Regex Frequency =
        new(@"^(\d+(?:\.\d*)?)\s*([kMG]?)Hz$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex Power =
        new(@"^([+-−]?\d+(?:\.\d*)?)\s*dBm$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A tone's frequency, Hz: <c>105MHz</c>.</summary>
    internal static double? ReadFrequency(string text)
    {
        var m = Frequency.Match(text.Trim());
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return null;
        return n * m.Groups[2].Value switch { "k" => 1e3, "M" => 1e6, "G" => 1e9, _ => 1 };
    }

    /// <summary>An available power, dBm: <c>-10 dBm</c>.</summary>
    internal static double? ReadPower(string text)
    {
        var m = Power.Match(text.Trim());
        return m.Success && double.TryParse(m.Groups[1].Value.Replace('−', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : null;
    }

    /// <summary>A DC source's volts or amps from a word beside it — <c>6V</c>, or the last word of <c>Vs 6V</c>.</summary>
    internal static double? SourceValue(string text, PartKind kind)
    {
        string last = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (!last.EndsWith(kind == PartKind.I ? "A" : "V", StringComparison.Ordinal)) return null;
        return PartsTableCsv.TryReadValue(last, kind, out double si, out _) ? si : null;
    }

    /// <summary>A coupling coefficient beside a transformer: <c>k=0.95</c>, <c>K 0.9</c>.</summary>
    internal static double? ReadCoupling(string text)
    {
        string t = text.Trim();
        if (t.Length < 2 || char.ToUpperInvariant(t[0]) != 'K') return null;
        t = t[1..].TrimStart(' ', '=');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double k) && k > 0 && k <= 1 ? k : null;
    }

    /// <summary>The factor a unit's prefix scales its number by, for an expression written in base SI.</summary>
    private static string UnitScale(string unit) => (unit.Length > 1 ? unit[0] : ' ') switch
    {
        'f' => "1e-15", 'p' => "1e-12", 'n' => "1e-9", 'u' or 'µ' => "1e-6", 'm' => "1e-3", 'k' => "1e3", 'M' => "1e6", 'G' => "1e9", _ => "1",
    };

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
    private static bool SupplyName(string name) => PortWords.IsSupply(name);

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
        return new SchematicFrame(symbols ?? NetlistSchematic.Grid / Math.Max(1, 4 * read.StrokeWidth / read.Scale), 0, 0);
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
