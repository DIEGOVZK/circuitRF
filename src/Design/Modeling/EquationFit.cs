// An equation's named parameters fitted to scattered intrinsic points (brief-modelcomposer-7 R-mc7-2): weighted least
// squares, searched by the optimizer registry's own algorithms over the unit box — a global search (CMA-ES or
// differential evolution) when asked for, then Levenberg–Marquardt — exactly as the network fit drives them (MC-5).
//
// The expression compiles ONCE, with vgs, vds and every free parameter as seed lanes of the SDD's dual-number program,
// so a trial point is an evaluation and never a recompile; locked parameters are its constants.

using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine;
using CircuitRF.Engine.Optimization;

namespace CircuitRF.Design.Modeling;

/// <summary>A fitted equation: every parameter's value, and the weighted cost it reached.</summary>
public sealed record EquationFitResult(IReadOnlyList<KeyValuePair<string, double>> Values, double Cost);

/// <summary>One expression and the points it is fitted to: rows of (vgs, vds), targets, weights. With
/// <paramref name="Derivative"/> the targets are the expression's partial derivative by that input (0 = vgs, 1 = vds)
/// — how a charge equation is fitted to capacitances (MC-8).</summary>
internal sealed record EquationTerm(string Expression, double[][] X, double[] Y, double[] W, int? Derivative = null);

internal sealed class EquationFit
{
    /// <summary>Two lanes go to vgs and vds; the rest may be free parameters.</summary>
    public const int MaxFree = 14;

    private readonly (CompiledSddExpr Expr, EquationTerm Term, double[] SqrtW, double Scale)[] _terms;
    private readonly string _label;
    private readonly FitCoordinate[] _free;
    private readonly IReadOnlyList<TemplateParameter> _parameters;

    /// <summary>Every term shares the parameters: the two gate branches of one constituent carry one parameter list.
    /// Each term's cost is its own weighted mean square relative to its largest target, so neither swamps the other.</summary>
    private EquationFit(IReadOnlyList<EquationTerm> terms, IReadOnlyList<TemplateParameter> parameters, string label)
    {
        _label = label;
        _parameters = parameters;
        var free = parameters.Where(p => !p.Lock).ToList();
        if (free.Count > MaxFree)
            throw new ModelingException(label, $"{free.Count} free parameters; an equation fit takes at most {MaxFree}.");
        _free = [.. free.Select((p, i) => new FitCoordinate(i, p.Name, NetworkElementKind.Other, p.Min, p.Max,
                                                           p.Min > 0 && p.Max >= 100 * p.Min))];
        var constants = parameters.Where(p => p.Lock).ToDictionary(p => p.Name, p => p.Start, StringComparer.Ordinal);
        var known = new HashSet<string>(parameters.Select(p => p.Name), StringComparer.Ordinal) { "vgs", "vds", "pi", "e" };
        _terms = new (CompiledSddExpr, EquationTerm, double[], double)[terms.Count];
        for (int t = 0; t < terms.Count; t++)
        {
            var term = terms[t];
            Expr ast;
            try { ast = Parser.Parse(term.Expression); }
            catch (Exception ex) { throw new ModelingException(label, $"'{term.Expression}' does not parse: {ex.Message}"); }
            foreach (string r in AstWalker.CollectRefs(ast))
                if (!known.Contains(r))
                    throw new ModelingException(label, $"'{r}' is neither vgs, vds nor one of its parameters.");
            double sw = term.W.Sum();
            _terms[t] = (CompiledSddExpr.CompileNamed(ast, ["vgs", "vds", .. free.Select(p => p.Name)], constants, label),
                         term, [.. term.W.Select(v => sw > 0 ? Math.Sqrt(v / sw) : 0)],
                         Math.Max(term.Y.Select(Math.Abs).DefaultIfEmpty(0).Max(), 1e-300));
        }
    }

