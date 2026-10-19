// brief-pdg-1-two-y-axes.md §3 — a y axis on the right, alone or beside the left one. The pictures are circuitRF's own
// plotter's (PlotPictureSet): dB(S21) left and phase(S21) right, dB(S21) left and dB(S11) right, and phase(S21) on a
// right-hand axis with everything left of the frame painted out. The plotter colours each axis's title like its trace,
// which is the colour evidence R-pdg1-2 (3) reads.
//
// THE BOUND IS PlotRoundTripTests': 1.5 × (the trace's own y resolution + what its pixel column cannot tell apart), both
// from the session, never a number chosen here.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Ui.Recognition;
using Xunit;
using Xunit.Abstractions;
using static CircuitRF.Ui.Tests.PlotRecognition.ForeignPlots;

namespace CircuitRF.Ui.Tests.PlotRecognition;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class TwoYAxesTests(PlotPictureSet pictures, ITestOutputHelper output) : IClassFixture<PlotPictureSet>
{
    private PlotSession Read(string name)
    {
        var s = PlotPictureSet.Read(null, pictures.Picture(name));
        foreach (var l in s.Lines()) output.WriteLine(l);
        return s;
    }

    [Fact]
    public void DbLeft_PhaseRight_BothComeBackWithinTheirResolution_EachOnItsOwnAxis()
    {
        var s = Read("two-y");
        var r = s.Plots[0].Rect!;
        Assert.NotNull(r.Y2);
        Assert.Contains(s.Traces, t => t.Axis == PlotSide.Left);
        Assert.Contains(s.Traces, t => t.Axis == PlotSide.Right);
        Assert.DoesNotContain(s.Traces, t => t.AxisAmbiguous);

        foreach (var t in s.Traces)
        {
            Func<double, double> truth = t.Axis == PlotSide.Left
                ? f => Answer.Db(Answer.S21(f))
                : f => Answer.S21(f).Phase * 180 / Math.PI;
            double worst = 0;
            foreach (var p in t.Samples)
            {
                double bound = 1.5 * (t.Resolution + PlotPictureSet.Spread(truth, r.X!, p.Px, t.Curve));
                double d = Math.Abs(p.Y - truth(p.X));
                worst = Math.Max(worst, d / bound);
                Assert.True(d <= bound, $"{t.Name} ({t.Axis}) at {p.X / 1e9:0.###} GHz: read {p.Y:0.###}, drawn {truth(p.X):0.###}, bound {bound:0.###}");
            }
            output.WriteLine($"{t.Name} {t.Axis} ({t.AxisEvidence}): worst share of the bound {worst:0.00}");
        }
    }

    [Fact]
    public void ARightOnlyPlot_IsCalibratedFromItsRightLabels_AsY()
    {
        var s = Read("right-only");
        var p = s.Plots[0];
        var r = p.Rect!;
        Assert.NotNull(r.Y);
        Assert.Equal(PlotSide.Right, r.YSide);
        Assert.Null(r.Y2);
        Assert.All(r.YLabels, l => Assert.True(l.Box.Left > p.Frame.Right, l.Text));
    }

    [Fact]
    public void APlotWithNoRightLabels_HasNoY2_AndEveryTraceIsLeft()
    {
        var s = Read("s21db");
        Assert.Null(s.Plots[0].Rect!.Y2);
        Assert.All(s.Traces, t => Assert.Equal((PlotSide.Left, ""), (t.Axis, t.AxisEvidence)));
    }

    [Fact]
    public void ARightTitleInATracesColour_PutsThatTraceOnTheRight()
    {
        // Both axes are dB: neither the quantity nor the titles' words can tell the traces apart; only the colour can.
        var s = Read("two-y-db");
        var blue = s.Traces.MinBy(t => PlotInk.Distance(t.Curve.Rgb, 0x0000FF))!;
        Assert.Equal((PlotSide.Right, "colour"), (blue.Axis, blue.AxisEvidence));
    }

    [Fact]
    public void ALabelRightOfTheFrame_IsY2_ButYOnARightOnlyPlot()
    {
        PlotAxis Clicked(string picture, string id)
        {
            var s = Read(picture);
            var item = ImageOverlay.BuildPlot(s, 0).Items.Single(i => i.Id == id);
            Assert.True(item.Bounds.CentreX > s.Plots[0].Frame.Right, id);
            return PlotAxisLabels.Find(s, 0, item)!.Axis;
        }
        Assert.Equal(PlotAxis.Y2, Clicked("two-y", "tick:1:2000"));
        Assert.Equal(PlotAxis.Y, Clicked("right-only", "tick:1:1000"));
    }

    [Fact]
    public void TheCsv_EndsWithEachTracesYAxis()
    {
        var s = Read("two-y");
        var lines = PlotCsv.Write(s).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#')).ToList();
        Assert.Equal("y_axis", lines[0].Split(',')[^1]);
        foreach (var t in s.Traces)
            Assert.All(lines.Skip(1).Where(l => l.StartsWith(t.Name + ",", StringComparison.Ordinal)),
                       l => Assert.Equal(PlotReading.Side(t.Axis), l.Split(',')[^1]));
        Assert.Contains(lines, l => l.EndsWith(",right", StringComparison.Ordinal));
    }
}
