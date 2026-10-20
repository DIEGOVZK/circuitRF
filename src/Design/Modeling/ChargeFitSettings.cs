// The charge step's settings and its recorded result, as the .cmc's Steps.Charge block carries them
// (brief-modelcomposer-8 R-mc8-1 … R-mc8-4).
//
//   "Charge": {
//     "Network":  "…",                                   the network digest the step was computed through
//     "Settings": { "DataSets": ["bias"], "Qg": { "Method": "Table", "Path": "Mean" },
//                   "Qd": { "Method": "Equation", "Template": "Angelov" }, "Anchor": [0, 0], "FlatnessLimit": 0.1,
//                   "Drift": { "Lambda": 0, "VdsZero": 0.05 } },
//     "Result":   { "Constituents": { "Qg": { …as model.json writes it… }, "Qd": { … } }, "Arrays": "<key>",
//                   "Anchor": [vgs, vds],
//                   "Errors": { "C11": [rms, relative], "C12": …, "C21": …, "C22": … },
//                   "PathDifference": { "Qg": …, "Qd": … }, "CdsSpread": …, "Report": [ "…" ] } }
//
// Every Settings key is optional and written only when it differs from its default. As in Steps.Current, the
// constituents are recorded exactly as the .cmodel will carry them; their arrays live in
// <name>.cmc-cache/charge/<Arrays>.npy.

using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using static CircuitRF.Design.Modeling.CmcExtrinsic;

namespace CircuitRF.Design.Modeling;

/// <summary>How a charge constituent is built (R-mc8-2). Every one is a charge function (D4).</summary>
public enum ChargeMethod { ANN, Table, Equation }

/// <summary>Which of [P1] Fig. 6.16's paths a line-integral table takes (R-mc8-2).</summary>
public enum IntegrationPath { A, B, Mean }

/// <summary>One charge's method and that method's settings.</summary>
public sealed record ChargeConstituentSettings
{
    public const double DefaultGridStep = 0.1;
    public const int DefaultMaxIterations = 3000;

    public ChargeMethod Method { get; init; } = ChargeMethod.ANN;

    /// <summary>ANN: the network, trained by adjoint (derivative) training; Table with an ANN interpolant: the
    /// interpolant's network.</summary>
    public IReadOnlyList<int> Hidden { get; init; } = CurrentConstituentSettings.DefaultHidden;
    public AnnActivation Activation { get; init; } = AnnActivation.Tanh;
    public int MaxIterations { get; init; } = DefaultMaxIterations;

    /// <summary>Table: the grid's spacing on both axes, volts; what each capacitance is interpolated by; which path; the
    /// spline's smoothing weight λ. A network is the default interpolant: on the synthetic device through the GaN
    /// network, a table integrated over one fits C11 to 0.34 % of its range, over an RBF to 15.6 % — an RBF fitted
    /// to each capacitance alone is exact at the points but not mutually conservative between them, and the
    /// integrals carry that across the domain.</summary>
    public double GridStep { get; init; } = DefaultGridStep;
    public RegridSource Interpolant { get; init; } = RegridSource.ANN;
    public IntegrationPath Path { get; init; } = IntegrationPath.Mean;
    public double Smoothing { get; init; }

    /// <summary>Equation: a template (<see cref="ChargeTemplates"/>), or the user's own expression.</summary>
    public string? Template { get; init; }
    public string? Expression { get; init; }
    public IReadOnlyDictionary<string, EquationParameterSettings> Parameters { get; init; } =
        new Dictionary<string, EquationParameterSettings>(StringComparer.Ordinal);
    public string Algorithm { get; init; } = CurrentConstituentSettings.DefaultAlgorithm;

    private static readonly string[] Keys =
        ["Method", "Hidden", "Activation", "MaxIterations", "GridStep", "Interpolant", "Path", "Smoothing", "Template",
         "Expression", "Parameters", "Algorithm"];
    private static readonly string[] ParameterKeys = ["Value", "Min", "Max", "Lock"];

