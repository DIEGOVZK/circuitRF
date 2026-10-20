using System.Text.RegularExpressions;
using CircuitRF.Core.Devices.Composed;

namespace CircuitRF.Core.Tests.Composed;

/// <summary>
/// The equation <c>.cmodel</c> the brief-modelcomposer-1 gates share, and the SDD that is its
/// independent reference. Linked into Engine.Tests and Ui.Tests, so every gate reads the same device.
///
/// <para>The device: a Curtice-style <c>tanh</c> drain current with a smooth pinch-off, diode gate
/// branches to source and to drain, and NON-SEPARABLE charges whose cross derivatives differ — a
/// transcapacitance, <c>∂Qd/∂vgs ≠ ∂Qg/∂vds</c> — inside the GaN two-shell network of overview D5.</para>
/// </summary>
public static class ComposedTestModels
{
    public const string IdEq   = "beta*(ln(1+exp((vgs-Vto)/Vs))*Vs)^2*(1+lambda*vds)*tanh(alpha*vds)";
    public const string IgGsEq = "Isg*(exp(vgs/Vtg)-1)";
    public const string IgGdEq = "Isg*(exp((vgs-vds)/Vtg)-1)";
    public const string QgEq   = "C0*vgs+C1*(vgs-vds)+Cx*vgs*tanh(vds)";
    public const string QdEq   = "-C1*(vgs-vds)+Cd*vds+Cy*vgs*tanh(vds)";

    public static readonly (string Name, double Value)[] IdParams =
        [("beta", 0.04), ("Vto", -1.2), ("Vs", 0.15), ("lambda", 0.03), ("alpha", 2.5)];
    public static readonly (string Name, double Value)[] IgParams = [("Isg", 1e-12), ("Vtg", 0.035)];
    public static readonly (string Name, double Value)[] QgParams = [("C0", 0.3e-12), ("C1", 0.05e-12), ("Cx", 0.02e-12)];
    public static readonly (string Name, double Value)[] QdParams = [("C1", 0.05e-12), ("Cd", 0.1e-12), ("Cy", 0.04e-12)];

    /// <summary>The D5 GaN two-shell network as a six-pin define block, values as its variables.</summary>
    public const string GanVariables =
        "parameters Cpga=7e-15 Cpda=7e-15 Cpdga=0.5e-15 Cpgi=4.9e-15 Cpdi=21e-15 Cpdgi=1e-15\n" +
        "parameters Lg=39.5e-12 Ld=46.4e-12 Ls=5.25e-12 M_feedback=1e-12 rg=5.2 rd=9.2 rs=6.6\n";

    /// <summary>The network's elements over pins P1..P6. The gate resistor is named <c>rg</c> so the
    /// probe gate can find <c>X1.rg</c>.</summary>
    public const string GanElements =
        "C:C1 P1 P3 C=Cpga\nC:C4 P1 P2 C=Cpdga\nC:C6 P2 P3 C=Cpda\n" +
        "L:L1 P1 n1 L=Lg\nL:L2 P2 n3 L=Ld\nL:L3 P3 n2 L=Ls\n" +
        "R:rg n1 P4 R=rg\nR:rd n3 P5 R=rd\nR:rs n2 P6 R=rs\n" +
        "C:C2 n1 n2 C=Cpgi\nC:C3 n1 n3 C=Cpdgi\nC:C5 n3 n2 C=Cpdi\n" +
        "Mutual:M1 Inductor1=\"L1\" Inductor2=\"L2\" M=M_feedback\n";

    public static string GanNetwork =>
        "define gan (P1 P2 P3 P4 P5 P6)\n" + GanVariables + GanElements + "end\n";

    /// <summary>D9's defaults for the GaN network.</summary>
    public static readonly ScalingRule[] GanScaling =
    [
        new("rg", PeripheryScaling.PerFingerInverse), new("rd", PeripheryScaling.PerFingerInverse),
        new("rs", PeripheryScaling.PerFingerInverse),
        new("Cpgi", PeripheryScaling.PerFinger), new("Cpdi", PeripheryScaling.PerFinger),
        new("Cpdgi", PeripheryScaling.PerFinger),
    ];

