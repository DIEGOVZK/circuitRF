// A picture in a schematic resizes as a layout bitmap does: 8 handles, a corner scales uniformly about
// the opposite corner, a side stretches its own axis, Alt anchors the centre, and a locked picture
// shows its handles but does not scale. And a picture crosses between the two editors by clipboard,
// keeping its file and its aspect.

using Avalonia.Input;
using CircuitRF.Render;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Schematic;

public sealed class SchematicPictureScaleHandleTests
{
    // Centre (500,300), 400×200: the rect is (300,200)-(700,400).
    private static (SchematicViewModel Vm, EditableBitmap Picture) Selected(bool locked = false)
    {
        var model = new SchematicEditModel();
        var picture = new EditableBitmap { ImagePath = "/nowhere/p.png", X = 500, Y = 300, Width = 400, Height = 200, IsLocked = locked };
        model.CanvasObjects.Add(picture);
        var vm = new SchematicViewModel(model);
        vm.Selection.SetAll([picture.Id]);
        return (vm, picture);
    }

    private static void Drag(SchematicViewModel vm, (double X, double Y) from, (double X, double Y) to, KeyModifiers mods = default)
    {
        vm.OnPointerPressed(from.X, from.Y, mods);
        vm.OnPointerMoved(to.X, to.Y, leftDown: true, modifiers: mods);
        vm.OnPointerReleased(to.X, to.Y, mods);
    }

    [Fact]
    public void ASoleSelectedPicture_ShowsTheLayoutsEightHandles_OnItsOwnRect()
    {
        var (vm, _) = Selected();
        Assert.Equal((300.0, 200.0, 400.0, 200.0), vm.Overlay.CanvasObjectScaleBox);
        Assert.Equal(8, SchematicScaleHandles.Build(300, 200, 400, 200).Count);
    }

    [Fact]
    public void ACorner_ScalesUniformlyAboutTheOppositeCorner_ASide_StretchesOneAxis_AndUndoes()
    {
        var (vm, p) = Selected();
        Drag(vm, (700, 400), (1100, 600));                 // bottom-right corner, twice the diagonal
        Assert.Equal((800.0, 400.0, 700.0, 400.0), (p.Width, p.Height, p.X, p.Y));   // top-left (300,200) held

        vm.UndoRedo.Undo();
        vm.KeepPictureAspect = false;
        Drag(vm, (700, 300), (900, 300));                  // right side: X only
        Assert.Equal((600.0, 200.0, 600.0, 300.0), (p.Width, p.Height, p.X, p.Y));   // left edge 300 held
    }

    [Fact]
    public void Alt_AnchorsTheCentre_AndALockedPicture_DoesNotScale()
    {
        var (vm, p) = Selected();
        vm.KeepPictureAspect = false;
        Drag(vm, (500, 400), (500, 500), KeyModifiers.Alt);   // bottom side about the centre
        Assert.Equal((400.0, 400.0, 500.0, 300.0), (p.Width, p.Height, p.X, p.Y));

        var (lockedVm, lockedPicture) = Selected(locked: true);
        Assert.NotNull(lockedVm.Overlay.CanvasObjectScaleBox);
        Drag(lockedVm, (700, 400), (1100, 600));
        Assert.Equal((400.0, 200.0), (lockedPicture.Width, lockedPicture.Height));
    }

    [Fact]
    public void KeepAspect_InTheInspector_MakesASideHandleScaleBothAxes()
    {
        var (vm, p) = Selected();
        var inspector = new SchematicPictureInspectorViewModel();
        inspector.SetContext(vm);
        Assert.True(inspector.KeepAspect);                 // on by default, and the same setting as the canvas's

        Drag(vm, (700, 300), (900, 300));                  // right side, 1.5×, about the left edge's midpoint
        Assert.Equal((600.0, 300.0, 600.0, 300.0), (p.Width, p.Height, p.X, p.Y));

        inspector.KeepAspect = false;
        Assert.False(vm.KeepPictureAspect);
    }

    [Fact]
    public void TheHandles_FollowAMoveDrag_AndStayAfterTheRelease()
    {
        var (vm, _) = Selected();
        vm.OnPointerPressed(500, 300, default);              // inside the picture, on no handle
        vm.OnPointerMoved(600, 300, leftDown: true);
        Assert.Equal(400.0, vm.Overlay.CanvasObjectScaleBox?.X);
        vm.OnPointerReleased(600, 300);
        Assert.Equal((400.0, 200.0, 400.0, 200.0), vm.Overlay.CanvasObjectScaleBox);
    }

    [Fact]
    public void TheInspector_FollowsALiveResize_AndEscape_RestoresItAndTheHandles()
    {
        var (vm, _) = Selected();
        var inspector = new SchematicPictureInspectorViewModel();
        inspector.SetContext(vm);

        vm.OnPointerPressed(700, 400, default);
        vm.OnPointerMoved(1100, 600, leftDown: true);
        Assert.Equal(("800", "400"), (inspector.WidthText, inspector.HeightText));   // before release

        vm.OnKeyDown(Key.Escape, default);
        Assert.Equal(("400", "200"), (inspector.WidthText, inspector.HeightText));
        Assert.Equal((300.0, 200.0, 400.0, 200.0), vm.Overlay.CanvasObjectScaleBox);
    }

    [Fact]
    public void APicture_CrossesBetweenLayoutAndSchematic_WithItsFileAspectAndArrangement()
    {
        var layer = new LayerKey(1, 0);
        var layout = new List<BitmapShape>
        {
            new() { Layer = layer, ImagePathRef = "/img/a.png", X = 0,    Y = 1000, W = 4000, H = 2000, Opacity = 0.5 },
            new() { Layer = layer, ImagePathRef = "/img/b.png", X = 4000, Y = 0,    W = 2000, H = 1000 },
        };

        var schematic = PictureClipboard.ToSchematic(layout);
        Assert.Equal(["/img/a.png", "/img/b.png"], schematic.Select(b => b.ImagePath));
        Assert.Equal(PictureClipboard.SchematicPlacementLongEdge, schematic[0].Width + schematic[1].Width, 9);
        Assert.Equal(2.0, schematic[0].Width / schematic[0].Height, 9);
        Assert.Equal(0.5, schematic[0].Transparency, 9);
        Assert.True(schematic[0].Y < schematic[1].Y);      // higher in the layout (Y up) is higher in the schematic (Y down)

        var back = PictureClipboard.ToLayout(schematic, layer, 0, 0, viewportWidthDbu: 24000);
        Assert.Equal(["/img/a.png", "/img/b.png"], back.Select(b => b.ImagePathRef));
        Assert.Equal((4000L, 2000L, 0.5), (back[0].W, back[0].H, back[0].Opacity));
        Assert.Equal(1000L, back[0].Y - back[1].Y);
    }
}
