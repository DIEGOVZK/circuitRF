namespace CircuitRF.Engine.Modeling.Ann;

/// <summary>Why <see cref="Lbfgs.Minimize"/> stopped.</summary>
public enum LbfgsStop
{
    /// <summary>The gradient's largest component fell to the tolerance.</summary>
    Gradient,
    /// <summary>One iteration changed the loss by less than the tolerance, relative to the loss.</summary>
    LossChange,
    /// <summary>The iteration cap.</summary>
    Iterations,
    /// <summary>No step along the search direction, nor along the steepest descent, lowered the loss.</summary>
    LineSearch,
    /// <summary>The caller's per-iteration callback asked to stop (early stopping).</summary>
    Callback,
}

/// <summary>
/// Limited-memory BFGS with a strong-Wolfe line search (R-mc6-4), for a smooth loss with an exact
/// gradient. In-house and double precision; single-threaded and branch-for-branch deterministic, so
/// the same objective gives the same iterates everywhere.
///
/// <para>The two-loop recursion of Nocedal &amp; Wright Alg. 7.4, the initial Hessian scaled by
/// <c>sᵀy / yᵀy</c>, and the bracketing line search with cubic-interpolation zoom of their Alg. 3.5/3.6
/// (c₁ = 1e-4, c₂ = 0.9). A pair with <c>sᵀy</c> not safely positive is skipped rather than stored. When
/// the line search finds no acceptable step the memory is dropped and steepest descent is tried once;
/// if that fails too, the minimiser stops.</para>
/// </summary>
public static class Lbfgs
{
    /// <summary>The loss at <c>p</c>, its gradient written into the second argument.</summary>
    public delegate double Objective(double[] p, double[] grad);

    public sealed record Settings(int Memory = 10, int MaxIterations = 2000,
                                  double GradientTolerance = 1e-9, double LossChangeTolerance = 1e-14);

    public sealed record Outcome(double Loss, int Iterations, int Evaluations, LbfgsStop Stop);

    private const double C1 = 1e-4, C2 = 0.9;
    private const int MaxLineEvaluations = 25;

    /// <summary>Minimises in place, starting from <paramref name="p"/>.</summary>
    /// <param name="onIteration">Called after every accepted step with the iteration count and the
    /// current point; returning false stops. It is where progress, cancellation and early stopping
    /// live.</param>
    public static Outcome Minimize(Objective f, double[] p, Settings settings, Func<int, double[], double, bool>? onIteration = null)
    {
        int n = p.Length, m = Math.Max(1, settings.Memory);
        var g = new double[n];
        var d = new double[n];
        var trial = new double[n];
        var gTrial = new double[n];
        var sNew = new double[n];
        var yNew = new double[n];
        var S = new double[m][];
        var Y = new double[m][];
        var rho = new double[m];
        var alpha = new double[m];
        for (int i = 0; i < m; i++) { S[i] = new double[n]; Y[i] = new double[n]; }
        int stored = 0, head = 0;

        double loss = f(p, g);
        int evaluations = 1;
        if (!double.IsFinite(loss)) return new(loss, 0, evaluations, LbfgsStop.LineSearch);
        bool resetOnce = false;

        for (int iter = 1; ; iter++)
        {
            if (MaxAbs(g) <= settings.GradientTolerance) return new(loss, iter - 1, evaluations, LbfgsStop.Gradient);
            if (iter > settings.MaxIterations) return new(loss, iter - 1, evaluations, LbfgsStop.Iterations);

            // d = −H·g by the two-loop recursion, newest pair first.
            for (int i = 0; i < n; i++) d[i] = -g[i];
            for (int k = 0; k < stored; k++)
            {
                int s = (head - 1 - k + m) % m;
                alpha[s] = rho[s] * Dot(S[s], d);
                Axpy(-alpha[s], Y[s], d);
            }
            if (stored > 0)
            {
                int newest = (head - 1 + m) % m;
                double gamma = Dot(S[newest], Y[newest]) / Dot(Y[newest], Y[newest]);
                for (int i = 0; i < n; i++) d[i] *= gamma;
            }
            for (int k = stored - 1; k >= 0; k--)
            {
                int s = (head - 1 - k + m) % m;
                double beta = rho[s] * Dot(Y[s], d);
                Axpy(alpha[s] - beta, S[s], d);
            }

            double slope = Dot(g, d);
            if (!(slope < 0))
            {
                for (int i = 0; i < n; i++) d[i] = -g[i];
                slope = Dot(g, d);
                stored = 0;
            }

            double step0 = stored == 0 ? Math.Min(1.0, 1.0 / Math.Sqrt(Dot(g, g))) : 1.0;
            var (ok, step, newLoss, evals) = LineSearch(f, p, loss, g, d, slope, step0, trial, gTrial);
            evaluations += evals;
            if (!ok)
            {
                if (stored == 0 || resetOnce) return new(loss, iter - 1, evaluations, LbfgsStop.LineSearch);
                stored = 0; resetOnce = true; iter--;
                continue;
            }
            resetOnce = false;

            // The pair (s, y) of this step; kept only when its curvature is safely positive. It is built
            // apart from the memory and swapped in, because with the memory full S[head] is the OLDEST pair
            // still in use, and a rejected pair written over it would leave that slot with a stale ρ.
            for (int i = 0; i < n; i++)
            {
                sNew[i] = trial[i] - p[i];
                yNew[i] = gTrial[i] - g[i];
            }
            double sy = Dot(sNew, yNew);
            if (sy > 1e-12 * Math.Sqrt(Dot(sNew, sNew) * Dot(yNew, yNew)))
            {
                (S[head], sNew) = (sNew, S[head]);
                (Y[head], yNew) = (yNew, Y[head]);
                rho[head] = 1.0 / sy;
                head = (head + 1) % m;
                stored = Math.Min(stored + 1, m);
            }

            double change = loss - newLoss;
            Array.Copy(trial, p, n);
            Array.Copy(gTrial, g, n);
            loss = newLoss;

            if (onIteration is not null && !onIteration(iter, p, loss)) return new(loss, iter, evaluations, LbfgsStop.Callback);
            if (change <= settings.LossChangeTolerance * Math.Max(Math.Abs(loss), double.Epsilon))
                return new(loss, iter, evaluations, LbfgsStop.LossChange);
        }
    }

