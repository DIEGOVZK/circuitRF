// The current step's settings and its recorded result, as the .cmc's Steps.Current block carries them
// (brief-modelcomposer-7 R-mc7-1 … R-mc7-5).
//
//   "Current": {
//     "Network":  "…",                                   the network digest the step was computed through
//     "Settings": { "DataSets": ["dc"], "Id": { "Method": "Table", "GridStep": 0.05 },
//                   "Ig": { "Method": "Equation", "Template": "Diode" }, "IdFloor": 0.01, "GateSplit": "Sign",
//                   "Dispersion": { "Tau": 1e-6, "DataSets": ["bias"] } },
//     "Result":   { "Constituents": { "Id": { …as model.json writes it… }, "Ig": { … }, "IdRF": { … } },
//                   "Gx": { "Id": 0.4, "IgGs": 2e-4, "IgGd": 1e-9 }, "Tau": 1e-6, "Arrays": "<key>",
//                   "Errors": { "Id": { "Train": …, "TrainRelative": …, "HeldOut": …, "HeldOutRelative": … } },
//                   "Conductance": { "Gm": [rms, relative], "Gds": [rms, relative] },
//                   "Symmetry": { "ValueJump": …, "SlopeJump": …, "GateJump": … }, "Eq649": …,
//                   "Reference": [vgs, vds], "Report": [ "…" ] } }
//
// Every Settings key is optional and written only when it differs from its default. The constituents are recorded
// exactly as the .cmodel will carry them (CmodelFile.ConstituentJson), so export copies rather than re-spells them;
// their arrays live in <name>.cmc-cache/current/<Arrays>.npy, derived like everything in the cache — a missing file
// is a fit to re-run (deterministic for its seed), never an error.

using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using static CircuitRF.Design.Modeling.CmcExtrinsic;

namespace CircuitRF.Design.Modeling;

/// <summary>How a current constituent is built (R-mc7-2).</summary>
public enum CurrentMethod { ANN, Table, Equation }

/// <summary>
/// How the measured gate current is shared between the gate–source and gate–drain branches (R-mc7-1). <see cref="Sign"/>:
/// for <c>vds ≥ 0</c> a forward (positive) gate current is all <c>IgGs</c> and a reverse one all <c>IgGd</c> (breakdown at
/// the drain side); for <c>vds &lt; 0</c> the roles exchange.
/// </summary>
public enum GateSplit { Sign, GateSource, GateDrain }

/// <summary>What a table is re-gridded through (R-mc7-2), in order of preference.</summary>
public enum RegridSource { ANN, RBF }

/// <summary>One equation parameter: its start (null takes the template's estimate from the data), its bounds, and a lock.</summary>
public sealed record EquationParameterSettings(double? Value = null, double? Min = null, double? Max = null, bool Lock = false);

/// <summary>One current constituent's method and that method's settings.</summary>
public sealed record CurrentConstituentSettings
{
    public const double DefaultGridStep = 0.1;
    /// <summary>A table's temperature axis spacing, °C (MC-9), when T is an input.</summary>
    public const double DefaultTemperatureStep = 25;
    /// <summary>L-BFGS iterations per start: relative weighting makes the problem stiff, and 2000 measured short of
    /// converged on a 2 × 8 network over ~170 points (5.6 % worst held-out error, against 0.6 % at 6000).</summary>
    public const int DefaultMaxIterations = 6000;
    public const string DefaultAlgorithm = "cmaes";
    public static readonly IReadOnlyList<int> DefaultHidden = [8, 8];

    public CurrentMethod Method { get; init; } = CurrentMethod.ANN;

    /// <summary>ANN, and the network a table is re-gridded through.</summary>
    public IReadOnlyList<int> Hidden { get; init; } = DefaultHidden;
    public AnnActivation Activation { get; init; } = AnnActivation.Tanh;
    public int MaxIterations { get; init; } = DefaultMaxIterations;

