// The thermal step's settings and its recorded result, as the .cmc's Steps.Thermal block carries them
// (brief-modelcomposer-9 R-mc9-1 … R-mc9-5).
//
//   "Thermal": {
//     "Settings": { "SelfHeating": true, "Rth": 120, "RthModel": "Linear", "TauTh": 1e-6,
//                   "Ladder": [ { "Fraction": 0.7, "Tau": 1e-6 }, { "Fraction": 0.3, "Tau": 1e-4 } ],
//                   "PulsedSets": ["p25", "p85"], "DcSets": ["dc"], "MinPower": 0.1,
//                   "Isothermal": { …a current constituent's settings… }, "FitTau": true, "TauSets": ["bias"] },
//     "Result":   { "Temperatures": [25, 85], "Rth": 118.7, "RthSlope": 0.04, "Tref": 25, "RthSource": "Extracted",
//                   "Poles": [ { "Rth": 118.7, "Cth": 8.4e-9 } ], "TauTh": 1e-6,
//                   "Extraction": { "Residual": 0.8, "ResidualRelative": 0.01, "Used": 120, "LowPower": 40, "Unresolved": 0,
//                                   "Vgs": [lo, hi], "Vds": [lo, hi], "Power": [lo, hi] },
//                   "TauFit": { "Tau": 1.1e-6, "Residual": 0.02, "Points": 30 }, "Report": [ "…" ] } }
//
// Every Settings key is optional and written only when it differs from its default. Thermal values are at the base
// periphery: K/W, J/K, seconds.

using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using static CircuitRF.Design.Modeling.CmcExtrinsic;

namespace CircuitRF.Design.Modeling;

/// <summary>How the extracted thermal resistance depends on the ambient (R-mc9-3).</summary>
public enum RthModel { Constant, Linear }

/// <summary>One pole of a typed Foster ladder: its share of the total <c>Rth</c>, and its time constant <c>Ri·Ci</c>.</summary>
public sealed record LadderPoleSettings(double Fraction, double Tau);

/// <summary>The thermal step's settings (R-mc9-1 … R-mc9-3), each with its stated default.</summary>
public sealed record ThermalFitSettings
{
    /// <summary>Points dissipating less than this fraction of the largest DC power are left out of the Rth fit: their
    /// temperature rise is too small to resolve against the isothermal model's error.</summary>
    public const double DefaultMinPower = 0.1;

    /// <summary>Model self-heating: a thermal node, and DC points at their junction temperatures.</summary>
    public bool SelfHeating { get; init; }
    /// <summary>A typed thermal resistance, K/W; null extracts it from pulsed and DC I–V.</summary>
    public double? Rth { get; init; }
    public RthModel RthModel { get; init; } = RthModel.Constant;
    /// <summary>At most one of <see cref="Cth"/>, <see cref="TauTh"/> and <see cref="Ladder"/> gives the dynamics.</summary>
    public double? Cth { get; init; }
    public double? TauTh { get; init; }
    public IReadOnlyList<LadderPoleSettings> Ladder { get; init; } = [];
    /// <summary>The isothermal sets the Rth search trains on; empty takes every PulsedIv set whose quiescent bias does not
    /// dissipate.</summary>
    public IReadOnlyList<string> PulsedSets { get; init; } = [];
    /// <summary>The self-heated sets; empty takes every DcIv set.</summary>
    public IReadOnlyList<string> DcSets { get; init; } = [];
    public double MinPower { get; init; } = DefaultMinPower;
    /// <summary>How the isothermal Id is built; null takes the current step's Id settings.</summary>
    public CurrentConstituentSettings? Isothermal { get; init; }
    /// <summary>Fit τth to the low-frequency roll-off of Re Y22 ([P1] Fig. 6.34); only with traps not modelled.</summary>
    public bool FitTau { get; init; }
    /// <summary>The S sets the τth fit reads; empty takes every SParams set.</summary>
    public IReadOnlyList<string> TauSets { get; init; } = [];

    public static ThermalFitSettings Default { get; } = new();