    /// <summary>
    /// Nocedal &amp; Wright Alg. 3.5: grow the step until the strong-Wolfe conditions hold or a bracket
    /// appears, then zoom. On success <paramref name="trial"/> and <paramref name="gTrial"/> hold the
    /// accepted point and its gradient.
    /// </summary>
    private static (bool Ok, double Step, double Loss, int Evaluations) LineSearch(
        Objective f, double[] p, double f0, double[] g0, double[] d, double slope0, double step,
        double[] trial, double[] gTrial)
    {
        int evals = 0;
        double prevStep = 0.0, prevLoss = f0, prevSlope = slope0;
        for (int i = 0; i < MaxLineEvaluations; i++)
        {
            double fs = Probe(f, p, d, step, trial, gTrial); evals++;
            double gs = Dot(gTrial, d);
            if (!double.IsFinite(fs) || fs > f0 + C1 * step * slope0 || (i > 0 && fs >= prevLoss))
                return Zoom(f, p, f0, slope0, d, prevStep, prevLoss, prevSlope, step, fs, gs, trial, gTrial, evals);
            if (Math.Abs(gs) <= -C2 * slope0) return (true, step, fs, evals);
            if (gs >= 0)
                return Zoom(f, p, f0, slope0, d, step, fs, gs, prevStep, prevLoss, prevSlope, trial, gTrial, evals);
            prevStep = step; prevLoss = fs; prevSlope = gs;
            step *= 2.0;
        }

        // Still descending at the last doubling: the longest step tried met the sufficient decrease, so take it.
        if (prevStep > 0)
        {
            double fs = Probe(f, p, d, prevStep, trial, gTrial); evals++;
            return (true, prevStep, fs, evals);
        }
        return (false, 0, f0, evals);
    }

    /// <summary>Alg. 3.6: the bracket [lo, hi] shrinks by a safeguarded cubic step until the strong-Wolfe conditions hold.</summary>
    private static (bool Ok, double Step, double Loss, int Evaluations) Zoom(
        Objective f, double[] p, double f0, double slope0, double[] d,
        double lo, double fLo, double gLo, double hi, double fHi, double gHi,
        double[] trial, double[] gTrial, int evals)
    {
        while (evals < MaxLineEvaluations)
        {
            double step = Cubic(lo, fLo, gLo, hi, fHi, gHi);
            double width = Math.Abs(hi - lo);
            double left = Math.Min(lo, hi), right = Math.Max(lo, hi);
            if (!double.IsFinite(fHi) || !(step > left + 0.1 * width && step < right - 0.1 * width))
                step = 0.5 * (lo + hi);
            if (width <= 1e-16 * Math.Max(1.0, Math.Abs(lo))) break;

            double fs = Probe(f, p, d, step, trial, gTrial); evals++;
            double gs = Dot(gTrial, d);
            if (!double.IsFinite(fs) || fs > f0 + C1 * step * slope0 || fs >= fLo)
            {
                hi = step; fHi = fs; gHi = gs;
                continue;
            }
            if (Math.Abs(gs) <= -C2 * slope0) return (true, step, fs, evals);
            if (gs * (hi - lo) >= 0) { hi = lo; fHi = fLo; gHi = gLo; }
            lo = step; fLo = fs; gLo = gs;
        }

        // No strong-Wolfe step, but lo strictly lowered the loss: accept it (its pair may be skipped).
        if (lo > 0 && fLo < f0)
        {
            double fs = Probe(f, p, d, lo, trial, gTrial); evals++;
            return (true, lo, fs, evals);
        }
        return (false, 0, f0, evals);
    }

    /// <summary>The minimiser of the cubic through (a, fa, ga) and (b, fb, gb); NaN when it has none.</summary>
    private static double Cubic(double a, double fa, double ga, double b, double fb, double gb)
    {
        double d1 = ga + gb - 3.0 * (fa - fb) / (a - b);
        double disc = d1 * d1 - ga * gb;
        if (!(disc >= 0)) return double.NaN;
        double d2 = Math.Sign(b - a) * Math.Sqrt(disc);
        return b - (b - a) * (gb + d2 - d1) / (gb - ga + 2.0 * d2);
    }

    private static double Probe(Objective f, double[] p, double[] d, double step, double[] trial, double[] gTrial)
    {
        for (int i = 0; i < p.Length; i++) trial[i] = p[i] + step * d[i];
        return f(trial, gTrial);
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0.0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static void Axpy(double a, double[] x, double[] y)
    {
        for (int i = 0; i < x.Length; i++) y[i] += a * x[i];
    }

    private static double MaxAbs(double[] v)
    {
        double m = 0.0;
        foreach (double x in v) m = Math.Max(m, Math.Abs(x));
        return m;
    }
}
