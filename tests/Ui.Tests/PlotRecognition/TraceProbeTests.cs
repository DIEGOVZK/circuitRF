// brief-pdg-3-snap-to-curve.md §3 — a point on a traced curve: snapped onto the curve rather than a sample, the nearer
// trace winning, a right-axis trace read through Y2, a chart trace's Γ and frequency, AtX in log x and never beyond the
// range; the dialog's pins, delete, copy and Escape; and the CLI's --probe against the drawn network.
//
// THE SESSIONS ARE BUILT BY HAND (a frame, a linear calibration, a two-segment trace) except the CLI's, which reads the
// dB(S21) picture PlotPictureSet draws: 1 GHz per 100 px across, 0.1 dB per pixel up, so a point's expected values are
// arithmetic on its pixels.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Ui.Recognition;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;
using AxisScale = CircuitRF.Design.Imaging.Plots.AxisScale;
using PlotType = CircuitRF.Design.Imaging.Plots.PlotType;

namespace CircuitRF.Ui.Tests.PlotRecognition;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class TraceProbeTests(PlotPictureSet pictures, ITestOutputHelper output) : IClassFixture<PlotPictureSet>
{
    private static readonly AxisCalibration X = new(AxisScale.Linear, 0, 1e7, 0, 2, 0, true);     // 0.01 GHz a pixel
    private static readonly AxisCalibration Y = new(AxisScale.Linear, 0, -0.1, 0, 2, 0, true);    // 0.1 dB a pixel, up
    private static readonly AxisCalibration Y2 = new(AxisScale.Linear, 180, -0.5, 0, 2, 0, true); // 0.5° a pixel, up

    private static PlotRead Rect(int number = 1) =>
        new(number, 0, new PlotFrame { Type = PlotType.Rectangular, Box = new PixelBox(0, 0, 1000, 1000) },
            new RectCalibration(X, Y, [], [], null, null, null, 1e9, true, true) { Y2 = Y2 }, null, null, false, []);

    private static PlotRead Smith(int number) =>
        new(number, 0, new PlotFrame { Type = PlotType.Smith, Box = new PixelBox(100, 100, 900, 900) }, null,
            new SmithCalibration(500, 500, 400, false, false, 50, true, [], 0), null, false, []);

    /// <summary>A rectangular trace through <paramref name="points"/> (pixels), valued through its own axis.</summary>
    private static PlotTraceRead Trace(int number, PlotRead plot, PlotSide side, params (double X, double Y)[] points)
    {
        var ya = side == PlotSide.Right ? Y2 : Y;
        var samples = points.Select(p => new PlotSample(X.ValueAt(p.X), ya.ValueAt(p.Y), default, p.X, p.Y)).ToList();
        var curve = new TraceCurve(plot.Number, 0xC8463A, TraceStyle.Solid, false, points, 0, [], [], 1);
        return new PlotTraceRead(number, plot.Number, curve, new PlotAssignment(2, 1, side == PlotSide.Right ? PlotQuantityKind.PhaseDeg : PlotQuantityKind.Db, "user"),
                                 samples, ya.PerPixel(0), side == PlotSide.Right ? "°" : "dB", null, side);
    }

    private static PlotSession Session(IReadOnlyList<PlotRead> plots, params PlotTraceRead[] traces) =>
        new([new PlotPictureRead(0, "p.png", "", 1000, 1000, [.. plots.Select(p => p.Number)])], plots, traces, [], [],
            new Dictionary<int, IReadOnlyList<PlotWord>>());

    /// <summary>The two-segment trace every gate starts from: (100,500) → (200,300) → (300,400).</summary>
    private static PlotTraceRead Vee(int number = 1, PlotSide side = PlotSide.Left) =>
        Trace(number, Rect(), side, (100, 500), (200, 300), (300, 400));

    /// <summary>A point 3 px off the first segment, beside its point 30 % along: (130, 440).</summary>
    private static readonly (double X, double Y) Off = (130 + 6 / Math.Sqrt(5), 440 + 3 / Math.Sqrt(5));

