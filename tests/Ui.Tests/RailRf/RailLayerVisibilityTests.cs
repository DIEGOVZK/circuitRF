// ================================================================
//  RailLayerVisibilityTests.cs — brief-railrf-20-layer-visibility.md R-rail20-1
//
//  THE BOARD PANEL HAS ITS OWN LAYER VISIBILITY, AND IT IS NOT THE `.ctech`'s.
//
//  A first-time designer's report (2026-09-20): the board is "difficult to navigate if I can't
//  disable layers visibility", and the only route there was to open the technology, untick Vis,
//  save, and come back. Layer visibility is a property of the VIEW, not of the process — a
//  technology is a manufacturing document shared across a workspace, so using its Vis boxes as a
//  per-window display switch makes one reader's navigation another reader's diff.
//
//  The two halves that are easy to get wrong in opposite directions are asserted together: the
//  drawing has to follow BOTH the copper and the map shading laid over it (R-rail20-1c's own rule,
//  which is the defect SyncHiddenLayers' comment records), and the RESULT must not move, because a
//  question about the picture that cost a re-solve is not worth asking.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine.Pdn;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public class RailLayerVisibilityTests
{
    /// <summary>The list is built from the RESOLVED technology, one row per drawing layer, each
    /// reading that layer's own <c>Visible</c>.</summary>
    [Fact]
    public void TheListIsOneRowPerDrawingLayerOfTheResolvedTechnology()
    {
        var vm = Example();

        var tech = vm.Board!.Technology;
        Assert.NotEmpty(tech.Layers);
        Assert.Equal(tech.Layers.Count, vm.BoardLayers.Count);
        Assert.Equal(tech.Layers.Select(l => l.Key), vm.BoardLayers.Select(r => r.Key));   // the .ctech's order

        foreach (var layer in tech.Layers)
        {
            var row = Assert.Single(vm.BoardLayers, r => r.Key == layer.Key);
            Assert.True(row.Visible);                           // a technology hides nothing
            Assert.Equal(layer.Name, row.Name);
        }

        // Nothing is hidden on a document nobody has hidden a layer in, so the way back is
        // offered and does nothing.
        Assert.False(vm.ShowAllLayersCommand.CanExecute(null));
    }

    /// <summary>
    /// Hiding a layer takes the copper AND the map shading laid over it. <b>Both, in one
    /// assertion</b> — R-rail20-1c.
    /// </summary>
    /// <remarks>
    /// The maps are drawn OVER the artwork, so a layer the renderer skips has to take its shading
    /// with it; otherwise hiding a layer removes the copper and leaves the drop map of it floating
    /// on the board. One hidden-layer set reaches the drawing, not two.
    /// </remarks>
    [Fact]
    public void HidingALayerTakesTheCopperAndItsMapShadingWithIt()
    {
        var vm = Example();
        var key = vm.BoardLayers[0].Key;

        vm.BoardLayers[0].Visible = false;

        // The copper: the canvas is drawing a technology in which that layer is not visible.
        var canvasTech = vm.BoardLayout!.Technology!;
        Assert.False(canvasTech.Layers.Single(l => l.Key == key).Visible);
        Assert.True(canvasTech.Layers.Where(l => l.Key != key).All(l => l.Visible));

        // …and it is a CLONE. Mutating the shared cache instance would narrow every later picture
        // taken through it, in the layout editor beside this window included.
        Assert.NotSame(vm.Board!.Technology, canvasTech);
        Assert.True(vm.Board.Technology.Layers.Single(l => l.Key == key).Visible);

        // The shading: the same layer, in the overlay's own hidden set.
        Assert.Contains(key, vm.BoardOverlayLayer.HiddenLayers);
    }

    /// <summary>Hiding a layer does not clear the answer — R-rail20-1d.</summary>
    /// <remarks>
    /// The stackup is untouched, so the numbers stand. Losing the answer a user just ran for would
    /// make every toggle cost a re-solve, which is the whole workflow this list exists to serve.
    /// </remarks>
    [Fact]
    public void HidingALayerDoesNotClearTheResult()
    {
        var vm = Ready();
        vm.RunCommand.Execute(null);
        Assert.NotEmpty(vm.ByModel);
        var before = vm.ByModel[0];

        vm.BoardLayers[0].Visible = false;

        Assert.NotEmpty(vm.ByModel);
        Assert.Same(before, vm.ByModel[0]);      // the same object, not an equal one
    }

    /// <summary>It round-trips in the <c>.crail</c> — R-rail20-1b.</summary>
    [Fact]
    public void TheHiddenLayersRoundTripInTheCrail()
    {
        var vm = Ready();
        vm.BoardLayers[0].Visible = false;
        vm.BoardLayers[1].Visible = false;

        // …and hiding one is an EDIT, so the document says it has unsaved work.
        Assert.True(vm.IsDirty);

        var reread = RailDocumentIo.Deserialize(RailDocumentIo.Serialize(vm.Document));

        Assert.Equal(2, reread.HiddenLayers.Count);
        Assert.Contains(vm.BoardLayers[0].Key, reread.HiddenLayers);
        Assert.Contains(vm.BoardLayers[1].Key, reread.HiddenLayers);

        // A document nobody has hidden a layer in says nothing about layers, so a `.crail` written
        // before this existed opens exactly as it always did.
        Assert.DoesNotContain("HiddenLayers", RailDocumentIo.Serialize(new RailDocument()),
                              StringComparison.Ordinal);
    }

    /// <summary>And it is NOT written to the <c>.ctech</c> — byte for byte, R-rail20-1b.</summary>
    [Fact]
    public void NothingIsWrittenToTheTechnologyFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"rail-layers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string techPath = Path.Combine(dir, "board.ctech");
            TechPersistence.SaveToFile(techPath, TwoLayerTech());
            byte[] before = File.ReadAllBytes(techPath);

            var vm = Window(OneRail());
            vm.Board = new RailBoardInputs
            {
                Shapes     = [],
                Technology = TechPersistence.LoadFromFile(techPath),
                TechPath   = techPath,
            };

            vm.BoardLayers[0].Visible = false;
            vm.ShowAllLayersCommand.Execute(null);
            vm.BoardLayers[1].Visible = false;

            Assert.Equal(before, File.ReadAllBytes(techPath));

            // …and the in-memory technology the solve prices copper against is untouched too.
            Assert.True(vm.Board!.Technology.Layers.All(l => l.Visible));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>"Show all" clears the window's hidden set — R-rail20-1e, renamed by
    /// brief-layout-layers-panel.md now that a technology hides nothing.</summary>
    [Fact]
    public void ShowAllClearsTheWindowsOwnChoices()
    {
        var vm = Example();
        vm.BoardLayers[0].Visible = false;
        vm.BoardLayers[2].Visible = false;

        Assert.Equal(2, vm.Document.HiddenLayers.Count);
        Assert.True(vm.ShowAllLayersCommand.CanExecute(null));

        vm.ShowAllLayersCommand.Execute(null);

        Assert.Empty(vm.Document.HiddenLayers);
        Assert.True(vm.BoardLayers.All(r => r.Visible));
        Assert.Empty(vm.BoardOverlayLayer.HiddenLayers);
        Assert.Same(vm.Board!.Technology, vm.BoardLayout!.Technology);   // no clone once nothing is hidden
        Assert.False(vm.ShowAllLayersCommand.CanExecute(null));
    }

    /// <summary>
    /// An older <c>.crail</c> that carried <c>ShownLayers</c> — the layers it drew although the
    /// <c>.ctech</c> hid them — still loads, and the list is ignored: a technology hides nothing now
    /// (brief-layout-layers-panel.md §7 test 8).
    /// </summary>
    [Fact]
    public void AnOldCrailWithShownLayersLoads_AndTheListIsIgnored()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(RailDocumentIo.Serialize(OneRail()))!;
        node["ShownLayers"] = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["Layer"] = 1, ["Datatype"] = 0 });
        node["HiddenLayers"] = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["Layer"] = 2, ["Datatype"] = 0 });

        var doc = RailDocumentIo.Deserialize(node.ToJsonString());
        var vm = Window(doc);
        vm.Board = new RailBoardInputs { Shapes = [], Technology = TwoLayerTech() };

        Assert.True(vm.BoardLayers.Single(r => r.Key == new LayerKey(1, 0)).Visible);
        Assert.False(vm.BoardLayers.Single(r => r.Key == new LayerKey(2, 0)).Visible);
        Assert.DoesNotContain("ShownLayers", RailDocumentIo.Serialize(vm.Document), StringComparison.Ordinal);
    }

    // ══ helpers ══════════════════════════════════════════════════════════════════════════════════

    private static RailRfViewModel Example()
    {
        string crail = Path.Combine(
            RepoRoot(), "examples", "Power Rail", "Sensor board", "Sensor board.crail");
        Assert.True(File.Exists(crail), $"The shipped example is not at {crail}.");

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail);
        Assert.Empty(vm.LoadDocumentReferences());
        return vm;
    }

    /// <summary>A window with a board and a solve that answers, so a run leaves numbers behind.</summary>
    private static RailRfViewModel Ready()
    {
        var vm = Window(OneRail());
        vm.Board = new RailBoardInputs { Shapes = [], Technology = TwoLayerTech() };
        vm.ConfirmReferenceCommand.Execute(null);
        Assert.True(vm.CanRun);
        return vm;
    }

    internal static RailRfViewModel Window(RailDocument doc)
    {
        var vm = new RailRfViewModel(doc, null)
        {
            PostToUi     = a => a(),
            RunOffThread = (work, _) => Task.FromResult(work()),
        };
        vm.SolveFunc = (request, _) =>
            new RailDcRunResult(null, [], [.. request.Document.Rails.Select(r => r.Name)], []);
        return vm;
    }

    internal static RailDocument OneRail()
    {
        var doc = new RailDocument { Name = "layers" };
        var rail = new RailSpec { Name = "+1V8", NetName = "+1V8" };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" }, OpenCircuitVoltageV = 3.7,
        });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" } });
        doc.Rails.Add(rail);
        return doc;
    }

    internal static Technology TwoLayerTech()
    {
        var tech = new Technology { Name = "board" };
        tech.Layers.Add(new LayerDef { Key = new LayerKey(1, 0), Name = "L1", ZOrder = 1 });
        tech.Layers.Add(new LayerDef { Key = new LayerKey(2, 0), Name = "L2", ZOrder = 2 });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "L1", DrawingLayers = [new LayerKey(1, 0)],
        });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "L2", IsGroundReference = true,
            DrawingLayers = [new LayerKey(2, 0)],
        });
        return tech;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
