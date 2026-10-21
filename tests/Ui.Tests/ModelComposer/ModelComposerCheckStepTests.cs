using System.Collections.Concurrent;
using CircuitRF.Design.Modeling;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-15 R-mc15-6: Check Charge Conservation runs MC-8's function off the UI thread, and its result line is
/// that function's own for the same document.
/// </summary>
public sealed class ModelComposerCheckStepTests
{
    [Fact]
    public async Task TheCheck_RunsOffTheUiThread_AndItsLineIsMc8s()
    {
        string cmc = ExportFixture.Document();
        var vm = await ComposerFixture.Open(cmc);
        int ui = Environment.CurrentManagedThreadId;
        var ran = new ConcurrentBag<int>();
        vm.RunOffThread = (work, token) => Task.Run(() => { ran.Add(Environment.CurrentManagedThreadId); return work(); }, token);

        Assert.True(await vm.CheckChargeAsync(), vm.StatusText);

        Assert.NotEmpty(ran);
        Assert.DoesNotContain(ui, ran);
        var expected = ConservationCheck.SummaryLines(ConservationCheck.RunFile(cmc, new LargeSignalSettings()));
        Assert.Equal(expected, vm.CheckLines.Take(expected.Count));
        Assert.StartsWith("Data gate ", expected[0]);
        Assert.StartsWith("Data drain ", expected[1]);
        Assert.StartsWith("HB ", expected[^1]);
        Assert.NotNull(vm.CheckPlots!.Data["Spectrum.f"]);
    }
}