    /// <summary>Table: the grid's spacing on both axes, volts.</summary>
    public double GridStep { get; init; } = DefaultGridStep;
    public RegridSource Regrid { get; init; } = RegridSource.ANN;
    /// <summary>Table: the spline's smoothing weight λ (R-mc2-1).</summary>
    public double Smoothing { get; init; }
    /// <summary>Table, with T an input (MC-9): the temperature axis's spacing, °C.</summary>
    public double TemperatureStep { get; init; } = DefaultTemperatureStep;
    /// <summary>Equation, with T an input (MC-9 R-mc9-1): the template parameters made linear in temperature, each
    /// <c>p</c> becoming <c>p + p_T·(T − Tnom)</c>.</summary>
    public IReadOnlyList<string> TemperatureCoefficients { get; init; } = [];

    /// <summary>Equation: a template's name (<see cref="CurrentTemplates"/>), or null for the user's own expressions.</summary>
    public string? Template { get; init; }
    /// <summary>Equation, Id: the user's own expression in <c>vgs</c>, <c>vds</c> and named parameters.</summary>
    public string? Expression { get; init; }
    /// <summary>Equation, Ig: the user's own branch expressions.</summary>
    public string? IgGs { get; init; }
    public string? IgGd { get; init; }
    public IReadOnlyDictionary<string, EquationParameterSettings> Parameters { get; init; } =
        new Dictionary<string, EquationParameterSettings>(StringComparer.Ordinal);
    /// <summary>Equation: the global search before the Levenberg–Marquardt polish — <c>cmaes</c>, <c>de</c>, or <c>lm</c>
    /// for the polish alone.</summary>
    public string Algorithm { get; init; } = DefaultAlgorithm;

    public static readonly IReadOnlyList<string> Algorithms = ["cmaes", "de", "lm"];

    private static readonly string[] Keys =
        ["Method", "Hidden", "Activation", "MaxIterations", "GridStep", "Regrid", "Smoothing", "TemperatureStep", "Template", "Expression",
         "IgGs", "IgGd", "Parameters", "TemperatureCoefficients", "Algorithm"];
    private static readonly string[] ParameterKeys = ["Value", "Min", "Max", "Lock"];

