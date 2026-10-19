using CircuitRF.Core.Devices.Composed;
using CircuitRF.Engine.Modeling.Ann;
using CircuitRF.Engine.Optimization;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Modeling.Ann;

/// <summary>brief-modelcomposer-6 R-mc6-3, R-mc6-3b: training to partial derivatives through the adjoint
/// network, its exact weight-gradient, the least-squares answer for non-conservative data, and combined
/// value-and-derivative training.</summary>
public sealed class AnnAdjointTrainingTests(ITestOutputHelper output)
{
    // A non-separable charge-like function and its gradient.
    private static double Q(double a, double b) => Math.Tanh(a + 0.5 * b) * (1.0 + 0.3 * b * b);
    private static double Sech2(double z) { double t = Math.Tanh(z); return 1.0 - t * t; }
    private static double Qa(double a, double b) => Sech2(a + 0.5 * b) * (1.0 + 0.3 * b * b);
    private static double Qb(double a, double b) => 0.5 * Sech2(a + 0.5 * b) * (1.0 + 0.3 * b * b) + Math.Tanh(a + 0.5 * b) * 0.6 * b;

    private static double[][] Grid(int n, double lo, double hi)
        => [.. Enumerable.Range(0, n * n).Select(k => new[] { lo + (hi - lo) * (k / n) / (n - 1), lo + (hi - lo) * (k % n) / (n - 1) })];

    [Fact]
    public void TrainedOnlyOnTheGradient_TheValueIsTheFunction_UpToTheAnchoredConstant()
    {
        var xs = AnnTestData.Scatter(300, 21, (-1.5, 1.5), (-1.5, 1.5));
        double[] anchor = [0.3, -0.2];
        var problem = new AnnTrainingProblem(2)
        {
            Derivatives = new AnnDerivativeData(xs, [0, 1], [.. xs.Select(p => new[] { Qa(p[0], p[1]), Qb(p[0], p[1]) })]),
            Anchor = new AnnAnchor(anchor, Q(anchor[0], anchor[1])),
        };
        var result = AnnTrainer.Train(problem, new AnnTrainingSettings { Restarts = 1 });

        var test = Grid(21, -1.5, 1.5);
        var g = new double[2];
        double rms = AnnTestData.Rms(test.Select(p => result.Network.Evaluate(p, g) - Q(p[0], p[1])));
        double rel = rms / AnnTestData.Range(test.Select(p => Q(p[0], p[1])));
        output.WriteLine($"value RMS {rms:E3} ({rel:P3} of range); derivative held-out {result.Derivatives!.HeldOutRelative:P3}; " +
                         $"{result.Iterations} iterations, {result.Stop}");
        // Stated: within 0.5 % of Q's range over the whole box, at no point trained on a value.
        Assert.True(rel < 5e-3, $"value RMS is {rel:P3} of the range");
    }

    [Fact]
    public void NonConservativeTargets_GiveTheLeastSquaresProjection()
    {
        // t = ∇Q + (0, c·a): the added field has curl c, so no function has t as its gradient. The
        // least-squares answer minimises ∫|∇φ − t|² over the square — computed here independently, on a
        // grid, by conjugate gradients on the finite-difference normal equations.
        const double c = 0.6;
        const int n = 41;
        double[] Target(double[] p) => [Qa(p[0], p[1]), Qb(p[0], p[1]) + c * p[0]];

        var xs = Grid(25, -1, 1);
        var problem = new AnnTrainingProblem(2)
        {
            Derivatives = new AnnDerivativeData(xs, [0, 1], [.. xs.Select(Target)]),
            Anchor = new AnnAnchor([0, 0], 0),
        };
        var result = AnnTrainer.Train(problem, new AnnTrainingSettings { Restarts = 1 });

        var phi = GridProjection(n, Target);
        var nodes = Grid(n, -1, 1);
        var g = new double[2];
        double[] net = [.. nodes.Select(p => result.Network.Evaluate(p, g))];
        double[] q = [.. nodes.Select(p => Q(p[0], p[1]))];
        Centre(net); Centre(q);

        double range = AnnTestData.Range(phi);
        double error = AnnTestData.Rms(net.Zip(phi, (a, b) => a - b)) / range;
        double fromQ = AnnTestData.Rms(q.Zip(phi, (a, b) => a - b)) / range;
        output.WriteLine($"network vs projection {error:P3}; Q vs projection {fromQ:P3}; {result.Iterations} iterations, {result.Stop}");
        // Stated: within 1 % of the projection's range, where Q itself is several times further off —
        // so the network found the compromise, not the conservative part it was built from.
        Assert.True(error < 1e-2, $"the network is {error:P3} of the range from the projection");
        Assert.True(fromQ > 5 * error, $"Q is only {fromQ:P3} from the projection");
    }