    [Fact]
    public void APointOffASegment_ProjectsOntoIt_AndReadsTheInterpolatedValues()
    {
        var hit = TraceProbe.Nearest(Session([Rect()], Vee()), 0, Off.X, Off.Y, 8);
        Assert.NotNull(hit);
        Assert.Equal((130, 440, 3), (Math.Round(hit.Px, 9), Math.Round(hit.Py, 9), Math.Round(hit.DistancePx, 9)));
        // Not either sample's 1 GHz / −50 dB or 2 GHz / −30 dB: the curve at the projected point.
        Assert.Equal(1.3e9, hit.X, 1e-3);
        Assert.Equal(-44, hit.Y, 1e-9);
        Assert.Equal(1.3e9, hit.FrequencyHz!.Value, 1e-3);
        Assert.Null(hit.Value);
        Assert.Equal("T1 · 1.30 GHz · −44.0 dB", TraceProbe.Readout(Session([Rect()], Vee()), hit));
    }

    [Fact]
    public void TheNearerOfTwoTraces_Wins_AndNothingWithinReach_IsNull()
    {
        // T2 is T1 moved 2 px down: the point 3 px off T1's first segment is 2.1 px from T2's.
        var t2 = Trace(2, Rect(), PlotSide.Left, (100, 502), (200, 302), (300, 402));
        var s = Session([Rect()], Vee(), t2);
        Assert.Equal(2, TraceProbe.Nearest(s, 0, Off.X, Off.Y, 8)!.Trace);
        Assert.Null(TraceProbe.Nearest(s, 0, 600, 900, 8));
    }

    [Fact]
    public void ARightAxisTrace_ReadsItsValueThroughY2()
    {
        var hit = TraceProbe.Nearest(Session([Rect()], Vee(side: PlotSide.Right)), 0, Off.X, Off.Y, 8)!;
        Assert.Equal(180 - 0.5 * 440, hit.Y, 1e-9);
    }

    [Fact]
    public void AChartTrace_ReadsGamma_AndFrequencyWhenItHasOne()
    {
        var chart = Smith(1);
        var sc = chart.Smith!;
        PlotTraceRead Chart(int number, bool withFrequency)
        {
            (double X, double Y)[] px = [(500, 300), (700, 500)];
            var samples = px.Select((p, k) => new PlotSample(withFrequency ? (k + 1) * 1e9 : double.NaN, 0, sc.GammaAt(p.X, p.Y), p.X, p.Y)).ToList();
            var curve = new TraceCurve(1, 0x1565C0, TraceStyle.Solid, true, px, 0, [], [], 1);
            return new PlotTraceRead(number, 1, curve, new PlotAssignment(1, 1, PlotQuantityKind.Complex, "chart"), samples, sc.PerPixel, "|Γ|", null);
        }

        // The segment's midpoint is (600, 400): Γ = 0.25 + 0.25j, and halfway from 1 GHz to 2 GHz.
        var s = Session([chart], Chart(1, withFrequency: true));
        var hit = TraceProbe.Nearest(s, 0, 600, 400, 8)!;
        Assert.Equal(new Complex(0.25, 0.25), hit.Value!.Value);
        Assert.Equal(1.5e9, hit.FrequencyHz!.Value, 1e-3);

        var bare = Session([chart], Chart(1, withFrequency: false));
        var none = TraceProbe.Nearest(bare, 0, 600, 400, 8)!;
        Assert.Null(none.FrequencyHz);
        Assert.EndsWith("· no frequency", TraceProbe.Readout(bare, none));
    }

    [Fact]
    public void AtX_OnALogAxis_InterpolatesInLogX_AndIsNullOutsideTheRange()
    {
        // 100 MHz at px 100, 10 GHz at px 300: 1 GHz is halfway in log x, so −10 dB — not the −1.8 dB a linear x gives.
        var logX = new AxisCalibration(AxisScale.Log, 7, 0.01, 0, 2, 0, true);
        var plot = Rect() with { Rect = Rect().Rect! with { X = logX } };
        var samples = new List<PlotSample> { new(1e8, 0, default, 100, 400), new(1e10, -20, default, 300, 600) };
        var curve = new TraceCurve(1, 0xC8463A, TraceStyle.Solid, false, [(100, 400), (300, 600)], 0, [], [], 1);
        var t = new PlotTraceRead(1, 1, curve, new PlotAssignment(2, 1, PlotQuantityKind.Db, "user"), samples, 0.1, "dB", null);
        var s = Session([plot], t);

        var hit = TraceProbe.AtX(s, 1, 1e9)!;
        Assert.Equal(-10, hit.Y, 1e-9);
        Assert.Equal((200, 500), (Math.Round(hit.Px, 9), Math.Round(hit.Py, 9)));
        Assert.Null(TraceProbe.AtX(s, 1, 2e10));
        Assert.Null(TraceProbe.AtX(s, 1, 5e7));
    }