    private static readonly string[] Keys =
        ["SelfHeating", "Rth", "RthModel", "Cth", "TauTh", "Ladder", "PulsedSets", "DcSets", "MinPower", "Isothermal", "FitTau", "TauSets"];

    public static ThermalFitSettings Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        var ladder = new List<LadderPoleSettings>();
        if (o["Ladder"] is { } ln)
        {
            if (ln is not JsonArray a) throw new CmcFormatException($"{f}.Ladder", "it is not a list of poles.");
            for (int k = 0; k < a.Count; k++)
            {
                string g = $"{f}.Ladder[{k}]";
                var e = Obj(a[k]!, g);
                Only(e, g, ["Fraction", "Tau"]);
                ladder.Add(new LadderPoleSettings(Num(e, "Fraction", $"{g}.Fraction") ?? throw new CmcFormatException($"{g}.Fraction", "it is missing."),
                                                  Num(e, "Tau", $"{g}.Tau") ?? throw new CmcFormatException($"{g}.Tau", "it is missing.")));
            }
        }
        var s = new ThermalFitSettings
        {
            SelfHeating = Bool(o, "SelfHeating", $"{f}.SelfHeating"),
            Rth = Num(o, "Rth", $"{f}.Rth"),
            RthModel = CurrentConstituentSettings.Enum<RthModel>(o, "RthModel", f) ?? RthModel.Constant,
            Cth = Num(o, "Cth", $"{f}.Cth"),
            TauTh = Num(o, "TauTh", $"{f}.TauTh"),
            Ladder = ladder,
            PulsedSets = o["PulsedSets"] is { } p ? CurrentFitSettings.Names(p, $"{f}.PulsedSets") : [],
            DcSets = o["DcSets"] is { } d ? CurrentFitSettings.Names(d, $"{f}.DcSets") : [],
            MinPower = Num(o, "MinPower", $"{f}.MinPower") ?? DefaultMinPower,
            Isothermal = o["Isothermal"] is { } iso ? CurrentConstituentSettings.Parse(iso, $"{f}.Isothermal") : null,
            FitTau = Bool(o, "FitTau", $"{f}.FitTau"),
            TauSets = o["TauSets"] is { } t ? CurrentFitSettings.Names(t, $"{f}.TauSets") : [],
        };
        if (s.Rth is { } r && !(r > 0)) throw new CmcFormatException($"{f}.Rth", "it is not a positive thermal resistance in °C/W.");
        if (s.Cth is { } c && !(c > 0)) throw new CmcFormatException($"{f}.Cth", "it is not a positive thermal capacitance in J/°C.");
        if (s.TauTh is { } tau && !(tau > 0)) throw new CmcFormatException($"{f}.TauTh", "it is not a positive time in seconds.");
        int given = (s.Cth is null ? 0 : 1) + (s.TauTh is null ? 0 : 1) + (s.Ladder.Count > 0 ? 1 : 0) + (s.FitTau ? 1 : 0);
        if (given > 1) throw new CmcFormatException(f, "Cth, TauTh, Ladder and FitTau each set the dynamics; give at most one.");
        if (s.Ladder.Count > ModelThermal.MaxPoles)
            throw new CmcFormatException($"{f}.Ladder", $"{s.Ladder.Count} poles; a ladder has at most {ModelThermal.MaxPoles}.");
        if (s.Ladder.Any(l => !(l.Fraction > 0) || !(l.Tau > 0)))
            throw new CmcFormatException($"{f}.Ladder", "every pole has a positive Fraction and a positive Tau.");
        if (s.Ladder.Count > 0 && Math.Abs(s.Ladder.Sum(l => l.Fraction) - 1) > 1e-6)
            throw new CmcFormatException($"{f}.Ladder", "the poles' fractions do not add up to 1.");
        if (!(s.MinPower is > 0 and < 1)) throw new CmcFormatException($"{f}.MinPower", "it is not above 0 and below 1.");
        if (s.Rth is not null && s.RthModel != RthModel.Constant)
            throw new CmcFormatException($"{f}.RthModel", "a typed Rth is constant; Linear describes an extracted one.");
        return s;
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        if (SelfHeating) o["SelfHeating"] = true;
        if (Rth is { } r) o["Rth"] = r;
        if (RthModel != RthModel.Constant) o["RthModel"] = RthModel.ToString();
        if (Cth is { } c) o["Cth"] = c;
        if (TauTh is { } t) o["TauTh"] = t;
        if (Ladder.Count > 0)
            o["Ladder"] = new JsonArray([.. Ladder.Select(l => (JsonNode)new JsonObject { ["Fraction"] = l.Fraction, ["Tau"] = l.Tau })]);
        if (PulsedSets.Count > 0) o["PulsedSets"] = new JsonArray([.. PulsedSets.Select(n => (JsonNode)n)]);
        if (DcSets.Count > 0) o["DcSets"] = new JsonArray([.. DcSets.Select(n => (JsonNode)n)]);
        if (MinPower != DefaultMinPower) o["MinPower"] = MinPower;
        if (Isothermal is { } iso) o["Isothermal"] = iso.ToJson();
        if (FitTau) o["FitTau"] = true;
        if (TauSets.Count > 0) o["TauSets"] = new JsonArray([.. TauSets.Select(n => (JsonNode)n)]);
        return o;
    }
}

