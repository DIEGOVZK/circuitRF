using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;
using static CircuitRF.Engine.Tests.Composed.ComposedFetEquivalenceTests;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>brief-modelcomposer-1 R-mc1-8 (D8): one warning per run, naming what was used.</summary>
public sealed class ComposedFetTemperatureTests
{
    [Fact]
    public void ASingleTemperatureModel_RunElsewhere_WarnsOnce()
    {
        string path = ComposedTestModels.WriteTemp(ComposedTestModels.Model(ModelTemperature.Single(25)));
        var nl = Elaborate("temp = 85\n" + Bench(-0.6, 6) + Two(path));

        var w = Assert.Single(nl.Warnings, x => x.Contains("one-temperature"));
        Assert.Contains("25 °C", w);
        Assert.Contains("85 °C", w);

        // At its own temperature it says nothing.
        Assert.DoesNotContain(Elaborate("temp = 25\n" + Bench(-0.6, 6) + Two(path)).Warnings,
                              x => x.Contains("one-temperature"));
    }

    [Fact]
    public void ARangedModel_ClampsAndWarnsOnce()
    {
        // Id reads T, so the clamp is visible in the current as well as in the warning.
        var model = ComposedTestModels.Model(ModelTemperature.Range(0, 100));
        model = model with
        {
            Core = model.Core with
            {
                Id = Constituent.Equation(ComposedTestModels.IdEq + "*(1-0.002*(T-25))", ["vgs", "vds", "T"],
                                          ComposedTestModels.IdParams),
            },
        };
        string path = ComposedTestModels.WriteTemp(model);

        var nl = Elaborate("temp = 150\n" + Bench(-0.6, 6) + Two(path));
        var w = Assert.Single(nl.Warnings, x => x.Contains("covers"));
        Assert.Contains("150 °C", w);
        Assert.Contains("evaluated at 100 °C", w);

        var cores = nl.Components.Select(c => c.Model).OfType<ComposedCoreModel>().ToList();
        Assert.Equal(2, cores.Count);
        Assert.All(cores, c => Assert.Equal(100.0, c.TemperatureC));
    }

    private static string Two(string path)
        => $"ComposedFet:X1 g d 0 File=\"{path}\"\nComposedFet:X2 g d 0 File=\"{path}\" N=2\n";
}
