using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-5 R-mc5-3 and D13: a fit cancelled part-way through a start leaves the previous values — in the
/// document on disk, byte for byte.
/// </summary>
public sealed class NetworkFitCancelTests
{
    private sealed class CancelAfter(CancellationTokenSource cts, int evaluations) : IProgress<RunProgress>
    {
        public RunProgress? At { get; private set; }

        public void Report(RunProgress p)
        {
            if (At is null && p.Stage.StartsWith("Start 1 of") && p.StageCompleted >= evaluations)
            {
                At = p;
                cts.Cancel();
            }
        }
    }

    [Fact]
    public void FitCancelledMidStart_LeavesThePreviousValues()
    {
        double[] freqs = [.. Enumerable.Range(1, 20).Select(i => i * 2e9)];
        var data = new Dictionary<string, NormalisedData>
        {
            ["bias"] = SyntheticFetData.Biased(ComposedTestModels.GanNetwork, "gan", freqs, 0, SyntheticFetData.ActivePoints),
        };
        var (cmc, doc) = SyntheticFetData.Document("gan-two-shell", new NetworkFitSettings { Starts = 2, Budget = 400 },
                                                   new Dictionary<string, double> { ["rg"] = 7, ["Lg"] = 50e-12 });
        CmcFile.Write(cmc, doc);
        byte[] before = File.ReadAllBytes(cmc);

        using var cts = new CancellationTokenSource();
        var progress = new CancelAfter(cts, 20);
        var rc = new RunControl { Token = cts.Token, Progress = progress };
        Assert.ThrowsAny<OperationCanceledException>(() => NetworkFit.FitFile(cmc, data, rc));

        Assert.NotNull(progress.At);
        Assert.InRange(progress.At!.StageCompleted, 1, progress.At.StageTotal - 1);
        Assert.Equal(before, File.ReadAllBytes(cmc));
        Assert.Equal((double?)7, CmcExtrinsic.Network(CmcFile.Read(cmc)).Variables["rg"].Value);
    }
}