    public static ChargeConstituentSettings Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        var parameters = new Dictionary<string, EquationParameterSettings>(StringComparer.Ordinal);
        if (o["Parameters"] is { } pn)
            foreach (var (name, value) in Obj(pn, $"{f}.Parameters"))
            {
                string g = $"{f}.Parameters.{name}";
                var e = Obj(value!, g);
                Only(e, g, ParameterKeys);
                var p = new EquationParameterSettings(Num(e, "Value", $"{g}.Value"), Num(e, "Min", $"{g}.Min"),
                                                      Num(e, "Max", $"{g}.Max"), Bool(e, "Lock", $"{g}.Lock"));
                if (p.Min is { } lo && p.Max is { } hi && !(lo < hi)) throw new CmcFormatException(g, "Min must be below Max.");
                parameters[name] = p;
            }
        var s = new ChargeConstituentSettings
        {
            Method = CurrentConstituentSettings.Enum<ChargeMethod>(o, "Method", f) ?? ChargeMethod.ANN,
            Hidden = o["Hidden"] is { } h ? CurrentConstituentSettings.HiddenOf(h, $"{f}.Hidden") : CurrentConstituentSettings.DefaultHidden,
            Activation = CurrentConstituentSettings.Enum<AnnActivation>(o, "Activation", f) ?? AnnActivation.Tanh,
            MaxIterations = (int)(Num(o, "MaxIterations", $"{f}.MaxIterations") ?? DefaultMaxIterations),
            GridStep = Num(o, "GridStep", $"{f}.GridStep") ?? DefaultGridStep,
            Interpolant = CurrentConstituentSettings.Enum<RegridSource>(o, "Interpolant", f) ?? RegridSource.ANN,
            Path = CurrentConstituentSettings.Enum<IntegrationPath>(o, "Path", f) ?? IntegrationPath.Mean,
            Smoothing = Num(o, "Smoothing", $"{f}.Smoothing") ?? 0,
            Template = Str(o, "Template", $"{f}.Template", required: false),
            Expression = Str(o, "Expression", $"{f}.Expression", required: false),
            Parameters = parameters,
            Algorithm = Str(o, "Algorithm", $"{f}.Algorithm", required: false) ?? CurrentConstituentSettings.DefaultAlgorithm,
        };
        if (s.MaxIterations < 1) throw new CmcFormatException($"{f}.MaxIterations", "it is below 1.");
        if (!(s.GridStep > 0)) throw new CmcFormatException($"{f}.GridStep", "it is not a positive step in volts.");
        if (s.Smoothing < 0) throw new CmcFormatException($"{f}.Smoothing", "it is negative.");
        if (!CurrentConstituentSettings.Algorithms.Contains(s.Algorithm))
            throw new CmcFormatException($"{f}.Algorithm", $"'{s.Algorithm}' is not {string.Join(", ", CurrentConstituentSettings.Algorithms)}.");
        return s;
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        if (Method != ChargeMethod.ANN) o["Method"] = Method.ToString();
        if (!Hidden.SequenceEqual(CurrentConstituentSettings.DefaultHidden)) o["Hidden"] = new JsonArray([.. Hidden.Select(w => (JsonNode)w)]);
        if (Activation != AnnActivation.Tanh) o["Activation"] = Activation.ToString();
        if (MaxIterations != DefaultMaxIterations) o["MaxIterations"] = MaxIterations;
        if (GridStep != DefaultGridStep) o["GridStep"] = GridStep;
        if (Interpolant != RegridSource.ANN) o["Interpolant"] = Interpolant.ToString();
        if (Path != IntegrationPath.Mean) o["Path"] = Path.ToString();
        if (Smoothing != 0) o["Smoothing"] = Smoothing;
        if (Template is not null) o["Template"] = Template;
        if (Expression is not null) o["Expression"] = Expression;
        if (Parameters.Count > 0)
        {
            var po = new JsonObject();
            foreach (var (name, p) in Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var e = new JsonObject();
                if (p.Value is { } v) e["Value"] = v;
                if (p.Min is { } lo) e["Min"] = lo;
                if (p.Max is { } hi) e["Max"] = hi;
                if (p.Lock) e["Lock"] = true;
                po[name] = e;
            }
            o["Parameters"] = po;
        }
        if (Algorithm != CurrentConstituentSettings.DefaultAlgorithm) o["Algorithm"] = Algorithm;
        return o;
    }
}

/// <summary>
/// [I11]'s drift-charge decomposition (R-mc8-3), an advanced option that replaces both charges' methods. Offered only
/// once the current step has run: the drift charge reads its fitted <c>Id</c>.
/// </summary>
public sealed record DriftChargeSettings
{
    public const double DefaultVdsZero = 0.05;

