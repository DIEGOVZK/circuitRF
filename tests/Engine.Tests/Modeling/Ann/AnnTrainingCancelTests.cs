using CircuitRF.Engine.Modeling.Ann;
using Xunit;

namespace CircuitRF.Engine.Tests.Modeling.Ann;

/// <summary>brief-modelcomposer-6 R-mc6-7: training reports progress per iteration and per start, and a
/// cancelled training returns no network — the caller's previous one is untouched.</summary>
public sealed class AnnTrainingCancelTests
{
    private sealed class Sink(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }

    [Fact]
    public void Cancelled_ReturnsNoNetwork_AndTheCallersNetworkIsUnchanged()
    {
        var xs = AnnTestData.Scatter(100, 51, (-1, 1));
        var problem = new AnnTrainingProblem(1) { Values = new AnnValueData(xs, [.. xs.Select(p => Math.Sin(3 * p[0]))]) };
        var settings = new AnnTrainingSettings { Hidden = [5], Restarts = 2, MaxIterations = 400, GradientTolerance = 0, LossChangeTolerance = 0 };

        var network = AnnTrainer.Train(problem, settings with { MaxIterations = 50 });
        double[] before = [.. network.Weights.SelectMany(w => w.Cast<double>()), .. network.Biases.SelectMany(b => b)];
        double valueBefore = network.Network.Evaluate([0.3], new double[1]);

        using var cts = new CancellationTokenSource();
        var stages = new HashSet<string>();
        long ticks = 0;
        var rc = new RunControl
        {
            Token = cts.Token,
            MinReportIntervalMs = 0,
            Progress = new Sink(p =>
            {
                stages.Add(p.Stage);
                ticks = Math.Max(ticks, p.StageCompleted);
                if (p.Stage.Contains("start 2") && p.StageCompleted >= 10) cts.Cancel();
            }),
        };

        AnnTrainingResult? replaced = null;
        // The caller's stage names the network; each start keeps that name.
        rc.BeginStage("Training Ig");
        Assert.ThrowsAny<OperationCanceledException>(() => replaced = AnnTrainer.Train(problem, settings, rc));
        Assert.Null(replaced);
        Assert.Contains("Training Ig, start 1 of 2", stages);
        Assert.Contains("Training Ig, start 2 of 2", stages);
        Assert.True(ticks >= 10);

        double[] after = [.. network.Weights.SelectMany(w => w.Cast<double>()), .. network.Biases.SelectMany(b => b)];
        Assert.Equal(before, after);
        Assert.Equal(valueBefore, network.Network.Evaluate([0.3], new double[1]));
    }
}
