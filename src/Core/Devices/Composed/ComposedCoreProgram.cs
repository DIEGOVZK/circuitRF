using System.Globalization;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// The constituents of one <c>.cmodel</c>, compiled ONCE when the file is loaded and shared by every
/// <see cref="ComposedCoreModel"/> built on it (R-mc1-3, R-mc2-1/2/3).
///
/// <para><b>Inputs are seeds.</b> <c>vgs</c> and <c>vds</c> always, and <c>T</c> when the model is
/// over a temperature range, occupy the first gradient lanes in that order, so one evaluation returns
/// a constituent's value and its derivative by each. An equation runs through the SDD's own
/// dual-number arithmetic (<see cref="CompiledSddExpr.CompileNamed"/>); a table or an ANN runs through
/// its evaluator inside its <see cref="GuidedExtrapolation"/>, its inputs gathered from the seeds and
/// its gradient scattered back into their lanes.</para>
/// </summary>
public sealed class ComposedCoreProgram
{
    /// <summary>The intrinsic gate–source voltage, V.</summary>
    public const string Vgs = "vgs";
    /// <summary>The intrinsic drain–source voltage, V.</summary>
    public const string Vds = "vds";
    /// <summary>The device temperature, °C — an input only of a <see cref="TemperatureMode.Range"/> model.</summary>
    public const string T   = "T";

    /// <summary>The seed names in lane order.</summary>
    public IReadOnlyList<string> Seeds { get; }

    /// <summary>Gradient width: 2, or 3 with <c>T</c>.</summary>
    public int Width => Seeds.Count;

    /// <summary>The drain–source symmetry option (R-mc2-4).</summary>
    public DrainSourceSymmetry Symmetry { get; }

    /// <summary>[P1] eq. 6.52's time constant, seconds, when the model carries <c>IdRF</c>; else null.</summary>
    public double? DispersionTau { get; }

    // One compiled function per payload entry, in a fixed order the core reads by field. IdRF is null
    // unless the model is dispersive, and then Id is the DC current.
    internal readonly ComposedFunction Id, IgGs, IgGd, Qg, Qd;
    internal readonly ComposedFunction? IdRF;

    /// <summary>The drift decomposition when <c>Qg</c> and <c>Qd</c> are <c>Drift</c> (MC-8); the core then takes the
    /// charges from it, and <see cref="Qg"/>/<see cref="Qd"/> are zero.</summary>
    public DriftCharges? Drift { get; }

    private readonly string _label;

    private ComposedCoreProgram(IReadOnlyList<string> seeds, string label, DrainSourceSymmetry symmetry,
                                ComposedFunction id, ComposedFunction igGs, ComposedFunction igGd,
                                ComposedFunction qg, ComposedFunction qd, ComposedFunction? idRf, double? tau,
                                DriftCharges? drift)
    {
        Seeds = seeds; _label = label; Symmetry = symmetry;
        Id = id; IgGs = igGs; IgGd = igGd; Qg = qg; Qd = qd; IdRF = idRf; DispersionTau = tau; Drift = drift;
    }

    /// <summary>Compiles a model already checked by <see cref="CmodelFile.Validate"/>.</summary>
    public static ComposedCoreProgram Compile(ComposedModel model, string label)
    {
        IReadOnlyList<string> seeds = model.Temperature.Mode == TemperatureMode.Range
            ? [Vgs, Vds, T]
            : [Vgs, Vds];

        ComposedFunction One(Constituent c, string name, string key, double[]? ownBranch)
        {
            if (c.Method == Constituent.DriftMethod) return ZeroFunction.Instance;
            if (c.Method == Constituent.EquationMethod)
            {
                var parameters = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var p in c.Parameters) parameters[p.Key] = p.Value;
                return new EquationFunction(
                    CompiledSddExpr.CompileNamed(Parser.Parse(c.Expression(key)!), seeds, parameters, label), label);
            }

            var evaluator = RuntimePayload.Build(model, c, key, $"Core.Constituents.{name}.{key}");
            double gx = ownBranch is null ? 0.0 : model.Extrapolation!.Gx[GxKey(name, key)];
            return new RuntimeFunction(
                new GuidedExtrapolation(evaluator, c.Domain!, c.Inputs, gx, ownBranch), c.Inputs, seeds);
        }

