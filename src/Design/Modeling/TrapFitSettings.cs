// The trap step's settings and its recorded result, as the .cmc's Steps.Traps block carries them
// (brief-modelcomposer-10 R-mc10-1 … R-mc10-6).
//
//   "Traps": {
//     "Settings": { "Model": "CaptureEmission", "States": ["phi2"], "Emission": "ThermallyActivated",
//                   "Parameters": { "TauEmit2": 1e-3, "Ea2": 0.5 }, "DcIv": "Validation",
//                   "Expressions": { "phi2": { "Dynamics": "…", "Dc": "vds" } } },
//     "Result":   { "Levels": { "phi2": [10, 20, 28] }, "Beyond": { "phi2": 38 }, "Report": [ "…" ] } }
//
//   with a threshold-shift coupling (brief-modelcomposer-19):
//     "Settings": { "Model": "CaptureEmission", "Coupling": "Shift", "States": ["phi1", "phi2"], "Parameters": { "ShiftGate": 0.1 } },
//     "Result":   { "Coupling": "Shift", "Strengths": { "ShiftDrain": { "Value": 0.05, "Error": 4e-4 }, "ShiftGate": { "Value": 0.1 } },
//                   "Residual": { "Points": 63, "Gm": 2e-4, "Gds": 1e-4 }, "Report": [ "…" ] }
//
// Every Settings key is optional and written only when it differs from its default; Model None (the default) models no
// traps. Parameters are the trap model's own, by the names a .cmodel carries them under, in base SI (Tref in °C); a
// shift strength among them is TYPED, and the fit leaves it as it is. A strength in the Result without an Error was typed.

using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed.Traps;
using static CircuitRF.Design.Modeling.CmcExtrinsic;

namespace CircuitRF.Design.Modeling;

/// <summary>The emission time constant of a <c>CaptureEmission</c> state (R-mc10-2).</summary>
public enum TrapEmission { Constant, ThermallyActivated }

/// <summary>What the DC I–V is to a fit with traps (R-mc10-5): a training target ([X14]), or held out as validation, not
/// playback ([P1] §6.7.5).</summary>
public enum DcIvUse { Target, Validation }

/// <summary>The trap step's settings (R-mc10-1 … R-mc10-5), each with its stated default.</summary>
public sealed record TrapFitSettings
{
    /// <summary>The tooltip of the time constants: what CW HB and two-tone each depend on.</summary>
    public const string TimeConstantsNote =
        "CW HB depends on the time constants only weakly, through their ratio; two-tone spacing near 1/τ_emit depends on them.";

    /// <summary><c>None</c>, <c>CaptureEmission</c>, <c>Custom</c>, or a registered model.</summary>
    public string Model { get; init; } = ModelTraps.None;

    /// <summary>How the states reach the current (R-mc19-1): <see cref="ModelTraps.Trained"/> (the default — <c>Id</c> trained
    /// with the states as inputs, from pulsed I–V) or <see cref="ModelTraps.Shift"/> (a threshold shift whose strengths are
    /// typed or fitted from multi-bias S).</summary>
    public string Coupling { get; init; } = ModelTraps.Trained;

    public bool Shifted => Coupling == ModelTraps.Shift;

    /// <summary>The enabled states; empty takes the model's default — drain-lag alone for <c>CaptureEmission</c> (gate-lag
    /// off when the setting is first chosen), every state with expressions for <c>Custom</c>.</summary>
    public IReadOnlyList<string> States { get; init; } = [];

    public TrapEmission Emission { get; init; } = TrapEmission.Constant;

