using CircuitRF.Core.Devices.Composed;

namespace CircuitRF.Core.Tests.Composed;

/// <summary>brief-modelcomposer-2 R-mc2-2: the MLP's input gradient — the derivative network of [P1]
/// §6.2.7 — is the exact derivative of its value, for both activations; and so is the Hessian along a
/// direction that guided extrapolation reads.</summary>
public sealed class AnnEvaluatorTests
{
    [Theory]
    [InlineData(AnnActivation.Tanh)]
    [InlineData(AnnActivation.Sigmoid)]
    public void ValueGradientAndHessian_MatchFiniteDifferences(AnnActivation activation)
    {
        // Three inputs, two hidden layers (4 and 3 neurons), hand-set weights of both signs.
        var net = new AnnEvaluator(
            inputOffset: [-0.5, 4.0, 25.0], inputScale: [0.9, 0.3, 0.02],
            weights:
            [
                [0.8, -0.4, 0.3, 1.1, 0.5, -0.7, -0.6, 0.2, 0.9, 0.3, -1.2, 0.4],
                [0.7, -0.5, 0.2, 0.9, -0.3, 0.6, 1.0, 0.1, -0.8, 0.5, 0.4, -0.2],
                [1.3, -0.9, 0.6],
            ],
            biases: [[0.1, -0.3, 0.2, 0.05], [-0.1, 0.2, 0.3], [0.4]],
            outputOffset: 0.01, outputScale: 0.05, activation);

        double[] w = [0.3, -1.1, 4.0];
        foreach (double[] x in (double[][])[[-0.7, 3.0, 40.0], [0.4, 9.5, -10.0], [-2.0, 0.2, 85.0]])
        {
            var g = new double[3];
            var hw = new double[3];
            double f = net.Evaluate(x, g, w, hw);
            Assert.Equal(f, net.Evaluate(x, new double[3]));

            for (int k = 0; k < 3; k++)
            {
                double h = 1e-5 * Math.Max(1.0, Math.Abs(x[k]));
                var xp = (double[])x.Clone(); xp[k] += h;
                var xm = (double[])x.Clone(); xm[k] -= h;
                var gp = new double[3]; var gm = new double[3];
                double fd = (net.Evaluate(xp, gp) - net.Evaluate(xm, gm)) / (2 * h);
                Assert.True(Math.Abs(fd - g[k]) <= 1e-8 * Math.Max(Math.Abs(g[k]), 1e-3 * Math.Abs(f)),
                    $"∂f/∂x{k} at [{string.Join(", ", x)}]: {g[k]:R} vs finite difference {fd:R}");
            }

            // H·w is the derivative of the gradient along w.
            double t = 1e-5;
            var x1 = new double[3]; var x2 = new double[3];
            for (int k = 0; k < 3; k++) { x1[k] = x[k] + t * w[k]; x2[k] = x[k] - t * w[k]; }
            var g1 = new double[3]; var g2 = new double[3];
            net.Evaluate(x1, g1); net.Evaluate(x2, g2);
            for (int k = 0; k < 3; k++)
            {
                double fd = (g1[k] - g2[k]) / (2 * t);
                Assert.True(Math.Abs(fd - hw[k]) <= 1e-6 * Math.Max(Math.Abs(hw[k]), 1e-3),
                    $"(H·w){k}: {hw[k]:R} vs finite difference {fd:R}");
            }
        }
    }
}
