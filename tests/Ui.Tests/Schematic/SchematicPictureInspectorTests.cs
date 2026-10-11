// A picture placed in a schematic has its own Properties panel: the file, where its centre is, its size (the aspect kept
// by default) and its opacity, each change one undoable command — field report after Paste Image as Schematic, whose
// picture showed an empty inspector.

using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Schematic;

public sealed class SchematicPictureInspectorTests
{
    [Fact]
    public void ASelectedPicture_IsResizedWithItsAspect_FadedAndUndone()
    {
        var model = new SchematicEditModel();
        var picture = new EditableBitmap { ImagePath = "/nowhere/p.png", X = 500, Y = 300, Width = 400, Height = 200, Transparency = 0.65 };
        var part = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        model.CanvasObjects.Add(picture);
        model.Components.Add(part);
        var vm = new SchematicViewModel(model);
        var inspector = new SchematicPictureInspectorViewModel();
        inspector.SetContext(vm);

        Assert.False(inspector.IsActive);
        vm.Selection.SetAll([picture.Id, part.Id]);
        Assert.False(inspector.IsActive);   // a picture AND a part: not the picture's panel
        vm.Selection.SetAll([picture.Id]);
        Assert.True(inspector.IsActive);
        Assert.Equal("400", inspector.WidthText);
        Assert.Equal("35", inspector.OpacityText);

        inspector.CommitField("Width", "800");
        Assert.Equal((800.0, 400.0, 500.0, 300.0), (picture.Width, picture.Height, picture.X, picture.Y));
        inspector.CommitField("Opacity", "80");
        Assert.Equal(0.2, picture.Transparency, 9);
        inspector.CommitField("Opacity", "120");
        Assert.True(inspector.HasError);
        Assert.Equal(0.2, picture.Transparency, 9);

        vm.UndoRedo.Undo();
        vm.UndoRedo.Undo();
        Assert.Equal((400.0, 200.0, 0.65), (picture.Width, picture.Height, picture.Transparency));
    }
}
