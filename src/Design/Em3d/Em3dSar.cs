// brief-sar-6 R-sar6-1 / R-sar6-2 — what a setup's Sar group makes of a problem: which solids are tissue (their density is
// written, so openEMS's raw SAR dump carries it), which the reported numbers are restricted to, which B1's mean is taken over,
// and the box the dumps cover. docs/design/sar.md §8.
//
// TISSUE IS A LOSSY DIELECTRIC THAT STATES A DENSITY. openEMS's raw dump carries ρ per cell, and every SAR tool, circuitRF's
// and sar_calc, treats ρ = 0 as background (air). So a cell is tissue exactly when its material was written with a density,
// and this file decides which materials are. A dielectric whose σ is zero (an FR4 board, a plastic housing) is never tissue,
// whatever density its record states: it dissipates nothing, and counting its mass would dilute every cube average next to
// it. A lossy dielectric with no density cannot be SAR's tissue — it would be counted as air — so one inside the region is
// REFUSED, naming the material (sar.md §7.7); one outside a stated region is written as background and the run says so.
//
// THE DUMPS ARE CROPPED to the tissue's bounding box plus one grid cell on every side (R-sar6-2): the cube search needs the
// background around the tissue's surface to judge a cube's air fraction, and nothing outside it holds SAR.

