using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// A <c>.cmodel</c>'s extrinsic network, read and checked once, ready to be turned into the cell a
/// <c>ComposedFet</c> elaborates as (D3, R-mc1-5) at any finger count.
///
/// <para><b>The values the cell holds are NUMBERS, already scaled.</b> Every variable is resolved
/// once here, multiplied by its rule's factor in <see cref="Synthesize"/>, and every element
/// parameter that evaluates to a real number is written into the synthesised instance as that
/// number — so the netlist, <c>explain</c> and the Data Display show the values simulated, not an
/// expression over variables nobody can see (R-mc1-7).</para>
/// </summary>
public sealed class ComposedNetworkTemplate
{
    /// <summary>What pins 4, 5 and 6 — the intrinsic gate, drain and source — are called inside the
    /// synthesised cell, so <c>V(X1.gi)</c> probes an intrinsic voltage whatever the network named
    /// them.</summary>
    public static readonly IReadOnlyList<string> IntrinsicNets = ["gi", "di", "si"];

    /// <summary>The instance name of the nonlinear core inside the synthesised cell.</summary>
    public const string CoreInstanceName = "core";

    private readonly Cell _cell;
    private readonly IReadOnlyList<(string Name, double Value, string Rule)> _variables;

    /// <summary>The network's six pins, in order.</summary>
    public IReadOnlyList<string> Pins => _cell.Ports;

    /// <summary>Every network variable, its resolved value and its rule.</summary>
    public IReadOnlyList<(string Name, double Value, string Rule)> Variables => _variables;

    private ComposedNetworkTemplate(Cell cell, IReadOnlyList<(string, double, string)> variables)
    {
        _cell      = cell;
        _variables = variables;
    }

