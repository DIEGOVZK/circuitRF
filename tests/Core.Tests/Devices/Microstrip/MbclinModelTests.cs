using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Coupled;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Expressions;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// MBCLIN, the broadside-coupled microstrip pair: an inhomogeneous medium, so each mode has its own C
/// and C₀ and its own speed. Held to the single microstrip line when uncoupled, and to BCLIN when a top
/// ground closes the dielectric.
/// </summary>
public class MbclinModelTests(ITestOutputHelper output)
{
    private const double C0 = MicrostripLoss.SpeedOfLight, Um = 1e-6;

    /// <summary>
    /// O → ∞: strip A, on the surface, is a microstrip of height Hd = Hb + Tb + Hs. Its own C and C₀ must
    /// give Hammerstad-Jensen's Z and εeff (thin strips, where that closed form is accurate to 0.2 %).
    /// </summary>
    [Fact]
    public void FarOffset_StripAIsTheSingleMicrostripLine()
    {
        double w = 600 * Um, hb = 300 * Um, hs = 335 * Um, er = 4.1;
        var m = new MicrostripBroadsideCoupledModel(w, 30e-3, 1e-3, hb, hs, 0, 0, double.NaN, er, 5.8e7, 0, "MBCLIN:X1");
        double za = 1 / (C0 * Math.Sqrt(m.Modes.C[0, 0] * m.Modes.C0[0, 0])), ea = m.Modes.C[0, 0] / m.Modes.C0[0, 0];
        var (zh, eh) = HammerstadJensen.Compute(w, hb + hs, 0, er, new MicrostripValidityReporter("t"));
        output.WriteLine($"strip A: {za:F2} ohm, εeff {ea:F4}; Hammerstad-Jensen {zh:F2} ohm, {eh:F4}");
        Assert.True(Math.Abs(m.Modes.C[0, 1]) < 2e-3 * m.Modes.C[0, 0], "uncoupled");
        Assert.InRange(za / zh, 0.98, 1.01);
        Assert.InRange(ea / eh, 0.99, 1.01);
    }

    /// <summary>With a top ground at the dielectric's surface, MBCLIN's general two-solve path is BCLIN.</summary>
    [Fact]
    public void CoveredAtTheDielectricSurface_ReproducesBclin()
    {
        double w = 400 * Um, hs = 76 * Um, ha = 73 * Um, hb = 77 * Um, ta = 25 * Um, tb = 18 * Um, er = 3.0;
        var b = new StriplineBroadsideCoupledModel(w, 50 * Um, 1e-3, hs, ha, hb, ta, tb, er, 5.8e7, 0, "BCLIN:X1");
        var m = new MicrostripBroadsideCoupledModel(w, 50 * Um, 1e-3, hb, hs, ta, tb, hb + tb + hs + ta + ha, er, 5.8e7, 0,
                                                    "MBCLIN:X1", haCover: ha);
        Assert.Equal(b.Modes.Ze, m.Modes.Ze, 9);
        Assert.Equal(b.Modes.Zo, m.Modes.Zo, 9);
        Assert.Equal(er, m.Modes.EeffE, 9);
        Assert.Equal(er, m.Modes.EeffO, 9);
        Assert.Equal(b.Modes.LossFactorE, m.Modes.LossFactorE, 6);
        Assert.Equal(b.Modes.LossFactorO, m.Modes.LossFactorO, 6);
    }

    [Fact]
    public void OpenPair_HasTwoModalSpeeds_AndIsPassive()
    {
        var m = new MicrostripBroadsideCoupledModel(400 * Um, 0, 15e-3, 200 * Um, 100 * Um, 18 * Um, 18 * Um, double.NaN, 4.4,
                                                    5.8e7, 0.02, "MBCLIN:X1");
        output.WriteLine($"Ze {m.Modes.Ze:F2} Zo {m.Modes.Zo:F2} εe {m.Modes.EeffE:F3} εo {m.Modes.EeffO:F3} asym {m.Modes.Asymmetry:P1}");
        Assert.True(m.Modes.Ze > m.Modes.Zo);
        Assert.True(m.Modes.EeffE > 1 && m.Modes.EeffE < 4.4 && m.Modes.EeffO > 1 && m.Modes.EeffO < 4.4);
        // Broadside: the odd mode's field is mostly between the strips, inside the dielectric.
        Assert.True(m.Modes.EeffO > m.Modes.EeffE);
        foreach (double f in new[] { 1e9, 4e9 })
        {
            var s = CoupledPairTestKit.S(m, f);
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    Assert.True((s[i, j] - s[j, i]).Magnitude < 1e-12);
            Assert.True(CoupledPairTestKit.MaxSingularSquared(s) < 1.0);
        }
        CoupledPairTestKit.Y(m, 1e9);
        Assert.Contains(m.DrainWarnings(), x => x.Key == "MBCLIN:asymmetric");
    }

    [Theory]
    [InlineData(100e-6, "mbclin.hd.below-strip-a")]
    public void DielectricBelowStripA_IsRefused(double hd, string code)
        => Assert.Equal(code, MicrostripBroadsideCoupledModel.Refusal(400e-6, 0, 1e-3, 200e-6, 100e-6, 18e-6, 18e-6, hd, 4.4)!.Id);

    [Fact]
    public void Factory_BuildsMbclin_WithStripAOnTheSurfaceByDefault()
    {
        var p = new Dictionary<string, Value>
        {
            ["W"] = new(400e-6), ["O"] = new(100e-6), ["L"] = new(10e-3), ["Hb"] = new(200e-6), ["Hs"] = new(100e-6),
            ["T"] = new(18e-6), ["Er"] = new(4.4),
        };
        var m = Assert.IsType<MicrostripBroadsideCoupledModel>(ComponentModelFactory.TryCreate("MBCLIN", p));
        Assert.Equal(318e-6, m.Geometry.Hd, 12);
        Assert.Null(m.Geometry.Ha);
        Assert.True(ComponentModelFactory.IsPrimitive("MBCLIN"));
    }
}
