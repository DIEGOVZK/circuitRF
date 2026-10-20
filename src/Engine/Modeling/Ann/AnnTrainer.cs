using CircuitRF.Core.Devices.Composed;
using CircuitRF.Engine.Optimization;
using RfCore.Data;

namespace CircuitRF.Engine.Modeling.Ann;

/// <summary>Values to train to (R-mc6-2): a point, its target, and an optional weight per point (default 1).</summary>
public sealed record AnnValueData(IReadOnlyList<double[]> X, IReadOnlyList<double> Y, IReadOnlyList<double>? Weights = null);

/// <summary>
/// Partial derivatives to train to (R-mc6-3): at each point, the targets of <c>∂f/∂x_j</c> for every
/// <c>j</c> in <see cref="Directions"/> (input indices, in that order), and an optional weight per
/// point and direction (default 1; a weight of 0 marks a component that was not measured).
/// </summary>
public sealed record AnnDerivativeData(IReadOnlyList<double[]> X, IReadOnlyList<int> Directions,
                                       IReadOnlyList<double[]> Targets, IReadOnlyList<double[]>? Weights = null);

/// <summary>The value that fixes a derivative-trained function's arbitrary constant — a charge's zero (R-mc6-3).</summary>
public sealed record AnnAnchor(double[] X, double Y, double Weight = 1.0);

/// <summary>
/// What one network is trained to: values, derivatives, or both (R-mc6-3b), each over its own points.
///
/// <para><b>The loss.</b> Each term is a weighted MEAN in the network's normalised units — the value
/// term divides its residuals by the output scale, the derivative term by the RMS of its targets — so
/// <see cref="ValueWeight"/> and <see cref="DerivativeWeight"/> are dimensionless and mean the same
/// whatever the data's units or point counts. A constant factor per term moves no minimiser, so within
/// a term this is exactly <c>Σ w (f − y)²</c> and <c>Σ w (∂f/∂x_j − t)²</c>, and non-conservative
/// derivative data gets the least-squares compromise of [P1] §6.3.6 ¶2. The anchor adds
/// <c>Weight·((f(x₀) − y₀)/outputScale)²</c>.</para>
/// </summary>
public sealed record AnnTrainingProblem(int InputCount)
{
    public AnnValueData? Values { get; init; }
    public double ValueWeight { get; init; } = 1.0;
    public AnnDerivativeData? Derivatives { get; init; }
    public double DerivativeWeight { get; init; } = 1.0;
    public AnnAnchor? Anchor { get; init; }
}

public sealed record AnnTrainingSettings
{
    /// <summary>Hidden-layer widths. Default two layers of eight (R-mc6-1).</summary>
    public IReadOnlyList<int> Hidden { get; init; } = [8, 8];
    public AnnActivation Activation { get; init; } = AnnActivation.Tanh;
    /// <summary>The weight penalty <c>λ·ΣW²</c> on the normalised network's weights (not its biases); 0 for none.</summary>
    public double L2 { get; init; }
    public int MaxIterations { get; init; } = 2000;
    /// <summary>Stop when the loss gradient's largest component falls to this.</summary>
    public double GradientTolerance { get; init; } = 1e-9;
    /// <summary>Stop when one iteration changes the loss by less than this, relative to the loss.</summary>
    public double LossChangeTolerance { get; init; } = 1e-14;
    /// <summary>L-BFGS's memory, in step pairs.</summary>
    public int Memory { get; init; } = 10;
    /// <summary>How many times to train from a fresh seeded start; the start with the lowest held-out loss is kept.</summary>
    public int Restarts { get; init; } = 3;
    public ulong Seed { get; init; } = 1;
    /// <summary>The fraction of each point set held out of training (R-mc6-5).</summary>
    public double HeldOutFraction { get; init; } = 0.1;
    /// <summary>Keep the iterate with the lowest held-out loss, and stop after
    /// <see cref="EarlyStoppingPatience"/> iterations without a new lowest.</summary>
    public bool EarlyStopping { get; init; }
    public int EarlyStoppingPatience { get; init; } = 100;
    /// <summary>
    /// Opt-in. Evaluates the loss over blocks of points on several threads. Promised only to match the
    /// single-threaded weights to within 1e-12 relative; off, training is bit-identical on every
    /// platform for the same seed and data.
    /// </summary>
    public bool Parallel { get; init; }
}

