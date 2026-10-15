// ================================================================
//  LayoutLayersPanelTests.cs — brief-layout-layers-panel.md §7
//
//  Layer visibility and selectability belong to a VIEW of a layout: they are not stored in the
//  .ctech, they live in the layout editor's own Layers list, and the per-user .cwsuser keeps them.
//  The field report behind it: clicking a microstrip line over a ground plane kept selecting the
//  plane, and there was no way to stop it short of editing a shared technology.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia.Input;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

public class LayoutLayersPanelTests : IDisposable
{
    private static readonly LayerKey Trace = new(1, 0);
    private static readonly LayerKey Plane = new(2, 0);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-lyr-" + Guid.NewGuid().ToString("N"));

    public LayoutLayersPanelTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static Technology TwoLayerTech() => new()
    {
        Name = "Trace over plane",
        Layers =
        {
            new LayerDef { Key = Trace, Name = "Top",    ZOrder = 1 },
            new LayerDef { Key = Plane, Name = "Ground", ZOrder = 2 },   // paints above, so a click would pick it first
        },
    };

    /// <summary>A trace on Top lying on a larger ground plane, both selectable — the field report.</summary>
    private static LayoutEditorViewModel TraceOverPlane(Technology? tech = null, string? clayPath = null)
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        model.Shapes.Add(new RectShape { Layer = Trace, X1 = 0,       Y1 = -1_000,  X2 = 50_000, Y2 = 1_000 });
        model.Shapes.Add(new RectShape { Layer = Plane, X1 = -20_000, Y1 = -20_000, X2 = 70_000, Y2 = 20_000 });
        var vm = new LayoutEditorViewModel(model, clayPath) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.ApplyTechResolution(new TechResolution(tech ?? TwoLayerTech(), "/ws/tech/t.ctech",
                                                  TechResolutionSource.WorkspaceDefault, []));
        return vm;
    }

    private static LayerKey[] HitLayers(LayoutEditorViewModel vm, long x, long y) =>
        [.. LayoutHitTest.HitStack(vm.Model, vm.DrawTechnology, x, y, 0).Select(i => vm.Model.Shapes[i].Layer)];

    // ── 1. The .ctech carries no view state ───────────────────────────────────────────────────────

    [Fact]
    public void ACtechCarriesNoViewState_AndNoTrackedCtechStillHasTheKeys()
    {
        var tech = TwoLayerTech();
        tech.Layers[0].Visible = false;
        tech.Layers[1].Selectable = false;

        string json = TechPersistence.Serialize(tech);

        Assert.DoesNotContain("\"Visible\"", json);
        Assert.DoesNotContain("\"Selectable\"", json);

        // The folders that hold the tracked technologies — not the whole tree, which also holds a
        // developer's own untracked boards.
        string root = RepoRoot();
        string[] trackedRoots = ["examples", "src", "tests", "testdata/antenna", "testdata/em3d", "testdata/portcal"];
        string sep = Path.DirectorySeparatorChar.ToString();
        var stale = trackedRoots
            .Select(r => Path.Combine(root, r)).Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*.ctech", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{sep}bin{sep}") && !p.Contains($"{sep}obj{sep}"))
            .Where(p => JsonNode.Parse(File.ReadAllText(p))?["Layers"]?.AsArray()
                            .Any(l => l?["Visible"] is not null || l?["Selectable"] is not null) == true)
            .Select(p => Path.GetRelativePath(root, p))
            .ToList();
        Assert.Empty(stale);
    }

    // ── 2. Clone keeps the flags (R-lyr-1b's trap) ────────────────────────────────────────────────

    [Fact]
    public void Clone_KeepsAHiddenAndALockedLayer()
    {
        var tech = TwoLayerTech();
        tech.Layers[0].Visible = false;
        tech.Layers[1].Selectable = false;

        var clone = TechPersistence.Clone(tech);

        Assert.False(clone.Layers[0].Visible);
        Assert.False(clone.Layers[1].Selectable);
    }

    // ── 3. An old .ctech with "Visible": false opens visible (D3) ─────────────────────────────────

    [Fact]
    public void AnOldCtechThatHidALayer_OpensWithItVisibleAndSelectable()
    {
        string json = TechPersistence.Serialize(TwoLayerTech());
        var node = JsonNode.Parse(json)!;
        node["Layers"]![0]!["Visible"] = false;
        node["Layers"]![0]!["Selectable"] = false;

        var tech = TechPersistence.Deserialize(node.ToJsonString());

        Assert.True(tech.Layers[0].Visible);
        Assert.True(tech.Layers[0].Selectable);
    }

    // ── 4. The field report ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void LockingThePlanesLayer_LeavesItOutOfAClickASelectAllAndAMarquee()
    {
        var vm = TraceOverPlane();
        Assert.Equal([Plane, Trace], HitLayers(vm, 25_000, 0));   // the report: the plane comes first

        vm.SetLayerSelectable(Plane, false);
        AssertOnlyTheTraceIsPickable(vm);
    }

    [Fact]
    public void CurrentLayerOnly_LeavesThePlaneOut_AndTheSelColumnIsUnchanged()
    {
        var vm = TraceOverPlane();
        vm.MakeLayerCurrent(Trace);

        vm.SelectCurrentLayerOnly = true;
        AssertOnlyTheTraceIsPickable(vm);
        Assert.True(vm.LayerRows.Single(r => r.Key == Plane).Selectable);

        vm.SelectCurrentLayerOnly = false;
        Assert.Equal([Plane, Trace], HitLayers(vm, 25_000, 0));
    }

    private static void AssertOnlyTheTraceIsPickable(LayoutEditorViewModel vm)
    {
        Assert.Equal([Trace], HitLayers(vm, 25_000, 0));

        vm.SelectAllCommand.Execute(null);
        Assert.Equal([Trace], vm.SelectedIndices.Select(i => vm.Model.Shapes[i].Layer));

        vm.DeselectAllCommand.Execute(null);
        vm.OnPointerPressed(-30_000, -30_000, KeyModifiers.None);
        vm.OnPointerMoved(80_000, 30_000, leftDown: true, KeyModifiers.None);
        vm.OnPointerReleased(80_000, 30_000, KeyModifiers.None);
        Assert.Equal([Trace], vm.SelectedIndices.Select(i => vm.Model.Shapes[i].Layer));
    }

    // ── 5. Nothing is dirtied ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Toggling_DirtiesNeitherTheLayoutNorTheTechnology()
    {
        string clay = Path.Combine(_dir, "a.clay"), ctech = Path.Combine(_dir, "t.ctech");
        var vm0 = TraceOverPlane();
        LayoutPersistence.SaveToFile(clay, vm0.Model);
        TechPersistence.SaveToFile(ctech, TwoLayerTech());
        byte[] clayBefore = File.ReadAllBytes(clay), ctechBefore = File.ReadAllBytes(ctech);

        var vm = TraceOverPlane(TechPersistence.LoadFromFile(ctech), clay);
        vm.SetLayerVisible(Trace, false);
        vm.SetLayerSelectable(Plane, false);
        vm.SelectCurrentLayerOnly = true;
        vm.ShowAllLayersCommand.Execute(null);

        Assert.False(vm.IsDirty);
        Assert.Equal(clayBefore, File.ReadAllBytes(clay));
        Assert.Equal(ctechBefore, File.ReadAllBytes(ctech));
    }

    // ── 6. A hidden layer is still exported ───────────────────────────────────────────────────────

    [Fact]
    public void AHiddenLayer_IsStillExported()
    {
        var vm = TraceOverPlane();
        vm.SetLayerVisible(Plane, false);
        Assert.False(vm.DrawTechnology!.Layers.Single(l => l.Key == Plane).Visible);

        string cellDir = CellFolder.CreateCellFolder(_dir, "TOP");
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "TOP.clay"), vm.Model);
        var plan = GdsiiExport.Analyze(cellDir, vm.Technology, 1000, vm.Model);
        string gds = Path.Combine(_dir, "out.gds");
        GdsiiExport.Write(gds, plan);

        using var stream = File.OpenRead(gds);
        var top = GdsiiReader.Open(stream).ReadStructures().Single(s => s.Name == "TOP");
        Assert.Contains(top.Shapes, s => s.Layer == Plane);
    }

    // ── 7. Round trip through the .cwsuser, never the .cws ────────────────────────────────────────

    [Fact]
    public void TheLayerViewRoundTripsThroughTheCwsuser_AndNeverReachesTheCws()
    {
        var vm = TraceOverPlane();
        Assert.Null(vm.LayerViewToPersist());                      // D4: untouched writes nothing
        vm.SetLayerVisible(Trace, false);
        vm.SetLayerSelectable(Plane, false);
        vm.SelectCurrentLayerOnly = true;
        vm.CommitLayerListWidth(320);

        string cws = Path.Combine(_dir, ".cws");
        WorkspacePersistence.SaveToFile(cws, new CwsFile
        {
            LayoutLayerViews = new() { ["cells/a/layout/a.clay"] = vm.LayerViewToPersist()! },
        });

        Assert.DoesNotContain(nameof(CwsFile.LayoutLayerViews), File.ReadAllText(cws));
        var back = WorkspacePersistence.LoadFromFile(cws).LayoutLayerViews!["cells/a/layout/a.clay"];

        var reopened = TraceOverPlane();
        reopened.ApplyLayerView(back);
        Assert.False(reopened.LayerRows.Single(r => r.Key == Trace).Visible);
        Assert.False(reopened.LayerRows.Single(r => r.Key == Plane).Selectable);
        Assert.True(reopened.SelectCurrentLayerOnly);
        Assert.Equal(320, reopened.LayerListWidth);

        // Only a resized width is written; the default is no entry at all (D4).
        var untouched = TraceOverPlane();
        untouched.CommitLayerListWidth(LayoutEditorViewModel.DefaultLayerListWidth);
        Assert.Null(untouched.LayerViewToPersist());
    }

    // ── 10. A row click sets the current layer, and the toolbar follows ───────────────────────────

    [Fact]
    public void ClickingARowsName_MakesItTheDrawingLayer_AndTheToolbarComboFollows()
    {
        var vm = TraceOverPlane();
        var row = vm.LayerRows.Single(r => r.Key == Plane);

        row.MakeCurrentCommand.Execute(null);

        Assert.Equal(Plane, vm.CurrentLayerKey);
        Assert.Equal(Plane, vm.CurrentLayerItem!.Key);
        Assert.True(row.IsCurrent);

        vm.CurrentLayerItem = vm.AvailableLayers.Single(l => l.Key == Trace);
        Assert.True(vm.LayerRows.Single(r => r.Key == Trace).IsCurrent);
        Assert.False(row.IsCurrent);
    }

    // ── The filter and the page keys (owner follow-up, 2026-10-15) ───────────────────────────────

    /// <summary>The filter narrows what the list SHOWS, case-insensitively, and says so in its count;
    /// Show all still reaches a layer the filter has hidden from view.</summary>
    [Fact]
    public void TheFilterNarrowsTheListedRows_AndShowAllStillReachesTheOthers()
    {
        var vm = TraceOverPlane();
        vm.SetLayerVisible(Plane, false);

        vm.LayerListFilter = "tOP";

        Assert.Equal([Trace], vm.FilteredLayerRows.Select(r => r.Key));
        Assert.Equal("1 of 2", vm.LayerListFilterSummary);

        vm.ShowAllLayersCommand.Execute(null);
        Assert.True(vm.LayerRows.Single(r => r.Key == Plane).Visible);

        vm.LayerListFilter = "";
        Assert.Equal(2, vm.FilteredLayerRows.Count);
        Assert.Equal("2", vm.LayerListFilterSummary);
    }

    /// <summary>The bulk Vis and Sel toggles sweep the rows the filter is SHOWING and leave the others
    /// alone — the technology editor's Layers tab behaviour, kept when the boxes moved here.</summary>
    [Fact]
    public void TheBulkToggles_SweepOnlyTheFilteredRows()
    {
        var vm = TraceOverPlane();
        vm.LayerListFilter = "ground";

        vm.AllListedLayersVisible = false;
        vm.AllListedLayersSelectable = false;

        Assert.False(vm.LayerRows.Single(r => r.Key == Plane).Visible);
        Assert.False(vm.LayerRows.Single(r => r.Key == Plane).Selectable);
        Assert.True(vm.LayerRows.Single(r => r.Key == Trace).Visible);      // filtered out, untouched
        Assert.True(vm.LayerRows.Single(r => r.Key == Trace).Selectable);
        Assert.False(vm.AllListedLayersVisible);

        vm.LayerListFilter = "";
        Assert.False(vm.AllListedLayersVisible);                           // one listed row is now off
        vm.AllListedLayersVisible = true;
        Assert.True(vm.LayerRows.All(r => r.Visible));
        Assert.False(vm.IsDirty);
    }

    /// <summary>Page Up/Down page the list by the technology editor's own rule — from inside the list
    /// (its filter box included) on a tunnel, and from the canvas while the list is open.</summary>
    [Fact]
    public void ThePageKeysAreWiredToTheListsScroller()
    {
        string cs = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs"));
        Assert.Contains("LayerListPanel.AddHandler(KeyDownEvent, OnLayerListScrollKeyDown, RoutingStrategies.Tunnel)", cs);
        Assert.Contains("PanelScrollKeys.ActionFor(e.Key, e.Source is TextBox)", cs);
        Assert.Contains("if (TryPageLayerList(e)) return;", cs);
    }

    /// <summary>The list opens and closes three ways: the toolbar button, the L key anywhere in the
    /// layout view (never from a field that types it), and View ▸ Layers List in both menus.</summary>
    [Fact]
    public void TheListOpensAndClosesFromTheKeyboardAndTheViewMenu()
    {
        string cs = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs"));
        Assert.Contains("TryToggleLayerList(e, layersDoc)", cs);
        Assert.Contains("e.Key != Key.L || e.KeyModifiers != KeyModifiers.None", cs);
        Assert.Contains("e.Source is TextBox or ComboBox", cs);
        Assert.Contains("vm.IsTypingLabel", cs);

        string window = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "WorkspaceWindow.axaml"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(window, @"\{Binding ToggleLayoutLayerListCommand\}").Count);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
