// A pin-numbered .csch solved by the linear engine (brief-modelcomposer-4 R-mc4-1, R-mc4-2, R-mc4-3, R-mc4-4).
//
// Two users: the six-pin extrinsic network of overview D5 (pins 1-3 extrinsic G/D/S, 4-6 intrinsic G/D/S), and a
// pad network (pins 1 probe side, 2 device side, its return the schematic's ground). Both are read the way the
// application reads any schematic — SchematicCircuit's extraction and .cnl round trip — and solved by the engine
// as it is: SParameterEngine for the multiport admittance, LinearDcEngine for the DC operating point. Nothing here
// stamps a matrix.
//
// THE SOURCE PIN IS GROUND. Data is measured common-source, so pin 3 of a six-pin network becomes node 0 of every
// bench built here, exactly as it is node 0 of the measurement and of a simulation that generated the data. The
// intrinsic ports (4-6 and 5-6) are ordinary engine ports with a floating reference.

using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;
using NumFlat;
using RfCore;

namespace CircuitRF.Design.Modeling;

/// <summary>A de-embedding input the composer refuses, naming its source and why.</summary>
public sealed class ModelingException(string source, string sentence) : Exception($"{source}: {sentence}")
{
    public string Input { get; } = source;
}

/// <summary>Which element kind a network variable is the value of — what its default bounds follow (R-mc4-2).</summary>
public enum NetworkElementKind { Other, Resistance, Inductance, Capacitance, Mutual }

/// <summary>One variable of a network's VAR block: its expression as written, and its value in base SI.</summary>
public sealed record NetworkVariable(string Name, string Expression, string? Unit, double Value, NetworkElementKind Kind);

/// <summary>A schematic whose pins are numbered 1…N, solved by the linear engine.</summary>
public abstract class PinNetwork
{
    /// <summary>What reading a network produced: the netlist text a run consumes, and what it says about itself.</summary>
    protected sealed record Loaded(string Path, string Cnl, string? ReferenceBase, IReadOnlyList<string> PinNets,
                                   IReadOnlyList<NetworkVariable> Variables,
                                   IReadOnlyList<(string Inductor1, string Inductor2, string M)> Mutuals);

    private readonly string _cnl;
    private readonly string? _referenceBase;

    protected PinNetwork(Loaded l)
    {
        SchematicPath = l.Path;
        _cnl = l.Cnl;
        _referenceBase = l.ReferenceBase;
        PinNets = l.PinNets;
        Variables = l.Variables;
        Mutuals = l.Mutuals;
    }

    public string SchematicPath { get; }

    /// <summary>The net each pin is on, pin 1 first.</summary>
    public IReadOnlyList<string> PinNets { get; }

    /// <summary>The VAR block, in the order written.</summary>
    public IReadOnlyList<NetworkVariable> Variables { get; }

    /// <summary>Each mutual: its two inductors' value expressions and its own (for the coupling constraint).</summary>
    public IReadOnlyList<(string Inductor1, string Inductor2, string M)> Mutuals { get; }

    /// <summary>
    /// Reads <paramref name="cschPath"/> and checks it has exactly <paramref name="pins"/> pins numbered 1…N, each
    /// once. A wrong count, or a duplicated or missing number, is a refusal that lists the pins found.
    /// </summary>
    protected static Loaded Read(string cschPath, int pins, string role)
    {
        string name = Path.GetFileName(cschPath);
        if (!File.Exists(cschPath)) throw new ModelingException(name, "the file does not exist.");
        SchematicEditModel model;
        try { model = SchematicPersistence.LoadFromFile(cschPath).model; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new ModelingException(name, $"it is not a readable schematic ({ex.Message})."); }

        var found = new List<string>();
        var nums = new List<int>();
        foreach (var c in model.Components)
        {
            if (c.Symbol != SymbolKind.Pin) continue;
            string text = c.Parameters.FirstOrDefault(p => p.Name == "Num")?.Expression?.Trim() ?? "";
            found.Add(text.Length == 0 ? "(unnumbered)" : text);
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) nums.Add(n);
        }
        bool exact = found.Count == pins && nums.Count == pins && nums.Order().SequenceEqual(Enumerable.Range(1, pins));
        if (!exact)
        {
            string list = found.Count == 0 ? "none" : string.Join(", ", found.OrderBy(Key));
            throw new ModelingException(name, $"{role} has exactly {pins} pins numbered 1 to {pins}; found {list}.");
        }