    /// <summary>The model's parameters by name; a declared one left out takes its default.</summary>
    public IReadOnlyDictionary<string, double> Parameters { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary><c>Custom</c>: per state, its <c>Dynamics</c> and <c>Dc</c> expressions.</summary>
    public IReadOnlyDictionary<string, (string Dynamics, string Dc)> Expressions { get; init; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal);

    public DcIvUse DcIv { get; init; } = DcIvUse.Target;

    public bool Modelled => Model != ModelTraps.None;

    public static TrapFitSettings Default { get; } = new();

    private static readonly string[] Keys = ["Model", "Coupling", "States", "Emission", "Parameters", "Expressions", "DcIv"];

    public static TrapFitSettings Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        var parameters = new Dictionary<string, double>(StringComparer.Ordinal);
        if (o["Parameters"] is { } pn)
            foreach (var (name, value) in Obj(pn, $"{f}.Parameters")) parameters[name] = NumberOf(value, $"{f}.Parameters.{name}");
        var expressions = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (o["Expressions"] is { } en)
            foreach (var (state, value) in Obj(en, $"{f}.Expressions"))
            {
                string g = $"{f}.Expressions.{state}";
                var e = Obj(value!, g);
                Only(e, g, [CustomTraps.DynamicsKey, CustomTraps.DcKey]);
                expressions[state] = (Str(e, CustomTraps.DynamicsKey, $"{g}.{CustomTraps.DynamicsKey}")!,
                                      Str(e, CustomTraps.DcKey, $"{g}.{CustomTraps.DcKey}")!);
            }
        var s = new TrapFitSettings
        {
            Model = Str(o, "Model", $"{f}.Model", required: false) ?? ModelTraps.None,
            Coupling = Str(o, "Coupling", $"{f}.Coupling", required: false) ?? ModelTraps.Trained,
            States = o["States"] is { } st ? CurrentConstituentSettings.Words(st, $"{f}.States") : [],
            Emission = CurrentConstituentSettings.Enum<TrapEmission>(o, "Emission", f) ?? TrapEmission.Constant,
            Parameters = parameters,
            Expressions = expressions,
            DcIv = CurrentConstituentSettings.Enum<DcIvUse>(o, "DcIv", f) ?? DcIvUse.Target,
        };
        if (!TrapModels.Names.Contains(s.Model))
            throw new CmcFormatException($"{f}.Model", $"'{s.Model}' is not a known trap model; the known models are {string.Join(", ", TrapModels.Names)}.");
        if (!ModelTraps.Couplings.Contains(s.Coupling))
            throw new CmcFormatException($"{f}.Coupling", $"'{s.Coupling}' is not a coupling; the couplings are {string.Join(", ", ModelTraps.Couplings)}.");
        return s;
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        if (Model != ModelTraps.None) o["Model"] = Model;
        if (Coupling != ModelTraps.Trained) o["Coupling"] = Coupling;
        if (States.Count > 0) o["States"] = new JsonArray([.. States.Select(n => (JsonNode)n)]);
        if (Emission != TrapEmission.Constant) o["Emission"] = Emission.ToString();
        if (Parameters.Count > 0)
        {
            var p = new JsonObject();
            foreach (var (name, value) in Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal)) p[name] = value;
            o["Parameters"] = p;
        }
        if (Expressions.Count > 0)
        {
            var e = new JsonObject();
            foreach (var (state, (dynamics, dc)) in Expressions.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                e[state] = new JsonObject { [CustomTraps.DynamicsKey] = dynamics, [CustomTraps.DcKey] = dc };
            o["Expressions"] = e;
        }
        if (DcIv != DcIvUse.Target) o["DcIv"] = DcIv.ToString();
        return o;
    }
}

/// <summary>One shift strength after the step (R-mc19-3, R-mc19-4), V/V: its standard error when fitted, null when typed.</summary>
public sealed record ShiftStrength(double Value, double? Error)
{
    public bool Typed => Error is null;
}

/// <summary>How well the two frozen-trap relations hold over the fitted S points (R-mc19-3): RMS residuals in siemens of
/// <c>gm_RF = (1 − a_g)·gm₀</c> and <c>gds_RF = gds₀ + a_d·gm₀</c>.</summary>
public sealed record ShiftResidual(int Points, double Gm, double Gds);

