using System.Globalization;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices.Composed.Traps;

/// <summary>
/// The <c>Custom</c> trap model (brief-modelcomposer-10 R-mc10-3): per enabled state, a <b>dynamics</b> expression
/// <c>dphi/dt = …</c> and a <b>DC value</b> expression, in the one expression engine, over the state itself, <c>vgs</c>,
/// <c>vds</c>, <c>T</c> (°C, as every constituent reads it — write <c>(T+273.15)</c> for kelvin) and the block's named
/// parameters. Derivatives come from the engine's dual numbers. How a user adds a model — the thermally activated emission
/// of [P21]/[N22], say — without a code change.
///
/// <para><b>Checked when built:</b> every name an expression reads is one of those; and the DC expression is a fixed point
/// of the dynamics — <c>|D(phi_dc)| ≤ 1e-9 V·|∂D/∂phi|</c>, the Newton correction to the true fixed point being under a
/// nanovolt — at every bias of <see cref="SampleBiases"/>, at 25 °C and 125 °C.</para>
/// </summary>
public sealed class CustomTraps : TrapModelDefinition
{
    public const string ModelName = "Custom";
    public const string DynamicsKey = "Dynamics", DcKey = "Dc";

    /// <summary>The fixed-point tolerance, volts.</summary>
    public const double FixedPointTolerance = 1e-9;

    /// <summary>The biases the fixed point is checked at: (vgs, vds) over pinch-off to forward gate and 0 to 50 V.</summary>
    public static readonly IReadOnlyList<(double Vgs, double Vds)> SampleBiases =
        [.. from g in new[] { -5.0, -2, -1, 0, 1 } from d in new[] { 0.0, 1, 5, 10, 28, 50 } select (g, d)];

    private static readonly double[] SampleTemperatures = [25, 125];

    public override string Name => ModelName;
    public override IReadOnlyList<string> States { get; } = ["phi1", "phi2", "phi3", "phi4"];
    public override IReadOnlyList<TrapParameter> Parameters { get; } = [];
    public override bool DeclaresAllParameters => false;

    /// <summary>A state's expression key.</summary>
    public static string Key(string state, string what) => $"{state}.{what}";

    public override TrapDynamics Build(ModelTraps traps, string label)
    {
        var parameters = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, value) in traps.Parameters)
        {
            if (name is ComposedCoreProgram.Vgs or ComposedCoreProgram.Vds or ComposedCoreProgram.T || States.Contains(name))
                throw new CmodelFormatException($"Traps.Parameters.{name}", "a parameter cannot share an input's name.");
            parameters[name] = value;
        }
        foreach (var e in traps.Expressions)
        {
            int dot = e.Key.IndexOf('.');
            if (dot < 0 || !traps.States.Contains(e.Key[..dot]) || e.Key[(dot + 1)..] is not (DynamicsKey or DcKey))
                throw new CmodelFormatException($"Traps.Expressions.{e.Key}",
                    $"a Custom model's expressions are <state>.{DynamicsKey} and <state>.{DcKey} of its states ({string.Join(", ", traps.States)}).");
        }

        var dynamics = new CompiledSddExpr[traps.States.Count];
        var dc = new CompiledSddExpr[traps.States.Count];
        for (int k = 0; k < dynamics.Length; k++)
        {
            string state = traps.States[k];
            IReadOnlyList<string> seeds = [state, ComposedCoreProgram.Vgs, ComposedCoreProgram.Vds, ComposedCoreProgram.T];
            dynamics[k] = Compile(traps, Key(state, DynamicsKey), seeds, parameters, label);
            dc[k] = Compile(traps, Key(state, DcKey), seeds, parameters, label);
        }
        var built = new Dynamics(traps.States, dynamics, dc, label);
        CheckFixedPoints(built);
        return built;
    }

    private static CompiledSddExpr Compile(ModelTraps traps, string key, IReadOnlyList<string> seeds,
                                           IReadOnlyDictionary<string, double> parameters, string label)
    {
        string field = $"Traps.Expressions.{key}";
        string text = traps.Expression(key) ?? throw new CmodelFormatException(field, "it is missing.");
        Expr ast;
        try { ast = Parser.Parse(text); }
        catch (Exception ex) { throw new CmodelFormatException(field, $"it does not parse: {ex.Message}"); }
        foreach (string r in AstWalker.CollectRefs(ast))
            if (!seeds.Contains(r) && !parameters.ContainsKey(r) && r is not ("pi" or "e"))
                throw new CmodelFormatException(field,
                    $"'{r}' is neither one of its inputs ({string.Join(", ", seeds)}) nor one of the model's parameters.");
        return CompiledSddExpr.CompileNamed(ast, seeds, parameters, label);
    }

    /// <summary>Refuses a DC expression that is not a fixed point of its dynamics, naming the state and the bias.</summary>
    private static void CheckFixedPoints(Dynamics d)
    {
        Span<double> g = stackalloc double[4];
        for (int k = 0; k < d.States.Count; k++)
            foreach (double t in SampleTemperatures)
                foreach (var (vgs, vds) in SampleBiases)
                {
                    double phi = d.DcValue(k, vgs, vds, t);
                    double rate = d.Rate(k, phi, vgs, vds, t, g);
                    string at = $"vgs={G(vgs)} V, vds={G(vds)} V, T={G(t)} °C";
                    if (!double.IsFinite(phi) || !double.IsFinite(rate) || !double.IsFinite(g[0]))
                        throw new CmodelFormatException($"Traps.Expressions.{Key(d.States[k], DcKey)}",
                            $"the state or its rate is not finite at {at}.");
                    if (rate != 0 && !(Math.Abs(rate) <= FixedPointTolerance * Math.Abs(g[0])))
                        throw new CmodelFormatException($"Traps.Expressions.{Key(d.States[k], DcKey)}",
                            $"it is not a fixed point of {d.States[k]}'s dynamics: at {at} it gives {G(phi)} V, where dphi/dt is {G(rate)} V/s.");
                }
    }

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    private sealed class Dynamics(IReadOnlyList<string> states, CompiledSddExpr[] dynamics, CompiledSddExpr[] dc, string label)
        : RateTrapDynamics(states)
    {
        public override double Rate(int state, double phi, double vgs, double vds, double t, Span<double> grad)
        {
            Span<double> seeds = [phi, vgs, vds, t];
            return dynamics[state].EvalDualInto(seeds, grad, label);
        }

        public override double DcValue(int state, double vgs, double vds, double t)
        {
            Span<double> seeds = [0, vgs, vds, t];
            Span<double> grad = stackalloc double[4];
            return dc[state].EvalDualInto(seeds, grad, label);
        }
    }
}
