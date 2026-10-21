using System.Numerics;
using CircuitRF.Design.Modeling;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.ModelComposer;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// Model Composer review 3: a click on a bias point with the lasso disarmed moves the cursor to it; a marker on a plot outlives
/// the rebuild every cursor move causes; and choosing Equation starts on the first template.
/// </summary>
public sealed class ModelComposerMarkerTests
{
    private const double W = 400, H = 300;

    [Fact]
    public async Task AClickOnABiasPoint_MovesTheCursorToIt_WithTheLassoDisarmed()
    {
        var vm = await ComposerFixture.Open(ModelComposerCurrentStepTests.Document());
        vm.SelectedStep = vm.Steps.First(s => s.Name == "Data");
        vm.SelectedPlotTab = ModelComposerViewModel.BiasPlaneTab;
        Assert.False(vm.ExcludeArmed);

        var shown = vm.RectPlot.Traces.First(t => t.CubeName == "extrinsic");
        var p = shown.Points.First(q => q.X != vm.SelectedVds!.Value || q.Y != vm.SelectedVgs!.Value);
        var tf = PlotRenderer.BuildTransforms(vm.RectPlot, (W, H));
        var at = tf.PrimaryToCanvas(p.X, p.Y);
        var lasso = vm.BiasPlaneLasso;
        var handle = lasso.HitTest(at.X, at.Y, tf);
        Assert.NotNull(handle);
        lasso.DragBegin(handle!);
        var (x, y) = tf.PrimaryFromCanvas(at.X, at.Y);
        lasso.DragTo(new Complex(x, y));
        lasso.DragEnd(cancelled: false);

        Assert.Equal(p.X, vm.SelectedVds!.Value, 6);
        Assert.Equal(p.Y, vm.SelectedVgs!.Value, 6);
        Assert.Equal(0, vm.Document.DataSets.Sum(s => s.Exclusions.Count));
        var cursor = vm.RectPlot.Traces.Single(t => t.CubeName == "cursor").Points[0];
        Assert.Equal(p, cursor);
    }

    [Fact]
    public async Task AMarker_OutlivesTheRebuildACursorMoveCauses()
    {
        var vm = await ComposerFixture.Open(ModelComposerCurrentStepTests.Document());
        vm.SelectedStep = vm.Steps.First(s => s.Name == "Data");
        vm.SelectedPlotTab = ModelComposerViewModel.BiasPlaneTab;
        var trace = vm.RectPlot.Traces.First(t => t.CubeName == "extrinsic");
        Assert.True(trace.IsPointCloud);
        var marker = new Marker(trace, 0, false, false, 1) { MarkerKind = MarkerKind.Polyline, PositionStatic = trace.Points[3] };
        trace.Markers.Add(marker);

        vm.SelectedVds = vm.VdsChoices.First(c => c != vm.SelectedVds);
        var rebuilt = vm.RectPlot.Traces.First(t => t.CubeName == "extrinsic");
        Assert.NotSame(trace, rebuilt);
        Assert.Same(marker, Assert.Single(rebuilt.Markers));

        // Another tab's plot carries none of it, and coming back puts it back.
        vm.SelectedPlotTab = ModelComposerViewModel.OutputTab;
        Assert.DoesNotContain(vm.RectPlot.Traces, t => t.Markers.Count > 0);
        vm.SelectedPlotTab = ModelComposerViewModel.BiasPlaneTab;
        Assert.Same(marker, Assert.Single(vm.RectPlot.Traces.First(t => t.CubeName == "extrinsic").Markers));
    }

    [Fact]
    public async Task ChoosingEquation_StartsOnTheFirstTemplate()
    {
        var vm = await ComposerFixture.Open(ModelComposerCurrentStepTests.Document());
        vm.SelectedStep = vm.Steps.First(s => s.Name == "Current");
        var id = vm.CurrentPickers[0];
        Assert.Equal(MethodPickerRow.Custom, id.Templates[^1]);

        id.SelectedChoice = id.Choices.First(c => c.Name == "Equation");

        Assert.Equal(CurrentTemplates.Angelov, CurrentFit.Settings(vm.Document).Id.Template);
        Assert.Equal(CurrentTemplates.Angelov, vm.CurrentPickers[0].Template);
        Assert.False(vm.CurrentPickers[0].IsCustom);
    }
}
