using CircuitRF.Ui.ModelComposer;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-15 R-mc15-3: the 1-D C–V slice is taken at the cursor's fixed voltage and follows it; the ΔQ map is a
/// contour plot of exactly the charge step's plot data.
/// </summary>
public sealed class ModelComposerChargeStepTests
{
    [Fact]
    public async Task TheSlice_FollowsTheCursor_AndTheDeltaQMap_IsAContourOfThePlotData()
    {
        var vm = await ComposerFixture.Open(ExportFixture.Document());
        vm.SelectedStep = vm.Steps.First(s => s.Name == "Charge");
        if (vm.PlotsTask is { } t) await t;
        var plots = Assert.IsType<CircuitRF.Design.Modeling.StepPlotData>(vm.StepPlots);

        vm.SelectedPlotTab = ModelComposerViewModel.CvGateTab;
        foreach (var vds in vm.VdsChoices)
        {
            vm.SelectedVds = vds;
            var (sweepGate, fixedValue, data) = vm.LastSlice!.Value;
            Assert.True(sweepGate);
            Assert.Equal(vds.Value, fixedValue);
            Assert.Contains(vm.RectPlot.Traces, x => x.CubeName == "Cgs model" && x.Properties.LineEnabled);
            Assert.Equal(plots.Slice(true, vds.Value)!["Cgs.model"].RealValues, data["Cgs.model"].RealValues);
        }

        vm.SelectedPlotTab = ModelComposerViewModel.DQgTab;
        var contour = Assert.Single(vm.RectPlot.Traces, x => x.ContourData is not null).ContourData!;
        var cube = plots.Data["dQg"];
        Assert.Equal(cube.RealValues, contour.Grid!.Values);
        Assert.Equal(cube.Axes[1].Values, contour.Grid.XSpace);
        Assert.Equal(cube.Axes[0].Values, contour.Grid.YSpace);
    }
}
