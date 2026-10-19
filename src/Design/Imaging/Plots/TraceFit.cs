// A curve fitted through a digitised trace — brief-pdg-2-curve-fit.md R-pdg2-1 … R-pdg2-3.
//
// A FIT RUNS AFTER READING, ON SAMPLES. Nothing here touches a picture: the reader's samples go in, a formula comes out,
// and the outputs (the Touchstone, the CSV's y_fit, the overlay) evaluate it where they used to resample.
//
// IN THE TRACE'S OWN PLOTTED UNITS: dB for a dB trace, degrees for phase (unwrapped first, so a wrapped sawtooth is one
// curve), the axis's value for anything else — the quantity the curve is drawn in. x is the axis's value in its DISPLAY
// unit (f in GHz on a frequency axis), so the coefficients are readable; on a log x axis the variable is log10(f),
// because a straight line on a log plot is what the user is looking at. Rectangular traces only: a chart trace's x is a
// frequency spread along the curve, and it is not fitted.
//
// WELL CONDITIONED BY CONSTRUCTION. Every least-squares fit is solved on x centred and scaled to [−1, 1] by Householder
// QR, never the normal equations; a polynomial's coefficients are expanded back to raw x only for display. The exponential
// starts from a log-linear fit and is refined on the y residuals by Levenberg–Marquardt, and refuses — with a sentence —
// rather than return a poor fit. The spline is a cubic smoothing spline (Reinsch), smoothed by default until its RMS
// residual is one pixel's worth of value: that removes pixel noise and nothing the picture resolves.
//
// EXACT SAMPLES STAY EXACT: a marker's readout weighs ×100 in every least-squares fit, and the spline passes through it.
// A FIT NEVER EXTRAPOLATES: Evaluate is NaN outside the trace's own x range.
//
// PIECES JOINED ARE FITTED ONCE. Pieces TraceJoin joins (one entry and quantity on one plot) share one fit, made on the
// joined curve, whichever caller passes the session — joined already (the dialog) or not (the CLI).

using System.Globalization;
using System.Text;

namespace CircuitRF.Design.Imaging.Plots;

public enum TraceFitModel { None, Linear, Exponential, Polynomial, Spline }

/// <summary>What to fit to a trace.</summary>
/// <param name="Degree">A polynomial's degree, 1 to <see cref="MaxDegree"/>.</param>
/// <param name="Smoothing">A spline's RMS residual target in the trace's units; null is one pixel's worth (its resolution).</param>
/// <param name="LogX">Fit in log10(x); null is the x axis's own scale.</param>
public sealed record TraceFitSpec(TraceFitModel Model, int Degree = 3, double? Smoothing = null, bool? LogX = null)
{
    public const int MaxDegree = 8;

    public static TraceFitSpec None { get; } = new(TraceFitModel.None);

    /// <summary>The <c>--fit</c> spelling: <c>linear</c>, <c>exp</c>, <c>poly:3</c>, <c>spline</c>, <c>spline:0.05</c>, <c>none</c>.</summary>
    public override string ToString() => Model switch
    {
        TraceFitModel.Linear => "linear",
        TraceFitModel.Exponential => "exp",
        TraceFitModel.Polynomial => "poly:" + Degree.ToString(CultureInfo.InvariantCulture),
        TraceFitModel.Spline => Smoothing is { } s ? "spline:" + s.ToString("R", CultureInfo.InvariantCulture) : "spline",
        _ => "none",
    };

    /// <summary>Parses <see cref="ToString"/>'s spelling (and <c>polynomial</c>, <c>exponential</c>, <c>lin</c>); null when
    /// it is not one.</summary>
    public static TraceFitSpec? Parse(string text)
    {
        string s = text.Trim();
        int colon = s.IndexOf(':');
        string word = (colon < 0 ? s : s[..colon]).Trim().ToLowerInvariant();
        string? arg = colon < 0 ? null : s[(colon + 1)..].Trim();
        switch (word)
        {
            case "none" or "off" when arg is null: return None;
            case "linear" or "lin" or "line" when arg is null: return new(TraceFitModel.Linear);
            case "exp" or "exponential" when arg is null: return new(TraceFitModel.Exponential);
            case "poly" or "polynomial":
                if (arg is null) return new(TraceFitModel.Polynomial);
                return int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) && d >= 1 && d <= MaxDegree
                    ? new(TraceFitModel.Polynomial, d) : null;
            case "spline":
                if (arg is null) return new(TraceFitModel.Spline);
                return double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 && double.IsFinite(v)
                    ? new(TraceFitModel.Spline, Smoothing: v) : null;
            default: return null;
        }
    }

    /// <summary>The model in words: <c>polynomial degree 3</c>.</summary>
    public string Name => Model switch
    {
        TraceFitModel.Linear => "linear",
        TraceFitModel.Exponential => "exponential",
        TraceFitModel.Polynomial => "polynomial degree " + Degree.ToString(CultureInfo.InvariantCulture),
        TraceFitModel.Spline => "spline",
        _ => "none",
    };
}

