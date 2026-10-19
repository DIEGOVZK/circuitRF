// Designer report, round 21 — Update Schematic from Layout on imported artwork. One test per claim:
//   * artwork with no placed parts is drawn, with its lines, into an EMPTY schematic as one undoable edit, and the
//     drawing writes nothing;
//   * an empty schematic view (what an earlier no-op update leaves behind) does not block writing into the artwork's
//     own cell;
//   * a placed part linked to the schematic takes its kind and value from that schematic.

using System;
using System.IO;
using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Commands.Schematic;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class DesignerRound21LayoutToSchematicTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-r21-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void ArtworkWithNoPlacedParts_IsDrawnIntoAnEmptySchematic_AsOneUndoableEdit()
    {
        var input = EmitBoards.Saved(_root);
        string schematicDir = Path.Combine(_root, "Board", "schematic");

        var run = ArtworkRecognition.Draw(input, schematicDir);

        Assert.Null(run.Refusal);
        Assert.Null(run.SchematicPath);
        Assert.False(Directory.Exists(schematicDir) && Directory.EnumerateFiles(schematicDir).Any());
        var drawn = run.Schematic!;
        Assert.Contains(drawn.Components, c => c.FromArtwork && LineTypeSwap.IsLineKind(c.Symbol));
        Assert.NotNull(drawn.ArtworkSource);

        var open = new SchematicEditModel { SchematicDirectory = schematicDir };
        var fill = new FillEmptySchematicCommand(open, drawn, "fill");
        fill.Execute();
        Assert.Equal(drawn.Components.Count, open.Components.Count);
        Assert.Equal((drawn.TechRef, drawn.ArtworkSource), (open.TechRef, open.ArtworkSource));
        fill.Undo();
        Assert.True(RecognitionTarget.IsEmptyDrawing(open));
        Assert.Null(open.ArtworkSource);
    }

    [Fact]
    public void AnEmptySchematicView_DoesNotBlockTheArtworkCell()
    {
        var input = EmitBoards.Saved(_root);
        string cell = Path.Combine(_root, "Board");
        string empty = CellCreate.WriteSchematicView(cell, "Board", "Drawing");

        Assert.True(RecognitionTarget.ArtworkCellOffered(input.ClayPath!));
        var run = ArtworkRecognition.Run(input, RecognitionTarget.ArtworkCell);

        Assert.True(run.Ok, run.Refusal);
        Assert.Equal(empty, run.SchematicPath);
        Assert.NotEmpty(SchematicPersistence.LoadFromFile(empty).model.Components);
        Assert.False(RecognitionTarget.ArtworkCellOffered(input.ClayPath!));
    }

    /// <summary>An instance the schematic owns stores no kind; its designator here says nothing either. The kind and
    /// the value — written as the editor stores them, the prefix in the expression and the unit apart — are the
    /// linked component's.</summary>
    [Fact]
    public void APlacedPartLinkedToTheSchematic_TakesItsKindAndValue()
    {
        var land = Land("0603", DensityLevel.Nominal, 10_000, 5_000);
        var (l, r) = Ends(land);
        string landDir = CellFolder.SubFolderPath(CellFolder.CreateCellFolder(_root, "Land"), ViewType.Layout);
        Directory.CreateDirectory(landDir);
        var cellView = new LayoutView();
        cellView.Pins.Add(new LayoutPin { Name = "1", X = Um(l - 10_000), Y = 0, Layer = Top });
        cellView.Pins.Add(new LayoutPin { Name = "2", X = Um(r - 10_000), Y = 0, Layer = Top });
        LayoutPersistence.SaveToFile(Path.Combine(landDir, "Land.clay"), cellView);

        var view = PlaneAndLine(0, l);
        view.Shapes.Add(Line(Top, r, 40_000, 5_000));
        view.Shapes.AddRange(land);
        view.Instances.Add(new LayoutInstance { CellRef = Path.Combine("..", "..", "Land"), X = Um(10_000), Y = Um(5_000), SchematicId = "M5" });
        string board = CellFolder.CreateCellFolder(_root, "Board");
        string boardDir = CellFolder.SubFolderPath(board, ViewType.Layout);
        Directory.CreateDirectory(boardDir);
        string clay = Path.Combine(boardDir, "Board.clay");
        LayoutPersistence.SaveToFile(clay, view);

        var schematic = new SchematicEditModel();
        var m5 = new EditableComponent { InstanceName = "M5", Symbol = SymbolKind.Resistor };
        m5.Parameters.Add(new EditableParameter { Name = "R", Expression = "70k", Unit = "Ω" });
        schematic.Components.Add(m5);
        CellCreate.WriteSchematicView(board, "Board", "Board", schematic);

        var result = ArtworkRecognition.Recognize(new RecognitionInput
        {
            View = view, Technology = TwoLayerWithMask(), Shapes = view.Shapes, ClayPath = clay,
        });

        Assert.True(result.Ok, result.Refusal);
        var row = Assert.Single(result.Parts.Rows);
        Assert.Equal(("M5", PartKind.R, PartEvidenceSource.Instance, PartEvidenceSource.Instance),
                     (row.Refdes, row.Kind, row.Evidence[PartField.Kind], row.Evidence[PartField.Value]));
        Assert.Equal(70e3, row.Value!.Value, 9);
        Assert.Null(row.Variable);
    }
}
