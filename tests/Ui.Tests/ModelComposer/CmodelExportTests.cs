using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Modeling;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-11 §4: a composed model exported and placed in a <c>.cnl</c> simulates as the truth it was composed
/// from — DC, S at three biases and HB to 1e-9 — and an export missing N0, a fitted constituent or a fresh step refuses,
/// naming it, and leaves no file.
/// </summary>
public sealed class CmodelExportTests(ITestOutputHelper output)
{
    private static string Bench(double vgs, double vds) => $"""
        Port:P1 in 0 Num=1 Z=50 Ohm
        C:CB1 in g C=1e-6
        L:LB1 vg g L=1e-3
        Vdc:VG vg 0 V={vgs:R}
        Port:P2 out 0 Num=2 Z=50 Ohm
        C:CB2 out d C=1e-6
        L:LB2 vd d L=1e-3
        Vdc:VD vd 0 V={vds:R}

        """;

    private const string Drive = """
        V_1Tone:VS src 0 Vdc=-0.5 V=0.6 Freq=2e9 Phase=0
        R:RS src g R=50
        Vdc:VD vd 0 V=6
        R:RL vd d R=50
        analysis HB1 type=hb Tone=2e9 MaxHarm=5 Tol=1e-12

        """;

    [Fact]
    public void Exported_SimulatesAsTheTruth()
    {
        string cmc = ExportFixture.Document();
        string path = Path.Combine(Path.GetDirectoryName(cmc)!, "out", "fet.cmodel");
        var result = CmodelExport.Export(cmc, path, created: "2026-10-19T00:00:00Z");
        foreach (string line in result.Lines) output.WriteLine(line);
        string composed = $"ComposedFET:X1 g d 0 File=\"{path}\" N=4\n";

        var (a, b) = (Dc(Bench(-0.4, 6) + composed), Dc(Bench(-0.4, 6) + ExportFixture.Truth()));
        foreach (string node in (string[])["g", "d", "X1.di"])
            Assert.True(Math.Abs(a[node] - b[node]) <= 1e-9 * Math.Max(Math.Abs(b[node]), 1e-3), $"{node}: {a[node]:R} vs {b[node]:R}");

        double[] f = [0.5e9, 5e9, 20e9];
        foreach (var (vgs, vds) in new[] { (-1.5, 2.0), (-0.6, 6.0), (0.0, 10.0) })
        {
            var sa = SParameterEngine.Run(Elaborate(Bench(vgs, vds) + composed), f)["S"];
            var sb = SParameterEngine.Run(Elaborate(Bench(vgs, vds) + ExportFixture.Truth()), f)["S"];
            for (int k = 0; k < f.Length; k++)
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++)
                        Assert.True(((Complex)sa[k, i, j] - (Complex)sb[k, i, j]).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f[k]:G3} Hz, bias {vgs}/{vds}");
        }

        var (va, vb) = (Hb(Drive + composed), Hb(Drive + ExportFixture.Truth()));
        int ra = Row(va, "d"), rb = Row(vb, "d");
        for (int h = 0; h <= 5; h++)
        {
            Complex x = (Complex)va[ra, h], y = (Complex)vb[rb, h];
            Assert.True((x - y).Magnitude <= 1e-9 * Math.Max(y.Magnitude, 1e-3), $"d H{h}: {x} vs {y}");
        }
        Assert.True(((Complex)va[ra, 2]).Magnitude > 1e-3);
    }

    [Fact]
    public void Export_RefusesNamingWhatIsMissing_AndWritesNothing()
    {
        void Refused(string cmc, string expected)
        {
            string dir = Path.Combine(Path.GetDirectoryName(cmc)!, "out");
            string path = Path.Combine(dir, "fet.cmodel");
            var ex = Assert.Throws<ModelingException>(() => CmodelExport.Export(cmc, path));
            output.WriteLine(ex.Message);
            Assert.Contains(expected, ex.Message);
            Assert.False(Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any());
        }

        Refused(ExportFixture.Document(periphery: false), "N0");
        Refused(ExportFixture.Document(charge: false), "Charge: Qg and Qd are not fitted");

        // The data moved under the fitted steps: one point excluded after they ran.
        string stale = ExportFixture.Document();
        var doc = CmcFile.Read(stale);
        var set = doc.DataSets[0];
        CmcFile.Write(stale, doc with { DataSets = [set with { Exclusions = [new PointExclusion(set.Sources[0].Split('/')[1][..^4], "probe slipped")] }] });
        Refused(stale, "Current, Charge were fitted before the network or the data last changed");
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static Dictionary<string, double> Dc(string cnl)
    {
        var nl = Elaborate(cnl);
        var dc = NonlinearDcEngine.Run(nl);
        Assert.True(dc.Converged);
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        var (rows, names) = nl.Nodes.ResultRows(excludeInternal: false);
        for (int k = 0; k < rows.Length; k++) map[names[k]] = dc.NodeVoltages[rows[k] - 1];
        return map;
    }

    private static DataCube Hb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var p = HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().First(), nl.ResolvedGlobals);
        return ((DataSet)new HbEngine(nl, tb).Run(p))["V"];
    }

    private static int Row(DataCube v, string node)
    {
        int i = Array.FindIndex(v.Axes[0].Labels!, n => n == node);
        Assert.True(i >= 0, $"no node '{node}'");
        return i;
    }
}
