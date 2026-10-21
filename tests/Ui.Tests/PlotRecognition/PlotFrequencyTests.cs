// brief-img-14-sparameter-plots-to-touchstone.md §4 — frequency along a Smith trace (R-im14-8), on a uniform line's
// reflection drawn by circuitRF's plotter (PlotPictureSet): a 25 Ω load a quarter wave long at 1 GHz, 0.5–1.5 GHz, for
// which frequency is exactly proportional to the angle swept — so swept-angle interpolation has a right answer to meet.

using System.Linq;
using CircuitRF.Design.Imaging.Plots;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PlotRecognition;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class PlotFrequencyTests(PlotPictureSet pictures, ITestOutputHelper output) : IClassFixture<PlotPictureSet>
{
    private PlotSession Read(string name, PlotReadingOptions? options = null)
    {
        var s = PlotPictureSet.Read(options, pictures.Picture(name));
        foreach (var l in s.Lines()) output.WriteLine(l);
        return s;
    }

    /// <summary>Every sample of the one trace lies on the line at its own frequency, within 1 % of the radius.</summary>
    private void OnTheLine(PlotTraceRead t, bool clockwise)
    {
        double worst = t.Samples.Max(p => (PlotPictureSet.Line(p.X, clockwise) - p.Value).Magnitude);
        output.WriteLine($"{t.Name}: worst |ΔΓ| at its frequency {worst:0.0000}, {t.Frequency}");
        Assert.True(worst <= 0.01, $"|ΔΓ| {worst}");
    }


    [Fact]
    public void NoMarkersAndNoMatchingRectangularTrace_WaitsForATypedRange_ThenTurnsClockwise()
    {
        var waiting = Read("line-cw");
        Assert.Null(Assert.Single(waiting.Traces).Frequency);
        var refused = TouchstoneAssembly.Build(waiting).Refusal;
        Assert.Equal("frequency", refused?.Kind);
        Assert.StartsWith("--freq T1=", refused!.Flag);

        var typed = Read("line-cw", new PlotReadingOptions { Frequencies = [new("T1", 0.5e9, 1.5e9)] });
        var t = Assert.Single(typed.Traces);
        Assert.Equal(("typed range", "clockwise"), (t.Frequency?.Source, t.Frequency?.Direction));
        OnTheLine(t, clockwise: true);
    }

}
