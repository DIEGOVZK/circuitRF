// brief-sar-5 R-sar5-2…5 — circuitRF's own SAR: local SAR, the spatial-average SAR of any mass by cube averaging, and
// the absorbed power, mass and whole-volume SAR of a region. MIT code written from the public description of the
// method; docs/design/sar.md §7 writes the procedure out step by step, with every point where the descriptions leave a
// choice and which choice was made. openEMS's sar_calc was read to learn its behaviour, and nothing of it is copied
// (overview L3); agreement with it on the same input is the gate (SarAgainstSarCalcTests).
//
// THE PROCEDURE, per mass m (sar.md §7.2):
//   1. For every tissue cell, the cube CENTRED on it holding exactly m. With SarMethod.Cube it is VALID when it stays
//      inside the grid, holds under 10 % background by volume, and has tissue in the outermost layer of cells of each
//      of its six faces; a valid cube's average is its centre cell's value. With SarMethod.Simple the cube may be
//      clipped by the grid's edge and every cube that reaches m is valid.
//   2. A tissue cell that is not the centre of a valid cube but lies WHOLLY inside one or more (with Simple, partly
//      inside counts) is USED: it takes the largest of their averages.
//   3. Every other tissue cell is UNUSED: six cubes of mass m are grown from it, each with one face on one face of the
//      cell (clipped by the grid, no background rule), and among those within 5 % of the smallest volume the largest
//      average is its value (with Simple, only the smallest). A direction where m cannot be reached gives nothing; a
//      cell where none can is NoCube, value zero.
//
// A cube's mass is Σ ρᵢVᵢ·fᵢ over the cells it touches, fᵢ the fraction of the cell inside it (the partial-cell
// treatment), and its average is Σ ½σᵢ|Eᵢ|²Vᵢ·fᵢ / Σ ρᵢVᵢ·fᵢ.
//
// WHY IT IS FAST (R-sar5-5). The fractions are separable — a cell's fraction is the product of its three axis
// fractions, each 1 except at the cube's two end cells on that axis — so a cube's sums are a combination of at most
// 4 × 4 × 4 entries of a summed-volume table (a 3D prefix sum) of ρV, V, background V and power. One cube evaluation
// is therefore a fixed 64 table reads whatever the cube's size, and finding the cube of mass m is a 1D root search on
// its half-width (a few evaluations: the mass's cube root is close to linear in it). The face test reads a prefix
// count of tissue cells. Step 2's "largest containing average" is a painter's pass: valid cubes in falling order of
// value, each claiming the not-yet-claimed candidate cells inside it, found through a skip list per grid row, and only
// cubes that hold a candidate at all (another prefix count) are visited. Nothing loops over a cube's cells.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace CircuitRF.Engine.Em3d.Sar;

/// <summary>circuitRF's SAR engine (brief-sar-5). See the file header and docs/design/sar.md §7.</summary>
public static class SarEngine
{
    /// <summary>The background fraction a centred cube must stay under to be valid (<see cref="SarMethod.Cube"/>).</summary>
    public const double MaxBackgroundFraction = 0.1;

    /// <summary>An unused cell's face-grown cubes count when their volume is within this factor of the smallest.</summary>
    public const double UnusedVolumeLimit = 1.05;

    /// <summary>The relative mass error a cube is found to.</summary>
    public const double MassTolerance = 1e-9;

    private const int MaxIterations = 100;

    /// <summary>Computes everything <paramref name="options"/> asks for on <paramref name="grid"/>.</summary>
    public static SarResult Compute(SarGrid grid, SarOptions? options = null, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        options ??= new SarOptions();
        options.Region.Check(grid);
        foreach (double g in options.MassesGrams)
            if (!(g > 0) || !double.IsFinite(g))
                throw new ArgumentException($"An averaging mass of {g} g; every mass is positive.");

        var local = LocalSar(grid);
        var (power, mass, localPeak) = RegionTotals(grid, local, options.Region);

        var averages = new List<SarAverage>();
        long searches = 0, evaluations = 0, rows = 0;
        if (options.MassesGrams.Count > 0)
        {
            var work = new Averager(grid, options, control);
            foreach (double g in options.MassesGrams)
            {
                averages.Add(work.Run(g));
            }
            (searches, evaluations, rows) = (work.Searches, work.Evaluations, work.Rows);
        }
        return new SarResult(options.Region.Name, local, localPeak, power, mass, grid.UncountedPowerW, averages,
                             new SarOperationCounts(searches, evaluations, rows));
    }

