using System.Numerics;
using CircuitRF.Design.Modeling;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.ModelComposer;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-14 R-mc14-6: with the lasso armed, a click on a bias-plane point excludes it — driven through the
/// overlay exactly as <c>PlotControl</c> drives it (hit test, begin, the press point, release) — and the plot draws it
/// excluded at once, not only after the data's reload. A second click on it includes it again.
/// </summary>
public sealed class BiasPlaneLassoTests
{
    private const double W = 400, H = 300;

    private static void Click(ModelComposerViewModel vm, double wx, double wy)
    {
        var tf = PlotRenderer.BuildTransforms(vm.RectPlot, (W, H));
        var at = tf.PrimaryToCanvas(wx, wy);
        var lasso = vm.BiasPlaneLasso;
        var handle = lasso.HitTest(at.X, at.Y, tf);
        Assert.NotNull(handle);
        lasso.DragBegin(handle!);
        var (x, y) = tf.PrimaryFromCanvas(at.X, at.Y);
        lasso.DragTo(new Complex(x, y));
        lasso.DragEnd(cancelled: false);
    }

    [Fact]
    public async Task AClickWithTheLassoArmed_ExcludesThePoint_AndAClickAgainIncludesIt()
    {
        var vm = await ComposerFixture.Open(ModelComposerCurrentStepTests.Document());
        vm.SelectedStep = vm.Steps.First(s => s.Name == "Data");
        vm.SelectedPlotTab = ModelComposerViewModel.BiasPlaneTab;
        vm.ExcludeArmed = true;

        // The data's reload after the edit is held until the plot has been looked at.
        var gate = new TaskCompletionSource();
        vm.RunOffThread = async (work, _) => { await gate.Task; return work(); };

        var shown = vm.RectPlot.Traces.First(t => t.CubeName is "intrinsic" or "extrinsic");
        var p = shown.Points[shown.Points.Count / 2];
        Click(vm, p.X, p.Y);

        Assert.Equal(1, vm.Document.DataSets.Sum(s => s.Exclusions.Count));
        // Drawn at once, before the data's reload behind it has finished.
        Assert.Contains(vm.RectPlot.Traces, t => t.CubeName == "excluded");
        Assert.True(vm.IsRunning);
        gate.SetResult();
        if (vm.CurrentTask is { } reload) await reload;
        Assert.Contains(vm.RectPlot.Traces, t => t.CubeName == "excluded");

        Click(vm, p.X, p.Y);
        Assert.Equal(0, vm.Document.DataSets.Sum(s => s.Exclusions.Count));
    }
}
