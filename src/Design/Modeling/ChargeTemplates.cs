// The equation templates of the charge step (brief-modelcomposer-8 R-mc8-2): terminal charges written as expressions in
// the intrinsic vgs and vds, so every template is terminal-charge-conserving by construction (D7). A template is fitted
// to the capacitances — its partial derivatives — and its constant is set afterwards so the charge is zero at the anchor.
//
//   Depletion — [P1] eq. 6.11: a junction charge −C0·φ/(η+1)·(1 − v/φ)^(η+1) continued LINEARLY above Fc·φ, written with
//               min/max so neither branch is ever evaluated outside its range; Qg is a gate–source and a gate–drain
//               junction, Qd the gate–drain junction's other side plus a linear drain–source charge.
//   Angelov   — Angelov's charges: each junction's capacitance Cp + C0·(1 + tanh ψ1)·(1 ± tanh ψ2), a constant plus a
//               step from 0 to 2·C0 in its own voltage, switched by tanh in vds — integrated in closed form along that
//               voltage as Cp·v + C0·(v + ln(cosh ψ1)/P1)·(1 ± tanh ψ2). The step's "1 +" is what floors the
//               capacitance at Cp below pinch-off at every vds; without it the floor moves with vds and can go negative.

namespace CircuitRF.Design.Modeling;

/// <summary>The points a charge template's start is estimated from: intrinsic voltages and the row's two capacitances.</summary>
public sealed record ChargeTemplateData(double[] Vgs, double[] Vds, double[] C1, double[] C2);

/// <summary>A charge template: the expression of <c>Qg</c> and of <c>Qd</c>, and each one's parameters.</summary>
public sealed record ChargeTemplate(string Name, string Qg, string Qd,
                                    Func<ChargeTemplateData, IReadOnlyList<TemplateParameter>> QgParameters,
                                    Func<ChargeTemplateData, IReadOnlyList<TemplateParameter>> QdParameters)
{
    public string Expression(bool gate) => gate ? Qg : Qd;

    public IReadOnlyList<TemplateParameter> Parameters(bool gate, ChargeTemplateData d) => gate ? QgParameters(d) : QdParameters(d);
}

public static class ChargeTemplates
{
    public const string Depletion = "Depletion", Angelov = "Angelov";

    private static string Junction(string v, string c0) =>
        $"(-{c0}*Phi/(Eta+1)*(1-min({v},Fc*Phi)/Phi)^(Eta+1)+{c0}*(1-Fc)^Eta*max({v}-Fc*Phi,0))";

    private const string GateSource = "Cgsp*vgs+Cgs0*(vgs+ln(cosh(P10+P11*vgs))/P11)*(1+tanh(P30-P31*vds))";
    private const string GateDrain = "Cgdp*(vgs-vds)+Cgd0*((vgs-vds)+ln(cosh(P20+P21*(vgs-vds)))/P21)*(1-tanh(P40+P41*vds))";

    /// <summary>The junction's built-in potential, grading and linear-extension fraction: φ 1 V, η −0.5 (abrupt), Fc 0.8
    /// locked — η is kept away from −1, where [P1] eq. 6.11 is singular.</summary>
    private static readonly TemplateParameter[] Junctions =
        [new("Phi", 1.0, 0.2, 5.0), new("Eta", -0.5, -0.95, -0.05), new("Fc", 0.8, 0.8, 0.8, Lock: true)];

    public static IReadOnlyList<ChargeTemplate> All { get; } =
    [
        new(Depletion,
            Junction("vgs", "Cgs0") + "+" + Junction("vgs-vds", "Cgd0"),
            "-" + Junction("vgs-vds", "Cgd0") + "+Cds*vds",
            d => [Cap("Cgs0", Less(d.C1, d.C2)), Cap("Cgd0", Typical(d.C2)), .. Junctions],
            d => [Cap("Cgd0", Typical(d.C1)), Cap("Cds", Less(d.C2, d.C1)), .. Junctions]),
        new(Angelov,
            GateSource + "+" + GateDrain,
            "-(" + GateDrain + ")+Cds*vds",
            d => [.. Steps("gs", Less(d.C1, d.C2), d.Vgs), .. Steps("gd", Typical(d.C2), d.Vgs)],
            d => [.. Steps("gd", Typical(d.C1), d.Vgs), Cap("Cds", Less(d.C2, d.C1))]),
    ];

    public static ChargeTemplate? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    public static IEnumerable<string> Names => All.Select(t => t.Name);

    /// <summary>A capacitance between 0 and 100× its typical size, starting there.</summary>
    private static TemplateParameter Cap(string name, double typical)
    {
        double c = Math.Max(typical, 1e-18);
        return new TemplateParameter(name, c, 0, 100 * c);
    }

    /// <summary>Angelov's parameters of one junction (<paramref name="j"/> = gs or gd): a constant part and a step that each
    /// carry half the typical capacitance at the step's centre, mid-range in vgs, and its vds switch.</summary>
    private static TemplateParameter[] Steps(string j, double typical, double[] vgs)
    {
        double c = Math.Max(typical, 1e-18), mid = 0.5 * (vgs.Min() + vgs.Max()), span = Math.Max(vgs.Max() - vgs.Min(), 0.1);
        string n = j == "gs" ? "1" : "2", s = j == "gs" ? "3" : "4";
        double slope = 4 / span;
        return
        [
            new($"C{j}p", 0.5 * c, 0, 100 * c), new($"C{j}0", 0.5 * c, 0, 100 * c),
            new($"P{n}0", -slope * mid, -50, 50), new($"P{n}1", slope, 0.01, 50),
            new($"P{s}0", 0, -10, 10), new($"P{s}1", 0.1, -10, 10),
        ];
    }

    private static double Typical(double[] c) => c.Length == 0 ? 0 : c.Select(Math.Abs).Average();

    /// <summary>A diagonal's typical size less its row's off-diagonal one: <c>Cgs = C11 + C12</c> with <c>C12 = −Cgd</c>, and
    /// <c>Cds = C22 + C21</c> with <c>C21 ≈ −Cgd</c>. Floored at a tenth of the diagonal, where the data say the
    /// off-diagonal is the larger.</summary>
    private static double Less(double[] diagonal, double[] offDiagonal) =>
        Math.Max(Typical(diagonal) - Typical(offDiagonal), 0.1 * Typical(diagonal));
}
