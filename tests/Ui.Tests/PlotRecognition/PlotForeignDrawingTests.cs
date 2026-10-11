// brief-img-14-sparameter-plots-to-touchstone.md §4 — plots drawn another way: the committed pictures ForeignPlots
// draws (shipped fonts only), each read back against the band-pass it was drawn from. One case per claim. A curve's
// bound is PlotRoundTripTests': 1.5 × (its y resolution + what the curve moves through within its pixel column and half
// a pen either side, PlotPictureSet.Spread).

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Imaging.Plots;
using Xunit;
using AxisScale = CircuitRF.Design.Imaging.Plots.AxisScale;
using Xunit.Abstractions;
using static CircuitRF.Ui.Tests.PlotRecognition.ForeignPlots;

namespace CircuitRF.Ui.Tests.PlotRecognition;

public sealed class PlotForeignDrawingTests(ITestOutputHelper output)
{
    private PlotSession Read(string name, PlotReadingOptions? options = null)
    {
        var s = PlotPictureSet.Read(options, FixturePaths.Require(Path.Combine(Folder, name)));
        foreach (var l in s.Lines()) output.WriteLine(l);
        return s;
    }

    /// <summary>Every pixel of <paramref name="t"/> against <paramref name="truth"/> (dB against frequency), but — when
    /// <paramref name="clearOfFrame"/> — those within a pen of the frame's top or bottom: a curve in the frame's own
    /// colour loses its pixels on the frame line there, and its centre is read off what is left.</summary>
    private void Follows(PlotSession s, PlotTraceRead t, Func<double, double> truth, bool clearOfFrame = false)
    {
        var frame = s.Plots[t.Plot - 1].Frame;
        var x = s.Plots[t.Plot - 1].Rect!.X!;
        double worst = 0;
        foreach (var p in t.Samples)
        {
            double margin = frame.Pen + 2 * t.Curve.Width + 1;
            if (clearOfFrame && (p.Py - frame.Top < margin || frame.Bottom - p.Py < margin)) continue;
            double bound = 1.5 * (t.Resolution + PlotPictureSet.Spread(truth, x, p.Px, t.Curve));
            worst = Math.Max(worst, Math.Abs(p.Y - truth(p.X)) / bound);
            Assert.True(Math.Abs(p.Y - truth(p.X)) <= bound, $"{t.Name} at {p.X / 1e9:0.###} GHz: {p.Y:0.###} against {truth(p.X):0.###}");
        }
        output.WriteLine($"{t.Name}: worst share of the bound {worst:0.00}");
    }

    private static double S21Db(double f) => Answer.Db(Answer.S21(f));

    [Fact]
    public void ADarkBackground_Reads()
    {
        var s = Read("plot-dark.png");
        Follows(s, Assert.Single(s.Traces), S21Db);
    }

    [Fact]
    public void ALogFrequencyAxis_Labelled1e9_ReadsLogInHertz()
    {
        var s = Read("plot-log-1e9.png");
        var x = s.Plots[0].Rect!.X!;
        Assert.Equal(AxisScale.Log, x.Scale);
        Follows(s, Assert.Single(s.Traces), S21Db);
    }

    /// <summary>Two black curves, solid and dashed, crossing once: told apart by style, and neither swaps onto the
    /// other at the crossing.</summary>
    [Fact]
    public void TwoBlackTraces_SolidAndDashed_AreToldApart_AcrossTheirCrossing()
    {
        var s = Read("plot-two-black.png", new PlotReadingOptions
        {
            Traces = [new("solid", PlotQuantity.ParseAssignment("S21:db")!), new("dashed", PlotQuantity.ParseAssignment("S21:db")!)],
        });
        Assert.Equal(2, s.Traces.Count);
        Follows(s, s.Traces.Single(t => t.Curve.Style == TraceStyle.Solid), S21Db, clearOfFrame: true);
        Follows(s, s.Traces.Single(t => t.Curve.Style == TraceStyle.Dashed), f => Answer.Db(Answer.S21LowPass(f)), clearOfFrame: true);
    }

    [Fact]
    public void ALegendInsideThePlot_NamesEachTrace()
    {
        var s = Read("plot-legend.png");
        var red = s.Traces.Single(t => t.Curve.Rgb >> 16 > 0xa0);
        var blue = s.Traces.Single(t => (t.Curve.Rgb & 0xff) > 0xa0);
        Assert.Equal((1, 1), (red.Assignment.I, red.Assignment.J));
        Assert.Equal((2, 1), (blue.Assignment.I, blue.Assignment.J));
        Assert.StartsWith("legend", red.Assignment.Evidence);
        // The notch is drawn clamped to the plot's −30 dB floor.
        Follows(s, red, f => Math.Max(-30, Answer.Db(Answer.S11(f))));
        Follows(s, blue, S21Db);
    }

    [Fact]
    public void AnAdmittanceSmithChart_ReadsGamma()
    {
        var s = Read("smith-y.png", new PlotReadingOptions { Frequencies = [new("T1", 1e9, 5e9)] });
        Assert.True(s.Plots[0].Smith!.Admittance);
        var t = Assert.Single(s.Traces);
        Assert.Equal((1, 1, PlotQuantityKind.Complex), (t.Assignment.I, t.Assignment.J, t.Assignment.Quantity));
        double locus = t.Samples.Max(p => Enumerable.Range(0, 4001).Min(k => (Answer.S11(1e9 + k * 1e6) - p.Value).Magnitude));
        output.WriteLine($"locus |ΔΓ| {locus:0.0000}");
        Assert.True(locus <= 0.01, $"|ΔΓ| {locus}");
    }

    /// <summary>A JPEG at quality 60 blurs the line into parallel same-colour tracks and a darker core: it is still one
    /// curve.</summary>
    [Fact]
    public void AJpegAtQuality60_ReadsAsOneTrace()
    {
        var s = Read("plot-jpeg60.png");
        var t = Assert.Single(s.Traces);
        Assert.Equal(TraceStyle.Solid, t.Curve.Style);
        Follows(s, t, S21Db);
    }
}