    public static CurrentConstituentSettings Parse(JsonNode node, string f)
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
        var s = new CurrentConstituentSettings
        {
            Method = Enum<CurrentMethod>(o, "Method", f) ?? CurrentMethod.ANN,
            Hidden = o["Hidden"] is { } h ? HiddenOf(h, $"{f}.Hidden") : DefaultHidden,
            Activation = Enum<AnnActivation>(o, "Activation", f) ?? AnnActivation.Tanh,
            MaxIterations = (int)(Num(o, "MaxIterations", $"{f}.MaxIterations") ?? DefaultMaxIterations),
            GridStep = Num(o, "GridStep", $"{f}.GridStep") ?? DefaultGridStep,
            Regrid = Enum<RegridSource>(o, "Regrid", f) ?? RegridSource.ANN,
            Smoothing = Num(o, "Smoothing", $"{f}.Smoothing") ?? 0,
            TemperatureStep = Num(o, "TemperatureStep", $"{f}.TemperatureStep") ?? DefaultTemperatureStep,
            TemperatureCoefficients = o["TemperatureCoefficients"] is { } tc ? Words(tc, $"{f}.TemperatureCoefficients") : [],
            Template = Str(o, "Template", $"{f}.Template", required: false),
            Expression = Str(o, "Expression", $"{f}.Expression", required: false),
            IgGs = Str(o, "IgGs", $"{f}.IgGs", required: false),
            IgGd = Str(o, "IgGd", $"{f}.IgGd", required: false),
            Parameters = parameters,
            Algorithm = Str(o, "Algorithm", $"{f}.Algorithm", required: false) ?? DefaultAlgorithm,
        };
        if (s.MaxIterations < 1) throw new CmcFormatException($"{f}.MaxIterations", "it is below 1.");
        if (!(s.GridStep > 0)) throw new CmcFormatException($"{f}.GridStep", "it is not a positive step in volts.");
        if (s.Smoothing < 0) throw new CmcFormatException($"{f}.Smoothing", "it is negative.");
        if (!(s.TemperatureStep > 0)) throw new CmcFormatException($"{f}.TemperatureStep", "it is not a positive step in °C.");
        if (!Algorithms.Contains(s.Algorithm))
            throw new CmcFormatException($"{f}.Algorithm", $"'{s.Algorithm}' is not {string.Join(", ", Algorithms)}.");
        return s;
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        if (Method != CurrentMethod.ANN) o["Method"] = Method.ToString();
        if (!Hidden.SequenceEqual(DefaultHidden)) o["Hidden"] = new JsonArray([.. Hidden.Select(w => (JsonNode)w)]);
        if (Activation != AnnActivation.Tanh) o["Activation"] = Activation.ToString();
        if (MaxIterations != DefaultMaxIterations) o["MaxIterations"] = MaxIterations;
        if (GridStep != DefaultGridStep) o["GridStep"] = GridStep;
        if (Regrid != RegridSource.ANN) o["Regrid"] = Regrid.ToString();
        if (Smoothing != 0) o["Smoothing"] = Smoothing;
        if (TemperatureStep != DefaultTemperatureStep) o["TemperatureStep"] = TemperatureStep;
        if (Template is not null) o["Template"] = Template;
        if (Expression is not null) o["Expression"] = Expression;
        if (IgGs is not null) o["IgGs"] = IgGs;
        if (IgGd is not null) o["IgGd"] = IgGd;
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
        if (TemperatureCoefficients.Count > 0)
            o["TemperatureCoefficients"] = new JsonArray([.. TemperatureCoefficients.Select(n => (JsonNode)n)]);
        if (Algorithm != DefaultAlgorithm) o["Algorithm"] = Algorithm;
        return o;
    }

    /// <summary>A list of names: parameter names, not data sets.</summary>
    internal static IReadOnlyList<string> Words(JsonNode n, string f) =>
        n is JsonArray a
            ? [.. a.Select((x, i) => x is JsonValue v && v.TryGetValue<string>(out var w) && w.Length > 0 ? w
                                     : throw new CmcFormatException($"{f}[{i}]", "it is not a name."))]
            : throw new CmcFormatException(f, "it is not a list of names.");

    internal static IReadOnlyList<int> HiddenOf(JsonNode n, string f)
    {
        if (n is not JsonArray a || a.Count is < 1 or > AnnEvaluator.MaxHiddenLayers)
            throw new CmcFormatException(f, $"it is not a list of 1 to {AnnEvaluator.MaxHiddenLayers} layer widths.");
        var widths = a.Select((x, i) => NumberOf(x, $"{f}[{i}]")).ToList();
        if (widths.Any(w => w != Math.Floor(w) || w is < 1 or > AnnEvaluator.MaxNeurons))
            throw new CmcFormatException(f, $"a layer is not 1 to {AnnEvaluator.MaxNeurons} neurons.");
        return [.. widths.Select(w => (int)w)];
    }

    internal static T? Enum<T>(JsonObject o, string name, string f) where T : struct, Enum
    {
        string? text = Str(o, name, $"{f}.{name}", required: false);
        if (text is null) return null;
        if (System.Enum.TryParse<T>(text, ignoreCase: false, out var v) && System.Enum.IsDefined(v)) return v;
        throw new CmcFormatException($"{f}.{name}", $"'{text}' is not {string.Join(", ", System.Enum.GetNames<T>())}.");
    }
}

/// <summary>
/// [P1] eq. 6.52's DC/RF dispersion (R-mc7-4): <c>IdRF</c> trained by derivative training on the intrinsic RF
/// <c>Gm</c>/<c>Gds</c>, anchored to <c>IdDC</c> at <see cref="Reference"/>, with time constant <see cref="Tau"/>.
/// </summary>
public sealed record DispersionFitSettings
{
    /// <summary>[X06]: typically on the order of 1 µs.</summary>
    public const double DefaultTau = 1e-6;

    public double Tau { get; init; } = DefaultTau;
    /// <summary>The S-parameter sets the RF conductances come from; empty takes every SParams and PulsedSParams set.</summary>
    public IReadOnlyList<string> DataSets { get; init; } = [];
    /// <summary>The intrinsic bias where <c>IdRF = IdDC</c>; null takes the S point nearest the middle of the S data.</summary>
    public (double Vgs, double Vds)? Reference { get; init; }
    public IReadOnlyList<int> Hidden { get; init; } = CurrentConstituentSettings.DefaultHidden;

    private static readonly string[] Keys = ["Tau", "DataSets", "Reference", "Hidden"];