    /// <summary>The charge-partition factor λ; [I11] found 0 consistent with data.</summary>
    public double Lambda { get; init; }
    /// <summary>The points whose |vds| is at most this are the <c>Vds = 0</c> points the depletion charges are fitted to
    /// ([I11] §III, [P1] eq. 6.43), volts.</summary>
    public double VdsZero { get; init; } = DefaultVdsZero;
    public IReadOnlyList<int> Hidden { get; init; } = CurrentConstituentSettings.DefaultHidden;
    public int MaxIterations { get; init; } = ChargeConstituentSettings.DefaultMaxIterations;

    private static readonly string[] Keys = ["Lambda", "VdsZero", "Hidden", "MaxIterations"];

    public static DriftChargeSettings Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        var s = new DriftChargeSettings
        {
            Lambda = Num(o, "Lambda", $"{f}.Lambda") ?? 0,
            VdsZero = Num(o, "VdsZero", $"{f}.VdsZero") ?? DefaultVdsZero,
            Hidden = o["Hidden"] is { } h ? CurrentConstituentSettings.HiddenOf(h, $"{f}.Hidden") : CurrentConstituentSettings.DefaultHidden,
            MaxIterations = (int)(Num(o, "MaxIterations", $"{f}.MaxIterations") ?? ChargeConstituentSettings.DefaultMaxIterations),
        };
        if (!(s.Lambda is >= 0 and <= 1)) throw new CmcFormatException($"{f}.Lambda", "the partition factor is between 0 and 1.");
        if (!(s.VdsZero > 0)) throw new CmcFormatException($"{f}.VdsZero", "it is not a positive voltage.");
        if (s.MaxIterations < 1) throw new CmcFormatException($"{f}.MaxIterations", "it is below 1.");
        return s;
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        if (Lambda != 0) o["Lambda"] = Lambda;
        if (VdsZero != DefaultVdsZero) o["VdsZero"] = VdsZero;
        if (!Hidden.SequenceEqual(CurrentConstituentSettings.DefaultHidden)) o["Hidden"] = new JsonArray([.. Hidden.Select(w => (JsonNode)w)]);
        if (MaxIterations != ChargeConstituentSettings.DefaultMaxIterations) o["MaxIterations"] = MaxIterations;
        return o;
    }
}

/// <summary>The charge step's settings (R-mc8-1 … R-mc8-3), each with its stated default.</summary>
public sealed record ChargeFitSettings
{
    public const double DefaultFlatnessLimit = 0.1;

    /// <summary>The S-parameter sets the capacitances come from; empty takes every set with intrinsic S data.</summary>
    public IReadOnlyList<string> DataSets { get; init; } = [];
    public ChargeConstituentSettings Qg { get; init; } = new();
    public ChargeConstituentSettings Qd { get; init; } = new();
    /// <summary>Where <c>Qg = Qd = 0</c> ([P1] §6.3.4's arbitrary constant); null takes <c>vgs = vds = 0</c>, or the data
    /// point nearest it when it lies outside the data.</summary>
    public (double Vgs, double Vds)? Anchor { get; init; }
    /// <summary>A point whose worse diagonal flatness (<c>C11</c> or <c>C22</c>, MC-4's spread ÷ mean over the band) exceeds
    /// this weighs <c>(limit/flatness)²</c>; the diagonals never cross zero, so their flatness is always defined.</summary>
    public double FlatnessLimit { get; init; } = DefaultFlatnessLimit;
    public ulong Seed { get; init; } = 1;
    public int Restarts { get; init; } = CurrentFitSettings.DefaultRestarts;
    public double HeldOutFraction { get; init; } = CurrentFitSettings.DefaultHeldOut;
    /// <summary>Null: no decomposition (the default).</summary>
    public DriftChargeSettings? Drift { get; init; }

    public static ChargeFitSettings Default { get; } = new();

    private static readonly string[] Keys =
        ["DataSets", "Qg", "Qd", "Anchor", "FlatnessLimit", "Seed", "Restarts", "HeldOutFraction", "Drift"];

