using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Coupled;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Expressions;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// MCLIN, the edge-coupled microstrip pair. The Kirschning-Jansen statics are held to circuitRF's own
/// 2D field solver (an independent method), the dispersion to its static limit, the wide-gap limit to
/// MLIN's single line, and the stamp to CLIN fed the same modal numbers.
/// </summary>
public class MclinModelTests(ITestOutputHelper output)
{
    private const double C0 = MicrostripLoss.SpeedOfLight;

    private static (double Ze, double Zo, double Ee, double Eo) FieldSolve(double u, double g, double er, double tOverH)
    {
        double h = 1e-3, w = u * h, s = g * h, t = tOverH * h;
        var cs = new LayeredCrossSection([new DielectricLayer(0, h, er)], null,
            [new StripRect(-s / 2 - w, -s / 2, h, h + t), new StripRect(s / 2, s / 2 + w, h, h + t)]);
        var d = QuasiStaticSolver2D.Solve(cs).C;
        var a = QuasiStaticSolver2D.Solve(cs, airOnly: true).C;
        double ce = d[0, 0] + d[0, 1], co = d[0, 0] - d[0, 1], ce0 = a[0, 0] + a[0, 1], co0 = a[0, 0] - a[0, 1];
        return (1 / (C0 * Math.Sqrt(ce * ce0)), 1 / (C0 * Math.Sqrt(co * co0)), ce / ce0, co / co0);
    }

    /// <summary>
    /// Static even/odd Z and εeff against the field solver, thin and thick strips. The published formulas
    /// claim ~1 %; the solver errs ~0.5 % low on thin strips; the thickness corrections are first-order.
    /// </summary>
    [Theory]
    [InlineData(1.0, 1.0, 9.8, 0.0)]
    [InlineData(1.0, 0.2, 9.8, 0.0)]
    [InlineData(0.5, 0.5, 4.4, 0.0)]
    [InlineData(2.0, 2.0, 3.0, 0.0)]
    [InlineData(3.0, 0.5, 2.2, 0.0)]
    [InlineData(1.0, 0.3, 4.4, 0.035)]
    [InlineData(0.3, 0.15, 9.8, 0.02)]
    [InlineData(0.5, 0.5, 3.5, 0.1)]
    public void StaticModes_AgreeWithTheFieldSolver(double u, double g, double er, double t)
    {
        var fd = FieldSolve(u, g, er, t);
        var kj = KirschningJansenCoupled.Compute(0, u * 1e-3, g * 1e-3, 1e-3, t * 1e-3, er, new MicrostripValidityReporter("t"));
        output.WriteLine($"u={u} g={g} er={er} t/h={t}: KJ Ze {kj.Ze:F2} Zo {kj.Zo:F2} ee {kj.EeffE:F3} eo {kj.EeffO:F3} | " +
                         $"field Ze {fd.Ze:F2} Zo {fd.Zo:F2} ee {fd.Ee:F3} eo {fd.Eo:F3}");
        Assert.InRange(kj.Ze / fd.Ze, 0.975, 1.025);
        Assert.InRange(kj.Zo / fd.Zo, 0.975, 1.03);
        Assert.InRange(kj.EeffE / fd.Ee, 0.98, 1.02);
        Assert.InRange(kj.EeffO / fd.Eo, 0.98, 1.02);
    }

    [Fact]
    public void Dispersion_StartsAtTheStaticValues_AndRaisesBothPermittivities()
    {
        var rep = new MicrostripValidityReporter("t");
        var st = KirschningJansenCoupled.Compute(0, 0.6e-3, 0.3e-3, 0.635e-3, 10e-6, 9.8, rep);
        var lo = KirschningJansenCoupled.Compute(1e3, 0.6e-3, 0.3e-3, 0.635e-3, 10e-6, 9.8, rep);
        Assert.Equal(st.Ze, lo.Ze, 6);
        Assert.Equal(st.Zo, lo.Zo, 6);
        Assert.Equal(st.EeffE, lo.EeffE, 9);
        Assert.Equal(st.EeffO, lo.EeffO, 9);

        double prevE = st.EeffE, prevO = st.EeffO;
        foreach (double f in new[] { 2e9, 5e9, 10e9, 20e9 })
        {
            var m = KirschningJansenCoupled.Compute(f, 0.6e-3, 0.3e-3, 0.635e-3, 10e-6, 9.8, rep);
            Assert.True(m.EeffE > prevE && m.EeffO > prevO, $"εeff must rise with frequency (f={f})");
            Assert.True(m.EeffE < 9.8 && m.EeffO < 9.8);
            prevE = m.EeffE; prevO = m.EeffO;
        }
        // The microstrip signature: the even mode is slower (more of its field in the substrate).
        Assert.True(st.EeffE > st.EeffO);
    }

