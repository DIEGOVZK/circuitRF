// The least-squares machinery the network fit (MC-5) and the passive fit (MC-12) share: the variables as coordinates
// of the unit box (start, bounds, locks, ties), the registry's ask/tell algorithms driven over a residual vector, the
// Levenberg–Marquardt polish, the seeded multi-start search and the identifiability report (R-mc5-3, R-mc5-4). It knows
// nothing about what the residual measures — that is the caller's function of the free values.

using System.Globalization;
using CircuitRF.Engine;
using CircuitRF.Engine.Optimization;
using NumFlat;

namespace CircuitRF.Design.Modeling;

/// <summary>A variable as a coordinate of the unit box.</summary>
internal sealed record FitCoordinate(int Index, string Name, NetworkElementKind Kind, double Min, double Max, bool Log)
{
    /// <summary>The bottom of a logarithmic axis: the minimum, or a millionth of the maximum when the minimum is zero.</summary>
    public double LogFloor => Min > 0 ? Min : Max * 1e-6;

    public double Decode(double u) => Log ? LogFloor * Math.Pow(Max / LogFloor, u) : Min + u * (Max - Min);

    public double Encode(double v) => Math.Clamp(
        Log ? Math.Log(Math.Max(v, LogFloor) / LogFloor) / Math.Log(Max / LogFloor) : (v - Min) / (Max - Min), 0, 1);
}

/// <summary>
/// A network's variables as a fit sees them (R-mc5-1.3, R-mc5-2): each one's start, and whether it is a free coordinate,
/// locked at its value, or tied to another as <c>value(To) × Factor</c>.
/// </summary>
internal sealed class FitVariables
{
    public IReadOnlyList<NetworkVariable> Vars { get; }
    /// <summary>Every variable's start, network order.</summary>
    public double[] Start { get; }
    public FitCoordinate[] Free { get; }
    private readonly int[] _tieTo;
    private readonly double[] _tieFactor;

    private FitVariables(IReadOnlyList<NetworkVariable> vars, double[] start, int[] tieTo, double[] tieFactor, FitCoordinate[] free)
    {
        Vars = vars;
        Start = start;
        _tieTo = tieTo;
        _tieFactor = tieFactor;
        Free = free;
    }

    /// <summary>
    /// The variables with their settings applied. A tie names a variable of the network; a free variable has both
    /// bounds (its own, else <paramref name="defaults"/>') and a non-empty range; a start outside its range moves inside
    /// it with a note. <paramref name="field"/> prefixes a refusal's field (<c>Network.Variables</c>).
    /// </summary>
    public static FitVariables Build(IReadOnlyList<NetworkVariable> vars, IReadOnlyDictionary<string, NetworkVariableSettings> settings,
                                     Func<NetworkVariable, NetworkVariableSettings> defaults, string field, List<string> notes,
                                     IReadOnlyDictionary<string, double>? starts = null)
    {
        int n = vars.Count;
        var start = new double[n];
        var tieTo = new int[n];
        var tieFactor = new double[n];
        var free = new List<FitCoordinate>();
        for (int i = 0; i < n; i++)
        {
            var v = vars[i];
            var s = settings.GetValueOrDefault(v.Name) ?? new NetworkVariableSettings();
            start[i] = s.Value ?? (starts is not null && starts.TryGetValue(v.Name, out var st) ? st : v.Value);
            tieTo[i] = -1;
            if (s.Tie is { } tie)
            {
                int t = IndexOf(vars, tie.To);
                if (t < 0) throw new ModelingException($"{field}.{v.Name}.Tie.To", $"'{tie.To}' is not a variable of the network.");
                (tieTo[i], tieFactor[i]) = (t, tie.Factor);
                continue;
            }
            if (s.Lock) continue;
            var d = defaults(v);
            double? lo = s.Min ?? d.Min, hi = s.Max ?? d.Max;
            if (lo is null || hi is null)
                throw new ModelingException(v.Name, $"it has no {(lo is null ? "Min" : "Max")}; give it one, or lock it.");
            if (!(hi > lo)) throw new ModelingException(v.Name, $"its range {G(lo.Value)} to {G(hi.Value)} is empty; lock it instead.");
            bool log = v.Kind != NetworkElementKind.Other && lo >= 0;
            var c = new FitCoordinate(i, v.Name, v.Kind, lo.Value, hi.Value, log);
            double inBox = c.Decode(c.Encode(start[i]));
            if (inBox != start[i] && Math.Abs(inBox - start[i]) > 1e-12 * Math.Abs(start[i]))
            {
                notes.Add($"{v.Name}: start {G(start[i])} moved to {G(inBox)}, inside its range.");
                start[i] = inBox;
            }
            free.Add(c);
        }
        return new FitVariables(vars, start, tieTo, tieFactor, [.. free]);
    }

