// A picture taken at an angle, put square — brief-img-13-board-photos.md R-im13-2 (D6, D17).
//
// A homography by the direct linear transform, in-house: each point pair gives two rows of A·h = 0, the nine unknowns
// fixed by h₂₂ = 1 and solved by Gaussian elimination (four pairs: exactly; more: least squares through the normal
// equations). Both point sets are first NORMALISED — centred on their centroid and scaled so the mean distance from it
// is √2 (Hartley) — without which pixel coordinates in the thousands square to a system eleven orders of magnitude out
// of balance. Normalising also makes h₂₂ = 1 safe: h₂₂ is zero only when the centroid maps to infinity, and the centroid
// of a convex quadrilateral lies inside it.
//
// Warp samples bilinearly at pixel centres (x + ½, y + ½), in fixed row bands, so the bytes are the same on any thread
// count. A pixel whose source falls outside the picture is white (the raster core's paper) and is marked in the mask.

namespace CircuitRF.Design.Imaging;

/// <summary>A plane-to-plane projective map: (x, y) → ((h₀x + h₁y + h₂) / w, (h₃x + h₄y + h₅) / w), w = h₆x + h₇y + h₈.</summary>
public readonly record struct Homography(double H0, double H1, double H2, double H3, double H4, double H5, double H6, double H7, double H8)
{
    public static Homography Identity { get; } = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    /// <summary>Where (x, y) goes. A point mapped to infinity gives NaN.</summary>
    public (double X, double Y) Map(double x, double y)
    {
        double w = H6 * x + H7 * y + H8;
        if (w == 0) return (double.NaN, double.NaN);
        return ((H0 * x + H1 * y + H2) / w, (H3 * x + H4 * y + H5) / w);
    }

    /// <summary>The inverse map, or null when this one is singular.</summary>
    public Homography? Inverse()
    {
        double c0 = H4 * H8 - H5 * H7, c1 = H5 * H6 - H3 * H8, c2 = H3 * H7 - H4 * H6;
        double det = H0 * c0 + H1 * c1 + H2 * c2;
        if (!(Math.Abs(det) > 1e-300) || !double.IsFinite(det)) return null;
        double k = 1 / det;
        return new Homography(
            c0 * k, (H2 * H7 - H1 * H8) * k, (H1 * H5 - H2 * H4) * k,
            c1 * k, (H0 * H8 - H2 * H6) * k, (H2 * H3 - H0 * H5) * k,
            c2 * k, (H1 * H6 - H0 * H7) * k, (H0 * H4 - H1 * H3) * k);
    }

    /// <summary>This map after <paramref name="first"/>: (this ∘ first)(p) = this(first(p)).</summary>
    public Homography After(Homography first) => new(
        H0 * first.H0 + H1 * first.H3 + H2 * first.H6, H0 * first.H1 + H1 * first.H4 + H2 * first.H7, H0 * first.H2 + H1 * first.H5 + H2 * first.H8,
        H3 * first.H0 + H4 * first.H3 + H5 * first.H6, H3 * first.H1 + H4 * first.H4 + H5 * first.H7, H3 * first.H2 + H4 * first.H5 + H5 * first.H8,
        H6 * first.H0 + H7 * first.H3 + H8 * first.H6, H6 * first.H1 + H7 * first.H4 + H8 * first.H7, H6 * first.H2 + H7 * first.H5 + H8 * first.H8);

    /// <summary>The map with h₈ = 1 (or as it is, when h₈ is zero).</summary>
    public Homography Normalised()
    {
        if (H8 == 0) return this;
        double k = 1 / H8;
        return new(H0 * k, H1 * k, H2 * k, H3 * k, H4 * k, H5 * k, H6 * k, H7 * k, 1);
    }
}

public static class Perspective
{
    /// <summary>
    /// The homography taking each <paramref name="from"/> point to its <paramref name="to"/> point — exactly for four
    /// pairs, in the least-squares sense for more. Null when the points are degenerate (three on a line, two the same).
    /// </summary>
    public static Homography? Solve(IReadOnlyList<(double X, double Y)> from, IReadOnlyList<(double X, double Y)> to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.Count != to.Count || from.Count < 4) throw new ArgumentException("A homography needs at least four point pairs.");
        if (Normaliser(from) is not { } nf || Normaliser(to) is not { } nt) return null;

