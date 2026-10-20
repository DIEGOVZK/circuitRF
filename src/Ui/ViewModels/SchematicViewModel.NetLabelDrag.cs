// A net label dragged in the Select tool — brief-dfr22-2-artwork-schematic-drawing.md R-dfr22-2-5.
//
// A label could only be re-anchored by double-clicking a wire and retyping its name. Pressing on one now drags it:
// dropped on a wire of its OWN net it rides that wire (SegmentIndex/AlongT); dropped clear of every wire it keeps its
// anchor and takes the drop as its offset; dropped on ANOTHER net's wire it snaps back. A label is a name, so a drag
// never changes connectivity — extraction reads an anchored label's net from its owner wire, not from where it is
// drawn (NetExtractor.FindLabelNetKey), which is what lets it sit off its wire at all. One undoable step
// (MoveNetLabelCommand); the drag previews through the overlay's NetLabelDragPositions, as a wire drag's labels do.

using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

public sealed partial class SchematicViewModel
{
    private EditableNetLabel? _labelDrag;
    private NetLabelPlace _labelDragFrom;
    private double _labelDragPressX, _labelDragPressY;
    private bool _labelDragMoved, _labelClickThrough;

    /// <summary>The label drag's last outcome — why a drop snapped back, for the status line and tests.</summary>
    public string? NetLabelDragRefusal { get; private set; }

    /// <summary>A press on a net label in the Select tool starts dragging it. False when the press is on none.</summary>
    private bool TryBeginNetLabelDrag(double wx, double wy)
    {
        if (_labelClickThrough || RenderModel is null || SpatialIndex is null) return false;
        var hit = SchematicHitTest.TestStack(EditModel, RenderModel, SpatialIndex, wx, wy, includeLabels: true, zoom: CanvasZoom)
                                  .FirstOrDefault(h => h.Kind == SchematicHitTest.HitKind.NetLabel);
        if (hit.Kind != SchematicHitTest.HitKind.NetLabel || EditModel.FindNetLabel(hit.Id) is not { } label) return false;
        _labelDrag = label;
        _labelDragFrom = NetLabelPlace.Of(label);
        (_labelDragPressX, _labelDragPressY) = (wx, wy);
        _labelDragMoved = false;
        NetLabelDragRefusal = null;
        return true;
    }

    private void NetLabelDragMove(double wx, double wy)
    {
        if (_labelDrag is not { } label) return;
        _labelDragMoved = true;
        var (to, _) = NetLabelDrop(label, wx, wy);
        Overlay = Overlay with
        {
            NetLabelDragPositions = new Dictionary<string, (double X, double Y)> { [label.Id] = (to.X, to.Y) },
        };
    }

    private void NetLabelDragRelease(double wx, double wy)
    {
        if (_labelDrag is not { } label) return;
        _labelDrag = null;
        Overlay = Overlay with { NetLabelDragPositions = null };
        if (!_labelDragMoved)
        {
            // A click that did not drag is the click it always was — on the wire under the label.
            _labelClickThrough = true;
            try
            {
                HandleSelectPress(_labelDragPressX, _labelDragPressY, false, 0, 0);
                HandleSelectRelease(_labelDragPressX, _labelDragPressY, default);
            }
            finally { _labelClickThrough = false; }
            return;
        }
        var (to, refusal) = NetLabelDrop(label, wx, wy);
        NetLabelDragRefusal = refusal;
        if (refusal is not null || to == _labelDragFrom) { RebuildOverlay(); return; }
        Execute(new MoveNetLabelCommand(EditModel, label, _labelDragFrom, to));
    }

    /// <summary>
    /// Where <paramref name="label"/> goes when dropped with the pointer at (<paramref name="wx"/>, <paramref name="wy"/>):
    /// re-anchored on a wire of its own net under the pointer, offset from its kept anchor when the pointer is over no
    /// wire, or back where it was — with the reason — over another net's wire. An unanchored label names the node it
    /// is drawn on, so it may only be dropped onto its own net.
    /// </summary>
    private (NetLabelPlace To, string? Refusal) NetLabelDrop(EditableNetLabel label, double wx, double wy)
    {
        double dx = wx - _labelDragPressX, dy = wy - _labelDragPressY;
        double tol = Math.Max(EditModel.GridSize / 2, 8.0 / Math.Max(CanvasZoom, 1e-6));
        var near = SchematicHitTest.NearestPointOnWireSegment(EditModel, wx, wy, tol);
        if (near.Found)
        {
            if (!NetOfLabelWires(label).Contains(near.WireId))
                return (_labelDragFrom, $"'{label.Name}' names its own net; it cannot be dropped on another net's wire.");
            // Its perpendicular gap kept when it is a label's own small one, so it reads the same on its new stretch.
            var (kx, ky) = Math.Abs(_labelDragFrom.OffsetX) + Math.Abs(_labelDragFrom.OffsetY) <= SchematicComponent.LabelWorldHeight
                ? (_labelDragFrom.OffsetX, _labelDragFrom.OffsetY) : (0.0, 0.0);
            var tmp = new EditableNetLabel();
            tmp.AnchorToWire(EditModel.FindWire(near.WireId)!, near.X + kx, near.Y + ky);
            return (NetLabelPlace.Of(tmp), null);
        }
        if (!label.IsAnchored)
            return (_labelDragFrom, $"'{label.Name}' is not on a wire; drop it on a wire of its own net.");
        // Off the wire it keeps its anchor and takes the drop as its offset — no further than MaxLabelOffset from the
        // point it is anchored at, so a label always reads as its wire's.
        double ox = _labelDragFrom.OffsetX + dx, oy = _labelDragFrom.OffsetY + dy, len = Math.Sqrt(ox * ox + oy * oy);
        if (len > MaxLabelOffset) (ox, oy) = (ox * MaxLabelOffset / len, oy * MaxLabelOffset / len);
        double footX = _labelDragFrom.X - _labelDragFrom.OffsetX, footY = _labelDragFrom.Y - _labelDragFrom.OffsetY;
        return (_labelDragFrom with { OffsetX = ox, OffsetY = oy, X = footX + ox, Y = footY + oy }, null);
    }

    /// <summary>How far a dragged net label may sit from the point on its wire it is anchored at: one connection-grid
    /// cell, which clears a part's value or a crossing wire and still reads as the wire's own label.</summary>
    public const double MaxLabelOffset = 100;

    /// <summary>Every wire of the net <paramref name="label"/> names: the run it is anchored to (or drawn on) and every run
    /// connected to it, and the same for each other label of its name — a name joins the runs it is on.</summary>
    private HashSet<string> NetOfLabelWires(EditableNetLabel label)
    {
        var seeds = new List<string>();
        foreach (var l in EditModel.NetLabels.Where(l => string.Equals(l.Name, label.Name, StringComparison.Ordinal)))
        {
            var place = ReferenceEquals(l, label) ? _labelDragFrom : NetLabelPlace.Of(l);
            if (place.OwnerWireId.Length > 0) seeds.Add(place.OwnerWireId);
            else if (SchematicHitTest.NearestPointOnWireSegment(EditModel, place.X, place.Y, EditModel.GridSize / 2) is { Found: true } on)
                seeds.Add(on.WireId);
        }
        return NetExtractor.ConnectedWireIds(EditModel, seeds);
    }
}
