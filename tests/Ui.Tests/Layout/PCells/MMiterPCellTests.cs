using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// MMiter artwork: one polygon whose chamfer and pins are the ELECTRICAL model's geometry — the
/// Douville-James cut (a 2·M·W/sin θ leg, corner to corner at 90°, 50 %) and pins at
/// <see cref="MicrostripMiterBend.PinOffset"/> — so the copper area is the bend's own, and an MLIN
/// abutting a pin never refills the chamfer.
/// </summary>
public class MMiterPCellTests
{
    private const double W = 300e-6;

    private static PCellResult Gen(double angleDeg, double? miter, double w = W)
    {
        Assert.True(PCellRegistry.TryGet("MMITER", out var generate));
        var p = new Dictionary<string, double> { ["W"] = w, ["Angle"] = angleDeg };
        if (miter is { } m) p["M"] = m;
        return generate(PCellParameters.FromReals(p), null, PCellLayerSelection.Default);
    }

    private static double Area(long[] xy)
    {
        double a = 0; int n = xy.Length / 2;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            a += (double)xy[2 * i] * xy[2 * j + 1] - (double)xy[2 * j] * xy[2 * i + 1];
        }
        return Math.Abs(a) / 2;
    }

    private static long Dbu(double metres) => (long)Math.Round(metres * 1e9);

    [Fact]
    public void AtNinetyDegrees_HalfAMiterCutsCornerToCorner_AndThePinsAreTheCornerSquareEdges()
    {
        var res = Gen(90, 0.5);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(res.Shapes));
        // Pins W/2 from the centre-line corner, which is W/2 from pin 1: a W x W square minus its outer half.
        Assert.Equal(0, res.Pins[0].X);
        Assert.Equal(Dbu(W / 2), res.Pins[1].X);
        Assert.Equal(Dbu(W / 2), res.Pins[1].Y);
        Assert.Equal(Dbu(W) * (double)Dbu(W) / 2, Area(poly.Xy), 0);
        Assert.Null(res.Diagnostics);
    }

    [Fact]
    public void ASharpCorner_IsTheFullSquare()
    {
        var poly = Assert.IsType<PolygonShape>(Assert.Single(Gen(90, 0.0).Shapes));
        Assert.Equal(Dbu(W) * (double)Dbu(W), Area(poly.Xy), 0);
    }

    [Theory]
    [InlineData(90.0, 0.736)]
    [InlineData(45.0, 0.2)]
    [InlineData(120.0, 0.6)]
    public void ThePins_SitWhereTheModelsPortsAre(double angleDeg, double miter)
    {
        var res = Gen(angleDeg, miter);
        double r = MicrostripMiterBend.PinOffset(W, angleDeg, miter);
        double th = angleDeg * Math.PI / 180;
        Assert.Equal(Dbu(r + r * Math.Cos(th)), res.Pins[1].X, 1.0);
        Assert.Equal(Dbu(r * Math.Sin(th)), res.Pins[1].Y, 1.0);
        Assert.Equal(angleDeg, res.Pins[1].OutwardDirectionDeg, 9);
    }

    [Fact]
    public void ADeepMiter_MovesThePinsOut_SoTheChamferEndsInsideTheCell()
    {
        // 0.736 at 90 deg: the leg (2MW = 1.47W) runs past the W/2 plane; the pin moves to 0.97W.
        Assert.True(MicrostripMiterBend.PinOffset(W, 90, 0.736) > W / 2);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(Gen(90, 0.736).Shapes));
        // Nothing of the outline lies before pin 1's edge (x < 0).
        for (int i = 0; i < poly.Xy.Length; i += 2) Assert.True(poly.Xy[i] >= 0);
    }

    [Fact]
    public void ARightTurn_IsTheMirrorImage()
    {
        var l = Assert.IsType<PolygonShape>(Assert.Single(Gen(60, 0.3).Shapes));
        var r = Assert.IsType<PolygonShape>(Assert.Single(Gen(-60, 0.3).Shapes));
        Assert.Equal(Area(l.Xy), Area(r.Xy), 0);
        Assert.Equal(-Gen(60, 0.3).Pins[1].Y, Gen(-60, 0.3).Pins[1].Y);
    }

    [Fact]
    public void NoMiter_IsTheAsymptoticOptimum_WithNoTechnology()
    {
        var a = Assert.IsType<PolygonShape>(Assert.Single(Gen(90, null).Shapes));
        var b = Assert.IsType<PolygonShape>(Assert.Single(Gen(90, 0.52).Shapes));
        Assert.Equal(b.Xy, a.Xy);
    }

    [Theory]
    [InlineData(150.0, 0.3)]
    [InlineData(90.0, 0.95)]
    public void GeometryTheModelRefuses_IsDrawnClamped_AndReported(double angleDeg, double miter)
    {
        var res = Gen(angleDeg, miter);
        Assert.Single(res.Shapes);
        Assert.Contains(res.Diagnostics!, d => d.Contains("the simulation refuses"));
    }

    [Fact]
    public void ItIsAMicrostripTransmissionLine_ToLvsAndTheLibrary()
    {
        Assert.True(DeviceTypes.TryGetSymbolKind("MMITER", out var kind));
        Assert.Equal(SymbolKind.MMiter, kind);
        Assert.True(MicrostripSubstrateInjection.IsMicrostripKind(SymbolKind.MMiter));
        Assert.Contains(MMiterPCell.GeneratorId, GroundPourPlanner.MicrostripGenerators);
    }
}
