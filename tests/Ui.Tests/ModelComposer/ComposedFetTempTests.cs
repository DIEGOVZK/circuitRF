using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// A ComposedFET's <c>Temp</c>: blank follows the design's <c>temp</c>; set, it holds that one device at its own temperature
/// while the rest of the design stays at <c>temp</c>. Run on the shipped GaN model, which covers 25 … 125 °C.
/// </summary>
public sealed class ComposedFetTempTests
{
    private static readonly string Model =
        Path.Combine(AppContext.BaseDirectory, "examples", "Model Composer", "synthetic-gan", "gan_4x125.cmodel");

    private static IReadOnlyList<ComposedCoreModel> Cores(string temps)
    {
        string cnl = "temp = 40\n" +
                     $"ComposedFET:X1 g1 d1 0 File=\"{Model}\" {temps.Split('|')[0]}\n" +
                     $"ComposedFET:X2 g2 d2 0 File=\"{Model}\" {temps.Split('|')[1]}\n" +
                     "Vdc:VG1 g1 0 V=-2\nVdc:VD1 d1 0 V=5\nVdc:VG2 g2 0 V=-2\nVdc:VD2 d2 0 V=5\nanalysis DC1 type=dc\n";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return [.. nl.Components.OrderBy(c => c.InstancePath, StringComparer.Ordinal).Select(c => c.Model).OfType<ComposedCoreModel>()];
    }

    [Fact]
    public void BlankTemp_FollowsTheDesignsTemp_AndASetTemp_HoldsOnlyItsDevice()
    {
        var cores = Cores("|Temp=85");
        Assert.Equal(40, cores[0].TemperatureC);
        Assert.Equal(85, cores[1].TemperatureC);
    }

    [Fact]
    public void ATempOutsideTheModelsRange_IsHeldToIt()
    {
        var cores = Cores("Temp=200|");
        Assert.Equal(125, cores[0].TemperatureC);
        Assert.Equal(200, cores[0].RequestedC);
        Assert.Equal(40, cores[1].TemperatureC);
    }
}
