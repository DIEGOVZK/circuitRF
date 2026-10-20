using System.Numerics;
using CircuitRF.Engine.Modeling;
using Xunit;

namespace CircuitRF.Engine.Tests.Modeling;

/// <summary>
/// brief-modelcomposer-12 §4: vector fitting recovers the poles of a known rational function — two lightly damped pairs
/// and a real pole shared by three responses, with constant and proportional terms — to 1e-8, and order selection by
/// error stops at the true order.
/// </summary>
public sealed class VectorFitTests
{
    private static readonly Complex[] Poles =
    [
        new(-2 * Math.PI * 0.4e9, 0),
        new(-1.2e8, 2 * Math.PI * 1.1e9), new(-1.2e8, -2 * Math.PI * 1.1e9),
        new(-6e8, 2 * Math.PI * 3.7e9), new(-6e8, -2 * Math.PI * 3.7e9),
    ];

    private static Complex[] Residues(double scale, double phase) =>
    [
        scale * 3e8,
        scale * Complex.FromPolarCoordinates(5e8, phase), scale * Complex.FromPolarCoordinates(5e8, -phase),
        scale * Complex.FromPolarCoordinates(2e9, 0.3 - phase), scale * Complex.FromPolarCoordinates(2e9, phase - 0.3),
    ];

    private static (double[] F, Complex[][] Data) Samples()
    {
        double[] f = [.. Enumerable.Range(0, 201).Select(k => 20e6 + k * (6e9 - 20e6) / 200)];
        var res = new[] { Residues(1, 0.7), Residues(-0.4, 1.9), Residues(0.25, -0.5) };
        double[] d = [0.02, -0.01, 0.005], e = [1e-12, 0, 3e-13];
        var data = new Complex[3][];
        for (int m = 0; m < 3; m++)
        {
            data[m] = new Complex[f.Length];
            for (int k = 0; k < f.Length; k++)
            {
                var s = new Complex(0, 2 * Math.PI * f[k]);
                Complex v = d[m] + s * e[m];
                for (int p = 0; p < Poles.Length; p++) v += res[m][p] / (s - Poles[p]);
                data[m][k] = v;
            }
        }
        return (f, data);
    }

    [Fact]
    public void KnownRationalFunction_PolesRecoveredTo1e8()
    {
        var (f, data) = Samples();
        var fit = VectorFit.Fit(f, data, Poles.Length);

        var got = fit.Model.Poles.OrderBy(p => p.Magnitude).ThenBy(p => p.Imaginary).ToArray();
        var want = Poles.OrderBy(p => p.Magnitude).ThenBy(p => p.Imaginary).ToArray();
        Assert.Equal(want.Length, got.Length);
        for (int k = 0; k < want.Length; k++)
            Assert.True((got[k] - want[k]).Magnitude / want[k].Magnitude < 1e-8, $"pole {k}: {got[k]} against {want[k]}");
        Assert.True(fit.RelativeError < 1e-10, $"relative error {fit.RelativeError:E2}");

        var byError = VectorFit.FitByError(f, data, 1e-6, 10);
        Assert.Equal(Poles.Length, byError.Order);
    }

    [Fact]
    public void FewSamples_OrderCappedAtWhatTheyDetermine()
    {
        var (f, data) = Samples();
        double[] few = [.. f.Where((_, k) => k % 50 == 0)];          // 5 samples: order 4 at most
        var sub = data.Select(d => d.Where((_, k) => k % 50 == 0).ToArray()).ToArray();

        var fit = VectorFit.FitByError(few, sub, 1e-12, 12);

        Assert.True(fit.Order <= VectorFit.MaxOrder(few.Length, VectorFitSettings.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => VectorFit.Fit(few, sub, 5));
    }
}