        string cell = Path.GetFileNameWithoutExtension(cschPath);
        string? refBase = SchematicCircuit.ReferenceBaseOf(cschPath);
        var extracted = SchematicCircuit.Extract(model, cell);
        string cnl = SchematicCircuit.CnlTextOf(extracted, cell);
        var (lib, tb) = SchematicCircuit.RoundTrip(cnl, cell, refBase);

        // Two pins on one net leave the second pin's name on no element: the extraction names a net once.
        var used = tb.Instances.SelectMany(i => i.NetBindings).ToHashSet(StringComparer.Ordinal);
        for (int k = 0; k < pins; k++)
            if (!used.Contains(extracted.CellPorts[k]))
                throw new ModelingException(name, $"pin {k + 1} shares its net with another pin, or reaches no element; " +
                                                  "join two pins through an element (a Short), never directly.");

        var (vars, mutuals) = Describe(cschPath, lib, tb);
        return new Loaded(cschPath, cnl, refBase, extracted.CellPorts, vars, mutuals);

        static (int, string) Key(string s) => (int.TryParse(s, out int n) ? n : int.MaxValue, s);
    }

    private static readonly Dictionary<string, (string Param, NetworkElementKind Kind)> ElementParams = new(StringComparer.OrdinalIgnoreCase)
    {
        ["R"] = ("R", NetworkElementKind.Resistance), ["L"] = ("L", NetworkElementKind.Inductance),
        ["C"] = ("C", NetworkElementKind.Capacitance), ["Mutual"] = ("M", NetworkElementKind.Mutual),
    };

    private static (IReadOnlyList<NetworkVariable>, IReadOnlyList<(string, string, string)>) Describe(string path, Library lib, TestBench tb)
    {
        // Which element each variable is the value of — by the identifiers in that element's own value.
        var kinds = new Dictionary<string, NetworkElementKind>(StringComparer.Ordinal);
        var lValue = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var inst in tb.Instances)
        {
            if (!ElementParams.TryGetValue(inst.Reference, out var ep)) continue;
            var a = inst.Overrides.FirstOrDefault(o => o.Name == ep.Param);
            if (a is null) continue;
            if (ep.Kind == NetworkElementKind.Inductance) lValue[inst.InstanceName] = a.Expression;
            foreach (Match m in Regex.Matches(a.Expression, @"[A-Za-z_][A-Za-z0-9_]*"))
                kinds.TryAdd(m.Value, ep.Kind);
        }
        var mutuals = new List<(string, string, string)>();
        foreach (var inst in tb.Instances.Where(i => i.Reference.Equals("Mutual", StringComparison.OrdinalIgnoreCase)))
        {
            string L(string p) => inst.Overrides.FirstOrDefault(o => o.Name == p)?.Expression.Trim('"') ?? "";
            string m = inst.Overrides.FirstOrDefault(o => o.Name == "M")?.Expression ?? "0";
            if (lValue.TryGetValue(L("Inductor1"), out var l1) && lValue.TryGetValue(L("Inductor2"), out var l2))
                mutuals.Add((l1, l2, m));
        }

        var values = ResolveGlobals(path, lib, tb);
        var vars = tb.GlobalVariables
            .Select(v => new NetworkVariable(v.Name, v.Expression, v.Unit,
                                             values.TryGetValue(v.Name, out var x) ? x : double.NaN,
                                             kinds.GetValueOrDefault(v.Name)))
            .ToList();
        return (vars, mutuals);
    }

    private static Dictionary<string, double> ResolveGlobals(string path, Library lib, TestBench tb)
    {
        try
        {
            using var nl = new Elaborator(lib).Elaborate(tb);
            return nl.ResolvedGlobals.Where(kv => kv.Value.Kind == ValueKind.Real)
                     .ToDictionary(kv => kv.Key, kv => kv.Value.AsReal(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new ModelingException(Path.GetFileName(path), $"it does not elaborate ({ex.Message})."); }
    }

    /// <summary>The variables' values, in base SI, with <paramref name="overrides"/> in place of the file's own.</summary>
    public IReadOnlyDictionary<string, double> Values(IReadOnlyDictionary<string, double>? overrides = null)
    {
        var d = Variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        if (overrides is not null) foreach (var (k, v) in overrides) d[k] = v;
        return d;
    }

    /// <summary>
    /// A fresh circuit of the network: every pin's net renamed through <paramref name="pinNet"/> (null keeps it), each
    /// overridden variable replaced by its number, and <paramref name="extra"/> instances added.
    /// </summary>
    protected ElaboratedNetlist Elaborate(IReadOnlyDictionary<string, double>? overrides, Func<int, string?> pinNet,
                                          IEnumerable<Instance> extra)
    {
        var (lib, tb) = SchematicCircuit.RoundTrip(_cnl, Path.GetFileNameWithoutExtension(SchematicPath), _referenceBase);
        var rename = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int k = 0; k < PinNets.Count; k++)
            if (pinNet(k + 1) is { } to) rename[PinNets[k]] = to;
        string R(string n) => rename.TryGetValue(n, out var to) ? to : n;

        var instances = tb.Instances
            .Select(i => new Instance(i.InstanceName, i.Reference, i.NetBindings.Select(R), i.Overrides)
                         { RefNetBinding = i.RefNetBinding is null ? null : R(i.RefNetBinding) })
            .ToList();
        tb.Instances.Clear();
        tb.Instances.AddRange(instances);
        tb.Instances.AddRange(extra);
        tb.Analyses.Clear();
        tb.Measurements.Clear();
        if (overrides is { Count: > 0 })
            for (int i = 0; i < tb.GlobalVariables.Count; i++)
                if (overrides.TryGetValue(tb.GlobalVariables[i].Name, out double v))
                    tb.GlobalVariables[i] = new Variable(tb.GlobalVariables[i].Name, v.ToString("R", CultureInfo.InvariantCulture));
        try { return new Elaborator(lib).Elaborate(tb); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new ModelingException(Path.GetFileName(SchematicPath), $"it does not elaborate ({ex.Message})."); }
    }

    protected const double PortZ0 = 50;

    protected static Instance Port(string name, int num, string plus, string minus) =>
        new(name, "Port", [plus, minus], [new ParameterAssignment("Num", num.ToString(CultureInfo.InvariantCulture)),
                                          new ParameterAssignment("Z", "50")]);

    protected static Instance VoltageSource(string name, string plus, string minus, double v) =>
        new(name, "Vdc", [plus, minus], [new ParameterAssignment("V", v.ToString("R", CultureInfo.InvariantCulture))]);

    /// <summary>A DC current source drawing <paramref name="amps"/> out of <paramref name="from"/> and into <paramref name="to"/>.</summary>
    protected static Instance CurrentSource(string name, string from, string to, double amps) =>
        new(name, "I_1Tone", [to, from], [new ParameterAssignment("Idc", amps.ToString("R", CultureInfo.InvariantCulture)),
                                          new ParameterAssignment("I", "0"), new ParameterAssignment("Freq", "1e9")]);

    /// <summary>
    /// The multiport admittance between the given ports at every frequency: one S-parameter run of the network with
    /// a 50 Ω port on each pair, converted to Y. <c>Y[f][i, j]</c>, ports in the order given.
    /// </summary>
    protected Complex[][,] Admittance(double[] freqsHz, IReadOnlyDictionary<string, double>? overrides,
                                      Func<int, string?> pinNet, IReadOnlyList<(string Plus, string Minus)> ports, RunControl? rc)
    {
        var portInstances = ports.Select((p, k) => Port($"__mc_port{k + 1}", k + 1, p.Plus, p.Minus)).ToList();
        using var nl = Elaborate(overrides, pinNet, portInstances);
        var s = SParameterEngine.Run(nl, freqsHz, control: rc?.Child())["S"];
        int n = ports.Count;
        var result = new Complex[freqsHz.Length][,];
        var m = new Mat<Complex>(n, n);
        for (int f = 0; f < freqsHz.Length; f++)
        {
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    m[i, j] = (Complex)s[f, i, j];
            var y = RFNetwork.SToY(m, new Complex(PortZ0, 0));
            var a = new Complex[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    a[i, j] = y[i, j];
            result[f] = a;
        }
        return result;
    }

    /// <summary>One DC solve with the sources given; the voltage of each net asked for.</summary>
    protected double[] DcVoltages(IReadOnlyDictionary<string, double>? overrides, Func<int, string?> pinNet,
                                  IEnumerable<Instance> sources, IReadOnlyList<string> nets)
    {
        using var nl = Elaborate(overrides, pinNet, sources);
        var r = LinearDcEngine.Run(nl, portsTerminate: false);
        if (r.Solution is not { } sol)
            throw new ModelingException(Path.GetFileName(SchematicPath), $"its DC solve failed ({r.Refusal}).");
        return [.. nets.Select(n => n == "0" ? 0.0 : sol.VoltageAt(nl.Nodes.IndexOf(n)))];
    }
}
