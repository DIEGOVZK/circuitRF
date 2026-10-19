using System.Runtime.CompilerServices;

namespace CircuitRF.Core.Devices.Coupled;

/// <summary>A rectangular strip conductor in a 2D cross-section, metres. <c>Y1 == Y2</c> is a
/// zero-thickness strip.</summary>
public readonly record struct StripRect(double X1, double X2, double Y1, double Y2);

/// <summary>One horizontal dielectric layer of a layered cross-section, from <c>YBottom</c> to
/// <c>YTop</c> (metres above the bottom ground), of relative permittivity <c>EpsR</c>.</summary>
public readonly record struct DielectricLayer(double YBottom, double YTop, double EpsR);

/// <summary>
/// A transmission-line cross-section the solver can take: a ground plane at y = 0, piecewise-constant
/// permittivity in y (<see cref="Layers"/>; anything not covered is air), an optional top ground plane
/// at <see cref="TopGroundY"/> (null = open above) and any number of thick or thin rectangular strips.
/// </summary>
public sealed record LayeredCrossSection(
    IReadOnlyList<DielectricLayer> Layers,
    double? TopGroundY,
    IReadOnlyList<StripRect> Strips);

/// <summary>
/// One solve of a <see cref="LayeredCrossSection"/>.
/// </summary>
/// <param name="C">Maxwell capacitance matrix, F/m: C[i, j] is the charge on strip i with strip j at
/// 1 V and every other conductor at 0 V. C[i, i] &gt; 0; C[i, j] &lt; 0 off the diagonal.</param>
/// <param name="SurfaceChargeSquare">Λ[i, j] = ∮ σᵢ·σⱼ dl over every conducting surface (both strips and
/// the ground planes), with σᵢ the surface charge density when strip i alone is at 1 V — (C/m)²/m. A
/// TEM current distribution follows the charge distribution of the AIR-filled line, so Λ of an air solve
/// is what the conductor loss of a mode integrates (see <see cref="QuasiStaticSolver2D"/>).</param>
/// <param name="Nx">Grid lines across.</param>
/// <param name="Ny">Grid lines up.</param>
public sealed record CrossSectionSolution(double[,] C, double[,] SurfaceChargeSquare, int Nx, int Ny);

/// <summary>
/// A small, self-contained 2D quasi-static field solver: the per-unit-length Maxwell capacitance
/// matrix of N strips over a ground plane in a layered dielectric, with an optional top ground.
///
/// <para><b>Method.</b> Laplace's equation ∇·(ε∇φ) = 0 discretised by finite volumes (five-point) on a
/// non-uniform tensor-product grid. Every strip edge, every dielectric interface and both grounds are
/// grid lines, so each material boundary is exact and every conductor is a set of Dirichlet nodes (a
/// thick strip a filled rectangle of them, a thin one a single row). The finest cell is the smallest
/// feature (width, thickness, gap, ground spacing) over <see cref="Resolution"/>; the spacing grows
/// geometrically (×1.25 per cell) away from every strip edge — finest at the corners, where the field
/// is singular — up to a sixth of the structure's height inside it, and ×1.4 per cell beyond it. The
/// box is closed by grounded side walls (and, with no top ground, a grounded lid) far enough away that
/// the stripline field (exponential decay: 4 ground spacings, e^−4π) or the open microstrip field
/// (dipole decay: 25 structure sizes) has died out.</para>
///
/// <para><b>Solve.</b> The node system is symmetric positive definite; ordered with the shorter grid
/// index fastest it is a band matrix, factored once by a banded Cholesky and back-substituted once per
/// strip. The charge on a strip is the discrete Gauss law summed over its nodes — the flux of every link
/// from a conductor node to a free node — which is exactly the energy-consistent capacitance of the
/// discrete system. Everything is managed code with no dependencies.</para>
///
/// <para><b>Accuracy and cost</b> (measured, 2026-10): against a grid ~4× finer, thick strips' C11 is
/// within 0.5 % and C12 within 1 % at the default <see cref="Resolution"/> (Z within ~0.3 %). A
/// ZERO-thickness strip's edge field is more singular (r^−½) and converges slowly, so a thin strip is
/// gridded <see cref="ThinStripRefinement"/> times finer, and its impedance is then within ~1.2 % of
/// Cohn's exact stripline value (the solver errs low). A stripline pair solves in ~25 ms; an open
/// (microstrip) cross-section, whose box is far larger, in ~200 ms per solve. This assembly is built
/// unoptimised, so these are the Release CLI's numbers too.</para>
/// </summary>
public static class QuasiStaticSolver2D
{
    /// <summary>Vacuum permittivity, F/m.</summary>
    public const double Eps0 = 8.8541878128e-12;

