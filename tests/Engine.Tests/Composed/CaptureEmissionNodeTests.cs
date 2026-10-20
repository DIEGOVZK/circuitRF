using System.Numerics;
using CircuitRF.Core.Devices.Composed.Traps;
using RfCore.Data;
using CircuitRF.Core.Tests.Composed;
using Xunit.Abstractions;
using static CircuitRF.Engine.Tests.Composed.TrapTestModels;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// brief-modelcomposer-10 R-mc10-2: the built-in <c>CaptureEmission</c> drain-lag state as a node of the core. At DC it is
/// exactly <c>vds</c>; under CW HB with <c>τ_capt ≪ 1/f ≪ τ_emit</c> it settles near <c>max vds(t)</c> ([P1] eq. 6.75) —
/// short of it by the gap at which a cycle's capture balances a cycle's emission; and far above <c>1/τ_emit</c> the
/// device's S-parameters are those of the frozen-state model.
/// </summary>
public sealed class CaptureEmissionNodeTests(ITestOutputHelper output)
{
    private static readonly string ModelPath = ComposedTestModels.WriteTemp(DrainLag());

    private static string Fet(string path) => $"ComposedFET:X1 g d 0 File=\"{path}\"\n";

    /// <summary>To 1e-12 of itself: the DC engine's gmin (1e-12 S) against the node's 1 S emission conductance
    /// (<c>C/τ_emit</c>) is the only thing between them.</summary>
    [Fact]
    public void Dc_TheStateIsTheIntrinsicDrainVoltage()
    {
        foreach (var (vg, vd) in new[] { (-0.6, 6.0), (0.0, 2.0), (-2.5, 28.0) })
        {
            var nl = ComposedFetEquivalenceTests.Elaborate(ComposedFetEquivalenceTests.Bench(vg, vd) + Fet(ModelPath));
            var v = Dc(ComposedFetEquivalenceTests.Bench(vg, vd) + Fet(ModelPath));
            double vds = v["X1.di"] - v["X1.si"], phi2 = v[nl.Nodes.NameOf(StateNode(nl))!];
            output.WriteLine($"({vg}, {vd}): vds {vds:R}, phi2 {phi2:R}");
            Assert.True(Math.Abs(phi2 - vds) <= 1e-12 * Math.Max(1, Math.Abs(vds)), $"phi2 {phi2:R} vs vds {vds:R}");
        }
    }

    [Fact]
    public void Hb_WellBelowTheEmissionCorner_TheStateSitsNearTheDrainPeak()
    {
        // 1 MHz: τ_capt = 10 ns ≪ 1 µs ≪ τ_emit = 1 ms. The drain swings ±4 V about 6 V.
        var h = Hb(DrainDrive(-0.6, 6, 4, 1e6, 48) + Fet(ModelPath));
        const int samples = 4096;
        var vds = Enumerable.Range(0, samples).Select(t => At(h["X1.di"], 2 * Math.PI * t / samples) - At(h["X1.si"], 2 * Math.PI * t / samples)).ToArray();
        double peak = vds.Max(), phi2 = h["phi2"][0].Real;
        double ripple = h["phi2"].Skip(1).Sum(c => c.Magnitude);

        // The prediction: the constant state whose cycle-average rate is zero over this vds(t) — eq. 6.75 with the balance
        // gap kept, by bisection on the model's own dynamics.
        var dynamics = TrapModels.Build(DrainLag().Traps!, "test");
        double Average(double phi)
        {
            Span<double> gi = stackalloc double[4], gq = stackalloc double[4];
            double sum = 0;
            foreach (double v in vds) sum += dynamics.Evaluate(0, phi, 0, v, 25, gi, out _, gq);
            return sum / vds.Length;
        }
        double lo = vds.Min(), hi = peak;
        for (int it = 0; it < 100; it++) { double mid = 0.5 * (lo + hi); if (Average(mid) < 0) lo = mid; else hi = mid; }
        double predicted = 0.5 * (lo + hi);

        output.WriteLine($"max vds {peak:F4} V, phi2 DC {phi2:F4} V (gap {peak - phi2:F4} V), predicted {predicted:F4} V, ripple {ripple:E2} V");
        Assert.True(peak - phi2 is > 0 and < 1.0, $"gap {peak - phi2:F4} V: a few tenths of a volt below the peak");
        Assert.True(Math.Abs(phi2 - predicted) < 0.02, $"phi2 {phi2:F4} V vs the cycle balance {predicted:F4} V");
        // Held: it moves by about one cycle's emission, (phi2 − mean vds)·T/τ_emit — 3 mV here.
        Assert.True(ripple < 1e-2, $"the state is held: ripple {ripple:E2} V");
    }

    [Fact]
    public void SParameters_FarAboveTheEmissionCorner_AreTheFrozenStateModel()
    {
        double[] f = [0.5e9, 5e9, 20e9];
        string bench = ComposedFetEquivalenceTests.Bench(-0.6, 6);
        // The DC engine stops at ‖F‖ < 1e-6 A by default; two devices that reach one bias by different Newton paths then
        // sit ~1e-6 V apart, which moves S by ~1e-6. Both are solved to 1e-14 A here.
        var tight = new AnalysisSettings { NonlinearAbsTol = 1e-14 };
        var nl = ComposedFetEquivalenceTests.Elaborate(bench + Fet(ModelPath));
        var bias = NonlinearDcEngine.Run(nl, tight);
        double vds = bias.NodeVoltages[nl.Nodes.IndexOf("X1.di") - 1] - bias.NodeVoltages[nl.Nodes.IndexOf("X1.si") - 1];
        DataCube Sp(string path) => SParameterEngine.Run(ComposedFetEquivalenceTests.Elaborate(bench + Fet(path)), f, tight)["S"];
        var trapped = Sp(ModelPath);
        var frozen = Sp(ComposedTestModels.WriteTemp(Frozen(vds)));
        var dcState = Sp(ComposedTestModels.WriteTemp(ComposedTestModels.Model()));

        double worst = 0, apart = 0;
        for (int k = 0; k < f.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    Complex x = (Complex)trapped[k, i, j], y = (Complex)frozen[k, i, j];
                    worst = Math.Max(worst, (x - y).Magnitude);
                    apart = Math.Max(apart, ((Complex)dcState[k, i, j] - y).Magnitude);
                }
        output.WriteLine($"max |ΔS| vs the frozen-state model {worst:E3}; the state-following device is {apart:E3} away");
        Assert.True(worst < 1e-6, $"max |ΔS| {worst:E3}");
        Assert.True(apart > 1e-3, "not vacuous: a state that followed vds would change the output conductance");
    }
}
