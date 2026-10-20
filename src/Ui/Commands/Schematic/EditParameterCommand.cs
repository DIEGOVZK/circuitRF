using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Schematic;

/// <summary>
/// Changes the expression, unit, and optionally the name of a single parameter on a component.
/// Pass newName to rename (used for VAR/SDD inline editing where the name is editable).
/// </summary>
internal sealed class EditParameterCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableParameter _param;
    private readonly string _newExpression;
    private readonly string _oldExpression;
    private readonly string _newUnit;
    private readonly string _oldUnit;
    private readonly string _newName;
    private readonly string _oldName;

    public string Description => $"Edit {_param.Name}";

    public EditParameterCommand(SchematicEditModel model, EditableParameter param,
        string newExpression, string newUnit = "", string? newName = null)
    {
        _model         = model;
        _param         = param;
        _oldExpression = param.Expression;
        _oldUnit       = param.Unit;
        _oldName       = param.Name;
        _newExpression = newExpression;
        _newUnit       = newUnit;
        _newName       = newName ?? param.Name;
    }

    public void Execute() { _param.Name = _newName; _param.Expression = _newExpression; _param.Unit = _newUnit; _model.NotifyChanged(); }
    public void Undo()    { _param.Name = _oldName; _param.Expression = _oldExpression; _param.Unit = _oldUnit; _model.NotifyChanged(); }
}

/// <summary>
/// Changes a component's InstanceName.
/// </summary>
internal sealed class RenameComponentCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableComponent _comp;
    private readonly string _newName;
    private readonly string _oldName;

    public string Description => "Rename component";

    public RenameComponentCommand(SchematicEditModel model, EditableComponent comp, string newName)
    {
        _model   = model;
        _comp    = comp;
        _oldName = comp.InstanceName;
        _newName = newName;
    }

    public void Execute() { _comp.InstanceName = _newName; _model.NotifyChanged(); }
    public void Undo()    { _comp.InstanceName = _oldName; _model.NotifyChanged(); }
}

/// <summary>
/// Renames an existing net label.
/// </summary>
internal sealed class RenameNetLabelCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableNetLabel _label;
    private readonly string _newName;
    private readonly string _oldName;

    public string Description => $"Rename net label to {_newName}";

    public RenameNetLabelCommand(SchematicEditModel model, EditableNetLabel label, string newName)
    {
        _model   = model;
        _label   = label;
        _oldName = label.Name;
        _newName = newName;
    }

    public void Execute() { _label.Name = _newName; _model.NotifyChanged(); }
    public void Undo()    { _label.Name = _oldName; _model.NotifyChanged(); }
}

/// <summary>Where a net label is: its wire anchor and its drawn point — every field a move changes.</summary>
internal readonly record struct NetLabelPlace(
    string OwnerWireId, int SegmentIndex, double AlongT, double OffsetX, double OffsetY, double X, double Y)
{
    public static NetLabelPlace Of(EditableNetLabel l) =>
        new(l.OwnerWireId, l.SegmentIndex, l.AlongT, l.OffsetX, l.OffsetY, l.X, l.Y);

    public void ApplyTo(EditableNetLabel l)
    {
        l.OwnerWireId  = OwnerWireId;
        l.SegmentIndex = SegmentIndex;
        l.AlongT       = AlongT;
        l.OffsetX      = OffsetX;
        l.OffsetY      = OffsetY;
        l.X            = X;
        l.Y            = Y;
    }
}

/// <summary>
/// A net label dragged in the Select tool (brief-dfr22-2 R-dfr22-2-5): re-anchored along a wire of its own net, or
/// offset from the anchor it keeps — one undoable step. The name never changes, so neither does any net.
/// </summary>
internal sealed class MoveNetLabelCommand(SchematicEditModel model, EditableNetLabel label, NetLabelPlace from, NetLabelPlace to)
    : IUiCommand
{
    public string Description => $"Move net label '{label.Name}'";

    public void Execute() { to.ApplyTo(label); model.NotifyChanged(); }
    public void Undo()    { from.ApplyTo(label); model.NotifyChanged(); }
}

/// <summary>
/// Moves a net label to a new wire anchor (re-anchors + optionally renames in one undoable step).
/// Used when the user double-clicks a different wire in the same net to reposition the label.
/// </summary>
internal sealed class MoveNetLabelAnchorCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly EditableNetLabel   _label;

    // Old full state
    private readonly string _oldName, _oldOwnerWireId;
    private readonly int    _oldSegmentIndex;
    private readonly double _oldAlongT, _oldOffsetX, _oldOffsetY, _oldX, _oldY;

    // New full state (computed at construction via AnchorToWire on a temp label)
    private readonly string _newName, _newOwnerWireId;
    private readonly int    _newSegmentIndex;
    private readonly double _newAlongT, _newOffsetX, _newOffsetY, _newX, _newY;

    public string Description => $"Move net label '{_oldName}'";

    public MoveNetLabelAnchorCommand(
        SchematicEditModel model,
        EditableNetLabel   label,
        string             newName,
        EditableWire?      newOwnerWire,
        double             worldX,
        double             worldY)
    {
        _model = model;
        _label = label;

        _oldName         = label.Name;
        _oldOwnerWireId  = label.OwnerWireId;
        _oldSegmentIndex = label.SegmentIndex;
        _oldAlongT       = label.AlongT;
        _oldOffsetX      = label.OffsetX;
        _oldOffsetY      = label.OffsetY;
        _oldX            = label.X;
        _oldY            = label.Y;

        _newName = newName;

        // Compute new anchor by re-anchoring a temporary label — reuses AnchorToWire math.
        var tmp = new EditableNetLabel { X = worldX, Y = worldY };
        if (newOwnerWire is not null && newOwnerWire.Points.Count >= 2)
            tmp.AnchorToWire(newOwnerWire, worldX, worldY);
        _newOwnerWireId  = tmp.OwnerWireId;
        _newSegmentIndex = tmp.SegmentIndex;
        _newAlongT       = tmp.AlongT;
        _newOffsetX      = tmp.OffsetX;
        _newOffsetY      = tmp.OffsetY;
        _newX            = tmp.X;
        _newY            = tmp.Y;
    }

    public void Execute() => Apply(_newName, _newOwnerWireId, _newSegmentIndex, _newAlongT, _newOffsetX, _newOffsetY, _newX, _newY);
    public void Undo()    => Apply(_oldName, _oldOwnerWireId, _oldSegmentIndex, _oldAlongT, _oldOffsetX, _oldOffsetY, _oldX, _oldY);

    private void Apply(string name, string wireId, int seg, double t, double ox, double oy, double x, double y)
    {
        _label.Name         = name;
        _label.OwnerWireId  = wireId;
        _label.SegmentIndex = seg;
        _label.AlongT       = t;
        _label.OffsetX      = ox;
        _label.OffsetY      = oy;
        _label.X            = x;
        _label.Y            = y;
        _model.NotifyChanged();
    }
}
