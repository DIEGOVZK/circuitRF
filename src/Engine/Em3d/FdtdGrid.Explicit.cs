// brief-sar-3 R-sar3-5 — the FDTD grid on lines the setup gives (OpenEms.GridLines), as an openEMS script's own mesh is.
//
// WHY. circuitRF's grid and openEMS's SmoothMeshLines place different lines, so a replication of an openEMS example on
// circuitRF's grid differs from the reference for a reason that has nothing to do with what is being compared (sar.md §3,
// overview §7). Running on the reference's own lines removes that difference first; it is also how an openEMS script is
// ported with its mesh.
//
// WHAT CHANGES. The lines are exactly the given ones: none is collected, merged, filled or added for a PML. The simulated
// domain is the grid's extent, as it is in openEMS, and each face's boundary is applied at the grid's outermost line; a PML
// (PmlCells) takes the outermost cells of the given grid, as openEMS's PML_n does. So the air box must lie inside the grid:
// anything beyond it would not be simulated, and that is refused rather than dropped.
//
// WHAT IS REFUSED, each with a sentence naming the feature and the lines either side of it:
//   * a lumped port's or lumped element's sheet whose plane is not on a line, whose two ends along its direction land on one
//     line (openEMS skips an element of zero snapped length), or whose width holds no line (it would snap off where it is
//     drawn). openEMS snaps a lumped element's box to the nearest lines, so an extent between lines is accepted when the
//     element still spans the same edges — the reference examples' own ports sit a few µm off their lines;
//   * a sheet whose plane is not on a line (brief-em3d-123's fixed lines: openEMS would move it to the nearest one);
//   * a wave port, whose feed is a lattice of lines only circuitRF's own grid places.

using System.Globalization;

namespace CircuitRF.Engine.Em3d;

public static partial class FdtdGrid
{
    /// <summary>A coordinate is on a given line within this fraction of the cells beside it: the reference XMLs write their
    /// coordinates to seven significant figures, a few parts per million of a cell off the line they were placed on.</summary>
    public const double GivenLineTolerance = 1e-3;

    private static FdtdGridResult BuildExplicit(Em3dProblem problem, OpenEmsGridSettings settings, FdtdExplicitLines given,
                                                long? availableMemoryBytes)
    {
        var ctx = new Context(problem, settings);
        var axes = new FdtdAxisGrid[3];
        foreach (var a in new[] { FdtdAxis.X, FdtdAxis.Y, FdtdAxis.Z })
        {
            var lines = given.Axis(a);
            double best = double.PositiveInfinity, at = lines[0];
            for (int i = 1; i < lines.Count; i++)
                if (lines[i] - lines[i - 1] < best) { best = lines[i] - lines[i - 1]; at = lines[i - 1]; }
            axes[(int)a] = new FdtdAxisGrid(a, lines, [], best, at, [new FdtdLineSource("OpenEms.GridLines", FdtdLineKind.Given, at)],
                                            ctx.BoxMin(a) - lines[0], lines[^1] - ctx.BoxMax(a));
        }
        var (x, y, z) = (axes[0], axes[1], axes[2]);
        long cells = checked((long)x.Lines.Count * y.Lines.Count * z.Lines.Count);
        double dt = CourantTimeStep(x.SmallestCellM, y.SmallestCellM, z.SmallestCellM);
        double pulse = ExcitationLength(problem.Frequency);
        long steps = (long)Math.Ceiling(pulse * (1 + RingDownPulses) / dt);
        long memory = Em3dSizeEstimate.OpenEmsMemoryBytes(cells);
        long limit = availableMemoryBytes ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var result = new FdtdGridResult(x, y, z, ctx.MinCell, cells, dt, pulse, steps, memory, [], [], null);
        string? refusal = GivenLinesRefusal(problem, result, given);
        return refusal is not null ? result with { Refusal = refusal }
             : memory > limit ? result with { Refusal = MemoryRefusal(result, limit) } : result;
    }

