using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// brief-modelcomposer-7 R-mc7-4: [P1] eq. 6.52 through the core's state node. A model whose DC and RF
/// currents differ — different <c>beta</c> and <c>lambda</c>, so different gm and gds — solves at DC exactly
/// as the same device with <c>Id = IdDC</c> and no dispersion, and far above 1/τ its S-parameters are those
/// of the device with <c>Id = IdRF</c>. HB walks between the two smoothly across the corner frequency.
/// </summary>
public sealed class DispersionNodeTests(ITestOutputHelper output)
{
    private static readonly string[] Inputs = ["vgs", "vds"];
    private static readonly (string, double)[] RfParams =
        [("beta", 0.05), ("Vto", -1.2), ("Vs", 0.15), ("lambda", -0.01), ("alpha", 2.5)];

    private static string Write(ComposedModel m) => ComposedTestModels.WriteTemp(m);

    private static ComposedModel Dispersive(double tau)
    {
        var m = ComposedTestModels.Model();
        return m with
        {
            Core = m.Core with
            {
                IdRF = Constituent.Equation(ComposedTestModels.IdEq, Inputs, RfParams),
                Dispersion = new ModelDispersion(tau),
            },
        };
    }

    private static ComposedModel RfOnly()
    {
        var m = ComposedTestModels.Model();
        return m with { Core = m.Core with { Id = Constituent.Equation(ComposedTestModels.IdEq, Inputs, RfParams) } };
    }

    private static string Fet(string path) => $"ComposedFet:X1 g d 0 File=\"{path}\"\n";

    [Fact]
    public void Dc_IsTheDcCurrent()
    {
        string bench = ComposedFetEquivalenceTests.Bench(-0.6, 6);
        var a = ComposedFetEquivalenceTests.Dc(bench + Fet(Write(Dispersive(1e-6))));
        var b = ComposedFetEquivalenceTests.Dc(bench + Fet(Write(ComposedTestModels.Model())));
        var c = ComposedFetEquivalenceTests.Dc(bench + Fet(Write(RfOnly())));
        foreach (string node in (string[])["X1.gi", "X1.di", "X1.si"])
        {
            output.WriteLine($"{node}: {a[node]:R} vs {b[node]:R} (RF-only {c[node]:R})");
            Assert.True(Math.Abs(a[node] - b[node]) <= 1e-12 * Math.Max(Math.Abs(b[node]), 1e-3), node);
        }
        // Not vacuous: IdRF alone puts the intrinsic drain elsewhere.
        Assert.True(Math.Abs(c["X1.di"] - b["X1.di"]) > 1e-4);
    }

    [Fact]
    public void SParameters_FarAboveTheCorner_AreTheRfCurrents()
    {
        double[] f = [0.5e9, 5e9, 20e9];
        string bench = ComposedFetEquivalenceTests.Bench(-0.6, 6);
        // τ = 1 ms: at 0.5 GHz the state node's residual response is 1/(ωτ) ≈ 3e-7 of the DC–RF difference.
        var sa = ComposedFetEquivalenceTests.Sp(bench + Fet(Write(Dispersive(1e-3))), f);
        var dcBias = ComposedFetEquivalenceTests.Dc(bench + Fet(Write(Dispersive(1e-3))));
        // The reference is biased where the dispersive device is: same intrinsic point, RF conductances.
        // IdRF alone would bias elsewhere, so its gate and drain supplies are moved to reproduce that point.
        var sb = SpAtIntrinsic(dcBias, f);
        var sDc = ComposedFetEquivalenceTests.Sp(bench + Fet(Write(ComposedTestModels.Model())), f);
        double worst = 0, apart = 0;
        for (int k = 0; k < f.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    Complex x = (Complex)sa[k, i, j], y = (Complex)sb[k, i, j];
                    worst = Math.Max(worst, (x - y).Magnitude);
                    apart = Math.Max(apart, ((Complex)sDc[k, i, j] - y).Magnitude);
                }
        output.WriteLine($"max |ΔS| vs the RF device {worst:E3}; DC device is {apart:E3} away");
        Assert.True(worst < 1e-6, $"max |ΔS| {worst:E3}");
        Assert.True(apart > 1e-2);
    }