    /// <summary>σ|E|²/(2ρ) per cell (R-sar5-2), zero where ρ = 0.</summary>
    public static float[] LocalSar(SarGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var sar = new float[grid.Count];
        var pd = grid.PowerDensity;
        var rho = grid.Density;
        for (int c = 0; c < sar.Length; c++)
            if (rho[c] > 0) sar[c] = (float)((double)pd[c] / rho[c]);
        return sar;
    }

    private static (double Power, double Mass, SarPeak? Peak) RegionTotals(SarGrid grid, float[] local, SarRegion region)
    {
        double power = 0, mass = 0;
        int best = -1;
        for (int i = 0, c = 0; i < grid.Nx; i++)
            for (int j = 0; j < grid.Ny; j++)
                for (int k = 0; k < grid.Nz; k++, c++)
                {
                    if (!(grid.Density[c] > 0) || !region.Contains(c, i, j, k)) continue;
                    power += (double)grid.PowerDensity[c] * grid.Volume[c];
                    mass += (double)grid.Density[c] * grid.Volume[c];
                    if (best < 0 || local[c] > local[best]) best = c;
                }
        SarPeak? peak = null;
        if (best >= 0)
        {
            var (i, j, k) = grid.Cell(best);
            peak = new SarPeak(local[best], i, j, k, grid.X.Centre(i), grid.Y.Centre(j), grid.Z.Centre(k), SarCubeKind.Valid, null);
        }
        return (power, mass, peak);
    }

    // ── one cube along one axis ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A cube's extent along one axis: the cells S..E it touches, the fractions of the two end cells inside it (one cell
    /// when S = E), and whether it reached past the grid's low or high face (it is then clipped there).
    /// </summary>
    internal struct Span
    {
        public int S, E;
        public double Ws, We;
        public bool OutLow, OutHigh;

        public readonly bool Out => OutLow || OutHigh;
    }

    /// <summary>The span of the interval [lo, hi] (metres) on <paramref name="axis"/>, clipped to the grid.</summary>
    internal static Span Extent(SarAxis axis, double lo, double hi)
    {
        var f = axis.Faces;
        int n = axis.Count;
        var span = new Span { OutLow = lo < f[0], OutHigh = hi > f[n] };
        double l = Math.Max(lo, f[0]), h = Math.Min(hi, f[n]);

        // S: the cell holding the low end — the largest face at or below it. E: the cell holding the high end — the
        // smallest whose upper face is at or above it. An end exactly on a face takes that cell whole.
        int at = Array.BinarySearch(f, l);
        int s = at >= 0 ? at : ~at - 1;
        at = Array.BinarySearch(f, h);
        int e = (at >= 0 ? at : ~at) - 1;
        s = Math.Clamp(s, 0, n - 1);
        e = Math.Clamp(e, s, n - 1);
        span.S = s;
        span.E = e;
        if (s == e)
        {
            span.Ws = span.We = (h - l) / axis.Widths[s];
        }
        else
        {
            span.Ws = (f[s + 1] - l) / axis.Widths[s];
            span.We = (h - f[e]) / axis.Widths[e];
        }
        return span;
    }

    // ── the averaging, for one mass at a time ────────────────────────────────────────────────

    /// <summary>A summed-volume table entry: ρV, V, background V and ½σ|E|²V, summed over every cell below it.</summary>
    internal struct Sums
    {
        public double Mass, Volume, Background, Power;
    }

    /// <summary>A valid cube the painter visits: its value and the cells wholly inside it.</summary>
    private readonly record struct Painter(float Value, int Centre, int X0, int X1, int Y0, int Y1, int Z0, int Z1);

