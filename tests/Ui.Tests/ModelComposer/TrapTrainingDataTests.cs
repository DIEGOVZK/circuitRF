using CircuitRF.Design.Modeling;
using Xunit;
using static CircuitRF.Ui.Tests.ModelComposer.SyntheticTrapData;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-10 R-mc10-4: pulsed points mapped to their trap states. Toward points take the trap-setting bias
/// under every TrapState — the pre-pulse level when one is given, the quiescent point otherwise; beyond points are left out
/// and counted under <c>auto</c>, and mapped to the setting under <c>setting</c> and to the pulse's extreme under
/// <c>extreme</c>. One trap level warns once; none refuses the state, naming what it needs. A conducting pre-pulse that
/// states no current, and a pulse too long against τ_emit, are caught.
/// </summary>
public sealed class TrapTrainingDataTests
{
    private const string Drain = "phi2";

    private static TrapMapping Map(params (CmcDataSet, NormalisedData)[] sets)
    {
        var (doc, data, intrinsic) = Document(DrainLag(), sets);
        return TrapFit.Map(doc, data, intrinsic, DrainLag(), doc.DataSets, "pulsed I–V");
    }

    /// <summary>The mapped drain-lag state of every point of <paramref name="set"/>, NaN for a validation-only point.</summary>
    private static double[] States(TrapMapping m, string set) => [.. m.Phi[set].Select(r => r?[0] ?? double.NaN)];

    [Fact]
    public void TowardPoints_TakeTheTrapSettingBias_UnderEveryTrapState()
    {
        foreach (var mode in new[] { TrapStateMode.Auto, TrapStateMode.Setting, TrapStateMode.Extreme })
        {
            var (set, data) = Pulsed("q5", 5, mode);
            var m = Map(Dc(), (set, data));
            var phi = States(m, "q5");
            for (int k = 0; k < data.Count; k++)
                if (data.Points[k].Vds <= 5) Assert.Equal(5, phi[k]);

            // A double pulse: the pre-pulse level sets the state, not the quiescent point.
            var (pp, ppData) = Pulsed("pp", 5, mode, new PrePulse(-3, 20, 1e-6, 1e-6, Id: 0));
            var withPre = States(Map(Dc(), (pp, ppData)), "pp");
            Assert.All(withPre, v => Assert.Equal(20, v));
        }
        // DC points sit at their DC state.
        var (dc, dcData) = Dc();
        var atDc = States(Map((dc, dcData), Pulsed("q20", 20)), "dc");
        for (int k = 0; k < dcData.Count; k++) Assert.Equal(dcData.Points[k].Vds, atDc[k]);
    }

    [Fact]
    public void BeyondPoints_AreValidationUnderAuto_TheSettingUnderSetting_AndTheExtremeUnderExtreme()
    {
        var (set, data) = Pulsed("q5", 5);
        int beyond = data.Points.Count(p => p.Vds > 5);
        Assert.True(beyond > 0);

        var auto = Map(Dc(), (set, data));
        var phi = States(auto, "q5");
        Assert.Equal(beyond, phi.Count(double.IsNaN));
        Assert.Equal(beyond, auto.Beyond[Drain]);
        Assert.Contains($"drain-lag: {beyond} points beyond the trap-setting bias, validation only", auto.Report);

        var setting = States(Map(Dc(), Pulsed("q5", 5, TrapStateMode.Setting)), "q5");
        Assert.All(setting, v => Assert.Equal(5, v));

        var extreme = States(Map(Dc(), Pulsed("q5", 5, TrapStateMode.Extreme)), "q5");
        for (int k = 0; k < data.Count; k++) Assert.Equal(Math.Max(5, data.Points[k].Vds), extreme[k]);

        // With both states a point beyond both counts against both, not only the first.
        var both = DrainLag() with { States = ["phi1", "phi2"] };
        var (dbl, dblData) = Pulsed("pp", 5, prePulse: new PrePulse(0, 5, 1e-6, 1e-6, Id: 0.01));
        var (doc, all, intrinsic) = Document(both, Dc(), (dbl, dblData));
        var m = TrapFit.Map(doc, all, intrinsic, both, doc.DataSets, "pulsed I–V");
        Assert.Equal(dblData.Points.Count(p => p.Vgs < 0), m.Beyond["phi1"]);
        Assert.Equal(dblData.Points.Count(p => p.Vds > 5), m.Beyond["phi2"]);
    }

    [Fact]
    public void AParameterTheModelDoesNotDeclare_IsRefused()
    {
        var ex = Assert.Throws<ModelingException>(() =>
            TrapFit.Block(DrainLag() with { Parameters = new Dictionary<string, double> { ["TauEmt2"] = 1e-3 } }));
        Assert.Contains("TauEmt2 is not a parameter of CaptureEmission", ex.Message);
    }

    [Fact]
    public void OneLevelWarnsOnce_AndNoneRefusesTheState()
    {
        var one = Map(Dc(), Pulsed("a", 10), Pulsed("b", 10));
        Assert.Equal([10.0], one.Levels[Drain]);
        Assert.Single(one.Report, l => l.Contains("from one trap level"));
        Assert.Contains("drain-lag from one trap level, 10 V", one.Report);

        var three = Map(Dc(), Pulsed("a", 10), Pulsed("b", 20), Pulsed("c", 28));
        Assert.Equal([10.0, 20, 28], three.Levels[Drain]);
        Assert.DoesNotContain(three.Report, l => l.Contains("from one trap level"));

        // A cold pulse from 0 V: every point lies beyond, so under auto the state has no level — it names the remedy.
        var cold = Assert.Throws<ModelingException>(() => Map(Dc(), Pulsed("cold", 0)));
        Assert.Contains("drain-lag has no trap level", cold.Message);
        Assert.Contains("TrapState", cold.Message);

        // No pulsed data at all.
        var none = Assert.Throws<ModelingException>(() => Map(Dc()));
        Assert.Contains("drain-lag needs pulsed I–V", none.Message);
    }

    [Fact]
    public void AConductingPrePulseWithNoCurrent_IsRefused_AndALongPulseWarns()
    {
        var hot = Assert.Throws<ModelingException>(() => Map(Dc(), Pulsed("pp", 5, prePulse: new PrePulse(0, 28, 1e-6, 1e-6))));
        Assert.Contains("PrePulse bias (0 V, 28 V) conducts and states no drain current", hot.Message);
        // At pinch-off it conducts nothing, so no current is needed.
        Map(Dc(), Pulsed("pp", 5, prePulse: new PrePulse(-3, 28, 1e-6, 1e-6)));

        var (set, data) = Pulsed("slow", 10);
        set = set with { Conditions = set.Conditions with { PulseWidth = 5e-4 } };
        var m = Map(Dc(), (set, data));
        Assert.Contains(m.Report, l => l.StartsWith("slow: PulseWidth 0.0005 s is not ≪ drain-lag τ_emit 0.001 s", StringComparison.Ordinal));
    }
}
