// Create Schematic from Image over the placed picture (brief-img-10-schematic-emit.md R-im10-6, D14), and Edit ▸ Paste
// Image as Schematic.
//
// The drawing is an EDIT, never a file write: the editor's own commands — the paste that adds components, wires, the
// picture under them and anchored net labels (renaming a designator the schematic already has, as a paste does), a net
// label the drawing could not anchor, the tuning block with the new entries added, and any analysis — chained into ONE
// CommandBatch, so a single Undo takes the whole drawing back off, and everything it added is selected.

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Analysis;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Recognition;

internal static class SchematicImageEditCommand
{
    public const string Description = "Create Schematic from Image";

    /// <summary>One undoable command adding <paramref name="edit"/>'s drawing to <paramref name="vm"/>'s schematic, the
    /// added parts selected; null when there is nothing to add.</summary>
    public static IUiCommand? For(SchematicViewModel vm, SchematicImageEdit edit, string description = Description)
    {
        if (edit.Drawing is not { } drawing || drawing.Components.Count == 0) return null;
        var model = vm.EditModel;
        var commands = new List<IUiCommand>
        {
            new SchematicPasteCommand(model, drawing.Components, drawing.Wires, drawing.CanvasObjects, ids => vm.Selection.SetAll(ids),
                                      netLabels: drawing.NetLabels.Where(l => l.IsAnchored)),
        };
        commands.AddRange(drawing.NetLabels.Where(l => !l.IsAnchored).Select(l => new PlaceNetLabelCommand(model, l)));
        if (drawing.Tuning is { IsEmpty: false } added)
        {
            var merged = model.Tuning?.Clone() ?? new Core.Design.TuningSetup();
            foreach (var entry in added.Variables) merged.Variables.Add(entry);
            commands.Add(new SetTuningSetupCommand(model, merged, description));
        }
        commands.AddRange(drawing.Analyses.Select(a => new AddAnalysisCommand(model, a)));
        return new CommandBatch(description, commands);
    }
}