    private sealed class Averager
    {
        private readonly SarGrid _g;
        private readonly SarOptions _o;
        private readonly RunControl? _control;
        private readonly int _nx, _ny, _nz, _sy, _sz; // _sy, _sz: the table's strides (it is (nx+1)(ny+1)(nz+1))
        private readonly Sums[] _sums;
        private readonly int[] _tissue;     // prefix count of tissue cells
        private readonly int[] _candidates; // prefix count of tissue cells that are not valid, rebuilt per mass
        private readonly double[] _half;    // a valid cell's cube half-width, per mass
        private readonly ParallelOptions _parallel;

        public long Searches, Evaluations, Rows;

        public Averager(SarGrid grid, SarOptions options, RunControl? control)
        {
            _g = grid; _o = options; _control = control;
            (_nx, _ny, _nz) = (grid.Nx, grid.Ny, grid.Nz);
            _sz = _nz + 1;
            _sy = (_ny + 1) * _sz;
            long entries = (long)(_nx + 1) * _sy;
            if (entries > Array.MaxLength) throw new ArgumentException($"The grid's {grid.Count} cells are too many for one table.");
            _parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : -1,
                CancellationToken = control?.Token ?? CancellationToken.None,
            };
            _sums = new Sums[entries];
            _tissue = new int[entries];
            _candidates = new int[entries];
            _half = new double[grid.Count];
            BuildTables();
        }

        private int T(int i, int j, int k) => i * _sy + j * _sz + k;

        private void BuildTables()
        {
            var rho = _g.Density;
            var vol = _g.Volume;
            var pd = _g.PowerDensity;
            Parallel.For(0, _nx, _parallel, i =>
            {
                for (int j = 0; j < _ny; j++)
                {
                    int c = (i * _ny + j) * _nz;
                    int t = T(i + 1, j + 1, 1);
                    for (int k = 0; k < _nz; k++, c++, t++)
                    {
                        double v = vol[c];
                        bool tissue = rho[c] > 0;
                        _sums[t] = new Sums
                        {
                            Mass = rho[c] * v, Volume = v, Background = tissue ? 0 : v, Power = (double)pd[c] * v,
                        };
                        _tissue[t] = tissue ? 1 : 0;
                    }
                }
            });
            PrefixSum(_sums, Add);
            PrefixSum(_tissue, (ref int a, in int b) => a += b);
        }

        private delegate void Accumulate<TValue>(ref TValue into, in TValue add);

        private static void Add(ref Sums a, in Sums b)
        {
            a.Mass += b.Mass; a.Volume += b.Volume; a.Background += b.Background; a.Power += b.Power;
        }

        /// <summary>Turns per-cell values (at table index cell + 1 on each axis, zero borders) into a summed-volume table,
        /// one axis at a time.</summary>
        private void PrefixSum<TValue>(TValue[] t, Accumulate<TValue> add)
        {
            Parallel.For(1, _nx + 1, _parallel, i =>
            {
                for (int j = 1; j <= _ny; j++)
                    for (int k = 2; k <= _nz; k++)
                        add(ref t[T(i, j, k)], in t[T(i, j, k - 1)]);
                for (int j = 2; j <= _ny; j++)
                    for (int k = 1; k <= _nz; k++)
                        add(ref t[T(i, j, k)], in t[T(i, j - 1, k)]);
            });
            Parallel.For(1, _ny + 1, _parallel, j =>
            {
                for (int i = 2; i <= _nx; i++)
                    for (int k = 1; k <= _nz; k++)
                        add(ref t[T(i, j, k)], in t[T(i - 1, j, k)]);
            });
        }

        /// <summary>The count over cells [x0, x1] × [y0, y1] × [z0, z1] (inclusive) of a prefix-count table.</summary>
        private int Count(int[] t, int x0, int x1, int y0, int y1, int z0, int z1)
        {
            if (x1 < x0 || y1 < y0 || z1 < z0) return 0;
            int a = x0, b = x1 + 1, c = y0, d = y1 + 1, e = z0, f = z1 + 1;
            return t[T(b, d, f)] - t[T(a, d, f)] - t[T(b, c, f)] - t[T(b, d, e)]
                 + t[T(a, c, f)] + t[T(a, d, e)] + t[T(b, c, e)] - t[T(a, c, e)];
        }

