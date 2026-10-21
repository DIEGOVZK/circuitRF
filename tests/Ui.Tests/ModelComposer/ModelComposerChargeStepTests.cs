using CircuitRF.Ui.ModelComposer;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-15 R-mc15-3, review 2: the 1-D C–V slices are taken at the model sweep's family values over its span,
/// and follow it, with the sweep in place of the cursor on the toolbar; the ΔQ map is a contour plot of exactly the charge
/// step's plot data.
/// </summary>
public sealed class ModelComposerChargeStepTests
{
    [Fact]
    public async Task TheSlice_FollowsTheSweep_AndTheDeltaQMap_IsAContourOfThePlotData()
    {
        var vm = await ComposerFixture.Open(ExportFixture.Document());
        vm.SelectedStep = vm.Steps.First(s => s.Name == "Charge");
        if (vm.PlotsTask is { } t) await t;
        var plots = Assert.IsType<CircuitRF.Design.Modeling.StepPlotData>(vm.StepPlots);

        vm.SelectedPlotTab = ModelComposerViewModel.CvGateTab;
        Assert.True(vm.ShowSweep);
        Assert.False(vm.ShowVdsCursor);
        vm.SweepVgsStart = "-2";
        vm.SweepVgsStop = "0";
        foreach (string vds in new[] { "1", "5" })
        {
            vm.SweepVdsStart = vds;
            vm.SweepVdsStop = vds;
            var (sweepGate, fixedValue, data) = vm.LastSlice!.Value;
            Assert.True(sweepGate);
            Assert.Equal(double.Parse(vds), fixedValue);
            Assert.Contains(vm.RectPlot.Traces, x => x.CubeName == "Cgs model" && x.Properties.LineEnabled);
            var expected = plots.Slice(true, fixedValue, (-2, 0), 101)!["Cgs.model"];
            Assert.Equal(expected.RealValues, data["Cgs.model"].RealValues);
            Assert.Equal(-2, data["Cgs.model"].Axes[0].Values[0]);
        }

        vm.SelectedPlotTab = ModelComposerViewModel.DQgTab;
        var contour = Assert.Single(vm.RectPlot.Traces, x => x.ContourData is not null).ContourData!;
        var cube = plots.Data["dQg"];
        Assert.Equal(cube.RealValues, contour.Grid!.Values);
        Assert.Equal(cube.Axes[1].Values, contour.Grid.XSpace);
        Assert.Equal(cube.Axes[0].Values, contour.Grid.YSpace);

        // A charge surface is drawn in the prefix its title names, so its contour labels are short numbers, not 0.
        vm.SelectedPlotTab = ModelComposerViewModel.QgTab;
        var qg = Assert.Single(vm.RectPlot.Traces, x => x.ContourData is not null).ContourData!;
        double peak = qg.Grid!.Values.Where(double.IsFinite).Max(Math.Abs);
        Assert.InRange(peak, 1, 1000);
        var (scale, prefix) = ModelComposerViewModel.ChargePrefix(plots.Data["Qg.model"].RealValues.Where(double.IsFinite).Max(Math.Abs));
        Assert.EndsWith($", {prefix}C", vm.RectPlot.CustomTitle);
        Assert.Equal(plots.Data["Qg.model"].RealValues[0] * scale, qg.Grid.Values[0], 1e-9);
    }
}
