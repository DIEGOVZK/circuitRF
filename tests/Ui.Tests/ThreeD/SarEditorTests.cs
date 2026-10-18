// ================================================================
//  SarEditorTests.cs — brief-sar-3's three editor gestures, headless: the Ellipsoid tool makes an ellipsoid; Add Phantom
//  inserts one phantom in one undo entry and Flatten replaces it with its layers; the Lumped Element tool adds a record
//  that undo takes away. What they write is the Design layer's, gated in tests/Ui.Tests/Em3d/SarPhantomsLumpedGridTests.cs.
// ================================================================

using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.ThreeD.Tools;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class SarEditorTests : IDisposable
{
    private const float W = 400, H = 300;
    private const double UmM = 1e-6;
    private const long Um = 1000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar3-editor-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public SarEditorTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>A click for the centre and one 30 µm away make one ellipsoid with three equal semi-axes, in one undo entry.</summary>
    [Fact]
    public void TheEllipsoidTool_MakesAnEllipsoid()
    {
        var vm = Open();
        int before = vm.UndoEntries;
        vm.Arm(C3dToolKind.Ellipsoid);
        Assert.IsType<EllipsoidTool>(vm.Tool);
        ClickAt(vm, 20, 10, 0);
        ClickAt(vm, 50, 10, 0);
        var e = Assert.IsType<C3dEllipsoid>(vm.Document.Objects[^1]);
        Assert.Equal("ellipsoid1", e.Name);
        Assert.True(e.SemiAxes.X > 0 && e.SemiAxes.X == e.SemiAxes.Y && e.SemiAxes.Y == e.SemiAxes.Z, e.SemiAxes.ToString());
        Assert.Equal(before + 1, vm.UndoEntries);
    }

    /// <summary>Add Phantom is one undoable phantom; Flatten turns it into its three layers, in one more entry, and undo brings
    /// the phantom back.</summary>
    [Fact]
    public void AddPhantom_IsOneEntry_AndFlattenReplacesItWithItsLayers()
    {
        var vm = Open();
        int before = vm.UndoEntries;
        vm.AddPhantom(C3dPhantomShape.Ellipsoid3);
        var p = Assert.IsType<C3dPhantom>(vm.Document.Objects[^1]);
        Assert.Equal(("phantom1", 3), (p.Name, p.Layers.Count));
        Assert.Equal(before + 1, vm.UndoEntries);

        Assert.Null(vm.FlattenPhantom(vm.Document.Objects.Count - 1));
        Assert.DoesNotContain(vm.Document.Objects, o => o is C3dPhantom);
        Assert.Equal(["phantom1_skin", "phantom1_skull", "phantom1_brain"], vm.Document.Objects.OfType<C3dEllipsoid>().Select(o => o.Name));
        Assert.Equal(before + 2, vm.UndoEntries);

        vm.UndoRedo.Undo();
        Assert.Single(vm.Document.Objects.OfType<C3dPhantom>());
    }

    /// <summary>Two corners make one lumped element record, C1 at 1 pF; undo removes it.</summary>
    [Fact]
    public void TheLumpedElementTool_AddsARecord_ThatUndoRemoves()
    {
        var vm = Open();
        vm.Arm(C3dToolKind.LumpedElement);
        Assert.IsType<LumpedElementTool>(vm.Tool);
        ClickAt(vm, 20, 10, 0);
        ClickAt(vm, 40, 30, 0);
        var le = Assert.Single(vm.Document.LumpedElements);
        Assert.Equal(("C1", "1", "pF"), (le.Name, le.C, le.CUnit));
        Assert.Empty(vm.Document.Objects.Where(o => o.Name == "C1"));

        vm.UndoRedo.Undo();
        Assert.Empty(vm.Document.LumpedElements);
    }

    private C3dEditorViewModel Open()
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ws);
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), new Technology
        {
            Name = "tech", Materials = [new TechMaterial { Name = "Gold", Sigma20 = 4.1e7 }],
        });
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = Path.Combine(ws, "cell", "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cell.c3d");
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            SnapDbu = 1 * Um,
            Objects = [new C3dBox { Name = "pad", Material = "Gold", Min = new C3dPoint3(100 * Um, 0, 0), Size = new C3dPoint3(40 * Um, 40 * Um, 20 * Um) }],
        });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => Path.Combine(ws, ".cws"), a => a());
        _open.Add(vm);
        vm.Viewer.Resized(W, H);
        vm.Start();
        Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)), "the scene never settled");
        var s = vm.Viewer.Scene;
        var cam = Camera3D.Fit(s.ToLocal(-20 * UmM, -20 * UmM, -20 * UmM), s.ToLocal(150 * UmM, 80 * UmM, 80 * UmM), W / H);
        cam.Yaw = -0.9f; cam.Pitch = 0.5f;
        vm.Viewer.View.Camera = cam;
        return vm;
    }

    private static void ClickAt(C3dEditorViewModel vm, double x, double y, double z)
    {
        var v = vm.Viewer;
        var (sx, sy, front) = v.View.Camera.Project(v.Scene.ToLocal(x * UmM, y * UmM, z * UmM), W, H);
        Assert.True(front);
        v.Hover(sx, sy);
        var (id, face) = Scene3DPicking.PairAtPixel(v.Scene, v.View.Camera, sx, sy, W, H, v.View.Visible);
        v.OnPicked(id, face, Vector3.Zero, id != 0);
        v.Click(false);
    }
}
