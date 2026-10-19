using CircuitRF.Core.Devices.Composed;

namespace CircuitRF.Engine.Modeling;

/// <summary>
/// Fits the coefficients of a table constituent's tensor-product cubic spline to gridded values
/// (R-mc2-1). Done once, when a model is written; the <c>.cmodel</c> stores the result, so loading
/// fits nothing.
///
/// <para><b>Separable.</b> On a rectilinear grid the tensor-product fit factors into one-dimensional
/// fits along each axis in turn, so an axis of <c>n</c> sites costs a banded solve per grid line.</para>
///
/// <para><b>Smoothing.</b> Along an axis of four or more sites the coefficients minimise
/// <c>Σ (yᵢ − s(xᵢ))² + λ ∫ s″(u)² du</c>, with <c>u</c> the axis mapped onto [0, 1] so one λ means the
/// same on every axis. λ = 0 is interpolation. The normal equations <c>(BᵀB + λΩ) c = Bᵀy</c> are
/// symmetric positive definite and banded (half-bandwidth three), solved by a banded Cholesky
/// factorisation computed once per axis. Applied axis by axis this is the separable smoother; on an
/// axis of two or three sites, which carries a line or a parabola, nothing is smoothed.</para>
/// </summary>
public static class TensorSplineFit
{
    /// <param name="sites">Strictly increasing sites per axis.</param>
    /// <param name="values">Row-major over the grid, last axis fastest.</param>
    /// <param name="lambda">The smoothing weight, ≥ 0.</param>
    /// <returns>The coefficients, row-major, <see cref="CubicBSpline.BasisCount"/> per axis.</returns>
    public static double[] Fit(IReadOnlyList<double[]> sites, double[] values, double lambda = 0.0)
    {
        if (!(lambda >= 0) || double.IsInfinity(lambda))
            throw new ArgumentOutOfRangeException(nameof(lambda), "the smoothing weight is a number ≥ 0.");
        int d = sites.Count;
        var shape = new int[d];
        for (int k = 0; k < d; k++) shape[k] = sites[k].Length;
        if (shape.Aggregate(1, (a, b) => a * b) != values.Length)
            throw new ArgumentException("the values do not fill the grid.", nameof(values));

        double[] current = values;
        for (int k = 0; k < d; k++)
        {
            var axis = new AxisFit(sites[k], lambda);
            current = ApplyAlong(current, shape, k, axis);
            shape[k] = axis.BasisCount;
        }
        return current;
    }

    /// <summary>The arrays of a table payload, fitted: <see cref="ComposedArrays.Table"/> with its coefficients.</summary>
    public static IEnumerable<KeyValuePair<string, RfCore.Data.DataCube>> TableArrays(
        string name, IReadOnlyList<string> inputs, IReadOnlyList<double[]> sites, double[] values, double lambda = 0.0)
        => ComposedArrays.Table(name, inputs, sites, values, Fit(sites, values, lambda));

    private static double[] ApplyAlong(double[] data, int[] shape, int axis, AxisFit fit)
    {
        int n = shape[axis], m = fit.BasisCount;
        int outer = 1, inner = 1;
        for (int k = 0; k < axis; k++) outer *= shape[k];
        for (int k = axis + 1; k < shape.Length; k++) inner *= shape[k];

        var result = new double[outer * m * inner];
        var y = new double[n];
        var c = new double[m];
        for (int o = 0; o < outer; o++)
            for (int r = 0; r < inner; r++)
            {
                for (int i = 0; i < n; i++) y[i] = data[(o * n + i) * inner + r];
                fit.Solve(y, c);
                for (int j = 0; j < m; j++) result[(o * m + j) * inner + r] = c[j];
            }
        return result;
    }

    /// <summary>One axis: its collocation matrix and the factorised normal equations.</summary>
    private sealed class AxisFit
    {
        private const int Band = 3;

        private readonly double[] _sites;
        private readonly int _n;
        private readonly int[] _first = [];     // per site: first nonzero basis index
        private readonly double[] _b = [];      // per site: the four basis values
        private readonly double[,]? _chol;      // banded Cholesky factor, [row, row − col]

        public int BasisCount { get; }

