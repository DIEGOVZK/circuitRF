// Text copied in one of the schematic and symbol editors pastes into the other (Clipboard.TextClipboard).
// Only the texts cross, with every field but Force readable; each editor's own format is still its own.
// Driven at the text the clipboard carries — Avalonia's IClipboard cannot be implemented by a test.

using System.Linq;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Schematic;

public sealed class CrossEditorTextPasteTests
{
    [Fact]
    public void SymbolText_PastesIntoASchematic_AsTheSameText_AndOnlyTheText()
    {
        var text = new TextPrimitive
        {
            Content = "RF", AnchorX = 40, AnchorY = -60, FontSize = 30,
            FontStyle = SymbolFontStyle.Bold, Align = SymbolTextAlign.Center, VAlign = SymbolTextVAlign.Middle,
            Rotation = SymbolRotation.R90, ColorRole = SymbolColorRole.SymbolPlus, ForceReadable = true,
        };
        string json = SymbolClipboard.SerializeSelection(
            [text, new LinePrimitive { X1 = 0, Y1 = 0, X2 = 100, Y2 = 0 }], [new SymbolPin(0, 0, 0)])!;

        var pasted = Assert.Single(TextClipboard.SymbolTextsAsSchematic(json));
        Assert.Equal(("RF", 40.0, -60.0, 30.0), (pasted.Text, pasted.X, pasted.Y, pasted.FontSize));
        Assert.Equal((SymbolFontStyle.Bold, SymbolTextAlign.Center, SymbolTextVAlign.Middle),
                     (pasted.FontStyle, pasted.Align, pasted.VAlign));
        Assert.Equal((SymbolRotation.R90, SymbolColorRole.SymbolPlus), (pasted.Rotation, pasted.ColorRole));

        // And it lands in the schematic as one undoable paste, through the editor's own paste placement.
        var model = new SchematicEditModel();
        var vm = new SchematicViewModel(model);
        vm.PasteFragment([], [], [pasted], model.GridSize);
        Assert.Same(pasted, Assert.Single(model.CanvasObjects));
        vm.UndoRedo.Undo();
        Assert.Empty(model.CanvasObjects);
    }

    [Fact]
    public void SchematicText_PastesIntoASymbol_MovedByWholeGridSteps_NearTheOrigin()
    {
        var note = new EditableText
        {
            Text = "bias", X = 5030, Y = -2970, FontSize = 70,
            FontStyle = SymbolFontStyle.Italic, Align = SymbolTextAlign.Left, VAlign = SymbolTextVAlign.Top,
            Rotation = SymbolRotation.R180, ColorRole = SymbolColorRole.SymbolText,
        };
        var part = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 5000, Y = -3000 };
        string json = SchematicPersistence.SerializeSelection([part], [], [note], 100.0);

        var pasted = Assert.Single(TextClipboard.SchematicTextsAsSymbol(json));
        Assert.Equal(("bias", 70.0, SymbolFontStyle.Italic, SymbolRotation.R180, SymbolColorRole.SymbolText),
                     (pasted.Content, pasted.FontSize, pasted.FontStyle, pasted.Rotation, pasted.ColorRole));

        // Moved by whole P = 100 steps, so it keeps its place relative to the grid…
        Assert.Equal(30.0, ((pasted.AnchorX % 100) + 100) % 100, 9);
        Assert.Equal(30.0, ((pasted.AnchorY % 100) + 100) % 100, 9);
        // …and its box is centred within half a step of the symbol's origin.
        var (x0, y0, x1, y1) = SymbolGeometry.BboxOf(pasted);
        Assert.InRange((x0 + x1) / 2, -50, 50);
        Assert.InRange((y0 + y1) / 2, -50, 50);
    }

    [Fact]
    public void EachEditorsOwnFormat_IsNotReadAsTheOthers()
    {
        string symbolJson = SymbolClipboard.SerializeSelection([new TextPrimitive { Content = "s" }], [])!;
        string schematicJson = SchematicPersistence.SerializeSelection([], [], [new EditableText { Text = "t" }], 100.0);

        Assert.Empty(TextClipboard.SchematicTextsAsSymbol(symbolJson));
        Assert.Empty(TextClipboard.SymbolTextsAsSchematic(schematicJson));
        Assert.Empty(TextClipboard.SchematicTextsAsSymbol("not json at all"));
        Assert.Empty(TextClipboard.SymbolTextsAsSchematic(null));
    }
}