        var k = model.Core;
        return new ComposedCoreProgram(seeds, label, k.Symmetry,
            One(k.Id, "Id", k.Id.PayloadKey, Branch(k.Id.Inputs, vgs: 0, vds: 1)),
            One(k.Ig, "Ig", "IgGs", Branch(k.Ig.Inputs, vgs: 1, vds: 0)),
            One(k.Ig, "Ig", "IgGd", Branch(k.Ig.Inputs, vgs: 1, vds: -1)),
            One(k.Qg, "Qg", k.Qg.PayloadKey, null),
            One(k.Qd, "Qd", k.Qd.PayloadKey, null),
            k.IdRF is { } rf ? One(rf, "IdRF", rf.PayloadKey, Branch(rf.Inputs, vgs: 0, vds: 1)) : null,
            k.Dispersion?.Tau,
            k.Qg.Method == Constituent.DriftMethod ? DriftCharges.Build(model) : null);
    }

    /// <summary>
    /// The channel current the core evaluates at an operating point — <c>IdRF</c> for a dispersive model, else
    /// <c>Id</c> — and its gradient over the seed lanes: what a drift charge reads (MC-8 R-mc8-3), and what the
    /// composer trains one against.
    /// </summary>
    public double Channel(ReadOnlySpan<double> seeds, Span<double> grad)
        => IdRF is { } rf ? Eval(rf, "IdRF", seeds, grad) : Eval(Id, "Id", seeds, grad);

    /// <summary>
    /// Both charges and their gradients over the seed lanes at one operating point, unscaled and before any
    /// drain–source image — what the composer's report and conservation checks read (MC-8).
    /// </summary>
    public (double Qg, double Qd) Charges(ReadOnlySpan<double> seeds, Span<double> gQg, Span<double> gQd)
    {
        if (Drift is not { } drift) return (Eval(Qg, "Qg", seeds, gQg), Eval(Qd, "Qd", seeds, gQd));
        Span<double> g = stackalloc double[3];
        double id = Channel(seeds, g);
        drift.Evaluate(seeds[0], seeds[1], id, g, Width, out double qg, gQg, out double qd, gQd);
        return (qg, qd);
    }

    /// <summary>The <c>Extrapolation.Gx</c> key of a current payload: <c>Id</c>, <c>IgGs</c>, <c>IgGd</c>.</summary>
    internal static string GxKey(string constituent, string key) => constituent == "Ig" ? key : constituent;

    /// <summary>A branch voltage <c>a·vgs + b·vds</c> as a vector over <paramref name="inputs"/>.</summary>
    private static double[] Branch(IReadOnlyList<string> inputs, double vgs, double vds)
    {
        var own = new double[inputs.Count];
        for (int k = 0; k < own.Length; k++)
            own[k] = inputs[k] == Vgs ? vgs : inputs[k] == Vds ? vds : 0.0;
        return own;
    }

    /// <summary>
    /// One constituent at one operating point: its value, and its gradient written into
    /// <paramref name="grad"/>. A NaN or infinite value or derivative is a named failure — never a
    /// silent zero, which would be a device that conducts nothing exactly where its equation broke.
    /// </summary>
    internal double Eval(ComposedFunction f, string name, ReadOnlySpan<double> seeds, Span<double> grad)
    {
        double v = f.Evaluate(seeds, grad);
        bool finite = double.IsFinite(v);
        for (int i = 0; i < Width && finite; i++) finite = double.IsFinite(grad[i]);
        if (!finite) throw new ComposedEvaluationException(Describe(name, v, seeds));
        return v;
    }

    private string Describe(string name, double v, ReadOnlySpan<double> seeds)
    {
        var at = new List<string>(Width);
        for (int i = 0; i < Width; i++)
            at.Add($"{Seeds[i]}={seeds[i].ToString("G6", CultureInfo.InvariantCulture)}");
        string what = double.IsFinite(v) ? "a derivative that is not finite" : v.ToString(CultureInfo.InvariantCulture);
        return $"'{_label}': constituent {name} evaluated to {what} at {string.Join(", ", at)}.";
    }
}

/// <summary>One compiled constituent payload: value and gradient over the core's seed lanes.</summary>
internal abstract class ComposedFunction
{
    public abstract double Evaluate(ReadOnlySpan<double> seeds, Span<double> grad);
}

/// <summary>A charge the drift decomposition supplies instead.</summary>
internal sealed class ZeroFunction : ComposedFunction
{
    public static readonly ZeroFunction Instance = new();

    public override double Evaluate(ReadOnlySpan<double> seeds, Span<double> grad)
    {
        grad.Clear();
        return 0.0;
    }
}

internal sealed class EquationFunction(CompiledSddExpr expr, string label) : ComposedFunction
{
    public override double Evaluate(ReadOnlySpan<double> seeds, Span<double> grad)
        => expr.EvalDualInto(seeds, grad, label);
}

/// <summary>A table or ANN: its inputs gathered from the seeds, its gradient scattered back.</summary>
internal sealed class RuntimeFunction : ComposedFunction
{
    private readonly GuidedExtrapolation _f;
    private readonly int[] _lane;      // input k reads seed lane _lane[k]
    private readonly int _width;

    public RuntimeFunction(GuidedExtrapolation f, IReadOnlyList<string> inputs, IReadOnlyList<string> seeds)
    {
        _f = f;
        _width = seeds.Count;
        _lane = new int[inputs.Count];
        for (int k = 0; k < inputs.Count; k++)
        {
            int lane = -1;
            for (int s = 0; s < seeds.Count; s++) if (seeds[s] == inputs[k]) lane = s;
            _lane[k] = lane >= 0 ? lane
                : throw new InvalidOperationException($"input '{inputs[k]}' has no seed in this model.");
        }
    }

    public override double Evaluate(ReadOnlySpan<double> seeds, Span<double> grad)
    {
        int d = _lane.Length;
        Span<double> x = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> g = stackalloc double[RuntimeEvaluator.MaxInputs];
        for (int k = 0; k < d; k++) x[k] = seeds[_lane[k]];
        double v = _f.Evaluate(x[..d], g);
        grad[.._width].Clear();
        for (int k = 0; k < d; k++) grad[_lane[k]] = g[k];
        return v;
    }
}

/// <summary>A composed model's equation was not finite at an operating point. The message names the
/// model, the constituent and the point.</summary>
public sealed class ComposedEvaluationException(string message) : Exception(message);