    private static int IndexOf(IReadOnlyList<NetworkVariable> vars, string name)
    {
        for (int i = 0; i < vars.Count; i++) if (vars[i].Name == name) return i;
        return -1;
    }

    /// <summary>The free coordinates' start values.</summary>
    public double[] FreeStart => [.. Free.Select(c => Start[c.Index])];

    /// <summary>Every variable's value at the free coordinates' values <paramref name="freeValues"/>, ties applied.</summary>
    public double[] Compose(double[] freeValues)
    {
        var v = (double[])Start.Clone();
        for (int j = 0; j < Free.Length; j++) v[Free[j].Index] = freeValues[j];
        for (int i = 0; i < v.Length; i++)
            if (_tieTo[i] >= 0) v[i] = v[_tieTo[i]] * _tieFactor[i];
        return v;
    }

    public Dictionary<string, double> Named(double[] all)
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < all.Length; i++) d[Vars[i].Name] = all[i];
        return d;
    }

    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}

/// <summary>Evaluations counted across every search one fit runs.</summary>
internal sealed class EvaluationCounter
{
    private long _count;
    public long Count => Interlocked.Read(ref _count);
    public void Add(int n) => Interlocked.Add(ref _count, n);
}

/// <summary>A point a search reached: the free values and the cost there.</summary>
internal sealed record Solved(double[] Free, double Cost);

/// <summary>
/// A residual vector over free coordinates in a box, and the searches over it: an ask/tell algorithm, the
/// Levenberg–Marquardt polish, the seeded multi-start search and the identifiability report. The cost is Σ r².
/// </summary>
internal sealed class BoxLeastSquares(FitCoordinate[] free, Func<double[], double[]> residuals, EvaluationCounter counter)
{
    private static readonly ParallelOptions Serial = new() { MaxDegreeOfParallelism = 1 };

    public FitCoordinate[] Free { get; } = free;

    /// <summary>
    /// Runs <paramref name="alg"/> over the coordinates <paramref name="coords"/> (indices into <see cref="Free"/>), the
    /// others held at <paramref name="baseline"/>, until it finishes or has spent <paramref name="maxEvals"/>.
    /// A batch is evaluated in parallel; the results go back in batch order, so the run is the same whatever the cores.
    /// </summary>
    public Solved Drive(IOptimizerAlgorithm alg, int[] coords, double[] baseline, int maxEvals, RunControl? rc)
    {
        double[]? best = null;
        Evaluation? bestE = null;
        int used = 0;
        var po = new ParallelOptions { CancellationToken = rc?.Token ?? CancellationToken.None, MaxDegreeOfParallelism = Environment.ProcessorCount };
        while (!alg.IsFinished && used < maxEvals)
        {
            rc?.ThrowIfCancellationRequested();
            var batch = alg.Ask();
            if (batch.Count == 0) break;
            var results = new Evaluation[batch.Count];
            Parallel.For(0, batch.Count, batch.Count > 1 ? po : Serial, i => results[i] = Evaluate(batch[i], coords, baseline));
            rc?.ThrowIfCancellationRequested();
            used += batch.Count;
            counter.Add(batch.Count);
            rc?.TickStage(batch.Count);
            for (int i = 0; i < batch.Count; i++)
                if (bestE is null || Better(results[i], bestE)) (bestE, best) = (results[i], batch[i]);
            alg.Tell(results);
        }
        var freeValues = (double[])baseline.Clone();
        if (best is not null)
            for (int j = 0; j < coords.Length; j++) freeValues[coords[j]] = Free[coords[j]].Decode(best[j]);
        return new Solved(freeValues, bestE?.Cost ?? double.PositiveInfinity);
    }

