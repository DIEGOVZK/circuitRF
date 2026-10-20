using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-8 §4: a truth built from [I11] eq. 3 with λ = 0, written as an SDD and simulated at its intrinsic
/// plane, is recovered by the drift decomposition — QGS, QGD and QDS, <c>Qdrift = 0</c> along <c>Id = 0</c>, and the
/// model's capacitances against the truth's at held-out points.
///
/// <para>The truth's drift charge is <c>Td·Id²·e^(vgd/10)</c>: zero with its derivatives wherever <c>Id = 0</c>, so at
/// <c>vds = 0</c> the measured <c>Cgs</c> and <c>Cgd</c> are exactly the depletion charges' — the condition [I11] §III
/// takes them under. Its <c>Id</c> is <see cref="ComposedTestModels"/>' drain current, handed to the fit as the current
/// step's result, so the test measures the decomposition and not a current fit.</para>
/// </summary>
public sealed class DriftChargeTests(ITestOutputHelper output)
{
    private const string Globals =
        "Cgs0 = 0.1e-12\nCgs1 = 0.2e-12\nCgd0 = 0.02e-12\nCgd1 = 0.04e-12\nCds0 = 0.08e-12\nCds1 = 0.03e-12\nTd = 2e-12\n";

    private static string V(string eq) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(eq, @"\bvgs\b", "_v1"), @"\bvds\b", "_v2");

    private const string Qgs = "Cgs0*_v1+Cgs1/2*(_v1+0.3*ln(cosh((_v1+1)/0.3)))";
    private const string Qgd = "Cgd0*(_v1-_v2)+Cgd1/2*((_v1-_v2)+0.3*ln(cosh((_v1-_v2+0.8)/0.3)))";
    private const string Qds = "Cds0*_v2+Cds1*ln(1+_v2)";
    private static string Qdrift => $"Td*({V(ComposedTestModels.IdEq)})^2*exp(0.1*(_v1-_v2))";

    private static string Device =>
        ComposedTestModels.SddGlobals + Globals +
        $"SDD:X1 g 0 d 0 I[2,0]={V(ComposedTestModels.IdEq)} " +
        $"I[1,1]=({Qgs})+({Qgd})+({Qdrift}) I[2,1]=-({Qgd})-({Qdrift})+({Qds})\n";

    // The truth pieces' capacitances, by hand.
    private static double TruthCgs(double v) => 0.1e-12 + 0.1e-12 * (1 + Math.Tanh((v + 1) / 0.3));
    private static double TruthCgd(double v) => 0.02e-12 + 0.02e-12 * (1 + Math.Tanh((v + 0.8) / 0.3));
    private static double TruthCds(double v) => 0.08e-12 + 0.03e-12 / (1 + v);

    private static ChargeData Data(double[] vgs, double[] vds)
    {
        var (doc, data, intrinsic) = SyntheticFetData.SDocument(
            SyntheticFetData.BiasedS(Device, vgs, vds, SyntheticFetData.CvFreqs), network: false);
        return ChargeFit.Gather(doc, data, intrinsic, ChargeFitSettings.Default, []);
    }

    [Fact]
    public void DriftDecomposition_RecoversTheTruth()
    {
        var current = new CurrentFitResult(CurrentFitSettings.Default, new CurrentFitRecord
        {
            Id = Constituent.Equation(ComposedTestModels.IdEq, ["vgs", "vds"], ComposedTestModels.IdParams),
            Ig = Constituent.GateEquations("0", "0", ["vgs", "vds"]),
        }, new Dictionary<string, DataCube>());
        var data = Data(SyntheticFetData.CvVgs, SyntheticFetData.CvVds);
        var fit = ChargeFit.Run(data, ChargeFitSettings.Default with { Restarts = 1, Drift = new DriftChargeSettings() }, current);
        foreach (string line in fit.Record.Report) output.WriteLine(line);
        Assert.Equal(Constituent.DriftMethod, fit.Record.Qg.Method);

        var program = ComposedCoreProgram.Compile(ChargeFit.Model(fit, current), "drift");
        var drift = program.Drift!;
        var g = new double[2];
        double Slope(string piece, double v) { drift.Piece(piece, [v], g.AsSpan(0, 1)); return g[0]; }

        // The depletion pieces, as capacitances over where they were trained (vds = 0, so vgd = vgs) and QDS over all vds.
        // Measured: 0.34 % at worst (QGS); Qdrift along Id = 0, 0.27 % of its size; held-out capacitances 0.29 %.
        foreach (var (piece, truth, lo, hi) in new (string, Func<double, double>, double, double)[]
                 { (DriftCharges.Qgs, TruthCgs, -2, 0.7), (DriftCharges.Qgd, TruthCgd, -2, 0.7), (DriftCharges.Qds, TruthCds, 0, 10) })
        {
            var v = Enumerable.Range(0, 41).Select(k => lo + (hi - lo) * k / 40).ToArray();
            double scale = v.Max(truth), worst = v.Max(x => Math.Abs(Slope(piece, x) - truth(x))) / scale;
            output.WriteLine($"{piece}: worst {worst:P3} of {scale:E3} F");
            Assert.True(worst < 0.02, $"{piece} {worst:P3}");
        }

        // Qdrift = 0 along Id = 0, relative to the drift charge's own size over the data.
        double size = data.X.Max(p => Math.Abs(drift.Piece(DriftCharges.Qdrift, [p.Vgs - p.Vds, Id(p.Vgs, p.Vds)], g)));
        double zero = Enumerable.Range(0, 41).Select(k => -12.0 + 12.7 * k / 40)
                                .Max(vgd => Math.Abs(drift.Piece(DriftCharges.Qdrift, [vgd, 0.0], g)));
        output.WriteLine($"Qdrift at Id = 0: {zero:E3} C against {size:E3} C");
        Assert.True(zero < 0.01 * size, $"{zero:E3} C");

        // The model's capacitances against the truth's at held-out points.
        var held = Data(SyntheticFetData.CvHeldVgs, SyntheticFetData.CvHeldVds);
        double[][] truthC = [held.C11, held.C12, held.C21, held.C22];
        for (int i = 0; i < 4; i++)
        {
            double scale = truthC[i < 2 ? 0 : 3].Concat(truthC[i]).Max(Math.Abs);
            double worst = held.X.Select((p, k) =>
            {
                var c = ChargeFit.Capacitances(program, p.Vgs, p.Vds);
                double[] m = [c.C11, c.C12, c.C21, c.C22];
                return Math.Abs(m[i] - truthC[i][k]);
            }).Max() / scale;
            output.WriteLine($"{ChargeFitRecord.Capacitances[i]}: worst held-out {worst:P3} of {scale:E3} F");
            Assert.True(worst < 0.02, $"{ChargeFitRecord.Capacitances[i]} {worst:P3}");
        }
    }

    private static double Id(double vgs, double vds)
    {
        var p = ComposedTestModels.IdParams.ToDictionary(x => x.Name, x => x.Value);
        double s = Math.Log(1 + Math.Exp((vgs - p["Vto"]) / p["Vs"])) * p["Vs"];
        return p["beta"] * s * s * (1 + p["lambda"] * vds) * Math.Tanh(p["alpha"] * vds);
    }
}