        // Rows of A for h₀…h₇ (h₈ = 1 moves to the right-hand side), accumulated into the normal equations AᵀA h = Aᵀb.
        var ata = new double[8, 8];
        var atb = new double[8];
        Span<double> r = stackalloc double[8];
        for (int i = 0; i < from.Count; i++)
        {
            var (x, y) = nf.Map(from[i].X, from[i].Y);
            var (u, v) = nt.Map(to[i].X, to[i].Y);
            r.Clear();
            r[0] = x; r[1] = y; r[2] = 1; r[6] = -u * x; r[7] = -u * y;
            Accumulate(ata, atb, r, u);
            r.Clear();
            r[3] = x; r[4] = y; r[5] = 1; r[6] = -v * x; r[7] = -v * y;
            Accumulate(ata, atb, r, v);
        }
        if (SolveInPlace(ata, atb) is not { } h) return null;
        var normalised = new Homography(h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1);
        if (nt.Inverse() is not { } untoTo) return null;
        var full = untoTo.After(normalised.After(nf)).Normalised();
        return full.Inverse() is null ? null : full;
    }

    /// <summary>The similarity putting <paramref name="p"/>'s centroid at the origin and its mean distance from it at √2.</summary>
    private static Homography? Normaliser(IReadOnlyList<(double X, double Y)> p)
    {
        double cx = 0, cy = 0;
        foreach (var (x, y) in p) { cx += x; cy += y; }
        cx /= p.Count;
        cy /= p.Count;
        double d = 0;
        foreach (var (x, y) in p) d += Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
        d /= p.Count;
        if (!(d > 1e-12) || !double.IsFinite(d)) return null;
        double s = Math.Sqrt(2) / d;
        return new Homography(s, 0, -s * cx, 0, s, -s * cy, 0, 0, 1);
    }

    private static void Accumulate(double[,] ata, double[] atb, ReadOnlySpan<double> row, double rhs)
    {
        for (int i = 0; i < 8; i++)
        {
            if (row[i] == 0) continue;
            atb[i] += row[i] * rhs;
            for (int j = 0; j < 8; j++) ata[i, j] += row[i] * row[j];
        }
    }

    /// <summary>Gaussian elimination with partial pivoting; null when singular.</summary>
    private static double[]? SolveInPlace(double[,] a, double[] b)
    {
        int n = b.Length;
        double scale = 0;
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) scale = Math.Max(scale, Math.Abs(a[i, j]));
        double tiny = 1e-12 * Math.Max(scale, 1e-300);
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[p, c])) p = r;
            if (!(Math.Abs(a[p, c]) > tiny)) return null;
            if (p != c)
            {
                for (int j = 0; j < n; j++) (a[c, j], a[p, j]) = (a[p, j], a[c, j]);
                (b[c], b[p]) = (b[p], b[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = a[r, c] / a[c, c];
                if (f == 0) continue;
                for (int j = c; j < n; j++) a[r, j] -= f * a[c, j];
                b[r] -= f * b[c];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int j = r + 1; j < n; j++) s -= a[r, j] * x[j];
            x[r] = s / a[r, r];
            if (!double.IsFinite(x[r])) return null;
        }
        return x;
    }

    /// <summary>
    /// The picture seen through <paramref name="outputToSource"/>: output pixel (u, v)'s centre is mapped into
    /// <paramref name="source"/> and sampled bilinearly. <paramref name="inside"/> marks the output pixels whose centre
    /// fell on the picture; the rest are white.
    /// </summary>
    public static RasterImage Warp(RasterImage source, Homography outputToSource, int width, int height, out BinaryImage inside,
                                   int maxThreads = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        int sw = source.Width, sh = source.Height;
        var src = source.Rgba;
        var dst = new byte[(long)width * height * 4];
        var mask = new BinaryImage(width, height);
        var h = outputToSource;
        Bands.For(height, maxThreads, (y0, y1) =>
        {
            for (int v = y0; v < y1; v++)
                for (int u = 0; u < width; u++)
                {
                    int o = (v * width + u) * 4;
                    var (x, y) = h.Map(u + 0.5, v + 0.5);
                    if (!(x >= 0 && y >= 0 && x <= sw && y <= sh))
                    {
                        dst[o] = dst[o + 1] = dst[o + 2] = dst[o + 3] = 255;
                        continue;
                    }
                    mask.Pixels[v * width + u] = 1;
                    // Pixel centres at ½: sample between the four centres around (x, y), clamped at the picture's edge.
                    double fx = x - 0.5, fy = y - 0.5;
                    int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                    double tx = fx - ix, ty = fy - iy;
                    int x0 = Math.Clamp(ix, 0, sw - 1), x1 = Math.Clamp(ix + 1, 0, sw - 1);
                    int yA = Math.Clamp(iy, 0, sh - 1), yB = Math.Clamp(iy + 1, 0, sh - 1);
                    int a = (yA * sw + x0) * 4, b = (yA * sw + x1) * 4, c = (yB * sw + x0) * 4, d = (yB * sw + x1) * 4;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        double top = src[a + ch] + (src[b + ch] - src[a + ch]) * tx;
                        double bottom = src[c + ch] + (src[d + ch] - src[c + ch]) * tx;
                        dst[o + ch] = (byte)Math.Clamp((int)Math.Round(top + (bottom - top) * ty), 0, 255);
                    }
                    dst[o + 3] = 255;
                }
        });
        inside = mask;
        return new RasterImage(width, height, dst);
    }

    /// <summary>Whether four points, in order, make a convex quadrilateral (either winding) — the only shape four corners
    /// of a rectangle seen at an angle can make.</summary>
    public static bool IsConvexQuad(IReadOnlyList<(double X, double Y)> p)
    {
        if (p.Count != 4) return false;
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % 4];
            var c = p[(i + 2) % 4];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) < 1e-9) return false;
            int s = Math.Sign(cross);
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }
}
