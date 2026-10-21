using CircuitRF.Design.Modeling;
using CircuitRF.Ui.ModelComposer;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// Model Composer review 3: the report dialog offers the measured values rather than asking for text — the S biases as a
/// matrix of the bias plane, each box naming its bias — and a ticked box is the option the report reads. A bias stored
/// earlier that the data does not have is kept, ticked, rather than dropped on open.
/// </summary>
public sealed class ModelReportChoicesTests
{
    [Fact]
    public async Task ATickedBox_IsTheOption_AndAStoredBiasOffTheMatrixIsKept()
    {
        var vm = await ComposerFixture.Open(ModelComposerCurrentStepTests.Document());
        var dialog = new ModelReportViewModel(vm);
        Assert.True(dialog.SBiasAsMatrix);
        Assert.StartsWith("None chosen", dialog.SBiasesSummary);

        var cell = dialog.SBiasRows.SelectMany(r => r.Cells).First(c => c.IsPresent);
        var pair = cell.Text.Split(',').Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal($"Vgs {cell.Text.Split(", ")[0]} V, Vds {cell.Text.Split(", ")[1]} V", cell.Tip);
        cell.IsChecked = true;

        var options = dialog.Options()!;
        Assert.Equal([(pair[0], pair[1])], options.SBiases);
        Assert.Equal("1 chosen", dialog.SBiasesSummary);

        Assert.True(vm.SetReportOptions(options with { SBiases = [.. options.SBiases, (-9.25, 99)] }));
        var reopened = new ModelReportViewModel(vm);
        Assert.True(Assert.Single(reopened.SBiasOthers).IsChecked);
        Assert.Equal(2, reopened.Options()!.SBiases.Count);
    }
}
