// A layout bitmap's inspector shows X/Y as the picture's centre, as the schematic's picture inspector
// does; typing one moves the picture there without resizing it, as one undoable step.

using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class LayoutBitmapPositionInspectorTests
{
    [Fact]
    public void XAndY_AreTheCentre_AndMoveThePictureWithoutResizingIt()
    {
        var model = new LayoutView { DbuPerMicron = 1000 };
        var bmp = new BitmapShape { Layer = new LayerKey(1, 0), ImagePathRef = "/img/a.png", X = 1000, Y = 2000, W = 4000, H = 2000 };
        model.Shapes.Add(bmp);
        var vm = new LayoutEditorViewModel(model);
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        vm.SelectAllCommand.Execute(null);

        Assert.True(props.ShowBitmap);
        string centreX = props.BitmapXText;
        Assert.Equal(centreX, props.BitmapYText);            // centre (3000, 3000) DBU

        props.CommitField("BitmapX", "10");
        props.CommitField("BitmapY", "-1");
        Assert.Equal((8000L, -2000L, 4000L, 2000L), (bmp.X, bmp.Y, bmp.W, bmp.H));

        vm.UndoRedo.Undo();
        vm.UndoRedo.Undo();
        Assert.Equal((1000L, 2000L), (bmp.X, bmp.Y));
        Assert.Equal(centreX, props.BitmapXText);
    }

    [Fact]
    public void KeepAspect_IsOnByDefault_AndHoldsForTheWidthField_AndASideHandle()
    {
        var model = new LayoutView { DbuPerMicron = 1000 };
        var bmp = new BitmapShape { Layer = new LayerKey(1, 0), ImagePathRef = "/img/a.png", X = 0, Y = 0, W = 4000, H = 2000 };
        model.Shapes.Add(bmp);
        var vm = new LayoutEditorViewModel(model);
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        vm.SelectAllCommand.Execute(null);
        Assert.True(props.BitmapKeepAspect);

        props.CommitField("BitmapWidth", "8");                // 8 µm wide → 4 µm high
        Assert.Equal((8000L, 4000L), (bmp.W, bmp.H));

        // The right side handle at (8000, 2000), dragged to x = 12000 about the left side's midpoint: 1.5×.
        vm.OnPointerPressed(8000, 2000, KeyModifiers.None, hitTolDbu: 100);
        vm.OnPointerMoved(12000, 2000, leftDown: true, KeyModifiers.None, hitTolDbu: 100);
        vm.OnPointerReleased(12000, 2000, KeyModifiers.None);
        bmp = (BitmapShape)model.Shapes[0];                  // a scale replaces the shape with its scaled copy
        Assert.Equal((12000L, 6000L), (bmp.W, bmp.H));

        props.BitmapKeepAspect = false;
        Assert.False(vm.KeepBitmapAspect);
        props.CommitField("BitmapHeight", "2");
        Assert.Equal((12000L, 2000L), (bmp.W, bmp.H));
    }
}
