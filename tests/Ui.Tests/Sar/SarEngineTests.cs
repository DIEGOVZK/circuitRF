// ================================================================
//  SarEngineTests.cs — the gate for brief-sar-5: circuitRF's own SAR engine (CircuitRF.Engine.Em3d.Sar), its agreement
//  with openEMS's sar_calc on the same raw dump, the operation counts that hold its O(1)-per-cube cost, and the
//  sar_calc cross-check. The analytic cases are built here in C#; the sar_calc comparisons read SAR-1/SAR-4's fixed
//  data (developer machine only, skipped with a reason where absent) and start no openEMS process. docs/design/sar.md §7.
// ================================================================

using System.Numerics;
using CircuitRF.Design.Em3d;
using CircuitRF.Engine.Em3d.Sar;
using RfCore.Hdf5;
using Xunit;
using Npy = CircuitRF.Ui.Tests.Sar.SarReferenceSetTests.Npy;

namespace CircuitRF.Ui.Tests.Sar;

/// <summary>Builds small analytic grids: uniform cells of <c>cell</c> metres, a tissue predicate, and E and σ per cell.</summary>
internal static class SarBlocks
{
    public static SarGrid Build(int n, double cell, Func<int, int, int, (float Sigma, float Rho)> tissue, Func<int, int, int, Complex>? ez = null)
    {
        var axis = new SarAxis(0, Enumerable.Repeat(cell, n).ToArray());
        int count = n * n * n;
        var ex = new Complex[count]; var ey = new Complex[count]; var ezs = new Complex[count];
        var sigma = new float[count]; var rho = new float[count]; var vol = new float[count];
        for (int i = 0, c = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                for (int k = 0; k < n; k++, c++)
                {
                    (sigma[c], rho[c]) = tissue(i, j, k);
                    vol[c] = (float)(cell * cell * cell);
                    ezs[c] = ez?.Invoke(i, j, k) ?? Complex.One;
                }
        return SarGrid.FromCells(axis, axis, axis, ex, ey, ezs, sigma, rho, vol);
    }

    /// <summary>Tissue (σ, ρ) inside cells [lo, hi) on every axis, background elsewhere.</summary>
    public static Func<int, int, int, (float, float)> Block(int lo, int hi, float sigma, float rho) =>
        (i, j, k) => i >= lo && i < hi && j >= lo && j < hi && k >= lo && k < hi ? (sigma, rho) : (0f, 0f);
}

public sealed class SarLocalTests
{
    [Fact]
    public void UniformBlock_LocalSarIsSigmaESquaredOverTwoRho_AndTheTotalsFollow()
    {
        // openEMS's analytic case, built here: 4 × 4 × 4 cells of 1 mm, σ 0.5 S/m, ρ 1000 kg/m³, |E| 1 V/m peak.
        var grid = SarBlocks.Build(4, 1e-3, (_, _, _) => (0.5f, 1000f));
        var r = SarEngine.Compute(grid, new SarOptions { MassesGrams = [] });

        Assert.All(r.LocalSar, v => Assert.Equal(2.5e-4, v, 2.5e-4 * 1e-6));
        Assert.Equal(64 * 0.5 * 0.5 * 1e-9, r.AbsorbedPowerW, 1e-6 * r.AbsorbedPowerW);
        Assert.Equal(64e-9 * 1000, r.TissueMassKg, 1e-6 * r.TissueMassKg);
        Assert.Equal(2.5e-4, r.WholeSar, 2.5e-4 * 1e-6);
    }
}

public sealed class SarAveragingTests
{
    [Fact]
    public void UniformBlock_The10gAverageEqualsTheLocalValue_AndItsInteriorCubesAreValid()
    {
        // 40 mm of tissue in 1 mm cells with 2 mm of air around it; a 10 g cube at 1000 kg/m³ is 21.5 mm across.
        var grid = SarBlocks.Build(44, 1e-3, SarBlocks.Block(2, 42, 0.5f, 1000f));
        var a = SarEngine.Compute(grid, new SarOptions { MassesGrams = [10] }).For(10);

        int centre = grid.Index(22, 22, 22);
        Assert.Equal(SarCubeKind.Valid, a.Kinds[centre]);
        Assert.Equal(2.5e-4, a.Sar[centre], 2.5e-4 * 1e-6);
        Assert.Equal(10e-3, a.Peak!.Cube!.MassKg, 10e-3 * 1e-8);
        Assert.All(Enumerable.Range(0, grid.Count).Where(c => grid.Density[c] > 0), c => Assert.Equal(2.5e-4, a.Sar[c], 2.5e-4 * 1e-6));
    }