    [Fact]
    public void TheAdjointWeightGradient_EqualsAFiniteDifference()
    {
        var rng = new SplitMix64(5);
        double[] R() => [2 * rng.NextDouble() - 1, 2 * rng.NextDouble() - 1, 2 * rng.NextDouble() - 1];
        var points = new List<AnnTrainingPoint>();
        for (int k = 0; k < 5; k++) points.Add(AnnTrainingPoint.Value(R(), 0.3 + rng.NextDouble(), rng.NextDouble()));
        for (int k = 0; k < 5; k++)
            points.Add(new AnnTrainingPoint(R(), 0.0, 0.0, [0, 2], [0.7, 1.3], [1.1, 0.4], [rng.NextDouble(), -rng.NextDouble()]));
        points.Add(new AnnTrainingPoint(R(), 0.5, 0.2, [1], [0.9], [2.0], [0.3]));     // value and derivative together

        foreach (var activation in new[] { AnnActivation.Tanh, AnnActivation.Sigmoid })
        {
            var obj = new AnnObjective([3, 4, 3, 1], activation, [.. points], l2: 0.01, parallel: false);
            var p = obj.Initial(new SplitMix64(9));
            for (int i = 0; i < p.Length; i++) p[i] += 0.3 * (rng.NextDouble() - 0.5);    // non-zero biases too
            var grad = new double[p.Length];
            obj.Evaluate(p, grad);

            const double h = 1e-5;
            double worst = 0.0, scale = grad.Max(Math.Abs);
            for (int i = 0; i < p.Length; i++)
            {
                double keep = p[i];
                p[i] = keep + h; double up = obj.Evaluate(p, null);
                p[i] = keep - h; double down = obj.Evaluate(p, null);
                p[i] = keep;
                worst = Math.Max(worst, Math.Abs((up - down) / (2 * h) - grad[i]));
            }
            output.WriteLine($"{activation}: worst |adjoint − FD| / max|gradient| = {worst / scale:E2}");
            Assert.True(worst / scale < 1e-6, $"{activation}: {worst / scale:E2}");
        }
    }