        /// <summary>A cube's sums from its three spans: at most 64 table reads.</summary>
        private Sums Evaluate(in Span x, in Span y, in Span z)
        {
            Span<int> ix = stackalloc int[4], iy = stackalloc int[4], iz = stackalloc int[4];
            Span<double> cx = stackalloc double[4], cy = stackalloc double[4], cz = stackalloc double[4];
            int nx = Terms(x, ix, cx), ny = Terms(y, iy, cy), nz = Terms(z, iz, cz);
            var s = new Sums();
            for (int a = 0; a < nx; a++)
                for (int b = 0; b < ny; b++)
                {
                    double wab = cx[a] * cy[b];
                    int row = ix[a] * _sy + iy[b] * _sz;
                    for (int c = 0; c < nz; c++)
                    {
                        double w = wab * cz[c];
                        ref readonly var t = ref _sums[row + iz[c]];
                        s.Mass += w * t.Mass;
                        s.Volume += w * t.Volume;
                        s.Background += w * t.Background;
                        s.Power += w * t.Power;
                    }
                }
            return s;
        }

        /// <summary>
        /// One axis's weights as prefix-table entries: Σ fᵢ·cellᵢ over S..E, with fractions Ws, We at the ends and 1 in
        /// between, is −Ws·P[S] + (Ws − 1)·P[S+1] + (1 − We)·P[E] + We·P[E+1] (P the prefix along the axis); for S = E it
        /// is Ws·(P[S+1] − P[S]).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Terms(in Span s, Span<int> index, Span<double> coef)
        {
            if (s.S == s.E)
            {
                index[0] = s.S; coef[0] = -s.Ws;
                index[1] = s.S + 1; coef[1] = s.Ws;
                return 2;
            }
            index[0] = s.S; coef[0] = -s.Ws;
            index[1] = s.S + 1; coef[1] = s.Ws - 1;
            index[2] = s.E; coef[2] = 1 - s.We;
            index[3] = s.E + 1; coef[3] = s.We;
            return 4;
        }

        // ── the cube's shape about a cell ──

        /// <summary>The three spans of the cube of half-width <paramref name="h"/> about cell (i, j, k): centred
        /// (<paramref name="face"/> &lt; 0), or grown from face <paramref name="face"/> (2a is the low face on axis a, so the
        /// cube runs from it toward +a for an edge of 2h; 2a + 1 the high face, toward −a).</summary>
        private void Shape(int i, int j, int k, int face, double h, out Span x, out Span y, out Span z)
        {
            x = AxisSpan(_g.X, i, face, 0, h);
            y = AxisSpan(_g.Y, j, face, 1, h);
            z = AxisSpan(_g.Z, k, face, 2, h);
        }

        private static Span AxisSpan(SarAxis axis, int p, int face, int a, double h)
        {
            if (face == 2 * a) return Extent(axis, axis.Faces[p], axis.Faces[p] + 2 * h);
            if (face == 2 * a + 1) return Extent(axis, axis.Faces[p + 1] - 2 * h, axis.Faces[p + 1]);
            double c = axis.Centre(p);
            return Extent(axis, c - h, c + h);
        }

        /// <summary>The half-width past which the cube about (i, j, k) no longer changes: with <paramref name="clip"/> false,
        /// the largest that stays inside the grid; with it true, the smallest that has reached the grid's edge on every
        /// side it grows toward.</summary>
        private double MaxHalf(int i, int j, int k, int face, bool clip)
        {
            double best = clip ? 0 : double.PositiveInfinity;
            for (int a = 0; a < 3; a++)
            {
                var axis = a == 0 ? _g.X : a == 1 ? _g.Y : _g.Z;
                int p = a == 0 ? i : a == 1 ? j : k;
                var f = axis.Faces;
                double reach =
                    face == 2 * a ? (f[^1] - f[p]) / 2 :
                    face == 2 * a + 1 ? (f[p + 1] - f[0]) / 2 :
                    clip ? Math.Max(axis.Centre(p) - f[0], f[^1] - axis.Centre(p))
                         : Math.Min(axis.Centre(p) - f[0], f[^1] - axis.Centre(p));
                best = clip ? Math.Max(best, reach) : Math.Min(best, reach);
            }
            return best;
        }

