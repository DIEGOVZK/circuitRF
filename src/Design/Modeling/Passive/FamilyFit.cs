// A family of passive blocks over one geometry value — turns, a length — fitted as one topology whose element values are
// smooth functions of that value (brief-modelcomposer-12 R-mc12-6).
//
//   1. Each training geometry is fitted on its own, in ascending order, each started from its neighbour's solution and
//      from its own analytic starting values, whichever lands lower (a short search when both land poorly).
//   2. Each fitted variable becomes a law of the geometry: a polynomial of degree ≤ 3, or (a0 + a1·x)/(1 + b1·x), in the
//      normalised geometry x = (g − mid)/half — chosen by LEAVE-ONE-OUT error over the training geometries, the simplest
//      form within 2 % of the best winning; monotone over the range when asked, and never below an element's own minimum.
//   3. A joint refinement: every law's coefficients together, by Levenberg–Marquardt, against every training block's S.
//   4. The held-out geometry — the middle one unless the user names another — is predicted by the laws and compared with
//      its own block. It never trained anything.
//
// The family is valid over the training range; the exported cell wraps the geometry parameter in validrange(…), which
// says so at elaboration when a schematic asks for a value outside it.

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;
using static CircuitRF.Design.Modeling.CmcExtrinsic;

namespace CircuitRF.Design.Modeling.Passive;

/// <summary>The form of an element's law in the normalised geometry.</summary>
public enum FamilyForm { Auto, Constant, Linear, Quadratic, Cubic, Rational }

/// <summary>How a family is fitted (R-mc12-6).</summary>
public sealed record FamilySettings
{
    /// <summary>The geometry parameter's name — the exported cell's parameter (<c>turns</c>).</summary>
    public string Geometry { get; init; } = "g";
    public FamilyForm Form { get; init; } = FamilyForm.Auto;
    /// <summary>Every law monotone over the range.</summary>
    public bool Monotone { get; init; }
    /// <summary>The held-out geometry's value; null is the middle one.</summary>
    public double? HeldOut { get; init; }

    public static FamilySettings Default { get; } = new();

    private static readonly string[] Keys = ["Geometry", "Form", "Monotone", "HeldOut"];

    public static FamilySettings Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        string form = Str(o, "Form", $"{f}.Form", required: false) ?? nameof(FamilyForm.Auto);
        if (!Enum.TryParse<FamilyForm>(form, ignoreCase: false, out var ff) || !Enum.IsDefined(ff))
            throw new CmcFormatException($"{f}.Form", $"'{form}' is not one of {string.Join(", ", Enum.GetNames<FamilyForm>())}.");
        var s = new FamilySettings
        {
            Geometry = Str(o, "Geometry", $"{f}.Geometry")!,
            Form = ff,
            Monotone = Bool(o, "Monotone", $"{f}.Monotone"),
            HeldOut = Num(o, "HeldOut", $"{f}.HeldOut"),
        };
        if (!IsIdentifier(s.Geometry)) throw new CmcFormatException($"{f}.Geometry", $"'{s.Geometry}' is not a parameter name.");
        return s;
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject { ["Geometry"] = Geometry };
        if (Form != FamilyForm.Auto) o["Form"] = Form.ToString();
        if (Monotone) o["Monotone"] = true;
        if (HeldOut is { } h) o["HeldOut"] = h;
        return o;
    }

    internal static bool IsIdentifier(string t) =>
        t.Length > 0 && (char.IsLetter(t[0]) || t[0] == '_') && t.All(c => char.IsLetterOrDigit(c) || c == '_');
}

/// <summary>
/// One variable's law: its form and coefficients in <c>x = (g − Mid)/Half</c> — a polynomial's <c>c0…cd</c>, or a
/// rational's <c>a0, a1, b1</c> — and its leave-one-out error relative to the variable's largest value.
/// </summary>
public sealed record ElementLaw(string Variable, FamilyForm Form, double[] Coefficients, double Mid, double Half, double LeaveOneOutError)
{
    public double Evaluate(double g) => At(Form, Coefficients, Half == 0 ? 0 : (g - Mid) / Half);

    internal static double At(FamilyForm form, double[] c, double x)
    {
        if (form == FamilyForm.Rational) return (c[0] + c[1] * x) / (1 + c[2] * x);
        double v = 0;
        for (int k = c.Length - 1; k >= 0; k--) v = v * x + c[k];
        return v;
    }

