// brief-img-14-sparameter-plots-to-touchstone.md §4 — circuitRF's own plots, read back (PlotPictureSet draws them):
// dB(S21) and phase(S21) against linear frequency, dB(S11) against log frequency, and S11 on a Smith chart with three
// markers, all of the band-pass ForeignPlots.Answer describes.
//
// THE BOUND IS THE READING'S OWN. A pixel is uncertain in both directions: the stated resolution in y, and in x the
// frequency one pixel spans — which costs what the curve moves through in its pixel column and half a pen either side
// (PlotPictureSet.Spread) wherever it is steep. A rectangular comparison's bound is 1.5 × (y resolution + that), both
// taken from the session itself, never a number chosen here; the complex ones use |dS/df| × the pixel's frequency.
// A Smith trace whose frequency is matched by |Γ| along a rectangular plot is placed in frequency only as well as that
// plot's pixel resolves |Γ|, over how fast |Γ| moves: that term joins its bound.
// Measured: dB(S21) at most 0.53 of its bound; the four pictures' .s2p 0.30 of it for S21 (|ΔS21| 0.0063) and 0.81 for
// S11 (|ΔS11| 0.030, at 2.57 GHz just past the resonance, where |S11| turns and the match places frequency worst).

using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Imaging.Plots;
using Xunit;
using Xunit.Abstractions;
using static CircuitRF.Ui.Tests.PlotRecognition.ForeignPlots;

namespace CircuitRF.Ui.Tests.PlotRecognition;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class PlotRoundTripTests(PlotPictureSet pictures, ITestOutputHelper output) : IClassFixture<PlotPictureSet>
{
    private PlotSession Read(params string[] names)
    {
        var s = PlotPictureSet.Read(null, [.. names.Select(pictures.Picture)]);
        foreach (var l in s.Lines()) output.WriteLine(l);
        return s;
    }

    /// <summary>Frequency one pixel spans at <paramref name="f"/> on plot <paramref name="p"/>'s x axis.</summary>
    private static double HzPerPixel(PlotRead p, double f) => p.Rect!.X!.PerPixel(p.Rect.X.PixelAt(f));

    [Fact]
    public void RectangularDbS21_ComesBack_Within1_5xItsResolution_AtEveryGridPoint()
    {
        var s = Read("s21db");
        var t = Assert.Single(s.Traces);
        Assert.Equal((2, 1, PlotQuantityKind.Db), (t.Assignment.I, t.Assignment.J, t.Assignment.Quantity));
        var built = TouchstoneAssembly.Build(s);
        Assert.True(built.Ok, built.Refusal?.Sentence);

        var plot = s.Plots[0];
        double worst = 0;
        for (int k = 0; k < built.Snp!.FrequencyCount; k++)
        {
            double f = built.Snp.Frequencies[k];
            double read = Answer.Db(built.Snp[k][1, 0]), truth = Answer.Db(Answer.S21(f));
            var x = plot.Rect!.X!;
            double bound = 1.5 * (t.Resolution + PlotPictureSet.Spread(v => Answer.Db(Answer.S21(v)), x, x.PixelAt(f), t.Curve));
            worst = Math.Max(worst, Math.Abs(read - truth) / bound);
            Assert.True(Math.Abs(read - truth) <= bound, $"{f / 1e9:0.###} GHz: read {read:0.###} dB, drawn {truth:0.###} dB, bound {bound:0.###}");
        }
        output.WriteLine($"worst share of the bound {worst:0.00} over {built.Snp.FrequencyCount} points");
    }