    /// <summary>Reads and checks a network, refusing with a <see cref="CmodelFormatException"/> that
    /// names the field.</summary>
    public static ComposedNetworkTemplate Build(ExtrinsicNetwork network)
    {
        const string field = "Network.Netlist";

        Library   lib;
        TestBench tb;
        try { (lib, tb) = new CnlReader().Read(network.Netlist, "network"); }
        catch (Exception ex) { throw new CmodelFormatException(field, $"the netlist does not read: {ex.Message}"); }

        if (tb.ReadWarnings.Count > 0)
            throw new CmodelFormatException(field, tb.ReadWarnings[0]);
        if (lib.Cells.Count != 1)
            throw new CmodelFormatException(field,
                $"it holds {lib.Cells.Count} define blocks; an extrinsic network is exactly one.");
        if (tb.Instances.Count > 0)
            throw new CmodelFormatException(field,
                $"'{tb.Instances[0].InstanceName}' is outside the define block.");

        var cell = lib.Cells[0];
        if (cell.Ports.Count != 6)
            throw new CmodelFormatException(field,
                $"the network has {cell.Ports.Count} pins; an extrinsic network has exactly six — " +
                "extrinsic gate, drain and source, then intrinsic gate, drain and source.");
        if (cell.Ports.Distinct(StringComparer.Ordinal).Count() != 6)
            throw new CmodelFormatException(field, "two of its six pins have the same name.");
        if (cell.Ports.Contains("0"))
            throw new CmodelFormatException(field, "a pin is named 0, which is ground.");

        // The names the synthesised cell gives pins 4-6, and the core's own instance name, must not
        // already mean something else inside the network.
        var nets = cell.Instances.SelectMany(i => i.NetBindings).ToHashSet(StringComparer.Ordinal);
        for (int k = 0; k < 6; k++)
        {
            string pin = cell.Ports[k];
            if (k < 3 && IntrinsicNets.Contains(pin))
                throw new CmodelFormatException(field,
                    $"extrinsic pin {k + 1} is named '{pin}', which names an intrinsic net.");
        }
        foreach (string reserved in IntrinsicNets)
            if (nets.Contains(reserved) && !cell.Ports.Skip(3).Contains(reserved))
                throw new CmodelFormatException(field,
                    $"an internal net is named '{reserved}', which names an intrinsic pin.");
        if (cell.Instances.Any(i => i.InstanceName == CoreInstanceName))
            throw new CmodelFormatException(field,
                $"an element is named '{CoreInstanceName}', which names the nonlinear core.");

        // Variables: globals, then the define block's own parameters and variables.
        var global = new Scope("network");
        foreach (var v in tb.GlobalVariables) global.Bind(v.Name, v.Expression, v.Unit);
        var local = new Scope("network-cell", global);
        foreach (var p in cell.Parameters) local.Bind(p.Name, p.DefaultExpression, p.Unit);
        foreach (var v in cell.Variables)  local.Bind(v.Name, v.Expression, v.Unit);

        var names = tb.GlobalVariables.Select(v => v.Name)
            .Concat(cell.Parameters.Select(p => p.Name))
            .Concat(cell.Variables.Select(v => v.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var rules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in network.Scaling)
        {
            if (!names.Contains(r.Variable, StringComparer.Ordinal))
                throw new CmodelFormatException($"Network.Scaling.{r.Variable}",
                    $"the network has no variable '{r.Variable}'.");
            rules[r.Variable] = r.Rule;
        }

        var evaluator = new Evaluator();
        var variables = new List<(string, double, string)>(names.Count);
        foreach (string name in names)
        {
            Value v;
            try { v = evaluator.Resolve(name, local); }
            catch (Exception ex)
            {
                throw new CmodelFormatException(field, $"variable '{name}' does not resolve: {ex.Message}");
            }
            if (v.Kind != ValueKind.Real || !double.IsFinite(v.AsReal()))
                throw new CmodelFormatException(field, $"variable '{name}' is not a finite real number.");
            variables.Add((name, v.AsReal(), rules.GetValueOrDefault(name, PeripheryScaling.Fixed)));
        }

        return new ComposedNetworkTemplate(cell, variables);
    }

    /// <summary>
    /// The cell one (<c>.cmodel</c>, <paramref name="n"/>, <paramref name="multiplicity"/>) elaborates
    /// as: the network's elements with their values scaled to <paramref name="n"/> fingers, pins 4-6
    /// renamed <c>gi</c>/<c>di</c>/<c>si</c>, and <paramref name="core"/> on them. Its three ports are
    /// the network's pins 1-3.
    ///
    /// <para><b>The multiplier.</b> <paramref name="multiplicity"/> identical devices in parallel is
    /// every admittance times it. An inductor and a mutual are branch elements, which the device
    /// multiplier refuses (two inductors in parallel cannot be stamped as one branch twice), so their
    /// values are divided instead; every other element carries it as its <c>m</c>, where the engine's
    /// own multiplier applies it — and refuses it, by name, on any other branch element.</para>
    /// </summary>
    public Cell Synthesize(string cellName, int n, int n0, double multiplicity, Instance core)
    {
        var scaled = new Scope("network-scaled");
        foreach (var (name, value, rule) in _variables)
            scaled.Bind(name, Literal(value * PeripheryScaling.Factor(rule, n, n0)));

        var evaluator = new Evaluator();
        var cell = new Cell(cellName);
        cell.Ports.AddRange(_cell.Ports.Take(3));

        foreach (var inst in _cell.Instances)
        {
            var nets = inst.NetBindings.Select(MapNet).ToArray();
            var overrides = new List<ParameterAssignment>(inst.Overrides.Count + 1);
            bool sawM = false;
            foreach (var ov in inst.Overrides)
            {
                Value? v = null;
                try { v = evaluator.Eval(ov.Expression, scaled, ov.Unit); }
                catch (Exception) { /* not a value this cell can fold — kept as written */ }

                if (v is { Kind: ValueKind.Real } real)
                {
                    double x = real.AsReal();
                    if (multiplicity != 1.0)
                    {
                        if (IsBranchValue(inst.Reference, ov.Name)) x /= multiplicity;
                        else if (ov.Name == Elaboration.Elaborator.MultiplierParamName) { x *= multiplicity; sawM = true; }
                    }
                    overrides.Add(new ParameterAssignment(ov.Name, Literal(x)));
                }
                else
                    overrides.Add(ov);
            }
            if (multiplicity != 1.0 && !sawM && !IsBranchElement(inst.Reference))
                overrides.Add(new ParameterAssignment(Elaboration.Elaborator.MultiplierParamName, Literal(multiplicity)));

            cell.Instances.Add(new Instance(inst.InstanceName, inst.Reference, nets, overrides)
                               { RefNetBinding = inst.RefNetBinding is { } r ? MapNet(r) : null });
        }

        cell.Instances.Add(core);
        return cell;
    }

    private string MapNet(string net)
    {
        for (int k = 3; k < 6; k++)
            if (string.Equals(net, _cell.Ports[k], StringComparison.Ordinal))
                return IntrinsicNets[k - 3];
        return net;
    }

    private static bool IsBranchElement(string reference)
        => reference.Equals("L", StringComparison.OrdinalIgnoreCase)
        || reference.Equals("Mutual", StringComparison.OrdinalIgnoreCase);

    private static bool IsBranchValue(string reference, string parameter)
        => (reference.Equals("L", StringComparison.OrdinalIgnoreCase) && parameter == "L")
        || (reference.Equals("Mutual", StringComparison.OrdinalIgnoreCase) && parameter == "M");

    /// <summary>A double as expression text that reads back as the same double.</summary>
    public static string Literal(double x) => x.ToString("R", CultureInfo.InvariantCulture);
}
