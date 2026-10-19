// The connectivity check, every import — brief-schimp-1 R-si1-7, overview D8.
//
// The produced schematic is extracted by NetExtractor — the extraction Simulate performs — and each instance's pins are
// translated back to the source's (reference, pin number). Then the two are compared as SETS OF PIN SETS: net names are
// never compared, because circuitRF names nets its own way. Against the source's geometry always; against the nets the
// format declares, when it declares any.
//
// A symbol cell has no schematic, and NetExtractor skips a cell instance it cannot descend into. So the check hands it a
// resolver that answers every cell with a black box — one unconnected Pin per symbol pin — which is enough for the
// extraction to bind the instance's nets and no more. Nothing is simulated.

using CircuitRF.Core.Design;

namespace CircuitRF.Design.Schematic.Import;

/// <summary>One comparison: matched, or each difference as a sentence naming the pins.</summary>
public sealed record ConnectivityComparison(string Against, bool Matched, IReadOnlyList<string> Differences);

/// <summary>The check's verdict (R-si1-7): against the source's geometry, and against its declared nets when it has any.
/// <paramref name="SourceAgreement"/> is the source against ITSELF — its declared nets beside its drawing (R-si3-3): when
/// they differ, the file's own record disagrees with its picture, and no import can match both.</summary>
public sealed record ImportConnectivityResult(ConnectivityComparison Geometric, ConnectivityComparison? Declared,
                                              ConnectivityComparison? SourceAgreement = null)
{
    public bool Matched => Geometric.Matched && Declared?.Matched != false;

    /// <summary>One line for Messages.</summary>
    public string Summary => Matched
        ? "Connectivity matched" + (Declared is null ? " the source's drawing." : " the source's drawing and its declared nets.")
        : $"Connectivity differs from the source: {Geometric.Differences.Concat(Declared?.Differences ?? []).Count()} difference(s).";
}

public static class ImportConnectivity
{
    /// <summary>The pin a child sheet's hierarchical port is, on the source side and the built one.</summary>
    public static ForeignPinRef PortReference(string portName) => new("<port>", portName);

    /// <summary>
    /// Compares every schematic of <paramref name="built"/> with <paramref name="source"/>. Each model's
    /// <see cref="SchematicEditModel.SchematicDirectory"/> must be where it is (or will be) written, with its symbol cells
    /// on disk — a <c>CellRef</c> is relative to it.
    /// </summary>
    public static ImportConnectivityResult Compare(ForeignSchematic source, ImportBuildResult built)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(built);

        var differences = new List<string>();
        var produced = new List<IReadOnlySet<ForeignPinRef>>();
        foreach (var schematic in built.Schematics)
        {
            var sets = Extract(schematic);
            string where = schematic.SheetCell is { } cell ? $" (sheet {cell.SheetName})" : "";
            differences.AddRange(Differences(schematic.GeometricNets, sets, schematic.Excluded).Select(d => d + where));
            if (schematic.SheetCell is null) produced = sets;
        }
        var geometric = new ConnectivityComparison("the source's drawing", differences.Count == 0, differences);