/// <summary>Where an extracted Rth came from (R-mc9-3): the fit's RMS residual in kelvin and relative to the RMS
/// temperature rise, the points used, those left out for too little power and those whose junction temperature could
/// not be found, and the bias and power region of the points used.</summary>
public sealed record ThermalExtractionRecord(double Residual, double ResidualRelative, int Used, int LowPower, int Unresolved,
                                             (double Lo, double Hi) Vgs, (double Lo, double Hi) Vds, (double Lo, double Hi) Power);

/// <summary>A τth fitted to the low-frequency roll-off of Re Y22: the time constant, the RMS misfit relative to the RMS of
/// the roll-off's height, and the S points it read.</summary>
public sealed record TauFitRecord(double Tau, double Residual, int Points);

/// <summary>The thermal step's result as the document records it (R-mc9-1 … R-mc9-3).</summary>
public sealed record ThermalFitRecord
{
    public const string Extracted = "Extracted", Typed = "Typed";

    /// <summary>The data's distinct ambient temperatures, °C.</summary>
    public IReadOnlyList<double> Temperatures { get; init; } = [];
    /// <summary>The DC thermal resistance at <see cref="Tref"/>, K/W at the base periphery; null without self-heating.</summary>
    public double? Rth { get; init; }
    public double RthSlope { get; init; }
    public double Tref { get; init; } = 25;
    public string? RthSource { get; init; }
    /// <summary>The thermal network's poles; empty while the dynamics are not set.</summary>
    public IReadOnlyList<ThermalPole> Poles { get; init; } = [];
    public double? TauTh { get; init; }
    public ThermalExtractionRecord? Extraction { get; init; }
    public TauFitRecord? TauFit { get; init; }
    public IReadOnlyList<string> Report { get; init; } = [];

    private static readonly string[] Keys =
        ["Temperatures", "Rth", "RthSlope", "Tref", "RthSource", "Poles", "TauTh", "Extraction", "TauFit", "Report"];
    private static readonly string[] ExtractionKeys = ["Residual", "ResidualRelative", "Used", "LowPower", "Unresolved", "Vgs", "Vds", "Power"];