    public static DispersionFitSettings Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        var s = new DispersionFitSettings
        {
            Tau = Num(o, "Tau", $"{f}.Tau") ?? DefaultTau,
            DataSets = o["DataSets"] is { } d ? CurrentFitSettings.Names(d, $"{f}.DataSets") : [],
            Reference = o["Reference"] is { } r ? CurrentFitSettings.Pair(r, $"{f}.Reference") : null,
            Hidden = o["Hidden"] is { } h ? CurrentConstituentSettings.HiddenOf(h, $"{f}.Hidden") : CurrentConstituentSettings.DefaultHidden,
        };
        if (!(s.Tau > 0)) throw new CmcFormatException($"{f}.Tau", "it is not a positive time in seconds.");
        return s;
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        if (Tau != DefaultTau) o["Tau"] = Tau;
        if (DataSets.Count > 0) o["DataSets"] = new JsonArray([.. DataSets.Select(n => (JsonNode)n)]);
        if (Reference is { } r) o["Reference"] = new JsonArray(r.Vgs, r.Vds);
        if (!Hidden.SequenceEqual(CurrentConstituentSettings.DefaultHidden)) o["Hidden"] = new JsonArray([.. Hidden.Select(w => (JsonNode)w)]);
        return o;
    }
}

/// <summary>The current step's settings (R-mc7-1 … R-mc7-4), each with its stated default.</summary>
public sealed record CurrentFitSettings
{
    public const double DefaultIdFloor = 0.1, DefaultIgFloor = 1e-4, DefaultSmallestSwing = 0.05, DefaultHeldOut = 0.1;
    public const int DefaultRestarts = 3;

    /// <summary>The I–V data sets to train on; empty takes every DcIv, PulsedIv and SimulatedTables set.</summary>
    public IReadOnlyList<string> DataSets { get; init; } = [];
    public CurrentConstituentSettings Id { get; init; } = new();
    public CurrentConstituentSettings Ig { get; init; } = new();
    /// <summary><c>Id</c>'s relative weighting floor, as a fraction of the largest |Id|: a point weighs
    /// <c>1/max(|Id|, floor)²</c>, so sub-threshold and saturation both matter. Measured on the synthetic device's
    /// held-out points: 0.1 gives 0.6 % worst error over the range, 0.01 gives 2.7 % for no better sub-threshold fit,
    /// and uniform weighting 0.1 % with sub-threshold errors three times larger.</summary>
    public double IdFloor { get; init; } = DefaultIdFloor;
    /// <summary><c>Ig</c>'s log-magnitude floor <c>I0</c>, as a fraction of the largest |Ig|: a table or network is fitted
    /// in <c>asinh(Ig/I0)</c> (<see cref="OutputTransform"/>), an equation weighted by <c>1/max(|Ig|, I0)²</c>.</summary>
    public double IgFloor { get; init; } = DefaultIgFloor;
    public GateSplit GateSplit { get; init; } = GateSplit.Sign;
    public DrainSourceSymmetry Symmetry { get; init; } = DrainSourceSymmetry.None;
    /// <summary>The smallest signal swing a table must still model, volts: a table whose step exceeds twice it warns
    /// ([P1] Fig. 6.8).</summary>
    public double SmallestSwing { get; init; } = DefaultSmallestSwing;
    public ulong Seed { get; init; } = 1;
    public int Restarts { get; init; } = DefaultRestarts;
    public double HeldOutFraction { get; init; } = DefaultHeldOut;
    /// <summary>Null: no dispersion (the default).</summary>
    public DispersionFitSettings? Dispersion { get; init; }

    public static CurrentFitSettings Default { get; } = new();

    private static readonly string[] Keys =
        ["DataSets", "Id", "Ig", "IdFloor", "IgFloor", "GateSplit", "Symmetry", "SmallestSwing", "Seed", "Restarts",
         "HeldOutFraction", "Dispersion"];