    /// <summary>The law as an expression of <paramref name="geometry"/> (itself an expression).</summary>
    public string Expression(string geometry)
    {
        string x = Half == 0 ? "0" : $"((({geometry})-{R(Mid)})/{R(Half)})";
        if (Form == FamilyForm.Rational)
            return $"({R(Coefficients[0])}{Term(Coefficients[1], x, 1)})/(1{Term(Coefficients[2], x, 1)})";
        var sb = new System.Text.StringBuilder(R(Coefficients[0]));
        for (int k = 1; k < Coefficients.Length; k++) sb.Append(Term(Coefficients[k], x, k));
        return sb.ToString();
    }

    private static string Term(double c, string x, int power) =>
        (c < 0 ? "-" : "+") + R(Math.Abs(c)) + "*" + (power == 1 ? x : $"{x}^{power}");

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>A fitted family.</summary>
public sealed record FamilyFitResult(
    string Topology,
    FamilySettings Settings,
    /// <summary>The training range of the geometry — what the family is valid over.</summary>
    double Lo, double Hi,
    IReadOnlyList<(double Geometry, PassiveFitResult Fit)> PerGeometry,
    /// <summary>A law per fitted variable.</summary>
    IReadOnlyList<ElementLaw> Laws,
    /// <summary>Every variable that is not fitted — locked, or tied to another as <c>value(To) × Factor</c>.</summary>
    IReadOnlyDictionary<string, double> Locked,
    IReadOnlyDictionary<string, VariableTie> Ties,
    double HeldOutGeometry, double HeldOutRms, double HeldOutMax,
    /// <summary>√(mean |ΔS|²) over the training geometries: after the independent fits, and through the laws.</summary>
    double IndependentRms, double TrainingRms,
    IReadOnlyList<string> Lines)
{
    /// <summary>Every variable's value at geometry <paramref name="g"/>, in base SI.</summary>
    public IReadOnlyDictionary<string, double> ValuesAt(double g)
    {
        var d = new Dictionary<string, double>(Locked, StringComparer.Ordinal);
        foreach (var l in Laws) d[l.Variable] = l.Evaluate(g);
        foreach (var (name, tie) in Ties) d[name] = d[tie.To] * tie.Factor;
        return d;
    }
}

public static class FamilyFit
{
    /// <summary>Training geometries a family needs — enough for a leave-one-out choice — besides the held-out one.</summary>
    public const int MinTraining = 3;

    /// <summary>A family of a library topology: its starting values and positive variables come with it.</summary>
    public static FamilyFitResult Run(IReadOnlyList<PassiveBlock> blocks, PassiveTopology topology, FamilySettings family,
                                      PassiveFitSettings? settings = null,
                                      IReadOnlyDictionary<string, NetworkVariableSettings>? variables = null, RunControl? rc = null) =>
        Run(blocks, topology.Load(), family, settings, variables, topology.Seed, topology.Positive, topology.Id, rc);