    private static bool Better(Evaluation a, Evaluation b) => a.Failed != b.Failed ? !a.Failed : a.Cost < b.Cost;

    private Evaluation Evaluate(double[] u, int[] coords, double[] baseline)
    {
        var freeValues = (double[])baseline.Clone();
        for (int j = 0; j < coords.Length; j++) freeValues[coords[j]] = Free[coords[j]].Decode(u[j]);
        try
        {
            var r = residuals(freeValues);
            double cost = 0;
            foreach (double x in r) cost += x * x;
            return new Evaluation(cost, r);
        }
        catch (Exception ex) when (Failure(ex))
        {
            return new Evaluation(1e300, Failed: true);
        }
    }

    /// <summary>What makes a trial point a failed evaluation rather than a crash.</summary>
    public static bool Failure(Exception ex) =>
        ex is ArithmeticException or ModelingException or InvalidOperationException or SingularMatrixException;

    public double[] Encode(double[] freeValues, int[] coords) => [.. coords.Select(j => Free[j].Encode(freeValues[j]))];

    public int[] All => [.. Enumerable.Range(0, Free.Length)];

    /// <summary>Levenberg–Marquardt from <paramref name="from"/> over <paramref name="coords"/>, up to 100 iterations.</summary>
    public Solved Polish(double[] from, int[] coords, RunControl? rc)
    {
        if (coords.Length == 0) return new Solved(from, Cost(from));
        var lm = new LevenbergMarquardt(Encode(from, coords));
        var s = Drive(lm, coords, from, 100 * (coords.Length + 2), rc);
        return s.Cost <= Cost(from) ? s : new Solved(from, Cost(from));
    }

    public double Cost(double[] freeValues)
    {
        try { return residuals(freeValues).Sum(x => x * x); }
        catch (Exception ex) when (Failure(ex)) { return double.PositiveInfinity; }
    }

    /// <summary>
    /// Per start a seeded global search over every free coordinate from <paramref name="from"/>, each polished by
    /// Levenberg–Marquardt; the best start wins and the polished starts' spread is kept (R-mc5-3).
    /// </summary>
    public (double[] Values, double[] Spread) MultiStart(double[] from, int starts, ulong seed, string algorithm, int budget,
                                                        RunControl? rc, string stage = "Start")
    {
        var all = All;
        var u0 = Encode(from, all);
        var finals = new List<Solved>();
        for (int s = 0; s < starts; s++)
        {
            rc?.BeginStage($"{stage} {s + 1} of {starts}", budget, "evaluation(s)");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            if (algorithm == DifferentialEvolution.AlgorithmId)
                options["budget"] = budget.ToString(CultureInfo.InvariantCulture);
            var alg = OptimizerFactory.Create(algorithm, u0, seed + (ulong)s, options)!;
            var global = Drive(alg, all, from, budget, rc);
            rc?.BeginStage($"{stage} {s + 1} of {starts}: polish", 0, "evaluation(s)");
            finals.Add(Polish(global.Free, all, rc));
        }
        var best = finals.MinBy(f => f.Cost)!;
        var spread = new double[Free.Length];
        for (int j = 0; j < spread.Length; j++)
            spread[j] = finals.Max(f => f.Free[j]) - finals.Min(f => f.Free[j]);
        return (best.Free, spread);
    }

