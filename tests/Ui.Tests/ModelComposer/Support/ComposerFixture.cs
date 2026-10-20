using CircuitRF.Design.Modeling;
using CircuitRF.Ui.ModelComposer;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-14's documents: MC-13's raw files (<see cref="ModelCliFixture.WriteRaw"/>) in a fresh folder, a
/// <c>.cmc</c> made by the shared commands, and a window view model over it with no application host.
/// </summary>
public static class ComposerFixture
{
    /// <summary>A fresh folder with the raw files, and where its <c>.cmc</c> goes.</summary>
    public static string Folder(out string cmc)
    {
        string dir = Directory.CreateTempSubdirectory("crf-mc14-").FullName;
        ModelCliFixture.WriteRaw(dir);
        cmc = Path.Combine(dir, "fet.cmc");
        return dir;
    }

    /// <summary>
    /// An Active <c>.cmc</c> with the stock GaN network and the S set imported. <paramref name="free"/> names the network
    /// values left free to fit (their values start at the truth's × 1.3); every other is held.
    /// </summary>
    public static string ActiveWithS(params string[] free)
    {
        Folder(out string cmc);
        ModelCommands.New(cmc, CmcKind.Active, "gan-two-shell");
        ModelCommands.Import(cmc, ModelCliFixture.SSet(cmc));
        var doc = CmcFile.Read(cmc);
        var n = CmcExtrinsic.Network(doc);
        var six = SixPinNetwork.Load(Path.Combine(Path.GetDirectoryName(cmc)!, n.Schematic!));
        var vars = n.Variables.ToDictionary(
            kv => kv.Key,
            kv => free.Contains(kv.Key)
                ? kv.Value with { Value = 1.3 * six.Variables.First(v => v.Name == kv.Key).Value }
                : kv.Value with { Lock = true },
            StringComparer.Ordinal);
        CmcFile.Write(cmc, CmcExtrinsic.With(doc, n with { Variables = vars, Fit = new NetworkFitSettings { Starts = 1, Budget = 120 } }));
        return cmc;
    }

    /// <summary>The window's view model over <paramref name="cmc"/>, its data opened.</summary>
    public static async Task<ModelComposerViewModel> Open(string cmc)
    {
        var vm = ModelComposerViewModel.OpenFile(cmc);
        vm.BeginLoad();
        if (vm.CurrentTask is { } t) await t;
        return vm;
    }
}
