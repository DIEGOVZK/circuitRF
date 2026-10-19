// The .cmc's Pads and Network blocks (brief-modelcomposer-4 R-mc4-1, R-mc4-2, R-mc4-5; overview D5).
//
// MC-3 carried both as opaque JSON so a document saved before this phase lost nothing. They stay JsonNodes on
// CmcDocument — the writer still writes them back verbatim — and this file is their one reader and writer:
// CmcFile.Validate parses them through here, so a misspelt key is refused naming its field exactly as the rest
// of the document is, and a later phase adding a key adds it here.
//
//   "Pads":    { "Kind": "OpenShort", "Open": "pads/open.s2p", "Short": "pads/short.s2p" }
//              { "Kind": "TwoPorts",  "Gate": "pads/g.s2p",    "Drain": "pads/d.s2p" }      port 1 = probe side
//              { "Kind": "Network",   "Gate": "pads/g.csch",   "Drain": "pads/d.csch" }     pins 1 = probe, 2 = device
//   "Network": { "Schematic": "gan-two-shell.csch",
//                "Variables": { "rg": { "Min": 0, "Max": 520, "Lock": false, "Tie": { "To": "rd", "Factor": 1 },
//                                       "Scaling": "perFingerInverse" } },
//                "FlatnessBand": [1e9, 5e9] }
//
// Absent blocks mean no pads and no network: the data is already at the device plane, or already intrinsic.

using System.Globalization;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;

namespace CircuitRF.Design.Modeling;

/// <summary>How the launch is removed (R-mc4-1). The launch is never part of the model.</summary>
public enum PadsKind { None, OpenShort, TwoPorts, Network }

/// <summary>The pads block. Paths are relative to the <c>.cmc</c>.</summary>
public sealed record PadsSettings
{
    public PadsKind Kind { get; init; } = PadsKind.None;
    /// <summary>OpenShort: the open and short dummies, 2-port Touchstone files.</summary>
    public string? Open { get; init; }
    public string? Short { get; init; }
    /// <summary>TwoPorts: a Touchstone 2-port per side; Network: a two-pin <c>.csch</c> per side.</summary>
    public string? Gate { get; init; }
    public string? Drain { get; init; }

    public static PadsSettings None { get; } = new();
}

/// <summary>A tie: this variable is <see cref="To"/> times <see cref="Factor"/>.</summary>
public sealed record VariableTie(string To, double Factor = 1);

/// <summary>What the document says about one network variable (R-mc4-2). An unset bound takes the network's default.</summary>
public sealed record NetworkVariableSettings
{
    public double? Min { get; init; }
    public double? Max { get; init; }
    public bool Lock { get; init; }
    public VariableTie? Tie { get; init; }
    /// <summary>A <see cref="PeripheryScaling"/> rule name (D9); null takes the network's default.</summary>
    public string? Scaling { get; init; }
}

/// <summary>The network block.</summary>
public sealed record NetworkSettings
{
    /// <summary>The six-pin <c>.csch</c>, relative to the <c>.cmc</c>; null when the data is already intrinsic.</summary>
    public string? Schematic { get; init; }
    public IReadOnlyDictionary<string, NetworkVariableSettings> Variables { get; init; } =
        new Dictionary<string, NetworkVariableSettings>(StringComparer.Ordinal);
    /// <summary>The band the flatness metric spans, Hz (R-mc4-5); null takes 1/10 to 1/2 of the data's top frequency.</summary>
    public (double Lo, double Hi)? FlatnessBand { get; init; }

    public static NetworkSettings None { get; } = new();
}

public static class CmcExtrinsic
{
    private static readonly string[] PadsKeys = ["Kind", "Open", "Short", "Gate", "Drain"];
    private static readonly string[] NetworkKeys = ["Schematic", "Variables", "FlatnessBand"];
    private static readonly string[] VariableKeys = ["Min", "Max", "Lock", "Tie", "Scaling"];
    private static readonly string[] TieKeys = ["To", "Factor"];

    /// <summary>The document's pads block, validated; <see cref="PadsSettings.None"/> when absent.</summary>
    public static PadsSettings Pads(CmcDocument d) => d.Pads is null ? PadsSettings.None : ParsePads(d.Pads);

