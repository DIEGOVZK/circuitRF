// The extrinsic fit's settings and its recorded result, as the .cmc's Network block carries them
// (brief-modelcomposer-5 R-mc5-1 … R-mc5-5).
//
//   "Fit":    { "Method": "ColdFetThenMultiBias", "ForwardVariant": "Moderate", "ChannelResistance": 1.5,
//               "PinchOff": { "Set": "cold", "Point": "m4_0.s2p" }, "Forward": { … }, "ColdBand": [1e9, 40e9],
//               "Active": [ { "Set": "bias", "Point": "…" }, … ],
//               "Weights": { "Flatness": 1, "Cold": 1, "SmallSignal": 0 },
//               "Starts": 4, "Seed": 1, "Algorithm": "cmaes", "Budget": 1200,
//               "PoorRelativeError": 0.2, "PoorCorrelation": 0.95 }
//   "Result": { "Method": "MultiBias", "Cost": 1.2e-6, "Digest": "…",
//               "Variables": { "rg": { "StdError": 0.03, "Spread": 0.001, "AtBound": "Max" } },
//               "Report": [ "Cpgi poorly determined (ρ = −0.98 with Cpga)", … ] }
//
// Every Fit key is optional and is written only when it differs from its default, so a document that took every
// default carries "Fit": {}. The fitted values themselves are the variables' Value (CmcExtrinsic).

using System.Globalization;
using System.Text.Json.Nodes;
using static CircuitRF.Design.Modeling.CmcExtrinsic;

namespace CircuitRF.Design.Modeling;

/// <summary>How the network is fitted (R-mc5-1). The methods compose: a direct start, then the optimised refinement.</summary>
public enum NetworkFitMethod { ColdFet, MultiBias, ColdFetThenMultiBias }

/// <summary>The forward-gate cold bias ([P1] §5.5.2.4): the intrinsic device collapsed to a node, or the less extreme
/// bias with the channel-resistance term of [P1] Fig. 5.17.</summary>
public enum ForwardVariant { Extreme, Moderate }

/// <summary>A bias point of one data set, by its stable id.</summary>
public sealed record PointRef(string Set, string Point)
{
    public override string ToString() => $"{Set}/{Point}";
}

/// <summary>The multi-bias cost's three terms (R-mc5-1.2): (a) frequency flatness, (b) the cold-FET models, (c) S against a
/// fitted small-signal circuit.</summary>
public sealed record NetworkFitWeights(double Flatness = 1, double Cold = 1, double SmallSignal = 0)
{
    public static NetworkFitWeights Default { get; } = new();
}

/// <summary>The fit's settings, each with its stated default.</summary>
public sealed record NetworkFitSettings
{
    public const double DefaultPoorRelativeError = 0.2, DefaultPoorCorrelation = 0.95;
    public const int DefaultStarts = 4, DefaultBudgetPerCoordinate = 200;
    public const string DefaultAlgorithm = "cmaes";

    public NetworkFitMethod Method { get; init; } = NetworkFitMethod.MultiBias;
    /// <summary>The pinch-off cold point; null chooses one: Vds = 0 V, the lowest Vgs whose low-frequency Y22 is capacitive.</summary>
    public PointRef? PinchOff { get; init; }
    /// <summary>The forward-gate cold point; null chooses one: Vds = 0 V, the highest Vgs whose low-frequency Y11 is resistive.</summary>
    public PointRef? Forward { get; init; }
    public ForwardVariant ForwardVariant { get; init; } = ForwardVariant.Extreme;
    /// <summary>The channel resistance R_ch of the moderate forward variant, ohms — from another test structure ([P1]).</summary>
    public double? ChannelResistance { get; init; }
    /// <summary>The band the cold models are fitted over, Hz; null is the data's whole axis.</summary>
    public (double Lo, double Hi)? ColdBand { get; init; }
    /// <summary>The active points of the flatness term; empty chooses one off, one on, one near the knee, one in saturation.</summary>
    public IReadOnlyList<PointRef> Active { get; init; } = [];
    public NetworkFitWeights Weights { get; init; } = NetworkFitWeights.Default;
    /// <summary>Seeded starts of the global search, each polished by Levenberg–Marquardt.</summary>
    public int Starts { get; init; } = DefaultStarts;
    public ulong Seed { get; init; } = 1;
    /// <summary><c>cmaes</c> or <c>de</c> — the registry's global algorithms.</summary>
    public string Algorithm { get; init; } = DefaultAlgorithm;
    /// <summary>Evaluations each start's global search may spend; null is <see cref="DefaultBudgetPerCoordinate"/> per variable.</summary>
    public int? Budget { get; init; }
    public double PoorRelativeError { get; init; } = DefaultPoorRelativeError;
    public double PoorCorrelation { get; init; } = DefaultPoorCorrelation;

    public static NetworkFitSettings Default { get; } = new();

