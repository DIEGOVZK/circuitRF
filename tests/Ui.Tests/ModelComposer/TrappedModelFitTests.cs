using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;
using static CircuitRF.Ui.Tests.ModelComposer.SyntheticTrapData;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-10 R-mc10-5: the drain current re-trained with the drain-lag state as an input, on the closed-form
/// drain-lag truth. From pulsed I–V at three quiescent drain voltages, the DC I–V held out as validation and the CW S
/// trained on at the DC trap state (MC-6's combined mode), the model reproduces each level's pulsed I–V, the DC I–V it never
/// saw, and the RF output conductance — the frozen-state Gds that differs from the DC slope. One level fits and warns once;
/// no pulsed data leaves the step unavailable and the model without traps; dispersion alongside traps is refused.
/// </summary>
public sealed class TrappedModelFitTests(ITestOutputHelper output)
{
    private static CurrentFitSettings Fit => new()
    {
        Id = new CurrentConstituentSettings { MaxIterations = 3000 },
        Ig = SyntheticFetData.ThermalIgEquation,
        Restarts = 1,
    };

    /// <summary>The fitted Id as the core evaluates it: its value and its vds-slope at (vgs, vds, phi2).</summary>
    private static Func<double, double, double, (double Id, double Gds)> Evaluator(CurrentFitResult fit)
    {
        var truth = ComposedTestModels.Model();
        var model = truth with
        {
            Traps = fit.Record.Traps,
            Network = new ExtrinsicNetwork(ChargeFit.IntrinsicNetwork, []),
            Core = truth.Core with { Id = fit.Record.Id, Ig = fit.Record.Ig },
            Extrapolation = fit.Record.Gx.Count > 0 ? new ModelExtrapolation(fit.Record.Gx) : null,
            Arrays = fit.Arrays,
        };
        CmodelFile.Validate(model);
        var program = ComposedCoreProgram.Compile(model, "test");
        return (vgs, vds, phi2) =>
        {
            var g = new double[3];
            double id = program.Channel([vgs, vds, phi2], g);
            return (id, g[1]);
        };
    }

    /// <summary>RMS of model − truth over the points, relative to the truth's range.</summary>
    private static double Relative(IEnumerable<(double Model, double Truth)> pairs)
    {
        var p = pairs.ToList();
        double rms = Math.Sqrt(p.Average(x => Math.Pow(x.Model - x.Truth, 2)));
        return rms / (p.Max(x => x.Truth) - p.Min(x => x.Truth));
    }

    private double PulsedError(Func<double, double, double, (double Id, double Gds)> f, double vq)
    {
        double e = Relative(from g in Vgs from d in Vds where d <= vq select (f(g, d, vq).Id, Channel(g, d, vq)));
        output.WriteLine($"pulsed from {vq} V: {100 * e:F3} %");
        return e;
    }

    [Fact]
    public void FromThreeQuiescentLevels_TheModelReproducesThePulsedIv_TheHeldOutDcIv_AndTheRfGds()
    {
        var (doc, data, intrinsic) = Document(DrainLag(DcIvUse.Validation), Dc(), Pulsed("q5", 5), Pulsed("q10", 10), Pulsed("q20", 20), S());
        var traps = TrapFit.Run(doc, data, intrinsic, TrapFit.Settings(doc));
        Assert.Equal([5.0, 10, 20], traps.Record.Levels["phi2"]);
        doc = TrapFit.Apply(doc, traps);
        var fit = CurrentFit.Run(doc, data, intrinsic, Fit);
        foreach (string line in fit.Record.Report) output.WriteLine(line);
        Assert.Equal(["vgs", "vds", "phi2"], fit.Record.Id.Inputs);
        Assert.Equal(["vgs", "vds"], fit.Record.Ig.Inputs);
        Assert.Equal([5.0, 10, 20], fit.Record.Traps!.LevelsOf("phi2"));
        var f = Evaluator(fit);

        foreach (double vq in (double[])[5, 10, 20]) Assert.True(PulsedError(f, vq) < 0.01, $"pulsed from {vq} V");

        double dc = Relative(from g in Vgs from d in Vds select (f(g, d, d).Id, Channel(g, d, d)));
        output.WriteLine($"DC I–V (validation): {100 * dc:F3} %; recorded {100 * fit.Record.DcIv!.Relative:F3} %");
        Assert.True(dc < 0.01, "DC I–V");
        Assert.Equal(dc, fit.Record.DcIv.Relative, 1e-9);

        // RF: the frozen-state Gds at each DC point, against the truth's — which differs from the DC slope by gm·At/Ct.
        var gds = (from g in Vgs from d in Vds where d >= 2 select (f(g, d, d).Gds, Frozen(g, d).Gds)).ToList();
        double rms = Math.Sqrt(gds.Average(x => Math.Pow(x.Item1 - x.Item2, 2))), size = Math.Sqrt(gds.Average(x => x.Item2 * x.Item2));
        double dcSlope = Math.Sqrt((from g in Vgs from d in Vds where d >= 2
                                    let s = (Channel(g, d + 1e-5, d + 1e-5) - Channel(g, d - 1e-5, d - 1e-5)) / 2e-5
                                    select Math.Pow(s - Frozen(g, d).Gds, 2)).Average());
        output.WriteLine($"RF Gds: RMS error {100 * rms / size:F2} % of its RMS; the DC slope is {100 * dcSlope / size:F1} % away");
        Assert.True(rms < 0.05 * size, "RF Gds");
        Assert.True(dcSlope > 0.2 * size, "not vacuous: the DC and RF output conductances differ");
    }

