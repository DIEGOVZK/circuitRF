// `circuitrf recognize <picture>…` on plot pictures — brief-img-14-sparameter-plots-to-touchstone.md R-im14-14 (D15: no
// new verb; docs/design/cli.md §26).
//
// THIS FILE OWNS NO PLOT READING (R-im14-15): the reading is PlotReading.Read, the file TouchstoneAssembly.Build and
// WriteFile, the CSV PlotCsv, the overlay ImageOverlay.BuildPlot — the functions Create Touchstone from Plot calls.
// What is here is the flags those take, the refusals a command line makes where the dialog would have waited, and the
// projection of the session into result.recognize.plots.
//
// SEVERAL PICTURES MAKE ONE SESSION: `recognize s11.png s21.png -o part.s2p`. Plots and traces are numbered across the
// session, so `--trace T3=…` and `--x-axis …@2` mean the same thing whichever picture holds them.
//
// THE CSV NEVER WAITS. A digitisation needs no assignment, no frequency and no frequency axis, so --csv is written
// whatever the Touchstone's fate; a call that asked only for the CSV is not refused for the Touchstone it did not ask
// for. Every other call whose Touchstone cannot be made exits 1 with the sentence and the flag that answers it — the
// dialog's highlighted control, spelled as a flag.
//
// NOTHING IS WRITTEN BUT WHAT WAS ASKED, and the Touchstone last: --overlay and --csv (written even when the file is
// refused, because that is when they are most useful), then -o and the kept source pictures beside it.

using System.Text;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Design.Schematic.Recognition;
using RfCore.Export;

namespace CircuitRF.Cli;

internal static partial class Recognize
{
    /// <summary>A Touchstone's extension: <c>.s1p</c> … <c>.s4p</c>.</summary>
    private static bool IsTouchstone(string path) =>
        System.Text.RegularExpressions.Regex.IsMatch(Path.GetExtension(path), @"^\.[sS][1-4][pP]$");

    /// <summary>The flags a plot picture does not take: every layout, schematic and circuit flag.</summary>
    private static IEnumerable<string> NotPlotFlags(Options o)
    {
        foreach (string f in o.Picture.LayoutFlags.Concat(o.Picture.SchematicFlags).Concat(LayoutOnly(o))) yield return f;
        if (o.Into is not null) yield return "--into";
        if (o.Replace) yield return "--replace";
        if (o.Parts is not null) yield return "--parts";
        if (o.PartsOut is not null) yield return "--parts-out";
        if (o.Tech is not null) yield return "--tech";
        if (o.Start is not null) yield return "--start";
        if (o.Stop is not null) yield return "--stop";
        if (o.Npts is not null) yield return "--npts";
        if (o.Digits is not null) yield return "--digits";
        if (o.Picture.Underlay is not null) yield return "--underlay";
    }

