using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The operational amplifier, linear and frequency-independent: ideal by default, a finite-gain
/// voltage amplifier when <c>A</c> is stated.
///
/// <para><b><c>A=0</c>, the default, is the ideal op-amp</b> — infinite open-loop gain. Zero is the
/// spelling because a gain of zero is not an amplifier anyone would place, and because a blank value
/// is the <c>.cnl</c> reader's empty-parameter trap (src/Core/CLAUDE.md).</para>
///
/// <para><b>Ideal is a nullor</b>: the inputs draw no current and are held at the same
/// voltage, and the output is a voltage source to ground whose current is whatever the circuit needs
/// to make that true. That is the op-amp of every textbook figure — the feedback network alone sets
/// the gain — and it is exact in MNA rather than a large number standing in for infinity: the
/// constraint row is <c>V(in+) − V(in−) = 0</c> and the output node carries the branch current.</para>
///
/// <para><b>With <c>A</c></b> the output obeys <c>V(out) = A·(V(in+) − V(in−)) + Rout·i</c>, written
/// divided through by A so the same row reads the ideal case as A → ∞ — the two are one stamp, and a
/// sweep of A towards large values converges on the ideal answer rather than on a different circuit.
/// <c>Rout</c> (0 by default) is the output resistance; ideal, it has nothing to act
/// against and is ignored, as it is in the ideal device.</para>
///
/// <para><b>Three pins, or five.</b> <c>Pins=5</c> adds <c>V+</c> and <c>V−</c>, the supply pins a
/// drawing usually wires. They carry no current and constrain nothing — the model rails nothing —
/// so a figure with its supplies drawn reads as the same circuit as one without. A supply pin wired
/// to nothing else is a node nothing defines, which the DC engine's own gmin keeps solvable and an
/// AC analysis refuses as any floating node is refused.</para>
///
/// <para>Nets: <c>[in+, in−, out]</c>, then <c>[V+, V−]</c> when <c>Pins=5</c>. The output is
/// referenced to ground, as every op-amp's is.</para>
/// </summary>
public sealed class OpAmpModel : ComponentModel
{
    public OpAmpModel(int pins = 3)
    {
        if (pins is not (3 or 5))
            throw new DeviceRefusalException(IdealDeviceDiagnostics.OpAmpPins(pins.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        PinCount = pins;
    }

    /// <summary>3, or 5 with the supply pins.</summary>
    public int PinCount { get; }

    public override int       PortCount => 2;   // the output (to ground) and the differential input
    public override ModelKind Kind      => ModelKind.Linear;

    public override string[] TerminalNames => PinCount == 5
        ? ["in+", "in-", "out", "V+", "V-"]
        : ["in+", "in-", "out"];

    /// <summary>Matrix index of the output branch current from the most recent stamp; −1 before.</summary>
    public int LastBranchIndex { get; private set; } = -1;

    public override Activity Activity => Activity.ActiveExact;

    /// <inheritdoc/>
    public override string? PassivationNote => "A → 0; the output stays, as 0 V behind Rout";

    /// <summary>
    /// <c>A → 0</c>: the output is a 0 V source behind <c>Rout</c> and the inputs sense nothing. The
    /// branch and its row STAY, for the reason <see cref="VcvsModel.StampPassive"/> gives — the
    /// passive assembly is subtracted from the active one entry by entry.
    /// </summary>
    public override void StampPassive(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int outN = Nets(c).Out;
        int br = mna.AddBranch();
        LastBranchIndex = br;
        mna.AddBranchCurrent(br, outN, 0);
        mna.AddConstraint(br, outN, Complex.One);
        double rout = Real(c, "Rout") ?? 0;
        if (rout != 0) mna.AddBranchConstraint(br, br, new Complex(-rout, 0));
        mna.AddSourceValue(br, Complex.Zero);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        var (inP, inM, outN) = Nets(c);
        double? a = Real(c, "A");
        if (a == 0) a = null;   // A=0 is the ideal op-amp
        if (a is { } av && !(av > 0 && double.IsFinite(av)))
            throw new DeviceRefusalException(IdealDeviceDiagnostics.OpAmpGain(c.InstancePath, av));
        double rout = Real(c, "Rout") ?? 0;
        if (rout < 0)
            throw new DeviceRefusalException(IdealDeviceDiagnostics.OpAmpRout(c.InstancePath, rout));

        // Branch current i flows out → ground through the output source. The row is
        // V(out)/A − (V(in+) − V(in−)) − (Rout/A)·i = 0, which is V(in+) = V(in−) in the ideal limit.
        int br = mna.AddBranch();
        LastBranchIndex = br;
        mna.AddBranchCurrent(br, outN, 0);
        if (a is { } gain)
        {
            mna.AddConstraint(br, outN, new Complex(1 / gain, 0));
            if (rout != 0) mna.AddBranchConstraint(br, br, new Complex(-rout / gain, 0));
        }
        mna.AddConstraint(br, inP, new Complex(-1, 0));
        mna.AddConstraint(br, inM, new Complex(+1, 0));
        mna.AddSourceValue(br, Complex.Zero);
    }

    private (int InP, int InM, int Out) Nets(ElaboratedComponent c)
    {
        if (c.Nodes.Length != PinCount)
            throw new DeviceRefusalException(IdealDeviceDiagnostics.NetCount(
                "OpAmp", c.InstancePath, PinCount, string.Join(", ", TerminalNames), c.Nodes.Length));
        return (c.Nodes[0], c.Nodes[1], c.Nodes[2]);
    }

    private static double? Real(ElaboratedComponent c, string key) =>
        c.Parameters.TryGetValue(key, out var v)
            ? (v.Kind == ValueKind.Real ? v.AsReal() : v.AsComplex().Real)
            : null;
}