    /// <summary>Why <paramref name="problem"/> cannot run on <paramref name="grid"/>'s given lines, or null when it can.</summary>
    private static string? GivenLinesRefusal(Em3dProblem problem, FdtdGridResult grid, FdtdExplicitLines given)
    {
        string from = $"the lines OpenEms.GridLines gives ({given.Source})";
        if (problem.HasWavePorts)
            return $"This problem has wave ports, and it runs on {from}: a wave port's feed is a lattice of lines circuitRF's own grid " +
                   "places. Remove OpenEms.GridLines, or make the ports lumped.";
        var b = problem.Boundary;
        foreach (var a in new[] { FdtdAxis.X, FdtdAxis.Y, FdtdAxis.Z })
        {
            var lines = grid.Axis(a).Lines;
            double tol = CoincidenceTolerance(lines[^1] - lines[0]);
            double lo = A3(b.Min, a), hi = A3(b.Max, a);
            if (lo < lines[0] - tol || hi > lines[^1] + tol)
                return $"The air box runs from {AxisName(a)} = {FormatLength(lo)} to {FormatLength(hi)}, beyond {from}, which end at " +
                       $"{FormatLength(lines[0])} and {FormatLength(lines[^1])}: openEMS simulates the grid's extent only, so content " +
                       "outside it would be lost. Shrink the air box (the setup's AirBox padding) to lie inside the grid, or give lines " +
                       "that cover it.";
        }

        foreach (var p in problem.Ports.Where(p => p.Kind == Em3dPortKind.Lumped))
            if (SheetOffLines($"Port {p.Number}", p.Min, p.Max, p.Direction, grid, from) is { } why) return why;
        foreach (var le in problem.LumpedElements)
            if (SheetOffLines($"Lumped element '{le.Name}'", le.Min, le.Max, le.Direction, grid, from) is { } why) return why;

        foreach (var sh in problem.Sheets)
        {
            int? normal = sh.Frame is { } fr ? fr.NormalAxis : 2;
            if (normal is not int n) continue;                  // an oblique sheet is staircased, and the run warns
            var (x0, y0, z0, _, _, _) = sh.WorldBounds();
            double at = n == 0 ? x0 : n == 1 ? y0 : z0;
            var lines = grid.Axis((FdtdAxis)n).Lines;
            if (!OnLine(lines, at))
                return $"Sheet '{sh.Name}' lies at {AxisName((FdtdAxis)n)} = {FormatLength(at)}, {Between(lines, at)} of {from}, and a sheet " +
                       "needs a line on its plane (openEMS would move it to the nearer one). Add a line there, or move the sheet.";
        }
        return null;
    }

    /// <summary>Why a lumped sheet cannot be written on the given lines, or null: its plane on a line, its two ends along its
    /// direction on two different lines, and a line across its width.</summary>
    private static string? SheetOffLines(string who, Point3 min, Point3 max, Point3 dir, FdtdGridResult grid, string from)
    {
        double[] d = [dir.X, dir.Y, dir.Z];
        for (int k = 0; k < 3; k++)
        {
            var a = (FdtdAxis)k;
            var lines = grid.Axis(a).Lines;
            double lo = A3(min, a), hi = A3(max, a);
            if (hi == lo)
            {
                if (!OnLine(lines, lo))
                    return $"{who}'s sheet lies at {AxisName(a)} = {FormatLength(lo)}, {Between(lines, lo)} of {from}: its plane needs a " +
                           "line. Add a line there, or move it onto one.";
            }
            else if (Math.Abs(d[k]) > 0.5)
            {
                if (SnapToLine(lines, lo) == SnapToLine(lines, hi) || NearestIndex(lines, lo) == NearestIndex(lines, hi))
                    return $"{who} runs along {AxisName(a)} from {FormatLength(lo)} to {FormatLength(hi)}, and both ends land on the one " +
                           $"line at {FormatLength(lines[NearestIndex(lines, lo)])} of {from}: it spans no cell, and openEMS would skip it. " +
                           "Add a line at each end of its gap.";
            }
            else
            {
                double tol = CoincidenceTolerance(lines[^1] - lines[0]);
                if (!lines.Any(l => l >= lo - tol && l <= hi + tol))
                    return $"{who}'s width from {AxisName(a)} = {FormatLength(lo)} to {FormatLength(hi)} lies {Between(lines, lo)} of {from}, " +
                           "with no line across it: openEMS would move it onto the nearer line. Add a line inside its width.";
            }
        }
        return null;
    }

    private static bool OnLine(IReadOnlyList<double> lines, double at)
    {
        int i = NearestIndex(lines, at);
        double cell = Math.Min(i > 0 ? lines[i] - lines[i - 1] : double.PositiveInfinity,
                               i < lines.Count - 1 ? lines[i + 1] - lines[i] : double.PositiveInfinity);
        return Math.Abs(lines[i] - at) <= Math.Max(GivenLineTolerance * cell, CoincidenceTolerance(lines[^1] - lines[0]));
    }

    private static int NearestIndex(IReadOnlyList<double> lines, double at)
    {
        int best = 0;
        for (int i = 1; i < lines.Count; i++) if (Math.Abs(lines[i] - at) < Math.Abs(lines[best] - at)) best = i;
        return best;
    }

    private static string Between(IReadOnlyList<double> lines, double at)
    {
        int above = 0;
        while (above < lines.Count && lines[above] < at) above++;
        if (above == 0) return $"below the first line ({FormatLength(lines[0])})";
        if (above == lines.Count) return $"above the last line ({FormatLength(lines[^1])})";
        return $"between the lines at {FormatLength(lines[above - 1])} and {FormatLength(lines[above])}";
    }

    /// <summary>The sentence a run carries for a grid on given lines.</summary>
    public static string GivenLinesNote(FdtdExplicitLines given, FdtdGridResult grid)
        => $"openEMS runs on the {N(grid.X.Lines.Count)} × {N(grid.Y.Lines.Count)} × {N(grid.Z.Lines.Count)} lines OpenEms.GridLines gives " +
           $"({given.Source}); circuitRF placed none of its own, so the simulated domain is the grid's extent and each face's boundary is " +
           "applied at its outermost line, as openEMS applies it.";

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}