/// <summary>A fit asked of the traces a selector names (<c>--fit</c>, the traces table's Fit combo).</summary>
/// <param name="Selector">As <c>--trace</c>'s: <c>T3</c>, <c>#rrggbb</c>, a line style, each optionally <c>@n</c>; or <c>all</c>.</param>
public sealed record PlotFitOverride(string Selector, TraceFitSpec Spec);

/// <summary>The axes a trace is fitted against.</summary>
/// <param name="XScale">What one display unit of x is in the samples' x (1e9 for GHz on a frequency axis); 1 otherwise.</param>
/// <param name="XUnit">The display unit's name (<c>GHz</c>), empty when x has none.</param>
/// <param name="XVariable"><c>f</c> on a frequency axis, else <c>x</c>.</param>
/// <param name="Y">The y axis the trace is read against: residuals in pixels come from its <c>PerPixel</c>.</param>
/// <param name="Resolution">One pixel's worth of value — the spline's default RMS target.</param>
/// <param name="YUnit">The trace's unit (<c>dB</c>, <c>°</c>), empty when none.</param>
/// <param name="PhasePeriod">360 (or 2π) for a phase trace, which is unwrapped before fitting; null otherwise.</param>
public sealed record TraceFitAxis(double XScale = 1, string XUnit = "", string XVariable = "x", bool XLog = false,
                                  AxisCalibration? Y = null, double Resolution = 0, string YUnit = "", double? PhasePeriod = null);

/// <summary>A trace's fit.</summary>
/// <param name="Coefficients">Lowest power first in raw x (display unit, or log10 of it): a polynomial's (a linear fit's
/// <c>[a, b]</c>), an exponential's <c>[a, b, c]</c> of <c>a·exp(b·x) + c</c>; empty for a spline.</param>
/// <param name="Expression">The fit as circuitRF expression text (R-pdg2-2); null for a spline and a refused fit.</param>
/// <param name="XVariable">The variable the expression is written in (<c>f</c>, <c>x</c>).</param>
/// <param name="XUnit">The variable's unit (<c>GHz</c>), stated beside the expression, never in it.</param>
/// <param name="RmsResidual">Against the raw reading, in the trace's units.</param>
/// <param name="RmsResidualPx">The same in pixels: under ~0.5 px fits the drawing; several is the wrong model.</param>
/// <param name="Refusal">Why it was not fitted; null when it was.</param>
public sealed record TraceFitResult(TraceFitSpec Spec, IReadOnlyList<double> Coefficients, string? Expression, string XVariable, string XUnit,
                                    double RmsResidual, double MaxResidual, double RmsResidualPx, string? Refusal)
{
    /// <summary>The trace fitted: the joined trace's name.</summary>
    public string Trace { get; init; } = "";

    /// <summary>The pieces fitted together (<c>T37</c>, <c>T39</c>), or the trace alone.</summary>
    public IReadOnlyList<string> Pieces { get; init; } = [];

    public string YUnit { get; init; } = "";

    /// <summary>Fitted in log10 of x.</summary>
    public bool LogX { get; init; }

    /// <summary>A spline's knots (its distinct x values).</summary>
    public int Knots { get; init; }

    /// <summary>The RMS target a spline was smoothed to, in the trace's units.</summary>
    public double Smoothing { get; init; }

    /// <summary>The spline's target was the reading's resolution, not typed.</summary>
    public bool SmoothingIsResolution { get; init; }

    /// <summary>The trace's x range, in the samples' own x (hertz on a frequency axis).</summary>
    public double XMin { get; init; } = double.NaN;
    public double XMax { get; init; } = double.NaN;

    internal double XScale { get; init; } = 1;
    internal double PhasePeriod { get; init; }

    /// <summary>The fitted curve in the fit's variable (display unit, log10 when <see cref="LogX"/>).</summary>
    internal Func<double, double>? Curve { get; init; }

    /// <summary>It was fitted: a model other than None, and not refused.</summary>
    public bool Fitted => Refusal is null && Spec.Model != TraceFitModel.None && Curve is not null;

    /// <summary>The variable as the expression uses it: <c>f</c>, or <c>log10(f)</c> on a log x axis.</summary>
    public string VariableTerm => LogX ? $"log10({XVariable})" : XVariable;

    /// <summary>The expression with its variable's unit beside it (<c>… · f in GHz</c>), or <c>spline · N knots</c>.</summary>
    public string Readout => Refusal is { } r ? r
        : Spec.Model == TraceFitModel.Spline ? $"spline · {Knots.ToString(CultureInfo.InvariantCulture)} knots"
        : Expression + (XUnit.Length > 0 ? $" · {XVariable} in {XUnit}" : "");

    /// <summary><c>RMS 0.012 dB (0.3 px)</c>; empty when not fitted.</summary>
    public string ResidualText => !Fitted ? ""
        : $"RMS {TraceFit.Num(RmsResidual)}{TraceFit.UnitAfter(YUnit)} ({TraceFit.Px(RmsResidualPx)} px)";
}

public static class TraceFit
{
    /// <summary>A marker's readout weighs this much in a least-squares fit (R-pdg2-1).</summary>
    public const double ExactWeight = 100;

