// brief-img-14-sparameter-plots-to-touchstone.md §4 — a Smith chart's frame (R-im14-4): an impedance chart and an
// admittance chart of the same reflection read the same Γ, and a chart stating no Z0 is read at 50 Ω, said so.

using System.IO;
using System.Linq;
using CircuitRF.Design.Imaging.Plots;
using Xunit;
using Xunit.Abstractions;
using static CircuitRF.Ui.Tests.PlotRecognition.ForeignPlots;

namespace CircuitRF.Ui.Tests.PlotRecognition;

public sealed class SmithFrameTests(ITestOutputHelper output)
{
    private PlotSession Read(string name)
    {
        var s = PlotPictureSet.Read(new PlotReadingOptions { Frequencies = [new("T1", 1e9, 5e9)] },
                                    FixturePaths.Require(Path.Combine(Folder, name)));
        foreach (var l in s.Lines()) output.WriteLine(l);
        return s;
    }

    [Fact]
    public void AnImpedanceAndAnAdmittanceChart_GiveTheSameGamma_ForTheSamePoint()
    {
        var z = Read("smith-z.png");
        var y = Read("smith-y.png");
        Assert.False(z.Plots[0].Smith!.Admittance);
        Assert.True(y.Plots[0].Smith!.Admittance);

        // Where the generator drew S11 at 2 GHz (centre 320, 320; radius 260): both charts read that Γ there.
        var g = Answer.S11(2e9);
        double px = 320 + g.Real * 260, py = 320 - g.Imaginary * 260;
        Assert.True((z.Plots[0].Smith!.GammaAt(px, py) - g).Magnitude < 0.005);
        Assert.True((y.Plots[0].Smith!.GammaAt(px, py) - g).Magnitude < 0.005);

        // …and the two curves are one curve.
        var a = Assert.Single(z.Traces).Samples;
        var b = Assert.Single(y.Traces).Samples;
        double apart = a.Max(p => b.Min(q => (p.Value - q.Value).Magnitude));
        output.WriteLine($"the two readings lie within {apart:0.0000} of each other");
        Assert.True(apart < 0.005, $"{apart}");
    }

    [Fact]
    public void AChartWithNoZ0Word_Reads50Ohm_AndSaysSo()
    {
        var s = Read("smith-z.png");
        var smith = s.Plots[0].Smith!;
        Assert.Equal(50, smith.Z0);
        Assert.False(smith.Z0Stated);
        Assert.Contains(s.Findings, f => f.Class == "assumption" && f.Sentence.Contains("50 Ω assumed"));

        var built = TouchstoneAssembly.Build(s);
        Assert.True(built.Ok, built.Refusal?.Sentence);
        Assert.Contains(built.Header, l => l.Contains("Z0 50 ohm (assumed)"));
    }
}