    /// <summary>
    /// Fits a family (R-mc12-6). <paramref name="seed"/> gives a geometry's starting values from its analysis (null starts
    /// from the drawing's values). Throws <see cref="ModelingException"/> for a refusal and
    /// <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static FamilyFitResult Run(IReadOnlyList<PassiveBlock> blocks, PassiveNetwork network, FamilySettings family,
                                      PassiveFitSettings? settings, IReadOnlyDictionary<string, NetworkVariableSettings>? variables,
                                      Func<PassiveAnalysisResult, IReadOnlyDictionary<string, double>>? seed,
                                      IReadOnlyCollection<string>? positive, string? topology, RunControl? rc)
    {
        settings ??= PassiveFitSettings.Default;
        variables ??= new Dictionary<string, NetworkVariableSettings>();
        topology ??= Path.GetFileNameWithoutExtension(network.SchematicPath);
        string geo = family.Geometry;
        if (network.Variables.Any(v => v.Name == geo))
            throw new ModelingException("Geometry", $"'{geo}' is already a variable of {topology}; name the geometry otherwise.");

        // The blocks, in geometry order, and the held-out one.
        foreach (var b in blocks)
            if (!double.IsFinite(b.Geometry)) throw new ModelingException(b.Name, $"it has no {geo} value; every block of a family carries one.");
        var sorted = blocks.OrderBy(b => b.Geometry).ToList();
        for (int i = 1; i < sorted.Count; i++)
            if (sorted[i].Geometry == sorted[i - 1].Geometry)
                throw new ModelingException(sorted[i].Name, $"its {geo} = {G(sorted[i].Geometry)} is also {sorted[i - 1].Name}'s.");
        if (sorted.Count < MinTraining + 1)
            throw new ModelingException("Family", $"it has {sorted.Count} geometries; a family needs {MinTraining} to train and one held out.");
        var held = family.HeldOut is { } h
            ? sorted.FirstOrDefault(b => Math.Abs(b.Geometry - h) <= 1e-9 * Math.Max(1, Math.Abs(h)))
              ?? throw new ModelingException("HeldOut", $"no block has {geo} = {G(h)}.")
            : sorted[(sorted.Count - 1) / 2];
        var train = sorted.Where(b => b != held).ToList();

        // 1. Each training geometry on its own.
        var per = new List<(double, PassiveFitResult)>();
        PassiveFitResult? prev = null;
        foreach (var b in train)
        {
            rc?.ThrowIfCancellationRequested();
            var own = seed?.Invoke(PassiveAnalysis.Run(b, rc));   // its vector fit has a stage of its own; this one follows
            rc?.BeginStage($"{geo} = {G(b.Geometry)}", 0, "evaluation(s)");
            PassiveFitResult best;
            if (prev is null)
            {
                best = PassiveFit.Fit(b, network, settings, variables, own, positive, PassiveFit.Depth.Quick, topology, rc);
                // A variable locked with no value is locked at its start, and a geometry's own analytic start differs from
                // the first's: every geometry holds it where the first did, the value the family's cell carries.
                variables = PinLocked(variables, best.Values);
            }
            else
            {
                best = PassiveFit.Fit(b, network, settings, variables, prev.Values, positive, PassiveFit.Depth.Polish, topology, rc);
                if (own is not null)
                {
                    var alt = PassiveFit.Fit(b, network, settings, variables, own, positive, PassiveFit.Depth.Polish, topology, rc);
                    if (alt.Cost < best.Cost) best = alt;
                }
                if (best.RmsDeltaS > 10 * per[0].Item2.RmsDeltaS + 1e-4)
                {
                    var deep = PassiveFit.Fit(b, network, settings, variables, best.Values, positive, PassiveFit.Depth.Quick, topology, rc);
                    if (deep.Cost < best.Cost) best = deep;
                }
            }
            per.Add((b.Geometry, best));
            prev = best;
        }

        // 2. A law per fitted variable.
        var notes = new List<string>();
        var fv = PassiveFit.Variables(network, variables, per[0].Item2.Values, positive, notes);
        double lo = train[0].Geometry, hi = train[^1].Geometry, mid = (lo + hi) / 2, half = (hi - lo) / 2;
        double[] x = [.. train.Select(b => (b.Geometry - mid) / half)];
        var laws = new List<ElementLaw>();
        foreach (var c in fv.Free)
        {
            double[] v = [.. per.Select(p => p.Item2.Values[c.Name])];
            laws.Add(ChooseLaw(c, x, v, mid, half, family));
        }
        var locked = new Dictionary<string, double>(StringComparer.Ordinal);
        var ties = new Dictionary<string, VariableTie>(StringComparer.Ordinal);
        foreach (var v in network.Variables)
        {
            var s = variables.GetValueOrDefault(v.Name);
            if (s?.Tie is { } t) ties[v.Name] = t;
            else if (laws.All(l => l.Variable != v.Name)) locked[v.Name] = per[0].Item2.Values[v.Name];
        }

        // 3. The joint refinement over every law's coefficients.
        rc?.BeginStage("Joint refinement", 0, "evaluation(s)");
        var coords = new List<FitCoordinate>();
        var owner = new List<(int Law, int K)>();
        for (int j = 0; j < laws.Count; j++)
        {
            double scale = per.Max(p => Math.Abs(p.Item2.Values[laws[j].Variable]));
            for (int k = 0; k < laws[j].Coefficients.Length; k++)
            {
                double cval = laws[j].Coefficients[k];
                double span = 4 * Math.Max(Math.Abs(cval), laws[j].Form == FamilyForm.Rational && k == 2 ? 0.25 : 0.1 * scale);
                if (laws[j].Form == FamilyForm.Rational && k == 2) span = Math.Min(span, 0.95 - Math.Abs(cval) + 1e-12);
                if (!(span > 0)) span = 1e-30;                // a value fitted to exactly 0 everywhere: still a box, not a point
                coords.Add(new FitCoordinate(coords.Count, $"{laws[j].Variable}.c{k}", NetworkElementKind.Other, cval - span, cval + span, false));
                owner.Add((j, k));
            }
        }
        List<ElementLaw> Laws(double[] coef)
        {
            var list = laws.Select(l => l with { Coefficients = (double[])l.Coefficients.Clone() }).ToList();
            for (int i = 0; i < coef.Length; i++) list[owner[i].Law].Coefficients[owner[i].K] = coef[i];
            return list;
        }
        IReadOnlyDictionary<string, double> At(List<ElementLaw> ls, double g, bool strict = true)
        {
            var free = new double[fv.Free.Length];
            for (int j = 0; j < free.Length; j++)
            {
                double val = ls[j].Evaluate(g);
                if (strict && !(val >= fv.Free[j].Min && val <= fv.Free[j].Max)) throw new ArithmeticException();
                free[j] = val;
            }
            return fv.Named(fv.Compose(free));
        }
        double[] Residual(double[] coef)
        {
            var ls = Laws(coef);
            var parts = train.Select(b => PassiveFit.Residual(network.S(b.FrequenciesHz, b.Z0, At(ls, b.Geometry)), b,
                                                              settings.Weights(b.FrequenciesHz))).ToList();
            return [.. parts.SelectMany(p => p)];
        }
        var lsq = new BoxLeastSquares([.. coords], Residual, new EvaluationCounter());
        double[] start = [.. laws.SelectMany(l => l.Coefficients)];
        var refined = lsq.Polish(start, lsq.All, rc);
        laws = Laws(refined.Free);
        rc?.ThrowIfCancellationRequested();

        // 4. The held-out geometry, and the training error through the laws. A held-out geometry the user put at an end of
        //    the range is an extrapolation, and is predicted as one: the laws as they are, whatever bounds they cross.
        Complex[][,] heldS;
        try { heldS = network.S(held.FrequenciesHz, held.Z0, At(laws, held.Geometry, strict: false)); }
        catch (Exception ex) when (BoxLeastSquares.Failure(ex))
        {
            throw new ModelingException("HeldOut", $"the laws do not solve at the held-out {geo} = {G(held.Geometry)} ({ex.Message}); " +
                                                   $"hold out a geometry inside {G(lo)} to {G(hi)}.");
        }
        var (heldRms, heldMax) = PassiveFit.DeltaS(heldS, held);
        double Rms(IEnumerable<double> r) { var a = r.ToArray(); return Math.Sqrt(a.Sum(v => v * v) / a.Length); }
        double independent = Rms(per.Select(p => p.Item2.RmsDeltaS));
        double training = Rms(train.Select(b => PassiveFit.DeltaS(network.S(b.FrequenciesHz, b.Z0, At(laws, b.Geometry)), b).Rms));

        var lines = new List<string>
        {
            $"Family over {geo} = {G(lo)} to {G(hi)}: {train.Count} geometries trained, {geo} = {G(held.Geometry)} held out",
        };
        foreach (var l in laws)
            lines.Add($"{l.Variable}: {Describe(l.Form, l.Coefficients.Length)}, leave-one-out error {Pct(l.LeaveOneOutError)}");
        lines.Add($"Held-out {geo} = {G(held.Geometry)}: rms |ΔS| {E(heldRms)}, max {E(heldMax)}");
        lines.Add($"Training geometries: rms |ΔS| {E(training)} through the laws, {E(independent)} fitted one by one");
        lines.AddRange(notes);
        return new FamilyFitResult(topology, family, lo, hi, per, laws, locked, ties, held.Geometry, heldRms, heldMax,
                                   independent, training, lines);
    }

