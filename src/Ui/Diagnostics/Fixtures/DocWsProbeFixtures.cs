using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Views.DataDisplay;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The WSProbe figures of <c>reference/wsprobe.html</c>, and the example designs they are taken
/// from (brief-wsprobe-7 §3).
///
/// <para><b>The designs are not copied here.</b> Every one of them is a committed
/// <c>testdata/</c> netlist that a WSProbe brief's own gate already runs, and this file reads that
/// file. A second copy under <c>src/Ui</c> would agree with the first on the day it was written and
/// drift silently afterwards — and the point of a documentation figure is that it is the picture the
/// program actually produces. <see cref="Designs"/> is the catalogue, and each row says which page
/// section and which gate its design serves so that neither can be orphaned quietly.</para>
///
/// <para><b>What IS shaped here is the analysis card</b>, and only where the figure needs a
/// different sweep from the gate's — the harmonic-balance fan needs a small-signal sweep and a drive
/// sweep that the gate supplies from the test rather than from the file. That is the same departure
/// <see cref="DocRunData.Loadpull"/> already makes and for the same reason: the circuit is the
/// committed one, the analysis is the edit a reader would make in the Analyses panel.</para>
/// </summary>
public static class DocWsProbeFixtures
{
    /// <summary>One example design: where it lives, what the figure needs of its analysis, and what
    /// it is for.</summary>
    /// <param name="Key">The results file stem, and the id <see cref="Run"/> is asked for.</param>
    /// <param name="Path">Relative to the repository root.</param>
    /// <param name="AnalysisLines">Replaces every <c>analysis</c> line in the file when non-null.</param>
    /// <param name="Serves">Which page section and which brief gate this design is here for.</param>
    private readonly record struct Design(
        string Key, string Path, string? AnalysisLines, string Serves);

    /// <summary>
    /// The five example designs of brief-wsprobe-7 §3. <b>Every row names the page section and the
    /// gate it serves</b>, so a design that stops being cited is visible rather than merely unused.
    /// </summary>
    private static readonly IReadOnlyList<Design> Designs =
    [
        new("WspSeriesResonator", "testdata/wsprobe/series_resonator.cnl", null,
            "wsprobe.html#reading (the two loci) — WSP-1 gate (b), the reference document's Fig. 31."),

        new("WspParallelResonator", "testdata/wsprobe/parallel_resonator.cnl", null,
            "wsprobe.html#reading (the masking counterpart) — WSP-1 gate (c), Fig. 34."),

        new("WspMarginResonator", "testdata/wsprobe/margin_split_resonator.cnl", null,
            "wsprobe.html#stability-margin (the margin notch) — WSP-9 §3."),

        new("WspTwoStage", "testdata/wsprobe/two_stage_terms.cnl", null,
            "wsprobe.html#envelope (the envelope card) — WSP-3 gate (e), WSP-9 gate (f)."),

        new("WspOhtomo", "testdata/ndf/ohtomo_type_a_ndf.cnl", null,
            "wsprobe.html#envelope (the margin/NDF envelope) — WSP-9 gate (i), WSP-6 gate (k)."),

        // The varactor's committed analysis is a single HB solve; the fan is that solve swept over
        // the pump, with the small-signal sweep the gate supplies from the test. 40 points over
        // 0.6-1.4 GHz deliberately steps OVER f0/2 = 1 GHz, where the analysis reports NaN by
        // design — the crossing is found by the samples either side of it.
        new("WspHbFan", "testdata/wsprobe/hb_varactor_divider.cnl",
            "analysis HB1 type=hb Tone=f0 MaxHarm=6 Tol=1e-12 "
          + "SSStart=0.6 SSStop=1.4 SSNpts=40 SSUnit=GHz MarginThreshold=none\n"
          + "analysis SW1 type=parametric_sweep Inner=HB1 Var=Vpump Start=1 Stop=9 Npts=5",
            "wsprobe.html#hb (the drive-swept fan) — WSP-5 gate (e)."),

        new("WspNdfK", "testdata/ndf/hidden_pole_two_port.cnl", null,
            "wsprobe.html#ndf (K is not enough) — WSP-6 gate (i)."),
    ];