    private static readonly string[] Keys =
    [
        "Method", "PinchOff", "Forward", "ForwardVariant", "ChannelResistance", "ColdBand", "Active", "Weights",
        "Starts", "Seed", "Algorithm", "Budget", "PoorRelativeError", "PoorCorrelation",
    ];
    private static readonly string[] WeightKeys = ["Flatness", "Cold", "SmallSignal"];
    private static readonly string[] PointKeys = ["Set", "Point"];
    public static readonly IReadOnlyList<string> Algorithms = ["cmaes", "de"];

    public static NetworkFitSettings Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        var s = new NetworkFitSettings
        {
            Method = Enum<NetworkFitMethod>(o, "Method", f) ?? NetworkFitMethod.MultiBias,
            PinchOff = o["PinchOff"] is { } p ? Point(p, $"{f}.PinchOff") : null,
            Forward = o["Forward"] is { } fw ? Point(fw, $"{f}.Forward") : null,
            ForwardVariant = Enum<ForwardVariant>(o, "ForwardVariant", f) ?? ForwardVariant.Extreme,
            ChannelResistance = Num(o, "ChannelResistance", $"{f}.ChannelResistance"),
            ColdBand = o["ColdBand"] is { } b ? Band(b, $"{f}.ColdBand") : null,
            Active = o["Active"] is { } a ? Points(a, $"{f}.Active") : [],
            Weights = o["Weights"] is { } w ? WeightsOf(w, $"{f}.Weights") : NetworkFitWeights.Default,
            Starts = (int)(Num(o, "Starts", $"{f}.Starts") ?? DefaultStarts),
            Seed = (ulong)(Num(o, "Seed", $"{f}.Seed") ?? 1),
            Algorithm = Str(o, "Algorithm", $"{f}.Algorithm", required: false) ?? DefaultAlgorithm,
            Budget = Num(o, "Budget", $"{f}.Budget") is { } bu ? (int)bu : null,
            PoorRelativeError = Num(o, "PoorRelativeError", $"{f}.PoorRelativeError") ?? DefaultPoorRelativeError,
            PoorCorrelation = Num(o, "PoorCorrelation", $"{f}.PoorCorrelation") ?? DefaultPoorCorrelation,
        };
        if (s.Starts < 1) throw new CmcFormatException($"{f}.Starts", "it is below 1.");
        if (s.Budget is < 1) throw new CmcFormatException($"{f}.Budget", "it is below 1.");
        if (!Algorithms.Contains(s.Algorithm))
            throw new CmcFormatException($"{f}.Algorithm", $"'{s.Algorithm}' is not {string.Join(" or ", Algorithms)}.");
        if (s.ChannelResistance is < 0) throw new CmcFormatException($"{f}.ChannelResistance", "it is negative.");
        if (!(s.PoorRelativeError > 0)) throw new CmcFormatException($"{f}.PoorRelativeError", "it is not above 0.");
        if (!(s.PoorCorrelation is > 0 and < 1)) throw new CmcFormatException($"{f}.PoorCorrelation", "it is not between 0 and 1.");
        return s;
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        if (Method != NetworkFitMethod.MultiBias) o["Method"] = Method.ToString();
        if (PinchOff is { } p) o["PinchOff"] = PointJson(p);
        if (Forward is { } fw) o["Forward"] = PointJson(fw);
        if (ForwardVariant != ForwardVariant.Extreme) o["ForwardVariant"] = ForwardVariant.ToString();
        if (ChannelResistance is { } r) o["ChannelResistance"] = r;
        if (ColdBand is { } b) o["ColdBand"] = new JsonArray(b.Lo, b.Hi);
        if (Active.Count > 0) o["Active"] = new JsonArray([.. Active.Select(PointJson)]);
        if (Weights != NetworkFitWeights.Default)
            o["Weights"] = new JsonObject { ["Flatness"] = Weights.Flatness, ["Cold"] = Weights.Cold, ["SmallSignal"] = Weights.SmallSignal };
        if (Starts != DefaultStarts) o["Starts"] = Starts;
        if (Seed != 1) o["Seed"] = Seed;
        if (Algorithm != DefaultAlgorithm) o["Algorithm"] = Algorithm;
        if (Budget is { } bu) o["Budget"] = bu;
        if (PoorRelativeError != DefaultPoorRelativeError) o["PoorRelativeError"] = PoorRelativeError;
        if (PoorCorrelation != DefaultPoorCorrelation) o["PoorCorrelation"] = PoorCorrelation;
        return o;
    }

    private static JsonNode PointJson(PointRef p) => new JsonObject { ["Set"] = p.Set, ["Point"] = p.Point };

    private static PointRef Point(JsonNode n, string f)
    {
        var o = Obj(n, f);
        Only(o, f, PointKeys);
        return new PointRef(Str(o, "Set", $"{f}.Set")!, Str(o, "Point", $"{f}.Point")!);
    }

    private static IReadOnlyList<PointRef> Points(JsonNode n, string f) =>
        n is JsonArray a ? [.. a.Select((x, i) => Point(x!, $"{f}[{i}]"))] : throw new CmcFormatException(f, "it is not a list.");

    private static (double, double) Band(JsonNode n, string f)
    {
        if (n is not JsonArray a || a.Count != 2) throw new CmcFormatException(f, "it is not a list of two frequencies in Hz.");
        double lo = NumberOf(a[0], $"{f}[0]"), hi = NumberOf(a[1], $"{f}[1]");
        if (!(lo >= 0 && hi > lo)) throw new CmcFormatException(f, $"{G(lo)} to {G(hi)} Hz is not a band.");
        return (lo, hi);
    }

    private static NetworkFitWeights WeightsOf(JsonNode n, string f)
    {
        var o = Obj(n, f);
        Only(o, f, WeightKeys);
        var w = new NetworkFitWeights(Num(o, "Flatness", $"{f}.Flatness") ?? 1, Num(o, "Cold", $"{f}.Cold") ?? 1,
                                      Num(o, "SmallSignal", $"{f}.SmallSignal") ?? 0);
        if (w.Flatness < 0 || w.Cold < 0 || w.SmallSignal < 0) throw new CmcFormatException(f, "a weight is negative.");
        if (w.Flatness + w.Cold + w.SmallSignal == 0) throw new CmcFormatException(f, "every weight is zero.");
        return w;
    }

    private static T? Enum<T>(JsonObject o, string name, string f) where T : struct, Enum
    {
        string? text = Str(o, name, $"{f}.{name}", required: false);
        if (text is null) return null;
        if (System.Enum.TryParse<T>(text, ignoreCase: false, out var v) && System.Enum.IsDefined(v)) return v;
        throw new CmcFormatException($"{f}.{name}", $"'{text}' is not {string.Join(", ", System.Enum.GetNames<T>())}.");
    }
}