    public static ChargeFitSettings Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        var s = new ChargeFitSettings
        {
            DataSets = o["DataSets"] is { } d ? CurrentFitSettings.Names(d, $"{f}.DataSets") : [],
            Qg = o["Qg"] is { } qg ? ChargeConstituentSettings.Parse(qg, $"{f}.Qg") : new(),
            Qd = o["Qd"] is { } qd ? ChargeConstituentSettings.Parse(qd, $"{f}.Qd") : new(),
            Anchor = o["Anchor"] is { } a ? CurrentFitSettings.Pair(a, $"{f}.Anchor") : null,
            FlatnessLimit = Num(o, "FlatnessLimit", $"{f}.FlatnessLimit") ?? DefaultFlatnessLimit,
            Seed = (ulong)(Num(o, "Seed", $"{f}.Seed") ?? 1),
            Restarts = (int)(Num(o, "Restarts", $"{f}.Restarts") ?? CurrentFitSettings.DefaultRestarts),
            HeldOutFraction = Num(o, "HeldOutFraction", $"{f}.HeldOutFraction") ?? CurrentFitSettings.DefaultHeldOut,
            Drift = o["Drift"] is { } dn ? DriftChargeSettings.Parse(dn, $"{f}.Drift") : null,
        };
        if (!(s.FlatnessLimit > 0)) throw new CmcFormatException($"{f}.FlatnessLimit", "it is not above 0.");
        if (s.Restarts < 1) throw new CmcFormatException($"{f}.Restarts", "it is below 1.");
        if (!(s.HeldOutFraction is >= 0 and < 1)) throw new CmcFormatException($"{f}.HeldOutFraction", "it is not at least 0 and below 1.");
        return s;
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        if (DataSets.Count > 0) o["DataSets"] = new JsonArray([.. DataSets.Select(n => (JsonNode)n)]);
        if (Qg.ToJson() is JsonObject qg && qg.Count > 0) o["Qg"] = qg;
        if (Qd.ToJson() is JsonObject qd && qd.Count > 0) o["Qd"] = qd;
        if (Anchor is { } a) o["Anchor"] = new JsonArray(a.Vgs, a.Vds);
        if (FlatnessLimit != DefaultFlatnessLimit) o["FlatnessLimit"] = FlatnessLimit;
        if (Seed != 1) o["Seed"] = Seed;
        if (Restarts != CurrentFitSettings.DefaultRestarts) o["Restarts"] = Restarts;
        if (HeldOutFraction != CurrentFitSettings.DefaultHeldOut) o["HeldOutFraction"] = HeldOutFraction;
        if (Drift is { } d) o["Drift"] = d.ToJson();
        return o;
    }
}

/// <summary>The model's <c>∂Q/∂v</c> against one measured capacitance over the training points: RMS, absolute and
/// relative to the capacitance's range (R-mc8-4).</summary>
public sealed record CapacitanceErrorRecord(double Rms, double Relative);

/// <summary>The charge step's result as the document records it (R-mc8-4).</summary>
public sealed record ChargeFitRecord
{
    /// <summary>The capacitances a report names, in matrix order: <c>C11, C12</c> are <c>Qg</c>'s, <c>C21, C22</c> <c>Qd</c>'s.</summary>
    public static readonly IReadOnlyList<string> Capacitances = ["C11", "C12", "C21", "C22"];

    /// <summary>The constituents exactly as <c>model.json</c> will carry them.</summary>
    public required Constituent Qg { get; init; }
    public required Constituent Qd { get; init; }
    /// <summary>The cache key of the constituents' arrays; empty when both are equations.</summary>
    public string Arrays { get; init; } = "";
    /// <summary>Where both charges are zero.</summary>
    public (double Vgs, double Vds) Anchor { get; init; }
    public IReadOnlyDictionary<string, CapacitanceErrorRecord> Errors { get; init; } =
        new Dictionary<string, CapacitanceErrorRecord>(StringComparer.Ordinal);
    /// <summary>A line-integral table's two-path difference — RMS of <c>Q_A − Q_B</c> over the grid as a fraction of the
    /// charge's range — per charge built that way.</summary>
    public IReadOnlyDictionary<string, double> PathDifference { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);
    /// <summary>With the drift decomposition: the RMS spread of <c>Cds</c> across <c>vgs</c> at fixed <c>vds</c>, as a fraction
    /// of <c>Cds</c>'s RMS — what <c>QDS(vds)</c> cannot represent (R-mc8-3).</summary>
    public double? CdsSpread { get; init; }
    /// <summary>The trap block <c>Qd</c> takes its trap inputs under (MC-10), as the <c>.cmodel</c> carries it; null when the
    /// charges are trap-independent.</summary>
    public ModelTraps? Traps { get; init; }
    public IReadOnlyList<string> Report { get; init; } = [];