    /// <summary>Default grid fineness: the finest cell is the smallest feature over this.</summary>
    public const double Resolution = 8.0;

    /// <summary>How much finer a zero-thickness strip's edges are gridded than a thick strip's.</summary>
    public const double ThinStripRefinement = 8.0;

    private const double Growth = 0.25;

    /// <summary>Cell growth beyond the structure, where the field is smooth.</summary>
    private const double FarGrowth = 0.4;

    /// <summary>With no top ground, the lid and side walls are this many structure sizes away.</summary>
    private const double OpenExtent = 25.0;

    /// <summary>
    /// Solves <paramref name="cs"/>. <paramref name="airOnly"/> replaces every dielectric by vacuum (the C₀
    /// of an inhomogeneous line).
    /// </summary>
    public static CrossSectionSolution Solve(LayeredCrossSection cs, bool airOnly = false, double resolution = 0.0)
    {
        if (!(resolution > 0.0)) resolution = Resolution;
        int ns = cs.Strips.Count;
        if (ns == 0) return new CrossSectionSolution(new double[0, 0], new double[0, 0], 0, 0);
        var strips = cs.Strips;

        // ── the finest spacing: a fraction of the smallest feature ───────────────────
        double minFeature = double.MaxValue;
        bool anyThin = false;
        foreach (var s in strips)
        {
            minFeature = Math.Min(minFeature, s.X2 - s.X1);
            if (s.Y2 - s.Y1 > 0) minFeature = Math.Min(minFeature, s.Y2 - s.Y1);
            else anyThin = true;
            minFeature = Math.Min(minFeature, s.Y1);
            if (cs.TopGroundY is { } tg) minFeature = Math.Min(minFeature, tg - s.Y2);
        }
        for (int i = 0; i < ns; i++)
            for (int j = i + 1; j < ns; j++)
            {
                double gx = Math.Max(strips[j].X1 - strips[i].X2, strips[i].X1 - strips[j].X2);
                double gy = Math.Max(strips[j].Y1 - strips[i].Y2, strips[i].Y1 - strips[j].Y2);
                double gap = Math.Max(gx, gy);
                if (gap > 0) minFeature = Math.Min(minFeature, gap);
            }
        double dmin = minFeature / resolution / (anyThin ? ThinStripRefinement : 1.0);

        // ── the box ─────────────────────────────────────────────────────────────────
        double xMin = strips.Min(s => s.X1), xMax = strips.Max(s => s.X2);
        double yStripTop = strips.Max(s => s.Y2);
        double yLayerTop = cs.Layers.Count > 0 ? cs.Layers.Max(l => l.YTop) : 0.0;
        double height = cs.TopGroundY ?? Math.Max(yStripTop, yLayerTop);
        double yTop, sidePad;
        if (cs.TopGroundY is { } top)
        {
            yTop = top;
            sidePad = 4.0 * top;                                    // exp(−πx/b): e^−4π at the wall
        }
        else
        {
            double span = Math.Max(xMax - xMin, height);
            yTop = Math.Max(yStripTop, yLayerTop) + OpenExtent * span;   // open: a far grounded lid
            sidePad = OpenExtent * span;
        }
        double xLo = xMin - sidePad, xHi = xMax + sidePad;
        double dmax = height / 6.0;

        var xKeys = new List<double> { xLo, xHi };
        var yKeys = new List<double> { 0.0, yTop };
        foreach (var s in strips)
        {
            xKeys.AddRange([s.X1, s.X2]);
            yKeys.AddRange([s.Y1, s.Y2]);
        }
        foreach (var l in cs.Layers)
            if (l.YTop > 0) yKeys.AddRange([Math.Max(l.YBottom, 0.0), Math.Min(l.YTop, yTop)]);

        var xFine = strips.SelectMany(s => new[] { s.X1, s.X2 }).ToArray();
        var yFine = strips.SelectMany(s => new[] { s.Y1, s.Y2 }).ToArray();
        double[] xs = Grade(xKeys, xFine, dmin, dmax, xLo, xHi, xMin, xMax);
        double[] ys = Grade(yKeys, yFine, dmin, dmax, 0.0, yTop, 0.0, height);
        int nx = xs.Length, ny = ys.Length;

        // Cell permittivity depends on the cell's row only (a layered medium).
        var epsRow = new double[ny - 1];
        for (int j = 0; j < ny - 1; j++)
        {
            double ym = 0.5 * (ys[j] + ys[j + 1]);
            double er = 1.0;
            if (!airOnly)
                foreach (var l in cs.Layers)
                    if (ym > l.YBottom && ym < l.YTop) { er = l.EpsR; break; }
            epsRow[j] = er;
        }

        // Owner per node: −1 free, −2 the box (grounds and far walls), k ≥ 0 strip k.
        const int Free = -1, Box = -2;
        var owner = new int[nx, ny];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                if (i == 0 || i == nx - 1 || j == 0 || j == ny - 1) { owner[i, j] = Box; continue; }
                owner[i, j] = Free;
                for (int k = 0; k < ns; k++)
                {
                    var s = strips[k];
                    double ex = 1e-9 * (s.X2 - s.X1), ey = 1e-9 * Math.Max(s.Y2 - s.Y1, minFeature);
                    if (xs[i] >= s.X1 - ex && xs[i] <= s.X2 + ex && ys[j] >= s.Y1 - ey && ys[j] <= s.Y2 + ey)
                    {
                        owner[i, j] = k;
                        break;
                    }
                }
            }