/// <summary>A fit's RMS error over the training and the held-out points: absolute, in the target's
/// units, and relative to the range of the targets (R-mc6-5).</summary>
public sealed record AnnFitError(int TrainCount, double TrainRms, double TrainRelative,
                                 int HeldOutCount, double HeldOutRms, double HeldOutRelative);

/// <summary>A trained network: MC-2's runtime object, its arrays, and the report.</summary>
public sealed class AnnTrainingResult
{
    public required AnnEvaluator Network { get; init; }
    public required double[] InputOffset { get; init; }
    public required double[] InputScale { get; init; }
    public required double[][,] Weights { get; init; }
    public required double[][] Biases { get; init; }
    public required double OutputOffset { get; init; }
    public required double OutputScale { get; init; }

    public AnnFitError? Values { get; init; }
    public AnnFitError? Derivatives { get; init; }
    /// <summary>Which start (from 0) was kept, and how its minimiser ended.</summary>
    public int BestStart { get; init; }
    public int Iterations { get; init; }
    public LbfgsStop Stop { get; init; }
    public int ParameterCount { get; init; }
    /// <summary>One per value, one per measured derivative component (weight not 0), the anchor included.</summary>
    public int TargetCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The <c>.cmodel</c> arrays of an ANN constituent named <paramref name="name"/>.</summary>
    public IEnumerable<KeyValuePair<string, DataCube>> Arrays(string name)
        => ComposedArrays.Ann(name, InputOffset, InputScale, Weights, Biases, OutputOffset, OutputScale);
}

