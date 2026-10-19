// A schematic design dropped on an open schematic (brief-schimp-5 R-si5-3, overview D10): SchematicImport.BuildInto's
// drawing added through the editor's own commands — the paste that adds components, wires, canvas objects and anchored
// net labels, a net label the drawing could not anchor, and the connection grid the drawing needs when it is finer than
// the schematic's — chained into ONE CommandBatch, so a single Undo takes the whole design back off.

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Schematic.Import;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Schematic;

internal static class SchematicImportEditCommand
{
    public const string Description = "Import Schematic";

    /// <summary>One undoable command adding <paramref name="edit"/>'s drawing to <paramref name="vm"/>'s schematic, what it
    /// added selected; null when there is nothing to add.</summary>
    public static IUiCommand? For(SchematicViewModel vm, SchematicImportEdit edit)
    {
        if (edit.Drawing is not { } drawing || drawing.Components.Count + drawing.Wires.Count == 0) return null;
        var model = vm.EditModel;
        var commands = new List<IUiCommand>();
        if (edit.GridSize < model.GridSize) commands.Add(new SetGridSize(model, edit.GridSize));
        commands.Add(new SchematicPasteCommand(model, drawing.Components, drawing.Wires, drawing.CanvasObjects, ids => vm.Selection.SetAll(ids),
                                               netLabels: drawing.NetLabels.Where(l => l.IsAnchored)));
        commands.AddRange(drawing.NetLabels.Where(l => !l.IsAnchored).Select(l => new PlaceNetLabelCommand(model, l)));
        return new CommandBatch(Description, commands);
    }

    /// <summary>The connection grid, set and restored — a design on a 50-mil grid needs its points kept apart.</summary>
    private sealed class SetGridSize(SchematicEditModel model, double grid) : IUiCommand
    {
        private readonly double _was = model.GridSize;

        public string Description => SchematicImportEditCommand.Description;
        public void Execute() => model.GridSize = grid;
        public void Undo() => model.GridSize = _was;
    }
}