    [Fact]
    public void TwoLayerBlock_The1gAverageAcrossTheInterfaceIsTheLayersShares()
    {
        // ρ 1000 kg/m³ everywhere, so a 1 g cube is exactly 10 mm. σ 0.2 S/m for z < 20 mm, 0.6 S/m above; |E| = 1 V/m, so
        // local SAR is 1e-4 and 3e-4 W/kg. The cube about the first cell above the interface (centre z = 20.5 mm) spans
        // z 15.5–25.5 mm: 4.5 mm below, 5.5 mm above, and half-cells at each end on every axis (the partial-cell rule).
        var grid = SarBlocks.Build(40, 1e-3, (_, _, k) => (k < 20 ? 0.2f : 0.6f, 1000f));
        var a = SarEngine.Compute(grid, new SarOptions { MassesGrams = [1] }).For(1);

        int at = grid.Index(20, 20, 20);
        Assert.Equal(SarCubeKind.Valid, a.Kinds[at]);
        Assert.Equal((4.5 * 1e-4 + 5.5 * 3e-4) / 10, a.Sar[at], 1e-6 * 2e-4);
        Assert.Equal(3e-4, a.Sar[grid.Index(20, 20, 30)], 3e-4 * 1e-6);
    }

    [Fact]
    public void ACellAtATissueAirCorner_TakesACubeGrownFromOneOfItsFaces()
    {
        // The corner cell of a 30 mm block: its centred 1 g cube is 14 % air, no valid cube holds it wholly, so it is
        // unused and takes a face-grown cube. A gradient along x makes the six directions differ.
        var grid = SarBlocks.Build(34, 1e-3, SarBlocks.Block(2, 32, 0.5f, 1000f), (i, _, _) => 1 + 0.05 * i);
        var a = SarEngine.Compute(grid, new SarOptions { MassesGrams = [1] }).For(1);

        int corner = grid.Index(2, 2, 2);
        Assert.Equal(SarCubeKind.Unused, a.Kinds[corner]);
        Assert.True(a.Sar[corner] > 0);
        Assert.Equal(SarCubeKind.Valid, a.Kinds[grid.Index(17, 17, 17)]);
        Assert.Equal(0, a.Counts.NoCube);
    }
}

public sealed class SarOperationCountTests
{
    [Fact]
    public void CubeEvaluations_ScaleWithCellsTimesGrowthSteps_NotWithCubeVolume()
    {
        // The same 20 mm block at 1 mm and at 0.5 mm: a 1 g cube spans 8 times as many cells at the finer grid, but each
        // cube evaluation is a fixed number of table reads, so evaluations per cube search stay the same.
        var coarse = SarEngine.Compute(SarBlocks.Build(24, 1e-3, SarBlocks.Block(2, 22, 0.5f, 1000f)), new SarOptions { MassesGrams = [1] });
        var fine = SarEngine.Compute(SarBlocks.Build(48, 0.5e-3, SarBlocks.Block(4, 44, 0.5f, 1000f)), new SarOptions { MassesGrams = [1] });

        double perSearchCoarse = (double)coarse.Operations.MassEvaluations / coarse.Operations.CubeSearches;
        double perSearchFine = (double)fine.Operations.MassEvaluations / fine.Operations.CubeSearches;
        Assert.InRange(perSearchFine, 1, 1.5 * perSearchCoarse);
        Assert.InRange(perSearchCoarse, 1, 12);
    }
}

public sealed class SarAgainstSarCalcTests
{
    private static readonly string Root = OpenEmsHdf5Tests.Root;

    [SarHdf5SamplesFact]
    public void DipoleRawDump_10g_AgreesWithSarCalcOnTheSameFile()
    {
        var grid = OpenEmsSarGrid.Read(Path.Combine(Root, "Dipole_SAR", "SAR_raw_dump29.h5"));
        var theirs = OpenEmsHdf5.ReadSarResult(Path.Combine(Root, "Dipole_SAR", "SAR_10g.h5"));
        var r = SarEngine.Compute(grid, new SarOptions { MassesGrams = [10] });
        var ours = r.For(10);

        Assert.Equal(theirs.MaxSar, ours.Peak!.Value, theirs.MaxSar * 0.005);
        Assert.Equal(theirs.MaxSarIndex, new[] { ours.Peak.I, ours.Peak.J, ours.Peak.K });
        Assert.True(WithinOnePercent(ours.Sar, theirs.Sar) >= 0.99);
        Assert.Equal((theirs.ValidCubes, theirs.UsedCubes, theirs.UnusedCubes, theirs.AirCubes),
                     ((ulong)ours.Counts.Valid, (ulong)ours.Counts.Used, (ulong)(ours.Counts.Unused + ours.Counts.NoCube), (ulong)ours.Counts.Background));
        Assert.Equal(theirs.PowerW, r.AbsorbedPowerW, theirs.PowerW * 1e-5);
        Assert.Equal(theirs.MassKg, r.TissueMassKg, theirs.MassKg * 1e-5);
    }