        /// <summary>
        /// The half-width at which the cube about (i, j, k) holds <paramref name="target"/> kg, or NaN when it cannot within
        /// <paramref name="hMax"/>. Its mass never falls as it grows, and its cube root is close to linear in the half-width,
        /// so a secant on the cube root, kept inside a bracket and halved when it stalls, takes a few evaluations.
        /// </summary>
        private double Find(int i, int j, int k, int face, double hMax, double target, double guess,
                            ref long evaluations, out Sums sums, out Span x, out Span y, out Span z)
        {
            double tol = MassTolerance * target, gTarget = Math.Cbrt(target);
            double lo = 0, hi = hMax;
            bool hiKnown = false;
            double h = Math.Min(guess, hMax), hPrev = double.NaN, gPrev = double.NaN, width = hMax, widthBefore = double.PositiveInfinity;
            for (int it = 0; it < MaxIterations; it++)
            {
                Shape(i, j, k, face, h, out x, out y, out z);
                sums = Evaluate(x, y, z);
                evaluations++;
                double diff = sums.Mass - target;
                if (Math.Abs(diff) <= tol) return h;
                if (diff < 0)
                {
                    if (h >= hMax) return double.NaN; // as large as it gets, and still short of m
                    lo = h;
                }
                else
                {
                    hi = h;
                    hiKnown = true;
                }
                double g = Math.Cbrt(sums.Mass), next;
                if (!double.IsNaN(hPrev) && g != gPrev)
                    next = h + (gTarget - g) * (h - hPrev) / (g - gPrev);
                else
                    next = sums.Mass > 0 ? h * gTarget / g : 2 * h;
                double upper = hiKnown ? hi : hMax;
                if (hiKnown)
                {
                    double w = hi - lo;
                    if (w <= 1e-15 * hi) break; // the bracket cannot shrink: take its heavy end
                    // a secant that leaves the bracket, or one that has not halved it over two steps, is replaced by bisection
                    if (!(next > lo && next < hi) || w > 0.5 * widthBefore) next = 0.5 * (lo + hi);
                    widthBefore = width;
                    width = w;
                }
                else if (!(next > lo)) next = 2 * h;
                hPrev = h; gPrev = g;
                h = Math.Min(next, upper);
            }
            if (!hiKnown) { sums = default; x = y = z = default; return double.NaN; }
            Shape(i, j, k, face, hi, out x, out y, out z);
            sums = Evaluate(x, y, z);
            evaluations++;
            return hi;
        }

        /// <summary>Tissue in the outermost layer of cells on each of the cube's six faces.</summary>
        private bool FacesTouchTissue(in Span x, in Span y, in Span z) =>
            Count(_tissue, x.S, x.S, y.S, y.E, z.S, z.E) > 0 && Count(_tissue, x.E, x.E, y.S, y.E, z.S, z.E) > 0 &&
            Count(_tissue, x.S, x.E, y.S, y.S, z.S, z.E) > 0 && Count(_tissue, x.S, x.E, y.E, y.E, z.S, z.E) > 0 &&
            Count(_tissue, x.S, x.E, y.S, y.E, z.S, z.S) > 0 && Count(_tissue, x.S, x.E, y.S, y.E, z.E, z.E) > 0;

        /// <summary>The cells a cube holds wholly along one axis (with Simple, every cell it touches); empty when Lo &gt; Hi.</summary>
        private (int Lo, int Hi) Inside(in Span s)
        {
            if (_o.Method == SarMethod.Simple) return (s.S, s.E);
            if (s.S == s.E) return s.Ws == 1 ? (s.S, s.S) : (1, 0);
            return (s.Ws == 1 ? s.S : s.S + 1, s.We == 1 ? s.E : s.E - 1);
        }