    public static CurrentFitSettings Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        var s = new CurrentFitSettings
        {
            DataSets = o["DataSets"] is { } d ? Names(d, $"{f}.DataSets") : [],
            Id = o["Id"] is { } id ? CurrentConstituentSettings.Parse(id, $"{f}.Id") : new(),
            Ig = o["Ig"] is { } ig ? CurrentConstituentSettings.Parse(ig, $"{f}.Ig") : new(),
            IdFloor = Num(o, "IdFloor", $"{f}.IdFloor") ?? DefaultIdFloor,
            IgFloor = Num(o, "IgFloor", $"{f}.IgFloor") ?? DefaultIgFloor,
            GateSplit = CurrentConstituentSettings.Enum<GateSplit>(o, "GateSplit", f) ?? GateSplit.Sign,
            Symmetry = CurrentConstituentSettings.Enum<DrainSourceSymmetry>(o, "Symmetry", f) ?? DrainSourceSymmetry.None,
            SmallestSwing = Num(o, "SmallestSwing", $"{f}.SmallestSwing") ?? DefaultSmallestSwing,
            Seed = (ulong)(Num(o, "Seed", $"{f}.Seed") ?? 1),
            Restarts = (int)(Num(o, "Restarts", $"{f}.Restarts") ?? DefaultRestarts),
            HeldOutFraction = Num(o, "HeldOutFraction", $"{f}.HeldOutFraction") ?? DefaultHeldOut,
            Dispersion = o["Dispersion"] is { } dn ? DispersionFitSettings.Parse(dn, $"{f}.Dispersion") : null,
        };
        if (!(s.IdFloor > 0)) throw new CmcFormatException($"{f}.IdFloor", "it is not above 0.");
        if (!(s.IgFloor > 0)) throw new CmcFormatException($"{f}.IgFloor", "it is not above 0.");
        if (!(s.SmallestSwing > 0)) throw new CmcFormatException($"{f}.SmallestSwing", "it is not a positive swing in volts.");
        if (s.Restarts < 1) throw new CmcFormatException($"{f}.Restarts", "it is below 1.");
        if (!(s.HeldOutFraction is >= 0 and < 1)) throw new CmcFormatException($"{f}.HeldOutFraction", "it is not at least 0 and below 1.");
        return s;
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        if (DataSets.Count > 0) o["DataSets"] = new JsonArray([.. DataSets.Select(n => (JsonNode)n)]);
        if (Id.ToJson() is JsonObject id && id.Count > 0) o["Id"] = id;
        if (Ig.ToJson() is JsonObject ig && ig.Count > 0) o["Ig"] = ig;
        if (IdFloor != DefaultIdFloor) o["IdFloor"] = IdFloor;
        if (IgFloor != DefaultIgFloor) o["IgFloor"] = IgFloor;
        if (GateSplit != GateSplit.Sign) o["GateSplit"] = GateSplit.ToString();
        if (Symmetry != DrainSourceSymmetry.None) o["Symmetry"] = Symmetry.ToString();
        if (SmallestSwing != DefaultSmallestSwing) o["SmallestSwing"] = SmallestSwing;
        if (Seed != 1) o["Seed"] = Seed;
        if (Restarts != DefaultRestarts) o["Restarts"] = Restarts;
        if (HeldOutFraction != DefaultHeldOut) o["HeldOutFraction"] = HeldOutFraction;
        if (Dispersion is { } d) o["Dispersion"] = d.ToJson();
        return o;
    }

    internal static IReadOnlyList<string> Names(JsonNode n, string f) =>
        n is JsonArray a
            ? [.. a.Select((x, i) => x is JsonValue v && v.TryGetValue<string>(out var s) ? s
                                     : throw new CmcFormatException($"{f}[{i}]", "it is not a data set's name."))]
            : throw new CmcFormatException(f, "it is not a list of data set names.");

    internal static (double, double) Pair(JsonNode n, string f) =>
        n is JsonArray { Count: 2 } a ? (NumberOf(a[0], $"{f}[0]"), NumberOf(a[1], $"{f}[1]"))
                                      : throw new CmcFormatException(f, "it is not [vgs, vds].");
}

/// <summary>A payload's RMS error over the training and the held-out points, absolute and relative to the range of its
/// targets (R-mc7-5).</summary>
public sealed record FitErrorRecord(int TrainCount, double Train, double TrainRelative, int HeldOutCount, double HeldOut,
                                    double HeldOutRelative);

/// <summary>The fitted function's <c>Gm</c> and <c>Gds</c> against the data's own DC derivatives: RMS difference, and that
/// relative to the RMS of the data's derivative — a measure of the data's noise (R-mc7-5).</summary>
public sealed record ConductanceRecord(double GmRms, double GmRelative, double GdsRms, double GdsRelative);