    [Fact]
    public void Dialog_PinDeleteAndCopy_GiveTheTsv_AndEscapeDisarmsThePointTool()
    {
        var session = Session([Rect()], Vee());
        var vm = new PlotSessionViewModel();
        vm.Apply(session, TouchstoneAssembly.Build(session));
        string? status = null;
        vm.StatusRaised += s => status = s;

        Assert.True(vm.Pin(Off.X, Off.Y, 8));
        Assert.False(vm.Pin(600, 900, 8));                 // far from every curve: refused, nothing pinned
        Assert.NotNull(status);
        Assert.True(vm.Pin(200, 299, 8));                  // the corner: 2 GHz, −30 dB
        Assert.True(vm.Pin(250, 350, 8));
        Assert.Equal(["P1", "P2", "P3"], vm.ProbePoints.Select(p => p.Name));
        Assert.Equal("P1 · T1 · 1.30 GHz · −44.0 dB", vm.ProbePoints[0].Text);

        vm.RemoveProbePoint(vm.ProbePoints[2]);
        Assert.Equal("point\ttrace\tx\tx_unit\ty\ty_unit\n"
                   + "P1\tT1\t1.30\tGHz\t-44.0\tdB\n"
                   + "P2\tT1\t2.00\tGHz\t-30.0\tdB\n", vm.PointsTsv());

        string ws = Directory.CreateTempSubdirectory("crf-pdg3-").FullName;
        try
        {
            var dialog = new ImageSourceViewModel(ws, [], 0, null, true, debounce: TimeSpan.Zero, presetDirectory: Path.Combine(ws, "presets"));
            dialog.PointArmed = true;
            Assert.Equal(PlotBoxTool.Point, dialog.BoxTool);
            dialog.CancelTools();                            // the canvas's Escape
            Assert.Equal(PlotBoxTool.None, dialog.BoxTool);
            Assert.False(dialog.PointArmed);
        }
        finally
        {
            try { Directory.Delete(ws, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Cli_Probe_ReadsS21_WithinTheRoundTripBound()
    {
        string png = pictures.Picture("s21db");
        var r = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], ["recognize", png, "--trace", "T1=S21:db", "--probe", "2.4GHz", "--json"]);
        output.WriteLine($"{r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
        Assert.True(r.ExitCode == 0, r.StdErr);
        var probe = Assert.Single(JsonNode.Parse(r.StdOut)!["result"]!["recognize"]!["plots"]!["probes"]!.AsArray())!;
        Assert.Equal(("2.4GHz", 2.4e9, "T1", "dB"), ((string?)probe["at"], (double)probe["x"]!, (string?)probe["trace"], (string?)probe["yUnit"]));

        // The bound PlotRoundTripTests holds the whole trace to: 1.5 × (resolution + what the pixel column spans).
        var s = PlotPictureSet.Read(null, png);
        var t = Assert.Single(s.Traces);
        var x = s.Plots[0].Rect!.X!;
        double truth = ForeignPlots.Answer.Db(ForeignPlots.Answer.S21(2.4e9));
        double bound = 1.5 * (t.Resolution + PlotPictureSet.Spread(v => ForeignPlots.Answer.Db(ForeignPlots.Answer.S21(v)), x, x.PixelAt(2.4e9), t.Curve));
        double read = (double)probe["y"]!;
        output.WriteLine($"read {read:0.###} dB, drawn {truth:0.###} dB, bound {bound:0.###}");
        Assert.True(Math.Abs(read - truth) <= bound, $"read {read} dB, drawn {truth} dB, bound {bound}");
    }
}
