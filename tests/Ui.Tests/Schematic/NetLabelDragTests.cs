using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-dfr22-2 R-dfr22-2-5: a net label drags in the Select tool. Dropped on a wire of its own net it re-anchors
/// there; dropped on another net's wire it snaps back; dropped clear of every wire it keeps its anchor and takes the
/// drop as its offset, at most <see cref="SchematicViewModel.MaxLabelOffset"/> from it. One undoable step, and the
/// netlist partition — names included — never changes, as in <see cref="DesignerRound22Tests"/>.
/// </summary>
[Trait("Suite", "SchematicConnectivity")]
public class NetLabelDragTests
{
    private static string Connectivity(SchematicEditModel m) =>
        string.Join("\n", NetExtractor.Extract(m).TestBench.Instances
            .SelectMany(i => i.NetBindings.Select((n, k) => (Net: n, Pin: $"{i.InstanceName}.{k}")))
            .GroupBy(p => p.Net).Select(g => $"{g.Key}: {string.Join(" ", g.Select(p => p.Pin).Order())}").Order());

    private static EditableWire Wire(SchematicEditModel m, params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        m.Wires.Add(w);
        return w;
    }

    /// <summary>R1 upright at the origin; net "a" on its top pin — a run across and a run T'd down off it — and net "b"
    /// on its bottom pin, each labelled on its run across.</summary>
    private static (SchematicViewModel Vm, EditableNetLabel A, EditableWire Across, EditableWire Tee, EditableWire Other) Fixture()
    {
        var m = new SchematicEditModel();
        m.Components.Add(new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor });   // pins (0,-200) (0,200)
        m.Components.Add(new EditableComponent { InstanceName = "R2", Symbol = SymbolKind.Resistor, X = 1000 });
        var across = Wire(m, (0, -200), (1000, -200));
        var tee = Wire(m, (500, -200), (500, -800));
        var other = Wire(m, (0, 200), (1000, 200));
        var a = new EditableNetLabel { Name = "a" };
        a.AnchorToWire(across, 800, -200);
        var b = new EditableNetLabel { Name = "b" };
        b.AnchorToWire(other, 800, 200);
        m.NetLabels.AddRange([a, b]);
        return (new SchematicViewModel(m), a, across, tee, other);
    }

    private static void Drag(SchematicViewModel vm, (double X, double Y) from, (double X, double Y) to)
    {
        vm.OnPointerPressed(from.X, from.Y, default);
        vm.OnPointerMoved((from.X + to.X) / 2, (from.Y + to.Y) / 2, leftDown: true);
        vm.OnPointerMoved(to.X, to.Y, leftDown: true);
        vm.OnPointerReleased(to.X, to.Y);
    }

    [Fact]
    public void DroppedOnAWireOfItsOwnNet_ItReanchorsThere_AndUndoPutsItBack()
    {
        var (vm, a, across, tee, _) = Fixture();
        string before = Connectivity(vm.EditModel);

        Drag(vm, (820, -220), (505, -600));

        Assert.Equal(tee.Id, a.OwnerWireId);
        Assert.Equal((500.0, -600.0), (a.X, a.Y));
        Assert.Equal(before, Connectivity(vm.EditModel));

        vm.UndoRedo.Undo();
        Assert.Equal(across.Id, a.OwnerWireId);
        Assert.Equal((800.0, -200.0, 0.0, 0.0), (a.X, a.Y, a.OffsetX, a.OffsetY));
    }

    [Fact]
    public void DroppedOnAnotherNetsWire_ItSnapsBack()
    {
        var (vm, a, across, _, _) = Fixture();
        string before = Connectivity(vm.EditModel);

        Drag(vm, (820, -220), (300, 200));

        Assert.NotNull(vm.NetLabelDragRefusal);
        Assert.Equal((across.Id, 800.0, -200.0), (a.OwnerWireId, a.X, a.Y));
        Assert.Empty(vm.UndoRedo.Entries);
        Assert.Equal(before, Connectivity(vm.EditModel));
    }

    [Fact]
    public void DroppedClearOfEveryWire_ItKeepsItsAnchorAndTakesTheDropAsItsOffset_NoFurtherThanTheLimit()
    {
        var (vm, a, across, _, _) = Fixture();
        string before = Connectivity(vm.EditModel);
        (string, int, double) anchor = (a.OwnerWireId, a.SegmentIndex, a.AlongT);

        Drag(vm, (820, -220), (820, -260));   // 40 up: inside the limit
        Assert.Equal(anchor, (a.OwnerWireId, a.SegmentIndex, a.AlongT));
        Assert.Equal((0.0, -40.0), (a.OffsetX, a.OffsetY));

        Drag(vm, (820, -260), (820, -700));   // far off: held at the limit
        Assert.Equal(anchor, (a.OwnerWireId, a.SegmentIndex, a.AlongT));
        Assert.Equal((0.0, -SchematicViewModel.MaxLabelOffset), (a.OffsetX, a.OffsetY), new Near());
        Assert.Equal(before, Connectivity(vm.EditModel));

        vm.UndoRedo.Undo();
        Assert.Equal((0.0, -40.0), (a.OffsetX, a.OffsetY));
        Assert.Equal(across.Id, a.OwnerWireId);
    }

    private sealed class Near : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) x, (double, double) y) =>
            System.Math.Abs(x.Item1 - y.Item1) < 1e-6 && System.Math.Abs(x.Item2 - y.Item2) < 1e-6;
        public int GetHashCode((double, double) obj) => 0;
    }
}