    /// <summary>
    /// Fits the terms' expressions to their targets, weighting each squared residual by its weight. Throws
    /// <see cref="ModelingException"/> for an expression that does not parse or names an unknown, and
    /// <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static EquationFitResult Fit(IReadOnlyList<EquationTerm> terms, IReadOnlyList<TemplateParameter> parameters,
                                        string algorithm, ulong seed, string label, RunControl? rc)
        => new EquationFit(terms, parameters, label).Run(algorithm, seed, rc);

    private EquationFitResult Run(string algorithm, ulong seed, RunControl? rc)
    {
        int n = _free.Length;
        double[] start = [.. _free.Select(c => c.Encode(_parameters.First(p => p.Name == c.Name).Start))];
        if (n == 0) return Result(start, Evaluate(start).Cost);

        var best = (U: start, Cost: Evaluate(start).Cost);
        if (algorithm != LevenbergMarquardt.AlgorithmId)
        {
            int budget = 200 * n;
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            if (algorithm == DifferentialEvolution.AlgorithmId) options["budget"] = budget.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var global = Drive(OptimizerFactory.Create(algorithm, start, seed, options)!, budget, rc);
            if (global.Cost < best.Cost) best = global;
        }
        var polished = Drive(new LevenbergMarquardt(best.U), 100 * (n + 2), rc);
        if (polished.Cost < best.Cost) best = polished;
        return Result(best.U, best.Cost);
    }

    private EquationFitResult Result(double[] u, double cost)
    {
        var values = new List<KeyValuePair<string, double>>();
        foreach (var p in _parameters)
        {
            var c = _free.FirstOrDefault(f => f.Name == p.Name);
            values.Add(new(p.Name, c is null ? p.Start : c.Decode(u[c.Index])));
        }
        return new EquationFitResult(values, cost);
    }

    private (double[] U, double Cost) Drive(IOptimizerAlgorithm alg, int maxEvals, RunControl? rc)
    {
        double[]? best = null;
        double bestCost = double.PositiveInfinity;
        int used = 0;
        while (!alg.IsFinished && used < maxEvals)
        {
            rc?.ThrowIfCancellationRequested();
            var batch = alg.Ask();
            if (batch.Count == 0) break;
            var results = new Evaluation[batch.Count];
            for (int i = 0; i < batch.Count; i++)
            {
                results[i] = Evaluate(batch[i]);
                if (!results[i].Failed && results[i].Cost < bestCost) (bestCost, best) = (results[i].Cost, batch[i]);
            }
            used += batch.Count;
            rc?.TickStage(batch.Count);
            alg.Tell(results);
        }
        return (best ?? [.. Enumerable.Repeat(0.5, _free.Length)], bestCost);
    }

    private Evaluation Evaluate(double[] u)
    {
        Span<double> seeds = stackalloc double[2 + MaxFree];
        Span<double> grad = stackalloc double[2 + MaxFree];
        int n = _free.Length;
        for (int j = 0; j < n; j++) seeds[2 + j] = _free[j].Decode(u[j]);
        var r = new double[_terms.Sum(t => t.Term.Y.Length)];
        double cost = 0;
        int i = 0;
        foreach (var (expr, term, sqrtW, scale) in _terms)
            for (int k = 0; k < term.Y.Length; k++, i++)
            {
                seeds[0] = term.X[k][0];
                seeds[1] = term.X[k][1];
                double f;
                try { f = expr.EvalDualInto(seeds[..(2 + n)], grad, _label); }
                catch (ArithmeticException) { return new Evaluation(1e300, Failed: true); }
                if (term.Derivative is { } d) f = grad[d];
                if (!double.IsFinite(f)) return new Evaluation(1e300, Failed: true);
                r[i] = sqrtW[k] * (f - term.Y[k]) / scale;
                cost += r[i] * r[i];
            }
        return new Evaluation(cost, r);
    }

    /// <summary>The fitted equation as a runtime function of (vgs, vds) with its gradient — for the report.</summary>
    public static Func<double, double, (double F, double Dg, double Dd)> Function(string expression,
        IReadOnlyList<KeyValuePair<string, double>> values, string label)
    {
        var expr = CompiledSddExpr.CompileNamed(Parser.Parse(expression), ["vgs", "vds"],
                                                values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), label);
        return (vgs, vds) =>
        {
            Span<double> s = [vgs, vds];
            Span<double> g = stackalloc double[2];
            double f = expr.EvalDualInto(s, g, label);
            return (f, g[0], g[1]);
        };
    }
}