    [Fact]
    public void FromOneQuiescentLevel_ItFitsWarnsOnce_AndReproducesThatLevel()
    {
        var (doc, data, intrinsic) = Document(DrainLag(), Dc(), Pulsed("q10", 10));
        var fit = CurrentFit.Run(doc, data, intrinsic, Fit);
        Assert.Single(fit.Record.Report, l => l.Contains("from one trap level"));
        Assert.Equal([10.0], fit.Record.Traps!.LevelsOf("phi2"));
        Assert.True(PulsedError(Evaluator(fit), 10) < 0.01, "pulsed from 10 V");
    }

    [Fact]
    public void WithNoPulsedData_TheStepIsUnavailable_AndTheModelHasNoTraps()
    {
        var (doc, data, intrinsic) = Document(null, Dc());
        Assert.Equal(TrapFit.NoPulsedData, TrapFit.Unavailable(doc));
        var refused = Assert.Throws<ModelingException>(() => TrapFit.Run(doc, data, intrinsic, DrainLag()));
        Assert.Contains(TrapFit.NoPulsedData, refused.Message);

        var fit = CurrentFit.Run(doc, data, intrinsic, Fit with { Id = SyntheticFetData.ThermalIdEquation with { Expression = ComposedTestModels.IdEq,
                                                                   Parameters = SyntheticFetData.ThermalIdEquation.Parameters.Where(p => p.Key != "kT").ToDictionary() } });
        Assert.Null(fit.Record.Traps);
        var model = ComposedTestModels.Model() with { Core = ComposedTestModels.Model().Core with { Id = fit.Record.Id, Ig = fit.Record.Ig } };
        Assert.DoesNotContain("\"Traps\"", System.Text.Encoding.UTF8.GetString(CmodelFile.ModelJson(model)));
    }

    [Fact]
    public void AnEquationTemplate_GainsTheThresholdAndKneeTrapTerms()
    {
        var (doc, data, intrinsic) = Document(DrainLag(), Dc(), Pulsed("q10", 10));
        var fit = CurrentFit.Run(doc, data, intrinsic, Fit with
        {
            Id = new CurrentConstituentSettings { Method = CurrentMethod.Equation, Template = "Angelov", Algorithm = "lm" },
        });
        string id = fit.Record.Id.Expression("Expression")!;
        Assert.Contains("-a2*tanh((phi2-vds)/c2)", id);
        Assert.Contains("(Alpha*(1+ak*tanh((phi2-vds)/ck)))", id);
        Evaluator(fit);   // the fitted equation validates and compiles with phi2 an input
    }

    [Fact]
    public void DispersionAlongsideTraps_IsRefused()
    {
        var (doc, data, intrinsic) = Document(DrainLag(), Dc(), Pulsed("q10", 10), S());
        var ex = Assert.Throws<ModelingException>(() => CurrentFit.Run(doc, data, intrinsic, Fit with { Dispersion = new DispersionFitSettings() }));
        Assert.Contains("dispersion and traps are not combined", ex.Message);
    }
}