        ConnectivityComparison? declared = null, agreement = null;
        // A hierarchical design's declared nets span its sheets while its cells are compared one at a time, so they are
        // not compared here; each cell is still checked against its own sheet's drawing.
        if (source.DeclaredNets is { } nets && built.Top is { } top && !source.Hierarchical)
        {
            var d = Differences(nets, produced, top.Excluded);
            declared = new ConnectivityComparison("the source's declared nets", d.Count == 0, d);
            var self = Differences(nets, top.GeometricNets, top.Excluded)
                .Select(x => x.Replace("the source", "the declared nets", StringComparison.Ordinal)
                              .Replace("the import", "the drawing", StringComparison.Ordinal)).ToList();
            agreement = new ConnectivityComparison("the source's drawing, by its declared nets", self.Count == 0, self);
        }
        return new ImportConnectivityResult(geometric, declared, agreement);
    }

    /// <summary>The built schematic's nets, as source pins: NetExtractor's instances read back through the build's own
    /// table of which source pin each port is, and each <c>Pin</c> as the port it names.</summary>
    internal static List<IReadOnlySet<ForeignPinRef>> Extract(ImportBuiltSchematic schematic)
    {
        var model = schematic.Model;
        model.SchematicDirectory ??= schematic.Directory;
        var extracted = NetExtractor.Extract(model, "import", new BlackBoxCells(model));

        var byNet = new Dictionary<string, HashSet<ForeignPinRef>>(StringComparer.Ordinal);
        void Put(string net, ForeignPinRef r)
        {
            if (!byNet.TryGetValue(net, out var set)) byNet[net] = set = [];
            set.Add(r);
        }
        foreach (var inst in extracted.TestBench.Instances)
        {
            if (!schematic.InstancePins.TryGetValue(inst.InstanceName, out var pins)) continue;
            for (int k = 0; k < inst.NetBindings.Count && k < pins.Count; k++)
                if (pins[k] is { } r) Put(inst.NetBindings[k], r);
        }
        // A Pin OWNS its net's name in the extraction, so the net it is on is the net of that name.
        foreach (var port in schematic.PortNames)
            if (byNet.ContainsKey(port)) Put(port, PortReference(port));
        return [.. byNet.Values.Where(s => s.Count >= 2).Select(s => (IReadOnlySet<ForeignPinRef>)s)];
    }

    /// <summary>
    /// The two as partitions of the pins they share (single-pin nets and <paramref name="excluded"/> pins left out of both
    /// sides identically): each source net the import splits, and each import net joining pins the source keeps apart.
    /// </summary>
    internal static List<string> Differences(IReadOnlyList<IReadOnlySet<ForeignPinRef>> expected,
                                             IReadOnlyList<IReadOnlySet<ForeignPinRef>> actual,
                                             IReadOnlySet<ForeignPinRef> excluded)
    {
        static Dictionary<ForeignPinRef, int> Index(IEnumerable<IReadOnlySet<ForeignPinRef>> sets, IReadOnlySet<ForeignPinRef> excluded)
        {
            var index = new Dictionary<ForeignPinRef, int>();
            int id = 0;
            foreach (var set in sets)
            {
                var pins = set.Where(p => !excluded.Contains(p)).ToList();
                if (pins.Count < 2) continue;
                foreach (var p in pins) index.TryAdd(p, id);
                id++;
            }
            return index;
        }
        var want = Index(expected, excluded);
        var got = Index(actual, excluded);
        var differences = new List<string>();

        // A pin in a net on one side and in none on the other is alone there: its own net.
        foreach (var group in want.GroupBy(kv => kv.Value))
        {
            var pins = group.Select(kv => kv.Key).ToList();
            if (pins.Select(p => got.TryGetValue(p, out int g) ? (object)g : p).Distinct().Count() > 1)
                differences.Add($"pins {{{Join(pins)}}} joined in the source, split in the import");
        }
        foreach (var group in got.GroupBy(kv => kv.Value))
        {
            var pins = group.Select(kv => kv.Key).ToList();
            if (pins.Select(p => want.TryGetValue(p, out int w) ? (object)w : p).Distinct().Count() > 1)
                differences.Add($"pins {{{Join(pins)}}} joined in the import, split in the source");
        }
        return differences;

        static string Join(IEnumerable<ForeignPinRef> pins) =>
            string.Join(", ", pins.Select(p => p.ToString()).Order(StringComparer.Ordinal));
    }

    /// <summary>Every cell as a black box: one unconnected <c>Pin</c> per pin its symbol shows, numbered in port order —
    /// what the extraction needs to bind an instance's nets, and nothing it could descend into.</summary>
    private sealed class BlackBoxCells(SchematicEditModel top) : ICellResolver
    {
        public CellResolution? Resolve(EditableComponent cellInstance, SchematicEditModel containingModel)
        {
            if (cellInstance.CellRef is not { } cellRef) return null;
            var resolved = CellSymbolResolver.Resolve(cellRef, containingModel.SchematicDirectory ?? top.SchematicDirectory);
            int pins = resolved.Symbol?.Pins.Count ?? 0;
            if (pins == 0) return null;
            var box = new SchematicEditModel();
            for (int k = 1; k <= pins; k++)
            {
                var pin = new EditableComponent { Symbol = SymbolKind.Pin, X = k * 1000, Y = 0 };
                pin.Parameters.Add(new EditableParameter { Name = "Num", Expression = k.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                pin.Parameters.Add(new EditableParameter { Name = "Name", Expression = $"p{k}" });
                box.Components.Add(pin);
            }
            string key = Path.GetFullPath(Path.Combine(containingModel.SchematicDirectory ?? "", cellRef));
            return new CellResolution(Path.GetFileName(key.TrimEnd(Path.DirectorySeparatorChar, '/')), box, [], key);
        }
    }
}
