using System.Diagnostics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Coupled;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Expressions;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// The 2D quasi-static field solver behind BCLIN and MBCLIN, and BCLIN itself: held to exact closed
/// forms (Cohn's zero-thickness stripline; the parallel-plate increment of wide strips), to an external
/// 3D FEM electrostatic reference, and to its own uncoupled and mirror-symmetric limits.
/// </summary>
public class BclinFieldSolverTests(ITestOutputHelper output)
{
    private const double C0 = MicrostripLoss.SpeedOfLight, Eps0 = QuasiStaticSolver2D.Eps0, Um = 1e-6;

    /// <summary>K(k), the complete elliptic integral of the first kind, by the arithmetic-geometric mean.</summary>
    private static double K(double k)
    {
        double a = 1, b = Math.Sqrt(1 - k * k);
        for (int i = 0; i < 60; i++) (a, b) = ((a + b) / 2, Math.Sqrt(a * b));
        return Math.PI / (2 * a);
    }

    /// <summary>Cohn's exact impedance of a zero-thickness strip centred between grounds b apart (air).</summary>
    private static double Cohn(double w, double b)
        => 376.730313668 / 4 * K(1 / Math.Cosh(Math.PI * w / (2 * b))) / K(Math.Tanh(Math.PI * w / (2 * b)));

    [Theory]
    [InlineData(1.0e-3, 2.0e-3)]
    [InlineData(0.2e-3, 2.0e-3)]
    [InlineData(3.0e-3, 1.0e-3)]
    public void ThinCentredStrip_MatchesCohnsExactStripline(double w, double b)
    {
        var s = QuasiStaticSolver2D.Solve(new LayeredCrossSection([], b, [new StripRect(-w / 2, w / 2, b / 2, b / 2)]));
        double z = 1 / (C0 * s.C[0, 0]);
        output.WriteLine($"W/b={w / b}: solver {z:F3} ohm, Cohn {Cohn(w, b):F3} ohm");
        Assert.InRange(z / Cohn(w, b), 0.985, 1.005);
    }

    /// <summary>
    /// Wide thin strips: the fringing at each edge no longer depends on the width, so widening by ΔW adds
    /// exactly the parallel-plate capacitance — ε·ΔW·(1/Ha + 1/Hs) to C11, ε·ΔW·(1/Hb + 1/Hs) to C22 and
    /// −ε·ΔW/Hs to C12. An exact check of the two-conductor bookkeeping, independent of any fringing model.
    /// </summary>
    [Fact]
    public void WideStrips_AddExactlyTheParallelPlateCapacitance()
    {
        double hs = 0.1e-3, ha = 0.3e-3, hb = 0.2e-3, er = 2.5;
        LayeredCrossSection Cs(double w)
        {
            double top = hb + hs + ha;
            return new([new DielectricLayer(0, top, er)], top,
                [new StripRect(-w / 2, w / 2, hb + hs, hb + hs), new StripRect(-w / 2, w / 2, hb, hb)]);
        }
        double w1 = 3e-3, w2 = 6e-3, e = Eps0 * er, dw = w2 - w1;
        var c1 = QuasiStaticSolver2D.Solve(Cs(w1)).C;
        var c2 = QuasiStaticSolver2D.Solve(Cs(w2)).C;
        Assert.InRange((c2[0, 0] - c1[0, 0]) / (e * dw * (1 / ha + 1 / hs)), 0.995, 1.005);
        Assert.InRange((c2[1, 1] - c1[1, 1]) / (e * dw * (1 / hb + 1 / hs)), 0.995, 1.005);
        Assert.InRange((c2[0, 1] - c1[0, 1]) / (-e * dw / hs), 0.995, 1.005);
    }