/// <summary>With <c>Symmetry: Exchange</c>, the jumps at <c>vds = 0</c> (R-mc7-3): Id's value (<c>2·Id(vgs, 0)</c>) and its
/// <c>vds</c>-slope (<c>∂Id/∂vgs(vgs, 0)</c>), and the gate–source branch current (<c>IgGs − IgGd</c>), each its largest
/// magnitude over the domain's <c>vgs</c>.</summary>
public sealed record SymmetryRecord(double ValueJump, double SlopeJump, double GateJump);

/// <summary>The current step's result as the document records it (R-mc7-5).</summary>
public sealed record CurrentFitRecord
{
    /// <summary>The constituents exactly as <c>model.json</c> will carry them.</summary>
    public required Constituent Id { get; init; }
    public required Constituent Ig { get; init; }
    public Constituent? IdRF { get; init; }
    public IReadOnlyDictionary<string, double> Gx { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);
    public double? Tau { get; init; }
    /// <summary>The cache key of the constituents' arrays; empty when every constituent is an equation.</summary>
    public string Arrays { get; init; } = "";
    public IReadOnlyDictionary<string, FitErrorRecord> Errors { get; init; } = new Dictionary<string, FitErrorRecord>(StringComparer.Ordinal);
    public ConductanceRecord? Conductance { get; init; }
    public SymmetryRecord? Symmetry { get; init; }
    /// <summary>[P1] eq. 6.49's residual over the RF conductances: the part no single current carries — RMS of
    /// <c>∇IdRF − (Gm, Gds)</c> relative to RMS of <c>(Gm, Gds)</c> — zero when 6.49 holds.</summary>
    public double? Eq649 { get; init; }
    /// <summary>Where <c>IdRF</c> was anchored to <c>IdDC</c>.</summary>
    public (double Vgs, double Vds)? Reference { get; init; }
    public IReadOnlyList<string> Report { get; init; } = [];

    private static readonly string[] Keys =
        ["Constituents", "Gx", "Tau", "Arrays", "Errors", "Conductance", "Symmetry", "Eq649", "Reference", "Report"];
    private static readonly string[] ConstituentKeys = ["Id", "Ig", "IdRF"];
    private static readonly string[] ErrorKeys = ["TrainCount", "Train", "TrainRelative", "HeldOutCount", "HeldOut", "HeldOutRelative"];
    private static readonly string[] SymmetryKeys = ["ValueJump", "SlopeJump", "GateJump"];

    public static CurrentFitRecord Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        var c = Obj(o["Constituents"] ?? throw new CmcFormatException($"{f}.Constituents", "it is missing."), $"{f}.Constituents");
        Only(c, $"{f}.Constituents", ConstituentKeys);
        Constituent One(string name)
        {
            string g = $"{f}.Constituents.{name}";
            var n = c[name] ?? throw new CmcFormatException(g, "it is missing.");
            try { return CmodelFile.ParseConstituentJson(n.ToJsonString(), g); }
            catch (CmodelFormatException ex) { throw new CmcFormatException(ex.Field, ex.Message[(ex.Field.Length + 2)..]); }
        }

