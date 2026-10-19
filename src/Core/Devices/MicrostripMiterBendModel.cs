using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// MMITER — chamfered (mitred) microstrip bend of any miter fraction and any angle up to 120°, 2-port.
///
/// <para>Electrically: MLIN over the corner's own centre-line length between the planes
/// (<see cref="MicrostripMiterBend.CornerLength"/>), cut in half around a lumped excess
/// (series ΔL/2, shunt ΔC, series ΔL/2) taken from <see cref="MicrostripMiterBend"/>'s table. The line
/// physics — Z₀, εeff(f), conductor and dielectric loss — is <see cref="MicrostripLineModel.Propagation"/>,
/// the same function MLIN stamps with, so the corner section and the MLINs either side of it can
/// never disagree about the line. The excess does not depend on frequency and is resolved once, here.</para>
///
/// <para>Parameters (SI, resolved by the elaborator; the substrate injected as MLIN's is): <c>W</c> —
/// width (m). <c>Angle</c> — turn, counter-clockwise positive from the input arm (MBEND's
/// convention); only <c>|Angle|</c> is electrical. This constructor takes DEGREES; the factory receives
/// the elaborated value in radians (as TLIN's <c>E</c> and MCURVE's <c>Angle</c>) and converts once.
/// <c>M</c> — Douville-James miter fraction; <see cref="double.NaN"/> (absent) means their optimum
/// for the instance's own W/h, which is a right-angle fit: at any other angle it is applied as the
/// same FRACTION of the corner and the instance says so. <c>H</c>, <c>T</c>, <c>Er</c>,
/// <c>Sigma</c>, <c>TanD</c>, optional <c>Roughness</c> — exactly MLIN's.</para>
///
/// <para>Pins: perpendicular to each arm through the inner corner, <c>(W/2)·tan(θ/2)</c> from the
/// centre-line corner — MBEND's planes at 90° — or, for a chamfer reaching past that plane, where the
/// strip is full width again (<see cref="MicrostripMiterBend.PinOffset"/>), with the line in between
/// included in the model.</para>
/// </summary>
public sealed class MicrostripMiterBendModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;

    private const double Mu0 = 4e-7 * Math.PI;
    private const double Eps0 = 8.8541878128e-12;

    private readonly double _wMeters, _hMeters, _tMeters, _epsR, _sigmaSPerM, _tanD, _roughnessMeters;
    private readonly MicrostripValidityReporter _reporter;

    /// <summary>The corner's centre-line length between the planes, metres.</summary>
    public double CornerLengthMeters { get; }

    /// <summary>Distance from the centre-line corner to each pin, metres
    /// (<see cref="MicrostripMiterBend.PinOffset"/>).</summary>
    public double PinOffsetMeters { get; }

    /// <summary>The miter fraction actually used (the Douville-James optimum when none was given).</summary>
    public double Miter { get; }

    /// <summary>Excess series inductance (H, total — half each side of the shunt).</summary>
    public double ExcessInductance { get; }

    /// <summary>Excess shunt capacitance (F).</summary>
    public double ExcessCapacitance { get; }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _reporter.Drain();

    public MicrostripMiterBendModel(double wMeters, double angleDeg, double miter,
        double hMeters, double tMeters, double epsR, double sigmaSPerM, double tanD,
        string instancePath, double roughnessMeters = 0.0)
    {
        _reporter = new MicrostripValidityReporter(instancePath);
        double m = miter;
        if (double.IsNaN(m) && wMeters > 0 && hMeters > 0)
        {
            m = MicrostripMiterBend.DouvilleJamesOptimum(wMeters, hMeters);
            double cap = MicrostripMiterBend.MaxMiter(angleDeg);
            if (m > cap)
            {
                _reporter.ReportOnce("MMITER:optimum-capped",
                    $"the Douville-James optimum M={m:G4} for W/h={wMeters / hMeters:G4} is past the deepest miter " +
                    $"characterised ({cap:G3}); M={cap:G3} is used. Give M explicitly to silence this.");
                m = cap;
            }
            if (Math.Abs(Math.Abs(angleDeg) - 90.0) > 1e-9)
                _reporter.ReportOnce("MMITER:optimum-oblique",
                    $"no M was given, so the Douville-James right-angle optimum (M={m:G4}) is applied as a FRACTION " +
                    $"of the corner at Angle={Math.Abs(angleDeg):G4} deg; it is not an optimum there.");
        }

        if (MicrostripMiterBend.Refusal(wMeters, hMeters, angleDeg, m) is { } why)
            throw new ArgumentException(why.Render());

        _wMeters = wMeters; _hMeters = hMeters; _tMeters = tMeters; _epsR = epsR;
        _sigmaSPerM = sigmaSPerM; _tanD = tanD; _roughnessMeters = roughnessMeters;
        Miter = m;
        CornerLengthMeters = MicrostripMiterBend.CornerLength(wMeters, angleDeg);
        PinOffsetMeters = MicrostripMiterBend.PinOffset(wMeters, angleDeg, m);
        var (lambda, kappa) = MicrostripMiterBend.Excess(wMeters / hMeters, epsR, m, angleDeg, _reporter);
        ExcessInductance = Mu0 * hMeters * lambda;
        ExcessCapacitance = Eps0 * hMeters * kappa;
    }

    /// <summary>The bend's chain matrix at <paramref name="freqHz"/> (&gt; 0).</summary>
    public MicrostripAbcd Abcd(double freqHz)
    {
        double omega = 2.0 * Math.PI * freqHz;
        var (z0, gamma) = MicrostripLineModel.Propagation(freqHz, _wMeters, _hMeters, _tMeters, _epsR,
                                                          _sigmaSPerM, _tanD, _roughnessMeters, _reporter);
        // Pin to centre-line corner: half the corner's own line plus, for a chamfer reaching past the
        // inner-corner plane, the full-width line from that plane out to the pin — PinOffset in all.
        var half = MicrostripAbcd.UniformSection(new Complex(z0, 0.0), gamma * PinOffsetMeters);
        var series = new MicrostripAbcd(Complex.One, new Complex(0.0, omega * ExcessInductance / 2.0), Complex.Zero, Complex.One);
        var shunt = new MicrostripAbcd(Complex.One, Complex.Zero, new Complex(0.0, omega * ExcessCapacitance), Complex.One);
        return half.Cascade(series).Cascade(shunt).Cascade(series).Cascade(half);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int n1 = c.Nodes[0], n2 = c.Nodes[1];
        if (omega <= 0.0)
        {
            // DC: the corner is a short piece of conductor — both ports are one node (MBEND's DC limit).
            mna.AddAdmittance(n1, n2, new Complex(1.0e9, 0.0));
            return;
        }

        var t = Abcd(omega / (2.0 * Math.PI));
        Complex det = t.A * t.D - t.B * t.C;
        double z0Scale = MicrostripLineModel.Propagation(0.0, _wMeters, _hMeters, _tMeters, _epsR,
                                                         _sigmaSPerM, _tanD, _roughnessMeters, _reporter).Z0;
        // The excess ΔL is usually NEGATIVE (the current cuts the corner), so the corner's net series
        // impedance B can come close to zero; its shunt C cannot at the same time. Stamp nodal Y from
        // B unless it has nearly cancelled (three orders below C, both normalised to Z0), in which case
        // the branch-current Z form from C (MTAPER's) is the well-conditioned one.
        if (t.B.Magnitude / z0Scale >= 1e-3 * t.C.Magnitude * z0Scale)
        {
            mna.AddBlockAdmittance(n1, n1, t.D / t.B);
            mna.AddBlockAdmittance(n1, n2, -det / t.B);
            mna.AddBlockAdmittance(n2, n1, -Complex.One / t.B);
            mna.AddBlockAdmittance(n2, n2, t.A / t.B);
            return;
        }

        var (z11, z12, z21, z22) = t.ToZ();
        int b1 = mna.AddBranch();
        int b2 = mna.AddBranch();
        mna.AddBranchCurrent(b1, n1, 0);
        mna.AddBranchCurrent(b2, n2, 0);
        mna.AddConstraint(b1, n1, Complex.One);
        mna.AddConstraint(b2, n2, Complex.One);
        mna.AddBranchConstraint(b1, b1, -z11);
        mna.AddBranchConstraint(b1, b2, -z12);
        mna.AddBranchConstraint(b2, b1, -z21);
        mna.AddBranchConstraint(b2, b2, -z22);
    }
}