    // ── a session's fits ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every trace a fit is asked of, fitted, keyed by trace name: pieces TraceJoin joins share one fit made on the joined
    /// curve, under each piece's name. A fit names its traces as <c>--trace</c> does, plus <c>all</c>; the last one naming
    /// a trace (or any piece of it) wins, and <c>none</c> takes a fit away. Ignored traces and excluded plots are not fitted.
    /// </summary>
    public static IReadOnlyDictionary<string, TraceFitResult> Session(PlotSession session, IReadOnlyList<PlotFitOverride>? fits)
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = new Dictionary<string, TraceFitResult>(StringComparer.Ordinal);
        if (fits is not { Count: > 0 }) return result;
        var (joined, _) = TraceJoin.Session(session);
        foreach (var t in joined.Traces)
        {
            var plot = session.Plots[t.Plot - 1];
            if (plot.Excluded || t.Assignment.Ignored) continue;
            var spec = fits.LastOrDefault(f => Selects(f.Selector, t))?.Spec;
            if (spec is null || spec.Model == TraceFitModel.None) continue;
            var fit = For(t, plot, spec);
            foreach (string name in fit.Pieces) result[name] = fit;
        }
        return result;
    }

    /// <summary>The names of the pieces fitted together with trace <paramref name="name"/> — the pieces TraceJoin joins
    /// with it, or the trace alone.</summary>
    public static IReadOnlyList<string> FittedWith(PlotSession session, string name)
    {
        ArgumentNullException.ThrowIfNull(session);
        var (joined, _) = TraceJoin.Session(session);
        return joined.Traces.Select(PieceNames).FirstOrDefault(p => p.Contains(name)) ?? [name];
    }

    /// <summary>Trace <paramref name="t"/> (joined or not) of <paramref name="plot"/>, fitted.</summary>
    public static TraceFitResult For(PlotTraceRead t, PlotRead plot, TraceFitSpec spec)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(plot);
        TraceFitResult Named(TraceFitResult r) => r with { Trace = t.Name, Pieces = PieceNames(t) };
        if (t.Parametric || plot.Rect is null)
            return Named(Refused(spec, "a chart trace is not fitted: only a trace on rectangular axes has an x to fit against"));
        var r = plot.Rect;
        if (r.X is not { } xa || r.AxisOn(t.Axis) is not { } ya) return Named(Refused(spec, "its axes are not calibrated"));
        bool freq = r.XIsFrequency;
        double? period = t.Assignment.Quantity switch
        {
            PlotQuantityKind.PhaseDeg => 360,
            PlotQuantityKind.PhaseRad => 2 * Math.PI,
            _ => null,
        };
        var axis = new TraceFitAxis(freq ? r.XUnit : 1, freq ? FrequencyUnit(r.XUnit) : "", freq ? "f" : "x",
                                    spec.LogX ?? xa.Scale == AxisScale.Log, ya, t.Resolution, t.ResolutionUnit, period);
        return Named(Fit(t.Samples, spec, axis));
    }

    /// <summary>Fits <paramref name="samples"/> (their X and Y) against <paramref name="axis"/>. Pure and deterministic.</summary>
    public static TraceFitResult Fit(IReadOnlyList<PlotSample> samples, TraceFitSpec spec, TraceFitAxis axis)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(axis);
        if (spec.Model == TraceFitModel.None) return Refused(spec, null) with { XScale = axis.XScale };
        TraceFitResult Refuse(string why) => Refused(spec, why) with { YUnit = axis.YUnit };

        // The raw reading: in x order, phase unwrapped, x as the fit's variable.
        var raw = samples.Where(s => double.IsFinite(s.X) && double.IsFinite(s.Y) && (!axis.XLog || s.X > 0))
                         .OrderBy(s => s.X).ToList();
        if (raw.Count == 0) return Refuse("the trace has no samples to fit");
        if (axis.PhasePeriod is { } period)
            for (int i = 1; i < raw.Count; i++)
                raw[i] = raw[i] with { Y = raw[i].Y - period * Math.Round((raw[i].Y - raw[i - 1].Y) / period) };
        double Var(double x) => axis.XLog ? Math.Log10(x / axis.XScale) : x / axis.XScale;

        // One point per x: a marker's readout where there is one, else the mean, weighted by how many it stands for.
        var pts = raw.GroupBy(s => s.X).Select(g =>
        {
            var exact = g.FirstOrDefault(s => s.Exact);
            return exact.Exact ? new Point(Var(g.Key), exact.Y, ExactWeight, true)
                               : new Point(Var(g.Key), g.Average(s => s.Y), g.Count(), false);
        }).ToList();
        double vmin = pts[0].V, vmax = pts[^1].V;
        double mid = (vmin + vmax) / 2, half = (vmax - vmin) / 2;

        Model? model = spec.Model switch
        {
            TraceFitModel.Linear => Polynomial(pts, 1, mid, half, "a linear fit"),
            TraceFitModel.Polynomial => Polynomial(pts, Math.Clamp(spec.Degree, 1, TraceFitSpec.MaxDegree), mid, half,
                                                   $"a degree-{spec.Degree.ToString(CultureInfo.InvariantCulture)} polynomial"),
            TraceFitModel.Exponential => Exponential(pts, mid, half),
            _ => null,
        };
        double target = spec.Smoothing ?? axis.Resolution;
        if (spec.Model == TraceFitModel.Spline)
            model = !(target > 0) ? new Model("the reading has no resolution to smooth to: type a smoothing", null, [], 0)
                  : Spline(pts, raw.Select(s => Var(s.X)).ToArray(), raw.Select(s => s.Y).ToArray(), mid, half, target);
        if (model!.Refusal is { } no) return Refuse(no);

        // Residuals against the raw reading, in the trace's units and in pixels.
        var curve = model.Curve!;
        double sum = 0, sumPx = 0, max = 0;
        foreach (var s in raw)
        {
            double r = s.Y - curve(Var(s.X));
            double per = axis.Y?.PerPixel(s.Py) ?? axis.Resolution;
            sum += r * r;
            sumPx += per > 0 ? r * r / (per * per) : double.NaN;
            max = Math.Max(max, Math.Abs(r));
        }
        double rms = Math.Sqrt(sum / raw.Count), rmsPx = Math.Sqrt(sumPx / raw.Count);
        if (spec.Model == TraceFitModel.Exponential && Poor(raw.Select(s => s.Y).ToList(), rms) is { } poor) return Refuse(poor);

        string? expression = model.Coefficients.Count == 0 ? null
            : spec.Model == TraceFitModel.Exponential ? ExponentialText(model.Coefficients, axis.XLog ? $"log10({axis.XVariable})" : axis.XVariable)
            : PolynomialText(model.Coefficients, axis.XLog ? $"log10({axis.XVariable})" : axis.XVariable);
        return new TraceFitResult(spec, model.Coefficients, expression, axis.XVariable, axis.XUnit, rms, max, rmsPx, null)
        {
            YUnit = axis.YUnit,
            LogX = axis.XLog,
            Knots = model.Knots,
            Smoothing = spec.Model == TraceFitModel.Spline ? target : 0,
            SmoothingIsResolution = spec.Model == TraceFitModel.Spline && spec.Smoothing is null,
            XMin = raw[0].X,
            XMax = raw[^1].X,
            XScale = axis.XScale,
            PhasePeriod = axis.PhasePeriod ?? 0,
            Curve = curve,
        };
    }

    /// <summary>The fit at <paramref name="x"/> (the samples' own x: hertz on a frequency axis); NaN outside the trace's
    /// own x range — a fit never extrapolates — and for a fit that was not made.</summary>
    public static double Evaluate(TraceFitResult fit, double x)
    {
        ArgumentNullException.ThrowIfNull(fit);
        if (!fit.Fitted || !double.IsFinite(x)) return double.NaN;
        double slack = 1e-12 * Math.Max(Math.Abs(fit.XMin), Math.Abs(fit.XMax));
        if (x < fit.XMin - slack || x > fit.XMax + slack) return double.NaN;
        double v = x / fit.XScale;
        if (fit.LogX) v = v > 0 ? Math.Log10(v) : double.NaN;
        return fit.Curve!(v);
    }

    /// <summary>The fit at <paramref name="x"/>, a phase fit put on the branch of <paramref name="y"/> (a wrapped trace's
    /// sample) — what the CSV's y_fit and the overlay show beside a reading.</summary>
    public static double EvaluateNear(TraceFitResult fit, double x, double y)
    {
        double v = Evaluate(fit, x);
        return fit.PhasePeriod > 0 && double.IsFinite(v) && double.IsFinite(y) ? v + fit.PhasePeriod * Math.Round((y - v) / fit.PhasePeriod) : v;
    }

    /// <summary>The fit's line in the Touchstone's header and the report.</summary>
    public static string Line(TraceFitResult fit)
    {
        ArgumentNullException.ThrowIfNull(fit);
        string names = string.Join(" + ", fit.Pieces.Count > 0 ? fit.Pieces : [fit.Trace]);
        if (fit.Refusal is { } no) return $"{names} fit: {fit.Spec.Name} refused, {no}; written as read";
        string residual = $"RMS {Num(fit.RmsResidual)}{UnitAfter(fit.YUnit)} ({Px(fit.RmsResidualPx)} px), max {Num(fit.MaxResidual)}{UnitAfter(fit.YUnit)}";
        if (fit.Spec.Model == TraceFitModel.Spline)
            return $"{names} fit: spline, {fit.Knots.ToString(CultureInfo.InvariantCulture)} knots, smoothed to {Num(fit.Smoothing)}{UnitAfter(fit.YUnit)} RMS"
                   + (fit.SmoothingIsResolution ? " (one pixel)" : "") + $"; {residual}";
        var units = new List<string>();
        if (fit.XUnit.Length > 0) units.Add($"{fit.XVariable} in {fit.XUnit}");
        if (fit.YUnit.Length > 0) units.Add($"y in {fit.YUnit}");
        string unit = units.Count > 0 ? $" ({string.Join(", ", units)})" : "";
        return $"{names} fit: {fit.Spec.Name}, {fit.Expression}{unit}; {residual}";
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────────────────────

    private readonly record struct Point(double V, double Y, double W, bool Exact);

    private sealed record Model(string? Refusal, Func<double, double>? Curve, IReadOnlyList<double> Coefficients, int Knots);

    private static TraceFitResult Refused(TraceFitSpec spec, string? why) => new(spec, [], null, "x", "", double.NaN, double.NaN, double.NaN, why);

    private static IReadOnlyList<string> PieceNames(PlotTraceRead t) => t.Pieces.Count > 0 ? [.. t.Pieces.Select(n => $"T{n.ToString(CultureInfo.InvariantCulture)}")] : [t.Name];

    /// <summary>Whether <paramref name="selector"/> names <paramref name="t"/> or any piece joined into it; <c>all</c> names
    /// every trace.</summary>
    private static bool Selects(string selector, PlotTraceRead t)
    {
        string s = selector.Trim();
        if (string.Equals(s, "all", StringComparison.OrdinalIgnoreCase)) return true;
        int at = s.LastIndexOf('@');
        if (at > 0 && string.Equals(s[..at], "all", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s[(at + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p == t.Plot;
        return PlotReading.Selects(s, t) || t.Pieces.Any(n => n != t.Number && PlotReading.Selects(s, t with { Number = n }));
    }

    /// <summary>A weighted least-squares polynomial of degree <paramref name="degree"/>, solved on t = (v − mid)/half by QR
    /// and expanded back to v for display.</summary>
    private static Model Polynomial(List<Point> pts, int degree, double mid, double half, string what)
    {
        if (pts.Count <= degree)
            return new Model($"{what} needs at least {(degree + 1).ToString(CultureInfo.InvariantCulture)} distinct x values; the trace has {pts.Count.ToString(CultureInfo.InvariantCulture)}",
                             null, [], 0);
        var c = LeastSquares(pts, degree, mid, half);
        if (c is null) return new Model($"{what} cannot be solved on these x values", null, [], 0);
        // p(v) = Σ c_k t^k, t = (v − mid)/half  →  Σ a_j v^j, a_j = Σ_{k≥j} c_k C(k,j) (−mid)^(k−j) / half^k.
        var a = new double[degree + 1];
        for (int k = 0; k <= degree; k++)
        {
            double scale = c[k] / Math.Pow(half, k), binom = 1;
            for (int j = 0; j <= k; j++)
            {
                if (j > 0) binom = binom * (k - j + 1) / j;
                a[j] += scale * binom * Math.Pow(-mid, k - j);
            }
        }
        return new Model(null, v => Horner(c, (v - mid) / half), a, 0);
    }

    /// <summary>Centred coefficients c of Σ c_k t^k by Householder QR on √w-scaled rows; null when rank-deficient.</summary>
    private static double[]? LeastSquares(List<Point> pts, int degree, double mid, double half)
    {
        int m = pts.Count, n = degree + 1;
        var a = new double[m, n];
        var b = new double[m];
        for (int i = 0; i < m; i++)
        {
            double sw = Math.Sqrt(pts[i].W), t = (pts[i].V - mid) / half, p = 1;
            for (int k = 0; k < n; k++) { a[i, k] = sw * p; p *= t; }
            b[i] = sw * pts[i].Y;
        }
        for (int j = 0; j < n; j++)
        {
            double norm = 0;
            for (int i = j; i < m; i++) norm += a[i, j] * a[i, j];
            norm = Math.Sqrt(norm);
            if (norm == 0) return null;
            double alpha = a[j, j] > 0 ? -norm : norm;
            var v = new double[m - j];
            for (int i = j; i < m; i++) v[i - j] = a[i, j];
            v[0] -= alpha;
            double vv = 0;
            foreach (double x in v) vv += x * x;
            if (vv == 0) continue;
            for (int k = j; k < n; k++)
            {
                double dot = 0;
                for (int i = j; i < m; i++) dot += v[i - j] * a[i, k];
                double f = 2 * dot / vv;
                for (int i = j; i < m; i++) a[i, k] -= f * v[i - j];
            }
            double db = 0;
            for (int i = j; i < m; i++) db += v[i - j] * b[i];
            double fb = 2 * db / vv;
            for (int i = j; i < m; i++) b[i] -= fb * v[i - j];
        }
        double rmax = 0;
        for (int j = 0; j < n; j++) rmax = Math.Max(rmax, Math.Abs(a[j, j]));
        var c = new double[n];
        for (int j = n - 1; j >= 0; j--)
        {
            if (Math.Abs(a[j, j]) <= 1e-13 * rmax) return null;
            double s = b[j];
            for (int k = j + 1; k < n; k++) s -= a[j, k] * c[k];
            c[j] = s / a[j, j];
        }
        return c;
    }

    private static double Horner(IReadOnlyList<double> c, double t)
    {
        double y = 0;
        for (int k = c.Count - 1; k >= 0; k--) y = y * t + c[k];
        return y;
    }

    /// <summary>
    /// y = a·exp(b·v) + c: from a log-linear fit of y − c₀ (c₀ just beyond the data's extreme on the side its curvature
    /// points to — tried at a few distances, the best kept), refined by Levenberg–Marquardt on the y residuals.
    /// </summary>
    private static Model Exponential(List<Point> pts, double mid, double half)
    {
        if (pts.Count < 4) return new Model("an exponential needs at least 4 distinct x values", null, [], 0);
        double ymin = pts.Min(p => p.Y), ymax = pts.Max(p => p.Y), range = ymax - ymin;
        if (!(range > 0)) return new Model("the trace is flat: there is no exponential in it", null, [], 0);
        var quad = LeastSquares(pts, 2, mid, half);
        if (quad is null || quad[2] == 0) return new Model("the trace has no curvature: it is a straight line, which Linear fits", null, [], 0);
        double side = Math.Sign(quad[2]);
        var t = pts.Select(p => (p.V - mid) / half).ToArray();

        (double A, double B, double C, double Cost, bool Converged)? best = null;
        foreach (double reach in new[] { 0.02, 0.1, 0.5, 2, 10 })
        {
            double c0 = side > 0 ? ymin - reach * range : ymax + reach * range;
            // ln(side·(y − c₀)) = ln|A| + B·t
            var logPts = pts.Select((p, i) => new Point(t[i], Math.Log(side * (p.Y - c0)), p.W, p.Exact)).ToList();
            var line = LeastSquares(logPts, 1, 0, 1);
            if (line is null) continue;
            var got = Marquardt(pts, t, side * Math.Exp(line[0]), line[1], c0);
            if (got is { } g && (best is null || g.Cost < best.Value.Cost)) best = g;
        }
        if (best is not { Converged: true } fit)
            return new Model("Levenberg–Marquardt did not converge: an exponential does not describe this trace", null, [], 0);
        if (Math.Abs(fit.B) < 1e-6)
            return new Model("its rate went to zero: the trace is a straight line, which Linear fits", null, [], 0);
        double b = fit.B / half, a = fit.A * Math.Exp(-fit.B * mid / half), cc = fit.C;
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(cc))
            return new Model("its coefficients overflow on this x range", null, [], 0);
        return new Model(null, v => a * Math.Exp(b * v) + cc, [a, b, cc], 0);
    }

    private static (double A, double B, double C, double Cost, bool Converged)? Marquardt(List<Point> pts, double[] t, double a, double b, double c)
    {
        int n = pts.Count;
        double Cost(double pa, double pb, double pc)
        {
            double s = 0;
            for (int i = 0; i < n; i++)
            {
                double r = pts[i].Y - (pa * Math.Exp(pb * t[i]) + pc);
                s += pts[i].W * r * r;
            }
            return double.IsFinite(s) ? s : double.PositiveInfinity;
        }
        double cost = Cost(a, b, c);
        if (!double.IsFinite(cost)) return null;
        double lambda = 1e-3;
        for (int it = 0; it < 1000; it++)
        {
            var jtj = new double[3, 3];
            var g = new double[3];
            for (int i = 0; i < n; i++)
            {
                double e = Math.Exp(b * t[i]), w = pts[i].W;
                double r = pts[i].Y - (a * e + c);
                double j0 = e, j1 = a * t[i] * e;
                for (int p = 0; p < 3; p++)
                {
                    double jp = p == 0 ? j0 : p == 1 ? j1 : 1;
                    g[p] += w * jp * r;
                    for (int q = 0; q < 3; q++) jtj[p, q] += w * jp * (q == 0 ? j0 : q == 1 ? j1 : 1);
                }
            }
            while (true)
            {
                var m = new double[3, 3];
                for (int p = 0; p < 3; p++)
                    for (int q = 0; q < 3; q++) m[p, q] = jtj[p, q] + (p == q ? lambda * Math.Max(jtj[p, p], 1e-300) : 0);
                var d = Solve3(m, g);
                if (d is null) { lambda *= 10; if (lambda > 1e16) return (a, b, c, cost, true); continue; }
                double na = a + d[0], nb = b + d[1], nc = c + d[2];
                double next = Cost(na, nb, nc);
                if (next < cost)
                {
                    double step = Math.Abs(d[0]) / (Math.Abs(a) + 1e-300) + Math.Abs(d[1]) / (Math.Abs(b) + 1e-12) + Math.Abs(d[2]) / (Math.Abs(c) + Math.Abs(a) + 1e-300);
                    bool small = step < 1e-14 || cost - next <= 1e-15 * cost;
                    (a, b, c, cost) = (na, nb, nc, next);
                    lambda = Math.Max(lambda / 10, 1e-15);
                    if (small || cost == 0) return (a, b, c, cost, true);
                    break;
                }
                lambda *= 10;
                // No step lowers the cost any further: the minimum, to the precision there is.
                if (lambda > 1e16) return (a, b, c, cost, true);
            }
        }
        return (a, b, c, cost, false);
    }

    private static double[]? Solve3(double[,] m, double[] g)
    {
        var a = (double[,])m.Clone();
        var x = (double[])g.Clone();
        for (int col = 0; col < 3; col++)
        {
            int piv = col;
            for (int r = col + 1; r < 3; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            if (a[piv, col] == 0 || !double.IsFinite(a[piv, col])) return null;
            if (piv != col)
            {
                for (int k = 0; k < 3; k++) (a[col, k], a[piv, k]) = (a[piv, k], a[col, k]);
                (x[col], x[piv]) = (x[piv], x[col]);
            }
            for (int r = col + 1; r < 3; r++)
            {
                double f = a[r, col] / a[col, col];
                for (int k = col; k < 3; k++) a[r, k] -= f * a[col, k];
                x[r] -= f * x[col];
            }
        }
        var d = new double[3];
        for (int r = 2; r >= 0; r--)
        {
            double s = x[r];
            for (int k = r + 1; k < 3; k++) s -= a[r, k] * d[k];
            d[r] = s / a[r, r];
        }
        return d.All(double.IsFinite) ? d : null;
    }

    /// <summary>An exponential that explains too little of the trace's variation is the wrong model, said rather than returned.</summary>
    private static string? Poor(List<double> y, double rms)
    {
        double mean = y.Average();
        double sd = Math.Sqrt(y.Sum(v => (v - mean) * (v - mean)) / y.Count);
        if (!(sd > 0) || rms <= 0.3 * sd) return null;
        double explained = Math.Max(0, 1 - rms * rms / (sd * sd));
        return $"it explains only {(100 * explained).ToString("0", CultureInfo.InvariantCulture)} % of the trace's variation: an exponential is the wrong model";
    }

    /// <summary>
    /// A cubic smoothing spline (Reinsch): minimises Σ wᵢ(yᵢ − g(tᵢ))² + α∫g″², a marker's readout with σ = 0 so g passes
    /// through it. α is chosen so the RMS residual against the raw reading is <paramref name="target"/>.
    /// </summary>
    private static Model Spline(List<Point> pts, double[] rawV, double[] rawY, double mid, double half, double target)
    {
        int n = pts.Count;
        if (n < 3) return new Model("a spline needs at least 3 distinct x values", null, [], 0);
        var t = pts.Select(p => (p.V - mid) / half).ToArray();
        var y = pts.Select(p => p.Y).ToArray();
        var s2 = pts.Select(p => p.Exact ? 0 : 1 / p.W).ToArray();
        var h = new double[n - 1];
        for (int i = 0; i < n - 1; i++) h[i] = t[i + 1] - t[i];
        // Each raw sample's knot, for the residual against the raw reading.
        var knotOf = new int[rawV.Length];
        for (int i = 0, k = 0; i < rawV.Length; i++)
        {
            while (k < n - 1 && Math.Abs(pts[k].V - rawV[i]) > Math.Abs(pts[k + 1].V - rawV[i])) k++;
            knotOf[i] = k;
        }

        (double[] G, double[] Gamma) Solve(double alpha)
        {
            int m = n - 2;
            var d = new double[m];
            var e = new double[Math.Max(0, m - 1)];
            var f = new double[Math.Max(0, m - 2)];
            var rhs = new double[m];
            for (int c = 0; c < m; c++)
            {
                int k = c + 1;
                d[c] = (h[k - 1] + h[k]) / 3;
                if (c + 1 < m) e[c] = h[k] / 6;
                rhs[c] = (y[k + 1] - y[k]) / h[k] - (y[k] - y[k - 1]) / h[k - 1];
            }
            // + α·Qᵀ diag(σ²) Q: row i of Q touches columns i−2 … i.
            for (int i = 0; i < n; i++)
            {
                if (s2[i] == 0) continue;
                for (int c1 = Math.Max(0, i - 2); c1 <= Math.Min(m - 1, i); c1++)
                {
                    double q1 = Q(i, c1);
                    for (int c2 = c1; c2 <= Math.Min(m - 1, i); c2++)
                    {
                        double v = alpha * s2[i] * q1 * Q(i, c2);
                        switch (c2 - c1)
                        {
                            case 0: d[c1] += v; break;
                            case 1: e[c1] += v; break;
                            default: f[c1] += v; break;
                        }
                    }
                }
            }
            var gamma = Pentadiagonal(d, e, f, rhs);
            var g = new double[n];
            for (int i = 0; i < n; i++)
            {
                double qg = 0;
                for (int c = Math.Max(0, i - 2); c <= Math.Min(m - 1, i); c++) qg += Q(i, c) * gamma[c];
                g[i] = y[i] - alpha * s2[i] * qg;
            }
            var full = new double[n];
            Array.Copy(gamma, 0, full, 1, m);
            return (g, full);
        }
        // Q[i, c], c the interior knot c + 1.
        double Q(int i, int c)
        {
            int k = c + 1;
            return i == k - 1 ? 1 / h[k - 1] : i == k ? -1 / h[k - 1] - 1 / h[k] : i == k + 1 ? 1 / h[k] : 0;
        }
        double Rms((double[] G, double[] Gamma) s)
        {
            double sum = 0;
            for (int i = 0; i < rawY.Length; i++) { double r = rawY[i] - s.G[knotOf[i]]; sum += r * r; }
            return Math.Sqrt(sum / rawY.Length);
        }

        // RMS rises with α, from the interpolating spline's to the straight line's: bisect log α onto the target.
        double lo = -20, hi = 12;
        var chosen = Solve(Math.Pow(10, hi));
        if (Rms(chosen) > target)
        {
            chosen = Solve(Math.Pow(10, lo));
            if (Rms(chosen) < target)
                for (int it = 0; it < 80; it++)
                {
                    double at = (lo + hi) / 2;
                    var s = Solve(Math.Pow(10, at));
                    if (Rms(s) < target) lo = at; else hi = at;
                    chosen = s;
                    if (hi - lo < 1e-9) break;
                }
        }
        var (gv, gm) = chosen;
        double Curve(double v)
        {
            double tv = (v - mid) / half;
            if (tv <= t[0]) return gv[0];
            if (tv >= t[^1]) return gv[^1];
            int i = Array.BinarySearch(t, tv);
            if (i >= 0) return gv[i];
            i = ~i - 1;
            double hh = t[i + 1] - t[i], u = tv - t[i], w = t[i + 1] - tv;
            return (u * gv[i + 1] + w * gv[i]) / hh - u * w / 6 * ((1 + u / hh) * gm[i + 1] + (1 + w / hh) * gm[i]);
        }
        return new Model(null, Curve, [], n);
    }

    /// <summary>Solves the symmetric pentadiagonal system (diagonal d, first and second off-diagonals e and f) by LDLᵀ.</summary>
    private static double[] Pentadiagonal(double[] d, double[] e, double[] f, double[] rhs)
    {
        int m = d.Length;
        var dd = new double[m];
        var l1 = new double[m];
        var l2 = new double[m];
        for (int i = 0; i < m; i++)
        {
            double di = d[i];
            if (i >= 1) di -= l1[i - 1] * l1[i - 1] * dd[i - 1];
            if (i >= 2) di -= l2[i - 2] * l2[i - 2] * dd[i - 2];
            dd[i] = di;
            if (i + 1 < m) l1[i] = (e[i] - (i >= 1 ? l2[i - 1] * l1[i - 1] * dd[i - 1] : 0)) / di;
            if (i + 2 < m) l2[i] = f[i] / di;
        }
        var z = new double[m];
        for (int i = 0; i < m; i++)
        {
            double v = rhs[i];
            if (i >= 1) v -= l1[i - 1] * z[i - 1];
            if (i >= 2) v -= l2[i - 2] * z[i - 2];
            z[i] = v;
        }
        for (int i = 0; i < m; i++) z[i] /= dd[i];
        var x = new double[m];
        for (int i = m - 1; i >= 0; i--)
        {
            double v = z[i];
            if (i + 1 < m) v -= l1[i] * x[i + 1];
            if (i + 2 < m) v -= l2[i] * x[i + 2];
            x[i] = v;
        }
        return x;
    }

    // ── the expression (R-pdg2-2) ────────────────────────────────────────────────────────────────────────

    /// <summary><c>-0.512 + 0.0123*f - 0.00041*f^2</c>: every term's sign is the operator before it, so no coefficient is
    /// ever a negative literal raised to a power.</summary>
    internal static string PolynomialText(IReadOnlyList<double> a, string variable)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < a.Count; k++)
        {
            if (a[k] == 0 && (k > 0 || a.Skip(1).Any(c => c != 0))) continue;
            string power = k switch { 0 => "", 1 => "*" + variable, _ => $"*{variable}^{k.ToString(CultureInfo.InvariantCulture)}" };
            Term(sb, a[k], power);
        }
        return sb.Length == 0 ? "0" : sb.ToString();
    }

    /// <summary><c>3.2*exp(-0.81*f) + 0.05</c>.</summary>
    internal static string ExponentialText(IReadOnlyList<double> abc, string variable)
    {
        var sb = new StringBuilder();
        Term(sb, abc[0], $"*exp({R(abc[1])}*{variable})");
        if (abc[2] != 0) Term(sb, abc[2], "");
        return sb.ToString();
    }

    private static void Term(StringBuilder sb, double c, string suffix)
    {
        if (sb.Length == 0) sb.Append(R(c));
        else sb.Append(c < 0 ? " - " : " + ").Append(R(Math.Abs(c)));
        sb.Append(suffix);
    }

    /// <summary>Round-trip precision: the copied text reproduces the fit.</summary>
    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    internal static string Num(double v) => double.IsFinite(v) ? v.ToString("G3", CultureInfo.InvariantCulture) : "?";

    internal static string Px(double v) => double.IsFinite(v) ? v.ToString("0.0#", CultureInfo.InvariantCulture) : "?";

    /// <summary>A unit after a value: <c> dB</c>, <c>°</c> (no space), or nothing.</summary>
    internal static string UnitAfter(string unit) => unit.Length == 0 ? "" : unit == "°" ? unit : " " + unit;

    private static string FrequencyUnit(double multiplier) => multiplier switch
    {
        >= 1e12 => "THz",
        >= 1e9 => "GHz",
        >= 1e6 => "MHz",
        >= 1e3 => "kHz",
        _ => "Hz",
    };
}