    /// <summary>
    /// The residual Jacobian at the solution by central differences — in ln v for a logarithmic variable, so its column
    /// reads relative sensitivity — then <c>Cov = s²(JᵀJ)⁻¹</c>, <c>s² = SSR/(m − n)</c>: each variable's standard error
    /// and its correlation with every other ([R11] ch. 4).
    /// </summary>
    public IdentifiabilityReport Identify(double[] values, double[] spreads, double poorRelativeError, double poorCorrelation)
    {
        int n = Free.Length;
        double[] r0 = residuals(values);
        int m = r0.Length;
        var jac = new double[m, n];
        for (int j = 0; j < n; j++)
        {
            var c = Free[j];
            double h = c.Log ? 1e-4 : 1e-4 * (c.Max - c.Min);
            double[] Step(double sgn)
            {
                var v = (double[])values.Clone();
                v[j] = c.Log ? values[j] * Math.Exp(sgn * h) : values[j] + sgn * h;
                return residuals(v);
            }
            var rp = Step(+1);
            var rm = Step(-1);
            for (int k = 0; k < m; k++) jac[k, j] = (rp[k] - rm[k]) / (2 * h);
        }
        double ssr = r0.Sum(x => x * x);
        double s2 = ssr / Math.Max(1, m - n);

        // JᵀJ, normalised to a unit diagonal, inverted through its SVD (a direction the data does not see is a huge
        // variance, not a crash).
        var jtj = new double[n, n];
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                double sum = 0;
                for (int k = 0; k < m; k++) sum += jac[k, a] * jac[k, b];
                jtj[a, b] = sum;
            }
        var dScale = new double[n];
        for (int a = 0; a < n; a++) dScale[a] = jtj[a, a] > 0 ? 1 / Math.Sqrt(jtj[a, a]) : 0;
        var mat = new Mat<double>(n, n);
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
                mat[a, b] = jtj[a, b] * dScale[a] * dScale[b];
        var svd = mat.Svd();
        double smax = svd.S[0];
        var inv = new double[n, n];
        for (int q = 0; q < n; q++)
        {
            double sv = Math.Max(svd.S[q], 1e-15 * Math.Max(smax, 1e-300));
            for (int a = 0; a < n; a++)
                for (int b = 0; b < n; b++)
                    inv[a, b] += svd.U[a, q] * svd.U[b, q] / sv;
        }

        var vars = new List<FittedVariable>();
        for (int j = 0; j < n; j++)
        {
            var c = Free[j];
            double v = values[j];
            bool seen = dScale[j] > 0;
            double sigmaP = seen ? Math.Sqrt(s2 * inv[j, j]) * dScale[j] : double.PositiveInfinity;
            double stdErr = c.Log ? Math.Abs(v) * sigmaP : sigmaP;
            double rel = v != 0 ? stdErr / Math.Abs(v) : double.PositiveInfinity;
            string? partner = null;
            double rho = 0;
            for (int k = 0; k < n; k++)
            {
                if (k == j || !seen || dScale[k] == 0) continue;
                double rk = inv[j, k] / Math.Sqrt(inv[j, j] * inv[k, k]);
                if (Math.Abs(rk) > Math.Abs(rho)) (rho, partner) = (rk, Free[k].Name);
            }
            double u = c.Encode(v);
            string? atBound = u <= 0.005 ? "Min" : u >= 0.995 ? "Max" : null;
            bool poor = !seen || rel > poorRelativeError || Math.Abs(rho) > poorCorrelation;
            vars.Add(new FittedVariable(c.Name, v, c.Min, c.Max, stdErr, rel, spreads[j], atBound, partner, rho, poor));
        }

        var lines = new List<string>();
        foreach (var v in vars.Where(v => v.Poor))
        {
            if (double.IsInfinity(v.StdError)) lines.Add($"{v.Name} not determined by the data");
            else if (Math.Abs(v.Correlation) > poorCorrelation)
                lines.Add($"{v.Name} poorly determined (ρ = {Signed(v.Correlation)} with {v.Partner})");
            else lines.Add($"{v.Name} poorly determined (±{(100 * v.RelativeError).ToString("0", CultureInfo.InvariantCulture)} %)");
        }
        foreach (var v in vars.Where(v => v.AtBound is not null))
            lines.Add($"{v.Name} at bound {v.AtBound} ({G(v.AtBound == "Min" ? v.Min : v.Max)})");
        // One suggestion per poorly determined variable or correlated pair, named once.
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in vars.Where(v => v.Poor))
        {
            if (!named.Add(v.Name)) continue;
            if (v.Partner is { } p && Math.Abs(v.Correlation) > poorCorrelation)
            {
                named.Add(p);
                double other = vars.First(x => x.Name == p).Value;
                lines.Add($"Lock {v.Name}, or tie it to {p} × {G(v.Value / other)}.");
            }
            else lines.Add($"Lock {v.Name} at a typed value.");
        }
        return new IdentifiabilityReport(vars, lines);
    }

    private static string Signed(double x) =>
        (x < 0 ? "−" : "") + Math.Abs(x).ToString("0.00", CultureInfo.InvariantCulture);

    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}