    /// <summary>The document's network block, validated; <see cref="NetworkSettings.None"/> when absent.</summary>
    public static NetworkSettings Network(CmcDocument d)
    {
        if (d.Network is null) return NetworkSettings.None;
        if (d.Kind == CmcKind.Passive)
            throw new CmcFormatException("Network", "a Passive document has no six-pin extrinsic network.");
        return ParseNetwork(d.Network);
    }

    /// <summary>The document with its pads block replaced (null for <see cref="PadsKind.None"/>).</summary>
    public static CmcDocument With(CmcDocument d, PadsSettings pads) =>
        d with { Pads = pads.Kind == PadsKind.None ? null : ToJson(pads) };

    /// <summary>The document with its network block replaced.</summary>
    public static CmcDocument With(CmcDocument d, NetworkSettings network) =>
        d with { Network = network.Schematic is null && network.Variables.Count == 0 && network.FlatnessBand is null ? null : ToJson(network) };

    // ── Reading ───────────────────────────────────────────────────────────────

    private static PadsSettings ParsePads(JsonNode node)
    {
        var o = Obj(node, "Pads");
        Only(o, "Pads", PadsKeys);
        string kindText = Str(o, "Kind", "Pads.Kind")!;
        if (!Enum.TryParse<PadsKind>(kindText, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
            throw new CmcFormatException("Pads.Kind", $"'{kindText}' is not None, OpenShort, TwoPorts or Network.");
        var p = new PadsSettings
        {
            Kind = kind, Open = Path(o, "Open", "Pads"), Short = Path(o, "Short", "Pads"),
            Gate = Path(o, "Gate", "Pads"), Drain = Path(o, "Drain", "Pads"),
        };
        (string, string?)[] need = kind switch
        {
            PadsKind.OpenShort => [("Open", p.Open), ("Short", p.Short)],
            PadsKind.TwoPorts or PadsKind.Network => [("Gate", p.Gate), ("Drain", p.Drain)],
            _ => [],
        };
        foreach (var (name, value) in need)
            if (value is null) throw new CmcFormatException($"Pads.{name}", $"{kind} pads need it.");
        foreach (string name in PadsKeys[1..])
            if (o[name] is not null && !need.Any(n => n.Item1 == name))
                throw new CmcFormatException($"Pads.{name}", $"it does not apply to {kind} pads.");
        return p;
    }

    private static NetworkSettings ParseNetwork(JsonNode node)
    {
        var o = Obj(node, "Network");
        Only(o, "Network", NetworkKeys);
        var vars = new Dictionary<string, NetworkVariableSettings>(StringComparer.Ordinal);
        if (o["Variables"] is { } vn)
        {
            var vo = Obj(vn, "Network.Variables");
            foreach (var (name, value) in vo)
            {
                string f = $"Network.Variables.{name}";
                var e = Obj(value!, f);
                Only(e, f, VariableKeys);
                VariableTie? tie = null;
                if (e["Tie"] is { } tn)
                {
                    var t = Obj(tn, $"{f}.Tie");
                    Only(t, $"{f}.Tie", TieKeys);
                    string to = Str(t, "To", $"{f}.Tie.To")!;
                    if (to == name) throw new CmcFormatException($"{f}.Tie.To", "a variable cannot be tied to itself.");
                    double factor = Num(t, "Factor", $"{f}.Tie.Factor") ?? 1;
                    if (factor == 0) throw new CmcFormatException($"{f}.Tie.Factor", "it is zero.");
                    tie = new VariableTie(to, factor);
                }
                string? scaling = Str(e, "Scaling", $"{f}.Scaling", required: false);
                if (scaling is not null && !PeripheryScaling.IsKnown(scaling))
                    throw new CmcFormatException($"{f}.Scaling",
                        $"'{scaling}' is not a known rule; the known rules are {string.Join(", ", PeripheryScaling.Known)}.");
                var s = new NetworkVariableSettings
                {
                    Min = Num(e, "Min", $"{f}.Min"), Max = Num(e, "Max", $"{f}.Max"),
                    Lock = Bool(e, "Lock", $"{f}.Lock"), Tie = tie, Scaling = scaling,
                };
                if (s.Min is { } lo && s.Max is { } hi && !(lo <= hi))
                    throw new CmcFormatException($"{f}.Max", $"{G(hi)} is below the minimum {G(lo)}.");
                vars[name] = s;
            }
        }
        foreach (var (name, s) in vars)
            if (s.Tie is { } tie && vars.TryGetValue(tie.To, out var target) && target.Tie is not null)
                throw new CmcFormatException($"Network.Variables.{name}.Tie.To", $"'{tie.To}' is itself tied; tie both to the same variable.");

        (double, double)? band = null;
        if (o["FlatnessBand"] is { } bn)
        {
            if (bn is not JsonArray arr || arr.Count != 2)
                throw new CmcFormatException("Network.FlatnessBand", "it is not a list of two frequencies in Hz.");
            double lo = NumberOf(arr[0], "Network.FlatnessBand[0]"), hi = NumberOf(arr[1], "Network.FlatnessBand[1]");
            if (!(lo >= 0 && hi > lo)) throw new CmcFormatException("Network.FlatnessBand", $"{G(lo)} to {G(hi)} Hz is not a band.");
            band = (lo, hi);
        }
        return new NetworkSettings { Schematic = Path(o, "Schematic", "Network"), Variables = vars, FlatnessBand = band };
    }

    // ── Writing (fixed order, so the same block writes the same bytes) ────────

    public static JsonNode ToJson(PadsSettings p)
    {
        var o = new JsonObject { ["Kind"] = p.Kind.ToString() };
        if (p.Open is not null) o["Open"] = p.Open;
        if (p.Short is not null) o["Short"] = p.Short;
        if (p.Gate is not null) o["Gate"] = p.Gate;
        if (p.Drain is not null) o["Drain"] = p.Drain;
        return o;
    }

    public static JsonNode ToJson(NetworkSettings n)
    {
        var o = new JsonObject();
        if (n.Schematic is not null) o["Schematic"] = n.Schematic;
        if (n.Variables.Count > 0)
        {
            var vo = new JsonObject();
            foreach (var (name, s) in n.Variables.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var e = new JsonObject();
                if (s.Min is { } lo) e["Min"] = lo;
                if (s.Max is { } hi) e["Max"] = hi;
                if (s.Lock) e["Lock"] = true;
                if (s.Tie is { } t) e["Tie"] = new JsonObject { ["To"] = t.To, ["Factor"] = t.Factor };
                if (s.Scaling is not null) e["Scaling"] = s.Scaling;
                vo[name] = e;
            }
            o["Variables"] = vo;
        }
        if (n.FlatnessBand is { } b) o["FlatnessBand"] = new JsonArray(b.Lo, b.Hi);
        return o;
    }

    // ── Field helpers: every miss names its field ────────────────────────────

    private static JsonObject Obj(JsonNode node, string f) =>
        node as JsonObject ?? throw new CmcFormatException(f, "it is not an object.");

    private static void Only(JsonObject o, string f, string[] keys)
    {
        foreach (var (name, _) in o)
            if (Array.IndexOf(keys, name) < 0) throw new CmcFormatException($"{f}.{name}", "it is not a field this circuitRF knows.");
    }

    private static string? Str(JsonObject o, string name, string f, bool required = true)
    {
        var v = o[name];
        if (v is null)
            return required ? throw new CmcFormatException(f, "it is missing.") : null;
        if (v is not JsonValue jv || !jv.TryGetValue<string>(out var s)) throw new CmcFormatException(f, "it is not a string.");
        return s;
    }

    private static string? Path(JsonObject o, string name, string block)
    {
        string f = $"{block}.{name}";
        string? s = Str(o, name, f, required: false);
        if (s is null) return null;
        if (string.IsNullOrWhiteSpace(s)) throw new CmcFormatException(f, "it is empty.");
        if (System.IO.Path.IsPathRooted(s)) throw new CmcFormatException(f, $"'{s}' is not relative to the .cmc.");
        return s;
    }

    private static double? Num(JsonObject o, string name, string f) => o[name] is { } v ? NumberOf(v, f) : null;

    private static double NumberOf(JsonNode? v, string f)
    {
        if (v is JsonValue jv && jv.TryGetValue<double>(out var x) && double.IsFinite(x)) return x;
        throw new CmcFormatException(f, "it is not a number.");
    }

    private static bool Bool(JsonObject o, string name, string f)
    {
        var v = o[name];
        if (v is null) return false;
        if (v is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        throw new CmcFormatException(f, "it is not true or false.");
    }

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