        public SarAverage Run(double grams)
        {
            double m = grams * 1e-3;
            var sar = new float[_g.Count];
            var kinds = new SarCubeKind[_g.Count];
            var rho = _g.Density;
            bool cube = _o.Method == SarMethod.Cube;
            string label = $"{grams:G4} g";

            // Step 1: the centred cube of every tissue cell.
            _control?.BeginStage($"SAR {label}: centred cubes", _nx, "planes");
            Parallel.For(0, _nx, _parallel, () => (0L, 0L), (i, state, acc) =>
            {
                long searches = acc.Item1, evaluations = acc.Item2;
                for (int j = 0; j < _ny; j++)
                    for (int k = 0; k < _nz; k++)
                    {
                        int c = (i * _ny + j) * _nz + k;
                        if (!(rho[c] > 0)) continue;
                        searches++;
                        double hMax = MaxHalf(i, j, k, -1, clip: !cube);
                        double h = Find(i, j, k, -1, hMax, m, 0.5 * Math.Cbrt(m / rho[c]), ref evaluations,
                                        out var s, out var x, out var y, out var z);
                        if (double.IsNaN(h)) continue;
                        if (cube && (s.Background >= MaxBackgroundFraction * s.Volume || !FacesTouchTissue(x, y, z))) continue;
                        kinds[c] = SarCubeKind.Valid;
                        sar[c] = (float)(s.Power / s.Mass);
                        _half[c] = h;
                    }
                _control?.TickStage();
                return (searches, evaluations);
            }, acc => { Interlocked.Add(ref Searches, acc.Item1); Interlocked.Add(ref Evaluations, acc.Item2); });

            // Step 2: used cells, by the painter's pass.
            Paint(sar, kinds);

            // Step 3: the cubes grown from a face, for the rest.
            _control?.BeginStage($"SAR {label}: face-grown cubes", _nx, "planes");
            double limit = cube ? UnusedVolumeLimit : 1;
            Parallel.For(0, _nx, _parallel, () => (0L, 0L), (i, state, acc) =>
            {
                long searches = acc.Item1, evaluations = acc.Item2;
                Span<double> value = stackalloc double[6], volume = stackalloc double[6];
                for (int j = 0; j < _ny; j++)
                    for (int k = 0; k < _nz; k++)
                    {
                        int c = (i * _ny + j) * _nz + k;
                        if (!(rho[c] > 0) || kinds[c] != SarCubeKind.Background) continue;
                        double smallest = double.PositiveInfinity;
                        double guess = 0.5 * Math.Cbrt(m / rho[c]);
                        for (int face = 0; face < 6; face++)
                        {
                            searches++;
                            double h = Find(i, j, k, face, MaxHalf(i, j, k, face, clip: true), m, guess, ref evaluations,
                                            out var s, out _, out _, out _);
                            volume[face] = double.IsNaN(h) ? double.NaN : s.Volume;
                            value[face] = double.IsNaN(h) ? 0 : s.Power / s.Mass;
                            if (!double.IsNaN(h)) smallest = Math.Min(smallest, s.Volume);
                        }
                        double best = 0;
                        for (int face = 0; face < 6; face++)
                            if (volume[face] <= limit * smallest) best = Math.Max(best, value[face]);
                        kinds[c] = double.IsPositiveInfinity(smallest) ? SarCubeKind.NoCube : SarCubeKind.Unused;
                        sar[c] = (float)best;
                    }
                _control?.TickStage();
                return (searches, evaluations);
            }, acc => { Interlocked.Add(ref Searches, acc.Item1); Interlocked.Add(ref Evaluations, acc.Item2); });

            long valid = 0, used = 0, unused = 0, none = 0, background = 0;
            foreach (var kind in kinds)
                switch (kind)
                {
                    case SarCubeKind.Valid: valid++; break;
                    case SarCubeKind.Used: used++; break;
                    case SarCubeKind.Unused: unused++; break;
                    case SarCubeKind.NoCube: none++; break;
                    default: background++; break;
                }
            var peak = Peak(sar, kinds, m);
            return new SarAverage(grams, sar, kinds, peak, new SarCubeCounts(valid, used, unused, none, background));
        }