/// <summary>What a fit found for one variable (R-mc5-4): its standard error, its spread across the starts, and whether it
/// ended on a bound.</summary>
public sealed record FittedValueRecord(double StdError, double Spread, string? AtBound);

/// <summary>The last fit, as the document records it (R-mc5-5).</summary>
public sealed record NetworkFitRecord
{
    public required string Method { get; init; }
    public double Cost { get; init; }
    /// <summary>The network values' digest (<see cref="NetworkFit.Digest"/>); a later step that recorded another is stale.</summary>
    public required string Digest { get; init; }
    public IReadOnlyDictionary<string, FittedValueRecord> Variables { get; init; } =
        new Dictionary<string, FittedValueRecord>(StringComparer.Ordinal);
    /// <summary>The identifiability report's lines.</summary>
    public IReadOnlyList<string> Report { get; init; } = [];
    /// <summary>The data digest the fit read (<see cref="CmcStamps.DataDigest"/>); null before stamps existed (MC-11).</summary>
    public string? Data { get; init; }

    private static readonly string[] Keys = ["Method", "Cost", "Digest", "Variables", "Report", "Data"];
    private static readonly string[] VariableKeys = ["StdError", "Spread", "AtBound"];

    public static NetworkFitRecord Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        var vars = new Dictionary<string, FittedValueRecord>(StringComparer.Ordinal);
        if (o["Variables"] is { } vn)
            foreach (var (name, value) in Obj(vn, $"{f}.Variables"))
            {
                string g = $"{f}.Variables.{name}";
                var e = Obj(value!, g);
                Only(e, g, VariableKeys);
                string? atBound = Str(e, "AtBound", $"{g}.AtBound", required: false);
                if (atBound is not (null or "Min" or "Max")) throw new CmcFormatException($"{g}.AtBound", $"'{atBound}' is not Min or Max.");
                // A standard error that could not be computed is written as no number at all.
                vars[name] = new FittedValueRecord(Num(e, "StdError", $"{g}.StdError") ?? double.PositiveInfinity,
                                                   Num(e, "Spread", $"{g}.Spread") ?? 0, atBound);
            }
        IReadOnlyList<string> report = o["Report"] is { } rn
            ? rn is JsonArray a ? [.. a.Select((x, i) => x is JsonValue v && v.TryGetValue<string>(out var t) ? t
                                                         : throw new CmcFormatException($"{f}.Report[{i}]", "it is not a string."))]
                                : throw new CmcFormatException($"{f}.Report", "it is not a list.")
            : [];
        return new NetworkFitRecord
        {
            Method = Str(o, "Method", $"{f}.Method")!, Cost = Num(o, "Cost", $"{f}.Cost") ?? 0,
            Digest = Str(o, "Digest", $"{f}.Digest")!, Variables = vars, Report = report,
            Data = Str(o, "Data", $"{f}.Data", required: false),
        };
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject { ["Method"] = Method, ["Cost"] = Cost, ["Digest"] = Digest };
        if (Variables.Count > 0)
        {
            var vo = new JsonObject();
            foreach (var (name, v) in Variables.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var e = new JsonObject();
                if (double.IsFinite(v.StdError)) e["StdError"] = v.StdError;
                e["Spread"] = v.Spread;
                if (v.AtBound is not null) e["AtBound"] = v.AtBound;
                vo[name] = e;
            }
            o["Variables"] = vo;
        }
        if (Report.Count > 0) o["Report"] = new JsonArray([.. Report.Select(r => (JsonNode)r!)]);
        if (Data is not null) o["Data"] = Data;
        return o;
    }
}
