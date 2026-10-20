using System.Text.Json;
using CircuitRF.Design.Modeling;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.ModelComposer;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-15 R-mc15-7a: the report dialog's options round-trip through <c>Steps.Report</c>; the report's Data
/// Display document holds one tab per section that holds data; and its PDF is the bytes <c>circuitrf model report --pdf</c>
/// writes on the same document.
/// </summary>
public sealed class ModelReportDialogTests
{
    [Fact]
    public async Task TheOptions_RoundTripThroughStepsReport()
    {
        string cmc = ExportFixture.Document();
        var vm = await ComposerFixture.Open(cmc);
        var dialog = new ModelReportViewModel(vm)
        {
            ClassF = false, Rings = "4", Spokes = "8", SBiases = "-0.6, 6; 0, 2", FixedVds = "2, 6", FrequencyGHz = "3.5",
            Source2f0 = "0, -25", Thermal = false,
        };
        Assert.True(dialog.Apply(), dialog.Problem + vm.StatusText);
        Assert.True(await vm.SaveAsync(saveAs: false));

        var stored = ModelValidationReport.Options(CmcFile.Read(cmc));
        Assert.Equal(4, stored.Rings);
        Assert.Equal(8, stored.Spokes);
        Assert.DoesNotContain(CircuitRF.Harmonica.PaClass.F, stored.Modes);
        Assert.Equal([(-0.6, 6.0), (0.0, 2.0)], stored.SBiases);
        Assert.Equal(3.5e9, stored.FrequencyHz);
        Assert.False(stored.Thermal);

        var reopened = new ModelReportViewModel(ModelComposerViewModel.OpenFile(cmc));
        Assert.False(reopened.ClassF);
        Assert.Equal("4", reopened.Rings);
        Assert.Equal("-0.6, 6; 0, 2", reopened.SBiases);
        Assert.Equal("2, 6", reopened.FixedVds);
        Assert.Equal("3.5", reopened.FrequencyGHz);
        Assert.Equal("0, -25", reopened.Source2f0);
    }

    [Fact]
    public async Task TheDisplay_HoldsATabPerSection_AndItsPdfIsTheVerbs()
    {
        string cmc = ExportFixture.Document();
        string dir = Path.GetDirectoryName(cmc)!;
        var vm = await ComposerFixture.Open(cmc);
        // The PA classes run loadpulls; the sections kept here are the ones this document's data answers quickly.
        var dialog = new ModelReportViewModel(vm) { PaModes = false, Thermal = false, Dc = false };

        Assert.True(await dialog.GenerateAsync(), vm.StatusText);
        var config = JsonSerializer.Deserialize<DataDisplayConfig>(File.ReadAllText(vm.ReportDocument!), DataDisplayJson.Options)!;
        var withData = vm.Report!.Sections.Where(s => s.Data.Cubes.Count > 0).Select(s => s.Name).ToList();
        Assert.Equal(withData, config.Tabs.Select(t => t.Name));
        Assert.Contains("Charge", withData);
        Assert.All(config.Tabs, t => Assert.NotEmpty(t.Plots));

        string windowPdf = Path.Combine(dir, "window.pdf");
        Assert.True(await vm.ExportReportPdfAsync(windowPdf), vm.StatusText);

        var cli = ModelCliFixture.Run(dir, "model", "report", cmc, "-o", Path.Combine(dir, "cli-report"), "--pdf", Path.Combine(dir, "cli.pdf"));
        Assert.True(cli.ExitCode == 0, cli.StdErr);
        Assert.True(File.Exists(Path.Combine(dir, "cli-report.cdd")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(dir, "cli.pdf")), File.ReadAllBytes(windowPdf));
    }
}