        var gx = new Dictionary<string, double>(StringComparer.Ordinal);
        if (o["Gx"] is { } gn) foreach (var (k, v) in Obj(gn, $"{f}.Gx")) gx[k] = NumberOf(v, $"{f}.Gx.{k}");
        var errors = new Dictionary<string, FitErrorRecord>(StringComparer.Ordinal);
        if (o["Errors"] is { } en)
            foreach (var (k, v) in Obj(en, $"{f}.Errors"))
            {
                string g = $"{f}.Errors.{k}";
                var e = Obj(v!, g);
                Only(e, g, ErrorKeys);
                double N(string key) => Num(e, key, $"{g}.{key}") ?? 0;
                errors[k] = new FitErrorRecord((int)N("TrainCount"), N("Train"), N("TrainRelative"),
                                               (int)N("HeldOutCount"), N("HeldOut"), N("HeldOutRelative"));
            }
        ConductanceRecord? conductance = null;
        if (o["Conductance"] is { } cn)
        {
            var co = Obj(cn, $"{f}.Conductance");
            Only(co, $"{f}.Conductance", ["Gm", "Gds"]);
            var gm = CurrentFitSettings.Pair(co["Gm"] ?? throw new CmcFormatException($"{f}.Conductance.Gm", "it is missing."), $"{f}.Conductance.Gm");
            var gds = CurrentFitSettings.Pair(co["Gds"] ?? throw new CmcFormatException($"{f}.Conductance.Gds", "it is missing."), $"{f}.Conductance.Gds");
            conductance = new ConductanceRecord(gm.Item1, gm.Item2, gds.Item1, gds.Item2);
        }
        SymmetryRecord? symmetry = null;
        if (o["Symmetry"] is { } sn)
        {
            var so = Obj(sn, $"{f}.Symmetry");
            Only(so, $"{f}.Symmetry", SymmetryKeys);
            symmetry = new SymmetryRecord(Num(so, "ValueJump", $"{f}.Symmetry.ValueJump") ?? 0,
                                          Num(so, "SlopeJump", $"{f}.Symmetry.SlopeJump") ?? 0,
                                          Num(so, "GateJump", $"{f}.Symmetry.GateJump") ?? 0);
        }
        IReadOnlyList<string> report = o["Report"] is { } rn
            ? rn is JsonArray a ? [.. a.Select((x, i) => x is JsonValue v && v.TryGetValue<string>(out var t) ? t
                                                         : throw new CmcFormatException($"{f}.Report[{i}]", "it is not a string."))]
                                : throw new CmcFormatException($"{f}.Report", "it is not a list.")
            : [];
        return new CurrentFitRecord
        {
            Id = One("Id"), Ig = One("Ig"), IdRF = c["IdRF"] is null ? null : One("IdRF"),
            Gx = gx, Tau = Num(o, "Tau", $"{f}.Tau"), Arrays = Str(o, "Arrays", $"{f}.Arrays", required: false) ?? "",
            Errors = errors, Conductance = conductance, Symmetry = symmetry, Eq649 = Num(o, "Eq649", $"{f}.Eq649"),
            Reference = o["Reference"] is { } r ? CurrentFitSettings.Pair(r, $"{f}.Reference") : null, Report = report,
        };
    }

    public JsonNode ToJson()
    {
        var c = new JsonObject
        {
            ["Id"] = JsonNode.Parse(CmodelFile.ConstituentJson(Id)),
            ["Ig"] = JsonNode.Parse(CmodelFile.ConstituentJson(Ig)),
        };
        if (IdRF is not null) c["IdRF"] = JsonNode.Parse(CmodelFile.ConstituentJson(IdRF));
        var o = new JsonObject { ["Constituents"] = c };
        if (Gx.Count > 0)
        {
            var g = new JsonObject();
            foreach (string key in ModelExtrapolation.Keys) if (Gx.TryGetValue(key, out double v)) g[key] = v;
            o["Gx"] = g;
        }
        if (Tau is { } tau) o["Tau"] = tau;
        if (Arrays.Length > 0) o["Arrays"] = Arrays;
        if (Errors.Count > 0)
        {
            var eo = new JsonObject();
            foreach (string key in ModelExtrapolation.Keys)
                if (Errors.TryGetValue(key, out var e))
                    eo[key] = new JsonObject
                    {
                        ["TrainCount"] = e.TrainCount, ["Train"] = e.Train, ["TrainRelative"] = e.TrainRelative,
                        ["HeldOutCount"] = e.HeldOutCount, ["HeldOut"] = e.HeldOut, ["HeldOutRelative"] = e.HeldOutRelative,
                    };
            o["Errors"] = eo;
        }
        if (Conductance is { } k)
            o["Conductance"] = new JsonObject
            {
                ["Gm"] = new JsonArray(k.GmRms, k.GmRelative), ["Gds"] = new JsonArray(k.GdsRms, k.GdsRelative),
            };
        if (Symmetry is { } s)
            o["Symmetry"] = new JsonObject { ["ValueJump"] = s.ValueJump, ["SlopeJump"] = s.SlopeJump, ["GateJump"] = s.GateJump };
        if (Eq649 is { } r649) o["Eq649"] = r649;
        if (Reference is { } r) o["Reference"] = new JsonArray(r.Vgs, r.Vds);
        if (Report.Count > 0) o["Report"] = new JsonArray([.. Report.Select(l => (JsonNode)l!)]);
        return o;
    }
}