/// <summary>
/// The in-house MLP trainer (MC-6): value training, adjoint (derivative) training, and both at once,
/// by L-BFGS on the exact weight-gradient of <see cref="AnnObjective"/>. Produces MC-2's
/// <see cref="AnnEvaluator"/> directly, so a trained network loads with no conversion.
///
/// <para><b>Normalisation.</b> Each input maps its range over every point onto [−1, 1]. The output maps
/// the value targets' range onto [−1, 1]; with no values, it is scaled so the derivative targets are of
/// order one in normalised units and offset to the anchor's value.</para>
///
/// <para><b>Cancelled, it returns nothing</b>: <see cref="OperationCanceledException"/> propagates and
/// the caller's previous network is untouched, since nothing here holds or changes one (D13).</para>
/// </summary>
public static class AnnTrainer
{
    public static AnnTrainingResult Train(AnnTrainingProblem problem, AnnTrainingSettings settings, RunControl? rc = null)
    {
        rc?.ThrowIfCancellationRequested();
        Validate(problem, settings);
        int d = problem.InputCount;
        var values = problem.Values;
        var derivs = problem.Derivatives;

        // Input normalisation over every point.
        var all = new List<double[]>();
        if (values is not null) all.AddRange(values.X);
        if (derivs is not null) all.AddRange(derivs.X);
        if (problem.Anchor is not null) all.Add(problem.Anchor.X);
        var inOffset = new double[d];
        var inScale = new double[d];
        for (int i = 0; i < d; i++)
        {
            double lo = all.Min(x => x[i]), hi = all.Max(x => x[i]);
            inOffset[i] = 0.5 * (lo + hi);
            inScale[i] = hi > lo ? 2.0 / (hi - lo) : 1.0;
        }

        // Output normalisation.
        double outOffset, outScale;
        double derivNorm = 1.0;
        if (derivs is not null)
        {
            // Over the measured components only: a weight of 0 marks a placeholder, and its target says nothing.
            double ss = 0.0; int count = 0;
            for (int k = 0; k < derivs.X.Count; k++)
                for (int j = 0; j < derivs.Directions.Count; j++)
                    if (Measured(derivs, k, j)) { ss += derivs.Targets[k][j] * derivs.Targets[k][j]; count++; }
            derivNorm = Math.Sqrt(ss / Math.Max(count, 1));
        }
        // Values that are all one number say nothing about the output's size; with derivatives present (a charge
        // constrained to zero along a line — MC-8's drift charge) the derivatives set the scale.
        if (values is not null && (derivs is null || values.Y.Max() > values.Y.Min()))
        {
            double lo = values.Y.Min(), hi = values.Y.Max();
            outOffset = 0.5 * (lo + hi);
            outScale = hi > lo ? 0.5 * (hi - lo) : Math.Max(Math.Abs(outOffset), 1.0);
        }
        else
        {
            outOffset = values is not null ? values.Y[0] : problem.Anchor?.Y ?? 0.0;
            outScale = 0.0;
            for (int j = 0; j < derivs!.Directions.Count; j++)
            {
                int dir = derivs.Directions[j];
                double ss = 0.0; int count = 0;
                for (int k = 0; k < derivs.X.Count; k++)
                    if (Measured(derivs, k, j)) { ss += derivs.Targets[k][j] * derivs.Targets[k][j]; count++; }
                outScale = Math.Max(outScale, Math.Sqrt(ss / Math.Max(count, 1)) / inScale[dir]);
            }
            if (!(outScale > 0)) outScale = 1.0;
        }
        if (!(derivNorm > 0)) derivNorm = outScale * inScale.Max();

        double[] Hat(double[] x)
        {
            var h = new double[d];
            for (int i = 0; i < d; i++) h[i] = (x[i] - inOffset[i]) * inScale[i];
            return h;
        }

        // Held-out splits, one per point set.
        ulong splitSeed = SplitMix64.Mix(settings.Seed);
        bool[] valueHeld = values is null ? [] : HeldOutSplit.Select(
            [.. values.X.Select(Hat)], [.. values.Y.Select(y => new[] { y })], settings.HeldOutFraction, splitSeed);
        bool[] derivHeld = derivs is null ? [] : HeldOutSplit.Select(
            [.. derivs.X.Select(Hat)], derivs.Targets, settings.HeldOutFraction, splitSeed + 1);

        AnnTrainingPoint[] Points(bool heldOut)
        {
            var pts = new List<AnnTrainingPoint>();
            if (values is not null)
            {
                double sum = 0.0;
                for (int k = 0; k < values.X.Count; k++) if (valueHeld[k] == heldOut) sum += values.Weights?[k] ?? 1.0;
                for (int k = 0; k < values.X.Count; k++)
                {
                    if (valueHeld[k] != heldOut) continue;
                    double coef = sum > 0 ? problem.ValueWeight * (values.Weights?[k] ?? 1.0) / sum : 0.0;
                    pts.Add(AnnTrainingPoint.Value(Hat(values.X[k]), coef, (values.Y[k] - outOffset) / outScale));
                }
            }
            if (derivs is not null)
            {
                int m = derivs.Directions.Count;
                double sum = 0.0;
                for (int k = 0; k < derivs.X.Count; k++)
                    if (derivHeld[k] == heldOut)
                        for (int j = 0; j < m; j++) sum += derivs.Weights?[k][j] ?? 1.0;
                int[] dirs = [.. derivs.Directions];
                double[] alpha = [.. dirs.Select(dir => outScale * inScale[dir] / derivNorm)];
                for (int k = 0; k < derivs.X.Count; k++)
                {
                    if (derivHeld[k] != heldOut) continue;
                    var coef = new double[m];
                    var target = new double[m];
                    for (int j = 0; j < m; j++)
                    {
                        coef[j] = sum > 0 ? problem.DerivativeWeight * (derivs.Weights?[k][j] ?? 1.0) / sum : 0.0;
                        target[j] = derivs.Targets[k][j] / derivNorm;
                    }
                    pts.Add(new AnnTrainingPoint(Hat(derivs.X[k]), 0.0, 0.0, dirs, coef, alpha, target));
                }
            }
            if (!heldOut && problem.Anchor is { } anchor)
                pts.Add(AnnTrainingPoint.Value(Hat(anchor.X), anchor.Weight, (anchor.Y - outOffset) / outScale));
            return [.. pts];
        }

        int[] widths = [d, .. settings.Hidden, 1];
        var trainPoints = Points(heldOut: false);
        var heldPoints = Points(heldOut: true);
        var objective = new AnnObjective(widths, settings.Activation, trainPoints, settings.L2, settings.Parallel);
        var heldObjective = heldPoints.Length > 0
            ? new AnnObjective(widths, settings.Activation, heldPoints, 0.0, settings.Parallel)
            : null;

        var lbfgs = new Lbfgs.Settings(settings.Memory, settings.MaxIterations,
                                       settings.GradientTolerance, settings.LossChangeTolerance);
        var seeds = new SplitMix64(settings.Seed);
        double[]? best = null;
        double bestScore = double.PositiveInfinity;
        int bestStart = 0;
        Lbfgs.Outcome? bestOutcome = null;
        int starts = Math.Max(1, settings.Restarts);

        for (int start = 0; start < starts; start++)
        {
            var p = objective.Initial(new SplitMix64(seeds.NextULong()));
            rc?.BeginStage(starts == 1 ? "Training the network" : $"Training the network, start {start + 1} of {starts}",
                           settings.MaxIterations, "iteration(s)");

            double[]? kept = null;
            double keptHeld = double.PositiveInfinity;
            int sinceBest = 0;
            bool OnIteration(int iter, double[] current, double loss)
            {
                rc?.TickStage();
                if (!settings.EarlyStopping || heldObjective is null) return true;
                double h = heldObjective.Evaluate(current, null);
                if (h < keptHeld) { keptHeld = h; kept = (double[])current.Clone(); sinceBest = 0; return true; }
                return ++sinceBest < settings.EarlyStoppingPatience;
            }

            var outcome = Lbfgs.Minimize((x, g) => objective.Evaluate(x, g), p, lbfgs, OnIteration);
            if (kept is not null && keptHeld < (heldObjective?.Evaluate(p, null) ?? double.PositiveInfinity)) p = kept;

            double score = heldObjective?.Evaluate(p, null) ?? objective.Evaluate(p, null);
            if (score < bestScore || best is null)
            {
                bestScore = score; best = p; bestStart = start; bestOutcome = outcome;
            }
        }

        var (weights, biases) = objective.Unpack(best!);
        var network = objective.ToEvaluator(best!, inOffset, inScale, outOffset, outScale);

        int targets = (values?.X.Count ?? 0) + (problem.Anchor is null ? 0 : 1);
        if (derivs is not null)
            for (int k = 0; k < derivs.X.Count; k++)
                for (int j = 0; j < derivs.Directions.Count; j++)
                    if (Measured(derivs, k, j)) targets++;
        var warnings = new List<string>();
        if (objective.ParameterCount * 10 > targets)
            warnings.Add($"the network has {objective.ParameterCount} weights for {targets} targets; " +
                         $"one weight per ten targets suggests at most {targets / 10}.");

        return new AnnTrainingResult
        {
            Network = network,
            InputOffset = inOffset, InputScale = inScale,
            Weights = weights, Biases = biases,
            OutputOffset = outOffset, OutputScale = outScale,
            Values = values is null ? null : ValueError(network, values, valueHeld),
            Derivatives = derivs is null ? null : DerivativeError(network, derivs, derivHeld),
            BestStart = bestStart,
            Iterations = bestOutcome!.Iterations,
            Stop = bestOutcome.Stop,
            ParameterCount = objective.ParameterCount,
            TargetCount = targets,
            Warnings = warnings,
        };
    }

