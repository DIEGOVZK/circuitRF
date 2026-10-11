// A screenshot of circuitRF's own schematic editor, pasted back in — the first thing anyone tries, and the field report
// that reshaped the schematic emit: a 50 Ω Term and an S2P block with its Ref grounded, a wire running off the right
// edge, the labels drawn large enough to cover both ground symbols, the canvas grid behind it all.
//
// The picture states two nets, one port and one S-parameter block, and the drawing must look like it: a Term, an SnP,
// the wire off to the right left as a wire (no port invented at a loose end), no net label the picture does not write,
// a ground symbol where the picture draws each, every wire on a grid line of the picture's own grid, and the picture an
// ordinary bitmap under it. Paste Image as Schematic places that drawing clear of what the sheet already holds.

using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

public sealed class ImageScreenshotPasteTests
{
    private static ImageSource Screenshot() =>
        ImageSource.FromFile(Path.Combine(FixturePaths.Require(ForeignPictures.Folder), "term-s2p-screenshot.png")).Source!;

    [Fact]
    public void TheEditorsGrid_IsReadAsThePicturesScale()
    {
        var grid = PictureGrids.Detect(Screenshot().Raster);
        Assert.NotNull(grid);
        Assert.InRange(grid!.Value.Pitch, 71.2, 71.6);   // grid lines at x = 61, 133, 204 … and y = 47, 118, 190 …
        Assert.InRange(grid.Value.PhaseX, 60.5, 62.5);
        Assert.InRange(grid.Value.PhaseY, 46, 48);
    }

    [Fact]
    public void ATermAndAnS2P_AreATermAndAnS2P_DrawnAsThePictureDrawsThem()
    {
        var paste = SchematicImageRecognition.PasteDrawing(new SchematicImageInput { Source = Screenshot() }, null);
        Assert.True(paste.Ok, paste.Refusal);
        var tb = paste.Reading.Circuit!.TestBench;

        var port = Assert.Single(tb.Instances, i => i.Reference == "Port");
        var block = Assert.Single(tb.Instances, i => i.Reference == "SnP");
        Assert.Equal(2, tb.Instances.Count);
        Assert.Equal("2", block.Overrides.Single(o => o.Name == "NumPorts").Expression);
        Assert.Equal("0", block.RefNetBinding);
        Assert.Equal(port.NetBindings[0], block.NetBindings[0]);
        Assert.Equal("0", port.NetBindings[1]);

        var drawing = paste.Drawing!;
        Assert.Empty(drawing.NetLabels);
        Assert.Equal(2, drawing.Components.Count(c => c.Symbol == SymbolKind.Ground));
        // Every wire vertex on the grid, and the wire running off the picture's right edge drawn to a free end there.
        Assert.All(drawing.Wires.SelectMany(w => w.Points), p =>
        {
            Assert.Equal(0, p.X % NetlistSchematic.Grid, 6);
            Assert.Equal(0, p.Y % NetlistSchematic.Grid, 6);
        });
        double right = paste.Picture.X + paste.Picture.Width;
        Assert.Contains(drawing.Wires.SelectMany(w => w.Points), p => p.X >= right - NetlistSchematic.Grid);

        // The drawing extracts to the circuit read.
        var drawn = NetExtractor.Extract(drawing).TestBench;
        var drawnBlock = drawn.Instances.Single(i => i.InstanceName == block.InstanceName);
        var drawnPort = drawn.Instances.Single(i => i.InstanceName == port.InstanceName);
        Assert.Equal(drawnPort.NetBindings[0], drawnBlock.NetBindings[0]);
        Assert.NotEqual(drawnBlock.NetBindings[0], drawnBlock.NetBindings[1]);
    }

    [Fact]
    public void PasteInto_LandsClearOfTheSheet_WithThePictureAnOrdinaryBitmapUnderIt()
    {
        var into = new SchematicEditModel();
        into.Components.Add(new EditableComponent { InstanceName = "S1", Symbol = SymbolKind.Snp });
        var paste = SchematicImageRecognition.PasteDrawing(new SchematicImageInput { Source = Screenshot() }, null);
        var edit = SchematicImageRecognition.PasteInto(paste, into, (5000, -3000), "pasted.source.png");

        Assert.True(edit.Ok, edit.Refusal);
        var drawing = edit.Drawing!;
        var picture = Assert.Single(drawing.CanvasObjects.OfType<EditableBitmap>());
        Assert.False(picture.IsLocked);
        Assert.Equal("pasted.source.png", picture.ImagePath);
        // The picture's top-left corner within half a grid step of where it was asked for, moved by whole grid steps.
        Assert.InRange(picture.X - picture.Width / 2, 5000 - NetlistSchematic.Grid / 2, 5000 + NetlistSchematic.Grid / 2);
        Assert.InRange(picture.Y - picture.Height / 2, -3000 - NetlistSchematic.Grid / 2, -3000 + NetlistSchematic.Grid / 2);
        Assert.All(drawing.Wires.SelectMany(w => w.Points), p => Assert.Equal(0, p.X % NetlistSchematic.Grid, 6));
        Assert.Empty(drawing.Analyses);   // a paste is the drawing alone
        Assert.Empty(into.Components.Skip(1));   // nothing is added to the schematic here: the editor's commands do that
    }
}