    public static ComposedModel Model(ModelTemperature? temperature = null, int n0 = 4)
    {
        string[] inputs = ["vgs", "vds"];
        return new ComposedModel
        {
            BasePeriphery = new BasePeriphery(n0, "125 um"),
            Temperature   = temperature ?? ModelTemperature.Single(25),
            Network       = new ExtrinsicNetwork(GanNetwork, GanScaling),
            Core          = new CoreConstituents(
                Constituent.Equation(IdEq, inputs, IdParams),
                Constituent.GateEquations(IgGsEq, IgGdEq, inputs, IgParams),
                Constituent.Equation(QgEq, inputs, QgParams),
                Constituent.Equation(QdEq, inputs, QdParams)),
            Provenance    = new ModelProvenance
            {
                Created = "2026-10-19T00:00:00Z", CircuitRfVersion = "test",
                Sources = [new("data/bias.s2p", new string('0', 64))], SettingsDigest = "abc",
            },
        };
    }

    /// <summary>
    /// The intrinsic charge's Jacobian against a central difference (1e-6 V) of the charge, at <paramref name="at"/> (every
    /// port's voltage): the largest mismatch over <c>dc[0..1, 0..1]</c> as a fraction of the largest of those entries.
    /// </summary>
    public static double IntrinsicJacobianError(ComponentModel core, double[] at)
    {
        const double h = 1e-6;
        var r = core.Evaluate(new PortVoltages(at));
        double scale = 0, worst = 0;
        for (int p = 0; p < 2; p++) for (int j = 0; j < 2; j++) scale = Math.Max(scale, Math.Abs(r.Dc[p, j]));
        for (int j = 0; j < 2; j++)
        {
            var plus = (double[])at.Clone();  plus[j] += h;
            var minus = (double[])at.Clone(); minus[j] -= h;
            var qp = core.Evaluate(new PortVoltages(plus)).Q;
            var qm = core.Evaluate(new PortVoltages(minus)).Q;
            for (int p = 0; p < 2; p++) worst = Math.Max(worst, Math.Abs(r.Dc[p, j] - (qp[p] - qm[p]) / (2 * h)));
        }
        return worst / scale;
    }

    /// <summary>Writes <paramref name="model"/> into a fresh temporary folder; returns its path.</summary>
    public static string WriteTemp(ComposedModel model, string name = "gan.cmodel")
    {
        string dir = Path.Combine(Path.GetTempPath(), $"cmodel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        CmodelFile.Write(model, path);
        return path;
    }

    // ── The SDD reference ────────────────────────────────────────────────────

    /// <summary>The constituents' constants as global variables, for the reference SDD's scope.</summary>
    public static string SddGlobals =>
        string.Concat(IdParams.Concat(IgParams).Concat(QgParams).Concat(QdParams)
                             .DistinctBy(p => p.Name)
                             .Select(p => $"{p.Name} = {p.Value:R}\n"));

    private static string Sdd(string eq)
        => Regex.Replace(Regex.Replace(eq, @"\bvgs\b", "_v1"), @"\bvds\b", "_v2");

    /// <summary>
    /// The reference device as a cell: the same network written out by hand with every value
    /// multiplied as stated, and an SDD carrying the same equations times <paramref name="coreScale"/>
    /// on <c>gi si di si</c>.
    /// </summary>
    public static string ReferenceCell(string name, double coreScale = 1.0,
                                       double resistorScale = 1.0, double innerCapScale = 1.0)
    {
        string k = coreScale.ToString("R");
        string elements = GanElements
            .Replace("P4", "gi").Replace("P5", "di").Replace("P6", "si")
            .Replace("R=rg", $"R={(5.2 * resistorScale):R}").Replace("R=rd", $"R={(9.2 * resistorScale):R}")
            .Replace("R=rs", $"R={(6.6 * resistorScale):R}")
            .Replace("C=Cpgi", $"C={(4.9e-15 * innerCapScale):R}").Replace("C=Cpdi", $"C={(21e-15 * innerCapScale):R}")
            .Replace("C=Cpdgi", $"C={(1e-15 * innerCapScale):R}");
        return
            $"define {name} (P1 P2 P3)\n" + GanVariables + elements +
            $"SDD:core gi si di si I[1,0]={k}*(({Sdd(IgGsEq)})+({Sdd(IgGdEq)})) " +
            $"I[2,0]={k}*(({Sdd(IdEq)})-({Sdd(IgGdEq)})) " +
            $"I[1,1]={k}*({Sdd(QgEq)}) I[2,1]={k}*({Sdd(QdEq)})\n" +
            "end\n";
    }
}
