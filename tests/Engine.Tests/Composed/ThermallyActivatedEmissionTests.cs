using CircuitRF.Core;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Core.Tests.Composed;
using Xunit.Abstractions;
using static CircuitRF.Engine.Tests.Composed.TrapTestModels;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// brief-modelcomposer-10 R-mc10-2: <c>ThermallyActivated</c> emission. With <c>Ea</c> given, the emission rate follows
/// [N22] eq. 3, <c>1/τ_emit ∝ T²·e^(−Ea/kT)</c> with T in kelvin, through <c>τ_emit</c> at <c>Tref</c>; and the trap node's
/// recovery time in a circuit — its charge over its conductance at the DC operating point — changes between two ambients
/// by the factor that predicts.
/// </summary>
public sealed class ThermallyActivatedEmissionTests(ITestOutputHelper output)
{
    private const double Ea = 0.5, TauEmit = 1e-3, Tref = 25;

    private static ComposedModel Model() =>
        DrainLag(ModelTemperature.Range(0, 200), ("Ea2", Ea), ("TauEmit2", TauEmit), ("Tref", Tref));

    private static double Kelvin(double c) => c + 273.15;

    /// <summary>[N22] eq. 3 between <paramref name="t1"/> and <paramref name="t2"/> °C: the ratio of emission rates.</summary>
    private static double Arrhenius(double t1, double t2) =>
        Math.Pow(Kelvin(t2) / Kelvin(t1), 2) * Math.Exp(-Ea / CaptureEmissionTraps.BoltzmannEv * (1 / Kelvin(t2) - 1 / Kelvin(t1)));

    [Fact]
    public void TheEmissionRate_FollowsArrhenius()
    {
        var d = (RateTrapDynamics)TrapModels.Build(Model().Traps!, "test");
        var g = new double[4];
        // A state 3 V above vds: capture is e^(−80)-negligible, so ∂rate/∂phi is minus the emission rate.
        double Rate(double t) { d.Rate(0, 9, -1, 6, t, g); return -g[0]; }
        Assert.Equal(1 / TauEmit, Rate(Tref), 1e-9 / TauEmit);
        foreach (double t in (double[])[75, 125])
        {
            double ratio = Rate(t) / Rate(Tref), want = Arrhenius(Tref, t);
            output.WriteLine($"{t} °C: rate ×{ratio:R} (Arrhenius ×{want:R})");
            Assert.True(Math.Abs(ratio / want - 1) < 1e-12, $"{t} °C: ×{ratio} vs ×{want}");
        }
    }

    [Fact]
    public void TheTrapNodesRecoveryTime_ChangesByThePredictedFactor()
    {
        string path = ComposedTestModels.WriteTemp(Model());
        double Recovery(double ambient)
        {
            var nl = ComposedFetEquivalenceTests.Elaborate($"temp = {ambient}\n" + ComposedFetEquivalenceTests.Bench(-0.6, 6) +
                                                           $"ComposedFET:X1 g d 0 File=\"{path}\"\n");
            var dc = NonlinearDcEngine.Run(nl);
            Assert.True(dc.Converged);
            var core = nl.Components.Single(c => c.Model is ComposedCoreModel);
            double V(int node) => node == 0 ? 0 : dc.NodeVoltages[node - 1];
            var v = new PortVoltages([.. Enumerable.Range(0, core.Model.PortCount).Select(p => V(core.Nodes[2 * p]) - V(core.Nodes[2 * p + 1]))]);
            var r = core.Model.Evaluate(v);
            const int sp = 2;
            return r.Dc[sp, sp] / r.Dg[sp, sp];   // τ = C/G of the node
        }

        double t25 = Recovery(25), t125 = Recovery(125);
        // The node's conductance is C·(1/τ_emit(T) + f′(0)); f′(0) = (x_ref/τ_capt)/Vc·e^(−x_ref/Vc) does not move with T.
        double f0 = 1 / CaptureEmissionTraps.DefaultTauCapt / CaptureEmissionTraps.DefaultVc
                  * Math.Exp(-CaptureEmissionTraps.DefaultXref / CaptureEmissionTraps.DefaultVc);
        double Predicted(double t) => 1 / (Arrhenius(Tref, t) / TauEmit + f0);
        output.WriteLine($"recovery {t25:E6} s at 25 °C, {t125:E6} s at 125 °C: ×{t25 / t125:F3} (predicted ×{Predicted(25) / Predicted(125):F3})");
        Assert.True(Math.Abs(t25 / Predicted(25) - 1) < 1e-9, $"25 °C: {t25:E6} vs {Predicted(25):E6}");
        Assert.True(Math.Abs(t125 / Predicted(125) - 1) < 1e-9, $"125 °C: {t125:E6} vs {Predicted(125):E6}");
        Assert.True(t25 / t125 > 100, "not vacuous: 0.5 eV over 100 K is two orders of magnitude");
    }
}
