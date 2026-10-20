using System.Text.RegularExpressions;
using CircuitRF.Design.Modeling;
using CircuitRF.Ui.ModelComposer;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-14 R-mc14-2: each panel has a lamp that hides and shows it, the last lit lamp cannot be turned off,
/// and a hidden panel is kept in the <c>.cmc</c> — with nothing written when every panel shows (railRF's rule).
/// </summary>
public sealed class ModelComposerPanelsTests
{
    [Fact]
    public void EachLamp_HidesAndShowsItsPanel()
    {
        // The window places each panel under its own Show* flag, and each lamp runs its own toggle.
        string xaml = File.ReadAllText(Path.Combine(ModelCliFixture.RepoRoot(), "src", "Ui", "Views", "ModelComposer", "ModelComposerWindow.axaml"));
        foreach (var (view, flag, toggle) in new[]
                 {
                     ("StepsPanelView", "ShowSteps", "ToggleStepsCommand"), ("PlotsPanelView", "ShowPlots", "TogglePlotsCommand"),
                     ("NetworkPanelView", "ShowNetwork", "ToggleNetworkCommand"), ("ValuesPanelView", "ShowValues", "ToggleValuesCommand"),
                 })
        {
            Assert.Matches(new Regex($@"<v:{view}[^>]*IsVisible=""\{{Binding {flag}\}}"""), xaml);
            Assert.Matches(new Regex($@"Classes.ToolActive=""\{{Binding {flag}\}}""\s+Command=""\{{Binding {toggle}\}}"""), xaml);
        }

        var vm = new ModelComposerViewModel();
        vm.ChooseKindCommand.Execute("Active");
        foreach (string panel in ModelComposerViewModel.PanelNames)
        {
            vm.TogglePanel(panel);
            Assert.False(Shown(vm, panel));
            vm.TogglePanel(panel);
            Assert.True(Shown(vm, panel));
        }
    }

    [Fact]
    public void TheLastLitLamp_CannotBeTurnedOff()
    {
        var vm = new ModelComposerViewModel();
        vm.ChooseKindCommand.Execute("Active");
        vm.TogglePanel(ModelComposerViewModel.StepsPanel);
        vm.TogglePanel(ModelComposerViewModel.NetworkPanel);
        vm.TogglePanel(ModelComposerViewModel.ValuesPanel);

        Assert.False(vm.TogglePlotsCommand.CanExecute(null));
        vm.TogglePanel(ModelComposerViewModel.PlotsPanel);
        Assert.True(vm.ShowPlots);
        Assert.True(vm.ToggleStepsCommand.CanExecute(null));
    }

    [Fact]
    public async Task AHiddenPanel_IsWritten_AndNothingIsWrittenWhenAllShow()
    {
        ComposerFixture.Folder(out string cmc);
        ModelCommands.New(cmc, CmcKind.Active);
        var vm = await ComposerFixture.Open(cmc);

        vm.TogglePanel(ModelComposerViewModel.NetworkPanel);
        Assert.True(await vm.SaveAsync(saveAs: false));
        Assert.Equal(["Network"], CmcFile.Read(cmc).Panels.Hidden);

        vm.TogglePanel(ModelComposerViewModel.NetworkPanel);
        Assert.True(await vm.SaveAsync(saveAs: false));
        Assert.DoesNotContain("\"Panels\"", File.ReadAllText(cmc));
    }

    private static bool Shown(ModelComposerViewModel vm, string panel) => panel switch
    {
        ModelComposerViewModel.StepsPanel => vm.ShowSteps,
        ModelComposerViewModel.PlotsPanel => vm.ShowPlots,
        ModelComposerViewModel.NetworkPanel => vm.ShowNetwork,
        _ => vm.ShowValues,
    };
}
