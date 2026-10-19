// ================================================================
//  PhoneSarEditorTests.cs — brief-sar-12's two editor gestures, headless: Place Against Phantom… is one undo entry and draws
//  the separation as a dimension; Band Sweep…'s Create Setups writes the sweep and its band setups in one entry that undo
//  takes away whole. What they write is the Design layer's, gated in tests/Ui.Tests/Sar/PhoneSarTests.cs.
// ================================================================

using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Tests.Sar;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Viewer3D;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

[Collection(CircuitRF.Ui.Tests.Viewer3D.Viewer3DCollection.Name)]
public sealed class PhoneSarEditorTests : IDisposable
{
    private const long Mm = PhoneSarFixtures.Mm;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sar12-editor-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly List<C3dEditorViewModel> _open = [];

    public PhoneSarEditorTests() => Snap3DPreference.TestOverrideActive = true;

    public void Dispose()
    {
        foreach (var vm in _open) vm.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private C3dEditorViewModel Open()
    {
        var (path, cws) = PhoneSarFixtures.Workspace(_root);
        var template = CircuitRF.Ui.Tests.Em3d.SarFixtures.Setup();
        template.Name = "Template";
        template.Sar = new CemSar { FrequenciesGHz = [1.0] };
        C3dPersistence.SaveToFile(path, new C3dDocument
        {
            DisplayUnit = CircuitRF.Design.Layout.LayoutUnit.Mm,
            Objects =
            [
                C3dPhantom.New(C3dPhantomShape.FlatBox, "flat", new C3dPoint3(0, 0, 100 * Mm), 1000),
                new C3dBox { Name = "phone", Material = "Copper", Min = new(-30 * Mm, -70 * Mm, -5 * Mm), Size = new(60 * Mm, 140 * Mm, 8 * Mm) },
            ],
            Setups = [EmSetupPersistence.ToEmbedded(template)],
        });
        var fake = new PatchRecordingBackend();
        var vm = new C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => fake, () => cws, a => a());
        _open.Add(vm);
        vm.Viewer.Resized(400, 300);
        vm.Start();
        Settle(vm);
        return vm;
    }

    private static void Settle(C3dEditorViewModel vm)
        => Assert.True(SpinWait.SpinUntil(() => vm.AdoptedGeneration == vm.Viewer.Source.Requested, TimeSpan.FromSeconds(60)), "the scene never settled");

    [Fact]
    public void PlaceAgainstPhantom_IsOneEntry_AndDrawsTheSeparation()
    {
        var vm = Open();
        int before = vm.UndoEntries;
        Assert.Null(vm.PlaceAgainstPhantom(new C3dPlacementRequest(["phone"], "flat", "liquid", 10 * Mm)));
        Assert.Equal(before + 1, vm.UndoEntries);
        Settle(vm);
        var d = Assert.IsType<Viewer3DDimension>(vm.Viewer.Dimension);
        Assert.Equal("10 mm", d.Label);
        vm.UndoRedo.Undo();
        Assert.Equal(new C3dPoint3(0, 0, 0), vm.Document.Objects[1].Placement.Origin);
    }

    [Fact]
    public void CreateSetups_WritesTheSweepAndItsBands_InOneEntry_ThatUndoTakesAway()
    {
        var vm = Open();
        int before = vm.UndoEntries;
        var dialog = new BandSweepDialogViewModel(vm) { BandsText = "0.9, 1.9", PowerText = "0.25" };
        dialog.CreateSetupsCommand.Execute(null);
        Assert.Null(dialog.Error);
        Assert.Equal(before + 1, vm.UndoEntries);
        Assert.Equal(0.25, vm.Document.BandSweeps!.Single().DrivePowerW);
        Assert.Equal(new[] { "Template", "Bands 0.9 GHz", "Bands 1.9 GHz" }, C3dSetups.Read(vm.Document).Select(s => s.Name));
        // No results root in this harness, so no table to read.
        Assert.Empty(dialog.Rows);
        vm.UndoRedo.Undo();
        Assert.Null(vm.Document.BandSweeps);
        Assert.Single(C3dSetups.Read(vm.Document));
    }
}
