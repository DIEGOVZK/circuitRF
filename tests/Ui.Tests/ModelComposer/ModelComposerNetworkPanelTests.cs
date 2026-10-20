using System.Text;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.ModelComposer;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-14 R-mc14-8: the network panel takes a pasted <c>.csch</c> selection with the six pins as the
/// network, refuses one without them in one line, and copies its network as a selection that pastes into a schematic.
/// </summary>
public sealed class ModelComposerNetworkPanelTests
{
    private static (List<EditableComponent> Components, List<EditableWire> Wires, List<EditableNetLabel> Labels) Selection(string stock)
    {
        var model = SchematicPersistence.Deserialize(Encoding.UTF8.GetString(StockNetworks.Bytes(stock))).model;
        return ([.. model.Components], [.. model.Wires], [.. model.NetLabels]);
    }

    private static async Task<ModelComposerViewModel> Empty()
    {
        ComposerFixture.Folder(out string cmc);
        ModelCommands.New(cmc, CmcKind.Active);
        return await ComposerFixture.Open(cmc);
    }

    private static void Clipboard(ModelComposerViewModel vm, IReadOnlyList<EditableComponent> c, IReadOnlyList<EditableWire> w,
                                  IReadOnlyList<EditableNetLabel> l) =>
        vm.NetworkPasteSource = () =>
            Task.FromResult<(IReadOnlyList<EditableComponent>, IReadOnlyList<EditableWire>, IReadOnlyList<EditableNetLabel>)?>((c, w, l));

    [Fact]
    public async Task PastingASelectionWithSixPins_InstallsItAsTheNetwork()
    {
        var vm = await Empty();
        var (c, w, l) = Selection("series-only");
        Clipboard(vm, c, w, l);

        Assert.True(await vm.PasteNetwork(), vm.StatusText);
        Assert.Equal("network.csch", vm.NetworkName);
        Assert.True(File.Exists(Path.Combine(vm.DocumentFolder!, "network.csch")));
        Assert.Equal(6, vm.NetworkValues.Count);
        Assert.NotNull(vm.NetworkDrawing);
        Assert.Equal("", vm.NetworkRefusal);
        Assert.Equal(1, vm.UndoDepth);
    }

    [Fact]
    public async Task PastingASelectionWithoutTheSixPins_IsRefusedInOneLine_AndWritesNothing()
    {
        var vm = await Empty();
        var (c, w, l) = Selection("series-only");
        c.Remove(c.First(x => x.Symbol == SymbolKind.Pin));
        Clipboard(vm, c, w, l);

        Assert.False(await vm.PasteNetwork());
        Assert.Equal(ModelStatusLevel.Error, vm.StatusLevel);
        Assert.Contains("found", vm.StatusText);
        Assert.DoesNotContain('\n', vm.StatusText);
        Assert.False(File.Exists(Path.Combine(vm.DocumentFolder!, "network.csch")));
        Assert.Null(vm.Document.Network);
        Assert.Equal(0, vm.UndoDepth);
    }

    [Fact]
    public async Task CopyingTheNetwork_PastesIntoASchematicAsAValidSelection()
    {
        var vm = await Empty();
        await vm.ChooseStockCommand.ExecuteAsync("GaN two-shell");
        SchematicEditModel? copied = null;
        vm.NetworkCopySink = m => { copied = m; return Task.CompletedTask; };

        Assert.True(await vm.CopyNetwork());
        Assert.NotNull(copied);

        // What a schematic's paste reads back is the clipboard's JSON: a selection with every part and all six pins.
        string json = SchematicPersistence.SerializeSelection(copied!.Components, copied.Wires, copied.CanvasObjects, copied.GridSize,
                                                              copied.NetLabels);
        var pasted = SchematicPersistence.DeserializeSelectionWithLabels(json);
        Assert.Equal(copied.Components.Count, pasted.Comps.Count);
        Assert.Equal(6, pasted.Comps.Count(x => x.Symbol == SymbolKind.Pin));
        Assert.Equal(copied.Wires.Count, pasted.Wires.Count);

        // And it is a network again when pasted back.
        Clipboard(vm, pasted.Comps, pasted.Wires, pasted.NetLabels);
        Assert.True(await vm.PasteNetwork(), vm.StatusText);
    }
}
