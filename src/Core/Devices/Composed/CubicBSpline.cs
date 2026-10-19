namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// The one-dimensional cubic B-spline a table constituent is built from, one per axis (R-mc2-1).
///
/// <para><b>The knots are a function of the sites.</b> A table stores its grid sites and its
/// coefficients; the knot vector is derived here, by the one rule both the fitter and the evaluator
/// use, so a <c>.cmodel</c> never carries a knot vector that could disagree with its sites.</para>
/// <list type="bullet">
/// <item>Four or more sites: the <b>not-a-knot</b> spline — end knots of multiplicity four, every
/// interior site a simple knot except the second and the second-to-last. One basis function per
/// site, C² at every knot, and O(h⁴) interpolation error up to the ends (a natural spline's
/// zero-curvature end condition costs O(h²) there).</item>
/// <item>Two or three sites: one cubic piece over the whole axis — four Bernstein functions, which
/// carry the line or the parabola through the sites exactly. Any polynomial is C².</item>
/// </list>
///
/// <para>Simple interior knots are what make the spline C² on every axis: its first derivative is
/// continuous, and so is its second, which is the property bilinear interpolation lacks and the
/// reason a table model goes wrong at low drive ([P1] §6.2.6.2).</para>
/// </summary>
public static class CubicBSpline
{
    public const int Degree = 3;

    /// <summary>The knot vector for strictly increasing <paramref name="sites"/> (at least two).</summary>
    public static double[] Knots(ReadOnlySpan<double> sites)
    {
        int n = sites.Length;
        if (n < 2) throw new ArgumentException("a spline axis needs at least two sites.", nameof(sites));
        double a = sites[0], b = sites[n - 1];
        if (n < 4) return [a, a, a, a, b, b, b, b];

        var t = new double[n + 4];
        for (int k = 0; k < 4; k++) { t[k] = a; t[n + k] = b; }
        for (int k = 2; k <= n - 3; k++) t[k + 2] = sites[k];
        return t;
    }

    /// <summary>How many basis functions (coefficients) an axis of <paramref name="siteCount"/> sites has.</summary>
    public static int BasisCount(int siteCount) => siteCount < 4 ? 4 : siteCount;

    /// <summary>
    /// The knot span holding <paramref name="x"/>: the index <c>i</c> with <c>t[i] ≤ x &lt; t[i+1]</c>,
    /// in <c>[3, m − 1]</c> for <c>m</c> basis functions. A point at or past either end takes the end
    /// span; the basis functions nonzero there are <c>i − 3 … i</c>.
    /// </summary>
    public static int Span(ReadOnlySpan<double> knots, double x)
    {
        int m = knots.Length - 4;
        if (x >= knots[m]) return m - 1;
        if (x <= knots[3]) return 3;
        int lo = 3, hi = m;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (x < knots[mid]) hi = mid; else lo = mid;
        }
        return lo;
    }

    /// <summary>
    /// The four basis functions nonzero in <paramref name="span"/> at <paramref name="x"/>, with their
    /// first and second derivatives: <c>ders[0..3]</c> the values, <c>ders[4..7]</c> the first
    /// derivatives, <c>ders[8..11]</c> the second. The Cox–de Boor recurrence with its derivative
    /// form, written for degree three.
    /// </summary>
    public static void BasisDerivatives(ReadOnlySpan<double> knots, int span, double x, Span<double> ders)
    {
        const int p = Degree;
        // ndu[j, r]: the upper triangle holds basis functions, the lower the knot differences.
        Span<double> ndu   = stackalloc double[16];
        Span<double> left  = stackalloc double[4];
        Span<double> right = stackalloc double[4];
        Span<double> a     = stackalloc double[8];

        ndu[0] = 1.0;
        for (int j = 1; j <= p; j++)
        {
            left[j]  = x - knots[span + 1 - j];
            right[j] = knots[span + j] - x;
            double saved = 0.0;
            for (int r = 0; r < j; r++)
            {
                ndu[j * 4 + r] = right[r + 1] + left[j - r];
                double temp = ndu[r * 4 + j - 1] / ndu[j * 4 + r];
                ndu[r * 4 + j] = saved + right[r + 1] * temp;
                saved = left[j - r] * temp;
            }
            ndu[j * 4 + j] = saved;
        }

        for (int j = 0; j <= p; j++) ders[j] = ndu[j * 4 + p];

        for (int r = 0; r <= p; r++)
        {
            int s1 = 0, s2 = 1;
            a[0] = 1.0;
            for (int k = 1; k <= 2; k++)
            {
                double d = 0.0;
                int rk = r - k, pk = p - k;
                if (r >= k)
                {
                    a[s2 * 4] = a[s1 * 4] / ndu[(pk + 1) * 4 + rk];
                    d = a[s2 * 4] * ndu[rk * 4 + pk];
                }
                int j1 = rk >= -1 ? 1 : -rk;
                int j2 = r - 1 <= pk ? k - 1 : p - r;
                for (int j = j1; j <= j2; j++)
                {
                    a[s2 * 4 + j] = (a[s1 * 4 + j] - a[s1 * 4 + j - 1]) / ndu[(pk + 1) * 4 + rk + j];
                    d += a[s2 * 4 + j] * ndu[(rk + j) * 4 + pk];
                }
                if (r <= pk)
                {
                    a[s2 * 4 + k] = -a[s1 * 4 + k - 1] / ndu[(pk + 1) * 4 + r];
                    d += a[s2 * 4 + k] * ndu[r * 4 + pk];
                }
                ders[k * 4 + r] = d;
                (s1, s2) = (s2, s1);
            }
        }

        for (int j = 0; j <= p; j++) { ders[4 + j] *= p; ders[8 + j] *= p * (p - 1); }
    }
}
