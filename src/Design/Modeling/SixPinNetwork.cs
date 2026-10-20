// The six-pin extrinsic network (brief-modelcomposer-4 R-mc4-2, R-mc4-3, R-mc4-4; overview D5) and the two-pin pad
// network (R-mc4-1), each solved by the linear engine through PinNetwork.

using System.Globalization;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;

namespace CircuitRF.Design.Modeling;

/// <summary>A six-pin network's 4-port admittance at one frequency, as its 2×2 blocks: A = ports A/B (extrinsic), B = ports C/D (intrinsic).</summary>
public readonly record struct NetworkBlocks(C2 Aa, C2 Ab, C2 Ba, C2 Bb);

/// <summary>
/// The DC map from the extrinsic bias and the terminal currents to the intrinsic voltages — linear, because the
/// network is: <c>Vgs_i = Σ a_k u_k</c>, <c>Vds_i = Σ b_k u_k</c>, <c>u = (Vgs, Vds, Ig, Id)</c>.
/// </summary>
public sealed record DcTransfer(double[] Vgs, double[] Vds)
{
    public static DcTransfer Identity { get; } = new([1, 0, 0, 0], [0, 1, 0, 0]);

    /// <summary>True when the currents move the intrinsic voltages at all — a network with series resistance.</summary>
    public bool NeedsCurrents => Vgs[2] != 0 || Vgs[3] != 0 || Vds[2] != 0 || Vds[3] != 0;

    public (double Vgs, double Vds) Apply(double vgs, double vds, double ig, double id) =>
        (Vgs[0] * vgs + Vgs[1] * vds + Vgs[2] * ig + Vgs[3] * id,
         Vds[0] * vgs + Vds[1] * vds + Vds[2] * ig + Vds[3] * id);
}

/// <summary>
/// An extrinsic network: a <c>.csch</c> with exactly six pins — 1 extrinsic gate, 2 extrinsic drain, 3 extrinsic
/// source, 4 intrinsic gate, 5 intrinsic drain, 6 intrinsic source (D5). Pin 3 is ground in every solve.
/// </summary>
public sealed class SixPinNetwork : PinNetwork
{
    private const string Role = "an extrinsic network";

    private SixPinNetwork(Loaded l) : base(l) { }

    /// <summary>Reads and checks a six-pin network (R-mc4-2): a wrong pin count, or a duplicated or missing number, is a
    /// refusal listing the pins found.</summary>
    public static SixPinNetwork Load(string cschPath) => new(Read(cschPath, 6, Role));

    /// <summary><see cref="Load"/> for a schematic held as text — a pasted selection before it is written anywhere
    /// (MC-14 R-mc14-8). <paramref name="name"/> stands in for its path in a refusal.</summary>
    public static SixPinNetwork FromText(string name, string json) => new(ReadText(name, json, 6, Role));

    private string? PinNet(int pin) => pin == 3 ? "0" : null;
    private string Net(int pin) => pin == 3 ? "0" : PinNets[pin - 1];

    /// <summary>
    /// The network as a 4-port at every frequency (R-mc4-3): port A on pins 1–3, B on 2–3, C on 4–6, D on 5–6.
    /// </summary>
    public NetworkBlocks[] Blocks(double[] freqsHz, IReadOnlyDictionary<string, double>? values = null, RunControl? rc = null)
    {
        var y = Admittance(freqsHz, values, PinNet,
            [(Net(1), "0"), (Net(2), "0"), (Net(4), Net(6)), (Net(5), Net(6))], rc);
        return [.. y.Select(m => new NetworkBlocks(
            new C2(m[0, 0], m[0, 1], m[1, 0], m[1, 1]), new C2(m[0, 2], m[0, 3], m[1, 2], m[1, 3]),
            new C2(m[2, 0], m[2, 1], m[3, 0], m[3, 1]), new C2(m[2, 2], m[2, 3], m[3, 2], m[3, 3])))];
    }

