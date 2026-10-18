// brief-sar-9 R-sar9-2 / R-sar9-4 — the field of any excitation of an N-port, as the weighted sum of its N per-port fields:
// E = Σ_k a_k·E_k and H = Σ_k a_k·H_k, where E_k and H_k are port k's fields per unit incident wave (1 √W) with every other
// port matched, and a_k is the incident wave the excitation delivers to port k. Numbers only (overview L5): nothing here knows
// which solver wrote the fields, where they are stored, or what circuit chose the weights. src/Design reads a field set
// beside an .sNp and hands it here as an IPortFieldSet.
//
// STREAMED. A field set is N × 48 bytes a cell (E and H, three complex64 components each), which for a whole body at 2 mm is
// gigabytes per port, so nothing here holds a port's field whole. The full path reads one x slab of every port at a time,
// sums it, and hands the sum to SarGridBuilder, which reduces E to ½σ|E|² on arrival — so the summed field is never whole
// either. Local SAR and B1 are therefore O(N × cells), and averaged SAR is SAR-5's engine on the summed grid (R-sar9-4).
//
// THE FAST PATH reads only the cells of a box — one clip plane is a box one cell thick — so its cost is O(N × box cells):
// what interactive tuning needs (SAR-10). It gives local SAR and B1± there, never an average, because a 10 g cube reaches
// cells outside any plane.
//
// THE TISSUE MATRIX Q (N × N, Hermitian) is the absorbed power as a quadratic form, P_abs = aᴴ·Q·a, Q_mn = Σ ½σV·(E_m*·E_n)
// over the tissue cells (ρ > 0). It is computed once, when a field set is written, and lets a caller check a recombination's
// absorbed power against the field's own (R-sar9-5) and the S-matrix's dissipation, I − SᴴS, which must exceed it.

using System.Numerics;

namespace CircuitRF.Engine.Em3d.Sar;

/// <summary>
/// brief-sar-9 — N ports' fields over one Cartesian grid of cells, each per unit incident wave at its port with every other
/// port matched, and the cells' σ, ρ and volume. Cells are in C order over (x, y, z), z fastest; a field read returns, per
/// cell, the three components as (re, im) float pairs: six floats a cell.
/// </summary>
public interface IPortFieldSet
{
    SarAxis X { get; }
    SarAxis Y { get; }
    SarAxis Z { get; }

    /// <summary>The ports, in the order the weights are given.</summary>
    int PortCount { get; }

    /// <summary>Whether the set carries H (B1 needs it).</summary>
    bool HasH { get; }

    /// <summary>Port <paramref name="port"/>'s E (V/m per √W) over cells [<paramref name="cellStart"/>, + <paramref name="cellCount"/>):
    /// <c>6·cellCount</c> floats into <paramref name="destination"/>.</summary>
    void ReadE(int port, long cellStart, int cellCount, Span<float> destination);

    /// <summary>Port <paramref name="port"/>'s H (A/m per √W), as <see cref="ReadE"/>.</summary>
    void ReadH(int port, long cellStart, int cellCount, Span<float> destination);

    /// <summary>σ (S/m), ρ (kg/m³, zero for background) and the volume (m³) of the same cells.</summary>
    void ReadCells(long cellStart, int cellCount, Span<float> conductivity, Span<float> density, Span<float> volume);
}

/// <summary>A box of cells, i ∈ [I0, I1), j ∈ [J0, J1), k ∈ [K0, K1). A clip plane is a box one cell thick.</summary>
public readonly record struct FieldBox(int I0, int I1, int J0, int J1, int K0, int K1)
{
    public int Nx => I1 - I0;
    public int Ny => J1 - J0;
    public int Nz => K1 - K0;
    public long Count => (long)Nx * Ny * Nz;

    /// <summary>The plane of cells with index <paramref name="index"/> along <paramref name="axis"/> (0 x, 1 y, 2 z).</summary>
    public static FieldBox Plane(SarAxis x, SarAxis y, SarAxis z, int axis, int index) => axis switch
    {
        0 => new(index, index + 1, 0, y.Count, 0, z.Count),
        1 => new(0, x.Count, index, index + 1, 0, z.Count),
        2 => new(0, x.Count, 0, y.Count, index, index + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "A plane's axis is 0 (x), 1 (y) or 2 (z)."),
    };
}