    [Fact]
    public void CombinedTraining_RecoversValuesAndPartials_WhereValueOnlyTrainingDoesNot()
    {
        // f(v1, v2, s) with the state s tied to the bias at every value point (the DC manifold), while
        // the derivatives are ∂f/∂v at FIXED s — the small-signal reading. Values alone cannot tell a
        // change of v from a change of s along the manifold.
        static double S(double a, double b) => 0.5 * a - 0.4 * b * b + 0.2;
        static double F(double a, double b, double s) => Math.Tanh(2 * a - s) * (1 + 0.3 * b) + 0.5 * s * b;
        static double Fa(double a, double b, double s) => 2 * Sech2(2 * a - s) * (1 + 0.3 * b);
        static double Fb(double a, double b, double s) => 0.3 * Math.Tanh(2 * a - s) + 0.5 * s;

        var vb = AnnTestData.Scatter(300, 31, (-1, 1), (-1, 1));
        double[][] xs = [.. vb.Select(p => new[] { p[0], p[1], S(p[0], p[1]) })];
        var values = new AnnValueData(xs, [.. xs.Select(p => F(p[0], p[1], p[2]))]);
        var derivs = new AnnDerivativeData(xs, [0, 1], [.. xs.Select(p => new[] { Fa(p[0], p[1], p[2]), Fb(p[0], p[1], p[2]) })]);
        var settings = new AnnTrainingSettings { Restarts = 1 };

        var combined = AnnTrainer.Train(new AnnTrainingProblem(3) { Values = values, Derivatives = derivs }, settings);
        var valueOnly = AnnTrainer.Train(new AnnTrainingProblem(3) { Values = values }, settings);

        var test = AnnTestData.Scatter(400, 32, (-1, 1), (-1, 1)).Select(p => new[] { p[0], p[1], S(p[0], p[1]) }).ToArray();
        (double Value, double Partial) Errors(AnnTrainingResult r)
        {
            var g = new double[3];
            var ve = new List<double>(); var de = new List<double>();
            foreach (var p in test)
            {
                ve.Add(r.Network.Evaluate(p, g) - F(p[0], p[1], p[2]));
                de.Add(g[0] - Fa(p[0], p[1], p[2]));
                de.Add(g[1] - Fb(p[0], p[1], p[2]));
            }
            double dRange = AnnTestData.Range(test.SelectMany(p => new[] { Fa(p[0], p[1], p[2]), Fb(p[0], p[1], p[2]) }));
            return (AnnTestData.Rms(ve) / AnnTestData.Range(test.Select(p => F(p[0], p[1], p[2]))), AnnTestData.Rms(de) / dRange);
        }

        var c = Errors(combined);
        var v = Errors(valueOnly);
        output.WriteLine($"combined: value {c.Value:P3}, partials {c.Partial:P3}; value-only: value {v.Value:P3}, partials {v.Partial:P3}");
        // Stated: combined training recovers both within 1 % of their ranges; value-only training
        // fits the values as well, but its partials at fixed s are off by over 3 % of their range and
        // by more than ten times the combined error (measured: about 10 % against 0.03 %).
        Assert.True(c.Value < 1e-2, $"combined value error {c.Value:P3}");
        Assert.True(c.Partial < 1e-2, $"combined partial error {c.Partial:P3}");
        Assert.True(v.Value < 1e-2, $"value-only value error {v.Value:P3}");
        Assert.True(v.Partial > 0.03 && v.Partial > 10 * c.Partial, $"value-only partial error is only {v.Partial:P3}");
    }

    /// <summary>The least-squares φ on an n×n node grid over [−1, 1]²: each grid edge contributes
    /// (Δφ/h − t·edge at its midpoint)², solved by conjugate gradients with the mean held at zero.</summary>
    private static double[] GridProjection(int n, Func<double[], double[]> t)
    {
        double h = 2.0 / (n - 1);
        int N = n * n;
        var b = new double[N];
        void Edges(Action<int, int, double> edge)
        {
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double x = -1 + i * h, y = -1 + j * h;
                    if (i + 1 < n) edge(i * n + j, (i + 1) * n + j, t([x + h / 2, y])[0]);
                    if (j + 1 < n) edge(i * n + j, i * n + j + 1, t([x, y + h / 2])[1]);
                }
        }
        // Normal equations: Σ over edges (φ_q − φ_p − h·t)·(e_q − e_p) = 0 → L φ = b.
        Edges((p, q, te) => { b[q] += h * te; b[p] -= h * te; });
        var edges = new List<(int P, int Q)>();
        Edges((p, q, _) => edges.Add((p, q)));
        double[] Apply(double[] v)
        {
            var r = new double[N];
            foreach (var (p, q) in edges) { double d = v[q] - v[p]; r[q] += d; r[p] -= d; }
            return r;
        }

        var phi = new double[N];
        var res = (double[])b.Clone();
        var dir = (double[])res.Clone();
        double rr = res.Sum(x => x * x);
        for (int it = 0; it < 20 * N && rr > 1e-28; it++)
        {
            var Ad = Apply(dir);
            double alpha = rr / dir.Zip(Ad, (x, y) => x * y).Sum();
            for (int k = 0; k < N; k++) { phi[k] += alpha * dir[k]; res[k] -= alpha * Ad[k]; }
            double rrNew = res.Sum(x => x * x);
            for (int k = 0; k < N; k++) dir[k] = res[k] + rrNew / rr * dir[k];
            rr = rrNew;
        }
        Centre(phi);
        return phi;
    }

    private static void Centre(double[] v)
    {
        double mean = v.Average();
        for (int k = 0; k < v.Length; k++) v[k] -= mean;
    }
}
