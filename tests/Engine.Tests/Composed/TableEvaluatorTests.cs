using CircuitRF.Core.Devices.Composed;
using CircuitRF.Engine.Modeling;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>brief-modelcomposer-2 R-mc2-1: the table is a tensor-product cubic spline, C² on every axis,
/// accurate to the cubic's own error bound, with smoothing that reduces to interpolation at λ = 0.</summary>
public sealed class TableEvaluatorTests
{
    private static double F(double x, double y) => Math.Sin(x) * Math.Exp(0.3 * y) + 0.1 * x * y * y;

    // Non-uniform on both axes.
    private static readonly double[] Xs = [.. Enumerable.Range(0, 17).Select(i => 3.0 * Math.Pow(i / 16.0, 1.15))];
    private static readonly double[] Ys = [.. Enumerable.Range(0, 13).Select(i => -1.0 + 2.0 * i / 12.0 + 0.03 * Math.Sin(i))];

    private static double[] Values => [.. Xs.SelectMany(x => Ys.Select(y => F(x, y)))];

    private static TableEvaluator Table(double lambda = 0.0)
        => new([Xs, Ys], TensorSplineFit.Fit([Xs, Ys], Values, lambda));

    [Fact]
    public void ASpline_ReproducesASampledFunction_AndItsGradient_WithinTheCubicsBound()
    {
        // A cubic spline's error is O(h⁴)·max|f⁗| in value and O(h³)·max|f⁗| in slope. Stated with
        // a constant of one: ∂⁴f/∂x⁴ ≤ e^0.3 and ∂⁴f/∂y⁴ ≤ 0.0081·e^0.3 on this box.
        double hx = Xs.Zip(Xs.Skip(1), (a, b) => b - a).Max(), hy = Ys.Zip(Ys.Skip(1), (a, b) => b - a).Max();
        double m4x = Math.Exp(0.3), m4y = 0.0081 * Math.Exp(0.3);
        double valueBound = Math.Pow(hx, 4) * m4x + Math.Pow(hy, 4) * m4y;
        double slopeBound = Math.Pow(hx, 3) * m4x + Math.Pow(hy, 3) * m4y;

        var t = Table();
        var g = new double[2];
        double worstValue = 0, worstSlope = 0;
        for (double x = 0.013; x < 3.0; x += 0.0731)
            for (double y = -0.987; y < 0.98; y += 0.0613)
            {
                double s = t.Evaluate([x, y], g);
                worstValue = Math.Max(worstValue, Math.Abs(s - F(x, y)));
                worstSlope = Math.Max(worstSlope, Math.Abs(g[0] - Math.Cos(x) * Math.Exp(0.3 * y) - 0.1 * y * y));
                worstSlope = Math.Max(worstSlope, Math.Abs(g[1] - 0.3 * Math.Sin(x) * Math.Exp(0.3 * y) - 0.2 * x * y));
            }

        Assert.True(worstValue <= valueBound, $"value error {worstValue:E3} > bound {valueBound:E3}");
        Assert.True(worstSlope <= slopeBound, $"slope error {worstSlope:E3} > bound {slopeBound:E3}");
    }

    [Fact]
    public void TheSecondDerivative_IsContinuousAcrossAKnot()
    {
        var t = Table();
        double knot = Xs[5];          // a simple knot of the not-a-knot spline (sites 2 … n−3)
        double y = 0.37, d = 1e-6;

        // ∂²s/∂x² on each side, as a finite difference of the exact slope taken wholly on that side.
        double Slope(double x) { var g = new double[2]; t.Evaluate([x, y], g); return g[0]; }
        double left  = (Slope(knot - d) - Slope(knot - 2 * d)) / d;
        double right = (Slope(knot + 2 * d) - Slope(knot + d)) / d;

        Assert.True(Math.Abs(left - right) <= 1e-4 * Math.Max(Math.Abs(left), 1.0),
            $"s″ jumps at the knot: {left:R} vs {right:R}");
        // And it is a real second derivative, not zero on both sides.
        Assert.True(Math.Abs(left) > 0.1);
    }

    [Fact]
    public void SmoothingWithLambdaZero_EqualsInterpolation()
    {
        var exact = Table(0.0);
        var smoothed = Table(1e-3);
        double worstExact = 0, worstSmoothed = 0;
        var g = new double[2];
        for (int i = 0; i < Xs.Length; i++)
            for (int j = 0; j < Ys.Length; j++)
            {
                double f = F(Xs[i], Ys[j]);
                worstExact    = Math.Max(worstExact, Math.Abs(exact.Evaluate([Xs[i], Ys[j]], g) - f));
                worstSmoothed = Math.Max(worstSmoothed, Math.Abs(smoothed.Evaluate([Xs[i], Ys[j]], g) - f));
            }

        Assert.True(worstExact <= 1e-12, $"λ = 0 misses a site by {worstExact:E3}");
        Assert.True(worstSmoothed > 1e-6, "λ > 0 left every site untouched — the penalty did nothing");
    }
}
