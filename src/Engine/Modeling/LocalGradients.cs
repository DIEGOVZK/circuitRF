namespace CircuitRF.Engine.Modeling;

/// <summary>
/// The gradient of scattered 2-D data at each of its own points, from a local least-squares quadratic
/// through the point's nearest neighbours (R-mc7-5): the DC derivatives of measured I–V, against which a
/// fitted current's <c>Gm</c>/<c>Gds</c> are compared (their difference measures the data's noise).
///
/// <para>Not for SECOND derivatives of measured data: differencing conductances to test [P1] eq. 6.49 this way
/// measured 86 % "non-conservative" on exactly conservative data at a 0.3 V grid, which is why the current step
/// tests 6.49 through the least-squares current's residual instead.</para>
///
/// <para>Coordinates are each mapped onto [0, 1] over the points, so the neighbourhood is not stretched
/// along the axis with the larger span. A neighbourhood too degenerate for a quadratic (points on one
/// line, too few) falls back to a plane; one too degenerate for a plane gives NaN.</para>
/// </summary>
public static class LocalGradients
{
    public const int DefaultNeighbours = 12;

    /// <returns>Per point, <c>(∂f/∂x, ∂f/∂y)</c> in the data's own units.</returns>
    public static (double Dx, double Dy)[] Of(IReadOnlyList<(double X, double Y)> points, IReadOnlyList<double> values,
                                              int neighbours = DefaultNeighbours)
    {
        int n = points.Count;
        if (values.Count != n) throw new ArgumentException("one value per point.");
        var result = new (double, double)[n];
        if (n == 0) return result;
        double xLo = points.Min(p => p.X), xHi = points.Max(p => p.X);
        double yLo = points.Min(p => p.Y), yHi = points.Max(p => p.Y);
        double sx = xHi > xLo ? 1 / (xHi - xLo) : 1, sy = yHi > yLo ? 1 / (yHi - yLo) : 1;
        var u = points.Select(p => (p.X - xLo) * sx).ToArray();
        var v = points.Select(p => (p.Y - yLo) * sy).ToArray();
        int k = Math.Min(neighbours, n - 1);

        var order = new int[n];
        var dist = new double[n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                order[j] = j;
                double du = u[j] - u[i], dv = v[j] - v[i];
                dist[j] = du * du + dv * dv;
            }
            Array.Sort((double[])dist.Clone(), order);
            var near = order.Take(k + 1).ToArray();          // the point itself is first (distance 0)
            var g = Fit(near, i, u, v, values, quadratic: true) ?? Fit(near, i, u, v, values, quadratic: false);
            result[i] = g is { } gg ? (gg.Du * sx, gg.Dv * sy) : (double.NaN, double.NaN);
        }
        return result;
    }

    /// <summary>Least squares over the neighbourhood in coordinates centred on point <paramref name="at"/>;
    /// the gradient there is the linear coefficients. Null when the normal matrix is singular.</summary>
    private static (double Du, double Dv)? Fit(int[] near, int at, double[] u, double[] v, IReadOnlyList<double> f, bool quadratic)
    {
        int m = quadratic ? 6 : 3;
        if (near.Length < m + (quadratic ? 2 : 0)) return null;
        var a = new double[m, m];
        var b = new double[m];
        Span<double> row = stackalloc double[6];
        foreach (int j in near)
        {
            double du = u[j] - u[at], dv = v[j] - v[at];
            row[0] = 1; row[1] = du; row[2] = dv;
            if (quadratic) { row[3] = du * du; row[4] = du * dv; row[5] = dv * dv; }
            for (int p = 0; p < m; p++)
            {
                b[p] += row[p] * f[j];
                for (int q = 0; q < m; q++) a[p, q] += row[p] * row[q];
            }
        }
        var c = Solve(a, b);
        return c is null ? null : (c[1], c[2]);
    }

    /// <summary>Gaussian elimination with partial pivoting; null when a pivot vanishes relative to the matrix.</summary>
    private static double[]? Solve(double[,] a, double[] b)
    {
        int m = b.Length;
        double scale = 0;
        for (int p = 0; p < m; p++) scale = Math.Max(scale, Math.Abs(a[p, p]));
        if (!(scale > 0)) return null;
        for (int c = 0; c < m; c++)
        {
            int piv = c;
            for (int r = c + 1; r < m; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            if (Math.Abs(a[piv, c]) < 1e-12 * scale) return null;
            if (piv != c)
            {
                for (int q = 0; q < m; q++) (a[c, q], a[piv, q]) = (a[piv, q], a[c, q]);
                (b[c], b[piv]) = (b[piv], b[c]);
            }
            for (int r = c + 1; r < m; r++)
            {
                double l = a[r, c] / a[c, c];
                for (int q = c; q < m; q++) a[r, q] -= l * a[c, q];
                b[r] -= l * b[c];
            }
        }
        var x = new double[m];
        for (int r = m - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int q = r + 1; q < m; q++) s -= a[r, q] * x[q];
            x[r] = s / a[r, r];
        }
        return x;
    }
}