    private static readonly string[] Keys = ["Constituents", "Arrays", "Anchor", "Errors", "PathDifference", "CdsSpread", "Traps", "Report"];

    public static ChargeFitRecord Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        var c = Obj(o["Constituents"] ?? throw new CmcFormatException($"{f}.Constituents", "it is missing."), $"{f}.Constituents");
        Only(c, $"{f}.Constituents", ["Qg", "Qd"]);
        Constituent One(string name)
        {
            string g = $"{f}.Constituents.{name}";
            var n = c[name] ?? throw new CmcFormatException(g, "it is missing.");
            try { return CmodelFile.ParseConstituentJson(n.ToJsonString(), g); }
            catch (CmodelFormatException ex) { throw new CmcFormatException(ex.Field, ex.Message[(ex.Field.Length + 2)..]); }
        }
        var errors = new Dictionary<string, CapacitanceErrorRecord>(StringComparer.Ordinal);
        if (o["Errors"] is { } en)
        {
            var eo = Obj(en, $"{f}.Errors");
            Only(eo, $"{f}.Errors", [.. Capacitances]);
            foreach (var (k, v) in eo)
            {
                var (rms, rel) = CurrentFitSettings.Pair(v!, $"{f}.Errors.{k}");
                errors[k] = new CapacitanceErrorRecord(rms, rel);
            }
        }
        var paths = new Dictionary<string, double>(StringComparer.Ordinal);
        if (o["PathDifference"] is { } pn)
        {
            var po = Obj(pn, $"{f}.PathDifference");
            Only(po, $"{f}.PathDifference", ["Qg", "Qd"]);
            foreach (var (k, v) in po) paths[k] = NumberOf(v, $"{f}.PathDifference.{k}");
        }
        IReadOnlyList<string> report = o["Report"] is { } rn
            ? rn is JsonArray a ? [.. a.Select((x, i) => x is JsonValue v && v.TryGetValue<string>(out var t) ? t
                                                         : throw new CmcFormatException($"{f}.Report[{i}]", "it is not a string."))]
                                : throw new CmcFormatException($"{f}.Report", "it is not a list.")
            : [];
        return new ChargeFitRecord
        {
            Qg = One("Qg"), Qd = One("Qd"), Arrays = Str(o, "Arrays", $"{f}.Arrays", required: false) ?? "",
            Anchor = o["Anchor"] is { } an ? CurrentFitSettings.Pair(an, $"{f}.Anchor") : (0, 0),
            Errors = errors, PathDifference = paths, CdsSpread = Num(o, "CdsSpread", $"{f}.CdsSpread"),
            Traps = o["Traps"] is { } tn ? CurrentFitRecord.TrapsOf(tn, $"{f}.Traps") : null, Report = report,
        };
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject
        {
            ["Constituents"] = new JsonObject
            {
                ["Qg"] = JsonNode.Parse(CmodelFile.ConstituentJson(Qg)),
                ["Qd"] = JsonNode.Parse(CmodelFile.ConstituentJson(Qd)),
            },
        };
        if (Arrays.Length > 0) o["Arrays"] = Arrays;
        o["Anchor"] = new JsonArray(Anchor.Vgs, Anchor.Vds);
        if (Errors.Count > 0)
        {
            var eo = new JsonObject();
            foreach (string k in Capacitances)
                if (Errors.TryGetValue(k, out var e)) eo[k] = new JsonArray(e.Rms, e.Relative);
            o["Errors"] = eo;
        }
        if (PathDifference.Count > 0)
        {
            var po = new JsonObject();
            foreach (string k in (string[])["Qg", "Qd"]) if (PathDifference.TryGetValue(k, out double v)) po[k] = v;
            o["PathDifference"] = po;
        }
        if (CdsSpread is { } spread) o["CdsSpread"] = spread;
        if (Traps is { } traps) o["Traps"] = JsonNode.Parse(CmodelFile.TrapsJson(traps));
        if (Report.Count > 0) o["Report"] = new JsonArray([.. Report.Select(l => (JsonNode)l!)]);
        return o;
    }
}
