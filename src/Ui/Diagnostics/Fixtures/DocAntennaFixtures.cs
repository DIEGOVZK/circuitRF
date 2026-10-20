using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Views.DataDisplay;
using CircuitRF.Ui.Views.Layout;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The figures of <c>reference/antennas.html</c> — ANT-12's example antenna, drawn by the real layout
/// renderer and photographed in the real EM Setup panel.
///
/// <para><b>The design is NOT built here.</b> It is read out of <c>testdata/antenna/</c>, which is the
/// workspace the page is written about and which <c>AntennaExampleTests</c> runs end to end through the
/// CLI. A second copy under <c>src/Ui</c> would agree with the first on the day it was written and drift
/// silently afterwards — <see cref="DocWsProbeFixtures"/> states the rule and the reason, and this file
/// follows it, including the walk up to <c>circuitRF.slnx</c> and the hard failure if it is not found.
/// What a reader sees is therefore the artwork that produced the numbers beside it.</para>
/// </summary>
public static class DocAntennaFixtures
{
    /// <summary>The repository root, found by walking up from this assembly's own location. Both
    /// callers — <c>tools/DocGen</c> and <c>Ui.Tests</c> — run out of a <c>bin/</c> directory inside
    /// the tree, and the shipping application never enters this file.</summary>
    private static readonly Lazy<string> RepoRoot = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "The documentation figures that read a committed design out of testdata/ need the "
          + $"repository root, and no circuitRF.slnx was found above '{AppContext.BaseDirectory}'.");
    });

    /// <summary>The repository root, for the other fixtures that read a committed <c>testdata/</c>
    /// design rather than building a second copy of it — <see cref="DocCoupledLineFixtures"/> reads
    /// AN-01's coupled pair through it. One walk, one failure message.</summary>
    internal static string RepoRootPath => RepoRoot.Value;

    private static string Example(params string[] parts) =>
        Path.Combine([RepoRoot.Value, "testdata", "antenna", .. parts]);

    /// <summary>The example's <c>.cem</c> and the layout it resolves to, through the SAME resolver the
    /// `em` verb and the Simulate button use — so the figure cannot be of a layout the run would not
    /// have found.</summary>
    private static (EmSetup Setup, EmLayoutSource Source) Load()
    {
        string cem = Example("patch", "em", "patch-5p8GHz.cem");
        var setup = EmSetupPersistence.LoadFromFile(cem);
        var resolved = EmSetupResolver.Resolve(
            cem, setup.LayoutRef, Example(".cws"), new TechnologyCache());

        if (resolved.Source is null || resolved.Source.Technology is null)
            throw new InvalidOperationException(
                "The antenna example's layout or technology did not resolve: "
              + string.Join(" ", resolved.Diagnostics));

        return (setup, resolved.Source);
    }

    private static LayoutEditorViewModel EditorVm(EmLayoutSource source)
        => new(source.View) { Technology = source.Technology };

    /// <summary>
    /// <b>The whole example, ground pour and all.</b> Both copper layers are on, which is the point —
    /// the patch and its inset feed on top, the 40 × 40 mm pour under them, and the port mark on the
    /// feed's end face. The pour is what makes the run able to report the plane's size, and a figure
    /// with it switched off would teach the opposite.
    /// </summary>
    public static FigureScene PatchLayout()
    {
        var (_, source) = Load();
        return Framed(EditorVm(source), 760, 660, marginX: 0.06, marginY: 0.06);
    }

    /// <summary>
    /// <b>The feed, close up</b> — the inset notch, the 1 mm gaps either side of the 1.68 mm line, and
    /// the edge port's bar and arrow on the end face. This is the part of the artwork a reader has to
    /// copy, and at the whole-board zoom above it is four millimetres of a forty-millimetre picture.
    /// </summary>
    public static FigureScene PatchFeed()
    {
        var (_, source) = Load();
        var vm = EditorVm(source);

        var canvas = new LayoutCanvas { ViewModel = vm, Width = 560, Height = 380, ClipToBounds = true };
        var framed = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            Child = canvas,
        };

        // An explicit WINDOW rather than a fit, and it is sized by the PORT MARKER rather than by the
        // artwork. The marker's bar spans the port's own width and its arrow is a fraction of that, so
        // on a 1.68 mm feed it is 1.68 mm of picture however wide the frame is: at the whole-board
        // zoom above, the mark a reader is supposed to look at is two per cent of the figure. An 8 mm
        // window round the feed's end face puts it at a fifth of the frame, which is what makes it
        // readable — the same trade DocLayoutFixtures.EdgePortsOnATaper makes by choosing a part whose
        // ports are wide.
        const double Mm = 1e6;    // the example's .clay is 1000 DBU/µm, so 1e6 DBU per mm
        double cx = 8.47 * Mm, cy = -2.6 * Mm;
        double zoom = canvas.Width / (10.0 * Mm);
        var vp = new LayoutViewport(cx - canvas.Width / (2.0 * zoom),
                                   cy - canvas.Height / (2.0 * zoom),
                                   zoom, canvas.Width, canvas.Height);
        canvas.SetViewport(vp);
        return new FigureScene(framed) { AfterLayout = _ => canvas.SetViewport(vp) };
    }

    /// <summary>
    /// <b>The port setup, in the panel that owns it.</b> The real EM Setup editor on the real
    /// <c>.cem</c>: the kernel the registry chose, the resolved port with its side and its impedance,
    /// the mesh report for the radiating-sheet intent, and the Radiation pattern group that is the one
    /// control this whole page is about.
    ///
    /// <para>Meshed but not solved (<c>BuildActiveMesh</c>), which is what the panel itself shows
    /// before Simulate — and a full solve here would be minutes inside a figure run.</para>
    /// </summary>
    public static FigureScene PatchEmSetup()
    {
        var (setup, source) = Load();
        string cem = Example("patch", "em", "patch-5p8GHz.cem");

        var vm = new EmSetupEditorViewModel(cem, setup) { ResolveLayout = _ => source };
        vm.BuildActiveMesh(null);

        if (vm.PlanarPorts.Count == 0)
            throw new InvalidOperationException(
                "The antenna EM Setup figure resolved no port on its own artwork. A panel figure "
              + "showing an unresolved port would document the empty state, not the populated one.");

        return new FigureScene(new EmSetupEditorView
        {
            DataContext = new EmSetupDocument("patch-5p8GHz", vm, cem),
        });
    }

    /// <summary>
    /// <b>The one control this whole page is about, at a size a reader can read it at.</b> A CROP of
    /// the same panel rather than a picture of its own (see <see cref="FigureCrop"/>): the group is
    /// one checkbox and a header near the bottom of a panel 2,000 pixels tall, and in the whole-panel
    /// figure above it is below the fold.
    ///
    /// <para>The rectangle comes from the live visual tree by the checkbox's NAME, never from a pixel
    /// offset — a measured offset is right until the panel gains a row, and then the figure is of the
    /// wrong part of it with nothing to say so.</para>
    /// </summary>
    public static FigureScene RadiationPatternControl()
    {
        var (setup, source) = Load();
        string cem = Example("patch", "em", "patch-5p8GHz.cem");

        var vm = new EmSetupEditorViewModel(cem, setup) { ResolveLayout = _ => source };
        vm.BuildActiveMesh(null);

        var panel = new EmSetupEditorView
        {
            DataContext = new EmSetupDocument("patch-5p8GHz", vm, cem),
        };

        var crop = FigureCrop.Around(
            panel, 560, 2400,
            c => c.GetVisualDescendants().OfType<CheckBox>().Where(b => b.Name == "RadiationPatternCheck"),
            pad: 10,
            describeWhatIsMissing:
                "This figure crops to the EM Setup panel's Radiation pattern checkbox, by the name "
              + "RadiationPatternCheck in EmSetupEditorView.axaml. If that control is renamed or "
              + "removed, this fixture must follow it rather than guess at an offset.");

        return new FigureScene(crop.Content) { AfterLayout = _ => crop.Apply() };
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  The RESULT, and the three figures drawn from it
    //
    //  Everything above is a picture of the DESIGN and costs milliseconds. What follows needs the
    //  design SOLVED: a pattern is a post-process of currents, so there is no honest way to draw one
    //  without paying for the full-wave sweep it rides on.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The frequency the pattern figures are drawn at, in GHz. <b>5.85, because that is the row the
    /// page's own results table quotes</b> — a reader who checks the directivity in the caption
    /// against the number in the table is checking one run against itself. It is the requested grid
    /// point nearest the 5.8131 GHz resonance, and it is on the 21-point grid by construction
    /// (5.3…6.3 GHz in 50 MHz steps), so nothing here has to search for a nearby point.
    /// </summary>
    public const double PatternFreqGHz = 5.85;

    /// <summary>
    /// <b>The E-plane's azimuth, and the H-plane's.</b> The example's port drives current in +y (the
    /// run says so by name), so the plane CONTAINING the current axis — the E-plane — is φ = 90°/270°
    /// and the H-plane is φ = 0°/180°. It is the same axis the run's own Ludwig-3 reference derives
    /// (φ₀ = 90.00°), which is why a figure may name the two planes at all: <see cref="SurfaceStandardView"/>
    /// declines to, and for the right reason — on an arbitrary cube the naming would be a guess. Here
    /// it is read off the driven direction of the one port in the design the figure is OF.
    /// </summary>
    private const double EPlanePhiDeg = 90.0, HPlanePhiDeg = 0.0;

    /// <summary>
    /// <b>The example's own EM result, read from the workspace's own results directory.</b>
    /// <c>testdata/antenna/results/patch-5p8GHz_em.npy</c> — the file
    /// <c>circuitrf em testdata/antenna/patch/em/patch-5p8GHz.cem</c> writes, at the path
    /// <c>EmRunService.ResolveSnpPath</c>'s own convention puts it. The three figures below read it
    /// through the Data Display's data-source library, exactly as a user's own run is read.
    ///
    /// <para><b>It is not committed and it is not produced here.</b> A pattern is a post-process of
    /// solved currents, so there is no honest way to draw one without the sweep behind it — and that
    /// sweep is 45.7 s optimised and 5 min 11 s unoptimised on ten cores, for an answer that does not
    /// change. Neither cost belongs in a documentation regeneration, and the result itself is 54 MB,
    /// which does not belong in the repository (owner, 2026-09-12: keep the picture, not the
    /// dataset). So the three figures are <c>Static</c> in <c>FigureCatalog</c> — committed SVGs that
    /// an ordinary regeneration leaves alone — and this fixture runs only under
    /// <c>--rebuild-static</c>, when somebody has deliberately re-run the example and wants the
    /// pictures redrawn from it.</para>
    ///
    /// <para>Returns the file name, which is the data-source library's own logical id.</para>
    /// </summary>
    public static string ExampleResult() => ExampleRun.Value;

    private static readonly Lazy<string> ExampleRun = new(() =>
    {
        string source = Example("results", "patch-5p8GHz_em.npy");
        if (!File.Exists(source))
            throw new InvalidOperationException(
                "The antenna pattern figures are rebuilt from the example's own EM result, and there "
              + $"is none at '{source}'. Run the example first — it takes about 45 s built Release:\n\n"
              + "    dotnet run -c Release --project src/Cli -- em testdata/antenna/patch/em/patch-5p8GHz.cem\n\n"
              + "Results are not kept in the repository (the workspace's own .gitignore excludes "
              + "them), which is why these figures are committed pictures rather than regenerated "
              + "ones. Do not point this fixture at a different run: the numbers in the captions and "
              + "on the page are this design's.");

        Directory.CreateDirectory(DocRunData.ResultsRoot);
        // COPIED, so a docs run cannot write into the example workspace even by accident.
        string file = Path.GetFileName(source);
        File.Copy(source, Path.Combine(DocRunData.ResultsRoot, file), overwrite: true);
        return file;
    });

    /// <summary>
    /// <b>The two principal-plane cuts on one polar plot</b> — the comparison the page calls the one
    /// worth making. E-plane against H-plane, each drawn as ONE whole-plane trace rather than two
    /// half-disc ones, normalised so the outer ring is this pattern's own peak, with the bearings
    /// printed round the rim the way an antenna-range plot is drawn.
    ///
    /// <para>Every one of those is a control on the trace card or the Plot Inspector, driven here by
    /// the property a reader would click — so the figure cannot show an arrangement the interface
    /// cannot reach.</para>
    /// </summary>
    public static FigureScene PatternCuts()
    {
        var (vm, plot) = DocDataDisplayFixtures.PlotFor(
            ExampleResult(), PlotType.Polar, traces: 2, size: (500.0, 500.0),
            // BEFORE the traces: the dB-radial switch is what makes this a pattern plot, and it
            // decides which cubes the card may offer. Turned on afterwards, the two traces would
            // have been picked under the rule for a linear polar plot.
            before: p =>
            {
                p.Inspector.PolarRadialIsDb  = true;
                p.Inspector.PolarAngleLabels = true;
            });

        double[] planes = [EPlanePhiDeg, HPlanePhiDeg];
        for (int i = 0; i < planes.Length; i++)
        {
            var row = Card(plot, i);
            PickCube(row, "farfield.U");
            SetTransform(row, CubeTransform.dB10);   // U is a POWER density
            SweepAngle(row, "theta");
            Pin(row, "freq", PatternFreqGHz, "GHz");
            Pin(row, "phi",  planes[i], "deg");
            row().PatternCutIndex = 2;               // "Whole plane"
        }

        return new FigureScene(
            new DataDisplayView { DataContext = DocDataDisplayFixtures.Document(vm, "patch-5p8GHz") })
        { AfterLayout = DocDataDisplayFixtures.CentredPlot(plot) };
    }

    /// <summary>
    /// <b>The 3D pattern surface</b>, on the isometric view it opens at: r(θ, φ) is the radiation
    /// intensity in dB above the plot's own floor, coloured by the same value, over the upper
    /// hemisphere — which is the whole sphere here, because the ground plane is laterally infinite
    /// and the field below it is identically zero.
    ///
    /// <para>The page is careful that this is not the pattern view that says the most, and the
    /// figure keeps that order: it appears after the two cuts, not before them.</para>
    /// </summary>
    public static FigureScene Pattern3D()
    {
        var (vm, plot) = DocDataDisplayFixtures.PlotFor(
            ExampleResult(), PlotType.Surface3D, size: (620.0, 430.0),
            // A 20 dB FLOOR rather than the 40 dB default, and the reason is this antenna: over an
            // infinite ground plane a patch is a broad dome with no sidelobe and no null until exact
            // grazing, so at 5.85 GHz the whole visible surface lies within 15 dB of the peak. On a
            // 40 dB ramp that is the top third of one colour and the picture is a magenta blob. The
            // floor sets the radius as well as the colour, so this is a choice about the picture and
            // not about the data — and the colour bar beside it states the range either way.
            before: p => p.Inspector.PolarDbFloor = -20.0);

        var row = Card(plot, 0);
        PickCube(row, "farfield.U");
        SetTransform(row, CubeTransform.dB10);
        // The surface opens θ and φ itself and pins everything else at the row's own index, so the
        // frequency is the one thing left to say — and it has to be said, or the picture is of
        // whichever end of the sweep the axis happens to start at.
        Pin(row, "freq", PatternFreqGHz, "GHz");

        return new FigureScene(
            new DataDisplayView { DataContext = DocDataDisplayFixtures.Document(vm, "patch-5p8GHz") })
        { AfterLayout = DocDataDisplayFixtures.CentredPlot(plot) };
    }

    /// <summary>
    /// <b>Radiation efficiency in decibels, across the band</b> — <c>farfield.RadiationEfficiencyDb</c>
    /// against frequency, on an ordinary rectangular plot.
    ///
    /// <para>The dB cube is plotted rather than the percentage one, and with NO transform, because it
    /// is already a level: the page's own note says why 10·log10 of a percentage is not a loss.</para>
    ///
    /// <para>The figure exists because the quantity varies strongly across the band — 22.6 % at
    /// 5.3 GHz, 69.3 % at its maximum, 62.6 % at the 5.85 GHz the worked example quotes — and because
    /// the maximum is at 5.9426 GHz, the PARALLEL resonance the resonance search found, rather than at
    /// the 5.8131 GHz series resonance the feed is matched at.</para>
    /// </summary>
    public static FigureScene EfficiencySweep()
    {
        var (vm, plot) = DocDataDisplayFixtures.PlotFor(
            ExampleResult(), PlotType.Rect, size: (700.0, 380.0));

        var row = Card(plot, 0);
        PickCube(row, "farfield.RadiationEfficiencyDb");
        SetTransform(row, CubeTransform.None);
        SweepAngle(row, "freq");

        return new FigureScene(
            new DataDisplayView { DataContext = DocDataDisplayFixtures.Document(vm, "patch-5p8GHz") })
        { AfterLayout = DocDataDisplayFixtures.CentredPlot(plot) };
    }

    // ── Driving a trace card, by what things ARE rather than by where they sit ────────────────
    //
    //  Every helper below fails loudly and says what it was offered. A figure fixture that silently
    //  took the first row, the first signal or index 0 would keep producing a picture after the
    //  thing it is a picture OF moved — which is the one failure a generated figure cannot announce.
    //
    //  AND EVERY ONE OF THEM TAKES THE CARD AS A FUNCTION, not as a reference. Almost every edit
    //  here ends in RebuildAndNotify, which REPLACES the inspector's TraceRowViewModels and the
    //  AxisRoleRowViewModels under them — so a reference held across two edits is a live object for
    //  the first and a discarded one for the second, and writing to a discarded one throws nothing
    //  and changes nothing. That is not hypothetical: with the cut fixture written as an ordinary
    //  `foreach (var trace in Traces)`, the first cut came out configured and the SECOND came out as
    //  the plot had seeded it — a real, plausible-looking curve on the figure, with a label that
    //  named a frequency and an azimuth nobody had chosen.

    /// <summary>The n-th trace card, re-fetched from the live collection on every call.</summary>
    private static Func<TraceRowViewModel> Card(PlotContainerViewModel plot, int index) => () =>
        index < plot.Inspector.Traces.Count
            ? plot.Inspector.Traces[index]
            : throw new InvalidOperationException(
                $"The antenna figures expected at least {index + 1} trace card(s) and the plot has "
              + $"{plot.Inspector.Traces.Count}.");

    /// <summary>Point a trace at a cube BY NAME, selecting the group it lives in first, the way the
    /// card's group→signal cascade is used by hand.</summary>
    private static void PickCube(Func<TraceRowViewModel> card, string cubeName)
    {
        var offered = new List<string>();
        foreach (var group in card().AvailableGroups.ToList())
        {
            card().SelectedGroup = group;
            var item = card().AvailableSignals.FirstOrDefault(
                s => s.CubeName is { } c
                  && (c == cubeName || c.EndsWith("." + cubeName, StringComparison.Ordinal)));
            if (item is not null) { card().SelectedSignal = item; return; }
            offered.AddRange(card().AvailableSignals.Select(s => s.CubeName ?? s.Label));
        }
        throw new InvalidOperationException(
            $"The antenna figures' trace card offers no cube called '{cubeName}'. It offers: "
          + string.Join(", ", offered.Distinct()) + ".");
    }

    /// <summary>Set the card's one transform combo, and refuse a transform it has disabled — which
    /// on a pattern plot is most of them, for the reason <c>BuildTransformItems</c> gives.</summary>
    private static void SetTransform(Func<TraceRowViewModel> card, CubeTransform transform)
    {
        var item = card().TraceTransformItems.FirstOrDefault(i => i.Transform == transform)
            ?? throw new InvalidOperationException(
                $"The trace card offers no '{transform}' transform.");
        if (!item.Enabled)
            throw new InvalidOperationException(
                $"The trace card offers '{transform}' but has it DISABLED on this plot, so a figure "
              + "set to it would be showing a state the interface refuses.");
        card().SelectedTransformItem = item;
    }

    /// <summary>Make one axis the swept one — the card's X button.</summary>
    private static void SweepAngle(Func<TraceRowViewModel> card, string axisName)
        => Row(card, axisName).IsX = true;

    /// <summary>
    /// Pin an axis at the value NEAREST <paramref name="value"/>, and refuse if the nearest is not
    /// that value — a figure drawn at 5.8 GHz because 5.85 was not on the grid would be a caption
    /// that is confidently wrong.
    ///
    /// <para><b>The rows are the combo's own FORMATTED strings</b> — "5.85 GHz", "90 deg" — so they
    /// are parsed back into a number and a unit, and <paramref name="unit"/> is checked rather than
    /// assumed. It has to be: the number alone is meaningless if the axis is ever shown in a
    /// different unit, and a frequency axis is shown in whatever unit the PLOT is set to. A mismatch
    /// fails the docs build naming both units, which is the one outcome that cannot silently draw
    /// the wrong picture.</para>
    /// </summary>
    private static void Pin(Func<TraceRowViewModel> card, string axisName, double value, string unit)
    {
        var row = Row(card, axisName);
        var parsed = row.PinOptions.Select(ParseOption).ToArray();

        if (parsed.Any(o => double.IsNaN(o.Value)))
            throw new InvalidOperationException(
                $"The '{axisName}' axis of the antenna figures' cube offers values that do not parse "
              + "as a number and a unit, so a figure cannot be pinned to one by value: "
              + string.Join(", ", row.PinOptions) + ".");

        // A row states its unit in ONE of two places and which one is the card's business, not this
        // fixture's: a frequency is printed into every option ("5.85 GHz", because the plot's own
        // frequency unit can change under it) and an angle is not ("90", with "deg" on the row's
        // label). Either is accepted; disagreeing with the one that is there is not.
        string offered = parsed.Select(o => o.Unit).FirstOrDefault(u => u.Length > 0) ?? row.Unit ?? "";
        if (!string.Equals(offered, unit, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The antenna figures pin '{axisName}' by a value in {(unit.Length == 0 ? "no unit" : unit)}, "
              + $"and the card is now offering that axis in {(offered.Length == 0 ? "no unit" : offered)} "
              + $"({string.Join(", ", row.PinOptions.Take(3))}…). Fix the fixture rather than letting "
              + "it pick the nearest number in the wrong unit.");

        int best = 0;
        for (int i = 1; i < parsed.Length; i++)
            if (Math.Abs(parsed[i].Value - value) < Math.Abs(parsed[best].Value - value)) best = i;

        // The rows are ROUNDED for display (5.813 GHz is 5.812890625), so the tolerance is half a
        // step of the grid rather than an epsilon — close enough to catch "that value is not on this
        // axis at all", loose enough to accept the printed form of one that is.
        double span = parsed.Max(o => o.Value) - parsed.Min(o => o.Value);
        double tol  = Math.Max(1e-9, 1e-4 * (span > 0 ? span : Math.Abs(value)));
        if (Math.Abs(parsed[best].Value - value) > tol)
            throw new InvalidOperationException(
                $"The antenna figures ask for {axisName} = {value.ToString(CultureInfo.InvariantCulture)} {unit}, "
              + $"and the nearest value on that axis is {row.PinOptions[best]}. Pinning to it anyway "
              + "would caption a figure with a frequency it is not drawn at.");

        // Re-fetched between writes, because each one can rebuild the rows under us.
        Row(card, axisName).IsX      = false;
        Row(card, axisName).IsFamily = false;
        Row(card, axisName).PinIndex = best;
    }

    /// <summary>One combo row, split into its number and whatever unit follows it ("" for none).</summary>
    private static (double Value, string Unit) ParseOption(string option)
    {
        string text = option.Trim();
        int split = text.IndexOf(' ');
        string number = split < 0 ? text : text[..split];
        string unit   = split < 0 ? "" : text[(split + 1)..].Trim();
        return (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                    ? v : double.NaN,
                unit);
    }

    private static AxisRoleRowViewModel Row(Func<TraceRowViewModel> card, string axisName)
        => card().AxisRoles.FirstOrDefault(r => r.AxisName == axisName)
           ?? throw new InvalidOperationException(
                $"The antenna figures' cube has no '{axisName}' axis. It has: "
              + string.Join(", ", card().AxisRoles.Select(r => r.AxisName)) + ".");

    /// <summary>Frame a layout view model into a bordered canvas sized to its own artwork — the same
    /// arithmetic <see cref="DocLayoutFixtures"/>' port figures use, restated here rather than made
    /// public there: it is six lines and this file's margins are its own.</summary>
    private static FigureScene Framed(LayoutEditorViewModel vm, int width, int height,
                                      double marginX, double marginY)
    {
        var canvas = new LayoutCanvas { ViewModel = vm, Width = width, Height = height, ClipToBounds = true };
        var framed = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            Child = canvas,
        };

        var bb = Bbox.Empty;
        foreach (var s in vm.Model.Shapes) bb = bb.Union(LayoutGeometry.BboxOf(s));
        if (bb.IsEmpty)
            throw new InvalidOperationException("The antenna figure was built on artwork with no extent.");

        double zoom = Math.Min(width  / ((bb.MaxX - bb.MinX) * (1 + 2 * marginX)),
                               height / ((bb.MaxY - bb.MinY) * (1 + 2 * marginY)));
        double cx = 0.5 * (bb.MinX + bb.MaxX), cy = 0.5 * (bb.MinY + bb.MaxY);
        var vp = new LayoutViewport(cx - width / (2.0 * zoom), cy - height / (2.0 * zoom),
                                   zoom, width, height);

        canvas.SetViewport(vp);
        return new FigureScene(framed) { AfterLayout = _ => canvas.SetViewport(vp) };
    }
}