using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>brief-sar-6 — a Sar group resolved against one problem: everything the writer and the post-processing need.</summary>
/// <param name="Settings">The group, resolved.</param>
/// <param name="Densities">Each tissue solid's density, kg/m³, by solid name: what the writer writes.</param>
/// <param name="Region">The solids every reported number is restricted to.</param>
/// <param name="RegionName">What the region is called in a readout.</param>
/// <param name="B1Region">The solids B1's mean is taken over (empty when B1 is off).</param>
/// <param name="B1RegionName">What that region is called.</param>
/// <param name="Min">The tissue's bounding box, metres (before it is cropped to the grid).</param>
public sealed record Em3dSarPlan(SarSettings Settings, IReadOnlyDictionary<string, double> Densities, IReadOnlyList<string> Region,
                                 string RegionName, IReadOnlyList<string> B1Region, string B1RegionName, Point3 Min, Point3 Max)
{
    /// <summary>Things the run should say about the plan.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public static class Em3dSar
{
    /// <summary>The <paramref name="k"/>-th SAR frequency's raw dump (DumpType 29): its name, and so its file's stem.</summary>
    public static string RawDump(int k) => $"sar{k + 1}";

    /// <summary>The raw dump's file in a port's run directory.</summary>
    public static string RawDumpFile(int k) => RawDump(k) + ".h5";

    /// <summary>The <paramref name="k"/>-th SAR frequency's H dump (DumpType 11, VTK), for B1.</summary>
    public static string HDump(int k) => $"b1h{k + 1}";

    /// <summary>Bytes a raw SAR dump takes per cell and frequency: the E phasor (3 × complex64) and σ, ρ, V (3 × float32).
    /// Measured: the dipole's 83 × 90 × 91 cells are 24,505,463 bytes, 36.06 a cell.</summary>
    public const int RawBytesPerCell = 36;

    /// <summary>Bytes an openEMS VTK frequency-domain H dump takes per cell, at its peak: the magnitude and phase files (3
    /// float32 components each) and 21 fixed-phase snapshots (3 float32 each), uncompressed. The snapshots are removed after
    /// the run, as the radiation pattern's are.</summary>
    public const int HBytesPerCell = (2 + 21) * 12;

    /// <summary>Bytes circuitRF's own volumes take per cell: one float32 per quantity.</summary>
    public const int VolumeBytesPerQuantity = 4;

    /// <summary>Above this, the estimate is a warning: the run will write that much beside its fields.</summary>
    public const long WarnBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// <paramref name="settings"/> made of <paramref name="problem"/>: the tissue, the region, the B1 region and the box, or the
    /// sentence refusing it. <paramref name="phantoms"/> are the problem's phantoms and their layers' solids, outermost first
    /// (<c>Em3dGenerationResult.Phantoms</c>).
    /// </summary>
    public static (Em3dSarPlan? Plan, string? Refusal) Resolve(SarSettings settings, Em3dProblem problem,
                                                               IReadOnlyList<(string Phantom, IReadOnlyList<string> Layers)>? phantoms = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(problem);
        phantoms ??= [];
        if (settings.Problems() is { Count: > 0 } bad) return (null, string.Join(" ", bad));
        if (problem.Type != Em3dProblemType.Driven)
            return (null, $"The setup's Sar group asks for SAR, and a {problem.Type} problem has no driven field: SAR is a result of a driven run.");

        var materials = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        bool Lossy(Em3dSolid s) => s.Role == Em3dRole.Dielectric && materials.TryGetValue(s.Material, out var m) && m.SigmaSm > 0;
        var lossy = problem.Solids.Where(Lossy).ToList();
        if (lossy.Count == 0)
            return (null, "The setup asks for SAR, and no object is made of a lossy dielectric (a material with σ above zero): there is " +
                          "no tissue to absorb power. Add a phantom or a tissue object.");

        // ── the region: named objects (a phantom stands for its layers), or every lossy solid ──────────
        List<string> region;
        string regionName;
        if (settings.Region is { } named)
        {
            if (Expand(named, problem, phantoms, "Sar.Region", out var listed) is { } why) return (null, why);
            var notLossy = listed.Where(n => !lossy.Any(s => s.Name == n)).ToList();
            if (notLossy.Count > 0)
                return (null, $"Sar.Region names {Quote(notLossy)}, which {(notLossy.Count == 1 ? "is" : "are")} not made of a lossy dielectric " +
                              "(σ above zero), so no SAR is absorbed there. Name tissue objects or phantoms.");
            region = listed;
            regionName = string.Join(", ", named);
        }
        else
        {
            region = [.. lossy.Select(s => s.Name)];
            regionName = "all tissue";
        }

        var noDensity = region.Where(n => !problem.SolidDensities.ContainsKey(n)).ToList();
        if (noDensity.Count > 0)
        {
            var mats = noDensity.Select(n => problem.Solids.First(s => s.Name == n).Material).Distinct(StringComparer.Ordinal).ToList();
            return (null, $"SAR needs the density of every tissue, and {Quote(mats)} state{(mats.Count == 1 ? "s" : "")} none " +
                          $"({Quote(noDensity)}): openEMS would write {(noDensity.Count == 1 ? "it" : "them")} as air. Add DensityKgM3, " +
                          "with its source, to the material, or leave the object out of Sar.Region.");
        }

        // Tissue: every lossy solid that states a density, in or out of the region — a cube averages what surrounds its centre.
        var densities = new Dictionary<string, double>(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var s in lossy)
            if (problem.SolidDensities.TryGetValue(s.Name, out double rho) && rho > 0) densities[s.Name] = rho;
        var background = lossy.Where(s => !densities.ContainsKey(s.Name)).Select(s => s.Name).ToList();
        if (background.Count > 0)
            notes.Add($"{Quote(background)} {(background.Count == 1 ? "is" : "are")} lossy and outside Sar.Region with no density, so " +
                      $"{(background.Count == 1 ? "it is" : "they are")} written as background: SAR is not computed there and no cube counts its mass.");

        // ── B1's region: named, or the first phantom's innermost layer, or the SAR region ──────────────
        List<string> b1 = [];
        string b1Name = "";
        if (settings.B1)
        {
            if (settings.B1Region is { } b1Named)
            {
                if (Expand(b1Named, problem, phantoms, "Sar.B1Region", out var listed) is { } why) return (null, why);
                b1 = listed;
                b1Name = string.Join(", ", b1Named);
            }
            else if (phantoms.FirstOrDefault(p => p.Layers.Count > 0 && p.Layers.Any(densities.ContainsKey)) is { Phantom: not null } ph)
            {
                b1 = [ph.Layers[^1]];
                b1Name = ph.Layers[^1];
            }
            else
            {
                b1 = region;
                b1Name = regionName;
            }
        }

        // ── the box: every tissue solid's bounds ───────────────────────────────────────────────────────
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        foreach (var s in problem.Solids.Where(s => densities.ContainsKey(s.Name)))
        {
            var b = Em3dProblem.Bounds(s.Primitive);
            // brief-sar-8 — a voxelized layer reaches its voxels' faces, up to half a voxel past its smooth surface
            if (s.Voxel is { } v)
            {
                double Lo(double c, double a) => a + Math.Floor((c - a) / v.PitchM + 1e-9) * v.PitchM;
                double Hi(double c, double a) => a + Math.Ceiling((c - a) / v.PitchM - 1e-9) * v.PitchM;
                b = (Lo(b.X0, v.Anchor.X), Lo(b.Y0, v.Anchor.Y), Lo(b.Z0, v.Anchor.Z), Hi(b.X1, v.Anchor.X), Hi(b.Y1, v.Anchor.Y), Hi(b.Z1, v.Anchor.Z));
            }
            x0 = Math.Min(x0, b.X0); y0 = Math.Min(y0, b.Y0); z0 = Math.Min(z0, b.Z0);
            x1 = Math.Max(x1, b.X1); y1 = Math.Max(y1, b.Y1); z1 = Math.Max(z1, b.Z1);
        }
        return (new Em3dSarPlan(settings, densities, region, regionName, b1, b1Name, new Point3(x0, y0, z0), new Point3(x1, y1, z1))
                { Notes = notes }, null);
    }

    /// <summary>
    /// The dump box on <paramref name="grid"/>: the plan's box widened to the grid lines outside it, and one more cell on every
    /// side, inside the grid. Line indices, inclusive.
    /// </summary>
    public static (int I0, int I1, int J0, int J1, int K0, int K1) DumpLines(Em3dSarPlan plan, FdtdGridResult grid)
    {
        static (int, int) Span(IReadOnlyList<double> lines, double lo, double hi)
        {
            int a = 0;
            while (a + 1 < lines.Count && lines[a + 1] <= lo) a++;
            int b = lines.Count - 1;
            while (b - 1 >= 0 && lines[b - 1] >= hi) b--;
            return (Math.Max(0, a - 1), Math.Min(lines.Count - 1, Math.Max(b, a) + 1));
        }
        var (i0, i1) = Span(grid.X.Lines, plan.Min.X, plan.Max.X);
        var (j0, j1) = Span(grid.Y.Lines, plan.Min.Y, plan.Max.Y);
        var (k0, k1) = Span(grid.Z.Lines, plan.Min.Z, plan.Max.Z);
        return (i0, i1, j0, j1, k0, k1);
    }

    /// <summary>The dump box's corners, metres.</summary>
    public static (Point3 Min, Point3 Max) DumpBox(Em3dSarPlan plan, FdtdGridResult grid)
    {
        var (i0, i1, j0, j1, k0, k1) = DumpLines(plan, grid);
        return (new Point3(grid.X.Lines[i0], grid.Y.Lines[j0], grid.Z.Lines[k0]),
                new Point3(grid.X.Lines[i1], grid.Y.Lines[j1], grid.Z.Lines[k1]));
    }

    /// <summary>The cells inside the dump box.</summary>
    public static long DumpCells(Em3dSarPlan plan, FdtdGridResult grid)
    {
        var (i0, i1, j0, j1, k0, k1) = DumpLines(plan, grid);
        return (long)(i1 - i0) * (j1 - j0) * (k1 - k0);
    }

    /// <summary>
    /// R-sar6-2 — what a run's SAR dumps and volumes take on disk at their peak, for <paramref name="ports"/> driven ports:
    /// per port and SAR frequency, the raw dump, the H dump when B1 is on, and circuitRF's own volumes (local SAR, one per
    /// mass, and B1+ and B1− when B1 is on).
    /// </summary>
    public static long EstimateBytes(Em3dSarPlan plan, long cells, int ports)
    {
        long perFrequency = cells * RawBytesPerCell + (plan.Settings.B1 ? cells * HBytesPerCell : 0) +
                            cells * VolumeBytesPerQuantity * (1 + plan.Settings.MassesGrams.Count + (plan.Settings.B1 ? 2 : 0));
        // brief-sar-9 R-sar9-1 — the per-port fields beside the .sNp, when the group keeps them
        return perFrequency * plan.Settings.FrequenciesHz.Count * Math.Max(1, ports) + PortFieldSetWriter.EstimateBytes(plan, cells, Math.Max(1, ports));
    }

    /// <summary>The run's one line about the dumps: the box, its cells and the disk it takes; a warning above
    /// <see cref="WarnBytes"/>.</summary>
    public static (string Note, string? Warning) DiskNote(Em3dSarPlan plan, FdtdGridResult grid, int ports)
    {
        var (i0, i1, j0, j1, k0, k1) = DumpLines(plan, grid);
        long cells = (long)(i1 - i0) * (j1 - j0) * (k1 - k0);
        long bytes = EstimateBytes(plan, cells, ports);
        string note = $"SAR is dumped over the tissue's bounding box plus one cell: {i1 - i0} × {j1 - j0} × {k1 - k0} = {cells:N0} cells, " +
                      $"about {MachineMemory.Format(bytes)} on disk for {plan.Settings.FrequenciesHz.Count} frequenc" +
                      $"{(plan.Settings.FrequenciesHz.Count == 1 ? "y" : "ies")} and {ports} driven port{(ports == 1 ? "" : "s")}" +
                      (plan.Settings.B1 ? " with B1's H dump" : "") +
                      (plan.Settings.PerPortFields ? $", of which {MachineMemory.Format(PortFieldSetWriter.EstimateBytes(plan, cells, Math.Max(1, ports)))} " +
                                                     "is the per-port fields kept beside the .sNp" : "") + ".";
        string? warning = bytes > WarnBytes
            ? $"The SAR dumps will take about {MachineMemory.Format(bytes)} on disk. Fewer SAR frequencies, B1 off, or a coarser grid in the tissue would reduce it."
            : null;
        return (note, warning);
    }

    /// <summary>Each name as solids: a solid's own name, or a phantom's name for all its layers. Null, or the refusal naming
    /// what is neither.</summary>
    private static string? Expand(IReadOnlyList<string> names, Em3dProblem problem,
                                  IReadOnlyList<(string Phantom, IReadOnlyList<string> Layers)> phantoms, string key, out List<string> solids)
    {
        solids = [];
        var unknown = new List<string>();
        foreach (string raw in names)
        {
            string n = raw.Trim();
            if (problem.Solids.Any(s => s.Name == n)) { if (!solids.Contains(n)) solids.Add(n); continue; }
            if (phantoms.FirstOrDefault(p => p.Phantom == n) is { Phantom: not null } ph)
            {
                foreach (string l in ph.Layers) if (!solids.Contains(l)) solids.Add(l);
                continue;
            }
            unknown.Add(n);
        }
        if (unknown.Count == 0) return null;
        return $"{key} names {Quote(unknown)}, which {(unknown.Count == 1 ? "is" : "are")} not an object of the problem. Name an object or a " +
               "phantom that is modelled.";
    }

    private static string Quote(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

    internal static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}
