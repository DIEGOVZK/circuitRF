using System.Collections.Concurrent;
using CircuitRF.Design.Modeling;
using CircuitRF.Ui.ModelComposer;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-14 R-mc14-3 (overview D13): a fit runs off the UI thread and its progress is coalesced — a counter of
/// UI-thread work items, never a timing; Cancel leaves the previous network values; a second Run while one runs is refused.
/// </summary>
public sealed class ModelComposerBackgroundTaskTests
{
    [Fact]
    public async Task AFit_RunsOffTheUiThread_WithAtMostOneUiWorkItemPerProgressPost()
    {
        string cmc = ComposerFixture.ActiveWithS("rg", "Lg");
        var vm = await ComposerFixture.Open(cmc);
        double before = Value(vm, "rg");

        // A UI thread that is busy for the whole run: nothing posted is drained until the run is over.
        int ui = Environment.CurrentManagedThreadId;
        var queued = new ConcurrentQueue<Action>();
        var postedFrom = new ConcurrentBag<int>();
        vm.PostToUi = a => { postedFrom.Add(Environment.CurrentManagedThreadId); queued.Enqueue(a); };
        long reports0 = vm.ProgressReports, items0 = vm.UiWorkItems;

        Assert.True(await vm.FitAsync("network"), vm.StatusText);

        long reports = vm.ProgressReports - reports0, items = vm.UiWorkItems - items0;
        Assert.True(reports > 1, $"{reports} progress reports");
        Assert.True(items <= 1, $"{items} UI work items for {reports} reports, none drained");
        Assert.All(postedFrom, t => Assert.NotEqual(ui, t));
        while (queued.TryDequeue(out var a)) a();
        Assert.NotEqual(before, Value(vm, "rg"));
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Cancel_LeavesThePreviousNetworkValues()
    {
        string cmc = ComposerFixture.ActiveWithS("rg", "Lg");
        var vm = await ComposerFixture.Open(cmc);
        double before = Value(vm, "rg");
        byte[] bytes = File.ReadAllBytes(cmc);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModelComposerViewModel.TaskStage) && vm.TaskStage.StartsWith("Start", StringComparison.Ordinal))
                vm.CancelCommand.Execute(null);
        };

        Assert.False(await vm.FitAsync("network"));
        Assert.Contains("cancelled", vm.StatusText);
        Assert.Equal(before, Value(vm, "rg"));
        Assert.Equal(bytes, File.ReadAllBytes(cmc));
    }

    [Fact]
    public async Task ASecondRun_WhileOneRuns_IsRefused()
    {
        string cmc = ComposerFixture.ActiveWithS("rg", "Lg");
        var vm = await ComposerFixture.Open(cmc);
        var started = new TaskCompletionSource();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModelComposerViewModel.TaskStage) && vm.TaskStage.StartsWith("Start", StringComparison.Ordinal))
                started.TrySetResult();
        };

        var first = vm.FitAsync("network");
        await started.Task.WaitAsync(TimeSpan.FromMinutes(1));
        Assert.False(await vm.FitAsync("network"));
        Assert.Contains("is running", vm.StatusText);

        vm.CancelCommand.Execute(null);
        await first;
    }

    private static double Value(ModelComposerViewModel vm, string name) =>
        CmcExtrinsic.Network(vm.Document).Variables[name].Value!.Value;
}
