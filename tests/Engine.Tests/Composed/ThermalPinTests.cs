using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;
using static CircuitRF.Engine.Tests.Composed.ComposedFetEquivalenceTests;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// brief-modelcomposer-9 R-mc9-2: the thermal node. Pin 4 of a four-pin instance IS the node whose voltage is ΔT — the
/// compact-model convention for an exposed thermal terminal ([P1] Fig. 6.32's Rth ∥ Cth from Tj to the ambient; [R11]
/// ch. 7's "fourth terminal, loaded by the thermal impedance") — so a network attached there sits in parallel with the
/// model's own Rth. Rth scales by N0/N and Cth by N/N0 (D9).
/// </summary>
public sealed class ThermalPinTests
{
    private const double Rth = 50, Cth = 1e-9;

    private static readonly string ModelPath = ComposedTestModels.WriteTemp(SelfHeating());

    /// <summary>The shared test device over 0–200 °C, its drain current falling 0.2 %/K, with one thermal pole.</summary>
    private static ComposedModel SelfHeating()
    {
        var m = ComposedTestModels.Model(ModelTemperature.Range(0, 200), n0: 4);
        return m with
        {
            Thermal = ModelThermal.Single(Rth, Cth),
            Core = m.Core with
            {
                Id = Constituent.Equation(ComposedTestModels.IdEq + "*(1-0.002*(T-25))", ["vgs", "vds", "T"], ComposedTestModels.IdParams),
            },
        };
    }

    /// <summary>The intrinsic power, from the currents in rg and rd (unscaled at N0) and the intrinsic voltages.</summary>
    private static double Power(Dictionary<string, double> v)
    {
        double ig = (v["X1.n1"] - v["X1.gi"]) / 5.2, id = (v["X1.n3"] - v["X1.di"]) / 9.2;
        return ig * (v["X1.gi"] - v["X1.si"]) + id * (v["X1.di"] - v["X1.si"]);
    }

    [Fact]
    public void PinFour_IsDeltaT_AndAnExternalNetworkSitsInParallelWithRth()
    {
        // Nothing else on t: the model's own Rth closes eq. 6.67, ΔT = Rth·P.
        var alone = Dc(Bench(0, 6) + $"ComposedFET:X1 g d 0 t File=\"{ModelPath}\"\n");
        double p = Power(alone);
        Assert.True(p > 0.1, $"P = {p}");
        Assert.Equal(Rth * p, alone["t"], 1e-6 * Rth * p);

        // A heatsink network on pin 4 — R and C to the thermal ground — joins the model's Rth ∥ Cth: at DC, Rth ∥ Rext.
        const double rExt = 30;
        var sunk = Dc(Bench(0, 6) + $"ComposedFET:X1 g d 0 t File=\"{ModelPath}\"\nR:REXT t 0 R={rExt}\nC:CEXT t 0 C=1e-6\n");
        double ps = Power(sunk);
        Assert.Equal(Rth * rExt / (Rth + rExt) * ps, sunk["t"], 1e-6 * Rth * ps);
        Assert.True(sunk["t"] < alone["t"]);

        // Three nets: the same node, inside the cell.
        var inside = Dc(Bench(0, 6) + $"ComposedFET:X1 g d 0 File=\"{ModelPath}\"\n");
        Assert.Equal(alone["t"], inside["X1.t"], 1e-9 * alone["t"]);
    }

    [Fact]
    public void TwiceTheBaseFingers_HalvesRth_AndDoublesCth()
    {
        var nl = Elaborate(Bench(0, 6) + $"ComposedFET:X1 g d 0 t File=\"{ModelPath}\" N=8\n");
        double Value(string path, string p) => nl.Components.Single(c => c.InstancePath == path).Parameters[p].AsReal();
        Assert.Equal(Rth / 2, Value("X1.Rth", "R"), 1e-12);
        Assert.Equal(2 * Cth, Value("X1.Cth", "C"), 1e-21);
    }

    /// <summary>An instance's Rth and Cth are that device's own, at its finger count: they replace the scaled values, and
    /// the multiplier still applies on top. A model that does not self-heat refuses them.</summary>
    [Fact]
    public void AnInstancesRthAndCth_ReplaceTheScaledValues()
    {
        var nl = Elaborate(Bench(0, 6) + $"ComposedFET:X1 g d 0 File=\"{ModelPath}\" N=8 Rth=40 Cth=3e-9\n");
        double Value(string path, string p) => nl.Components.Single(c => c.InstancePath == path).Parameters[p].AsReal();
        Assert.Equal(40, Value("X1.Rth", "R"), 1e-12);
        Assert.Equal(3e-9, Value("X1.Cth", "C"), 1e-21);

        var cold = ComposedTestModels.WriteTemp(ComposedTestModels.Model(ModelTemperature.Range(0, 200), n0: 4));
        var ex = Assert.ThrowsAny<Exception>(() => Elaborate(Bench(0, 6) + $"ComposedFET:X1 g d 0 File=\"{cold}\" Rth=40\n"));
        Assert.Contains("does not self-heat", ex.Message);
    }
}