/// <summary>What one recombination read: cells, port slabs and the ports that took part. The proof R-sar9-4 asks for that the
/// cost is O(N × cells) and that nothing else ran.</summary>
public sealed record RecombinationCounts(long CellsRead, long PortReads, int PortsUsed);

/// <summary>brief-sar-9 — the summed field's grid (½σ|E|², ρ, V per cell — SAR-5's input), and |B1+| and |B1−| per cell (T) when
/// the set carries H and B1 was asked for.</summary>
public sealed record RecombinedField(SarGrid Grid, float[]? B1Plus, float[]? B1Minus, RecombinationCounts Counts);

/// <summary>brief-sar-9 — the fast path's answer over one box: local SAR (W/kg) and |B1±| (T) per box cell, C order over the box.</summary>
public sealed record RecombinedBox(FieldBox Box, float[] LocalSar, float[]? B1Plus, float[]? B1Minus, RecombinationCounts Counts);

/// <summary>brief-sar-10 — a region's sums from the fast path: |B1+| (T) volume-weighted over the region's cells, and the
/// absorbed power (W) and mass (kg) of its tissue cells, all as solved at the weights given.</summary>
public sealed record FieldRegionSums(long B1Cells, double B1VolumeM3, double B1VolumeSum, double B1VolumeSumSq, double B1Min,
                                     long TissueCells, double AbsorbedPowerW, double MassKg, RecombinationCounts Counts)
{
    /// <summary>Volume-weighted mean |B1+|, T; NaN without B1 cells.</summary>
    public double B1Mean => B1VolumeM3 > 0 ? B1VolumeSum / B1VolumeM3 : double.NaN;

    /// <summary>Volume-weighted standard deviation of |B1+| over its mean; NaN without B1 cells or with a zero mean.</summary>
    public double B1CoV
    {
        get
        {
            double mean = B1Mean;
            if (!(mean > 0)) return double.NaN;
            double variance = Math.Max(0, B1VolumeSumSq / B1VolumeM3 - mean * mean);
            return Math.Sqrt(variance) / mean;
        }
    }
}

public static class FieldRecombination
{
    /// <summary>Roughly how many cells each slab holds across all ports: ~2 M cells is ~100 MB of buffers at four ports.</summary>
    public const int SlabCells = 2_000_000;

    /// <summary>A weight this far below the largest is skipped: its port contributes below float32's resolution.</summary>
    public const double NegligibleWeight = 1e-12;

    /// <summary>
    /// R-sar9-4 — the summed field over every cell: the grid SAR-5's engine averages, and B1± from the summed H when
    /// <paramref name="b1"/> is asked and the set carries H. <paramref name="weights"/> are the ports' incident waves, √W.
    /// </summary>
    public static RecombinedField Recombine(IPortFieldSet set, IReadOnlyList<Complex> weights, bool b1, int b0Axis = 2, int b0Sign = 1,
                                            int slabCells = SlabCells, RunControl? control = null)
    {
        var used = Used(set, weights);
        bool withH = b1 && set.HasH;
        var builder = new SarGridBuilder(set.X, set.Y, set.Z);
        int plane = set.Y.Count * set.Z.Count;
        int nx = set.X.Count;
        long count = (long)nx * plane;
        float[]? plus = withH ? new float[count] : null, minus = withH ? new float[count] : null;
        int step = Math.Clamp(slabCells / Math.Max(1, plane * Math.Max(1, used.Count)), 1, nx);
        int max = step * plane;
        var buffer = new float[6 * max];
        var ex = new Complex[max];
        var ey = new Complex[max];
        var ez = new Complex[max];
        var sigma = new float[max];
        var rho = new float[max];
        var vol = new float[max];
        long reads = 0, cells = 0;
        control?.BeginStage("recombining the port fields", nx, "slabs");
        for (int x = 0; x < nx; x += step)
        {
            control?.ThrowIfCancellationRequested();
            int n = Math.Min(step, nx - x);
            int m = n * plane;
            long start = (long)x * plane;
            set.ReadCells(start, m, sigma.AsSpan(0, m), rho.AsSpan(0, m), vol.AsSpan(0, m));
            reads += Sum(set, used, weights, start, m, buffer, ex, ey, ez, h: false);
            builder.AddSlab(x, n, ex.AsSpan(0, m), ey.AsSpan(0, m), ez.AsSpan(0, m), sigma.AsSpan(0, m), rho.AsSpan(0, m), vol.AsSpan(0, m));
            if (withH)
            {
                reads += Sum(set, used, weights, start, m, buffer, ex, ey, ez, h: true);
                for (int c = 0; c < m; c++)
                {
                    var (bp, bm) = SarB1.FromH(ex[c], ey[c], ez[c], b0Axis, b0Sign);
                    plus![start + c] = (float)bp.Magnitude;
                    minus![start + c] = (float)bm.Magnitude;
                }
            }
            cells += m;
            control?.TickStage(n);
        }
        return new RecombinedField(builder.Build(), plus, minus, new RecombinationCounts(cells, reads, used.Count));
    }

