// brief-pdg-2-curve-fit.md §3 — fitting a curve to a digitised trace. Pure tests on synthetic samples, one per claim, but
// the last, which fits a picture circuitRF plotted and writes it as a Touchstone.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Imaging.Plots;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PlotRecognition;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class TraceFitTests(PlotPictureSet pictures, ITestOutputHelper output) : IClassFixture<PlotPictureSet>
{
    /// <summary>A frequency axis in GHz with a dB trace, one pixel worth <paramref name="resolution"/>.</summary>
    private static TraceFitAxis Ghz(double resolution = 0.01, bool log = false) => new(1e9, "GHz", "f", log, null, resolution, "dB");

    /// <summary><paramref name="n"/> samples of <paramref name="y"/> (of x in GHz) from <paramref name="lo"/> to
    /// <paramref name="hi"/> GHz.</summary>
    private static List<PlotSample> Samples(Func<double, double> y, double lo = 1, double hi = 10, int n = 200) =>
        [.. Enumerable.Range(0, n).Select(i => lo + (hi - lo) * i / (n - 1)).Select((g, i) => new PlotSample(g * 1e9, y(g), default, i, 0))];

    private static void Close(IReadOnlyList<double> want, IReadOnlyList<double> got, double rel)
    {
        Assert.Equal(want.Count, got.Count);
        for (int k = 0; k < want.Count; k++)
            Assert.True(Math.Abs(got[k] - want[k]) <= rel * Math.Abs(want[k]), $"coefficient {k}: {got[k]:R} for {want[k]:R}");
    }

    [Fact]
    public void Linear_Cubic_AndExponential_AreRecoveredFromExactSamples()
    {
        var line = TraceFit.Fit(Samples(f => -0.5 + 0.0123 * f), new TraceFitSpec(TraceFitModel.Linear), Ghz());
        Close([-0.5, 0.0123], line.Coefficients, 1e-9);

        double[] cubic = [-0.512, 0.0123, -4.1e-4, 2e-5];
        var poly = TraceFit.Fit(Samples(f => cubic[0] + cubic[1] * f + cubic[2] * f * f + cubic[3] * f * f * f),
                                new TraceFitSpec(TraceFitModel.Polynomial, 3), Ghz());
        Close(cubic, poly.Coefficients, 1e-9);

        var exp = TraceFit.Fit(Samples(f => 3.2 * Math.Exp(-0.81 * f) + 0.05), new TraceFitSpec(TraceFitModel.Exponential), Ghz());
        Assert.True(exp.Fitted, exp.Refusal);
        Close([3.2, -0.81, 0.05], exp.Coefficients, 1e-9);
        output.WriteLine(exp.Expression);
    }

    [Fact]
    public void ADegreeSixPolynomial_OnXInHertz_IsRecovered()
    {
        // x in hertz, 1e9 … 1e10: on raw x the normal equations' matrix spans ~1e120 and nothing could be solved.
        double[] c = [1.5, -0.8, 0.31, -0.052, 0.0043, -1.7e-4, 2.6e-6];
        double Truth(double hz) { double g = hz / 1e9, y = 0; for (int k = c.Length - 1; k >= 0; k--) y = y * g + c[k]; return y; }
        var samples = Samples(g => Truth(g * 1e9)).Select(s => s).ToList();
        var fit = TraceFit.Fit(samples, new TraceFitSpec(TraceFitModel.Polynomial, 6), new TraceFitAxis(1, "Hz", "f", false, null, 0.01, "dB"));
        Assert.True(fit.Fitted, fit.Refusal);
        double span = samples.Max(s => Math.Abs(s.Y));
        foreach (double hz in Enumerable.Range(0, 37).Select(i => 1e9 + i * 2.5e8))
            Assert.True(Math.Abs(TraceFit.Evaluate(fit, hz) - Truth(hz)) <= 1e-9 * span, $"{hz:G4} Hz");
        Close([.. c.Select((v, k) => v / Math.Pow(1e9, k))], fit.Coefficients, 1e-6);
    }

    [Fact]
    public void TheSplinesDefault_SmoothsToTheReadingsResolution()
    {
        const double res = 0.05;
        var samples = Samples(f => Math.Round((-3 + 2 * Math.Sin(f / 1.3)) / res) * res, 1, 5, 800);
        var fit = TraceFit.Fit(samples, new TraceFitSpec(TraceFitModel.Spline), Ghz(res));
        Assert.True(fit.Fitted, fit.Refusal);
        Assert.True(fit.SmoothingIsResolution);
        Assert.Null(fit.Expression);
        Assert.InRange(fit.RmsResidual, 0.95 * res, 1.05 * res);
        output.WriteLine($"{fit.Readout}; {fit.ResidualText}");
    }

    [Fact]
    public void AnExponential_RefusesASine_WithASentence()
    {
        var fit = TraceFit.Fit(Samples(f => Math.Sin(2 * Math.PI * f / 2), 1, 5), new TraceFitSpec(TraceFitModel.Exponential), Ghz());
        Assert.False(fit.Fitted);
        Assert.False(string.IsNullOrWhiteSpace(fit.Refusal));
        Assert.Null(fit.Expression);
        Assert.True(double.IsNaN(TraceFit.Evaluate(fit, 3e9)));
        output.WriteLine(fit.Refusal);
    }

    [Fact]
    public void Evaluate_OutsideTheTracesRange_IsNaN()
    {
        var fit = TraceFit.Fit(Samples(f => 1 + f, 1, 5), new TraceFitSpec(TraceFitModel.Linear), Ghz());
        Assert.True(double.IsNaN(TraceFit.Evaluate(fit, 0.99e9)));
        Assert.True(double.IsNaN(TraceFit.Evaluate(fit, 5.01e9)));
        Assert.Equal(4, TraceFit.Evaluate(fit, 3e9), 9);
    }

    [Fact]
    public void TheCopiedExpression_EvaluatesInCoresEngine_ToTheFit()
    {
        // Noisy samples, so the coefficients are full-precision numbers rather than the ones typed above.
        var rng = new Random(7);
        var noisy = Samples(f => 3.2 * Math.Exp(-0.81 * f) + 0.05 - 0.4 + 0.02 * f + (rng.NextDouble() - 0.5) * 0.01, 1, 6);
        var fits = new[]
        {
            TraceFit.Fit(noisy, new TraceFitSpec(TraceFitModel.Polynomial, 3), Ghz()),
            TraceFit.Fit(noisy, new TraceFitSpec(TraceFitModel.Exponential), Ghz()),
            TraceFit.Fit(noisy, new TraceFitSpec(TraceFitModel.Polynomial, 2), Ghz(log: true)),
        };
        foreach (var fit in fits)
        {
            Assert.True(fit.Fitted, fit.Refusal);
            Assert.Equal(("f", "GHz"), (fit.XVariable, fit.XUnit));
            output.WriteLine($"{fit.Expression}   ({fit.Readout})");
            foreach (double ghz in new[] { 1.0, 2.2, 3.5, 4.75, 6.0 })
            {
                var scope = new Scope("fit");
                scope.Bind("f", ghz.ToString("R", CultureInfo.InvariantCulture));
                double engine = new Evaluator().Eval(fit.Expression!, scope).AsReal(), mine = TraceFit.Evaluate(fit, ghz * 1e9);
                Assert.True(Math.Abs(engine - mine) <= 1e-9 * Math.Abs(mine), $"{fit.Expression} at {ghz} GHz: {engine:R} vs {mine:R}");
            }
        }
    }

    [Fact]
    public void AnExactSample_IsPassedThroughByTheSpline()
    {
        var rng = new Random(3);
        var samples = Samples(f => Math.Cos(f) + (rng.NextDouble() - 0.5) * 0.02, 1, 5, 400);
        var marker = samples[150] with { Y = samples[150].Y + 0.3, Exact = true };
        samples[150] = marker;
        var fit = TraceFit.Fit(samples, new TraceFitSpec(TraceFitModel.Spline), Ghz(0.02));
        Assert.True(fit.Fitted, fit.Refusal);
        Assert.True(Math.Abs(TraceFit.Evaluate(fit, marker.X) - marker.Y) <= 1e-6, $"{TraceFit.Evaluate(fit, marker.X):R} for {marker.Y:R}");
    }

    [Fact]
    public void ACircuitRFPlot_FittedWithADegreeFourPolynomial_WritesS21NoWorseThanTheReading_ButForTheModelsError()
    {
        var options = new PlotReadingOptions { Traces = [new PlotTraceOverride("solid", PlotQuantity.ParseAssignment("S21:db"))] };
        // A gentle low-pass, not the band-pass: a resonance's hump is the wrong model for a degree-4 polynomial (≈ 8 px RMS
        // on s21db), and the claim is about a curve the model describes — this one to 1e-4 in |S|, under what the reader
        // achieves on a crisp plot.
        var s = PlotPictureSet.Read(options, pictures.Picture("s21lowpass"));
        var read = TouchstoneAssembly.Build(s);
        var fitted = TouchstoneAssembly.Build(s, new TouchstoneAssemblyOptions { Fits = [new PlotFitOverride("all", new TraceFitSpec(TraceFitModel.Polynomial, 4))] });
        Assert.True(read.Ok && fitted.Ok, read.Refusal?.Sentence ?? fitted.Refusal?.Sentence);
        var fit = Assert.Single(fitted.Fits).Value;
        Assert.True(fit.Fitted, fit.Refusal);
        Assert.Contains(fitted.Header, l => l.Contains(" fit: polynomial degree 4", StringComparison.Ordinal));

        double Worst(TouchstoneAssemblyResult r) => Enumerable.Range(0, r.Snp!.FrequencyCount)
            .Max(k => Math.Abs(r.Snp[k][1, 0].Magnitude - PlotPictureSet.LowPassS21(r.Snp.Frequencies[k]).Magnitude));
        double raw = Worst(read), smooth = Worst(fitted);
        output.WriteLine($"{TraceFit.Line(fit)}\nworst |Δ|S21|| read {raw:0.00000}, fitted {smooth:0.00000}");
        // A crisp plot reads almost without noise, and what error is left is the axis calibration's, which the fit carries
        // unchanged (measured: read 0.00592, fitted 0.00593, the fit 0.04 px from the reading). So the fit is held to add
        // nothing beyond its model's own error on this curve, 1.0e-4 in |S| for degree 4, with a factor of two in hand.
        Assert.True(smooth <= raw + 2e-4, $"fitted {smooth:0.00000} > read {raw:0.00000} + the model's 2e-4");
    }
}
