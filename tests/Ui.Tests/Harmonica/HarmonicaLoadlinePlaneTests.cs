using System.Linq;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

/// <summary>
/// brief-modelcomposer-24 R-mc24-5/6 — the plane toggle on the view model: it moves the family and the loadline together
/// onto data the frame already carries, names the plane in the axis titles, persists in the document, and costs no HB
/// solve.
/// </summary>
public sealed class HarmonicaLoadlinePlaneTests
{
    [Fact]
    public void TogglingThePlane_AddsNoHbSolve_AndShowsTheOtherPlanesCurves()
    {
        var vm = new HarmonicaViewModel();
        var options = new HarmonicaSolver.Options { SkipContours = true };
        vm.SolveFrame(options);
        int intrinsicSolves = vm.LastSolveCount;
        int dciv = vm.DcivComputeCount;
        var frame = vm.Frame;
        Assert.True(intrinsicSolves > 0);
        Assert.True(frame.Loadline.ExtrinsicLoadlineVds.Length > 1, "the extrinsic loadline rides on every frame");
        Assert.Same(frame.Loadline.LoadlineVds, frame.Loadline.ShownVds);
        Assert.Equal("Vds,i (V)", frame.Loadline.VoltageTitle);

        vm.ToggleLoadlinePlaneCommand.Execute(null);

        // A relabel of the frame in hand: no solve, no family, the same published DataSet.
        Assert.Equal(intrinsicSolves, vm.LastSolveCount);
        Assert.Equal(dciv, vm.DcivComputeCount);
        Assert.Same(frame.Published, vm.Frame.Published);
        Assert.Same(frame.Loadline.ExtrinsicLoadlineVds, vm.Frame.Loadline.ShownVds);
        Assert.Same(frame.Loadline.ExtrinsicDciv, vm.Frame.Loadline.ShownDciv);
        Assert.Equal("Vds (V)", vm.Frame.Loadline.VoltageTitle);
        Assert.Equal("Id (A)", vm.Frame.Loadline.CurrentTitle);

        // A frame solved at the extrinsic plane costs exactly what the intrinsic one did.
        vm.SolveFrame(options);
        Assert.False(vm.Frame.Loadline.Intrinsic);
        Assert.Equal(intrinsicSolves, vm.LastSolveCount);
    }

    [Fact]
    public void ThePlane_IsSavedWithTheDocument_AndReopensAsLeft()
    {
        var vm = new HarmonicaViewModel();
        vm.ToggleLoadlinePlaneCommand.Execute(null);
        string json = vm.ToCharmJson();
        Assert.Equal(LoadlinePlane.Extrinsic, CharmIo.ReadAll(json, null).Appearance.LoadlinePlaneOf);

        var reopened = new HarmonicaViewModel();
        reopened.LoadCharm(json, null);
        Assert.False(reopened.IntrinsicPlane);

        var older = new HarmonicaViewModel();
        older.ToggleLoadlinePlaneCommand.Execute(null);
        older.LoadCharm(new HarmonicaViewModel().ToCharmJson(), null);
        Assert.True(older.IntrinsicPlane);
    }
}
