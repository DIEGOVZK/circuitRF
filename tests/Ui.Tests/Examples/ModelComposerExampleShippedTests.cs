// ================================================================
//  ModelComposerExampleShippedTests.cs — the Model Composer example as it SHIPS, read from the build output beside the
//  executable rather than from the repository (owner, review 4). The application's examples item group leaves every
//  `.npy` behind, and this example's data and fitted arrays are `.npy`: the shipped copy opened with every step ticked
//  and refused the report, asking for the Current step to be run again. A copy taken from the repository, as
//  ExampleWorkspaceLoadsTests takes one, cannot see that.
// ================================================================

using CircuitRF.Design.Modeling;
using CircuitRF.Ui.Tests.ModelComposer;
using Xunit;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class ModelComposerExampleShippedTests
{
    private static readonly string Shipped = Path.Combine(AppContext.BaseDirectory, "examples", ModelComposerExampleAuthoring.Folder);

    /// <summary>Every fit a shipped document records has its arrays beside it, and the simulated tables carry their data.</summary>
    [Fact]
    public void EveryShippedFit_HasItsArrays_AndEveryDataFileShips()
    {
        string repo = Path.Combine(ModelCliFixture.RepoRoot(), "examples", ModelComposerExampleAuthoring.Folder);
        foreach (string npy in Directory.GetFiles(repo, "*.npy", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(repo, npy);
            Assert.True(File.Exists(Path.Combine(Shipped, rel)), $"{rel} is in the example but does not ship");
        }
        foreach (string cmc in Directory.GetFiles(Shipped, "*.cmc", SearchOption.AllDirectories))
        {
            var doc = CmcFile.Read(cmc);
            if (CurrentFit.Record(doc) is not null) Assert.True(CurrentFit.HasArrays(cmc, doc), $"{cmc}: Current's arrays");
            if (ChargeFit.Record(doc) is not null) Assert.True(ChargeFit.HasArrays(cmc, doc), $"{cmc}: Charge's arrays");
        }
    }

    /// <summary>A fit whose arrays are gone is not ticked on the rail: it has to be run again before a report or export.</summary>
    [Fact]
    public async Task AFitWhoseArraysAreGone_IsNotTicked()
    {
        string to = Path.Combine(Directory.CreateTempSubdirectory("crf-mc-shipped-").FullName, ModelComposerExampleAuthoring.Folder);
        foreach (string f in Directory.GetFiles(Shipped, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(Shipped, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
        string gan = Path.Combine(to, "synthetic-gan", SyntheticGanDocument.Name);
        var vm = await ComposerFixture.Open(gan);
        Assert.True(vm.Steps.First(s => s.Name == "Current").IsDone);

        foreach (string f in Directory.GetFiles(Path.Combine(CmcCache.FolderFor(gan), CurrentFit.CacheFolder))) File.Delete(f);
        vm = await ComposerFixture.Open(gan);
        Assert.False(vm.Steps.First(s => s.Name == "Current").IsDone);
        Assert.True(vm.Steps.First(s => s.Name == "Charge").IsDone);
    }
}
