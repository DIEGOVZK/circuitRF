using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.ModelComposer;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-15 R-mc15-2: a current fit run from the window updates the I–V overlay — a measured trace (symbols)
/// and a model trace (a line) for every family member, all one colour — the error map and the training domain's outline are
/// drawn, and the Export step shows the same overlay.
/// </summary>
public sealed class ModelComposerCurrentStepTests
{
    /// <summary>MC-13's raw files imported, the network held at its stock values and the currents' networks small.</summary>
    internal static string Document()
    {
        ComposerFixture.Folder(out string cmc);
        ModelCommands.New(cmc, CmcKind.Active, "gan-two-shell", new BasePeriphery(4, "125 um"));
        ModelCommands.Import(cmc, ModelCliFixture.SSet(cmc));
        ModelCommands.Import(cmc, ModelCliFixture.DcSet);
        ModelCliFixture.Quick(cmc);
        var doc = CmcFile.Read(cmc);
        var s = CurrentFit.Settings(doc);
        CmcFile.Write(cmc, CurrentFit.WithSettings(doc, s with
        {
            Id = s.Id with { Hidden = [4], MaxIterations = 300 },
            Ig = s.Ig with { Hidden = [3], MaxIterations = 200 },
        }));
        return cmc;
    }

    [Fact]
    public async Task AFit_UpdatesTheOverlay_AndDrawsTheErrorMapAndTheDomain()
    {
        var vm = await ComposerFixture.Open(Document());
        vm.SelectedStep = vm.Steps.First(s => s.Name == "Current");
        Assert.Null(vm.StepPlots);
        vm.SelectedPlotTab = ModelComposerViewModel.OutputTab;
        Assert.Empty(vm.RectPlot.Traces);

        Assert.True(await vm.FitAsync("current"), vm.StatusText);
        if (vm.PlotsTask is { } t) await t;
        Assert.NotNull(vm.StepPlots);

        var traces = vm.RectPlot.Traces;
        var measured = traces.Where(x => x.Properties.MarkerEnabled && !x.Properties.LineEnabled).ToList();
        var model = traces.Where(x => x.Properties.LineEnabled && x.CubeName!.EndsWith(" model", StringComparison.Ordinal)).ToList();
        Assert.True(measured.Count >= 3, $"{measured.Count} measured family members");
        Assert.Equal(measured.Select(m => m.CubeName + " model"), model.Select(m => m.CubeName));

        vm.SelectedPlotTab = ModelComposerViewModel.ErrorTab;
        Assert.Contains(vm.RectPlot.Traces, x => x.ContourData?.Grid is not null);

        vm.SelectedPlotTab = ModelComposerViewModel.DomainTab;
        Assert.Contains(vm.RectPlot.Traces, x => x.CubeName == "domain" && x.Properties.LineEnabled);

        // One family, one colour: every member of the overlay is drawn in the same blue.
        vm.SelectedPlotTab = ModelComposerViewModel.OutputTab;
        Assert.Single(vm.RectPlot.Traces.Select(x => x.Properties.LineColorIndex).Distinct());

        // Export draws the same overlay: the model it writes, against its data.
        vm.SelectedStep = vm.Steps.First(s => s.Name == "Export");
        if (vm.PlotsTask is { } e) await e;
        Assert.Equal([ModelComposerViewModel.OutputTab, ModelComposerViewModel.TransferTab], vm.PlotTabs);
        Assert.Equal(model.Count, vm.RectPlot.Traces.Count(x => x.Properties.LineEnabled && x.CubeName!.EndsWith(" model", StringComparison.Ordinal)));
    }
}
