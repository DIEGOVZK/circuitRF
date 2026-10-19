using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// MMITER: a chamfered bend is MLIN over its own corner centre line, cut in half around a lumped excess
/// from a planar-MoM table. These tests hold the geometry conventions, the network's physics (reciprocal,
/// symmetric, passive, the right limits), the table plumbing, and the refusals. The table's ACCURACY
/// is held by <see cref="MicrostripMiterBendTableTests"/> against EM points that are not on its grid.
/// </summary>
public class MicrostripMiterBendModelTests
{
    private const double W = 0.89e-3, H = 0.367e-3, T = 25e-6, Er = 3.0, Sigma = 5.8e7, TanD = 0.0015;

    private static MicrostripMiterBendModel Make(double angleDeg = 90, double miter = 0.5, double w = W,
        double tanD = TanD, double sigma = Sigma)
        => new(w, angleDeg, miter, H, T, Er, sigma, tanD, "MMITER:X1");

    private static (Complex S11, Complex S21, Complex S12, Complex S22) S(MicrostripAbcd t, double z0 = 50.0)
    {
        Complex den = t.A + t.B / z0 + t.C * z0 + t.D;
        return ((t.A + t.B / z0 - t.C * z0 - t.D) / den, 2.0 / den,
                2.0 * (t.A * t.D - t.B * t.C) / den, (-t.A + t.B / z0 - t.C * z0 + t.D) / den);
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AtNinetyDegrees_ThePlanesAreTheCornerSquareEdges_AndFiftyPercentCutsCornerToCorner()
    {
        Assert.Equal(W, MicrostripMiterBend.CornerLength(W, 90), 15);
        Assert.Equal(W / 2, MicrostripMiterBend.PlaneOffset(W, 90), 15);
        // Douville-James: M = x/d with d the W*sqrt(2) diagonal, so 50 % removes the whole outer half
        // of the corner square — a W leg on each outer edge.
        Assert.Equal(W, MicrostripMiterBend.ChamferLeg(W, 90, 0.5), 15);
        Assert.Equal(0.52 + 0.65 * Math.Exp(-1.35 * 2.0), MicrostripMiterBend.DouvilleJamesOptimum(2 * H, H), 15);
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(45.0)]
    [InlineData(120.0)]
    public void TheCornerLength_IsWTanHalfTheAngle(double angleDeg)
        => Assert.Equal(W * Math.Tan(angleDeg * Math.PI / 360), MicrostripMiterBend.CornerLength(W, angleDeg), 15);

    // ── table plumbing ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheInterpolant_ReproducesEveryTableNode()
    {
        var t = MicrostripMiterBendTable.Instance;
        for (int iu = 0; iu < t.LnU.Length; iu += 3)
            for (int ie = 0; ie < t.EpsR.Length; ie++)
                for (int it = 0; it < t.Theta.Length; it += 2)
                    for (int im = 0; im < t.Miter.Length; im += 4)
                    {
                        var (l, k) = MicrostripMiterBend.Interpolate(t.LnU[iu], t.EpsR[ie], t.Miter[im], t.Theta[it]);
                        Assert.Equal(t.Lambda[iu, ie, it, im], l, 10);
                        Assert.Equal(t.Kappa[iu, ie, it, im], k, 10);
                    }
    }

    [Fact]
    public void AZeroAngleNode_IsExactlyNoCorner()
    {
        var t = MicrostripMiterBendTable.Instance;
        for (int iu = 0; iu < t.LnU.Length; iu++)
            for (int ie = 0; ie < t.EpsR.Length; ie++)
                for (int im = 0; im < t.Miter.Length; im++)
                {
                    Assert.Equal(0.0, t.Lambda[iu, ie, 0, im]);
                    Assert.Equal(0.0, t.Kappa[iu, ie, 0, im]);
                }
    }

    [Fact]
    public void Pchip_IsMonotoneBetweenMonotoneData_AndLinearOutside()
    {
        double[] x = [0, 1, 2, 3], y = [0, 0.1, 5, 5.2];
        double prev = double.NegativeInfinity;
        for (double q = 0; q <= 3; q += 0.01)
        {
            double v = MicrostripMiterBend.Pchip(x, y, q);
            Assert.True(v >= prev - 1e-12);
            prev = v;
        }
        double s = MicrostripMiterBend.Pchip(x, y, 3.0) - MicrostripMiterBend.Pchip(x, y, 2.999);
        Assert.Equal(MicrostripMiterBend.Pchip(x, y, 3.0) + 1000 * s, MicrostripMiterBend.Pchip(x, y, 4.0), 2);
    }

    // ── the network ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(90.0, 0.0, 1e9)]
    [InlineData(90.0, 0.55, 6e9)]
    [InlineData(45.0, 0.2, 3.5e9)]
    [InlineData(120.0, 0.7, 12e9)]
    public void TheNetwork_IsReciprocalSymmetricAndPassive(double angleDeg, double miter, double f)
    {
        var t = Make(angleDeg, miter).Abcd(f);
        Assert.True((t.A * t.D - t.B * t.C - 1).Magnitude < 1e-12);           // reciprocal
        Assert.True((t.A - t.D).Magnitude < 1e-12);                           // mirror-symmetric
        var (s11, s21, _, _) = S(t);
        Assert.True(s11.Magnitude * s11.Magnitude + s21.Magnitude * s21.Magnitude <= 1 + 1e-12);
    }

    [Fact]
    public void WithoutLoss_ItIsLossless()
    {
        var (s11, s21, _, _) = S(Make(90, 0.5, tanD: 0, sigma: 1e30).Abcd(5e9));
        Assert.Equal(1.0, s11.Magnitude * s11.Magnitude + s21.Magnitude * s21.Magnitude, 9);
    }

    [Fact]
    public void ASmallBend_TendsToTheStraightLineItIsDrawnAs()
    {
        // 2 degrees: the excess has all but vanished and the corner is a 15 um piece of line.
        var bend = Make(2.0, 0.0);
        var (z0, gamma) = MicrostripLineModel.Propagation(3e9, W, H, T, Er, Sigma, TanD, 0, new("x"));
        var line = MicrostripAbcd.UniformSection(z0, gamma * bend.CornerLengthMeters);
        var (a11, a21, _, _) = S(bend.Abcd(3e9)); var (b11, b21, _, _) = S(line);
        Assert.True((a11 - b11).Magnitude < 2e-3, $"S11 {a11} vs {b11}");
        Assert.True((a21 - b21).Magnitude < 2e-3, $"S21 {a21} vs {b21}");
    }

    [Fact]
    public void ADeepMiter_MovesThePinsOut_AndCarriesTheLineInBetween()
    {
        // 90 deg, M 0.6: the chamfer leg 2MW = 1.2W runs past the W/2 plane, so the pins sit at
        // 1.2W - W/2 = 0.7W from the centre-line corner; at M 0.5 they are still on the plane.
        Assert.Equal(W / 2, MicrostripMiterBend.PinOffset(W, 90, 0.5), 15);
        Assert.Equal(0.7 * W, MicrostripMiterBend.PinOffset(W, 90, 0.6), 12);
        Assert.Equal(0.7 * W, Make(90, 0.6).PinOffsetMeters, 12);
        // 45 deg, M 0.2: leg 2MW/sin45 past the (W/2)tan(22.5) plane.
        double s45 = W / 2 * Math.Tan(Math.PI / 8);
        Assert.Equal(2 * 0.2 * W / Math.Sin(Math.PI / 4) - s45, MicrostripMiterBend.PinOffset(W, 45, 0.2), 12);
    }

    [Fact]
    public void TheStamp_IsTheNodalAdmittanceOfTheChainMatrix()
    {
        var m = Make(90, 0.5);
        var mna = new CapturingMnaContext();
        m.Stamp(mna, new ElaboratedComponent("MMITER", "X1", [1, 2], new Dictionary<string, Value>(), m), 2 * Math.PI * 4e9);
        var t = m.Abcd(4e9);
        Assert.True((mna.Entries[(1, 1)] - t.D / t.B).Magnitude < 1e-9 * (t.D / t.B).Magnitude);
        Assert.True((mna.Entries[(2, 1)] + 1 / t.B).Magnitude < 1e-9 * (1 / t.B).Magnitude);
    }

    [Fact]
    public void AtDc_ItIsAThroughConnection_WithNoNaN()
    {
        var mna = new CapturingMnaContext();
        var m = Make();
        m.Stamp(mna, new ElaboratedComponent("MMITER", "X1", [1, 2], new Dictionary<string, Value>(), m), 0);
        Assert.All(mna.Entries.Values, v => Assert.False(double.IsNaN(v.Real) || double.IsNaN(v.Imaginary)));
    }

    [Fact]
    public void TheSignOfAngle_SteersOnlyTheArtwork()
    {
        var a = Make(60, 0.3).Abcd(2e9); var b = Make(-60, 0.3).Abcd(2e9);
        Assert.Equal(a.B, b.B); Assert.Equal(a.C, b.C);
    }

    // ── miter defaults and refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public void NoMiter_MeansTheDouvilleJamesOptimum_AtRightAngles_Silently()
    {
        var m = new MicrostripMiterBendModel(W, 90, double.NaN, H, T, Er, Sigma, TanD, "MMITER:X1");
        Assert.Equal(MicrostripMiterBend.DouvilleJamesOptimum(W, H), m.Miter, 15);
        Assert.DoesNotContain(m.DrainWarnings(), w => w.Key.StartsWith("MMITER:optimum", StringComparison.Ordinal));
    }

    [Fact]
    public void NoMiter_OnAnObliqueBend_SaysTheOptimumIsARightAngleFit()
    {
        var m = new MicrostripMiterBendModel(W, 45, double.NaN, H, T, Er, Sigma, TanD, "MMITER:X1");
        Assert.Contains(m.DrainWarnings(), w => w.Key == "MMITER:optimum-oblique");
    }

    [Fact]
    public void AnOptimumPastTheTable_IsCapped_AndSaysSo()
    {
        // W/h = 0.3: Douville-James asks for M ~ 0.95, the table stops at 0.8.
        var m = new MicrostripMiterBendModel(0.3 * H, 90, double.NaN, H, T, Er, Sigma, TanD, "MMITER:X1");
        Assert.Equal(0.8, m.Miter, 15);
        Assert.Contains(m.DrainWarnings(), w => w.Key == "MMITER:optimum-capped");
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(121.0, 0.3)]
    [InlineData(90.0, 0.81)]
    [InlineData(45.0, 0.75)]
    [InlineData(90.0, -0.1)]
    public void GeometryOutsideTheTable_IsRefused(double angleDeg, double miter)
        => Assert.Throws<ArgumentException>(() => Make(angleDeg, miter));

    [Fact]
    public void ABareAngle_IsRadians_AndNinetyRadiansIsRefused()
    {
        var parms = new Dictionary<string, Value>
        {
            ["W"] = new(W), ["Angle"] = new(90.0), ["M"] = new(0.5), ["H"] = new(H), ["T"] = new(T), ["Er"] = new(Er),
        };
        Assert.ThrowsAny<Exception>(() => ComponentModelFactory.TryCreate("MMITER", parms));
    }

    [Fact]
    public void Factory_BuildsMmiter_FromItsOwnParameterNames()
    {
        var parms = new Dictionary<string, Value>
        {
            ["W"] = new(W), ["Angle"] = new(Math.PI / 4), ["M"] = new(0.2), ["H"] = new(H), ["T"] = new(T), ["Er"] = new(Er),
        };
        var m = Assert.IsType<MicrostripMiterBendModel>(ComponentModelFactory.TryCreate("MMITER", parms));
        Assert.Equal(0.2, m.Miter, 15);
        Assert.Equal(W * Math.Tan(Math.PI / 8), m.CornerLengthMeters, 15);
        Assert.True(ComponentModelFactory.IsPrimitive("MMITER"));
    }

    [Fact]
    public void OutsideTheTablesWidthRange_IsReportedButComputed()
    {
        var m = Make(90, 0.3, w: 10 * H);
        Assert.Contains(m.DrainWarnings(), w => w.Message.Contains("MMITER corner table") && w.Message.Contains("W/h"));
        Assert.False(double.IsNaN(m.ExcessCapacitance));
    }
}