    /// <summary>
    /// R-sar9-4's fast path — local SAR σ|E|²/(2ρ) and |B1±| over <paramref name="box"/> only, reading nothing outside it.
    /// No average: a cube reaches outside any box.
    /// </summary>
    public static RecombinedBox Local(IPortFieldSet set, IReadOnlyList<Complex> weights, FieldBox box, bool b1, int b0Axis = 2, int b0Sign = 1)
    {
        if (box.I0 < 0 || box.J0 < 0 || box.K0 < 0 || box.I1 > set.X.Count || box.J1 > set.Y.Count || box.K1 > set.Z.Count || box.Count <= 0)
            throw new ArgumentException($"The box [{box.I0}, {box.I1}) × [{box.J0}, {box.J1}) × [{box.K0}, {box.K1}) is not inside the " +
                                        $"{set.X.Count} × {set.Y.Count} × {set.Z.Count} grid.");
        var used = Used(set, weights);
        bool withH = b1 && set.HasH;
        int nz = box.Nz;
        var sar = new float[box.Count];
        float[]? plus = withH ? new float[box.Count] : null, minus = withH ? new float[box.Count] : null;
        var buffer = new float[6 * nz];
        var ex = new Complex[nz];
        var ey = new Complex[nz];
        var ez = new Complex[nz];
        var sigma = new float[nz];
        var rho = new float[nz];
        var vol = new float[nz];
        long reads = 0, cells = 0;
        int at = 0;
        for (int i = box.I0; i < box.I1; i++)
            for (int j = box.J0; j < box.J1; j++)
            {
                // one row along z is contiguous in C order
                long start = ((long)i * set.Y.Count + j) * set.Z.Count + box.K0;
                set.ReadCells(start, nz, sigma, rho, vol);
                reads += Sum(set, used, weights, start, nz, buffer, ex, ey, ez, h: false);
                for (int k = 0; k < nz; k++)
                    sar[at + k] = rho[k] > 0
                        ? (float)(0.5 * sigma[k] * (MagSq(ex[k]) + MagSq(ey[k]) + MagSq(ez[k])) / rho[k])
                        : 0f;
                if (withH)
                {
                    reads += Sum(set, used, weights, start, nz, buffer, ex, ey, ez, h: true);
                    for (int k = 0; k < nz; k++)
                    {
                        var (bp, bm) = SarB1.FromH(ex[k], ey[k], ez[k], b0Axis, b0Sign);
                        plus![at + k] = (float)bp.Magnitude;
                        minus![at + k] = (float)bm.Magnitude;
                    }
                }
                at += nz;
                cells += nz;
            }
        return new RecombinedBox(box, sar, plus, minus, new RecombinationCounts(cells, reads, used.Count));
    }