        /// <summary>
        /// Step 2: every tissue cell that is not valid takes the largest average of the valid cubes wholly holding it. The
        /// valid cubes that hold any such cell are taken in falling order of value; each claims the cells inside it no
        /// earlier cube claimed, found through a skip list along z per (x, y) row, so a claimed cell is never visited
        /// again. Threads own disjoint ranges of x.
        /// </summary>
        private void Paint(float[] sar, SarCubeKind[] kinds)
        {
            var rho = _g.Density;
            Array.Clear(_candidates);
            Parallel.For(0, _nx, _parallel, i =>
            {
                for (int j = 0; j < _ny; j++)
                {
                    int c = (i * _ny + j) * _nz;
                    int t = T(i + 1, j + 1, 1);
                    for (int k = 0; k < _nz; k++, c++, t++)
                        _candidates[t] = rho[c] > 0 && kinds[c] != SarCubeKind.Valid ? 1 : 0;
                }
            });
            PrefixSum(_candidates, (ref int a, in int b) => a += b);
            if (Count(_candidates, 0, _nx - 1, 0, _ny - 1, 0, _nz - 1) == 0) return;

            var found = new ConcurrentBag<List<Painter>>();
            Parallel.For(0, _nx, _parallel, () => new List<Painter>(), (i, state, list) =>
            {
                for (int j = 0; j < _ny; j++)
                    for (int k = 0; k < _nz; k++)
                    {
                        int c = (i * _ny + j) * _nz + k;
                        if (kinds[c] != SarCubeKind.Valid) continue;
                        Shape(i, j, k, -1, _half[c], out var x, out var y, out var z);
                        var (x0, x1) = Inside(x);
                        var (y0, y1) = Inside(y);
                        var (z0, z1) = Inside(z);
                        if (Count(_candidates, x0, x1, y0, y1, z0, z1) > 0)
                            list.Add(new Painter(sar[c], c, x0, x1, y0, y1, z0, z1));
                    }
                return list;
            }, found.Add);
            var cubes = found.SelectMany(l => l).ToArray();
            Array.Sort(cubes, (a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value) : a.Centre.CompareTo(b.Centre));

            // The skip list: next[row·(nz+1) + k] leads to the first unclaimed candidate at or after k (nz past the end).
            int stride = _nz + 1;
            var next = new int[(long)_nx * _ny * stride];
            Parallel.For(0, _nx, _parallel, i =>
            {
                for (int j = 0; j < _ny; j++)
                {
                    int row = (i * _ny + j) * stride, c = (i * _ny + j) * _nz;
                    for (int k = 0; k < _nz; k++)
                        next[row + k] = rho[c + k] > 0 && kinds[c + k] != SarCubeKind.Valid ? k : k + 1;
                    next[row + _nz] = _nz;
                }
            });

            int threads = Math.Max(1, Math.Min(_nx, _o.MaxDegreeOfParallelism > 0 ? _o.MaxDegreeOfParallelism : Environment.ProcessorCount));
            long rows = 0;
            Parallel.For(0, threads, _parallel, t =>
            {
                int xa = (int)((long)_nx * t / threads), xb = (int)((long)_nx * (t + 1) / threads);
                long walked = 0;
                foreach (var p in cubes)
                {
                    int x0 = Math.Max(p.X0, xa), x1 = Math.Min(p.X1, xb - 1);
                    for (int x = x0; x <= x1; x++)
                        for (int y = p.Y0; y <= p.Y1; y++)
                        {
                            walked++;
                            int row = (x * _ny + y) * stride, cell = (x * _ny + y) * _nz;
                            for (int k = Next(next, row, p.Z0); k <= p.Z1; k = Next(next, row, k + 1))
                            {
                                sar[cell + k] = p.Value;
                                kinds[cell + k] = SarCubeKind.Used;
                                next[row + k] = k + 1;
                            }
                        }
                }
                Interlocked.Add(ref rows, walked);
            });
            Rows += rows;
            _painted = cubes;
        }