    private static AnnFitError ValueError(AnnEvaluator net, AnnValueData data, bool[] held)
    {
        var grad = new double[net.InputCount];
        var train = new List<double>(); var hold = new List<double>();
        for (int k = 0; k < data.X.Count; k++)
            (held[k] ? hold : train).Add(net.Evaluate(data.X[k], grad) - data.Y[k]);
        return Error(train, hold, data.Y.Max() - data.Y.Min());
    }

    private static AnnFitError DerivativeError(AnnEvaluator net, AnnDerivativeData data, bool[] held)
    {
        var grad = new double[net.InputCount];
        var train = new List<double>(); var hold = new List<double>();
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int k = 0; k < data.X.Count; k++)
        {
            net.Evaluate(data.X[k], grad);
            for (int j = 0; j < data.Directions.Count; j++)
            {
                if (!Measured(data, k, j)) continue;
                double t = data.Targets[k][j];
                lo = Math.Min(lo, t); hi = Math.Max(hi, t);
                (held[k] ? hold : train).Add(grad[data.Directions[j]] - t);
            }
        }
        return Error(train, hold, hi - lo);
    }

    private static bool Measured(AnnDerivativeData data, int k, int j) => (data.Weights?[k][j] ?? 1.0) != 0.0;

    private static AnnFitError Error(List<double> train, List<double> hold, double range)
    {
        static double Rms(List<double> r) => r.Count == 0 ? 0.0 : Math.Sqrt(r.Sum(e => e * e) / r.Count);
        double scale = range > 0 ? range : 1.0;
        double t = Rms(train), h = Rms(hold);
        return new(train.Count, t, t / scale, hold.Count, h, h / scale);
    }

    private static void Validate(AnnTrainingProblem problem, AnnTrainingSettings settings)
    {
        int d = problem.InputCount;
        if (d is < 1 or > RuntimeEvaluator.MaxInputs)
            throw new ArgumentException($"a network has 1 to {RuntimeEvaluator.MaxInputs} inputs.");
        if (problem.Values is null && problem.Derivatives is null)
            throw new ArgumentException("there is nothing to train to: no values and no derivatives.");
        if (settings.Hidden.Count is < 1 or > AnnEvaluator.MaxHiddenLayers
            || settings.Hidden.Any(w => w is < 1 or > AnnEvaluator.MaxNeurons))
            throw new ArgumentException(
                $"a network has 1 to {AnnEvaluator.MaxHiddenLayers} hidden layers of 1 to {AnnEvaluator.MaxNeurons} neurons.");
        if (!(settings.HeldOutFraction is >= 0 and < 1))
            throw new ArgumentException("the held-out fraction is at least 0 and below 1.");
        if (!(settings.L2 >= 0) || !(problem.ValueWeight >= 0) || !(problem.DerivativeWeight >= 0))
            throw new ArgumentException("the weight penalty and the term weights are numbers ≥ 0.");

        void Points(IReadOnlyList<double[]> xs, string what)
        {
            if (xs.Count == 0) throw new ArgumentException($"the {what} have no points.");
            foreach (var x in xs)
                if (x.Length != d || x.Any(v => !double.IsFinite(v)))
                    throw new ArgumentException($"a point of the {what} is not {d} finite numbers.");
        }

        if (problem.Values is { } values)
        {
            Points(values.X, "values");
            if (values.Y.Count != values.X.Count || values.Y.Any(v => !double.IsFinite(v)))
                throw new ArgumentException("the values do not give one finite target per point.");
            if (values.Weights is { } w && (w.Count != values.X.Count || w.Any(v => !(v >= 0) || double.IsInfinity(v))))
                throw new ArgumentException("the values' weights are not one number ≥ 0 per point.");
        }
        if (problem.Derivatives is { } derivs)
        {
            Points(derivs.X, "derivatives");
            int m = derivs.Directions.Count;
            if (m == 0 || derivs.Directions.Any(j => j < 0 || j >= d) || derivs.Directions.Distinct().Count() != m)
                throw new ArgumentException($"the derivative directions are distinct input indices, 0 to {d - 1}.");
            if (derivs.Targets.Count != derivs.X.Count || derivs.Targets.Any(t => t.Length != m || t.Any(v => !double.IsFinite(v))))
                throw new ArgumentException($"the derivatives do not give {m} finite targets per point.");
            if (derivs.Weights is { } w
                && (w.Count != derivs.X.Count || w.Any(r => r.Length != m || r.Any(v => !(v >= 0) || double.IsInfinity(v)))))
                throw new ArgumentException($"the derivatives' weights are not {m} numbers ≥ 0 per point.");
        }
        if (problem.Anchor is { } anchor
            && (anchor.X.Length != d || anchor.X.Any(v => !double.IsFinite(v)) || !double.IsFinite(anchor.Y) || !(anchor.Weight >= 0)))
            throw new ArgumentException($"the anchor is not {d} finite coordinates, a finite value and a weight ≥ 0.");
    }
}