    [Fact]
    public void Hb_IsContinuousAcrossTheCornerFrequency()
    {
        const double tau = 1e-6;
        double corner = 1 / (2 * Math.PI * tau);
        string disp = Write(Dispersive(tau)), dcOnly = Write(ComposedTestModels.Model());

        // ±2.5 decades: wide enough to settle on either side, low enough that the device's charges do not act.
        var mags = new List<double>();
        for (int k = -15; k <= 15; k++) mags.Add(DrainFundamental(disp, corner * Math.Pow(10, k / 6.0)));
        for (int k = 0; k < mags.Count; k++) output.WriteLine($"f/fc = 10^{(k - 15) / 6.0:F2}: |Vd1| = {mags[k]:E6}");

        // Below the corner the DC device; above it, the same device with x frozen (τ = 1 s), which is IdRF's
        // small signal about IdDC's bias.
        double lowRef = DrainFundamental(dcOnly, corner * Math.Pow(10, -2.5));
        double highRef = DrainFundamental(Write(Dispersive(1.0)), corner * Math.Pow(10, 2.5));
        double swing = Math.Abs(highRef - lowRef);
        Assert.True(swing > 1e-2 * lowRef, "not vacuous: the DC and RF responses differ");
        Assert.True(Math.Abs(mags[0] - lowRef) < 1e-2 * swing, $"low end {mags[0]:E6} vs {lowRef:E6}");
        Assert.True(Math.Abs(mags[^1] - highRef) < 1e-2 * swing, $"high end {mags[^1]:E6} vs {highRef:E6}");

        int sign = Math.Sign(mags[^1] - mags[0]);
        for (int k = 1; k < mags.Count; k++)
        {
            double step = mags[k] - mags[k - 1];
            Assert.True(sign * step > -1e-3 * swing, $"not monotonic at step {k}");
            Assert.True(Math.Abs(step) < 0.25 * swing, $"step {k} jumps {Math.Abs(step) / swing:P1} of the swing");
        }
    }

    // ── harness ──────────────────────────────────────────────────────────────

    /// <summary>|V(d)| at the fundamental, a 0.1 V gate drive at <paramref name="f"/> about −0.5 V.</summary>
    private static double DrainFundamental(string model, double f)
    {
        string cnl = $"""
            V_1Tone:VS src 0 Vdc=-0.5 V=0.1 Freq={f:R} Phase=0
            R:RS src g R=50
            Vdc:VD vd 0 V=6
            R:RL vd d R=50
            analysis HB1 type=hb Tone={f:R} MaxHarm=5 Tol=1e-12

            """ + Fet(model);
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Core.Elaboration.Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var v = ((DataSet)new HbEngine(nl, tb).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals)))["V"];
        int row = Array.FindIndex(v.Axes[0].Labels!, n => n == "d");
        return ((Complex)v[row, 1]).Magnitude;
    }

    /// <summary>S of the RF-only device with its supplies set so its intrinsic bias is <paramref name="at"/>'s:
    /// a DC solve per trial, Newton on the two supplies with a finite-difference Jacobian.</summary>
    private static DataCube SpAtIntrinsic(Dictionary<string, double> at, double[] f)
    {
        string path = Write(RfOnly());
        double tg = at["X1.gi"] - at["X1.si"], td = at["X1.di"] - at["X1.si"];
        double vg = -0.6, vd = 6;
        (double, double) Intrinsic(double g, double d)
        {
            var r = ComposedFetEquivalenceTests.Dc(ComposedFetEquivalenceTests.Bench(g, d) + Fet(path));
            return (r["X1.gi"] - r["X1.si"], r["X1.di"] - r["X1.si"]);
        }
        for (int it = 0; it < 20; it++)
        {
            var (a, b) = Intrinsic(vg, vd);
            double eg = a - tg, ed = b - td;
            if (Math.Abs(eg) < 1e-13 && Math.Abs(ed) < 1e-13) break;
            const double h = 1e-6;
            var (ag, bg) = Intrinsic(vg + h, vd);
            var (ad, bd) = Intrinsic(vg, vd + h);
            double j11 = (ag - a) / h, j12 = (ad - a) / h, j21 = (bg - b) / h, j22 = (bd - b) / h;
            double det = j11 * j22 - j12 * j21;
            vg -= (j22 * eg - j12 * ed) / det;
            vd -= (-j21 * eg + j11 * ed) / det;
        }
        return ComposedFetEquivalenceTests.Sp(ComposedFetEquivalenceTests.Bench(vg, vd) + Fet(path), f);
    }
}
