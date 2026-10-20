using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using static CircuitRF.Engine.Tests.Composed.ComposedFetEquivalenceTests;
using static CircuitRF.Engine.Tests.Composed.TrapTestModels;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// brief-modelcomposer-21 R-mc21-4: a lag switched off removes its trap states — each follows its DC value at the
/// instantaneous voltages and mints no node — and the switch is per instance and sweepable.
/// </summary>
public sealed class TrapSwitchTests
{
    private static readonly string DrainPath = ComposedTestModels.WriteTemp(DrainLag());
    private static readonly string BothPath = ComposedTestModels.WriteTemp(With(new ModelTraps
    {
        Model = CaptureEmissionTraps.ModelName, States = [CaptureEmissionTraps.GateLag, CaptureEmissionTraps.DrainLag],
    }));

    /// <summary>The truth: the same drain current with its trap input fed the instantaneous <c>vds</c>, and no traps.</summary>
    private static readonly string TruthPath = ComposedTestModels.WriteTemp(Instantaneous());

    private static ComposedModel Instantaneous()
    {
        var m = ComposedTestModels.Model();
        return m with { Core = m.Core with { Id = Constituent.Equation(IdTrapEq.Replace("phi2", "vds"), ["vgs", "vds"], IdTrapParams) } };
    }

    private static string Fet(string path, string switches = "", string name = "X1")
        => $"ComposedFET:{name} g d 0 File=\"{path}\" {switches}\n";

    private static ComposedCoreModel Core(ElaboratedNetlist nl, string fet = "X1")
        => nl.Components.Where(c => c.InstancePath.StartsWith(fet + ".", StringComparison.Ordinal))
                        .Select(c => c.Model).OfType<ComposedCoreModel>().Single();

    [Fact]
    public void BothLagsOff_RemoveEveryTrapNode_AndLeaveDcUnchanged()
    {
        string bench = Bench(-0.6, 6);
        var nl = Elaborate(bench + Fet(BothPath, "GateLag=false DrainLag=false"));
        Assert.Equal(0, Core(nl).InternalStateNodeCount);
        Assert.DoesNotContain(Enumerable.Range(1, nl.Nodes.Count - 1), i => nl.Nodes.NameOf(i)?.StartsWith("__composed_") == true);

        var a = Dc(bench + Fet(BothPath));
        var b = Dc(bench + Fet(BothPath, "GateLag=false DrainLag=false"));
        foreach (string node in (string[])["X1.gi", "X1.di", "X1.n1", "X1.n3"])
            Assert.True(Math.Abs(a[node] - b[node]) <= 1e-12 * Math.Max(Math.Abs(a[node]), 1e-3), $"{node}: {a[node]:R} vs {b[node]:R}");
    }

    [Fact]
    public void GateLagOffAlone_RemovesPhi1_AndKeepsPhi2()
    {
        var core = Core(Elaborate(Bench(-0.6, 6) + Fet(BothPath, "GateLag=false")));
        Assert.Equal(1, core.InternalStateNodeCount);
        Assert.Contains("phi2", core.TerminalNames);
        Assert.DoesNotContain("phi1", core.TerminalNames);
    }

    [Fact]
    public void DrainLagOff_OneHbPoint_IsTheInstantaneousTruth()
    {
        string drive = DrainDrive(-0.6, 6, 4, 1e6, 6);
        var a = HbV(drive + Fet(DrainPath, "DrainLag=false"));
        var b = HbV(drive + Fet(TruthPath));
        string[] nodes = a.Axes[0].Labels!;
        Assert.Equal(nodes, b.Axes[0].Labels!);
        for (int i = 0; i < nodes.Length; i++)
            for (int h = 0; h < a.Axes[1].Length; h++)
            {
                Complex x = (Complex)a[i, h], y = (Complex)b[i, h];
                Assert.True((x - y).Magnitude <= 1e-9 * Math.Max(y.Magnitude, 1e-3), $"{nodes[i]} H{h}: {x} vs {y}");
            }
    }

    [Fact]
    public void TwoInstancesOfOneModel_KeepTheirOwnSwitches()
    {
        var nl = Elaborate(Bench(-0.6, 6) + Fet(DrainPath) + Fet(DrainPath, "DrainLag=false", "X2"));
        Assert.Equal(1, Core(nl, "X1").InternalStateNodeCount);
        Assert.Equal(0, Core(nl, "X2").InternalStateNodeCount);
    }

    [Fact]
    public void ASweepOverAVariableBoundToTheSwitch_GivesTwoResults()
    {
        var (lib, tb) = new CnlReader().Read("dl = 1\n" + DrainDrive(-0.6, 6, 4, 1e6, 6) + Fet(DrainPath, "DrainLag=dl")
                                             + "analysis SW1 type=parametric_sweep Var=dl Values=1,0 Inner=HB1\n");
        var v = ParametricSweepEngine.Run(tb.Analyses.OfType<ParametricSweepAnalysis>().Single(), lib, tb)["V"];
        int di = Array.FindIndex(v.Axes[1].Labels!, n => n == "X1.di");
        Assert.True(di >= 0);
        double lagged = ((Complex)v[0, di, 0]).Real, instant = ((Complex)v[1, di, 0]).Real;
        Assert.True(Math.Abs(lagged - instant) > 1e-3, $"V(X1.di) DC: {lagged:R} with the lag, {instant:R} without");
    }

    private static DataCube HbV(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var r = new HbEngine(nl, tb).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals));
        Assert.True(r.Converged);
        return ((DataSet)r)["V"];
    }
}
