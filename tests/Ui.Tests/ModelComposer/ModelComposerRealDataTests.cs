using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-16 R-mc16-5: the acceptance flow on a real device, when a local data folder is present. The folder
/// holds one <c>.cmc</c> with its data imported and its settings chosen; a copy of it is fitted, exported, checked for
/// charge conservation and validated against its own data, and every line is written to the output for the owner to
/// read. Nothing is committed from it and nothing about it is asserted beyond the flow completing — the numbers are a
/// report, not a gate. Skipped with a reason when the folder is absent.
/// </summary>
public sealed class ModelComposerRealDataTests(ITestOutputHelper output)
{
    private const string Fixture = "testdata/model-composer-device";

    [FixtureFact(Fixture, "a local folder holding one .cmc with its data — device data is never committed")]
    public void TheFlow_RunsOnLocalDeviceData()
    {
        string source = FixturePaths.Require(Fixture);
        string dir = Directory.CreateTempSubdirectory("crf-mc16-real-").FullName;
        try
        {
            Copy(source, dir);
            string cmc = Assert.Single(Directory.GetFiles(dir, "*.cmc"));
            foreach (var l in ModelCommands.Deembed(cmc)) output.WriteLine(l.Text);
            foreach (var l in ModelCommands.Fit(cmc, "all")) output.WriteLine(l.Text);
            string model = Path.ChangeExtension(cmc, ".cmodel");
            CmodelExport.Export(cmc, model);
            foreach (var l in ConservationCheck.RunFile(cmc, new LargeSignalSettings()).Lines) output.WriteLine(l);
            foreach (var l in ModelValidation.RunFile(cmc, model).Lines) output.WriteLine(l);
            Assert.True(File.Exists(model));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static void Copy(string from, string to)
    {
        foreach (string d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
    }
}
