using CircuitRF.Engine.Modeling.Ann;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Modeling.Ann;

/// <summary>brief-modelcomposer-6 R-mc6-2, R-mc6-5: value training fits a smooth two-input function on
/// scattered points, and the held-out error says how well.</summary>
public sealed class AnnValueTrainingTests(ITestOutputHelper output)
{
    [Fact]
    public void OneHiddenLayerOfTen_FitsTanhTimesYSquared_OnTwoHundredScatteredPoints()
    {
        static double F(double x, double y) => Math.Tanh(x) * y * y;
        var xs = AnnTestData.Scatter(200, 11, (-2, 2), (-1, 1));
        var problem = new AnnTrainingProblem(2)
        {
            Values = new AnnValueData(xs, [.. xs.Select(p => F(p[0], p[1]))]),
        };
        var result = AnnTrainer.Train(problem, new AnnTrainingSettings { Hidden = [10], Restarts = 1 });

        var v = result.Values!;
        output.WriteLine($"train {v.TrainRms:E3} ({v.TrainRelative:P3}), held-out {v.HeldOutRms:E3} ({v.HeldOutRelative:P3}), " +
                         $"{result.Iterations} iterations, {result.Stop}");
        Assert.Equal(20, v.HeldOutCount);
        Assert.Equal(180, v.TrainCount);
        // Stated: held-out RMS below 0.5 % of the target's range (the range is about 1.93).
        Assert.True(v.HeldOutRelative < 5e-3, $"held-out RMS is {v.HeldOutRelative:P3} of the range");
        // The report is the runtime network's own error.
        Assert.Equal(F(0.7, -0.4), result.Network.Evaluate([0.7, -0.4], new double[2]), 2e-2);
    }
}
