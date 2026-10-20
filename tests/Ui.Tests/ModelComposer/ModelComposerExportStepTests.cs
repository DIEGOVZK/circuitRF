using System.Text.Json.Nodes;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Symbol;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-15 R-mc15-8: Export from the window writes the bytes <c>circuitrf model export</c> writes on the same
/// document; the symbol picker offers no 2-pin symbol; its plots are the exported model's I–V; a stale step blocks Export,
/// and Run stale clears it.
/// </summary>
public sealed class ModelComposerExportStepTests
{
    private const string Created = "2026-10-20T00:00:00Z";

    private static void Csym(string dir, string name, int pins) =>
        SymbolPersistence.SaveToFile(Path.Combine(dir, name),
            new Symbol([], [.. Enumerable.Range(0, pins).Select(k => new SymbolPin(0, 100 * k, k, $"p{k + 1}"))]));

    [Fact]
    public async Task Export_IsByteIdenticalToTheVerb_AndThePickerHidesTwoPinSymbols()
    {
        string cmc = ExportFixture.Document();
        string dir = Path.GetDirectoryName(cmc)!;
        Csym(dir, "two.csym", 2);
        Csym(dir, "three.csym", 3);

        var cli = ModelCliFixture.Run(dir, "model", "export", cmc, "-o", Path.Combine(dir, "cli.cmodel"), "--created", Created);
        Assert.True(cli.ExitCode == 0, cli.StdErr);

        var vm = await ComposerFixture.Open(cmc);
        vm.WorkspaceDirectory = dir;
        vm.ExportCreated = Created;
        vm.ExportPath = Path.Combine(dir, "window.cmodel");
        vm.SelectedStep = vm.Steps.First(s => s.Name == "Export");
        Assert.Contains(vm.Symbols, s => s.Value == "three.csym");
        Assert.DoesNotContain(vm.Symbols, s => s.Value == "two.csym");
        vm.SelectedSymbol = vm.Symbols.First(s => s.Value == "");

        Assert.True(await vm.ExportAsync(), vm.StatusText);
        Assert.Equal(File.ReadAllBytes(Path.Combine(dir, "cli.cmodel")), File.ReadAllBytes(Path.Combine(dir, "window.cmodel")));

        // The plots show what is exported — the model's I–V — and nothing about the bias plane.
        Assert.Equal([Ui.ModelComposer.ModelComposerViewModel.OutputTab, Ui.ModelComposer.ModelComposerViewModel.TransferTab], vm.PlotTabs);
    }

    [Fact]
    public async Task AStaleStep_BlocksExport_AndRunStaleClearsIt()
    {
        string cmc = ExportFixture.Document();
        // The charge step fitted to other data: its stamp no longer matches. Small networks keep the re-fit short.
        var doc = CmcFile.Read(cmc);
        var steps = (JsonObject)JsonNode.Parse(doc.Steps!.ToJsonString())!;
        steps["Charge"]![CmcStamps.DataKey] = "0";
        doc = doc with { Steps = steps };
        var q = ChargeFit.Settings(doc);
        doc = ChargeFit.WithSettings(doc, q with { Restarts = 1, Qg = q.Qg with { Hidden = [4], MaxIterations = 150 }, Qd = q.Qd with { Hidden = [4], MaxIterations = 150 } });
        CmcFile.Write(cmc, doc);

        var vm = await ComposerFixture.Open(cmc);
        vm.SelectedStep = vm.Steps.First(s => s.Name == "Export");
        Assert.Equal(["Charge"], vm.Loaded.Stale);
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.Equal("Charge is stale; run it first.", vm.ExportBlocked);
        Assert.False(await vm.ExportAsync());

        Assert.True(await vm.RunStaleAsync(), vm.StatusText);
        Assert.Empty(vm.Loaded.Stale);
        Assert.Equal("", vm.ExportBlocked);
        Assert.True(vm.ExportCommand.CanExecute(null));
    }
}
