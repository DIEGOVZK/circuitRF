// brief-img-14-sparameter-plots-to-touchstone.md §4 — the minimum-phase estimate (R-im14-10): a two-pole low-pass is
// minimum phase, so its magnitude alone gives back its phase.

using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Imaging.Plots;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PlotRecognition;

public sealed class MinimumPhaseTests(ITestOutputHelper output)
{
    [Fact]
    public void ATwoPoleLowPass_GivesBackItsPhase_Within2Degrees_AcrossTheMiddle80Percent()
    {
        const double f0 = 3e9, q = 0.7;
        static Complex H(double f) => 1 / new Complex(1 - f / f0 * (f / f0), f / (q * f0));
        var freqs = Enumerable.Range(0, 991).Select(k => 0.1e9 + k * 10e6).ToArray();

        var r = MinimumPhase.FromMagnitude(freqs, [.. freqs.Select(f => H(f).Magnitude)]);

        double band = freqs[^1] - freqs[0];
        Assert.Equal(freqs[0] + 0.1 * band, r.TrustedFromHz, 3);
        Assert.Equal(freqs[^1] - 0.1 * band, r.TrustedToHz, 3);
        double worst = 0;
        for (int k = 0; k < freqs.Length; k++)
        {
            if (freqs[k] < r.TrustedFromHz || freqs[k] > r.TrustedToHz) continue;
            double err = Math.Abs(Math.IEEERemainder(r.Phase[k] - H(freqs[k]).Phase, 2 * Math.PI)) * 180 / Math.PI;
            worst = Math.Max(worst, err);
        }
        output.WriteLine($"worst phase error {worst:0.000}° over {r.TrustedFromHz / 1e9:0.##}–{r.TrustedToHz / 1e9:0.##} GHz");
        Assert.True(worst <= 2, $"{worst}°");
    }
}
