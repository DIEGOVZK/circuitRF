using System.Numerics;
using CircuitRF.Design.Modeling.Passive;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer.Passive;

/// <summary>
/// brief-modelcomposer-12 §4, R-mc12-2: on a known RLC block — R, L and C in series between two ports — the analysis
/// reports reciprocity, passivity and the self-resonant frequency correctly; the same block with S raised 10 % over
/// 4–5 GHz is flagged non-passive at exactly those frequencies.
/// </summary>
public sealed class PassiveAnalysisTests
{
    private const double R = 2, L = 2e-9, C = 1e-12;
    private static readonly double F0 = 1 / (2 * Math.PI * Math.Sqrt(L * C));       // 3.559 GHz
    private static readonly double[] F = SyntheticPassiveData.Log(0.5e9, 8e9, 401);

    private static Complex Z(double f)
    {
        double w = 2 * Math.PI * f;
        return new Complex(R, w * L - 1 / (w * C));
    }

    [Fact]
    public void KnownSeriesRlc_ReciprocalPassiveAndSelfResonant()
    {
        var a = PassiveAnalysis.Run(SyntheticPassiveData.Series(Z, F));

        Assert.True(a.Reciprocity < 1e-12, $"reciprocity {a.Reciprocity:E2}");
        Assert.True(a.Passivity <= 1 + 1e-12, $"largest eigenvalue {a.Passivity}");
        Assert.Empty(a.NonPassiveHz);
        Assert.StartsWith("Reciprocal", a.Lines[1]);
        Assert.StartsWith("Passive", a.Lines[2]);

        // The interpolated reactance crossing, and the vector fit's pole (exact for a series RLC: the poles of Y).
        double srf = Assert.Single(a.SelfResonancesHz);
        Assert.True(Math.Abs(srf / F0 - 1) < 1e-4, $"self-resonance {srf:G6} against {F0:G6}");
        var pole = Assert.Single(a.Resonances);
        Assert.True(Math.Abs(pole.FrequencyHz / F0 - 1) < 1e-6, $"pole {pole.FrequencyHz:G8} against {F0:G8}");
        Assert.Contains(a.Lines, l => l.StartsWith("Self-resonance: series 3.559 GHz"));

        // The low-frequency asymptotes: series R, and the series C the reactance shows at the bottom of the band.
        Assert.True(Math.Abs(a.LowR - R) < 1e-9);
    }

    [Fact]
    public void RaisedS_FlaggedNonPassiveAtThoseFrequencies()
    {
        var b = SyntheticPassiveData.Series(Z, F);
        var raised = new HashSet<double>();
        for (int k = 0; k < F.Length; k++)
        {
            if (F[k] < 4e9 || F[k] > 5e9) continue;
            raised.Add(F[k]);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    b.S[k][i, j] *= 1.1;
        }

        var a = PassiveAnalysis.Run(b);

        Assert.Equal(raised.Order(), a.NonPassiveHz.Order());
        Assert.True(Math.Abs(a.Passivity - 1.21) < 1e-9, $"largest eigenvalue {a.Passivity}");
        Assert.StartsWith($"Not passive: largest eigenvalue of SᴴS 1.21, above 1 at {raised.Count} frequencies (4", a.Lines[2]);
    }
}
