// The build — brief-schimp-1 R-si1-4/5/6, overview D3–D6.
//
//   ForeignSchematic ──► nets from the source's GEOMETRY (once, for every format) ──► TestBench + ExactPlacement
//                                                                                        │
//                                                                  NetlistSchematic.Build ──► SchematicEditModel
//
// ── THE NETS, IN TWO LAYERS ─────────────────────────────────────────────────────────────────────
//
// PHYSICAL: wires joined at their vertices; a wire end, a junction, a pin or a label lying anywhere on a wire joins it;
//   points that coincide join. Two wires merely crossing do not. These groups are what the source DRAWS as one piece.
// LOGICAL: physical groups joined by name — labels of equal text on one sheet (local) or anywhere (global, power,
//   off-page), a hidden power pin to its implicit net, ground names and ground symbols to ground. These are the nets.
//
// The readers supply geometry, never nets. A format that also DECLARES its nets has them compared against what is
// built (ImportConnectivity) — they are never used to build.
//
// ── WHAT IS DRAWN WHERE ─────────────────────────────────────────────────────────────────────────
//
// A symbol cell's symbol is translated so its first pin is the local origin, and the instance is placed with that pin
// at the source's point: its pins are NOT snapped to circuitRF's 100-mil pin grid (a source pitching pins at 50 mils would
// have two land on one point — brief-schimp-2), so every pin lands exactly where the source's did. A native part is fitted to its source pins (the rotation and on-grid origin nearest them); a pin it misses is
// re-joined by NetlistSchematic's one dog-leg, the source wire never moved (D6). A pin the source connects by touching a
// label or another pin, with no wire, is joined by a net label on it. Each physical ground group with wires keeps its
// wires and a ground symbol where the source grounded it; a ground pin with no wire gets a ground symbol on the pin.
//
// ── THE GRID (R-si1-6) ─────────────────────────────────────────────────────────────────────────
//
// circuitRF connects points by quantizing them to the schematic's GridSize. A source on a 50-mil grid puts pins at odd
// multiples of 50, which a 100 grid rounds onto each other (150 and 250 both round to 2 under banker's rounding). So the
// drawing is never snapped (ExactPlacement.SnapToGrid = false) and its GridSize is the largest divisor of 100 every
// source point sits on. See src/Design/RESOLVED.md.

using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Pdk;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Schematic.Import;

/// <summary>What the import is told beyond the source (R-si1-8, overview D9).</summary>
public sealed record SchematicImportOptions
{
    /// <summary>References or net names to make circuitRF Ports, in port order (overview D9). With one, the default S-parameter
    /// analysis is added.</summary>
    public IReadOnlyList<string> Ports { get; init; } = [];

    /// <summary>References the user pushed back from a native part to a symbol cell (R-si1-3).</summary>
    public IReadOnlyCollection<string> SymbolCells { get; init; } = [];

    /// <summary>The import's date, for the provenance block; null is now.</summary>
    public DateTime? Now { get; init; }
}

/// <summary>A cell holding one source symbol unit's drawing and no model (R-si1-4).</summary>
/// <param name="PinNumbers">The source's pin numbers, in the symbol's port order.</param>
/// <param name="Pins">The symbol's pins, local, in port order — first pin at the origin.</param>
public sealed record ImportSymbolCell(
    string Name, string SymbolKey, int Unit, string SourceName, CircuitRF.Design.Symbol.Symbol Symbol, string Csym,
    IReadOnlyList<string> PinNumbers, IReadOnlyList<(float X, float Y)> Pins);

/// <summary>A cell for one distinct child sheet of a hierarchical design (overview D5): a rectangle symbol whose pins are
/// the sheet's ports, and the sheet's own schematic.</summary>
public sealed record ImportSheetCell(
    string Name, string SheetName, CircuitRF.Design.Symbol.Symbol Symbol, string Csym,
    IReadOnlyList<string> Ports, IReadOnlyList<(float X, float Y)> Pins);

/// <summary>One schematic the build produced: the top one, or a child sheet's (<see cref="SheetCell"/> set).</summary>
public sealed class ImportBuiltSchematic
{
    public required string                 CellName   { get; init; }
    public          ImportSheetCell?        SheetCell  { get; init; }
    public required string                 Directory  { get; init; }
    public required SchematicEditModel      Model      { get; init; }
    public required TestBench               TestBench  { get; init; }
    public required ExactPlacement          Placement  { get; init; }

    /// <summary>Per instance, the source pin each of its ports is — null for a port the source has no pin for.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<ForeignPinRef?>> InstancePins { get; init; }

    /// <summary>Per <c>Pin</c> component's name, the hierarchical port it is (<see cref="ImportConnectivity.PortReference"/>).</summary>
    public required IReadOnlyList<string> PortNames { get; init; }

    /// <summary>The source's own nets in this schematic's scope, as sets of pins — single-pin nets and no-connect pins
    /// already left out.</summary>
    public required IReadOnlyList<IReadOnlySet<ForeignPinRef>> GeometricNets { get; init; }

    /// <summary>Pins in this scope the comparison leaves out on both sides (ground symbols, no-connects).</summary>
    public required IReadOnlySet<ForeignPinRef> Excluded { get; init; }
}

/// <summary>What <see cref="SchematicImportBuild.Build"/> made, in memory: nothing is written.</summary>
public sealed record ImportBuildResult(
    IReadOnlyList<ImportBuiltSchematic> Schematics,
    IReadOnlyList<ImportSymbolCell> SymbolCells,
    IReadOnlyList<ImportSheetCell> SheetCells,
    IReadOnlyList<ImportPartDecision> Mapping,
    IReadOnlyDictionary<string, string> Renamed,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Refusals)
{
    public bool Ok => Refusals.Count == 0;
    public ImportBuiltSchematic? Top => Schematics.Count > 0 ? Schematics[0] : null;
}

public static class SchematicImportBuild
{
    private const double P = NetlistSchematic.Grid;

    /// <summary>Gap between tiled pages, in grid squares (overview D5).</summary>
    private const int PageGap = 20;