    /// <summary>S → ∞: both modes become the single line (Hammerstad-Jensen), so the pair is two MLINs.</summary>
    [Fact]
    public void WideGap_ReducesToTheSingleLine()
    {
        double w = 1e-3, h = 1e-3, t = 0, er = 4.4;
        var m = KirschningJansenCoupled.Compute(0, w, 10 * h, h, t, er, new MicrostripValidityReporter("t"));
        var (z, e) = HammerstadJensen.Compute(w, h, t, er, new MicrostripValidityReporter("t"));
        // s/h = 10 is the published range's edge, where the fit's own residual (~0.5 %) remains.
        Assert.InRange(m.Ze / z, 0.99, 1.01);
        Assert.InRange(m.Zo / z, 0.99, 1.01);
        Assert.InRange(m.EeffE / e, 0.99, 1.01);
        Assert.InRange(m.EeffO / e, 0.99, 1.01);
    }

    /// <summary>The stamp IS CLIN's, fed MCLIN's own modal numbers at that frequency (lossless).</summary>
    [Theory]
    [InlineData(1e9)]
    [InlineData(4e9)]
    public void LosslessStamp_EqualsClinWithTheSameModes(double f)
    {
        double l = 12e-3;
        var mc = new MicrostripCoupledLineModel(1e-3, 0.3e-3, l, 0.8e-3, 0, 3.5, double.PositiveInfinity, 0, "MCLIN:X1");
        var modes = mc.ModesAt(f);
        double k0 = 2 * Math.PI * f / C0;
        var cl = new CLineModel(modes.Ze, modes.Zo, k0 * Math.Sqrt(modes.EeffE) * l, k0 * Math.Sqrt(modes.EeffO) * l, f);
        var a = CoupledPairTestKit.Y(mc, f);
        var b = CoupledPairTestKit.Y(cl, f);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude <= 1e-9 * b[i, j].Magnitude + 1e-15, $"Y[{i},{j}]");
    }

    [Fact]
    public void LossyPair_IsReciprocal_Symmetric_AndPassive()
    {
        var m = new MicrostripCoupledLineModel(1.2e-3, 0.25e-3, 15e-3, 0.8e-3, 35e-6, 4.4, 5.8e7, 0.02, "MCLIN:X1", 1e-6);
        foreach (double f in new[] { 0.5e9, 2e9, 6e9 })
        {
            var s = CoupledPairTestKit.S(m, f);
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    Assert.True((s[i, j] - s[j, i]).Magnitude < 1e-12, "reciprocal");
            Assert.True((s[0, 0] - s[1, 1]).Magnitude < 1e-12 && (s[0, 0] - s[2, 2]).Magnitude < 1e-12, "symmetric");
            Assert.True((s[1, 0] - s[3, 2]).Magnitude < 1e-12 && (s[2, 0] - s[3, 1]).Magnitude < 1e-12, "line symmetry");
            Assert.True(CoupledPairTestKit.MaxSingularSquared(s) < 1.0, $"passive at {f}");
        }
    }

    /// <summary>Microstrip's finite directivity: unequal modal velocities leak to the isolated port.</summary>
    [Fact]
    public void UnequalModalVelocities_LimitTheIsolation()
    {
        var m = new MicrostripCoupledLineModel(0.6e-3, 0.15e-3, 13e-3, 0.635e-3, 0, 9.8, double.PositiveInfinity, 0, "MCLIN:X1");
        var s = CoupledPairTestKit.S(m, 2.2e9);
        double c = s[2, 0].Magnitude, iso = s[3, 0].Magnitude;
        output.WriteLine($"|S31| {20 * Math.Log10(c):F2} dB, |S41| {20 * Math.Log10(iso):F2} dB");
        Assert.True(iso > 1e-3, "an inhomogeneous pair has finite isolation");
        Assert.True(iso < c, "but it still couples forward less than backward");
    }

    [Theory]
    [InlineData(0.0, 0.3e-3, "mclin.width.not-positive")]
    [InlineData(1e-3, 0.0, "mclin.gap.not-positive")]
    public void InvalidGeometry_IsRefusedWithACode(double w, double s, string code)
    {
        var why = MicrostripCoupledLineModel.Refusal(w, s, 1e-3, 1e-3, 0, 4.4);
        Assert.Equal(code, why!.Id);
        Assert.Throws<ArgumentException>(() => new MicrostripCoupledLineModel(w, s, 1e-3, 1e-3, 0, 4.4, 5.8e7, 0, "X"));
    }

    [Fact]
    public void OutOfRangeGeometry_IsReportedOnce()
    {
        var m = new MicrostripCoupledLineModel(1e-3, 0.02e-3, 5e-3, 1e-3, 0, 4.4, 5.8e7, 0, "MCLIN:X1");
        CoupledPairTestKit.Y(m, 1e9);
        CoupledPairTestKit.Y(m, 2e9);
        var w = m.DrainWarnings();
        Assert.Single(w, x => x.Message.Contains("S/h"));
    }

    [Fact]
    public void Factory_BuildsMclin_FromNetlistParameters()
    {
        var p = new Dictionary<string, Value>
        {
            ["W"] = new(1e-3), ["S"] = new(0.2e-3), ["L"] = new(10e-3),
            ["H"] = new(0.8e-3), ["T"] = new(35e-6), ["Er"] = new(3.5),
        };
        Assert.IsType<MicrostripCoupledLineModel>(ComponentModelFactory.TryCreate("MCLIN", p));
        Assert.True(ComponentModelFactory.IsPrimitive("MCLIN"));
    }
}
