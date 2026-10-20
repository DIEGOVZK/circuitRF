using CircuitRF.Engine.Modeling;
using Xunit;

namespace CircuitRF.Engine.Tests.Modeling;

/// <summary>
/// brief-modelcomposer-8 §4: [P1] eqs. 6.30/6.31's two paths onto a grid. On a conservative field both equal the charge it
/// came from, to quadrature tolerance; with a deliberate curl κ they differ by exactly what Green's theorem says the
/// rectangle between them encloses, <c>Q_A − Q_B = −κ·(vgs − vgs0)·(vds − vds0)</c>.
/// </summary>
public sealed class ChargeLineIntegralTests
{
    // A non-separable charge with a transcapacitance-like cross term, in the shape the synthetic device uses.
    private static double Q(double g, double d) => 0.3e-12 * g + 0.05e-12 * (g - d) + 0.02e-12 * g * Math.Tanh(d) + 0.04e-12 * Math.Sin(g) * d;
    private static double C1(double g, double d) => 0.35e-12 + 0.02e-12 * Math.Tanh(d) + 0.04e-12 * Math.Cos(g) * d;
    private static double C2(double g, double d) => -0.05e-12 + 0.02e-12 * g / Math.Pow(Math.Cosh(d), 2) + 0.04e-12 * Math.Sin(g);

    private static readonly double[] Vgs = [-2, -1.5, -1, -0.5, 0, 0.5];
    private static readonly double[] Vds = [0, 1, 2.5, 5, 7.5, 10];
    private static readonly (double Vgs, double Vds) Anchor = (-0.7, 3);

    [Fact]
    public void ConservativeField_BothPathsEqualTheCharge()
    {
        var (a, b) = LineIntegral.OntoGrid(C1, C2, Anchor, Vgs, Vds, 1e-24);
        double q0 = Q(Anchor.Vgs, Anchor.Vds), worst = 0;
        for (int i = 0; i < Vgs.Length; i++)
            for (int j = 0; j < Vds.Length; j++)
            {
                double truth = Q(Vgs[i], Vds[j]) - q0;
                int k = i * Vds.Length + j;
                worst = Math.Max(worst, Math.Max(Math.Abs(a[k] - truth), Math.Abs(b[k] - truth)));
            }
        Assert.True(worst < 1e-21, $"{worst:E3} C");    // charges here are ~1e-12 C; tolerance 1e-24 per leg
    }

    [Fact]
    public void CurledField_PathsDifferByTheEnclosedCurl()
    {
        const double kappa = 0.01e-12;                   // ∂C1/∂vds − ∂C2/∂vgs, F/V
        var (a, b) = LineIntegral.OntoGrid((g, d) => C1(g, d) + kappa * d, C2, Anchor, Vgs, Vds, 1e-24);
        for (int i = 0; i < Vgs.Length; i++)
            for (int j = 0; j < Vds.Length; j++)
            {
                double expected = -kappa * (Vgs[i] - Anchor.Vgs) * (Vds[j] - Anchor.Vds);
                int k = i * Vds.Length + j;
                Assert.True(Math.Abs(a[k] - b[k] - expected) < 1e-21, $"({Vgs[i]}, {Vds[j]}): {a[k] - b[k]:E4} vs {expected:E4}");
            }
    }
}
