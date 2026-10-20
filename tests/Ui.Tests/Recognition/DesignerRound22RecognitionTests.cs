// Designer round 22 (2026-10-20), Create Schematic from Artwork: a land pattern the bill of materials does not list is
// not fitted; a 0 Ω part stays in the circuit as a 0 Ω resistor the drawing marks Shorted; and a line or tee Update
// Layout from Schematic generated is copper the traces are read from, never a part.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class DesignerRound22RecognitionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-r22-rec-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void ALandPatternTheBillOfMaterialsDoesNotList_IsNotFitted()
    {
        var (view, placement, _) = PartsBoard();
        var bom = new BomTable("board.csv", null, ',',
            [new BomRow("R1", null, "4R7", "0603", "R"), new BomRow("C2", null, "1 pF", "0603", "C")], 2, 0, [], []);

        var result = RecognizeParts(view, placement, bom);

        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(PartKind.R, result.Parts.Row("R1")!.Kind);
        Assert.Equal(PartKind.C, result.Parts.Row("C2")!.Kind);
        Assert.Equal(PartKind.Open, result.Parts.Row("C3")!.Kind);
    }

    [Fact]
    public void AZeroOhmPart_IsEmittedAsAShortedZeroOhmResistor()
    {
        var (view, placement, _) = PartsBoard();
        var bom = new BomTable("board.csv", null, ',',
            [new BomRow("R1", null, "0 Ω", "0603", "R"), new BomRow("C2", null, "1 pF", "0603", "C"),
             new BomRow("C3", null, "1 pF", "0603", "C")], 3, 0, [], []);
        var input = new RecognitionInput
        {
            View = view, Technology = TwoLayerWithMask(), Shapes = view.Shapes, Placement = placement, Bom = bom,
        };

        var result = ArtworkRecognition.Recognize(input);
        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(PartKind.Short, result.Parts.Row("R1")!.Kind);

        var circuit = RecognitionEmit.Build(result, input);
        var r1 = circuit.TestBench.Instances.Single(i => i.InstanceName == "R1");
        Assert.Equal("R", r1.Reference);
        Assert.Equal("0", r1.Overrides.Single(o => o.Name == "R").Expression);
        Assert.Contains("R1", circuit.Shorted);
    }

    /// <summary>The placed-instance board of <see cref="PartReadingTests"/>, with the instance's cell recorded as one
    /// a built-in structure generator (an MLIN) drew: its pads are not a part.</summary>
    [Fact]
    public void AnInstanceOfAGeneratedLine_IsCopperNotAPart()
    {
        var land = Land("0603", DensityLevel.Nominal, 10_000, 5_000);
        var (l, r) = Ends(land);
        string cellName = "MLIN_0123456789ab";
        string cellDir = CellFolder.SubFolderPath(CellFolder.CreateCellFolder(_root, cellName), ViewType.Layout);
        Directory.CreateDirectory(cellDir);
        var cell = new LayoutView();
        cell.Pins.Add(new LayoutPin { Name = "1", X = Um(l - 10_000), Y = 0, Layer = Top });
        cell.Pins.Add(new LayoutPin { Name = "2", X = Um(r - 10_000), Y = 0, Layer = Top });
        LayoutPersistence.SaveToFile(Path.Combine(cellDir, cellName + ".clay"), cell);

        var view = PlaneAndLine(0, l);
        view.Shapes.Add(Line(Top, r, 40_000, 5_000));
        view.Shapes.AddRange(land);
        view.Instances.Add(new LayoutInstance { CellRef = Path.Combine("..", "..", cellName), X = Um(10_000), Y = Um(5_000), RefDes = "ML1" });
        view.PCellSnapshots[cellName] = new PCellSnapshot("MLIN", new Dictionary<string, PCellValue>(), null, null, null);
        string boardDir = CellFolder.SubFolderPath(CellFolder.CreateCellFolder(_root, "Board"), ViewType.Layout);
        Directory.CreateDirectory(boardDir);
        string clay = Path.Combine(boardDir, "Board.clay");
        LayoutPersistence.SaveToFile(clay, view);

        var result = ArtworkRecognition.Recognize(new RecognitionInput
        {
            View = view, Technology = TwoLayerWithMask(), Shapes = view.Shapes, ClayPath = clay,
        });

        Assert.True(result.Ok, result.Refusal);
        Assert.DoesNotContain(result.Parts.Rows, row => row.Refdes == "ML1");
    }
}