        private Painter[] _painted = [];

        /// <summary>The first unclaimed candidate at or after <paramref name="k"/> in a row, halving the path as it goes.</summary>
        private static int Next(int[] next, int row, int k)
        {
            while (next[row + k] != k)
            {
                int up = next[row + next[row + k]];
                next[row + k] = up;
                k = up;
            }
            return k;
        }

        /// <summary>The peak in the region, with the cube it came from.</summary>
        private SarPeak? Peak(float[] sar, SarCubeKind[] kinds, double m)
        {
            int best = -1;
            for (int i = 0, c = 0; i < _nx; i++)
                for (int j = 0; j < _ny; j++)
                    for (int k = 0; k < _nz; k++, c++)
                    {
                        if (kinds[c] == SarCubeKind.Background || !_o.Region.Contains(c, i, j, k)) continue;
                        // a tie goes to the cell whose own cube it is, so the peak names that cube
                        if (best < 0 || sar[c] > sar[best] || (sar[c] == sar[best] && kinds[c] == SarCubeKind.Valid && kinds[best] != SarCubeKind.Valid))
                            best = c;
                    }
            if (best < 0) return null;
            var (bi, bj, bk) = _g.Cell(best);
            SarCube? cube = kinds[best] switch
            {
                SarCubeKind.Valid => CubeOf(bi, bj, bk, -1, _half[best]),
                SarCubeKind.Used => _painted.FirstOrDefault(p => p.X0 <= bi && bi <= p.X1 && p.Y0 <= bj && bj <= p.Y1 && p.Z0 <= bk && bk <= p.Z1)
                                    is { Value: var v } p && v == sar[best] ? Recentre(p.Centre) : null,
                SarCubeKind.Unused => FaceCubeOf(bi, bj, bk, m, sar[best]),
                _ => null,
            };
            return new SarPeak(sar[best], bi, bj, bk, _g.X.Centre(bi), _g.Y.Centre(bj), _g.Z.Centre(bk), kinds[best], cube);
        }

        private SarCube Recentre(int centre)
        {
            var (i, j, k) = _g.Cell(centre);
            return CubeOf(i, j, k, -1, _half[centre]);
        }

        private SarCube CubeOf(int i, int j, int k, int face, double h)
        {
            Shape(i, j, k, face, h, out var x, out var y, out var z);
            var s = Evaluate(x, y, z);
            double[] lo = new double[3], hi = new double[3];
            for (int a = 0; a < 3; a++)
            {
                var axis = a == 0 ? _g.X : a == 1 ? _g.Y : _g.Z;
                int p = a == 0 ? i : a == 1 ? j : k;
                double l, u;
                if (face == 2 * a) { l = axis.Faces[p]; u = l + 2 * h; }
                else if (face == 2 * a + 1) { u = axis.Faces[p + 1]; l = u - 2 * h; }
                else { l = axis.Centre(p) - h; u = axis.Centre(p) + h; }
                lo[a] = Math.Max(l, axis.Faces[0]);
                hi[a] = Math.Min(u, axis.Faces[^1]);
            }
            return new SarCube(lo, hi, s.Mass);
        }

        /// <summary>The face-grown cube that gave an unused cell <paramref name="value"/>.</summary>
        private SarCube? FaceCubeOf(int i, int j, int k, double m, float value)
        {
            double guess = 0.5 * Math.Cbrt(m / _g.Density[_g.Index(i, j, k)]);
            long ignored = 0;
            for (int face = 0; face < 6; face++)
            {
                double h = Find(i, j, k, face, MaxHalf(i, j, k, face, clip: true), m, guess, ref ignored, out var s, out _, out _, out _);
                if (!double.IsNaN(h) && (float)(s.Power / s.Mass) == value) return CubeOf(i, j, k, face, h);
            }
            return null;
        }
    }
}
