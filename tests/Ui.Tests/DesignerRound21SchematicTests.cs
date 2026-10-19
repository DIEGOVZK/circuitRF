using System.IO;
using System.Linq;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Designer round 21, the schematic editor's three: a copied line dropped onto a wire stayed shorted
/// by it, a microstrip line offered a chip footprint, and Duplicate Cell copied the last save rather
/// than the cell as it stood open.
/// </summary>
public class DesignerRound21SchematicTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), $"crf_r21_{Guid.NewGuid():N}");

    public DesignerRound21SchematicTests() => Directory.CreateDirectory(_ws);

    public void Dispose() { try { Directory.Delete(_ws, recursive: true); } catch { } }

    private static EditableWire AddWire(SchematicEditModel m, params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        m.Wires.Add(w);
        return w;
    }

    private static (double X, double Y)[] Pts(EditableWire w) => w.Points.ToArray();

    // ── A copied line arrives on a wire: it breaks in, as a placed one does ──────

    /// <summary>
    /// The field case: a line pasted somewhere free and then DRAGGED onto the run it was copied for.
    /// A part connected to nothing that is dropped along a wire is a part arriving, and the stretch
    /// between its pins is cleared exactly as a palette drop clears it. MLIN pins sit at local (±200, 0).
    /// </summary>
    [Fact]
    public void AFreeLineDraggedOntoAWire_BreaksIntoIt()
    {
        var model = new SchematicEditModel();
        var vm    = new SchematicViewModel(model);
        AddWire(model, (0, 0), (900, 0));
        vm.CommitPlacement(SymbolKind.Mlin, 2, SymbolRotation.R0, 500, -1000);   // off the wire
        var line = Assert.Single(model.Components);

        vm.Selection.SelectOne(line.Id);
        vm.SimulateDragCommit(dx: 0, dy: 1000);

        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (300.0, 0.0)],   Pts(model.Wires[0]));
        Assert.Equal([(700.0, 0.0), (900.0, 0.0)], Pts(model.Wires[1]));

        vm.UndoRedo.Undo();                               // the cut and the move are one step
        Assert.Equal([(0.0, 0.0), (900.0, 0.0)], Pts(Assert.Single(model.Wires)));
        Assert.Equal(-1000.0, line.Y);
    }

    /// <summary>A part already wired in is MOVED, never re-inserted: its wires follow it.</summary>
    [Fact]
    public void AWiredInPartDraggedAlongItsRun_IsAPlainMove()
    {
        var model = new SchematicEditModel();
        var vm    = new SchematicViewModel(model);
        AddWire(model, (0, 0), (900, 0));
        vm.CommitPlacement(SymbolKind.Mlin, 2, SymbolRotation.R0, 500, 0);      // cut: 0–300 and 700–900
        var line = Assert.Single(model.Components);

        vm.Selection.SelectOne(line.Id);
        vm.SimulateDragCommit(dx: 0, dy: 1000);   // off the run, dragging both wire ends along

        Assert.Equal(2, model.Wires.Count);
        Assert.Contains(model.Wires, w => w.Points[0] == (0.0, 0.0));
        Assert.Contains(model.Wires, w => w.Points[^1] == (900.0, 0.0));
    }

    /// <summary>A paste of one part landing along a wire breaks into it too.</summary>
    [Fact]
    public void ALinePastedOntoAWire_BreaksIntoIt()
    {
        var model = new SchematicEditModel();
        var vm    = new SchematicViewModel(model);
        AddWire(model, (0, 0), (900, 0));

        var line = new EditableComponent { InstanceName = "ML1", Symbol = SymbolKind.Mlin, X = 500, Y = 0 };
        vm.PasteFragment([line], [], [], model.GridSize);   // headless: pasted in place

        Assert.Equal(2, model.Wires.Count);
        Assert.Equal([(0.0, 0.0), (300.0, 0.0)],   Pts(model.Wires[0]));
        Assert.Equal([(700.0, 0.0), (900.0, 0.0)], Pts(model.Wires[1]));
    }

    // ── A line offers no chip footprint ───────────────────────────────────────

    [Theory]
    [InlineData(SymbolKind.Mlin)]
    [InlineData(SymbolKind.Tline)]
    [InlineData(SymbolKind.Cpwg)]
    [InlineData(SymbolKind.MTee)]
    public void ALineKind_ShowsNoFootprintRow_AndAStaleOneIsInert(SymbolKind kind)
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "X1", Symbol = kind };
        comp.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = "smt:0603@N" });
        model.Components.Add(comp);

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);

        Assert.False(editor.ShowFootprintRow);
        Assert.Null(comp.Footprint);
    }

    [Fact]
    public void AChipPart_StillShowsTheFootprintRow()
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "C1", Symbol = SymbolKind.Capacitor };
        model.Components.Add(comp);

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);

        Assert.True(editor.ShowFootprintRow);
    }

    // ── Duplicate Cell copies the cell as it is open ──────────────────────────

    /// <summary>
    /// The saved schematic has one part; the open one has a line added since. The duplicate gets
    /// both, and the original's FILE keeps the one — its unsaved edits are still the user's to save.
    /// </summary>
    [Fact]
    public void DuplicateCell_CopiesTheOpenSchematic_NotTheLastSave()
    {
        var cellDir = CellFolder.CreateCellFolder(_ws, "Amp");
        var csch    = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), "Amp.csch");
        var saved   = new SchematicEditModel();
        saved.Components.Add(new EditableComponent { InstanceName = "C1", Symbol = SymbolKind.Capacitor });
        SchematicPersistence.SaveToFile(csch, saved);

        var (open, _, _) = SchematicPersistence.LoadFromFile(csch);
        var vm       = new SchematicViewModel(open);
        var registry = new SchematicSessionRegistry();
        registry.Register(Path.GetFullPath(csch), vm, _ => { });
        vm.CommitPlacement(SymbolKind.Mlin, 2, SymbolRotation.R0, 1000, 0);   // unsaved
        Assert.True(registry.IsDirty(Path.GetFullPath(csch)));

        var copyDir = Path.Combine(_ws, "Amp_v2");
        var written = CellDuplicate.Copy(cellDir, copyDir, registry, []);

        var copy = Path.Combine(CellFolder.SubFolderPath(copyDir, ViewType.Schematic), "Amp.csch");
        Assert.Equal([copy], written);
        Assert.Equal(2, SchematicPersistence.LoadFromFile(copy).model.Components.Count);
        Assert.Single(SchematicPersistence.LoadFromFile(csch).model.Components);
        Assert.True(registry.IsDirty(Path.GetFullPath(csch)));
    }
}
