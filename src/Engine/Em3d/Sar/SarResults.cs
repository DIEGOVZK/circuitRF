// brief-sar-5 R-sar5-3/R-sar5-4 — what the SAR engine is asked for and what it returns. Numbers only: masses, a method,
// a region given as cells, and arrays in the grid's own C order over (x, y, z).

namespace CircuitRF.Engine.Em3d.Sar;

/// <summary>How an averaging cube is chosen (R-sar5-3; docs/design/sar.md §7).</summary>
public enum SarMethod
{
    /// <summary>The cube-averaging procedure of IEC/IEEE 62704-1 as openEMS's <c>sar_calc</c> implements it
    /// (<c>--method IEEE_62704</c>): a centred cube is valid only when it stays in the grid, holds under 10 % background
    /// and touches tissue on all six faces; other tissue cells take a containing valid cube's value, then a cube grown
    /// from one face.</summary>
    Cube,

    /// <summary>No background rule (<c>sar_calc --method SIMPLE</c>): every tissue cell takes the centred cube of the
    /// mass, clipped to the grid where it reaches an edge.</summary>
    Simple,
}

/// <summary>What the engine computes. Masses are in grams; any positive mass is allowed.</summary>
public sealed record SarOptions
{
    public IReadOnlyList<double> MassesGrams { get; init; } = [1, 10];
    public SarMethod Method { get; init; } = SarMethod.Cube;

    /// <summary>The cells every reported number is restricted to (R-sar5-4). Averaging itself always uses every tissue
    /// cell, since a cube centred in the region may reach tissue outside it.</summary>
    public SarRegion Region { get; init; } = SarRegion.All;

    /// <summary>The most threads to use; 0 is every core.</summary>
    public int MaxDegreeOfParallelism { get; init; }
}

/// <summary>
/// A set of cells (R-sar5-4): every cell, an index box, or a per-cell mask the caller built — a named phantom or tissue
/// set is a mask its caller resolves, so head-only SAR is a region, not another code path.
/// </summary>
public sealed class SarRegion
{
    private readonly bool[]? _mask;
    private readonly (int I0, int I1, int J0, int J1, int K0, int K1)? _box;

    /// <summary>What the region is called in a readout.</summary>
    public string Name { get; }

    private SarRegion(string name, bool[]? mask, (int, int, int, int, int, int)? box)
    {
        Name = name; _mask = mask; _box = box;
    }

    /// <summary>Every cell.</summary>
    public static SarRegion All { get; } = new("all tissue", null, null);

    /// <summary>The cells i ∈ [i0, i1), j ∈ [j0, j1), k ∈ [k0, k1).</summary>
    public static SarRegion Box(int i0, int i1, int j0, int j1, int k0, int k1, string name = "box")
    {
        if (i1 <= i0 || j1 <= j0 || k1 <= k0) throw new ArgumentException("A region box holds at least one cell.");
        return new(name, null, (i0, i1, j0, j1, k0, k1));
    }

    /// <summary>The cells whose centres lie in the box [lo, hi] (metres).</summary>
    public static SarRegion Box(SarGrid grid, double[] loM, double[] hiM, string name = "box")
    {
        ArgumentNullException.ThrowIfNull(grid);
        var (i0, i1) = Range(grid.X, loM[0], hiM[0]);
        var (j0, j1) = Range(grid.Y, loM[1], hiM[1]);
        var (k0, k1) = Range(grid.Z, loM[2], hiM[2]);
        if (i1 <= i0 || j1 <= j0 || k1 <= k0)
            throw new ArgumentException($"The region '{name}' holds no cell centre.");
        return new(name, null, (i0, i1, j0, j1, k0, k1));
    }

    /// <summary>The cells where <paramref name="cells"/> is true (one entry per grid cell).</summary>
    public static SarRegion Mask(bool[] cells, string name)
    {
        ArgumentNullException.ThrowIfNull(cells);
        return new(name, cells, null);
    }

    internal bool IsAll => _mask is null && _box is null;

    internal void Check(SarGrid grid)
    {
        if (_mask is not null && _mask.Length != grid.Count)
            throw new ArgumentException($"The region '{Name}' has {_mask.Length} entries for a grid of {grid.Count} cells.");
        if (_box is { } b && (b.I0 < 0 || b.J0 < 0 || b.K0 < 0 || b.I1 > grid.Nx || b.J1 > grid.Ny || b.K1 > grid.Nz))
            throw new ArgumentException($"The region '{Name}' reaches outside the grid.");
    }

