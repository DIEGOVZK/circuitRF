using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// brief-modelcomposer-1 §4: an equation <c>.cmodel</c> inside the GaN network simulates exactly as
/// an SDD carrying the same expressions inside the same network written as a <c>.cnl</c> — DC, small
/// signal at three biases, and HB. The SDD is the independent reference: the composed core shares
/// only the expression engine with it, never its port, stamp or charge code.
/// </summary>
public sealed class ComposedFetEquivalenceTests(ITestOutputHelper output)
{
    private static readonly string ModelPath = ComposedTestModels.WriteTemp(ComposedTestModels.Model());

    private static string Composed => $"ComposedFet:X1 g d 0 File=\"{ModelPath}\"\n";
    private static string Reference => ComposedTestModels.SddGlobals
                                     + ComposedTestModels.ReferenceCell("ref") + "ref:X1 g d 0\n";

    internal static string Bench(double vgs, double vds) => $"""
        Port:P1 in 0 Num=1 Z=50 Ohm
        C:CB1 in g C=1e-6
        L:LB1 vg g L=1e-3
        Vdc:VG vg 0 V={vgs:R}
        Port:P2 out 0 Num=2 Z=50 Ohm
        C:CB2 out d C=1e-6
        L:LB2 vd d L=1e-3
        Vdc:VD vd 0 V={vds:R}

        """;

    [Fact]
    public void DcNodeVoltages_MatchTheSdd()
    {
        var a = Dc(Bench(-0.4, 6) + Composed);
        var b = Dc(Bench(-0.4, 6) + Reference);

        foreach (string node in (string[])["X1.gi", "X1.di", "X1.si", "X1.n1", "X1.n2", "X1.n3", "g", "d"])
        {
            double va = a[node], vb = b[node];
            output.WriteLine($"{node}: {va:R} vs {vb:R}");
            Assert.True(Math.Abs(va - vb) <= 1e-12 * Math.Max(Math.Abs(vb), 1e-3), $"{node}: {va:R} vs {vb:R}");
        }
        // Not vacuous: the drain current drops volts across rd and rs.
        Assert.True(Math.Abs(a["X1.di"] - a["X1.si"]) < 5.99);
    }

    [Theory]
    [InlineData(-1.5, 2.0)]
    [InlineData(-0.6, 6.0)]
    [InlineData(0.0, 10.0)]
    public void SParameters_MatchTheSdd_AtThreeBiases(double vgs, double vds)
    {
        double[] f = [0.5e9, 5e9, 20e9];
        var sa = Sp(Bench(vgs, vds) + Composed, f);
        var sb = Sp(Bench(vgs, vds) + Reference, f);

        for (int k = 0; k < f.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    Complex x = (Complex)sa[k, i, j], y = (Complex)sb[k, i, j];
                    Assert.True((x - y).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f[k]:G3} Hz: {x} vs {y}");
                }
        // Not vacuous: there is gain at the open-channel bias.
        if (vgs == 0.0) Assert.True(((Complex)sa[0, 1, 0]).Magnitude > 1.0);
    }

    [Fact]
    public void Hb_FundamentalAndHarmonics_MatchTheSdd()
    {
        const string drive = """
            V_1Tone:VS src 0 Vdc=-0.5 V=0.6 Freq=2e9 Phase=0
            R:RS src g R=50
            Vdc:VD vd 0 V=6
            R:RL vd d R=50
            analysis HB1 type=hb Tone=2e9 MaxHarm=5 Tol=1e-12

            """;
        var va = Hb(drive + Composed);
        var vb = Hb(drive + Reference);

        foreach (string node in (string[])["d", "g", "X1.di"])
        {
            int ia = Row(va, node), ib = Row(vb, node);
            for (int h = 0; h <= 5; h++)
            {
                Complex x = (Complex)va[ia, h], y = (Complex)vb[ib, h];
                output.WriteLine($"{node} H{h}: {x.Magnitude:E6} vs {y.Magnitude:E6}");
                Assert.True((x - y).Magnitude <= 1e-9 * Math.Max(y.Magnitude, 1e-3), $"{node} H{h}: {x} vs {y}");
            }
        }
        // Not vacuous: the drive generates a real second harmonic at the drain.
        Assert.True(((Complex)va[Row(va, "d"), 2]).Magnitude > 1e-3);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    internal static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    internal static Dictionary<string, double> Dc(string cnl, AnalysisSettings? settings = null)
    {
        var nl = Elaborate(cnl);
        var dc = NonlinearDcEngine.Run(nl, settings);
        Assert.True(dc.Converged);
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        var (rows, names) = nl.Nodes.ResultRows(excludeInternal: false);
        for (int k = 0; k < rows.Length; k++) map[names[k]] = dc.NodeVoltages[rows[k] - 1];
        return map;
    }

    internal static DataCube Sp(string cnl, double[] f) => SParameterEngine.Run(Elaborate(cnl), f)["S"];

    private static DataCube Hb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        return ((DataSet)new HbEngine(nl, tb).Run(p))["V"];
    }

    private static int Row(DataCube v, string node)
    {
        int i = Array.FindIndex(v.Axes[0].Labels!, n => n == node);
        Assert.True(i >= 0, $"no node '{node}'");
        return i;
    }
}
