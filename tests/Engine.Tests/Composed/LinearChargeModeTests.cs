using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit.Abstractions;
using static CircuitRF.Engine.Tests.Composed.ComposedFetEquivalenceTests;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// brief-modelcomposer-21 R-mc21-1, R-mc21-3: <c>LinearC=true</c> is the charge's tangent at the quiescent DC point, and
/// <c>Charge=false</c> is no intrinsic charge at all — both judged against the same model evaluated as fitted.
/// </summary>
public sealed class LinearChargeModeTests(ITestOutputHelper output)
{
    private static readonly string ModelPath = ComposedTestModels.WriteTemp(ComposedTestModels.Model());

    private static string Fet(string path, string switches = "") => $"ComposedFet:X1 g d 0 File=\"{path}\" {switches}\n";

    [Fact]
    public void SParameters_AtTheBiasPoint_AreTheSameWithLinearC()
    {
        double[] f = [0.5e9, 5e9, 20e9];
        var a = Sp(Bench(-0.6, 6) + Fet(ModelPath), f);
        var b = Sp(Bench(-0.6, 6) + Fet(ModelPath, "LinearC=true"), f);
        for (int k = 0; k < f.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    Assert.True(((Complex)a[k, i, j] - (Complex)b[k, i, j]).Magnitude <= 1e-12, $"S{i + 1}{j + 1} at {f[k]:G3}");
    }

    [Fact]
    public void TheLinearCore_IsTheTangentAtTheFrozenPoint()
    {
        var loaded = ComposedModelCache.Load(ModelPath);
        var fitted = new ComposedCoreModel(loaded, 4, 25);
        var linear = new ComposedCoreModel(loaded, 4, 25, new ComposedSwitches { LinearC = true });
        double[] vq = [-0.6, 5.5];
        Assert.True(linear.FreezeAt(vq));
        Assert.False(linear.FreezeAt(vq));   // the same point again changes nothing
        var at = fitted.Evaluate(new PortVoltages(vq));

        var rng = new Random(21);
        for (int t = 0; t < 5; t++)
        {
            double[] v = [-2 + 2.5 * rng.NextDouble(), 12 * rng.NextDouble()];
            var r = linear.Evaluate(new PortVoltages(v));
            for (int p = 0; p < 2; p++)
            {
                double expected = at.Q[p] + at.Dc[p, 0] * (v[0] - vq[0]) + at.Dc[p, 1] * (v[1] - vq[1]);
                Assert.True(Math.Abs(r.Q[p] - expected) <= 1e-12 * Math.Abs(expected), $"q{p} at ({v[0]:G4}, {v[1]:G4})");
                Assert.Equal(at.Dc[p, 0], r.Dc[p, 0]);
                Assert.Equal(at.Dc[p, 1], r.Dc[p, 1]);
            }
            Assert.Equal(fitted.Evaluate(new PortVoltages(v)).I, r.I);   // the currents keep their nonlinearity
        }
    }

    [Fact]
    public void ChargeOff_IsTheSameCurrentsWithZeroCharges()
    {
        var m = ComposedTestModels.Model();
        string zeroPath = ComposedTestModels.WriteTemp(m with
        {
            Core = m.Core with { Qg = Constituent.Equation("0", ["vgs", "vds"]), Qd = Constituent.Equation("0", ["vgs", "vds"]) },
        });
        double[] f = [0.5e9, 5e9, 20e9];
        var a = Sp(Bench(-0.6, 6) + Fet(ModelPath, "Charge=false LinearC=true"), f);
        var b = Sp(Bench(-0.6, 6) + Fet(zeroPath), f);
        for (int k = 0; k < f.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    Assert.True(((Complex)a[k, i, j] - (Complex)b[k, i, j]).Magnitude <= 1e-12, $"S{i + 1}{j + 1} at {f[k]:G3}");
    }

    [Fact]
    public void PowerSweep_DiffersAtCompression_NotAtMinus30dBm()
    {
        double Gain(string switches, double amplitude)
        {
            string cnl = $"""
                V_1Tone:VS src 0 Vdc=-0.6 V={amplitude:R} Freq=2e9 Phase=0
                R:RS src g R=50
                Vdc:VD vd 0 V=6
                R:RL vd d R=50
                analysis HB1 type=hb Tone=2e9 MaxHarm=5 Tol=1e-12

                """ + Fet(ModelPath, switches);
            var v = Hb(cnl);
            int d = Array.FindIndex(v.Axes[0].Labels!, n => n == "d");
            return 20 * Math.Log10(((Complex)v[d, 1]).Magnitude / amplitude);
        }

        // 0.02 V behind 50 Ω is −30 dBm available.
        double small = Gain("", 0.02) - Gain("LinearC=true", 0.02);
        Assert.True(Math.Abs(small) <= 0.001, $"{small} dB at −30 dBm");
        double large = Gain("", 1.6) - Gain("LinearC=true", 1.6);
        output.WriteLine($"gain difference: {small:E3} dB at −30 dBm, {large:F4} dB at 1.6 V drive");
    }

    private static DataCube Hb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        return ((DataSet)new HbEngine(nl, tb).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals)))["V"];
    }
}
