using CircuitRF.Design.Modeling;
using CircuitRF.Ui.ModelComposer;
using CircuitRF.Ui.Views.Dialogs;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-14 R-mc14-1: a document is made, fed, saved, closed and reopened where it was left — its data from
/// the cache with no source read, its panels, its step and its bias cursor — and closing unsaved work asks.
/// </summary>
public sealed class ModelComposerLifecycleTests
{
    [Fact]
    public async Task NewImportSaveCloseReopen_RestoresDataPanelsStepAndCursor_WithNoSourceRead()
    {
        ComposerFixture.Folder(out string cmc);
        var vm = new ModelComposerViewModel { PickSavePath = _ => Task.FromResult<string?>(cmc) };
        vm.ChooseKindCommand.Execute("Active");

        Assert.True(await vm.ImportAsync(ModelCliFixture.SSet(cmc)), vm.StatusText);     // the first save happens here
        Assert.True(await vm.ImportAsync(ModelCliFixture.DcSet), vm.StatusText);
        Assert.Equal(cmc, vm.DocumentPath);
        Assert.Equal(["bias", "dc"], vm.DataSetRows.Select(r => r.Name));

        vm.TogglePanel(ModelComposerViewModel.ValuesPanel);
        vm.SelectedStep = vm.Steps.Single(s => s.Name == "Network");
        vm.SelectedDataSet = vm.DataSetRows.Single(r => r.Name == "bias");
        vm.SelectedVds = vm.VdsChoices[^1];
        double vds = vm.SelectedVds!.Value;
        Assert.True(await vm.SaveAsync(saveAs: false));
        Assert.True(await vm.ConfirmCloseAsync());
        vm.Dispose();

        var again = await ComposerFixture.Open(cmc);
        Assert.Equal(0, again.Loaded.SourceReads);
        Assert.Equal(["bias", "dc"], again.Loaded.Data.Keys.Order());
        Assert.False(again.ShowValues);
        Assert.True(again.ShowPlots && again.ShowSteps && again.ShowNetwork);
        Assert.Equal("Network", again.StepName);
        Assert.Equal(vds, again.SelectedVds?.Value);
    }

    [Fact]
    public async Task ClosingWithUnsavedWork_Asks_AndCancelKeepsTheWindow()
    {
        ComposerFixture.Folder(out string cmc);
        ModelCommands.New(cmc, CmcKind.Active);
        var vm = await ComposerFixture.Open(cmc);
        var answer = SaveChangesResult.Cancel;
        int asked = 0;
        vm.AskSaveChanges = _ => { asked++; return Task.FromResult(answer); };

        Assert.True(await vm.ConfirmCloseAsync());                // clean: nothing to ask
        Assert.Equal(0, asked);

        vm.PeripheryFingers = "8";
        vm.PeripheryWidth = "125 um";
        Assert.True(vm.IsDirty);
        Assert.StartsWith("• ", vm.Title);

        Assert.False(await vm.ConfirmCloseAsync());
        Assert.Equal(1, asked);
        answer = SaveChangesResult.Save;
        Assert.True(await vm.ConfirmCloseAsync());
        Assert.Equal(new Core.Devices.Composed.BasePeriphery(8, "125 um"), CmcFile.Read(cmc).BasePeriphery);
    }
}