    [SarReferenceFact]
    public void RandomPhantom_1g_WhereCubesReachTheGridsEdge_AgreesWithSarCalc()
    {
        // test_sar_analytic.py's 10 × 10 × 10 phantom holds 8 g: most cells' 1 g cubes are clipped or face-grown.
        string d = Path.Combine(Root, "analytic");
        double[] Lines(string a) => Npy.Read(Path.Combine(d, $"random_in_mesh_{a}_m.npy")).Real;
        SarAxis Axis(double[] c) => SarAxis.FromCentres(c, c.Select((_, i) => i + 1 < c.Length ? c[i + 1] - c[i] : c[i] - c[i - 1]).ToArray());
        float[] Floats(string name) => Npy.Read(Path.Combine(d, $"random_in_{name}.npy")).Real.Select(v => (float)v).ToArray();
        var e = Npy.Read(Path.Combine(d, "random_in_e.npy"));
        int n = e.Count / 3;
        var values = e.Values ?? e.Real.Select(v => new Complex(v, 0)).ToArray();
        var grid = SarGrid.FromCells(Axis(Lines("x")), Axis(Lines("y")), Axis(Lines("z")), values[..n], values[n..(2 * n)], values[(2 * n)..],
                                     Floats("conductivity"), Floats("density"), Floats("volume"));
        var theirs = Npy.Read(Path.Combine(d, "random_out_sar.npy")).Real.Select(v => (float)v).ToArray();

        var ours = SarEngine.Compute(grid, new SarOptions { MassesGrams = [1] }).For(1);
        Assert.Equal(theirs.Max(), ours.Peak!.Value, theirs.Max() * 0.005);
        Assert.True(WithinOnePercent(ours.Sar, theirs) >= 0.99);
    }

    /// <summary>The fraction of cells where either side has SAR that agree within 1 %.</summary>
    private static double WithinOnePercent(float[] ours, float[] theirs)
    {
        int n = 0, ok = 0;
        for (int c = 0; c < theirs.Length; c++)
        {
            if (ours[c] == 0 && theirs[c] == 0) continue;
            n++;
            if (Math.Abs(ours[c] - theirs[c]) <= 0.01 * Math.Abs(theirs[c])) ok++;
        }
        return (double)ok / n;
    }
}

public sealed class SarCalcCrossCheckTests
{
    private static readonly string RawDump = Path.Combine(OpenEmsHdf5Tests.Root, "Dipole_SAR", "SAR_raw_dump29.h5");

    [SarCalcFact]
    public void CrossCheckOn_RunsTheInstalledSarCalc_AndReportsBothPeaksWithNoWarning()
    {
        var openEms = SolverDiscovery.OpenEms.Find(out _)!;
        var ours = SarEngine.Compute(OpenEmsSarGrid.Read(RawDump), new SarOptions { MassesGrams = [10] });
        string work = Path.Combine(Path.GetTempPath(), $"crf-sar5-{Guid.NewGuid():N}");
        try
        {
            var check = OpenEmsSarCalc.CrossCheck(true, openEms, RawDump, ours, SarMethod.Cube, work);
            var row = Assert.Single(check.Rows);
            Assert.NotNull(row.SarCalcPeak);
            Assert.InRange(row.RelativeDifference!.Value, 0, OpenEmsSarCalc.WarningDifference);
            Assert.Empty(check.Warnings);
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
    }

    [Fact]
    public void CrossCheckOn_WithNoSarCalcBesideOpenEms_IsANote()
    {
        var grid = SarBlocks.Build(4, 1e-3, (_, _, _) => (0.5f, 1000f));
        var ours = SarEngine.Compute(grid, new SarOptions { MassesGrams = [0.01] });
        string dir = Path.Combine(Path.GetTempPath(), $"crf-sar5-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lone = new SolverInstallation(SolverTool.OpenEms, Path.Combine(dir, "openEMS"), "305df56", "c46e320", "",
                                              SolverHowFound.Settings, "set in Settings", "0.37.0");
            long before = OpenEmsSarCalc.Invocations;
            var check = OpenEmsSarCalc.CrossCheck(true, lone, "unused.h5", ours, SarMethod.Cube, dir);
            Assert.Contains("not cross-checked", Assert.Single(check.Notes));
            Assert.Empty(check.Warnings);
            Assert.Equal(before, OpenEmsSarCalc.Invocations);
            Assert.Same(SarCrossCheck.None, OpenEmsSarCalc.CrossCheck(false, lone, "unused.h5", ours, SarMethod.Cube, dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>Skips unless SAR-4's raw dipole dump is here and the installed openEMS has sar_calc beside it.</summary>
public sealed class SarCalcFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(() =>
    {
        if (!File.Exists(Path.Combine(OpenEmsHdf5Tests.Root, "Dipole_SAR", "SAR_raw_dump29.h5")))
            return "needs the SAR-4 raw dipole dump, which is not in the repository: testdata/sar/openems-ref/README.md says how to make it";
        var openEms = SolverDiscovery.OpenEms.Find(out _);
        return openEms is null ? "openEMS is not installed here"
             : SolverDiscovery.SarCalcBeside(openEms) is null ? $"there is no sar_calc beside {openEms.Where}" : null;
    });

    public SarCalcFactAttribute()
    {
        if (Reason.Value is { } why) Skip = why;
    }
}