    /// <summary>
    /// brief-sar-10 R-sar10-2 — the fast path's sums over a region: the cells of <paramref name="box"/> that
    /// <paramref name="inRegion"/> keeps (by full-grid cell index; null keeps every one), reading nothing outside the box.
    /// |B1+| is summed over every kept cell (B1 is wanted in air too), the absorbed power ½σ|E|²V and mass ρV over the kept
    /// cells that are tissue (ρ &gt; 0). Region statistics, SARwb and a goal on them need nothing else.
    /// </summary>
    public static FieldRegionSums RegionSums(IPortFieldSet set, IReadOnlyList<Complex> weights, FieldBox box, Func<long, bool>? inRegion,
                                             bool b1, int b0Axis = 2, int b0Sign = 1)
    {
        if (box.I0 < 0 || box.J0 < 0 || box.K0 < 0 || box.I1 > set.X.Count || box.J1 > set.Y.Count || box.K1 > set.Z.Count || box.Count <= 0)
            throw new ArgumentException($"The box [{box.I0}, {box.I1}) × [{box.J0}, {box.J1}) × [{box.K0}, {box.K1}) is not inside the " +
                                        $"{set.X.Count} × {set.Y.Count} × {set.Z.Count} grid.");
        var used = Used(set, weights);
        bool withH = b1 && set.HasH;
        int nz = box.Nz;
        var buffer = new float[6 * nz];
        var ex = new Complex[nz];
        var ey = new Complex[nz];
        var ez = new Complex[nz];
        var sigma = new float[nz];
        var rho = new float[nz];
        var vol = new float[nz];
        var keep = new bool[nz];
        double bVol = 0, bSum = 0, bSq = 0, bMin = double.PositiveInfinity, power = 0, mass = 0;
        long bCells = 0, tissue = 0, reads = 0, cells = 0;
        for (int i = box.I0; i < box.I1; i++)
            for (int j = box.J0; j < box.J1; j++)
            {
                long start = ((long)i * set.Y.Count + j) * set.Z.Count + box.K0;
                bool any = false;
                for (int k = 0; k < nz; k++) any |= keep[k] = inRegion?.Invoke(start + k) ?? true;
                if (!any) continue;
                set.ReadCells(start, nz, sigma, rho, vol);
                reads += Sum(set, used, weights, start, nz, buffer, ex, ey, ez, h: false);
                for (int k = 0; k < nz; k++)
                    if (keep[k] && rho[k] > 0)
                    {
                        power += 0.5 * sigma[k] * (MagSq(ex[k]) + MagSq(ey[k]) + MagSq(ez[k])) * vol[k];
                        mass += (double)rho[k] * vol[k];
                        tissue++;
                    }
                if (withH)
                {
                    reads += Sum(set, used, weights, start, nz, buffer, ex, ey, ez, h: true);
                    for (int k = 0; k < nz; k++)
                    {
                        if (!keep[k]) continue;
                        double bp = SarB1.FromH(ex[k], ey[k], ez[k], b0Axis, b0Sign).Plus.Magnitude;
                        bVol += vol[k];
                        bSum += bp * vol[k];
                        bSq += bp * bp * vol[k];
                        bMin = Math.Min(bMin, bp);
                        bCells++;
                    }
                }
                cells += nz;
            }
        return new FieldRegionSums(bCells, bVol, bSum, bSq, bCells > 0 ? bMin : double.NaN, tissue, power, mass,
                                   new RecombinationCounts(cells, reads, used.Count));
    }