    /// <summary>
    /// The external reference: a 3D FEM electrostatic solve of thick copper strips (25 µm upper, 18 µm
    /// lower), Hs = 76 µm, 73 µm and 77 µm to the grounds, εr 3.0, W 400 µm, no offset. The reference's
    /// own mesh and box are not this solver's; a few percent is the agreement to expect.
    /// </summary>
    [Fact]
    public void ReferenceStack_AgreesWithTheExternal3DReference()
    {
        var sw = Stopwatch.StartNew();
        var m = new StriplineBroadsideCoupledModel(400 * Um, 0, 10e-3, 76 * Um, 73 * Um, 77 * Um, 25 * Um, 18 * Um, 3.0, 5.8e7, 0, "BCLIN:X1");
        sw.Stop();
        var c = m.Modes.C;
        output.WriteLine($"C11 {c[0, 0]:E4} C22 {c[1, 1]:E4} C12 {c[0, 1]:E4} F/m; Ze {m.Modes.Ze:F2} Zo {m.Modes.Zo:F2} ohm; {sw.ElapsedMilliseconds} ms");
        Assert.InRange(c[0, 0] / 3.557e-10, 0.965, 1.035);
        Assert.InRange(c[1, 1] / 3.445e-10, 0.965, 1.035);
        Assert.InRange(c[0, 1] / -1.551e-10, 0.965, 1.035);
        // The modes the reference's own matrix implies, by the same symmetric-average convention.
        double avg = (3.557e-10 + 3.445e-10) / 2, v = C0 / Math.Sqrt(3.0);
        Assert.InRange(m.Modes.Ze / (1 / (v * (avg - 1.551e-10))), 0.96, 1.04);
        Assert.InRange(m.Modes.Zo / (1 / (v * (avg + 1.551e-10))), 0.96, 1.04);
        Assert.Equal(3.0, m.Modes.EeffE);
        Assert.Equal(3.0, m.Modes.EeffO);
    }

    /// <summary>O → ∞: no coupling, and each strip is the single stripline it then is (Cohn, where centred).</summary>
    [Fact]
    public void FarOffset_UncouplesIntoTwoSingleStriplines()
    {
        // Thin strips; A centred between its grounds (Hb + Hs = Ha), so A alone is Cohn's stripline.
        double w = 0.5e-3, hb = 0.2e-3, hs = 0.3e-3, ha = 0.5e-3, b = hb + hs + ha;
        var m = new StriplineBroadsideCoupledModel(w, 12 * b, 1e-3, hs, ha, hb, 0, 0, 1.0, 5.8e7, 0, "BCLIN:X1");
        var c = m.Modes.C;
        Assert.True(Math.Abs(c[0, 1]) < 1e-4 * c[0, 0], "uncoupled");
        Assert.InRange(1 / (C0 * c[0, 0]) / Cohn(w, b), 0.985, 1.005);
        Assert.InRange(m.Modes.Ze / m.Modes.Zo, 0.999, 1.001);
    }

    /// <summary>
    /// A symmetric pair's odd mode puts a ground plane on the mid-plane, so its C_o is exactly the C of
    /// ONE strip at Hs/2 over a ground with the other ground where it was.
    /// </summary>
    [Fact]
    public void SymmetricOddMode_IsOneStripOverTheMidPlane()
    {
        double w = 0.4e-3, hs = 0.1e-3, hab = 0.15e-3, t = 20e-6, er = 3.0;
        var m = new StriplineBroadsideCoupledModel(w, 0, 1e-3, hs, hab, hab, t, t, er, 5.8e7, 0, "BCLIN:X1");
        double top = hs / 2 + t + hab;
        var half = QuasiStaticSolver2D.Solve(new LayeredCrossSection([new DielectricLayer(0, top, er)], top,
            [new StripRect(-w / 2, w / 2, hs / 2, hs / 2 + t)])).C[0, 0];
        double co = m.Modes.C[0, 0] - m.Modes.C[0, 1];
        Assert.InRange(co / half, 0.99, 1.01);
        // The grid is graded bottom-up, so it is not exactly mirror-symmetric; the residual is the grid's.
        Assert.True(m.Modes.Asymmetry < 2e-3, $"asymmetry {m.Modes.Asymmetry:E2}");
    }