    internal bool Contains(int index, int i, int j, int k) =>
        _box is { } b ? i >= b.I0 && i < b.I1 && j >= b.J0 && j < b.J1 && k >= b.K0 && k < b.K1
                      : _mask is null || _mask[index];

    private static (int, int) Range(SarAxis axis, double lo, double hi)
    {
        int a = 0, b = 0;
        while (a < axis.Count && axis.Centre(a) < lo) a++;
        b = a;
        while (b < axis.Count && axis.Centre(b) <= hi) b++;
        return (a, b);
    }
}

/// <summary>How a cell got its averaged value.</summary>
public enum SarCubeKind : byte
{
    /// <summary>Background (ρ = 0): no SAR.</summary>
    Background = 0,
    /// <summary>The centre of a valid cube; its value is that cube's average.</summary>
    Valid = 1,
    /// <summary>Not the centre of a valid cube but wholly inside one or more; it takes the largest of their averages.</summary>
    Used = 2,
    /// <summary>Neither: it takes a cube grown from one of its faces (the largest average among the smallest such cubes).</summary>
    Unused = 3,
    /// <summary>Unused, and no cube of the mass could be grown from any face: its value is zero.</summary>
    NoCube = 4,
}

/// <summary>An averaging cube, in metres, and the mass it holds.</summary>
public sealed record SarCube(double[] LoM, double[] HiM, double MassKg)
{
    public double EdgeM => HiM[0] - LoM[0];
}

/// <summary>Where a peak is: its value, its cell, the cell's centre (metres), how it got its value and the cube it came
/// from (null for local SAR).</summary>
public sealed record SarPeak(double Value, int I, int J, int K, double XM, double YM, double ZM, SarCubeKind Kind, SarCube? Cube);

/// <summary>The averaged-cube statistics: how many cells got their value each way.</summary>
public sealed record SarCubeCounts(long Valid, long Used, long Unused, long NoCube, long Background)
{
    public long Tissue => Valid + Used + Unused + NoCube;
}

/// <summary>One mass's spatial-average SAR.</summary>
/// <param name="Sar">W/kg per cell (float32, the grid's order); zero in background.</param>
/// <param name="Kinds">How each cell got its value.</param>
/// <param name="Peak">The largest value in the region, or null when the region holds no tissue.</param>
/// <param name="Counts">Over the whole grid.</param>
public sealed record SarAverage(double MassGrams, float[] Sar, SarCubeKind[] Kinds, SarPeak? Peak, SarCubeCounts Counts);

/// <summary>
/// The algorithm's own operation counts (R-sar5-5) — what <c>SarOperationCountTests</c> holds, rather than a timing.
/// </summary>
/// <param name="CubeSearches">Searches for a cube of the mass about one cell (one per tissue cell, six per unused one).</param>
/// <param name="MassEvaluations">Evaluations of a cube's mass, power and volume: each is a fixed number of table reads,
/// whatever the cube's size.</param>
/// <param name="PaintedRows">Grid rows the used-cell pass walked.</param>
public sealed record SarOperationCounts(long CubeSearches, long MassEvaluations, long PaintedRows);

/// <summary>Everything the engine computed (R-sar5-4). Every scalar is over <see cref="Region"/>.</summary>
/// <param name="LocalSar">σ|E|²/(2ρ) per cell, W/kg (float32); zero in background.</param>
/// <param name="AbsorbedPowerW">Σ ½σ|E|²V over the region's tissue.</param>
/// <param name="TissueMassKg">Σ ρV over the region.</param>
/// <param name="UncountedPowerW">Power dissipated in background cells anywhere in the grid (<see cref="SarGrid.UncountedPowerW"/>).</param>
public sealed record SarResult(
    string Region, float[] LocalSar, SarPeak? LocalPeak, double AbsorbedPowerW, double TissueMassKg, double UncountedPowerW,
    IReadOnlyList<SarAverage> Averages, SarOperationCounts Operations)
{
    /// <summary>Whole-region SAR, P_abs / mass, W/kg (zero for a region with no tissue).</summary>
    public double WholeSar => TissueMassKg > 0 ? AbsorbedPowerW / TissueMassKg : 0;

    /// <summary>The average for <paramref name="grams"/>.</summary>
    public SarAverage For(double grams) => Averages.First(a => a.MassGrams == grams);
}
