using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Elaboration;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// brief-modelcomposer-2 R-mc2-5 and the ANN's place in the engines: an HB time grid evaluates with no
/// allocation, and an ANN <c>.cmodel</c> simulates exactly as an equation <c>.cmodel</c> carrying the
/// same function written out — DC, S-parameters and HB.
/// </summary>
public sealed class ComposedCoreGridTests
{
    [Fact]
    public void AnHbGrid_AllocatesNothing_AfterWarmUp()
    {
        // Tables and ANNs, half the samples inside the domain and half outside (the extrapolation path
        // with its Hessian), so every evaluator's scratch is exercised.
        var core = RuntimeTestModels.Core(RuntimeTestModels.MixedModel());
        const int samples = 64;
        var v = new double[2 * samples];
        for (int t = 0; t < samples; t++)
        {
            double phase = 2 * Math.PI * t / samples;
            v[t] = -1.0 + 3.0 * Math.Sin(phase);              // vgs −4 … 2
            v[samples + t] = 5.0 + 9.0 * Math.Cos(phase);     // vds −4 … 14
        }
        var into = new GridResult();

        core.EvaluateGrid(v, [], samples, into);   // warm-up: buffers sized, JIT done
        long before = GC.GetAllocatedBytesForCurrentThread();
        core.EvaluateGrid(v, [], samples, into);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Contains(into.I.Take(2 * samples), x => x != 0);
    }

    private static readonly string Ann  = ComposedTestModels.WriteTemp(RuntimeTestModels.AnnModel(RuntimeTestModels.Wide));
    private static readonly string Twin = ComposedTestModels.WriteTemp(RuntimeTestModels.EquationTwin());

    [Fact]
    public void AnAnnModel_EqualsItsEquationTwin_InDcSpAndHb()
    {
        string Fet(string path) => $"ComposedFet:X1 g d 0 File=\"{path}\"\n";

        // DC.
        var bench = ComposedFetEquivalenceTests.Bench(-0.4, 6);
        var da = ComposedFetEquivalenceTests.Dc(bench + Fet(Ann));
        var db = ComposedFetEquivalenceTests.Dc(bench + Fet(Twin));
        foreach (string node in (string[])["X1.gi", "X1.di", "X1.si", "g", "d"])
            Assert.True(Math.Abs(da[node] - db[node]) <= 1e-9 * Math.Max(Math.Abs(db[node]), 1e-3), $"DC {node}: {da[node]:R} vs {db[node]:R}");

        // S-parameters.
        double[] f = [0.5e9, 5e9, 20e9];
        var sa = ComposedFetEquivalenceTests.Sp(bench + Fet(Ann), f);
        var sb = ComposedFetEquivalenceTests.Sp(bench + Fet(Twin), f);
        for (int k = 0; k < f.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    Complex x = (Complex)sa[k, i, j], y = (Complex)sb[k, i, j];
                    Assert.True((x - y).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f[k]:G3} Hz: {x} vs {y}");
                }
        Assert.True(((Complex)sa[0, 1, 0]).Magnitude > 0.1);   // not vacuous: the device transmits

        // HB.
        const string drive = """
            V_1Tone:VS src 0 Vdc=-0.5 V=0.6 Freq=2e9 Phase=0
            R:RS src g R=50
            Vdc:VD vd 0 V=6
            R:RL vd d R=50
            analysis HB1 type=hb Tone=2e9 MaxHarm=5 Tol=1e-12

            """;
        var va = Hb(drive + Fet(Ann));
        var vb = Hb(drive + Fet(Twin));
        int ra = Row(va, "d"), rb = Row(vb, "d");
        for (int h = 0; h <= 5; h++)
        {
            Complex x = (Complex)va[ra, h], y = (Complex)vb[rb, h];
            Assert.True((x - y).Magnitude <= 1e-9 * Math.Max(y.Magnitude, 1e-3), $"d H{h}: {x} vs {y}");
        }
        Assert.True(((Complex)va[ra, 2]).Magnitude > 1e-3);   // not vacuous: a real second harmonic
    }

    private static DataCube Hb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        return ((DataSet)new HbEngine(nl, tb).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals)))["V"];
    }

    private static int Row(DataCube v, string node) => Array.FindIndex(v.Axes[0].Labels!, n => n == node);
}