    /// <summary>
    /// The tissue matrix Q, Q_mn = Σ ½σV·(E_m*·E_n) over the cells with ρ &gt; 0, so a recombination with weights a absorbs
    /// aᴴ·Q·a watts (R-sar9-5). Hermitian; read in x slabs of every port at once.
    /// </summary>
    public static Complex[,] TissueMatrix(IPortFieldSet set, int slabCells = SlabCells)
    {
        int n = set.PortCount;
        var q = new Complex[n, n];
        int plane = set.Y.Count * set.Z.Count;
        int nx = set.X.Count;
        int step = Math.Clamp(slabCells / Math.Max(1, plane * n), 1, nx);
        int max = step * plane;
        var fields = new float[n][];
        for (int p = 0; p < n; p++) fields[p] = new float[6 * max];
        var sigma = new float[max];
        var rho = new float[max];
        var vol = new float[max];
        for (int x = 0; x < nx; x += step)
        {
            int m = Math.Min(step, nx - x) * plane;
            long start = (long)x * plane;
            set.ReadCells(start, m, sigma.AsSpan(0, m), rho.AsSpan(0, m), vol.AsSpan(0, m));
            for (int p = 0; p < n; p++) set.ReadE(p, start, m, fields[p].AsSpan(0, 6 * m));
            for (int c = 0; c < m; c++)
            {
                if (!(rho[c] > 0)) continue;
                double w = 0.5 * sigma[c] * vol[c];
                if (w == 0) continue;
                for (int a = 0; a < n; a++)
                    for (int b = a; b < n; b++)
                    {
                        // Σ over components of conj(E_a)·E_b
                        double re = 0, im = 0;
                        for (int comp = 0; comp < 3; comp++)
                        {
                            int o = 6 * c + 2 * comp;
                            double ar = fields[a][o], ai = fields[a][o + 1], br = fields[b][o], bi = fields[b][o + 1];
                            re += ar * br + ai * bi;
                            im += ar * bi - ai * br;
                        }
                        q[a, b] += new Complex(w * re, w * im);
                    }
            }
        }
        for (int a = 0; a < n; a++)
        {
            q[a, a] = new Complex(q[a, a].Real, 0);
            for (int b = a + 1; b < n; b++) q[b, a] = Complex.Conjugate(q[a, b]);
        }
        return q;
    }

    /// <summary>aᴴ·M·a — the power a Hermitian power matrix (Q, or I − SᴴS) gives weights a, W.</summary>
    public static double QuadraticPower(Complex[,] m, IReadOnlyList<Complex> a)
    {
        int n = a.Count;
        if (m.GetLength(0) != n || m.GetLength(1) != n) throw new ArgumentException($"A {m.GetLength(0)} × {m.GetLength(1)} matrix and {n} weights.");
        Complex sum = Complex.Zero;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) sum += Complex.Conjugate(a[i]) * m[i, j] * a[j];
        return sum.Real;
    }

    // ── the pieces ──────────────────────────────────────────────────────────────────────────────────────

    private static List<int> Used(IPortFieldSet set, IReadOnlyList<Complex> weights)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count != set.PortCount)
            throw new ArgumentException($"{weights.Count} weights for a field set of {set.PortCount} ports.", nameof(weights));
        double largest = weights.Select(w => w.Magnitude).DefaultIfEmpty(0).Max();
        if (!double.IsFinite(largest)) throw new ArgumentException("A port weight is not finite.", nameof(weights));
        var used = new List<int>();
        for (int k = 0; k < weights.Count; k++)
            if (weights[k].Magnitude > NegligibleWeight * largest) used.Add(k);
        return used;
    }

    /// <summary>Σ_k a_k·F_k over <paramref name="count"/> cells into (ex, ey, ez); the number of port reads made.</summary>
    private static long Sum(IPortFieldSet set, List<int> used, IReadOnlyList<Complex> weights, long start, int count, float[] buffer,
                            Complex[] ex, Complex[] ey, Complex[] ez, bool h)
    {
        Array.Clear(ex, 0, count);
        Array.Clear(ey, 0, count);
        Array.Clear(ez, 0, count);
        var span = buffer.AsSpan(0, 6 * count);
        foreach (int k in used)
        {
            if (h) set.ReadH(k, start, count, span);
            else set.ReadE(k, start, count, span);
            Complex a = weights[k];
            for (int c = 0; c < count; c++)
            {
                int o = 6 * c;
                ex[c] += a * new Complex(span[o], span[o + 1]);
                ey[c] += a * new Complex(span[o + 2], span[o + 3]);
                ez[c] += a * new Complex(span[o + 4], span[o + 5]);
            }
        }
        return used.Count;
    }

    private static double MagSq(Complex c) => c.Real * c.Real + c.Imaginary * c.Imaginary;
}
