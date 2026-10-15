using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The ideal transformer, turns ratio <c>N</c>:1 (primary to secondary), lossless and
/// frequency-independent — it passes DC as readily as RF, which is what "ideal" means here. For a
/// winding with inductance, coupling and a low-frequency roll-off, use two inductors and a
/// <c>Mutual</c>.
///
/// <code>
///   V(p+) − V(p−) = N · (V(s+) − V(s−))        i_s = −N · i_p
/// </code>
/// <para>with <c>i_p</c> the current INTO <c>p+</c> and <c>i_s</c> the current INTO <c>s+</c>: the
/// power into one winding comes out of the other, so the device is passive and its ordinary stamp is
/// its passive stamp.</para>
///
/// <para><b>One branch current, two KCL couplings.</b> The primary current is the unknown; the
/// secondary's is −N times it, stamped as a node-to-branch coupling at <c>s±</c> rather than as a
/// second unknown. The constraint row is the voltage ratio. Nets: <c>[p+, p−, s+, s−]</c>, the
/// dotted ends first, in the ± pair order every four-terminal part here uses.</para>
/// </summary>
public sealed class IdealTransformerModel : ComponentModel
{
    public override int       PortCount => 2;   // primary pair, secondary pair
    public override ModelKind Kind      => ModelKind.Linear;

    public override string[] TerminalNames => ["p+", "p-", "s+", "s-"];

    /// <summary>Matrix index of the primary branch current from the most recent stamp; −1 before.</summary>
    public int LastBranchIndex { get; private set; } = -1;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        if (c.Nodes.Length != 4)
            throw new DeviceRefusalException(IdealDeviceDiagnostics.NetCount(
                "Transformer", c.InstancePath, 4, string.Join(", ", TerminalNames), c.Nodes.Length));
        int pp = c.Nodes[0], pm = c.Nodes[1], sp = c.Nodes[2], sm = c.Nodes[3];

        double n = 1;
        if (c.Parameters.TryGetValue("N", out var v))
            n = v.Kind == ValueKind.Real ? v.AsReal() : v.AsComplex().Real;
        if (!(n > 0 && double.IsFinite(n)))
            throw new DeviceRefusalException(IdealDeviceDiagnostics.TransformerRatio(c.InstancePath, n));

        int br = mna.AddBranch();
        LastBranchIndex = br;
        mna.AddBranchCurrent(br, pp, pm);                       // i_p in at p+, out at p−
        mna.AddNodeBranchCoupling(sp, br, new Complex(-n, 0));  // i_s = −N·i_p in at s+ …
        mna.AddNodeBranchCoupling(sm, br, new Complex(+n, 0));  // … and out at s−
        mna.AddConstraint(br, pp, Complex.One);
        mna.AddConstraint(br, pm, -Complex.One);
        mna.AddConstraint(br, sp, new Complex(-n, 0));
        mna.AddConstraint(br, sm, new Complex(+n, 0));
        mna.AddSourceValue(br, Complex.Zero);
    }
}