    /// <summary>R-im14-14 — one or more plot pictures, read into one session.</summary>
    private static int RecognisePlots(Options o, IReadOnlyList<(string Path, ImageSource Source, ImageKindResult Kind)> pictures)
    {
        if (NotPlotFlags(o).FirstOrDefault() is { } flag)
            return JsonRun.Fail(CliDiagnostics.PictureFlagWrongKind(Verb, flag, "a layout, a schematic picture or a layout picture", "a plot picture"));
        if (o.Output is { } output && !IsTouchstone(output))
            return JsonRun.Fail(CliDiagnostics.RecognizeOutputNotTouchstone(output));

        var inputs = pictures.Select(p => new PlotPictureInput(p.Source, Path.GetFileName(p.Path))).ToList();
        var flags = o.Plot;

        // A typed axis's pixels are the FILE's; a reading of a reduced picture is in the reduced picture's pixels. Which
        // picture a plot is on is known only after a reading, so a pre-pass finds it when the pictures differ.
        Func<int, double> factor = _ => pictures[0].Source.Raster.ReductionFactor;
        if (flags.Clicks.Count > 0 && pictures.Select(p => p.Source.Raster.ReductionFactor).Distinct().Count() > 1)
        {
            var first = PlotReading.Read(inputs, flags.ReadingOptions(_ => 1) with { Axes = [] }, RunHost.Control);
            factor = n => n <= first.Plots.Count ? pictures[first.Plots[n - 1].Picture].Source.Raster.ReductionFactor : 1;
        }
        foreach (var p in pictures) Console.Error.WriteLine($"[circuitRF] reading {p.Path} as a plot picture");
        var session = PlotReading.Read(inputs, flags.ReadingOptions(factor), RunHost.Control);

        // The overlays and the CSV: written whatever the Touchstone's fate.
        for (int i = 0; i < pictures.Count; i++)
            o.Picture.WriteOverlay(Verb, ImageOverlay.BuildPlot(session, i), pictures[i].Source, i + 1, pictures.Count);
        string? csv = null;
        if (flags.Csv is { } csvTarget)
        {
            csv = Path.GetFullPath(csvTarget);
            if (Write(csv, () => PlotCsv.WriteFile(csv, session)) is { } failed) return failed;
            JsonRun.AddOutput("csv", csv);
        }

        var built = TouchstoneAssembly.Build(session, flags.AssemblyOptions());
        string? touchstone = null;
        var kept = new List<string>();
        if (built.Refusal is null && o.Output is { } target)
        {
            touchstone = Path.GetFullPath(target);
            if (!string.Equals(Path.GetExtension(touchstone), built.Extension, StringComparison.OrdinalIgnoreCase))
            {
                JsonRun.Recognize = PlotsDocument(pictures, session, built, null, csv, kept);
                return JsonRun.Fail(CliDiagnostics.RecognizeOutputPorts(touchstone, built.Ports, built.Extension));
            }
            try
            {
                foreach (var k in TouchstoneAssembly.WriteFile(built, touchstone, inputs, flags.KeepSources))
                {
                    kept.Add(k.FullPath);
                    JsonRun.AddOutput("source", k.FullPath);
                }
                Console.Error.WriteLine($"[circuitRF] Wrote {touchstone}");
                JsonRun.AddOutput("touchstone", touchstone);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return JsonRun.Fail(CliDiagnostics.RecognizeOutputFailed(touchstone, ex.Message));
            }
        }

        var text = new StringBuilder();
        foreach (string line in session.Lines()) text.Append(line).Append('\n');
        foreach (string line in built.Report) text.Append(line).Append('\n');
        if (built.Matrix.Count > 0)
        {
            text.Append('\n');
            foreach (var c in built.Matrix)
                text.Append($"S{c.I}{c.J}: {c.Source}{(c.Rule.Length > 0 ? $" ({c.Rule})" : "")}{(c.Phase.Length > 0 ? $"; phase {c.Phase}" : "")}\n");
        }
        if (csv is not null) text.Append("csv: ").Append(csv).Append('\n');
        if (touchstone is not null) text.Append("touchstone: ").Append(touchstone).Append('\n');
        Console.Write(text.ToString());
        JsonRun.Recognize = PlotsDocument(pictures, session, built, touchstone, csv, kept);

        // A call that asked only for the CSV is not refused for a Touchstone it did not ask for.
        if (built.Refusal is { } no && !(csv is not null && o.Output is null))
            return JsonRun.Fail(CliDiagnostics.RecognizePlotRefused(no.Sentence, no.Flag));
        return 0;
    }

