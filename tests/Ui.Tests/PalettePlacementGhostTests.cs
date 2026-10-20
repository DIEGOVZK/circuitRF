using Avalonia.Input;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Two owner reports that share nothing but the day: a click-armed palette placement drawing the
/// placeholder glyph where drag-and-drop drew the kit's own symbol, and a parametric sweep reporting
/// one axis for a run with several.
/// </summary>
public class PalettePlacementGhostTests
{
    // ── The ghost's POSITION: it must survive an overlay rebuild ────────────────────────────

    private static (SchematicEditModel Model, SchematicViewModel Vm) MakeVm()
    {
        var model = new SchematicEditModel();
        return (model, new SchematicViewModel(model));
    }

    /// <summary>
    /// Reported by the owner while placing a current probe: the ghost jumps somewhere odd the moment
    /// the part lands, and only comes back on the next mouse move.
    ///
    /// <para>It was never about the probe. <c>CommitPlacement</c> ends by selecting what it placed,
    /// a selection change rebuilds the overlay, and the rebuild had no pointer position to work
    /// from — so it rebuilt the ghost at world (0,0). Every armed placement of every component type
    /// did this. The view model now remembers where the ghost is.</para>
    /// </summary>
    [Fact]
    public void PlacingAPart_LeavesTheGhostUnderTheCursor_NotAtTheWorldOrigin()
    {
        var (model, vm) = MakeVm();
        vm.BeginPlacement(SymbolKind.Resistor);
        vm.OnPointerMoved(700, 300, leftDown: false);

        vm.OnPointerPressed(700, 300, KeyModifiers.None);

        Assert.Single(model.Components);
        var ghost = vm.Overlay.Ghost;
        Assert.NotNull(ghost);
        Assert.Equal(700.0, ghost!.X);
        Assert.Equal(300.0, ghost.Y);
    }

    /// <summary>The same for an IProbe, whose placement also CUTS a wire — a second model change,
    /// and so a second chance to lose the ghost. This is the gesture that was reported.</summary>
    [Fact]
    public void PlacingAnIProbeOnAWire_LeavesTheGhostUnderTheCursor()
    {
        var (model, vm) = MakeVm();
        var wire = new EditableWire();
        wire.Points.AddRange([(0, 400), (900, 400)]);
        model.Wires.Add(wire);

        vm.BeginPlacement(SymbolKind.IProbe);
        // R0 puts the probe's pins 100 below its origin, i.e. on the wire at y = 400.
        vm.OnPointerMoved(500, 300, leftDown: false);
        vm.OnPointerPressed(500, 300, KeyModifiers.None);

        Assert.Equal(2, model.Wires.Count);          // the span between the pins really was cut
        var ghost = vm.Overlay.Ghost;
        Assert.NotNull(ghost);
        Assert.Equal(500.0, ghost!.X);
        Assert.Equal(300.0, ghost.Y);
    }

    /// <summary>The control, and the reason the remembered position is nullable: an armed tool shows
    /// no ghost at all until the pointer has been over the canvas. A ghost parked at the origin is
    /// exactly what the bug looked like.</summary>
    [Fact]
    public void ArmingAPlacement_ShowsNoGhostUntilThePointerMoves()
    {
        var (_, vm) = MakeVm();

        vm.BeginPlacement(SymbolKind.Resistor);

        Assert.Null(vm.Overlay.Ghost);
    }

    /// <summary>Arming a DIFFERENT part forgets the old position too — the pointer may have gone to
    /// the palette to arm it, and a ghost left behind on the canvas would be stale.</summary>
    [Fact]
    public void ArmingASecondPart_ForgetsTheFirstsGhostPosition()
    {
        var (_, vm) = MakeVm();
        vm.BeginPlacement(SymbolKind.Resistor);
        vm.OnPointerMoved(700, 300, leftDown: false);
        Assert.NotNull(vm.Overlay.Ghost);

        vm.BeginPlacement(SymbolKind.Capacitor);

        Assert.Null(vm.Overlay.Ghost);
    }

    // ── The ghost: a kit reference must not be split by hand ────────────────────────────────

    [Fact]
    public void AKitReference_ResolvesThroughTheOneAccessor_ButNotThroughAHandSplit()
    {
        // The whole of the reported difference between the two placement paths. A kit part's cell
        // reference is VIRTUAL, so GetDirectoryName/GetFileName — correct for an absolute cell folder
        // — turns it into a path that resolves to nothing, and the ghost silently falls back to the
        // placeholder glyph. Drag-and-drop looked right because it already went through
        // ResolveCellDirOrRef; click-to-arm did the split.
        const string kitRef = "pdk://SomeKit/SomePart";

        string trimmed = kitRef.TrimEnd('/', '\\');
        string? parent = Path.GetDirectoryName(trimmed);

        Assert.NotEqual("pdk://SomeKit", parent);   // the split does not survive the round trip
        Assert.False(CellSymbolResolver.Resolve(Path.GetFileName(trimmed), parent ?? "").State
                     == CellSymbolState.Resolved);

        // The accessor recognises the form instead of guessing at it. With no kit registered it
        // reports NotFound rather than mangling the reference into a different question.
        var viaAccessor = CellSymbolResolver.ResolveCellDirOrRef(kitRef);
        Assert.NotEqual(CellSymbolState.Resolved, viaAccessor.State);
    }