    /// <summary>Every variable locked without a value, given <paramref name="values"/>' value for it.</summary>
    private static IReadOnlyDictionary<string, NetworkVariableSettings> PinLocked(
        IReadOnlyDictionary<string, NetworkVariableSettings> variables, IReadOnlyDictionary<string, double> values)
    {
        var d = new Dictionary<string, NetworkVariableSettings>(variables, StringComparer.Ordinal);
        foreach (var (name, v) in variables)
            if (v is { Lock: true, Tie: null, Value: null } && values.TryGetValue(name, out double x)) d[name] = v with { Value = x };
        return d;
    }

    // ── Laws ──────────────────────────────────────────────────────────────────

    private static ElementLaw ChooseLaw(FitCoordinate c, double[] x, double[] v, double mid, double half, FamilySettings family)
    {
        FamilyForm[] forms = family.Form == FamilyForm.Auto
            ? [FamilyForm.Constant, FamilyForm.Linear, FamilyForm.Quadratic, FamilyForm.Rational, FamilyForm.Cubic]
            : [family.Form];
        double scale = Math.Max(v.Max(Math.Abs), 1e-300);
        var scored = new List<(FamilyForm Form, double[] Coef, double Loo)>();
        foreach (var form in forms)
        {
            int k = Count(form);
            if (x.Length - 1 < k) continue;                   // leave-one-out needs k points without the one left out
            if (Solve(form, x, v) is not { } coef || !Admissible(form, coef, c, family.Monotone)) continue;
            double sq = 0;
            bool ok = true;
            for (int i = 0; i < x.Length && ok; i++)
            {
                var xs = x.Where((_, j) => j != i).ToArray();
                var vs = v.Where((_, j) => j != i).ToArray();
                if (Solve(form, xs, vs) is not { } cv) { ok = false; break; }
                double d = ElementLaw.At(form, cv, x[i]) - v[i];
                sq += d * d;
            }
            if (ok) scored.Add((form, coef, Math.Sqrt(sq / x.Length) / scale));
        }
        if (scored.Count == 0)
        {
            if (family.Form != FamilyForm.Auto)
                throw new ModelingException(c.Name, $"no {family.Form} law fits it over {x.Length} training geometries{(family.Monotone ? " and stays monotone" : "")}.");
            return new ElementLaw(c.Name, FamilyForm.Constant, [v.Average()], mid, half, double.NaN);
        }
        double best = scored.Min(s => s.Loo);
        var pick = scored.OrderBy(s => Count(s.Form)).First(s => s.Loo <= 1.02 * best + 1e-12);
        return new ElementLaw(c.Name, pick.Form, pick.Coef, mid, half, pick.Loo);
    }