    private static RecognizeReportJson PlotsDocument(IReadOnlyList<(string Path, ImageSource Source, ImageKindResult Kind)> pictures,
                                                     PlotSession session, TouchstoneAssemblyResult built, string? touchstone, string? csv,
                                                     IReadOnlyList<string> kept)
    {
        var findings = session.Findings.Select(f => new RecognizeFindingJson(f.Class, 1, f.Sentence, []))
                              .Concat(built.Report.Select(l => new RecognizeFindingJson("touchstone", 1, l, [])))
                              .ToList();
        static PixelBoxJson Box(PixelBox b) => new(b.Left, b.Top, b.Right, b.Bottom);
        static PlotAxisJson? Axis(AxisCalibration? a, IReadOnlyList<AxisLabel> labels) => new(
            a is null ? null : a.Scale.ToString().ToLowerInvariant(), a?.Used ?? 0, a?.Rejected ?? 0, a is null ? null : Math.Round(a.ResidualPx, 3),
            a?.Manual ?? false, [.. labels.Select(l => new PlotTickJson(l.Text, Math.Round(l.Pixel, 2), l.Value, l.Used))]);
        var plots = session.Plots.Select(p => new PlotFrameJson(
            p.Number, p.Picture, PlotKind.Name(p.Type), p.Excluded, p.Calibrated, Box(p.Frame.Box),
            p.Frame.IsChart ? [Math.Round(p.Frame.Cx, 2), Math.Round(p.Frame.Cy, 2), Math.Round(p.Frame.Radius, 2)] : null,
            p.Rect is { } r ? Axis(r.X, r.XLabels) : null, p.Rect is { } ry ? Axis(ry.Y, ry.YLabels) : null,
            p.Rect?.XTitle, p.Rect?.YTitle, p.Rect?.Title,
            p.Rect is { XIsFrequency: true } rx ? (rx.XUnitStated ? "" : "assumed ") + Unit(rx.XUnit) : null,
            p.Smith?.Z0, p.Smith?.Z0Stated, p.Smith?.Admittance,
            p.Rect is { TwoY: true } r2 ? Axis(r2.Y2, r2.Y2Labels) : null, p.Rect?.Y2Title,
            p.Rect?.YSide == PlotSide.Right ? "right" : null)).ToList();
        var traces = session.Traces.Select(t =>
        {
            var xs = t.Samples.Select(s => s.X).Where(x => !double.IsNaN(x)).ToList();
            return new PlotTraceJson(t.Name, t.Plot, t.Curve.Hex, t.Curve.StyleName, t.Assignment.ToString(), t.Assignment.Assigned, t.Evidence,
                                     t.Samples.Count, t.Resolution, t.ResolutionUnit, xs.Count > 0 ? xs.Min() : null, xs.Count > 0 ? xs.Max() : null,
                                     t.Frequency?.Source, t.Frequency?.Direction, t.Curve.Gaps.Count, t.Curve.Bridged, t.Curve.Ambiguous.Count,
                                     t.Parametric ? null : PlotReading.Side(t.Axis), t.AxisEvidence.Length > 0 ? t.AxisEvidence : null);
        }).ToList();
        var markers = session.Markers.Select(m => new PlotMarkerJson(m.Name, m.Plot, m.Trace > 0 ? $"T{m.Trace}" : null, m.FrequencyHz,
                                                                      m.Placed ? Math.Round(m.X, 2) : null, m.Placed ? Math.Round(m.Y, 2) : null,
                                                                      m.Readout)).ToList();
        var matrix = built.Matrix.Select(c => new PlotMatrixCellJson($"S{c.I}{c.J}", c.Source, c.Rule, c.Phase, c.Plotted, c.Estimated)).ToList();
        var json = new RecognizePlotsJson(
            [.. session.Pictures.Select(p => new PlotPictureJson(p.Index, p.Name, p.Sha256, p.Width, p.Height, p.Plots))],
            plots, traces, markers, built.Ports, matrix, built.Grid?.ToString(), touchstone, csv, kept,
            built.Refusal is { } no ? new PlotRefusalJson(no.Kind, no.Sentence, no.Flag) : null,
            built.Health is { } h ? TouchstoneAssembly.HealthLine(h) : null);
        return new RecognizeReportJson(Path.GetFullPath(pictures[0].Path), null, "whole", 0, findings, [], null, null, null, null, false)
        {
            Image = PictureFlags.ImageJson(pictures[0].Source, pictures[0].Kind),
            Plots = json,
        };
    }

    private static string Unit(double multiplier) => multiplier switch
    {
        >= 1e12 => "THz", >= 1e9 => "GHz", >= 1e6 => "MHz", >= 1e3 => "kHz", _ => "Hz",
    };
}