    public static ThermalFitRecord Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        IReadOnlyList<double> temps = o["Temperatures"] is { } tn
            ? tn is JsonArray ta ? [.. ta.Select((x, i) => NumberOf(x, $"{f}.Temperatures[{i}]"))]
                                 : throw new CmcFormatException($"{f}.Temperatures", "it is not a list.")
            : [];
        var poles = new List<ThermalPole>();
        if (o["Poles"] is { } pn)
        {
            if (pn is not JsonArray pa) throw new CmcFormatException($"{f}.Poles", "it is not a list.");
            for (int k = 0; k < pa.Count; k++)
            {
                string g = $"{f}.Poles[{k}]";
                var e = Obj(pa[k]!, g);
                Only(e, g, ["Rth", "Cth"]);
                poles.Add(new ThermalPole(Num(e, "Rth", $"{g}.Rth") ?? 0, Num(e, "Cth", $"{g}.Cth") ?? 0));
            }
        }
        ThermalExtractionRecord? extraction = null;
        if (o["Extraction"] is { } en)
        {
            string g = $"{f}.Extraction";
            var e = Obj(en, g);
            Only(e, g, ExtractionKeys);
            double N(string key) => Num(e, key, $"{g}.{key}") ?? 0;
            (double, double) P(string key) => e[key] is { } v ? CurrentFitSettings.Pair(v, $"{g}.{key}") : (0, 0);
            extraction = new ThermalExtractionRecord(N("Residual"), N("ResidualRelative"), (int)N("Used"), (int)N("LowPower"),
                                                     (int)N("Unresolved"), P("Vgs"), P("Vds"), P("Power"));
        }
        TauFitRecord? tau = null;
        if (o["TauFit"] is { } tf)
        {
            string g = $"{f}.TauFit";
            var e = Obj(tf, g);
            Only(e, g, ["Tau", "Residual", "Points"]);
            tau = new TauFitRecord(Num(e, "Tau", $"{g}.Tau") ?? 0, Num(e, "Residual", $"{g}.Residual") ?? 0,
                                   (int)(Num(e, "Points", $"{g}.Points") ?? 0));
        }
        IReadOnlyList<string> report = o["Report"] is { } rn
            ? rn is JsonArray a ? [.. a.Select((x, i) => x is JsonValue v && v.TryGetValue<string>(out var t) ? t
                                                         : throw new CmcFormatException($"{f}.Report[{i}]", "it is not a string."))]
                                : throw new CmcFormatException($"{f}.Report", "it is not a list.")
            : [];
        string? source = Str(o, "RthSource", $"{f}.RthSource", required: false);
        if (source is not null and not (Extracted or Typed))
            throw new CmcFormatException($"{f}.RthSource", $"'{source}' is not {Extracted} or {Typed}.");
        return new ThermalFitRecord
        {
            Temperatures = temps, Rth = Num(o, "Rth", $"{f}.Rth"), RthSlope = Num(o, "RthSlope", $"{f}.RthSlope") ?? 0,
            Tref = Num(o, "Tref", $"{f}.Tref") ?? 25, RthSource = source, Poles = poles, TauTh = Num(o, "TauTh", $"{f}.TauTh"),
            Extraction = extraction, TauFit = tau, Report = report,
        };
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        if (Temperatures.Count > 0) o["Temperatures"] = new JsonArray([.. Temperatures.Select(t => (JsonNode)t)]);
        if (Rth is { } r) o["Rth"] = r;
        if (RthSlope != 0) o["RthSlope"] = RthSlope;
        if (Rth is not null) o["Tref"] = Tref;
        if (RthSource is not null) o["RthSource"] = RthSource;
        if (Poles.Count > 0)
            o["Poles"] = new JsonArray([.. Poles.Select(p => (JsonNode)new JsonObject { ["Rth"] = p.Rth, ["Cth"] = p.Cth })]);
        if (TauTh is { } tau) o["TauTh"] = tau;
        if (Extraction is { } e)
            o["Extraction"] = new JsonObject
            {
                ["Residual"] = e.Residual, ["ResidualRelative"] = e.ResidualRelative, ["Used"] = e.Used, ["LowPower"] = e.LowPower,
                ["Unresolved"] = e.Unresolved, ["Vgs"] = new JsonArray(e.Vgs.Lo, e.Vgs.Hi), ["Vds"] = new JsonArray(e.Vds.Lo, e.Vds.Hi),
                ["Power"] = new JsonArray(e.Power.Lo, e.Power.Hi),
            };
        if (TauFit is { } t)
            o["TauFit"] = new JsonObject { ["Tau"] = t.Tau, ["Residual"] = t.Residual, ["Points"] = t.Points };
        if (Report.Count > 0) o["Report"] = new JsonArray([.. Report.Select(l => (JsonNode)l!)]);
        return o;
    }
}