    [Fact]
    public void SmithS11_ComesBack_Within1PercentOfTheRadius_WithFrequencyFromItsMarkers()
    {
        var s = Read("s11smith");
        var t = Assert.Single(s.Traces);
        Assert.Equal("markers", t.Frequency?.Source);
        // Γ against the drawn locus (|ΔΓ| in units of the radius, which is |Γ| = 1)…
        double locus = t.Samples.Max(p => Enumerable.Range(0, 4001).Min(k => (Answer.S11(1e9 + k * 1e6) - p.Value).Magnitude));
        output.WriteLine($"locus |ΔΓ| {locus:0.0000}");
        Assert.True(locus <= 0.01, $"|ΔΓ| {locus}");
        // …and at each marker's own frequency, where the reading is exact by construction of the frequency.
        foreach (double f in new[] { 1.5e9, 3e9, 4e9 })
        {
            var at = t.Samples.MinBy(p => Math.Abs(p.X - f));
            Assert.True(Math.Abs(at.X - f) < 0.01 * f && (at.Value - Answer.S11(f)).Magnitude <= 0.01,
                        $"{f / 1e9} GHz: read {at.Value} at {at.X / 1e9:0.###} GHz");
        }
    }

    [Fact]
    public void TheFourPictures_MakeAnS2p_WithinTheirResolutions()
    {
        var s = Read("s21db", "s21ph", "s11log", "s11smith");
        Assert.All(s.Traces, t => Assert.True(t.Assignment.Assigned, t.Name));
        var built = TouchstoneAssembly.Build(s);
        Assert.True(built.Ok, built.Refusal?.Sentence);
        foreach (var l in built.Report) output.WriteLine(l);
        Assert.Equal(2, built.Ports);

        var db = s.Traces.Single(t => t.Assignment is { I: 2, Quantity: PlotQuantityKind.Db });
        var ph = s.Traces.Single(t => t.Assignment.IsPhase);
        var smith = s.Traces.Single(t => t.Parametric);
        var s11Db = s.Traces.Single(t => t.Assignment is { I: 1, Quantity: PlotQuantityKind.Db });
        var s11Rect = s.Plots[s11Db.Plot - 1];
        var s21Rect = s.Plots[db.Plot - 1];
        var worst = new double[2];
        for (int k = 0; k < built.Snp!.FrequencyCount; k++)
        {
            double f = built.Snp.Frequencies[k];
            Complex Slope(Func<double, Complex> sf) => (sf(f + 1e6) - sf(f - 1e6)) / 2e6;
            // S21 from dB and phase: |S|·(dB resolution in nepers + phase resolution in radians), plus the x pixel.
            var s21 = Answer.S21(f);
            double b21 = 1.5 * (s21.Magnitude * (db.Resolution * Math.Log(10) / 20 + ph.Resolution * Math.PI / 180)
                                + Slope(Answer.S21).Magnitude * HzPerPixel(s21Rect, f));
            // S11 from the chart, its frequency matched along the log-frequency dB(S11) by |Γ|: the chart's pixel, plus
            // the frequency the match can place — the rectangular plot's x pixel, and its y pixel (and the chart's) over
            // how fast |S11| moves there.
            var s11 = Answer.S11(f);
            double dMag = Math.Abs((Answer.S11(f + 1e6).Magnitude - Answer.S11(f - 1e6).Magnitude) / 2e6);
            double hz = HzPerPixel(s11Rect, f) + (s11.Magnitude * s11Db.Resolution * Math.Log(10) / 20 + smith.Resolution) / dMag;
            double b11 = 1.5 * (smith.Resolution + Slope(Answer.S11).Magnitude * hz);
            double d21 = (built.Snp[k][1, 0] - s21).Magnitude, d11 = (built.Snp[k][0, 0] - Answer.S11(f)).Magnitude;
            worst[0] = Math.Max(worst[0], d11 / b11);
            worst[1] = Math.Max(worst[1], d21 / b21);
            Assert.True(d21 <= b21, $"{f / 1e9:0.###} GHz: |ΔS21| {d21:0.0000} > {b21:0.0000}");
            Assert.True(d11 <= b11, $"{f / 1e9:0.###} GHz: |ΔS11| {d11:0.0000} > {b11:0.0000}");
            // The fills: S12 = S21 (passive), S22 = S11.
            Assert.Equal(built.Snp[k][1, 0], built.Snp[k][0, 1]);
            Assert.Equal(built.Snp[k][0, 0], built.Snp[k][1, 1]);
        }
        output.WriteLine($"worst share of the bound: S11 {worst[0]:0.00}, S21 {worst[1]:0.00}");
    }
}
