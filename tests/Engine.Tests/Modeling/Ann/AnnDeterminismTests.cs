using CircuitRF.Engine.Modeling.Ann;
using CircuitRF.Engine.Optimization;
using Xunit;

namespace CircuitRF.Engine.Tests.Modeling.Ann;

/// <summary>brief-modelcomposer-6 R-mc6-5, R-mc6-6: the same seed and data give the same weights bit
/// for bit, a different seed gives different ones, and the held-out points do not depend on the order
/// of the input array.</summary>
public sealed class AnnDeterminismTests
{
    private static AnnTrainingProblem Problem()
    {
        var xs = AnnTestData.Scatter(120, 41, (-1, 1), (0, 2));
        return new AnnTrainingProblem(2)
        {
            Values = new AnnValueData(xs, [.. xs.Select(p => Math.Tanh(2 * p[0]) * p[1])]),
            Derivatives = new AnnDerivativeData(xs, [1], [.. xs.Select(p => new[] { Math.Tanh(2 * p[0]) })]),
        };
    }

    private static double[] Flat(AnnTrainingResult r)
        => [.. r.Weights.SelectMany(w => w.Cast<double>()), .. r.Biases.SelectMany(b => b)];

    private static AnnTrainingSettings Settings(ulong seed, bool parallel = false)
        => new() { Hidden = [6], Restarts = 2, MaxIterations = 150, Seed = seed, Parallel = parallel };

    [Fact]
    public void TheSameSeed_GivesTheSameWeights_BitForBit_AndAnotherSeedDoesNot()
    {
        var a = Flat(AnnTrainer.Train(Problem(), Settings(7)));
        var b = Flat(AnnTrainer.Train(Problem(), Settings(7)));
        var c = Flat(AnnTrainer.Train(Problem(), Settings(8)));
        var parallel = Flat(AnnTrainer.Train(Problem(), Settings(7, parallel: true)));

        Assert.Equal(a.Select(BitConverter.DoubleToInt64Bits), b.Select(BitConverter.DoubleToInt64Bits));
        Assert.NotEqual(a, c);
        double scale = a.Max(Math.Abs);
        Assert.All(a.Zip(parallel), pair => Assert.True(Math.Abs(pair.First - pair.Second) <= 1e-12 * scale));
    }

    [Fact]
    public void TheHeldOutPoints_DoNotDependOnTheOrderOfTheArray()
    {
        var xs = AnnTestData.Scatter(250, 43, (-1, 1), (-1, 1), (-1, 1));
        double[][] targets = [.. xs.Select(p => new[] { p[0] * p[1] - p[2] })];
        var held = HeldOutSplit.Select(xs, targets, 0.1, 99);

        var order = Enumerable.Range(0, xs.Length).ToArray();
        new Random(5).Shuffle(order);
        var heldShuffled = HeldOutSplit.Select([.. order.Select(i => xs[i])], [.. order.Select(i => targets[i])], 0.1, 99);

        var first = Enumerable.Range(0, xs.Length).Where(i => held[i]).ToHashSet();
        var second = Enumerable.Range(0, xs.Length).Where(k => heldShuffled[k]).Select(k => order[k]).ToHashSet();
        Assert.Equal(25, first.Count);
        Assert.Equal(first, second);

        // Spread over the domain: every octant of the cube gives up some of its points.
        int Octant(double[] p) => (p[0] > 0 ? 1 : 0) + (p[1] > 0 ? 2 : 0) + (p[2] > 0 ? 4 : 0);
        Assert.Equal(8, first.Select(i => Octant(xs[i])).Distinct().Count());
    }
}
