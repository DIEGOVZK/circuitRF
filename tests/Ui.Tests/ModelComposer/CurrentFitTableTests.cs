using CircuitRF.Core;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-7 §4: a table Id — the scattered intrinsic points re-gridded through a network, then MC-2's spline —
/// reproduces the SDD truth at held-out intrinsic points, and the grid-step warning of [P1] Fig. 6.8 fires for a coarse
/// grid and not for a fine one.
/// </summary>
public sealed class CurrentFitTableTests(ITestOutputHelper output)
{
    private static readonly Lazy<(CmcDocument Doc, Dictionary<string, NormalisedData> Data, IntrinsicResult Intrinsic)> Iv =
        new(() => SyntheticFetData.IvDocument(SyntheticFetData.Iv(SyntheticFetData.IvVgs, SyntheticFetData.IvVds)));

    /// <summary>The gate as a diode equation, which fits in milliseconds: these gates are about Id's table.</summary>
    private static readonly CurrentConstituentSettings DiodeGate = new() { Method = CurrentMethod.Equation, Template = CurrentTemplates.Diode };

    [Fact]
    public void TableId_ReproducesTheTruth_AtHeldOutIntrinsicPoints()
    {
        var (doc, data, intrinsic) = Iv.Value;
        var settings = CurrentFitSettings.Default with
        {
            Restarts = 1, Id = new CurrentConstituentSettings { Method = CurrentMethod.Table }, Ig = DiodeGate,
        };
        var fit = CurrentFit.Run(doc, data, intrinsic, settings);
        Assert.Equal(Constituent.TableMethod, fit.Record.Id.Method);
        Assert.Contains("Id.coefficients", fit.Arrays.Keys);

        var model = SyntheticFetData.Core(fit);
        var truth = SyntheticFetData.TruthCore();
        var pairs = CurrentFitAnnTests.HeldOut()
            .Select(p => (M: model.Evaluate(new PortVoltages([p.Vgs, p.Vds])), T: truth.Evaluate(new PortVoltages([p.Vgs, p.Vds])))).ToList();
        double range = pairs.Max(x => Math.Abs(x.T.I[1]));
        double worst = pairs.Max(x => Math.Abs(x.M.I[1] - x.T.I[1])) / range;
        output.WriteLine($"table Id worst held-out error {worst:P3} of {range:E3} A");
        Assert.True(worst < 0.02, $"{worst:P3}");     // measured 1.5 %, the network it was re-gridded from
    }

    [Theory]
    [InlineData(0.2, true)]
    [InlineData(0.1, false)]
    public void GridStepWarning_FiresForACoarseGridOnly(double step, bool warns)
    {
        var (doc, data, intrinsic) = Iv.Value;
        var settings = CurrentFitSettings.Default with
        {
            Id = new CurrentConstituentSettings { Method = CurrentMethod.Table, Regrid = RegridSource.RBF, GridStep = step },
            Ig = DiodeGate,
        };
        var report = CurrentFit.Run(doc, data, intrinsic, settings).Record.Report;
        foreach (string line in report) output.WriteLine(line);
        Assert.Equal(warns, report.Contains("Id: table step 0.2 V: low-level IM below ~0.1 V swing unreliable."));
        Assert.Equal(warns, report.Any(l => l.Contains("table step", StringComparison.Ordinal)));
    }
}