    /// <summary>
    /// The DC solve of R-mc4-4 step 2 — inductors short, capacitors open, the extrinsic voltages imposed on pins 1 and
    /// 2 (pin 3 the reference), the terminal currents drawn from pins 4 and 5 and returned on pin 6 — done once per
    /// unit source, which is exact for a linear network.
    /// </summary>
    public DcTransfer Dc(IReadOnlyDictionary<string, double>? values = null)
    {
        double[] gs = new double[4], ds = new double[4];
        for (int k = 0; k < 4; k++)
        {
            double U(int i) => i == k ? 1.0 : 0.0;
            var v = DcVoltages(values, PinNet,
                [VoltageSource("__mc_vg", Net(1), "0", U(0)), VoltageSource("__mc_vd", Net(2), "0", U(1)),
                 CurrentSource("__mc_ig", Net(4), Net(6), U(2)), CurrentSource("__mc_id", Net(5), Net(6), U(3))],
                [Net(4), Net(5), Net(6)]);
            gs[k] = v[0] - v[2];
            ds[k] = v[1] - v[2];
        }
        return new DcTransfer(gs, ds);
    }

    /// <summary>Every mutual's coupling factor <c>|M|/√(L1·L2)</c> at <paramref name="values"/> — a fit holds each below 1
    /// as a constraint, not a bound (R-mc4-2).</summary>
    public IReadOnlyList<double> CouplingFactors(IReadOnlyDictionary<string, double> values)
    {
        var list = new List<double>();
        foreach (var (l1, l2, m) in Mutuals)
        {
            double a = Eval(l1, values), b = Eval(l2, values), c = Eval(m, values);
            list.Add(Math.Abs(c) / Math.Sqrt(a * b));
        }
        return list;
    }

    private static double Eval(string expr, IReadOnlyDictionary<string, double> values)
    {
        var scope = new Scope("network");
        foreach (var (k, v) in values) scope.Bind(k, v.ToString("R", CultureInfo.InvariantCulture));
        return new Evaluator().Eval(expr, scope).AsReal();
    }

    /// <summary>
    /// The defaults of R-mc4-2 for one variable: <c>Min</c> 0 for an R, L, C or mutual M value, <c>Max</c> 100× the
    /// starting value; <paramref name="scaling"/> supplies the D9 rule (a stock network's, or <c>fixed</c>).
    /// </summary>
    public static NetworkVariableSettings DefaultFor(NetworkVariable v, string? scaling = null) => new()
    {
        Min = v.Kind == NetworkElementKind.Other ? null : 0,
        Max = double.IsFinite(v.Value) && v.Value != 0 ? 100 * Math.Abs(v.Value) : null,
        Scaling = scaling ?? PeripheryScaling.Fixed,
    };

    /// <summary>A network block naming this schematic with every variable at its default.</summary>
    public NetworkSettings DefaultSettings(string schematicRelative, IReadOnlyDictionary<string, string>? scaling = null) => new()
    {
        Schematic = schematicRelative,
        Variables = Variables.ToDictionary(v => v.Name, v => DefaultFor(v, scaling?.GetValueOrDefault(v.Name)), StringComparer.Ordinal),
    };
}

/// <summary>A pad network (R-mc4-1): a <c>.csch</c> with pins 1 (probe side) and 2 (device side), returning through ground.</summary>
public sealed class PadNetwork : PinNetwork
{
    private PadNetwork(Loaded l) : base(l) { }

    public static PadNetwork Load(string cschPath) => new(Read(cschPath, 2, "a pad network"));

    /// <summary>The pad as a two-port, ABCD, port 1 at the probe — at every frequency.</summary>
    public C2[] Abcd(double[] freqsHz, RunControl? rc = null) =>
        [.. Admittance(freqsHz, null, _ => null, [(PinNets[0], "0"), (PinNets[1], "0")], rc)
            .Select(m => NetworkAlgebra.YToAbcd(new C2(m[0, 0], m[0, 1], m[1, 0], m[1, 1])))];

    /// <summary>The DC voltage at the device side for a probe-side voltage and a current drawn at the device side:
    /// <c>V2 = a·V1 + b·I</c>, as (a, b).</summary>
    public (double A, double B) Dc()
    {
        double[] r = new double[2];
        for (int k = 0; k < 2; k++)
            r[k] = DcVoltages(null, _ => null,
                [VoltageSource("__mc_v", PinNets[0], "0", k == 0 ? 1 : 0), CurrentSource("__mc_i", PinNets[1], "0", k == 1 ? 1 : 0)],
                [PinNets[1]])[0];
        return (r[0], r[1]);
    }
}
