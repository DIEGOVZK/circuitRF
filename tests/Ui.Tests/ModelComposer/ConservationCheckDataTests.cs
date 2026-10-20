using CircuitRF.Design.Modeling;
using CircuitRF.Engine.Optimization;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-8 §4: check (a)'s curl map reads ~0 on capacitances that come from charges, and localises a curl
/// planted in one row to where it was planted, leaving the other row alone.
///
/// <para>The data are closed-form capacitances of two non-separable charges at scattered points (a jittered grid), so
/// the truth's curl is exactly zero everywhere but in the planted bump: a Gaussian added to <c>C12</c>, whose curl is
/// <c>−∂b/∂vgs</c> — two lobes either side of its centre along <c>vgs</c>.</para>
/// </summary>
public sealed class ConservationCheckDataTests(ITestOutputHelper output)
{
    private const double A = 0.15e-12, Beta = 0.3, B = 0.05e-12, D = 0.1e-12, E = 0.03e-12, W = 0.3, G1 = -1.0;
    private const double BumpG = -0.8, BumpD = 5, SigmaG = 0.3, SigmaD = 1.0, Height = 0.02e-12;

    private static double S(double g) => g + W * Math.Log(Math.Cosh((g - G1) / W));
    private static double Sp(double g) => 1 + Math.Tanh((g - G1) / W);
    private static double T(double d) => Math.Tanh(d / 2);
    private static double Tp(double d) => 0.5 / Math.Pow(Math.Cosh(d / 2), 2);
    private static double Bump(double g, double d) =>
        Height * Math.Exp(-0.5 * (Math.Pow((g - BumpG) / SigmaG, 2) + Math.Pow((d - BumpD) / SigmaD, 2)));

    /// <summary>Qg = A·S(vgs)·(1 + β·T(vds)) + B·vgd and Qd = −B·vgd + D·vds + E·vgs·T(vds), differentiated by hand.</summary>
    private static ChargeData Data(bool planted)
    {
        var rng = new SplitMix64(8);
        var x = new List<(double, double)>();
        foreach (double g in Enumerable.Range(0, 12).Select(i => -2.0 + 2.5 * i / 11))
            foreach (double d in Enumerable.Range(0, 10).Select(j => 10.0 * j / 9))
                x.Add((g + 0.05 * (rng.NextDouble() - 0.5), Math.Max(0, d + 0.2 * (rng.NextDouble() - 0.5))));
        double[] c11 = [.. x.Select(p => A * Sp(p.Item1) * (1 + Beta * T(p.Item2)) + B)];
        double[] c12 = [.. x.Select(p => A * S(p.Item1) * Beta * Tp(p.Item2) - B + (planted ? Bump(p.Item1, p.Item2) : 0))];
        double[] c21 = [.. x.Select(p => -B + E * T(p.Item2))];
        double[] c22 = [.. x.Select(p => B + D + E * p.Item1 * Tp(p.Item2))];
        return ChargeData.Of(x, c11, c12, c21, c22);
    }

    [Fact]
    public void ConservativeData_ReadsNearZero()
    {
        // Measured: 3.0 % and 0.23 %, the paths 0.02 % — what a network interpolant's own error leaves.
        var r = ConservationCheck.Data(Data(planted: false));
        output.WriteLine($"curl RMS Qg {r.GateRms:P3}, Qd {r.DrainRms:P3}; paths {r.GatePaths:P4}, {r.DrainPaths:P4}");
        Assert.True(r.GateRms < 0.05 && r.DrainRms < 0.05, $"{r.GateRms:P3} {r.DrainRms:P3}");
        Assert.True(r.GatePaths < 0.005 && r.DrainPaths < 0.005, $"{r.GatePaths:P3} {r.DrainPaths:P3}");
    }

    [Fact]
    public void PlantedCurl_IsLocalisedToTheGateRowWhereItWasPlanted()
    {
        // Measured: the peak 2.37 at (−1.09, 4.50); 0.21 at most beyond three widths; the drain row 0.23 %.
        var data = Data(planted: true);
        var r = ConservationCheck.Data(data);
        output.WriteLine($"curl RMS Qg {r.GateRms:P3}, Qd {r.DrainRms:P3}; paths {r.GatePaths:P4}, {r.DrainPaths:P4}");
        int peak = Enumerable.Range(0, data.X.Count).MaxBy(k => Math.Abs(r.GateMap[k]));
        var (g, d) = data.X[peak];
        output.WriteLine($"peak {r.GateMap[peak]:F3} at ({g:F2}, {d:F2})");
        Assert.True(Math.Abs(g - BumpG) < 2 * SigmaG && Math.Abs(d - BumpD) < 2 * SigmaD, $"peak at ({g:F2}, {d:F2})");

        double far = Enumerable.Range(0, data.X.Count)
            .Where(k => Math.Abs(data.X[k].Vgs - BumpG) > 3 * SigmaG || Math.Abs(data.X[k].Vds - BumpD) > 3 * SigmaD)
            .Max(k => Math.Abs(r.GateMap[k]));
        output.WriteLine($"largest beyond three widths {far:F3}");
        Assert.True(far < 0.25 * Math.Abs(r.GateMap[peak]), $"{far:F3} against the peak's {r.GateMap[peak]:F3}");
        Assert.True(r.DrainRms < 0.05, $"the drain row reads {r.DrainRms:P3}");
    }
}