        public AxisFit(double[] sites, double lambda)
        {
            _sites = sites;
            _n = sites.Length;
            BasisCount = CubicBSpline.BasisCount(_n);
            if (_n < 4) return;

            var knots = CubicBSpline.Knots(sites);
            _first = new int[_n];
            _b = new double[_n * 4];
            Span<double> ders = stackalloc double[12];
            for (int i = 0; i < _n; i++)
            {
                int span = CubicBSpline.Span(knots, sites[i]);
                CubicBSpline.BasisDerivatives(knots, span, sites[i], ders);
                _first[i] = span - CubicBSpline.Degree;
                for (int j = 0; j < 4; j++) _b[i * 4 + j] = ders[j];
            }

            int m = BasisCount;
            var a = new double[m, Band + 1];   // lower band of BᵀB + λΩ: a[row, row − col]
            for (int i = 0; i < _n; i++)
                for (int p = 0; p < 4; p++)
                    for (int q = 0; q <= p; q++)
                        a[_first[i] + p, p - q] += _b[i * 4 + p] * _b[i * 4 + q];

            if (lambda > 0) AddRoughness(a, knots, lambda);
            _chol = Cholesky(a, m);
        }

        /// <summary>λ ∫ s″(u)² du over the axis mapped to [0, 1]: <c>L³ ∫ s″(x)² dx</c>, integrated
        /// exactly on each knot interval by two-point Gauss (s″ is linear there).</summary>
        private static void AddRoughness(double[,] a, double[] knots, double lambda)
        {
            double length = knots[^1] - knots[0];
            double w = lambda * length * length * length;
            Span<double> ders = stackalloc double[12];
            double g = 0.5 / Math.Sqrt(3.0);
            for (int span = 3; span < knots.Length - 4; span++)
            {
                double h = knots[span + 1] - knots[span];
                if (h <= 0) continue;
                double mid = 0.5 * (knots[span] + knots[span + 1]);
                foreach (double x in (double[])[mid - g * h, mid + g * h])
                {
                    CubicBSpline.BasisDerivatives(knots, span, x, ders);
                    int first = span - CubicBSpline.Degree;
                    for (int p = 0; p < 4; p++)
                        for (int q = 0; q <= p; q++)
                            a[first + p, p - q] += w * 0.5 * h * ders[8 + p] * ders[8 + q];
                }
            }
        }

        private static double[,] Cholesky(double[,] a, int m)
        {
            var l = new double[m, Band + 1];
            for (int j = 0; j < m; j++)
            {
                double s = a[j, 0];
                for (int k = Math.Max(0, j - Band); k < j; k++) s -= l[j, j - k] * l[j, j - k];
                if (!(s > 0)) throw new InvalidOperationException("the spline's normal equations are not positive definite.");
                double diag = Math.Sqrt(s);
                l[j, 0] = diag;
                for (int i = j + 1; i <= Math.Min(m - 1, j + Band); i++)
                {
                    double t = a[i, i - j];
                    for (int k = Math.Max(0, i - Band); k < j; k++) t -= l[i, i - k] * l[j, j - k];
                    l[i, i - j] = t / diag;
                }
            }
            return l;
        }

        public void Solve(double[] y, double[] c)
        {
            if (_n < 4) { Polynomial(y, c); return; }

            int m = BasisCount;
            Array.Clear(c);
            for (int i = 0; i < _n; i++)
                for (int p = 0; p < 4; p++) c[_first[i] + p] += _b[i * 4 + p] * y[i];

            var l = _chol!;
            for (int i = 0; i < m; i++)
            {
                double t = c[i];
                for (int k = Math.Max(0, i - Band); k < i; k++) t -= l[i, i - k] * c[k];
                c[i] = t / l[i, 0];
            }
            for (int i = m - 1; i >= 0; i--)
            {
                double t = c[i];
                for (int k = i + 1; k <= Math.Min(m - 1, i + Band); k++) t -= l[k, k - i] * c[k];
                c[i] = t / l[i, 0];
            }
        }

        /// <summary>Two or three sites: the line or parabola through them, as the four Bernstein
        /// coefficients of one cubic piece (degree elevation in the local coordinate s ∈ [0, 1]).</summary>
        private void Polynomial(double[] y, double[] c)
        {
            double a0 = y[0], a1, a2 = 0.0;
            if (_n == 2) a1 = y[1] - y[0];
            else
            {
                double sigma = (_sites[1] - _sites[0]) / (_sites[2] - _sites[0]);
                a2 = ((y[1] - y[0]) - sigma * (y[2] - y[0])) / (sigma * sigma - sigma);
                a1 = (y[2] - y[0]) - a2;
            }
            c[0] = a0;
            c[1] = a0 + a1 / 3.0;
            c[2] = a0 + 2.0 * a1 / 3.0 + a2 / 3.0;
            c[3] = a0 + a1 + a2;
        }
    }
}
