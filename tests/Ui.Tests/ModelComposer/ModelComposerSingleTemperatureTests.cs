using CircuitRF.Design.Modeling;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>brief-modelcomposer-15 R-mc15-4 (overview D8): a one-temperature document's Thermal step shows T0 and the words
/// "one temperature" in place of its controls.</summary>
public sealed class ModelComposerSingleTemperatureTests
{
    [Fact]
    public async Task OneTemperature_ShowsT0_AndNoThermalControls()
    {
        ComposerFixture.Folder(out string cmc);
        ModelCommands.New(cmc, CmcKind.Active, "gan-two-shell");
        ModelCommands.Import(cmc, ModelCliFixture.SSet(cmc));
        ModelCommands.Import(cmc, ModelCliFixture.DcSet);
        var vm = await ComposerFixture.Open(cmc);
        vm.SelectedStep = vm.Steps.First(s => s.Name == "Thermal");

        Assert.Equal("Thermal", vm.StepName);
        Assert.True(vm.OneTemperature);
        Assert.Equal("T0 25 °C", vm.T0Text);
    }
}
