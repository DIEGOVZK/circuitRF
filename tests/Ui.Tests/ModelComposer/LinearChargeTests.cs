using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Modeling;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-17: the Linear charge template fits from one S point — exactly there, and to the least-squares
/// constant matrix with its spread from a few — while ANN and Table keep their minimum of four and say how to get past
/// it; the conservation check reports (a) as not run and reads (b) zero; and a one-bias model exported and run as
/// S-parameters at that bias matches the data, with its provenance saying the charges are linear.
/// </summary>
public sealed class LinearChargeTests(ITestOutputHelper output)
{
    private const double C11 = 0.35e-12, C12 = -0.05e-12, C21 = -0.09e-12, C22 = 0.13e-12;

    private static readonly ChargeConstituentSettings Linear =
        new() { Method = ChargeMethod.Equation, Template = ChargeTemplates.Linear };

    private static readonly ChargeFitSettings LinearBoth = ChargeFitSettings.Default with { Qg = Linear, Qd = Linear };

    private static double P(CircuitRF.Core.Devices.Composed.Constituent c, string key) => c.Parameters.First(kv => kv.Key == key).Value;

    [Fact]
    public void OneBias_RecoversTheMatrix_AndTheCheckReportsTheDataPartNotRun()
    {
        var data = ChargeData.Of([(-0.6, 6.0)], [C11], [C12], [C21], [C22]);
        var fit = ChargeFit.Run(data, LinearBoth);
        foreach (string line in fit.Record.Report) output.WriteLine(line);
        foreach (var (name, c, truth) in new[] { ("C11", fit.Record.Qg, C11), ("C12", fit.Record.Qg, C12), ("C21", fit.Record.Qd, C21), ("C22", fit.Record.Qd, C22) })
            Assert.True(Math.Abs(P(c, name) / truth - 1) < 1e-9, $"{name}: {P(c, name):R} against {truth:R}");
        Assert.Contains(fit.Record.Report, l => l.StartsWith("Cgs 300 fF, Cgd 50 fF, Cds 80 fF, Cm -40 fF", StringComparison.Ordinal));
        Assert.Equal("Qg, Qd: Linear from 1 bias point(s)", ChargeFit.LinearProvenance(LinearBoth, fit.Record));

        var r = ConservationCheck.Run(data, fit, null, new LargeSignalSettings());
        foreach (string line in r.Lines) output.WriteLine(line);
        Assert.Null(r.Data);
        Assert.Contains("data: not run — 1 bias point(s); the data's curl needs at least 4 across the bias plane.", r.Lines);
        Assert.Equal(0, r.Model!.GateRms);
        Assert.Equal(0, r.Model.DrainRms);
        Assert.NotNull(r.LargeSignal);
    }

    [Fact]
    public void TwoBiases_FitTheirMean_AndTheReportCarriesTheSpread()
    {
        var data = ChargeData.Of([(-1.0, 4.0), (-0.2, 8.0)], [0.30e-12, 0.40e-12], [C12, C12], [C21, C21], [C22, C22]);
        var fit = ChargeFit.Run(data, LinearBoth);
        foreach (string line in fit.Record.Report) output.WriteLine(line);
        Assert.True(Math.Abs(P(fit.Record.Qg, "C11") / 0.35e-12 - 1) < 1e-6, $"C11 {P(fit.Record.Qg, "C11"):R}");
        Assert.Contains("Qg: Linear from 2 bias point(s) — C11 350 fF ± 50 fF, C12 -50 fF ± 0 F.", fit.Record.Report);
    }

    [Fact]
    public void ThreePoints_AnnIsRefusedNamingTheWayOut_AndLinearFits()
    {
        var data = ChargeData.Of([(-1.0, 4.0), (-0.2, 8.0), (0.0, 2.0)], [C11, C11, C11], [C12, C12, C12], [C21, C21, C21], [C22, C22, C22]);
        var ex = Assert.Throws<ModelingException>(() => ChargeFit.Run(data, ChargeFitSettings.Default));
        Assert.Equal("Charge: 3 usable S point(s); an ANN charge needs at least 4 — the Linear equation fits from one.", ex.Message);
        Assert.Equal(CircuitRF.Core.Devices.Composed.Constituent.EquationMethod, ChargeFit.Run(data, LinearBoth).Record.Qg.Method);
    }

    [Fact]
    public void OneBiasModel_Exported_MatchesTheDataAsSParameters_AndSaysItsChargesAreLinear()
    {
        double[] vgs = [-0.6], vds = [6];
        string cmc = ExportFixture.Document(charge: false, vgs: vgs, vds: vds);
        var doc = CmcFile.Read(cmc);
        CmcFile.Write(cmc, ChargeFit.WithSettings(doc, LinearBoth));
        var fit = ChargeFit.FitFile(cmc);
        foreach (string line in fit.Record.Report) output.WriteLine(line);

        string path = Path.Combine(Path.GetDirectoryName(cmc)!, "out", "fet.cmodel");
        CmodelExport.Export(cmc, path, created: "2026-10-20T00:00:00Z");
        Assert.Equal("Qg, Qd: Linear from 1 bias point(s)", CircuitRF.Core.Devices.Composed.CmodelFile.Read(path).Provenance.Charges);
        Assert.Contains(ModelCommands.Explain(cmc), l => l.Text == "Qg, Qd: Linear from 1 bias point(s).");

        string bench = $"""
            Port:P1 in 0 Num=1 Z=50 Ohm
            C:CB1 in g C=1e-6
            L:LB1 vg g L=1e-3
            Vdc:VG vg 0 V={vgs[0]:R}
            Port:P2 out 0 Num=2 Z=50 Ohm
            C:CB2 out d C=1e-6
            L:LB2 vd d L=1e-3
            Vdc:VD vd 0 V={vds[0]:R}
            ComposedFet:X1 g d 0 File="{path}" N=4

            """;
        var (lib, tb) = new CnlReader().Read(bench);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), ExportFixture.Freqs)["S"];
        var measured = SyntheticFetData.BiasedS(SyntheticFetData.NetworkDevice, vgs, vds, ExportFixture.Freqs);
        double worst = 0;
        for (int k = 0; k < ExportFixture.Freqs.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    worst = Math.Max(worst, ((Complex)s[k, i, j] - measured.SAt(0, k, i, j)).Magnitude);
        output.WriteLine($"worst |ΔS| {worst:G3}");
        Assert.True(worst <= 0.02, $"worst |ΔS| {worst:G3}");
    }
}
