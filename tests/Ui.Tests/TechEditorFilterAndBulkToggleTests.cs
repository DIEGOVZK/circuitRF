using System;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  .ctech editor — per-tab row filter and keyboard scrolling.
//
//  Owner request (2026-08-22): a filter row above the rows on every tab, and Page Up/Down/Home/End
//  scrolling for each tab's list. (The bulk "all visible" / "all selectable" toggles that came with
//  it are gone: a technology carries no visibility since brief-layout-layers-panel.md, and the
//  layout's own Layers list has a Show all.)
//
//  The behaviour worth pinning is not "a filter filters" but the two ways it interacts with
//  everything already here: the filters are PER TAB (Layers and Interchange list the same layers),
//  and they survive the wholesale row rebuild every committed edit and every undo performs.
//
//  The .axaml half is a source scan — an AXAML layout change has no headlessly assertable rendered
//  output (the convention this file follows from TechEditorNarrowWidthTests).
// ──────────────────────────────────────────────────────────────────────────────

public class TechEditorFilterAndBulkToggleTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"techfilter-{Guid.NewGuid():N}.ctech");

    private static Technology FreshTech() => new()
    {
        Name = "Filter Test",
        DefaultDisplayUnit = LayoutUnit.Um,
        DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef { Key = new LayerKey(1, 0),   Name = "Metal1",           Color = new Rgba(200, 100, 50), ZOrder = 1 },
            new LayerDef { Key = new LayerKey(2, 0),   Name = "Metal2",           Color = new Rgba(50, 100, 200), ZOrder = 2 },
            new LayerDef { Key = new LayerKey(126, 0), Name = "TopMetal1.drawing", Color = new Rgba(9, 9, 9),      ZOrder = 3 },
            new LayerDef { Key = new LayerKey(1, 25),  Name = "Activ.text",       Color = new Rgba(1, 2, 3),      ZOrder = 4 },
        ],
        Stackup = new Stackup
        {
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor,  Name = "Metal1 sheet", ThicknessDbu = 1000 },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Oxide",        ThicknessDbu = 2000 },
            ],
        },
        DrcRules =
        [
            new DrcRule { Name = "Metal1 min width",   Kind = DrcRuleKind.MinWidth,   Layer = new LayerKey(1, 0) },
            new DrcRule { Name = "Poly min spacing",   Kind = DrcRuleKind.MinSpacing, Layer = new LayerKey(2, 0) },
        ],
    };

    private static TechEditorViewModel Vm() => new(TempPath(), FreshTech());

    // ── The filters ───────────────────────────────────────────────────────────

    [Fact]
    public void NoFilter_EveryTabListsEveryRow()
    {
        var vm = Vm();

        Assert.Equal(vm.Layers.Count,        vm.FilteredLayers.Count);
        Assert.Equal(vm.Layers.Count,        vm.FilteredInterchangeLayers.Count);
        Assert.Equal(vm.StackupLayers.Count, vm.FilteredStackupLayers.Count);
        Assert.Equal(vm.DrcRules.Count,      vm.FilteredDrcRules.Count);

        // Order is the technology's own order, not a re-sort.
        Assert.Equal(vm.Layers.ToArray(), vm.FilteredLayers.ToArray());
    }

    [Fact]
    public void LayerFilter_KeepsOnlyNameSubstringMatches_CaseInsensitive()
    {
        var vm = Vm();

        vm.LayerFilter = "metal";   // lower case against "Metal1"/"Metal2"/"TopMetal1.drawing"

        Assert.Equal(["Metal1", "Metal2", "TopMetal1.drawing"], vm.FilteredLayers.Select(r => r.StagedName));
        Assert.DoesNotContain(vm.FilteredLayers, r => r.StagedName == "Activ.text");
    }

    [Fact]
    public void ClearingTheFilter_RestoresEveryRow()
    {
        var vm = Vm();
        vm.LayerFilter = "activ";
        Assert.Single(vm.FilteredLayers);

        vm.LayerFilter = "";
        Assert.Equal(vm.Layers.Count, vm.FilteredLayers.Count);
    }

    [Fact]
    public void EachTabOwnsItsOwnFilter_NarrowingOneNeverNarrowsAnother()
    {
        var vm = Vm();

        vm.LayerFilter = "Activ";

        Assert.Single(vm.FilteredLayers);
        Assert.Equal(vm.Layers.Count, vm.FilteredInterchangeLayers.Count);   // same layers, different tab

        vm.InterchangeFilter = "Metal";
        Assert.Equal(3, vm.FilteredInterchangeLayers.Count);
        Assert.Single(vm.FilteredLayers);                                    // unchanged by the other tab
    }

    [Fact]
    public void StackupAndDrcTabs_FilterTheirOwnRowsByName()
    {
        var vm = Vm();

        vm.StackupFilter = "oxide";
        Assert.Equal(["Oxide"], vm.FilteredStackupLayers.Select(r => r.StagedName));

        vm.DrcFilter = "spacing";
        Assert.Equal(["Poly min spacing"], vm.FilteredDrcRules.Select(r => r.StagedName));
    }

    [Fact]
    public void FilterSummary_ReportsShownOfTotal_OnlyWhileNarrowing()
    {
        var vm = Vm();
        Assert.Equal("4", vm.LayerFilterSummary);

        vm.LayerFilter = "metal";
        Assert.Equal("3 of 4", vm.LayerFilterSummary);
    }

    // Every committed edit (and every undo/redo) replaces the row VMs wholesale — the filtered
    // views are rebuilt from the new rows, so a filter set before an edit still holds after it.
    [Fact]
    public void AFilterSurvivesACommittedEdit_AndAnUndo()
    {
        var vm = Vm();
        vm.LayerFilter = "metal";
        Assert.Equal(3, vm.FilteredLayers.Count);

        vm.Layers[3].StagedName = "Activ.renamed";   // a layer the filter is hiding
        vm.Layers[3].CommitName();
        Assert.Equal(3, vm.FilteredLayers.Count);
        Assert.All(vm.FilteredLayers, r => Assert.Contains("metal", r.StagedName, StringComparison.OrdinalIgnoreCase));

        vm.UndoRedo.Undo();
        Assert.Equal(3, vm.FilteredLayers.Count);
    }

    [Fact]
    public void RenamingALayerIntoTheFilter_MakesItAppear()
    {
        var vm = Vm();
        vm.LayerFilter = "metal";
        Assert.Equal(3, vm.FilteredLayers.Count);

        vm.Layers[3].StagedName = "MetalOxide";
        vm.Layers[3].CommitName();

        Assert.Equal(4, vm.FilteredLayers.Count);
    }

    // ── Page Up / Page Down / Home / End ──────────────────────────────────────

    [Fact]
    public void PageKeys_AlwaysScroll_EvenFromInsideAnEditableCell()
    {
        Assert.Equal(PanelScrollAction.PageUp,   PanelScrollKeys.ActionFor(Key.PageUp,   sourceIsTextInput: true));
        Assert.Equal(PanelScrollAction.PageDown, PanelScrollKeys.ActionFor(Key.PageDown, sourceIsTextInput: true));
    }

    // Every row here is built out of TextBoxes, where Home/End are caret motion. Taking them would
    // break text editing across the whole editor to add a shortcut nobody asked for there.
    [Fact]
    public void HomeAndEnd_ScrollOnlyWhenTheKeystrokeIsNotComingFromATextField()
    {
        Assert.Equal(PanelScrollAction.Home, PanelScrollKeys.ActionFor(Key.Home, sourceIsTextInput: false));
        Assert.Equal(PanelScrollAction.End,  PanelScrollKeys.ActionFor(Key.End,  sourceIsTextInput: false));

        Assert.Null(PanelScrollKeys.ActionFor(Key.Home, sourceIsTextInput: true));
        Assert.Null(PanelScrollKeys.ActionFor(Key.End,  sourceIsTextInput: true));
    }

    [Fact]
    public void OtherKeys_AreLeftAlone()
    {
        foreach (var k in new[] { Key.Up, Key.Down, Key.Enter, Key.A, Key.Space })
        {
            Assert.Null(PanelScrollKeys.ActionFor(k, sourceIsTextInput: false));
            Assert.Null(PanelScrollKeys.ActionFor(k, sourceIsTextInput: true));
        }
    }

    // ── The view (source scans) ───────────────────────────────────────────────

    private static string Axaml() => File.ReadAllText(RepoFile(Path.Combine(
        "src", "Ui", "Views", "Layout", "TechEditorView.axaml")));

    private static string CodeBehind() => File.ReadAllText(RepoFile(Path.Combine(
        "src", "Ui", "Views", "Layout", "TechEditorView.axaml.cs")));

    private static string RepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }

    // Binding a list to the UNFILTERED collection is the one regression that disables the whole
    // feature while still compiling and still looking right until someone types in the box.
    [Theory]
    [InlineData("ViewModel.FilteredLayers")]
    [InlineData("ViewModel.FilteredInterchangeLayers")]
    [InlineData("ViewModel.FilteredStackupLayers")]
    [InlineData("ViewModel.FilteredDrcRules")]
    public void EveryRowListBindsToItsFilteredCollection(string binding)
    {
        Assert.Contains($"ItemsSource=\"{{Binding {binding}}}\"", Axaml());
    }

    [Theory]
    [InlineData("ViewModel.LayerFilter")]
    [InlineData("ViewModel.InterchangeFilter")]
    [InlineData("ViewModel.StackupFilter")]
    [InlineData("ViewModel.DrcFilter")]
    public void EveryTabHasItsOwnFilterBox(string binding)
    {
        Assert.Contains($"Text=\"{{Binding {binding}, Mode=TwoWay}}\"", Axaml());
    }

    // Bubbling would let the ListBox move its (invisible) selection first and swallow the key.
    [Fact]
    public void TheScrollKeyHandlerTunnels()
    {
        var cs = CodeBehind();
        Assert.Contains("AddHandler(KeyDownEvent, OnScrollKeyDown, RoutingStrategies.Tunnel)", cs);
        Assert.Contains("PanelScrollKeys.ActionFor", cs);
    }
}