    [Fact]
    public void LossyPair_IsReciprocal_Passive_AndTheStampIsClinsInAHomogeneousMedium()
    {
        var m = new StriplineBroadsideCoupledModel(300 * Um, 100 * Um, 20e-3, 76 * Um, 73 * Um, 77 * Um, 18 * Um, 18 * Um, 3.0, 5.8e7, 0.003, "BCLIN:X1");
        foreach (double f in new[] { 1e9, 3e9, 6e9 })
        {
            var s = CoupledPairTestKit.S(m, f);
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    Assert.True((s[i, j] - s[j, i]).Magnitude < 1e-12);
            Assert.True(CoupledPairTestKit.MaxSingularSquared(s) < 1.0);
        }
        // Lossless, both modes at c/√εr: exactly CLIN with E = Eo.
        var ll = new StriplineBroadsideCoupledModel(300 * Um, 100 * Um, 20e-3, 76 * Um, 73 * Um, 77 * Um, 18 * Um, 18 * Um, 3.0, double.PositiveInfinity, 0, "BCLIN:X2");
        double f0 = 2e9, e = 2 * Math.PI * f0 / C0 * Math.Sqrt(3.0) * 20e-3;
        var cl = new CLineModel(ll.Modes.Ze, ll.Modes.Zo, e, e, f0);
        var a = CoupledPairTestKit.Y(ll, f0);
        var b = CoupledPairTestKit.Y(cl, f0);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude <= 1e-9 * b[i, j].Magnitude + 1e-15);
        // A homogeneous pair is a perfect directional coupler when matched: isolation limited only by Ze·Zo ≠ 50².
        var s0 = CoupledPairTestKit.S(cl, f0, Math.Sqrt(ll.Modes.Ze * ll.Modes.Zo));
        Assert.True(s0[3, 0].Magnitude < 1e-9);
    }

    /// <summary>Conductor loss on the solved field scales as Rs (√f), is larger for the odd mode (its
    /// current crowds onto the facing surfaces), and the homogeneous dielectric loss is exact.</summary>
    [Fact]
    public void ConductorLoss_HasTheSkinEffectScaling()
    {
        var m = new StriplineBroadsideCoupledModel(400 * Um, 0, 1, 76 * Um, 73 * Um, 77 * Um, 18 * Um, 18 * Um, 3.0, 5.8e7, 0, "BCLIN:X1");
        var (e1, o1) = BroadsideCoupledPair.Gamma(m.Modes, 1e9, 3.0, 5.8e7, 0, 0);
        var (e4, o4) = BroadsideCoupledPair.Gamma(m.Modes, 4e9, 3.0, 5.8e7, 0, 0);
        Assert.Equal(2.0, e4.Real / e1.Real, 9);                 // √f
        Assert.Equal(2.0, o4.Real / o1.Real, 9);
        Assert.True(o1.Real > e1.Real, "the odd mode's currents crowd onto the facing surfaces");
        // Dielectric loss is exact in a homogeneous medium: π·√εr·tanδ/λ₀ on both modes.
        var (ed, od) = BroadsideCoupledPair.Gamma(m.Modes, 1e9, 3.0, double.PositiveInfinity, 0.01, 0);
        double expect = Math.PI * Math.Sqrt(3.0) * 0.01 * 1e9 / C0;
        Assert.Equal(expect, ed.Real, 12);
        Assert.Equal(expect, od.Real, 12);
    }

    [Theory]
    [InlineData(0.0, 0.0, 76e-6, "bclin.width.not-positive")]
    [InlineData(400e-6, -1e-6, 76e-6, "bclin.offset.negative")]
    [InlineData(400e-6, 0.0, 0.0, "bclin.hs.not-positive")]
    public void InvalidCrossSection_IsRefusedWithACode(double w, double o, double hs, string code)
    {
        var why = StriplineBroadsideCoupledModel.Refusal(w, o, 1e-3, hs, 73e-6, 77e-6, 18e-6, 18e-6, 3.0);
        Assert.Equal(code, why!.Id);
    }

    [Fact]
    public void Factory_BuildsBclin_WithOneThicknessOrTwo()
    {
        var p = new Dictionary<string, Value>
        {
            ["W"] = new(400e-6), ["O"] = new(0.0), ["L"] = new(10e-3), ["Hs"] = new(76e-6), ["Ha"] = new(73e-6),
            ["Hb"] = new(77e-6), ["T"] = new(18e-6), ["Ta"] = new(25e-6), ["Er"] = new(3.0),
        };
        var m = Assert.IsType<StriplineBroadsideCoupledModel>(ComponentModelFactory.TryCreate("BCLIN", p));
        Assert.Equal(25e-6, m.Geometry.Ta);
        Assert.Equal(18e-6, m.Geometry.Tb);
        Assert.True(ComponentModelFactory.IsPrimitive("BCLIN"));
    }

    [Fact]
    public void ASecondInstanceOfTheSameGeometry_IsServedFromTheCache()
    {
        var a = new StriplineBroadsideCoupledModel(333 * Um, 50 * Um, 1e-3, 70 * Um, 80 * Um, 90 * Um, 17 * Um, 17 * Um, 3.3, 5.8e7, 0, "A");
        var b = new StriplineBroadsideCoupledModel(333 * Um, 50 * Um, 2e-3, 70 * Um, 80 * Um, 90 * Um, 17 * Um, 17 * Um, 3.3, 5.8e7, 0, "B");
        Assert.Same(a.Modes, b.Modes);
    }
}
