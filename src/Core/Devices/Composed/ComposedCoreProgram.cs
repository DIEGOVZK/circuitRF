using System.Globalization;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// The constituents of one <c>.cmodel</c>, compiled ONCE when the file is loaded and shared by every
/// <see cref="ComposedCoreModel"/> built on it (R-mc1-3).
///
/// <para><b>Inputs are seeds.</b> <c>vgs</c> and <c>vds</c> always, and <c>T</c> when the model is
/// over a temperature range, occupy the first gradient lanes in that order, so one dual-number
/// evaluation returns a constituent's value and its derivative by each — the SDD's own arithmetic
/// (<see cref="CompiledSddExpr.CompileNamed"/>), not a second implementation of it.</para>
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

    // One compiled equation per payload entry, in a fixed order the core reads by index.
    internal readonly CompiledSddExpr Id, IgGs, IgGd, Qg, Qd;

    private readonly string _label;

    private ComposedCoreProgram(IReadOnlyList<string> seeds, string label,
                                CompiledSddExpr id, CompiledSddExpr igGs, CompiledSddExpr igGd,
                                CompiledSddExpr qg, CompiledSddExpr qd)
    {
        Seeds = seeds; _label = label;
        Id = id; IgGs = igGs; IgGd = igGd; Qg = qg; Qd = qd;
    }

    /// <summary>Compiles a model already checked by <see cref="CmodelFile.Validate"/>.</summary>
    public static ComposedCoreProgram Compile(ComposedModel model, string label)
    {
        IReadOnlyList<string> seeds = model.Temperature.Mode == TemperatureMode.Range
            ? [Vgs, Vds, T]
            : [Vgs, Vds];

        CompiledSddExpr One(Constituent c, string key)
        {
            var parameters = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var p in c.Parameters) parameters[p.Key] = p.Value;
            return CompiledSddExpr.CompileNamed(Parser.Parse(c.Expression(key)!), seeds, parameters, label);
        }

        var k = model.Core;
        return new ComposedCoreProgram(seeds, label,
            One(k.Id, "Expression"), One(k.Ig, "IgGs"), One(k.Ig, "IgGd"),
            One(k.Qg, "Expression"), One(k.Qd, "Expression"));
    }

    /// <summary>
    /// One constituent at one operating point: its value, and its gradient written into
    /// <paramref name="grad"/>. A NaN or infinite value or derivative is a named failure — never a
    /// silent zero, which would be a device that conducts nothing exactly where its equation broke.
    /// </summary>
    internal double Eval(CompiledSddExpr expr, string name, ReadOnlySpan<double> seeds, Span<double> grad)
    {
        double v = expr.EvalDualInto(seeds, grad, _label);
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

/// <summary>A composed model's equation was not finite at an operating point. The message names the
/// model, the constituent and the point.</summary>
public sealed class ComposedEvaluationException(string message) : Exception(message);