    [Fact]
    public void TheGhostBuilder_DoesNotHandSplitACellReference()
    {
        // A source scan, because the alternative needs a registered kit and a live canvas — and
        // because the property that actually matters is "nobody re-derives the split", which is
        // exactly what a scan can state. The rule is in src/Ui/CLAUDE.md.
        string src = File.ReadAllText(RepoFile("src/Ui/ViewModels/SchematicViewModel.cs"));
        int builder = src.IndexOf("private PlacementGhost BuildPlacementGhost", StringComparison.Ordinal);
        Assert.True(builder > 0, "BuildPlacementGhost has moved — re-point this scan");

        // The method body, up to the next method-level declaration.
        int end = src.IndexOf("\n    private void HandlePlacePress", builder, StringComparison.Ordinal);
        Assert.True(end > builder);
        string body = src[builder..end];

        Assert.Contains("ResolveCellDirOrRef", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetDirectoryName", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetFileName", body, StringComparison.Ordinal);
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    // ── The sweep summary: every axis, and the product ──────────────────────────────────────

    private static (TestBench Tb, ParametricSweepAnalysis Top) NestedSweep(
        bool outerEnabled = true, params (string Var, int Points)[] axes)
    {
        // Built outermost-first, each wrapping the next; the innermost wraps a DC analysis.
        var tb = new TestBench("TB");
        tb.Analyses.Add(new DcAnalysis("DC1"));

        ParametricSweepAnalysis? outer = null;
        string inner = "DC1";
        for (int i = axes.Length - 1; i >= 0; i--)
        {
            var values = Enumerable.Range(0, axes[i].Points).Select(v => (double)v).ToArray();
            outer = new ParametricSweepAnalysis($"SW{i}", axes[i].Var, values, inner);
            if (i == 0) outer.Enabled = outerEnabled;
            tb.Analyses.Add(outer);
            inner = outer.Name;
        }
        return (tb, outer!);
    }

    [Fact]
    public void ASingleAxis_ReportsItsOwnCount_AndNoTotal()
    {
        // "= 101 total" beside "101 pt(s)" is the same number said twice.
        var (tb, top) = NestedSweep(axes: [("VDS", 101)]);

        string s = ParametricSweepRunSummary.Describe(top, tb);

        Assert.Contains("101 pt(s) over VDS", s, StringComparison.Ordinal);
        Assert.DoesNotContain("total", s, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedAxes_ReportEachCountOutermostFirst_AndTheProduct()
    {
        // The reported case: sweeping VDS inside VGS said only the VGS count, with no indication that
        // each of those points is itself a whole VDS sweep.
        var (tb, top) = NestedSweep(axes: [("VGS", 11), ("VDS", 101)]);

        string s = ParametricSweepRunSummary.Describe(top, tb);

        Assert.Contains("11 pt(s) over VGS", s, StringComparison.Ordinal);
        Assert.Contains("101 pt(s) over VDS", s, StringComparison.Ordinal);
        Assert.Contains("1,111 total pt(s)", s, StringComparison.Ordinal);
        Assert.True(s.IndexOf("VGS", StringComparison.Ordinal) < s.IndexOf("VDS", StringComparison.Ordinal),
                    "the outermost axis is the slow one and is listed first");
    }

    [Fact]
    public void ThreeAxes_MultiplyAllOfThem()
    {
        var (tb, top) = NestedSweep(axes: [("A", 3), ("B", 4), ("C", 5)]);

        Assert.Contains("60 total pt(s)", ParametricSweepRunSummary.Describe(top, tb), StringComparison.Ordinal);
    }

    [Fact]
    public void ADisabledSweep_ContributesNoAxisAndNoPoints()
    {
        // AnalysisChain's own rule: a disabled sweep collapses and its axis is dropped, so counting
        // it would report points that are never simulated. The chain is still descended through it.
        var (tb, top) = NestedSweep(axes: [("VGS", 11), ("VDS", 101)]);
        tb.Analyses.OfType<ParametricSweepAnalysis>().Single(a => a.SweepVarName == "VDS").Enabled = false;

        string s = ParametricSweepRunSummary.Describe(top, tb);

        Assert.Contains("11 pt(s) over VGS", s, StringComparison.Ordinal);
        Assert.DoesNotContain("VDS", s, StringComparison.Ordinal);
        Assert.DoesNotContain("total", s, StringComparison.Ordinal);   // one axis left, no product
    }
}