/// <summary>The trap step's result (R-mc10-4): per enabled state the distinct trap-setting levels its training data held,
/// and how many pulsed points lay beyond them and were left as validation; under a shift coupling (R-mc19-3) the
/// strengths and the residuals instead; the one-line report.</summary>
public sealed record TrapFitRecord
{
    public IReadOnlyDictionary<string, IReadOnlyList<double>> Levels { get; init; } =
        new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int> Beyond { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>The coupling the step ran under; <see cref="ModelTraps.Trained"/> is never written.</summary>
    public string Coupling { get; init; } = ModelTraps.Trained;

    /// <summary>Per strength parameter (<c>ShiftDrain</c>, <c>ShiftGate</c>), its value — a shift coupling only.</summary>
    public IReadOnlyDictionary<string, ShiftStrength> Strengths { get; init; } = new Dictionary<string, ShiftStrength>(StringComparer.Ordinal);

    /// <summary>The relations' residuals, or null when every strength was typed and no S point was read.</summary>
    public ShiftResidual? Residual { get; init; }

    public IReadOnlyList<string> Report { get; init; } = [];

    private static readonly string[] Keys = ["Levels", "Beyond", "Coupling", "Strengths", "Residual", "Report"];

    public static TrapFitRecord Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        var levels = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
        if (o["Levels"] is { } ln)
            foreach (var (state, value) in Obj(ln, $"{f}.Levels"))
                levels[state] = value is JsonArray a ? [.. a.Select((x, i) => NumberOf(x, $"{f}.Levels.{state}[{i}]"))]
                                                     : throw new CmcFormatException($"{f}.Levels.{state}", "it is not a list of voltages.");
        var beyond = new Dictionary<string, int>(StringComparer.Ordinal);
        if (o["Beyond"] is { } bn)
            foreach (var (state, value) in Obj(bn, $"{f}.Beyond")) beyond[state] = (int)NumberOf(value, $"{f}.Beyond.{state}");
        IReadOnlyList<string> report = o["Report"] is JsonArray r
            ? [.. r.Select((x, i) => x is JsonValue v && v.TryGetValue<string>(out var t) ? t : throw new CmcFormatException($"{f}.Report[{i}]", "it is not a string."))]
            : [];
        var strengths = new Dictionary<string, ShiftStrength>(StringComparer.Ordinal);
        if (o["Strengths"] is { } sn)
            foreach (var (name, value) in Obj(sn, $"{f}.Strengths"))
            {
                string g = $"{f}.Strengths.{name}";
                var e = Obj(value!, g);
                Only(e, g, ["Value", "Error"]);
                strengths[name] = new ShiftStrength(NumberOf(e["Value"], $"{g}.Value"), e["Error"] is { } err ? NumberOf(err, $"{g}.Error") : null);
            }
        ShiftResidual? residual = null;
        if (o["Residual"] is { } rn)
        {
            var e = Obj(rn, $"{f}.Residual");
            Only(e, $"{f}.Residual", ["Points", "Gm", "Gds"]);
            residual = new ShiftResidual((int)NumberOf(e["Points"], $"{f}.Residual.Points"), NumberOf(e["Gm"], $"{f}.Residual.Gm"),
                                         NumberOf(e["Gds"], $"{f}.Residual.Gds"));
        }
        return new TrapFitRecord
        {
            Levels = levels, Beyond = beyond, Report = report, Strengths = strengths, Residual = residual,
            Coupling = Str(o, "Coupling", $"{f}.Coupling", required: false) ?? ModelTraps.Trained,
        };
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        if (Levels.Count > 0)
        {
            var l = new JsonObject();
            foreach (var (state, levels) in Levels.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                l[state] = new JsonArray([.. levels.Select(v => (JsonNode)v)]);
            o["Levels"] = l;
        }
        if (Beyond.Count > 0)
        {
            var b = new JsonObject();
            foreach (var (state, n) in Beyond.OrderBy(kv => kv.Key, StringComparer.Ordinal)) b[state] = n;
            o["Beyond"] = b;
        }
        if (Coupling != ModelTraps.Trained) o["Coupling"] = Coupling;
        if (Strengths.Count > 0)
        {
            var st = new JsonObject();
            foreach (var (name, v) in Strengths.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var e = new JsonObject { ["Value"] = v.Value };
                if (v.Error is { } err) e["Error"] = err;
                st[name] = e;
            }
            o["Strengths"] = st;
        }
        if (Residual is { } r) o["Residual"] = new JsonObject { ["Points"] = r.Points, ["Gm"] = r.Gm, ["Gds"] = r.Gds };
        if (Report.Count > 0) o["Report"] = new JsonArray([.. Report.Select(l => (JsonNode)l!)]);
        return o;
    }
}
