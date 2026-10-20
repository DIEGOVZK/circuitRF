using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;
using static CircuitRF.Engine.Tests.Composed.ComposedFetEquivalenceTests;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// brief-modelcomposer-21 R-mc21-5: <c>SelfHeating=false</c> evaluates the core at the run's ambient and puts no power into
/// the thermal node — which stays connected, so a bound pin 4 keeps working and sits at no rise.
/// </summary>
public sealed class SelfHeatingSwitchTests
{
    private static ComposedModel Model(bool heats)
    {
        var m = ComposedTestModels.Model(ModelTemperature.Range(0, 200), n0: 4);
        return m with
        {
            Thermal = heats ? ModelThermal.Single(50, 1e-9) : null,
            Core = m.Core with
            {
                Id = Constituent.Equation(ComposedTestModels.IdEq + "*(1-0.002*(T-25))", ["vgs", "vds", "T"], ComposedTestModels.IdParams),
            },
        };
    }

    private static readonly string HotPath = ComposedTestModels.WriteTemp(Model(heats: true));
    private static readonly string IsothermalPath = ComposedTestModels.WriteTemp(Model(heats: false));

    [Fact]
    public void Off_IsTheIsothermalDeviceAtAmbient_AndPinFourSitsAtNoRise()
    {
        string bench = "temp = 25\n" + Bench(0, 6);
        var heated = Dc(bench + $"ComposedFet:X1 g d 0 t File=\"{HotPath}\"\n");
        Assert.True(heated["t"] > 1, $"ΔT = {heated["t"]} with self-heating on");

        var off   = Dc(bench + $"ComposedFet:X1 g d 0 t File=\"{HotPath}\" SelfHeating=false\n");
        var truth = Dc(bench + $"ComposedFet:X1 g d 0 File=\"{IsothermalPath}\"\n");
        Assert.True(Math.Abs(off["t"]) <= 1e-12, $"ΔT = {off["t"]} with self-heating off");
        foreach (string node in (string[])["X1.gi", "X1.di", "X1.n1", "X1.n3"])
            Assert.True(Math.Abs(off[node] - truth[node]) <= 1e-9 * Math.Max(Math.Abs(truth[node]), 1e-3),
                $"{node}: {off[node]:R} vs {truth[node]:R}");
    }
}