    private static int Count(FamilyForm f) => f switch
    {
        FamilyForm.Constant => 1, FamilyForm.Linear => 2, FamilyForm.Quadratic => 3, FamilyForm.Rational => 3, FamilyForm.Cubic => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    /// <summary>The law's coefficients by least squares; a rational by its linearisation v(1 + b·x) = a0 + a1·x.</summary>
    private static double[]? Solve(FamilyForm form, double[] x, double[] v)
    {
        int k = Count(form), n = x.Length;
        if (n < k) return null;
        var a = new double[n, k];
        for (int i = 0; i < n; i++)
        {
            if (form == FamilyForm.Rational) { a[i, 0] = 1; a[i, 1] = x[i]; a[i, 2] = -x[i] * v[i]; continue; }
            double p = 1;
            for (int j = 0; j < k; j++) { a[i, j] = p; p *= x[i]; }
        }
        var c = LeastSquares.Solve(a, v);
        return c.All(double.IsFinite) ? c : null;
    }

    /// <summary>A law is admissible when a rational has no pole in the range, it stays inside the variable's bounds, and —
    /// when asked — it is monotone over the range.</summary>
    private static bool Admissible(FamilyForm form, double[] c, FitCoordinate coord, bool monotone)
    {
        if (form == FamilyForm.Rational && !(Math.Abs(c[2]) < 0.95)) return false;
        double prev = double.NaN;
        int sign = 0;
        for (int i = 0; i <= 200; i++)
        {
            double x = -1 + i / 100.0, y = ElementLaw.At(form, c, x);
            if (!(y >= coord.Min && y <= coord.Max)) return false;
            if (monotone && !double.IsNaN(prev))
            {
                int s = Math.Sign(y - prev);
                if (s != 0 && sign != 0 && s != sign) return false;
                if (s != 0) sign = s;
            }
            prev = y;
        }
        return true;
    }

    private static string Describe(FamilyForm f, int n) => f switch
    {
        FamilyForm.Constant => "constant", FamilyForm.Linear => "linear", FamilyForm.Quadratic => "quadratic",
        FamilyForm.Cubic => "cubic", FamilyForm.Rational => "rational (a0 + a1·x)/(1 + b1·x)", _ => $"{n} coefficients",
    };

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    private static string E(double v) => v.ToString("0.0E+0", CultureInfo.InvariantCulture);
    private static string Pct(double v) => double.IsNaN(v) ? "not measured" : (100 * v).ToString("0.###", CultureInfo.InvariantCulture) + " %";
}