    /// <summary>
    /// The repository root, found by walking up from this assembly's own location until
    /// <c>circuitRF.slnx</c> appears.
    ///
    /// <para>Both callers — <c>tools/DocGen</c> and <c>Ui.Tests</c> — run out of a
    /// <c>bin/</c> directory inside the tree, so the walk always terminates; the shipping
    /// application never enters this file. A failure is a hard error naming what it looked for,
    /// because the alternative is a documentation run that silently produces no figures.</para>
    /// </summary>
    private static readonly Lazy<string> RepoRoot = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "The WSProbe documentation fixtures read their example designs out of testdata/, and no "
          + $"circuitRF.slnx was found above '{AppContext.BaseDirectory}'. The figures are generated "
          + "from inside the repository, so this is an error rather than a fallback.");
    });

    /// <summary>Run one design and hand back its results file, as the data-source library's own
    /// logical id.</summary>
    private static string Run(string key)
    {
        var d = Designs.FirstOrDefault(x => x.Key == key);
        if (d.Key is null)
            throw new InvalidOperationException(
                $"No WSProbe documentation design called '{key}'. They are: "
              + string.Join(", ", Designs.Select(x => x.Key)) + ".");

        string source = Path.Combine(RepoRoot.Value, d.Path);
        if (!File.Exists(source))
            throw new InvalidOperationException(
                $"The WSProbe documentation design '{d.Key}' reads '{d.Path}', which is not there. "
              + $"It serves {d.Serves}");

        string text = File.ReadAllText(source);
        if (d.AnalysisLines is { } lines)
        {
            var kept = text.Split('\n')
                           .Where(l => !l.TrimStart().StartsWith("analysis ", StringComparison.Ordinal));
            text = string.Join("\n", kept).TrimEnd() + "\n" + lines + "\n";
        }

        return DocRunData.RunNetlistText(key, text);
    }

    // ── The figures ───────────────────────────────────────────────────────────

    /// <summary>
    /// The two driving-point loci of the reference document's own series resonator: <c>1/Y0</c>,
    /// which carries Kurokawa's clockwise crossing of the negative real axis at the resonance, and
    /// <c>1/H0</c>, which does not.
    ///
    /// <para>That pairing IS the figure. §4.10's point is that a zero can mask the pole in one of
    /// the two functions but never in both, and a picture of only the one that shows it would be a
    /// picture of the wrong half of the lesson.</para>
    ///
    /// <para><b>TWO plots, which is what the page's own heading says and what this figure did not
    /// do until 2026-09-08.</b> The two loci are reciprocal quantities — <c>1/H0</c> is an
    /// admittance and <c>1/Y0</c> an impedance — so on one shared polar radius their magnitudes are
    /// whatever the circuit's impedance level makes them. On this resonator that is 10 to 30 Ω
    /// against 0.05 S: a ratio of about 600, which drew <c>1/H0</c> as a dot on the origin. Nothing
    /// was wrong with the trace and nothing said so; the reader simply could not see it, which the
    /// owner reported as "am I supposed to see 1/H0?". A plot each gives each locus its own radius,
    /// and the comparison the caption asks for is then one a reader can actually make.</para>
    /// </summary>
    public static FigureScene ResonatorPolar()
    {
        const double side = 360.0;
        string id = Run("WspSeriesResonator");

        var (vm, yPlot) = DocDataDisplayFixtures.PlotFor(id, PlotType.Polar, traces: 1, size: (side, side));
        PickWsp(yPlot.Inspector.Traces[0], WspMetric.InvY0, "P");

        var display = vm.Window.DataDisplay
            ?? throw new InvalidOperationException("The Data Display document has no active tab.");
        var hPlot = display.AddPlot(PlotType.Polar, width: side, height: side);
        hPlot.Inspector.AddTraceCommand.Execute(null);
        PickWsp(hPlot.Inspector.Traces[0], WspMetric.InvH0, "P");

        return new FigureScene(new DataDisplayView
            { DataContext = DocDataDisplayFixtures.Document(vm, "Series resonator") })
            { AfterLayout = DocDataDisplayFixtures.CentredRow(yPlot, hPlot) };
    }

    /// <summary>
    /// Both stability margins of the split series resonator, in dB against frequency — the notch at
    /// <c>f0</c> on <c>SM_Y0</c> and a minimum at a different frequency on <c>SM_H0</c>.
    ///
    /// <para>The plot draws the analysis' own threshold and the −12 dB floor beneath the traces, so
    /// the figure carries the two lines the page tells the reader to read against without the
    /// caption having to describe them.</para>
    /// </summary>
    public static FigureScene MarginResonator()
    {
        var (vm, plot) = Plot2(Run("WspMarginResonator"), PlotType.Rect,
                               WspMetric.SM_Y0, WspMetric.SM_H0, "P");
        return new FigureScene(new DataDisplayView
            { DataContext = DocDataDisplayFixtures.Document(vm, "Split series resonator") })
            { AfterLayout = DocDataDisplayFixtures.CentredPlot(plot) };
    }

    /// <summary>
    /// The trace card with its Envelope sub-card open: which probe each side is pulled at, the two
    /// |Γ| ladders, the angular step, and the count of terminations the card is about to compute.
    ///
    /// <para>A card, not a plot, because the envelope's controls are the part a reader cannot guess
    /// — in particular that the source and load probes are named separately from the probe the
    /// circuit is <em>read</em> at, which is the arrangement that makes "a probe not at its
    /// termination" a question that can be asked at all.</para>
    /// </summary>
    public static FigureScene EnvelopeCard()
    {
        var (_, plot) = Plot1(Run("WspTwoStage"), PlotType.Rect, WspMetric.SMenv, "P3");
        var row = plot.Inspector.Traces[0];
        SetEnvelope(row, source: "PS", load: "PL", gammaS: "0.9, 0.7", gammaL: "", theta: 15.0);
        return new FigureScene(new PlotInspectorView { DataContext = plot.Inspector });
    }

    /// <summary>
    /// The margin envelope and the NDF's encirclement count over the same terminations: <c>SMenv</c>
    /// against source phase at <c>ρ = 0.9</c>, with <c>NDFenc</c> on the right axis.
    ///
    /// <para>[E]'s own comparison. The numbers are this fixture's — a redrawn two-device topology
    /// with circuitRF's own element values — and what the figure is for is the shape: terminations
    /// at which the encirclement count has returned to zero while the margin is still far down.</para>
    /// </summary>
    public static FigureScene MarginEnvelopeOhtomo()
    {
        var (vm, plot) = Plot2(Run("WspOhtomo"), PlotType.Rect,
                               WspMetric.SMenv, WspMetric.NDFenc, "PG");
        SetEnvelope(plot.Inspector.Traces[0], "PS", "PL", "0.9", "", 15.0);
        SetEnvelope(plot.Inspector.Traces[1], "PS", "PL", "0.9", "", 15.0);
        plot.Inspector.Traces[1].UseSecondaryAxis = true;
        return new FigureScene(new DataDisplayView
            { DataContext = DocDataDisplayFixtures.Document(vm, "Stability envelope") })
            { AfterLayout = DocDataDisplayFixtures.CentredPlot(plot) };
    }

    /// <summary>
    /// The drive-swept fan: the pumped varactor's <c>SM_Y0</c> against the small-signal probe
    /// frequency, one curve per pump level, collapsing at half the fundamental as the pump crosses
    /// the parametric threshold.
    ///
    /// <para>This is the instability no linear analysis can see, which is why the figure is a fan
    /// and not a curve: at the bottom of the drive sweep there is nothing there.</para>
    /// </summary>
    public static FigureScene HbFan()
    {
        var (vm, plot) = Plot1(Run("WspHbFan"), PlotType.Rect, WspMetric.SM_Y0, "TANK");
        Family(plot.Inspector.Traces[0], "Vpump");
        return new FigureScene(new DataDisplayView
            { DataContext = DocDataDisplayFixtures.Document(vm, "Pumped varactor") })
            { AfterLayout = DocDataDisplayFixtures.CentredPlot(plot) };
    }

    /// <summary>
    /// K is not enough, on one pair of axes: Rollett's <c>K</c> of the terminal two-port on the
    /// left, above 1 across the whole band, and the NDF's running encirclement count on the right,
    /// stepping to the two right-half-plane poles of the loop those terminal S-parameters cannot
    /// see.
    /// </summary>
    public static FigureScene NdfK()
    {
        var (vm, plot) = DocDataDisplayFixtures.PlotFor(Run("WspNdfK"), PlotType.Rect, traces: 2);
        DocDataDisplayFixtures.PickSignal(plot.Inspector.Traces[0],
                                          DerivedParameters.K.Description());
        PickCube(plot.Inspector.Traces[1], "NDF_enc");
        plot.Inspector.Traces[1].UseSecondaryAxis = true;
        return new FigureScene(new DataDisplayView
            { DataContext = DocDataDisplayFixtures.Document(vm, "Hidden pole") })
            { AfterLayout = DocDataDisplayFixtures.CentredPlot(plot) };
    }

    // ── Driving the card the way a reader does ────────────────────────────────

    private static (DataDisplayDocumentViewModel Doc, PlotContainerViewModel Plot) Plot1(
        string logicalId, PlotType type, WspMetric metric, string probe)
    {
        var (vm, plot) = DocDataDisplayFixtures.PlotFor(logicalId, type, traces: 1);
        PickWsp(plot.Inspector.Traces[0], metric, probe);
        return (vm, plot);
    }

    private static (DataDisplayDocumentViewModel Doc, PlotContainerViewModel Plot) Plot2(
        string logicalId, PlotType type, WspMetric a, WspMetric b, string probe)
    {
        var (vm, plot) = DocDataDisplayFixtures.PlotFor(logicalId, type, traces: 2);
        PickWsp(plot.Inspector.Traces[0], a, probe);
        PickWsp(plot.Inspector.Traces[1], b, probe);
        return (vm, plot);
    }

    /// <summary>
    /// Point one trace at a WSProbe quantity through the card's own two gestures — choose the
    /// WSProbe group, then the metric — and then the probe.
    /// </summary>
    private static void PickWsp(TraceRowViewModel row, WspMetric metric, string probe)
    {
        row.SelectedGroup = row.AvailableGroups.FirstOrDefault(
                g => g.EndsWith("▸ WSProbe", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The trace card offers no WSProbe group for this result, so no probe figure can be "
              + "built from it. The groups offered are: " + string.Join(", ", row.AvailableGroups) + ".");

        var info = WspMetrics.Info(metric)!.Value;
        DocDataDisplayFixtures.PickSignal(row, $"{info.Name} — {info.Description}");

        string item = row.WspProbeItems.FirstOrDefault(
                i => i.StartsWith(probe + " ", StringComparison.Ordinal) || i == probe)
            ?? throw new InvalidOperationException(
                $"The trace card offers no probe '{probe}'. It offers: "
              + string.Join(", ", row.WspProbeItems) + ".");
        row.SelectedWspProbeItem = item;
    }

    /// <summary>Fill in the Envelope sub-card: the two side probes, their |Γ| ladders and the
    /// angular step. An empty ladder is the side's OFF state, which is what leaves the load
    /// unpulled.</summary>
    private static void SetEnvelope(TraceRowViewModel row, string source, string load,
                                    string gammaS, string gammaL, double theta)
    {
        row.SelectedWspSourceItem = SideItem(row, source);
        row.SelectedWspLoadItem   = SideItem(row, load);
        row.WspGammaSText         = gammaS;
        row.WspGammaLText         = gammaL;
        row.WspThetaStepDeg       = theta;
    }

    private static string SideItem(TraceRowViewModel row, string probe)
        => row.WspSideProbeItems.FirstOrDefault(
               i => i.StartsWith(probe + " ", StringComparison.Ordinal) || i == probe)
           ?? throw new InvalidOperationException(
               $"The envelope card offers no probe '{probe}' as a side. It offers: "
             + string.Join(", ", row.WspSideProbeItems) + ".");

    /// <summary>
    /// Draw one axis of a swept cube as a FAMILY rather than pinning it — the card's own axis-role
    /// row. Without it a drive-swept run draws the first operating point and nothing else, which is
    /// a curve where the figure is supposed to be a fan.
    /// </summary>
    private static void Family(TraceRowViewModel row, string axis)
    {
        var r = row.AxisRoles.FirstOrDefault(a => a.AxisName == axis)
            ?? throw new InvalidOperationException(
                $"The trace has no '{axis}' axis to iterate. It has: "
              + string.Join(", ", row.AxisRoles.Select(a => a.AxisName)) + ".");
        r.IsFamily = true;
    }

    /// <summary>Point a trace at a plain cube by name, from the list the card offers.</summary>
    private static void PickCube(TraceRowViewModel row, string cube)
    {
        var item = row.AvailableSignals.FirstOrDefault(s => s.Label == cube)
            ?? throw new InvalidOperationException(
                $"The trace card offers no signal called '{cube}'. It offers: "
              + string.Join(", ", row.AvailableSignals.Select(s => s.Label)) + ".");
        row.SelectedSignal = item;
    }
}