        // Link conductances (ε₀ factored out): ge[i, j] joins (i, j)–(i+1, j), gn[i, j] joins
        // (i, j)–(i, j+1); fe/fn are the lengths of the control-volume faces they cross.
        var ge = new double[nx, ny];
        var gn = new double[nx, ny];
        var fe = new double[nx, ny];
        var fn = new double[nx, ny];
        for (int i = 0; i < nx - 1; i++)
            for (int j = 0; j < ny; j++)
            {
                double below = j > 0 ? ys[j] - ys[j - 1] : 0.0, above = j < ny - 1 ? ys[j + 1] - ys[j] : 0.0;
                double eb = j > 0 ? epsRow[j - 1] : 0.0, ea = j < ny - 1 ? epsRow[j] : 0.0;
                fe[i, j] = (below + above) / 2.0;
                ge[i, j] = (eb * below + ea * above) / 2.0 / (xs[i + 1] - xs[i]);
            }
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny - 1; j++)
            {
                double left = i > 0 ? xs[i] - xs[i - 1] : 0.0, right = i < nx - 1 ? xs[i + 1] - xs[i] : 0.0;
                fn[i, j] = (left + right) / 2.0;
                gn[i, j] = epsRow[j] * fn[i, j] / (ys[j + 1] - ys[j]);
            }

        // Interior unknowns (i = 1..nx−2, j = 1..ny−2), the SHORTER index fastest.
        int mx = nx - 2, my = ny - 2, n = mx * my;
        bool yFast = my <= mx;
        int bw = yFast ? my : mx, stride = bw + 1;
        int Idx(int i, int j) => yFast ? (i - 1) * my + (j - 1) : (j - 1) * mx + (i - 1);

        var band = new double[(long)n * stride];
        for (int i = 1; i <= mx; i++)
            for (int j = 1; j <= my; j++)
            {
                int p = Idx(i, j);
                if (owner[i, j] != Free) { band[(long)p * stride] = 1.0; continue; }
                band[(long)p * stride] = ge[i, j] + ge[i - 1, j] + gn[i, j] + gn[i, j - 1];
                if (j - 1 >= 1 && owner[i, j - 1] == Free) band[(long)p * stride + (p - Idx(i, j - 1))] = -gn[i, j - 1];
                if (i - 1 >= 1 && owner[i - 1, j] == Free) band[(long)p * stride + (p - Idx(i - 1, j))] = -ge[i - 1, j];
            }
        BandCholesky(band, n, bw);

        // One solve per driven strip; keep each conducting-surface link's charge for the loss integral.
        // A link is a (conductor node, free node) pair; the ground planes' links are the free nodes'
        // links to row 0 and, with a top ground, to the top row. The far walls and lid carry no field.
        var links = new List<(int I, int J, bool East, int Sign)>();   // the link leaving (I, J) east / north
        for (int i = 0; i < nx - 1; i++)
            for (int j = 0; j < ny; j++)
            {
                int a = owner[i, j], b = owner[i + 1, j];
                if ((a >= 0 && b == Free) || (a == Free && b >= 0)) links.Add((i, j, true, a >= 0 ? 1 : -1));
            }
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny - 1; j++)
            {
                int a = owner[i, j], b = owner[i, j + 1];
                bool groundBelow = j == 0 && i > 0 && i < nx - 1 && b == Free;
                bool groundAbove = cs.TopGroundY is not null && j + 1 == ny - 1 && i > 0 && i < nx - 1 && a == Free;
                if ((a >= 0 && b == Free) || (a == Free && b >= 0) || groundBelow || groundAbove)
                    links.Add((i, j, false, a >= 0 || groundBelow ? 1 : -1));
            }

        var c = new double[ns, ns];
        var linkQ = new double[ns][];
        var phi = new double[nx, ny];
        var rhs = new double[n];
        for (int drive = 0; drive < ns; drive++)
        {
            Array.Clear(rhs);
            for (int i = 1; i <= mx; i++)
                for (int j = 1; j <= my; j++)
                {
                    int p = Idx(i, j);
                    int o = owner[i, j];
                    if (o >= 0) { rhs[p] = o == drive ? 1.0 : 0.0; continue; }
                    double bsum = 0;
                    if (owner[i + 1, j] == drive) bsum += ge[i, j];
                    if (owner[i - 1, j] == drive) bsum += ge[i - 1, j];
                    if (owner[i, j + 1] == drive) bsum += gn[i, j];
                    if (owner[i, j - 1] == drive) bsum += gn[i, j - 1];
                    rhs[p] = bsum;
                }
            BandSolve(band, n, bw, rhs);
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                    phi[i, j] = owner[i, j] == Box ? 0.0 : rhs[Idx(i, j)];

            // Gauss: the charge on strip k is the flux of its links (from the conductor side).
            var q = new double[ns];
            var lq = new double[links.Count];
            for (int l = 0; l < links.Count; l++)
            {
                var (i, j, east, sign) = links[l];
                double flux = east ? ge[i, j] * (phi[i, j] - phi[i + 1, j]) : gn[i, j] * (phi[i, j] - phi[i, j + 1]);
                double qc = sign * flux * Eps0;                    // charge on the conductor end of the link
                lq[l] = qc;
                int o = sign > 0 ? owner[i, j] : (east ? owner[i + 1, j] : owner[i, j + 1]);
                if (o >= 0) q[o] += qc;
            }
            linkQ[drive] = lq;
            for (int k = 0; k < ns; k++) c[k, drive] = q[k];
        }

        // Λ[a, b] = Σ_links q_a·q_b / face length  (= ∮ σ_a σ_b dl).
        var lam = new double[ns, ns];
        for (int l = 0; l < links.Count; l++)
        {
            var (i, j, east, _) = links[l];
            double len = east ? fe[i, j] : fn[i, j];
            for (int a = 0; a < ns; a++)
                for (int b = 0; b < ns; b++)
                    lam[a, b] += linkQ[a][l] * linkQ[b][l] / len;
        }

        // Symmetrise the round-off (the discrete system is exactly reciprocal).
        for (int i = 0; i < ns; i++)
            for (int j = i + 1; j < ns; j++)
                c[i, j] = c[j, i] = 0.5 * (c[i, j] + c[j, i]);
        return new CrossSectionSolution(c, lam, nx, ny);
    }

    /// <summary>
    /// A graded 1D grid through every key coordinate: spacing <paramref name="dmin"/> at each
    /// <paramref name="fine"/> point, 4·dmin at any other key, growing by ×(1 + <see cref="Growth"/>) per
    /// cell away from them, capped at <paramref name="dmax"/> inside the core [coreLo, coreHi] and growing
    /// freely beyond it.
    /// </summary>
    private static double[] Grade(List<double> keys, double[] fine, double dmin, double dmax, double lo, double hi,
                                  double coreLo, double coreHi)
    {
        var k = keys.Where(v => v >= lo && v <= hi).Append(lo).Append(hi).OrderBy(v => v).ToList();
        var uniq = new List<double>();
        double merge = dmin * 1e-3;
        foreach (double v in k)
            if (uniq.Count == 0 || v - uniq[^1] > merge) uniq.Add(v);
        uniq[^1] = hi;

        double Spacing(double x)
        {
            double outside = Math.Max(0.0, Math.Max(coreLo - x, x - coreHi));
            double s = Math.Max(dmax, FarGrowth * outside);
            foreach (double f in fine) s = Math.Min(s, dmin + Growth * Math.Abs(x - f));
            foreach (double f in uniq) s = Math.Min(s, 4.0 * dmin + Growth * Math.Abs(x - f));
            return s;
        }

        var pts = new List<double> { uniq[0] };
        for (int seg = 0; seg + 1 < uniq.Count; seg++)
        {
            double a = uniq[seg], b = uniq[seg + 1];
            var steps = new List<double>();
            double x = a;
            while (true)
            {
                double s = Spacing(x);
                if (x + s >= b - 0.3 * s) { steps.Add(b - x); break; }
                steps.Add(s);
                x += s;
            }
            double sum = steps.Sum(), scale = (b - a) / sum, acc = a;
            for (int i = 0; i < steps.Count - 1; i++) { acc += steps[i] * scale; pts.Add(acc); }
            pts.Add(b);
        }
        return pts.ToArray();
    }

    /// <summary>In-place banded Cholesky, A = L·Lᵀ, flat lower band storage band[p·(bw+1) + d] = L[p, p − d].</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BandCholesky(double[] band, int n, int bw)
    {
        int stride = bw + 1;
        for (int p = 0; p < n; p++)
        {
            long rp = (long)p * stride;
            int d0 = Math.Min(bw, p);
            for (int d = d0; d >= 1; d--)
            {
                int q = p - d;                        // column q < p
                long rq = (long)q * stride;
                double s = band[rp + d];
                int dd = Math.Min(bw - d, q);         // shared columns k < q within both bands
                long ia = rp + d + 1, ib = rq + 1;
                for (int t = 0; t < dd; t++) s -= band[ia + t] * band[ib + t];
                band[rp + d] = s / band[rq];
            }
            double diag = band[rp];
            for (int d = 1; d <= d0; d++) diag -= band[rp + d] * band[rp + d];
            band[rp] = Math.Sqrt(diag);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BandSolve(double[] band, int n, int bw, double[] b)
    {
        int stride = bw + 1;
        for (int p = 0; p < n; p++)
        {
            long rp = (long)p * stride;
            double s = b[p];
            int d0 = Math.Min(bw, p);
            for (int d = 1; d <= d0; d++) s -= band[rp + d] * b[p - d];
            b[p] = s / band[rp];
        }
        for (int p = n - 1; p >= 0; p--)
        {
            double s = b[p];
            int dmaxp = Math.Min(bw, n - 1 - p);
            for (int d = 1; d <= dmaxp; d++) s -= band[(long)(p + d) * stride + d] * b[p + d];
            b[p] = s / band[(long)p * stride];
        }
    }
}