    /// <summary>
    /// Builds <paramref name="source"/> into its schematic(s) and cells, in memory (R-si1-5). <paramref name="schematicDirectory"/>
    /// is where the top schematic will be written and <paramref name="partsDirectory"/> the folder its symbol and sheet
    /// cells go in (overview D4) — both absolute, neither touched, both needed because a <c>CellRef</c> is relative.
    /// </summary>
    public static ImportBuildResult Build(ForeignSchematic source, string topCellName, string schematicDirectory,
                                          string partsDirectory, SchematicImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new SchematicImportOptions();
        var notes = new List<string>(source.Notes);
        var refusals = new List<string>();

        if (source.Sheet(source.RootSheet) is null)
            return Refuse($"The design names its top sheet '{source.RootSheet}', which it does not contain.");

        source = UniqueReferences(source, notes);
        var mapping = SchematicImportMap.Decide(source, options.SymbolCells);
        var byRef = mapping.ToDictionary(d => d.Reference, StringComparer.Ordinal);
        foreach (var d in mapping) if (d.Note is { } n) notes.Add(n);

        // ── the symbol cells and the sheet cells ─────────────────────────────────────────────────
        var cellNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var symbolCells = new Dictionary<(string Key, int Unit), ImportSymbolCell>();
        foreach (var sheet in source.Sheets)
            foreach (var inst in sheet.Instances)
            {
                if (byRef[inst.Reference].Becomes != ImportPartKind.SymbolCell || symbolCells.ContainsKey((inst.SymbolKey, inst.Unit))
                    || options.Ports.Contains(inst.Reference)) continue;
                if (source.Symbol(inst.SymbolKey) is not { } symbol || symbol.Unit(inst.Unit) is not { Pins.Count: > 0 } unit)
                {
                    refusals.Add($"{inst.Reference}: its symbol '{inst.SymbolKey}' (unit {inst.Unit}) has no pins in the design.");
                    continue;
                }
                string name = Unique(SchematicImportMap.CellNameFor(symbol.DisplayName) + (symbol.UnitCount > 1 ? "_" + UnitLetter(unit.Unit) : ""), cellNames);
                if (SymbolCellOf(symbol, unit, name, notes) is { } cell) symbolCells[(inst.SymbolKey, inst.Unit)] = cell;
                else refusals.Add($"{inst.Reference}: its symbol '{symbol.DisplayName}' could not be drawn.");
            }

        var sheetCells = new Dictionary<string, ImportSheetCell>(StringComparer.Ordinal);
        if (source.Hierarchical)
            foreach (var sheet in source.Sheets)
                foreach (var si in sheet.SheetInstances)
                {
                    if (sheetCells.ContainsKey(si.SheetName)) continue;
                    if (source.Sheet(si.SheetName) is null) { refusals.Add($"Sheet '{si.Name}' shows '{si.SheetName}', which the design does not contain."); continue; }
                    if (SheetCellOf(si, Unique(SchematicImportMap.CellNameFor(si.SheetName), cellNames), notes) is { } cell) sheetCells[si.SheetName] = cell;
                    else refusals.Add($"Sheet '{si.Name}' has no ports, so it has nothing to connect to its parent.");
                }
        if (refusals.Count > 0) return Refuse();

        int grid = GridOf(source);
        if (grid < (int)P)
            notes.Add($"The source is drawn on a {grid}-mil grid, finer than circuitRF's {P:0}; the schematic keeps its points "
                    + $"exactly, on a {grid}-unit connection grid.");

        // ── the schematics ───────────────────────────────────────────────────────────────────────
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var built = new List<ImportBuiltSchematic>();
        var scopes = new List<(IReadOnlyList<ForeignSheet> Sheets, ImportSheetCell? Cell)>();
        if (!source.Hierarchical) scopes.Add(([.. source.Pages], null));
        else
            foreach (var s in source.Pages)
                if (s.Name == source.RootSheet) scopes.Add(([s], null));
                else if (sheetCells.TryGetValue(s.Name, out var c)) scopes.Add(([s], c));
        if (source.Hierarchical)
            foreach (var unused in source.Sheets.Where(s => s.Name != source.RootSheet && !sheetCells.ContainsKey(s.Name)))
                notes.Add($"Sheet '{unused.Name}' is placed by no other sheet and was not imported.");

        foreach (var (sheets, sheetCell) in scopes)
        {
            string cellName = sheetCell?.Name ?? topCellName;
            string dir = sheetCell is null ? schematicDirectory : Path.Combine(partsDirectory, sheetCell.Name, CellFolder.SubFolderName(ViewType.Schematic));
            var scope = new Scope(source, sheets, sheetCell, byRef, symbolCells, sheetCells, grid, dir, partsDirectory, options, notes, renamed);
            if (scope.Build() is { } schematic) built.Add(schematic);
            else { refusals.AddRange(scope.Refusals); return Refuse(); }
        }

        if (source.Hierarchical)
            foreach (var name in GlobalNamesAcrossSheets(source))
                notes.Add($"The global net '{name}' is used on more than one sheet; circuitRF keeps a cell's nets to that cell, so "
                        + "the sheets' copies of it are not joined.");

        return new ImportBuildResult(built, [.. symbolCells.Values], [.. sheetCells.Values], mapping, renamed, notes, refusals);

        ImportBuildResult Refuse(string? why = null)
        {
            if (why is not null) refusals.Add(why);
            return new ImportBuildResult([], [], [], [], new Dictionary<string, string>(), notes, refusals);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  One schematic's worth: a flat design's pages together, or one sheet of a hierarchical one
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Scope(
        ForeignSchematic source, IReadOnlyList<ForeignSheet> sheets, ImportSheetCell? sheetCell,
        IReadOnlyDictionary<string, ImportPartDecision> byRef,
        IReadOnlyDictionary<(string Key, int Unit), ImportSymbolCell> symbolCells,
        IReadOnlyDictionary<string, ImportSheetCell> sheetCells, int grid, string directory, string partsDirectory,
        SchematicImportOptions options, List<string> notes, Dictionary<string, string> renamed)
    {
        public List<string> Refusals { get; } = [];

        private readonly UnionFind _physical = new();
        private readonly UnionFind _logical = new();
        private readonly List<Node> _nodes = [];
        private readonly HashSet<int> _wiredGroups = [];
        private readonly HashSet<int> _noConnectGroups = [];
        private readonly HashSet<int> _groundRoots = [];

        private enum NodeKind { Wire, Pin, Label, Junction, SheetPin, NoConnect }

        private sealed record Node(NodeKind Kind, int Sheet, (double X, double Y) At)
        {
            public ForeignWire?          Wire  { get; init; }
            public ForeignLabel?         Label { get; init; }
            public PlacedPin?            Pin   { get; init; }
            public (int Inst, string Port)? SheetPin { get; init; }
        }

        /// <summary>One pin of one placed unit, where it lands (page offset applied).</summary>
        private sealed record PlacedPin(int Sheet, ForeignInstance Inst, ForeignPin Pin, string Number, (double X, double Y) At);

        public ImportBuiltSchematic? Build()
        {
            var offsets = PageOffsets();

            // ── nodes ──────────────────────────────────────────────────────────────────────────
            var pinNodes = new List<int>();
            for (int s = 0; s < sheets.Count; s++)
            {
                var sheet = sheets[s];
                var off = offsets[s];
                foreach (var w in sheet.Wires.Where(w => w.Points.Count >= 1))
                    Add(new Node(NodeKind.Wire, s, Shift(w.Points[0], off)) { Wire = w with { Points = [.. w.Points.Select(p => Shift(p, off))] } });
                foreach (var j in sheet.Junctions) Add(new Node(NodeKind.Junction, s, Shift((j.X, j.Y), off)));
                foreach (var l in sheet.Labels) Add(new Node(NodeKind.Label, s, Shift((l.X, l.Y), off)) { Label = l });
                foreach (var nc in sheet.NoConnects) Add(new Node(NodeKind.NoConnect, s, Shift((nc.X, nc.Y), off)));
                foreach (var inst in sheet.Instances)
                    foreach (var pin in PinsOf(s, inst, off))
                        pinNodes.Add(Add(new Node(NodeKind.Pin, s, pin.At) { Pin = pin }));
                for (int i = 0; i < sheet.SheetInstances.Count; i++)
                    foreach (var sp in sheet.SheetInstances[i].Pins)
                        Add(new Node(NodeKind.SheetPin, s, Shift((sp.X, sp.Y), off)) { SheetPin = (i, sp.Name) });
            }

            JoinPhysically();
            JoinLogically();
            for (int n = 0; n < _nodes.Count; n++)
            {
                if (_nodes[n].Kind == NodeKind.Wire) _wiredGroups.Add(_physical.Find(n));
                if (_nodes[n].Kind == NodeKind.NoConnect) _noConnectGroups.Add(_physical.Find(n));
                if (IsGroundMark(_nodes[n])) _groundRoots.Add(_logical.Find(n));
            }

            // ── names ──────────────────────────────────────────────────────────────────────────
            var naming = NameNets(pinNodes);
            if (naming is null) return null;
            var (netOf, quiet, groundGroups) = naming.Value;

            // ── the netlist and its drawing ─────────────────────────────────────────────────────
            var tb = new TestBench(sheetCell?.Name ?? "import");
            var exactInstances = new Dictionary<string, ExactInstance>(StringComparer.Ordinal);
            var cells = new Dictionary<string, ExactCell>(StringComparer.Ordinal);
            var joinByLabel = new HashSet<(string, int)>();
            var instancePins = new Dictionary<string, IReadOnlyList<ForeignPinRef?>>(StringComparer.Ordinal);
            var excluded = new HashSet<ForeignPinRef>();
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            var portRefs = PortReferences();

            string NetOfPin(int node) => netOf[node];
            bool Wired(int node) => GroupHasWire(_physical.Find(node));

            // Pins by unit, in declaration order.
            var units = pinNodes.GroupBy(n => (_nodes[n].Pin!.Sheet, _nodes[n].Pin!.Inst)).ToList();
            foreach (var unit in units)
            {
                var inst = unit.Key.Inst;
                var decision = byRef[inst.Reference];
                var nodes = unit.ToList();
                if (decision.Becomes == ImportPartKind.Ground)
                {
                    foreach (int n in nodes) excluded.Add(Ref(n));
                    continue;
                }
                if (portRefs.Contains(inst.Reference)) continue;   // drawn as a Port below

                bool multi = source.Symbol(inst.SymbolKey) is { UnitCount: > 1 };
                string wanted = inst.Reference + (multi ? UnitLetter(inst.Unit) : "");
                string name = InstanceName(wanted, usedNames);
                if (name != wanted) renamed[wanted] = name;
                var nets = nodes.Select(NetOfPin).ToList();

                if (decision.Becomes == ImportPartKind.Native)
                {
                    var overrides = new List<ParameterAssignment>
                    {
                        new(decision.Parameter!, decision.Expression!, decision.Unit),
                    };
                    if (decision.Footprint is { } footprint) overrides.Add(new(ArtworkParameters.FootprintName, footprint, null));
                    var instance = new Instance(name, ComponentTypeRegistry.EngineReference(decision.Native!.Value), nets, overrides);
                    tb.Instances.Add(instance);
                    if (decision.Variable is { } v && !tb.GlobalVariables.Any(g => g.Name == v.Name))
                        tb.GlobalVariables.Add(new Variable(v.Name, v.Expression, v.Unit));
                    exactInstances[name] = FitNative(instance, nodes.Select(n => _nodes[n].At).ToList());
                }
                else
                {
                    var cell = symbolCells[(inst.SymbolKey, inst.Unit)];
                    // Port order is the symbol's: one node per pin number, in that order.
                    var ordered = cell.PinNumbers.Select(num => nodes.First(n => _nodes[n].Pin!.Number == num)).ToList();
                    nodes = ordered;
                    nets = ordered.Select(NetOfPin).ToList();
                    tb.Instances.Add(new Instance(name, cell.Name, nets));
                    cells[name] = new ExactCell(ExternalCellRef.MakeCellRef(directory, Path.Combine(partsDirectory, cell.Name)), cell.Pins);
                    var first = _nodes[ordered[0]];
                    exactInstances[name] = new ExactInstance(first.At.X, first.At.Y, Rotation(inst.Rotation), inst.Mirror);
                }

                instancePins[name] = [.. nodes.Select(n => (ForeignPinRef?)Ref(n))];
                for (int k = 0; k < nodes.Count; k++)
                    if (nets[k] != "0" && (_nodes[nodes[k]].Pin!.Pin.Hidden || !Wired(nodes[k]))) joinByLabel.Add((name, k));
            }

            // ── child sheets ─────────────────────────────────────────────────────────────────────
            for (int s = 0; s < sheets.Count; s++)
                for (int i = 0; i < sheets[s].SheetInstances.Count; i++)
                {
                    var si = sheets[s].SheetInstances[i];
                    if (!sheetCells.TryGetValue(si.SheetName, out var cell)) continue;
                    string name = InstanceName(si.Name, usedNames);
                    var nodes = cell.Ports.Select(port => _nodes.FindIndex(n => n.Kind == NodeKind.SheetPin && n.Sheet == s
                                                                               && n.SheetPin == (i, port))).ToList();
                    var nets = nodes.Select((n, k) => n >= 0 ? netOf[n] : $"{name}_{NetName(cell.Ports[k])}").ToList();
                    tb.Instances.Add(new Instance(name, cell.Name, nets));
                    cells[name] = new ExactCell(ExternalCellRef.MakeCellRef(directory, Path.Combine(partsDirectory, cell.Name)), cell.Pins);
                    var first = nodes[0] >= 0 ? _nodes[nodes[0]].At : Shift((si.X, si.Y), offsets[s]);
                    exactInstances[name] = new ExactInstance(first.X, first.Y, SymbolRotation.R0, false);
                    instancePins[name] = [.. cell.Ports.Select(p => (ForeignPinRef?)new ForeignPinRef(si.Name, p))];
                    for (int k = 0; k < nodes.Count; k++)
                        if (nodes[k] >= 0 && nets[k] != "0" && !Wired(nodes[k])) joinByLabel.Add((name, k));
                }

            // ── ports (overview D9) ───────────────────────────────────────────────────────────────
            AddPorts(tb, units.Select(u => (u.Key.Inst, u.ToList())).ToList(), netOf, exactInstances, joinByLabel, instancePins,
                     usedNames, excluded);

            // ── routes ──────────────────────────────────────────────────────────────────────────
            var routes = new Dictionary<string, List<IReadOnlyList<(double X, double Y)>>>(StringComparer.Ordinal);
            for (int n = 0; n < _nodes.Count; n++)
            {
                if (_nodes[n].Kind != NodeKind.Wire || !netOf.TryGetValue(n, out var net) || net == "0") continue;
                if (!routes.TryGetValue(net, out var list)) routes[net] = list = [];
                list.Add(_nodes[n].Wire!.Points);
            }

            var placement = new ExactPlacement
            {
                Instances = exactInstances,
                Routes = routes.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<IReadOnlyList<(double X, double Y)>>)kv.Value),
                SnapToGrid = false,
                KeepOverlappingGlyphs = true,
                Cells = cells,
                QuietNets = quiet,
                JoinByLabel = joinByLabel,
            };

            var result = NetlistSchematic.Build(new Library("import"), tb, directory, exact: placement);
            if (result.Schematic is not { } model)
            {
                Refusals.AddRange(result.Refusals);
                return null;
            }
            notes.AddRange(result.Notes);
            if (result.PlacementFallbacks.Count > 0)
                notes.Add($"{string.Join(", ", result.PlacementFallbacks)} could not be drawn where the source draws them "
                        + "(a pin would land on another net's pin) and were placed beside the drawing.");
            model.GridSize = grid;

            // ── what NetlistSchematic does not draw ────────────────────────────────────────────────
            AddGroundSymbols(model, groundGroups);
            var portNames = sheetCell is null ? [] : AddSheetPorts(model, netOf);
            AddFreeText(model, offsets);
            foreach (var n in _nodes.Where(n => n.Kind == NodeKind.Junction)) model.Dots.Add(new EditableDot { X = n.At.X, Y = n.At.Y });

            // ── the source's own nets, for the check ─────────────────────────────────────────────
            foreach (int n in pinNodes) if (IsNoConnected(n)) excluded.Add(Ref(n));
            var geometric = GeometricNets(pinNodes, excluded);

            return new ImportBuiltSchematic
            {
                CellName = sheetCell?.Name ?? "", SheetCell = sheetCell, Directory = directory, Model = model, TestBench = tb,
                Placement = placement, InstancePins = instancePins, PortNames = portNames, GeometricNets = geometric, Excluded = excluded,
            };
        }

        // ── nodes and their joins ───────────────────────────────────────────────────────────────

        private int Add(Node node)
        {
            _nodes.Add(node);
            _physical.Add(_nodes.Count - 1);
            _logical.Add(_nodes.Count - 1);
            return _nodes.Count - 1;
        }

        private IEnumerable<PlacedPin> PinsOf(int sheet, ForeignInstance inst, (double X, double Y) off)
        {
            if (source.Symbol(inst.SymbolKey)?.Unit(inst.Unit) is not { } unit) yield break;
            for (int i = 0; i < unit.Pins.Count; i++)
            {
                var p = unit.Pins[i];
                var (x, y) = SchematicGeometry.LocalToWorld((float)p.X, (float)-p.Y, inst.X, inst.Y, Rotation(inst.Rotation), inst.Mirror);
                yield return new PlacedPin(sheet, inst, p, PinIdentity(unit, i), Shift((x, y), off));
            }
        }

        /// <summary>The physical joins: a wire's vertices; anything lying on a wire; anything at the same point.</summary>
        private void JoinPhysically()
        {
            // Segments hashed by cell, so a point is tested against the few wires near it.
            const double Cell = 500;
            var segs = new Dictionary<(int, long, long), List<(int Node, (double X, double Y) A, (double X, double Y) B)>>();
            for (int n = 0; n < _nodes.Count; n++)
            {
                if (_nodes[n].Wire is not { } w) continue;
                for (int i = 0; i + 1 < w.Points.Count; i++)
                {
                    var (a, b) = (w.Points[i], w.Points[i + 1]);
                    for (long cx = (long)Math.Floor((Math.Min(a.X, b.X) - 1) / Cell); cx <= (long)Math.Floor((Math.Max(a.X, b.X) + 1) / Cell); cx++)
                        for (long cy = (long)Math.Floor((Math.Min(a.Y, b.Y) - 1) / Cell); cy <= (long)Math.Floor((Math.Max(a.Y, b.Y) + 1) / Cell); cy++)
                        {
                            var key = (_nodes[n].Sheet, cx, cy);
                            if (!segs.TryGetValue(key, out var l)) segs[key] = l = [];
                            l.Add((n, a, b));
                        }
                }
            }

            var at = new Dictionary<(int, long, long), int>();
            void Touch(int node, (double X, double Y) p, bool onWires)
            {
                var key = (_nodes[node].Sheet, (long)Math.Round(p.X * 100), (long)Math.Round(p.Y * 100));
                if (at.TryGetValue(key, out int other)) _physical.Union(node, other);
                else at[key] = node;
                if (!onWires) return;
                if (segs.TryGetValue((_nodes[node].Sheet, (long)Math.Floor(p.X / Cell), (long)Math.Floor(p.Y / Cell)), out var near))
                    foreach (var (wn, a, b) in near)
                        if (wn != node && SchematicGeometry.PointOnSegment(p.X, p.Y, a.X, a.Y, b.X, b.Y, 0.01)) _physical.Union(node, wn);
            }

            for (int n = 0; n < _nodes.Count; n++)
            {
                var node = _nodes[n];
                switch (node.Kind)
                {
                    case NodeKind.Wire:
                        // Every vertex is a point other things meet; only the two ENDS join another wire's interior.
                        var pts = node.Wire!.Points;
                        for (int i = 0; i < pts.Count; i++) Touch(n, pts[i], onWires: i == 0 || i == pts.Count - 1);
                        break;
                    case NodeKind.Pin when node.Pin!.Pin.Hidden && node.Pin.Pin.ImplicitNet is not null:
                        break;   // a hidden power pin joins its net by name, never by where it is drawn
                    default:
                        Touch(n, node.At, onWires: true);
                        break;
                }
            }
            for (int n = 0; n < _nodes.Count; n++) _logical.Union(n, _physical.Find(n));
        }

        /// <summary>The logical joins: names (labels, implicit nets) and ground.</summary>
        private void JoinLogically()
        {
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            void Join(int node, string key)
            {
                if (byName.TryGetValue(key, out int other)) _logical.Union(node, other);
                else byName[key] = node;
            }
            for (int n = 0; n < _nodes.Count; n++)
            {
                var node = _nodes[n];
                if (node.Label is { } l)
                {
                    if (IsGroundLabel(l)) { Join(n, GroundKey); continue; }
                    Join(n, l.Kind switch
                    {
                        ForeignLabelKind.Local or ForeignLabelKind.Hierarchical => $"L:{node.Sheet}:{l.Text}",
                        _ => $"G:{l.Text}",
                    });
                    // A hierarchical label and a local one of the same text on one sheet are one net.
                }
                else if (node.Pin is { } p)
                {
                    if (byRef[p.Inst.Reference].Becomes == ImportPartKind.Ground) Join(n, GroundKey);
                    else if (p.Pin.ImplicitNet is { Length: > 0 } implicitNet)
                        Join(n, SchematicImportMap.IsGroundName(implicitNet) ? GroundKey : $"G:{implicitNet}");
                }
            }
        }

        private const string GroundKey = "\u0000ground";

        private static bool IsGroundLabel(ForeignLabel l) =>
            l.Kind is ForeignLabelKind.Power or ForeignLabelKind.Global && SchematicImportMap.IsGroundName(l.Text);

        /// <summary>A node that grounds its net: a ground label, a ground symbol's pin, a hidden pin whose net is ground.</summary>
        private bool IsGroundMark(Node node) =>
            node.Label is { } l && IsGroundLabel(l)
            || node.Pin is { } p && (byRef[p.Inst.Reference].Becomes == ImportPartKind.Ground
                                     || p.Pin.ImplicitNet is { } i && SchematicImportMap.IsGroundName(i));

        private bool IsGroundRoot(int root) => _groundRoots.Contains(root);

        private bool GroupHasWire(int physicalRoot) => _wiredGroups.Contains(physicalRoot);

        private bool IsNoConnected(int pinNode) => _noConnectGroups.Contains(_physical.Find(pinNode));

        // ── names ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every node's netlist net: ground's wired physical groups each their own quiet net (grounded by a symbol drawn
        /// where the source grounds it), an unwired ground pin <c>0</c>, any other net its best label's text — power and
        /// global first, then hierarchical, off-page, local — made unique, and an unnamed one a quiet <c>N</c>k.
        /// </summary>
        private (Dictionary<int, string> NetOf, HashSet<string> Quiet, List<(string Net, List<(double X, double Y)> At)> GroundGroups)? NameNets(List<int> pinNodes)
        {
            var netOf = new Dictionary<int, string>();
            var quiet = new HashSet<string>(StringComparer.Ordinal);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0" };
            var groundGroups = new List<(string Net, List<(double X, double Y)> At)>();
            var rootName = new Dictionary<int, string>();
            var groundRoots = new Dictionary<int, bool>();

            var rootsInOrder = Enumerable.Range(0, _nodes.Count).Select(_logical.Find).Distinct().ToList();
            var members = Enumerable.Range(0, _nodes.Count).ToLookup(_logical.Find);
            // Named nets first, so a source name is never displaced by an invented one.
            foreach (int root in rootsInOrder)
            {
                if (groundRoots[root] = IsGroundRoot(root)) continue;
                var labels = members[root]
                                       .Select(n => (Text: _nodes[n].Label?.Text ?? _nodes[n].Pin?.Pin.ImplicitNet, Rank: Rank(_nodes[n])))
                                       .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                                       .OrderBy(x => x.Rank).ThenBy(x => x.Text, StringComparer.Ordinal).ToList();
                if (labels.Count == 0) continue;
                rootName[root] = Unique(NetName(labels[0].Text!), taken);
            }
            int k = 0;
            foreach (int root in rootsInOrder)
            {
                if (groundRoots[root] || rootName.ContainsKey(root)) continue;
                string name;
                do name = $"N{++k}"; while (taken.Contains(name));
                taken.Add(name);
                rootName[root] = name;
                quiet.Add(name);
            }

            // Ground: one quiet net per wired physical group, grounded where the source grounds it.
            var groundNetOfGroup = new Dictionary<int, string>();
            int g = 0;
            for (int n = 0; n < _nodes.Count; n++)
            {
                int root = _logical.Find(n);
                if (!groundRoots[root]) { netOf[n] = rootName[root]; continue; }
                int group = _physical.Find(n);
                if (!GroupHasWire(group)) { netOf[n] = "0"; continue; }
                if (!groundNetOfGroup.TryGetValue(group, out var net))
                {
                    do net = $"GND_{++g}"; while (taken.Contains(net));
                    taken.Add(net);
                    quiet.Add(net);
                    groundNetOfGroup[group] = net;
                    groundGroups.Add((net, GroundPoints(group)));
                }
                netOf[n] = net;
            }
            return (netOf, quiet, groundGroups);
        }

        /// <summary>Where the source grounds a physical group: its ground labels and ground symbols' pins — else, a group
        /// grounded only by a name it shares, its first label or wire vertex.</summary>
        private List<(double X, double Y)> GroundPoints(int group)
        {
            var members = Enumerable.Range(0, _nodes.Count).Where(n => _physical.Find(n) == group).ToList();
            var points = members.Where(n => IsGroundMark(_nodes[n]) && _nodes[n].Kind != NodeKind.Pin
                                             || _nodes[n].Pin is { } p && byRef[p.Inst.Reference].Becomes == ImportPartKind.Ground)
                                .Select(n => _nodes[n].At).Distinct().ToList();
            if (points.Count == 0)
                points = [members.Where(n => _nodes[n].Kind == NodeKind.Label).Select(n => _nodes[n].At)
                                 .DefaultIfEmpty(_nodes[members.First(n => _nodes[n].Kind == NodeKind.Wire)].Wire!.Points[0]).First()];
            return points;
        }

        private static int Rank(Node n) => n.Label?.Kind switch
        {
            ForeignLabelKind.Power or ForeignLabelKind.Global => 0,
            ForeignLabelKind.Hierarchical => 1,
            ForeignLabelKind.OffPage => 2,
            ForeignLabelKind.Local => 3,
            _ => n.Pin is not null ? 4 : 5,
        };

        // ── placement ───────────────────────────────────────────────────────────────────────────

        /// <summary>A native part at the rotation, and the origin on the source's grid, that put its pins nearest the
        /// source's (overview D6): every quarter turn tried, the origin the mean of what each pin asks for.</summary>
        private ExactInstance FitNative(Instance instance, IReadOnlyList<(double X, double Y)> sourcePins)
        {
            ExactInstance best = new(sourcePins[0].X, sourcePins[0].Y, SymbolRotation.R0, false);
            double bestCost = double.MaxValue;
            foreach (var rot in new[] { SymbolRotation.R0, SymbolRotation.R90, SymbolRotation.R180, SymbolRotation.R270 })
            {
                if (NetlistSchematic.PinOffsets(instance, rot, false) is not { } offsets || offsets.Count != sourcePins.Count) continue;
                double ox = Snap(sourcePins.Select((p, k) => p.X - offsets[k].X).Average());
                double oy = Snap(sourcePins.Select((p, k) => p.Y - offsets[k].Y).Average());
                double cost = sourcePins.Select((p, k) => Math.Abs(ox + offsets[k].X - p.X) + Math.Abs(oy + offsets[k].Y - p.Y)).Sum();
                if (cost < bestCost - 1e-9) { bestCost = cost; best = new ExactInstance(ox, oy, rot, false); }
            }
            return best;
        }

        private double Snap(double v) => Math.Round(v / grid) * grid;

        private (double X, double Y)[] PageOffsets()
        {
            var offsets = new (double X, double Y)[sheets.Count];
            double x = 0;
            for (int s = 0; s < sheets.Count; s++)
            {
                offsets[s] = (x, 0);
                double width = sheets[s].Width > 0 ? sheets[s].Width : ContentRight(sheets[s]);
                x += Math.Ceiling((width + PageGap * P) / P) * P;
            }
            return offsets;
        }

        private static double ContentRight(ForeignSheet sheet) =>
            sheet.Wires.SelectMany(w => w.Points.Select(p => p.X))
                 .Concat(sheet.Instances.Select(i => i.X + 1000))
                 .Concat(sheet.Labels.Select(l => l.X)).Concat(sheet.Texts.Select(t => t.X))
                 .DefaultIfEmpty(0).Max();

        // ── ports ───────────────────────────────────────────────────────────────────────────────

        private HashSet<string> PortReferences()
        {
            var refs = sheets.SelectMany(s => s.Instances).Select(i => i.Reference).ToHashSet(StringComparer.Ordinal);
            return options.Ports.Where(refs.Contains).ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>Overview D9: a connector the user marked becomes a Port where it was drawn — its first pin not on ground
        /// the signal, the next its reference; a net the user marked gets a Port to ground beside the drawing. With any,
        /// the default S-parameter analysis.</summary>
        private void AddPorts(TestBench tb, List<(ForeignInstance Inst, List<int> Nodes)> units, Dictionary<int, string> netOf,
                              Dictionary<string, ExactInstance> exact, HashSet<(string, int)> joinByLabel,
                              Dictionary<string, IReadOnlyList<ForeignPinRef?>> instancePins, HashSet<string> used,
                              HashSet<ForeignPinRef> excludedPorts)
        {
            int num = 0;
            double left = _nodes.Where(n => n.Kind is NodeKind.Wire or NodeKind.Pin).Select(n => n.At.X).DefaultIfEmpty(0).Min() - 800;
            double top = _nodes.Where(n => n.Kind is NodeKind.Wire or NodeKind.Pin).Select(n => n.At.Y).DefaultIfEmpty(0).Min();
            bool IsGround(string net) => net == "0" || net.StartsWith("GND_", StringComparison.Ordinal);
            foreach (var want in options.Ports)
            {
                var unit = units.FirstOrDefault(u => u.Inst.Reference == want);
                string name = InstanceName($"P{num + 1}", used);
                if (unit.Inst is not null)
                {
                    var nodes = unit.Nodes;
                    int sig = nodes.FindIndex(n => !IsGround(netOf[n]));
                    var others = nodes.Where((n, i) => i != sig).ToList();
                    var otherNets = others.Select(n => netOf[n]).Where(n => !IsGround(n)).Distinct().ToList();
                    if (sig < 0 || otherNets.Count > 1)
                    {
                        notes.Add($"{want} cannot be a Port: it has {(sig < 0 ? "no pin off ground" : "more than two signal nets")}.");
                        continue;
                    }
                    string minus = otherNets.Count == 1 ? otherNets[0] : "0";
                    tb.Instances.Add(PortInstance(name, ++num, netOf[nodes[sig]], minus));
                    var at = _nodes[nodes[sig]].At;
                    exact[name] = new ExactInstance(at.X, at.Y + 200, SymbolRotation.R0, false);
                    if (!GroupHasWire(_physical.Find(nodes[sig]))) joinByLabel.Add((name, 0));
                    if (minus != "0") joinByLabel.Add((name, 1));
                    // The reference pin is the first on the minus net; a connector's further ground pins have no
                    // terminal of their own, so the check leaves them out on both sides.
                    int minusPin = others.FirstOrDefault(n => minus == "0" ? IsGround(netOf[n]) : netOf[n] == minus, -1);
                    instancePins[name] = [Ref(nodes[sig]), minusPin >= 0 ? Ref(minusPin) : null];
                    foreach (int n in others.Where(n => n != minusPin)) excludedPorts.Add(Ref(n));
                    continue;
                }
                var target = netOf.Values.Distinct().FirstOrDefault(n => n.Equals(NetName(want), StringComparison.OrdinalIgnoreCase));
                if (target is null || IsGround(target))
                {
                    notes.Add($"No part or net named '{want}' to make a Port of.");
                    continue;
                }
                tb.Instances.Add(PortInstance(name, ++num, target, "0"));
                exact[name] = new ExactInstance(Snap(left), Snap(top + (num - 1) * 800 + 200), SymbolRotation.R0, false);
                joinByLabel.Add((name, 0));
                instancePins[name] = [null, null];
            }
            if (num > 0) tb.Analyses.Add(new SParameterAnalysis(RecognitionEmit.AnalysisName, RecognitionEmitOptions.DefaultSweep));
        }

        private static Instance PortInstance(string name, int num, string plus, string minus) =>
            new(name, ComponentTypeRegistry.EngineReference(SymbolKind.Term), [plus, minus],
                [new ParameterAssignment("Num", num.ToString(CultureInfo.InvariantCulture), null), new ParameterAssignment("Z", "50", "Ohm")]);

        // ── what NetlistSchematic does not draw ─────────────────────────────────────────────────

        private static void AddGroundSymbols(SchematicEditModel model, List<(string Net, List<(double X, double Y)> At)> groups)
        {
            string prefix = ComponentTypeRegistry.InstancePrefix(SymbolKind.Ground);
            int n = model.Components.Where(c => c.Symbol == SymbolKind.Ground && c.InstanceName.StartsWith(prefix, StringComparison.Ordinal))
                         .Select(c => int.TryParse(c.InstanceName.AsSpan(prefix.Length), out int i) ? i : 0).DefaultIfEmpty(0).Max();
            foreach (var (_, points) in groups)
                foreach (var (x, y) in points)
                    model.Components.Add(NetlistSchematic.NewGround(x, y, 0, 1, ++n));
        }

        /// <summary>A child sheet's hierarchical labels as <c>Pin</c>s, numbered in its symbol's port order, each with a
        /// net label of its net on the same point so it joins whatever the label touched.</summary>
        private List<string> AddSheetPorts(SchematicEditModel model, Dictionary<int, string> netOf)
        {
            var placed = new List<string>();
            for (int i = 0; i < sheetCell!.Ports.Count; i++)
            {
                string port = sheetCell.Ports[i];
                int n = _nodes.FindIndex(x => x.Label is { Kind: ForeignLabelKind.Hierarchical } l && l.Text == port);
                if (n < 0) { notes.Add($"Sheet '{sheetCell.SheetName}' has no hierarchical label for its port '{port}'."); continue; }
                var (x, y) = _nodes[n].At;
                var pin = new EditableComponent { Symbol = SymbolKind.Pin, X = x - 100, Y = y, Rotation = SymbolRotation.R0 };
                pin.Parameters.Add(new EditableParameter { Name = "Num", Expression = (i + 1).ToString(CultureInfo.InvariantCulture) });
                pin.Parameters.Add(new EditableParameter { Name = "Name", Expression = NetName(port), ShowOnSchematic = false });
                model.Components.Add(pin);
                if (netOf[n] != "0") model.NetLabels.Add(new EditableNetLabel { X = x, Y = y, Name = netOf[n] });
                placed.Add(NetName(port));
            }
            return placed;
        }

        private void AddFreeText(SchematicEditModel model, (double X, double Y)[] offsets)
        {
            for (int s = 0; s < sheets.Count; s++)
            {
                foreach (var t in sheets[s].Texts.Where(t => !string.IsNullOrWhiteSpace(t.Text)))
                {
                    var (x, y) = Shift((t.X, t.Y), offsets[s]);
                    model.CanvasObjects.Add(new EditableText
                    {
                        Text = t.Text, X = x, Y = y, Align = SymbolTextAlign.Left, VAlign = SymbolTextVAlign.Top,
                        RotationDeg = t.Rotation,
                    });
                }
                foreach (var gr in sheets[s].Graphics)
                {
                    var pts = gr.Points.Select(p => Shift(p, offsets[s])).ToList();
                    if (gr.Closed && pts.Count > 2) pts.Add(pts[0]);
                    for (int i = 0; i + 1 < pts.Count; i++)
                        model.CanvasObjects.Add(new EditablePrimitive
                        {
                            Shape = PrimitiveShape.Line, P1X = pts[i].X, P1Y = pts[i].Y, P2X = pts[i + 1].X, P2Y = pts[i + 1].Y,
                            X = (pts[i].X + pts[i + 1].X) / 2, Y = (pts[i].Y + pts[i + 1].Y) / 2,
                        });
                }
            }
        }

        // ── the check's half ────────────────────────────────────────────────────────────────────

        private ForeignPinRef Ref(int pinNode) => new(_nodes[pinNode].Pin!.Inst.Reference, _nodes[pinNode].Pin!.Number);

        /// <summary>The source's nets as sets of pins (overview D8): each logical net's pins, plus a child sheet's
        /// hierarchical labels and a parent's sheet pins as the ports they are.</summary>
        private List<IReadOnlySet<ForeignPinRef>> GeometricNets(List<int> pinNodes, HashSet<ForeignPinRef> excluded)
        {
            var sets = new Dictionary<int, HashSet<ForeignPinRef>>();
            void Put(int node, ForeignPinRef r)
            {
                int root = _logical.Find(node);
                if (!sets.TryGetValue(root, out var set)) sets[root] = set = [];
                set.Add(r);
            }
            foreach (int n in pinNodes) if (!excluded.Contains(Ref(n))) Put(n, Ref(n));
            for (int n = 0; n < _nodes.Count; n++)
            {
                if (_nodes[n].SheetPin is { } sp && sheets[_nodes[n].Sheet].SheetInstances[sp.Inst] is var si && sheetCells.ContainsKey(si.SheetName))
                    Put(n, new ForeignPinRef(si.Name, sp.Port));
                if (sheetCell is not null && _nodes[n].Label is { Kind: ForeignLabelKind.Hierarchical } l && sheetCell.Ports.Contains(l.Text))
                    Put(n, ImportConnectivity.PortReference(NetName(l.Text)));
            }
            return [.. sets.Values.Where(s => s.Count >= 2).Select(s => (IReadOnlySet<ForeignPinRef>)s)];
        }

        private static (double X, double Y) Shift((double X, double Y) p, (double X, double Y) off) => (p.X + off.X, p.Y + off.Y);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Cells
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One source symbol unit as a symbol cell (R-si1-4), through Import Component's own symbol path
    /// (<see cref="ComponentImport.BuildSymbol"/>). Translated so its first pin is the local origin — what lets an
    /// instance put every pin on the source's point. Ports in declaration order.
    /// </summary>
    private static ImportSymbolCell? SymbolCellOf(ForeignSymbol symbol, ForeignSymbolUnit unit, string name, List<string> notes)
    {
        double ox = unit.Pins[0].X, oy = unit.Pins[0].Y;
        var drawing = new ComponentSymbolDrawing { Name = name };
        var numbers = new List<string>();
        int fallbacks = 0;
        for (int i = 0; i < unit.Pins.Count; i++)
        {
            var p = unit.Pins[i];
            string number = PinIdentity(unit, i);
            if (number != p.Number.Trim()) fallbacks++;
            numbers.Add(number);
            drawing.Pins.Add(new ComponentSymbolPin(p.Name.Length > 0 ? p.Name : number, number,
                                                    (int)Math.Round(p.X - ox), (int)Math.Round(p.Y - oy), NameAlign: AlignOf(p.BodySide)));
        }
        foreach (var shape in unit.Graphics) drawing.Shapes.Add(Translate(shape, -ox, -oy));
        if (fallbacks > 0)
            notes.Add($"{fallbacks} pin(s) of '{symbol.DisplayName}' have no number; each is known by its name, else its position.");

        var messages = new List<string>();
        var ports = Enumerable.Range(0, unit.Pins.Count).ToDictionary(i => i, i => i + 1);
        if (ComponentImport.BuildSymbol(drawing, ports, unit.Pins.Count, messages, snapPins: false) is not { } built) return null;
        foreach (var m in messages) notes.Add($"{name}: {m}");
        var pins = built.Pins.OrderBy(p => p.PortIndex).Select(p => ((float)p.LocalX, (float)p.LocalY)).ToList();
        return new ImportSymbolCell(name, symbol.Key, unit.Unit, symbol.DisplayName, built, SymbolPersistence.Serialize(built), numbers, pins);
    }

    /// <summary>A child sheet's rectangle symbol (overview D5): the rectangle the parent draws, its ports where the parent
    /// draws them, the first at the local origin.</summary>
    private static ImportSheetCell? SheetCellOf(ForeignSheetInstance si, string name, List<string> notes)
    {
        if (si.Pins.Count == 0) return null;
        var ports = si.Pins.Select(p => p.Name).Distinct(StringComparer.Ordinal).ToList();
        var first = si.Pins[0];
        var drawing = new ComponentSymbolDrawing { Name = name };
        // Sheet coordinates are Y down and a drawing is Y up; the first pin is the origin.
        foreach (var port in ports)
        {
            var p = si.Pins.First(q => q.Name == port);
            drawing.Pins.Add(new ComponentSymbolPin(port, port, (int)Math.Round(p.X - first.X), (int)Math.Round(first.Y - p.Y)));
        }
        drawing.Shapes.Add(new KitSymbolRectangle(si.X - first.X, first.Y - si.Y, si.X + si.Width - first.X, first.Y - (si.Y + si.Height), false));
        var messages = new List<string>();
        if (ComponentImport.BuildSymbol(drawing, Enumerable.Range(0, ports.Count).ToDictionary(i => i, i => i + 1), ports.Count, messages, snapPins: false) is not { } built)
            return null;
        foreach (var m in messages) notes.Add($"{name}: {m}");
        var pins = built.Pins.OrderBy(p => p.PortIndex).Select(p => ((float)p.LocalX, (float)p.LocalY)).ToList();
        return new ImportSheetCell(name, si.SheetName, built, SymbolPersistence.Serialize(built), ports, pins);
    }

    private static KitTextAlign AlignOf(ForeignPinDirection body) => body switch
    {
        ForeignPinDirection.Left => KitTextAlign.Right,
        ForeignPinDirection.Up => KitTextAlign.Bottom,
        ForeignPinDirection.Down => KitTextAlign.Top,
        _ => KitTextAlign.Left,
    };

    private static KitSymbolShape Translate(KitSymbolShape shape, double dx, double dy) => shape switch
    {
        KitSymbolLine l => new KitSymbolLine(l.X1 + dx, l.Y1 + dy, l.X2 + dx, l.Y2 + dy) { Width = l.Width },
        KitSymbolRectangle r => new KitSymbolRectangle(r.X1 + dx, r.Y1 + dy, r.X2 + dx, r.Y2 + dy, r.Filled) { Width = r.Width },
        KitSymbolPath p => new KitSymbolPath([.. p.Xy.Select((v, i) => v + (i % 2 == 0 ? dx : dy))], p.Closed, p.Filled) { Width = p.Width },
        KitSymbolArc a => new KitSymbolArc(a.Cx + dx, a.Cy + dy, a.Radius, a.StartDeg, a.SweepDeg) { Width = a.Width },
        _ => shape,
    };

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Small rules
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A pin's identity (R-si1-2, R-si1-4): its number; with none, its name; with neither, its 1-based position.</summary>
    internal static string PinIdentity(ForeignSymbolUnit unit, int index)
    {
        var p = unit.Pins[index];
        if (p.Number.Trim() is { Length: > 0 } n) return n;
        if (p.Name.Trim() is { Length: > 0 } name) return name;
        return (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    internal static string UnitLetter(int unit) => unit is >= 1 and <= 26 ? ((char)('A' + unit - 1)).ToString() : $"_{unit}";

    private static SymbolRotation Rotation(int degrees) => (((degrees % 360) + 360) % 360) switch
    {
        90 => SymbolRotation.R90,
        180 => SymbolRotation.R180,
        270 => SymbolRotation.R270,
        _ => SymbolRotation.R0,
    };

    /// <summary>A net name as a schematic label carries it: no whitespace.</summary>
    internal static string NetName(string text) =>
        new([.. text.Trim().Select(c => char.IsWhiteSpace(c) || c is '"' or ';' or '=' ? '_' : c)]);

    /// <summary>An instance name from a reference: letters, digits and underscores, unique in the schematic.</summary>
    private static string InstanceName(string reference, HashSet<string> used)
    {
        string s = new([.. reference.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_')]);
        if (s.Length == 0 || char.IsAsciiDigit(s[0])) s = "X" + s;
        return Unique(s, used);
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        string n = name;
        for (int k = 2; !taken.Add(n); k++) n = $"{name}_{k}";
        return n;
    }

    /// <summary>
    /// The connection grid every source point sits on (R-si1-6): the largest divisor of 100 mils that divides every
    /// pin, wire vertex, junction, label and sheet pin. 100 for a source on circuitRF's own grid.
    /// </summary>
    private static int GridOf(ForeignSchematic source)
    {
        int g = (int)P;
        void On(double v)
        {
            long i = (long)Math.Round(v);
            if (Math.Abs(v - i) > 1e-3) i = (long)Math.Round(v * 1000) % 1000 == 0 ? i : 1;   // off the mil: as fine as it gets
            g = (int)Gcd(g, Math.Abs(i));
        }
        foreach (var sheet in source.Sheets)
        {
            foreach (var w in sheet.Wires) foreach (var (x, y) in w.Points) { On(x); On(y); }
            foreach (var j in sheet.Junctions) { On(j.X); On(j.Y); }
            foreach (var l in sheet.Labels) { On(l.X); On(l.Y); }
            foreach (var si in sheet.SheetInstances) foreach (var p in si.Pins) { On(p.X); On(p.Y); }
            foreach (var inst in sheet.Instances)
                if (source.Symbol(inst.SymbolKey)?.Unit(inst.Unit) is { } unit)
                    foreach (var p in unit.Pins.Where(p => !p.Hidden))
                    {
                        var (x, y) = SchematicGeometry.LocalToWorld((float)p.X, (float)-p.Y, inst.X, inst.Y, Rotation(inst.Rotation), inst.Mirror);
                        On(x); On(y);
                    }
        }
        return Math.Max(1, g);

        static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return a; }
    }

    /// <summary>Two placed units of one part both claiming the same reference AND unit are two parts the source left
    /// unannotated: the second and later get a suffix, reported.</summary>
    private static ForeignSchematic UniqueReferences(ForeignSchematic source, List<string> notes)
    {
        var seen = new HashSet<(string, int)>();
        var refs = source.Sheets.SelectMany(s => s.Instances).Select(i => i.Reference).ToHashSet(StringComparer.Ordinal);
        var changed = new List<string>();
        var sheets = source.Sheets.Select(sheet => sheet with
        {
            Instances = [.. sheet.Instances.Select(i =>
            {
                if (seen.Add((i.Reference, i.Unit))) return i;
                string n = i.Reference;
                for (int k = 2; refs.Contains(n); k++) n = $"{i.Reference}_{k}";
                refs.Add(n);
                seen.Add((n, i.Unit));
                changed.Add($"{i.Reference} → {n}");
                return i with { Reference = n };
            })],
        }).ToList();
        if (changed.Count == 0) return source;
        notes.Add($"{changed.Count} part(s) share a reference with another and were renamed: {string.Join(", ", changed)}.");
        return source with { Sheets = sheets };
    }

    private static IEnumerable<string> GlobalNamesAcrossSheets(ForeignSchematic source) =>
        source.Sheets.SelectMany(s => s.Labels.Where(l => l.Kind is ForeignLabelKind.Global or ForeignLabelKind.Power
                                                          && !SchematicImportMap.IsGroundName(l.Text))
                                              .Select(l => l.Text).Distinct().Select(t => (Sheet: s.Name, Text: t)))
              .GroupBy(x => x.Text).Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal);

    /// <summary>A plain union-find over node indices.</summary>
    private sealed class UnionFind
    {
        private readonly List<int> _parent = [];
        public void Add(int n) { while (_parent.Count <= n) _parent.Add(_parent.Count); }
        public int Find(int n) { while (_parent[n] != n) n = _parent[n] = _parent[_parent[n]]; return n; }
        public void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _parent[Math.Max(a, b)] = Math.Min(a, b); }
    }
}
