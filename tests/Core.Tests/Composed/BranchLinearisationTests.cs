using System.Globalization;
using CircuitRF.Core.Devices.Composed;

namespace CircuitRF.Core.Tests.Composed;

/// <summary>
/// brief-modelcomposer-22 R-mc22-1, R-mc22-2: one capacitance held at its bias-point value. On a SEPARABLE cubic truth —
/// <c>Qg = Qgs(vgs) + Qgd(vgs − vds)</c>, <c>Qd = −Qgd(vgs − vds) + Qds(vds)</c> — eight-point Gauss–Legendre is exact, so a
/// frozen branch is exactly the model with that piece replaced by its tangent, and all four are exactly <c>LinearC</c>.
/// </summary>
public sealed class BranchLinearisationTests
{
    private static readonly double[] Vq = [-0.7, 4.5];

    // Each piece a cubic p(x) = a·x + b·x² + c·x³, in coulombs.
    private static readonly (double A, double B, double C) Gs = (0.30e-12, 0.04e-12, 0.006e-12),
                                                           Gd = (0.05e-12, -0.008e-12, 0.0011e-12),
                                                           Ds = (0.10e-12, -0.012e-12, 0.0007e-12);

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Cubic((double A, double B, double C) p, string x)
        => $"({R(p.A)}*{x}+{R(p.B)}*{x}*{x}+{R(p.C)}*{x}*{x}*{x})";

    /// <summary>The piece's tangent at <paramref name="x0"/>, as text.</summary>
    private static string Tangent((double A, double B, double C) p, string x, double x0)
    {
        double value = p.A * x0 + p.B * x0 * x0 + p.C * x0 * x0 * x0;
        double slope = p.A + 2 * p.B * x0 + 3 * p.C * x0 * x0;
        return $"({R(value)}+{R(slope)}*({x}-({R(x0)})))";
    }

    private static ComposedCoreModel Core(ChargeBranches tangents, ComposedSwitches? switches = null,
                                          DrainSourceSymmetry symmetry = DrainSourceSymmetry.None)
    {
        double vgsQ = Vq[0], vdsQ = Vq[1];
        string gs  = (tangents & ChargeBranches.Cgs) != 0 ? Tangent(Gs, "vgs", vgsQ) : Cubic(Gs, "vgs");
        string gd  = (tangents & ChargeBranches.Cdg) != 0 ? Tangent(Gd, "(vgs-vds)", vgsQ - vdsQ) : Cubic(Gd, "(vgs-vds)");
        string ds  = (tangents & ChargeBranches.Cds) != 0 ? Tangent(Ds, "vds", vdsQ) : Cubic(Ds, "vds");
        var m = ComposedTestModels.Model();
        var model = m with
        {
            Core = m.Core with
            {
                Qg = Constituent.Equation($"{gs}+{gd}", ["vgs", "vds"]),
                Qd = Constituent.Equation($"-{gd}+{ds}", ["vgs", "vds"]),
                Symmetry = symmetry,
            },
        };
        var core = new ComposedCoreModel(ComposedModelCache.Load(ComposedTestModels.WriteTemp(model)), 4, 25, switches);
        if (core.NeedsBiasPoint) Assert.True(core.FreezeAt(Vq));
        return core;
    }

    private static ComposedSwitches Freeze(ChargeBranches b) => new()
    {
        LinearCgs = (b & ChargeBranches.Cgs) != 0, LinearCdg = (b & ChargeBranches.Cdg) != 0,
        LinearCds = (b & ChargeBranches.Cds) != 0, LinearCm = (b & ChargeBranches.Cm) != 0,
    };

    private static IEnumerable<double[]> Points(int seed, int count)
    {
        var rng = new Random(seed);
        for (int t = 0; t < count; t++) yield return [-2.5 + 3 * rng.NextDouble(), 10 * rng.NextDouble()];
    }

    private static void Same(NonlinearResult expected, NonlinearResult actual, double[] v, string what)
    {
        for (int p = 0; p < 2; p++)
        {
            Assert.True(Math.Abs(actual.Q[p] - expected.Q[p]) <= 1e-12 * Math.Max(Math.Abs(expected.Q[p]), 1e-13),
                $"{what}: q{p} at ({v[0]:G4}, {v[1]:G4}): {actual.Q[p]:R} vs {expected.Q[p]:R}");
            for (int j = 0; j < 2; j++)
                Assert.True(Math.Abs(actual.Dc[p, j] - expected.Dc[p, j]) <= 1e-12 * Math.Max(Math.Abs(expected.Dc[p, j]), 1e-14),
                    $"{what}: dc[{p},{j}] at ({v[0]:G4}, {v[1]:G4}): {actual.Dc[p, j]:R} vs {expected.Dc[p, j]:R}");
        }
    }

    public static TheoryData<int> Sets => [(int)ChargeBranches.Cgs, (int)ChargeBranches.Cdg, (int)ChargeBranches.Cds,
                                           (int)ChargeBranches.Cm, (int)(ChargeBranches.Cgs | ChargeBranches.Cds)];

    [Theory]
    [MemberData(nameof(Sets))]
    public void AFrozenBranch_IsThatPieceReplacedByItsTangent(int set)
    {
        var b = (ChargeBranches)set;
        var frozen = Core(ChargeBranches.None, Freeze(b));
        var hand   = Core(b);   // Cm is zero in a separable charge: freezing it changes nothing
        foreach (var v in Points(22 + set, 10))
            Same(hand.Evaluate(new PortVoltages(v)), frozen.Evaluate(new PortVoltages(v)), v, b.ToString());
    }

    [Fact]
    public void AllFour_AreLinearC()
    {
        var four   = Core(ChargeBranches.None, Freeze(ChargeBranches.All));
        var linear = Core(ChargeBranches.None, new ComposedSwitches { LinearC = true });
        foreach (var v in Points(4, 10))
            Same(linear.Evaluate(new PortVoltages(v)), four.Evaluate(new PortVoltages(v)), v, "all four");
    }

    /// <summary>R-mc22-4: under <c>Exchange</c> the construction acts on the terminal-frame charge, so a line from
    /// <c>vds_Q &gt; 0</c> to <c>vds &lt; 0</c> crosses the image with nothing special — its Jacobian still the derivative of its
    /// charge.</summary>
    [Fact]
    public void UnderExchange_ALineAcrossVdsZero_KeepsTheJacobianExact()
    {
        var core = Core(ChargeBranches.None, new ComposedSwitches { LinearCds = true }, DrainSourceSymmetry.Exchange);
        foreach (var v in new double[][] { [-1.1, -0.8], [-0.2, -2.5], [0.3, -4] })
        {
            double error = ComposedTestModels.IntrinsicJacobianError(core, v);
            Assert.True(error <= 1e-6, $"dc vs FD at ({v[0]}, {v[1]}): {error:E2} of the largest entry");
        }
    }
}
