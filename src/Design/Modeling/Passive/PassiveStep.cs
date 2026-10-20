// The passive model as the .cmc carries it (brief-modelcomposer-12; overview D2, D12, D13), and the operations the
// window (MC-14) and the `model` verb (MC-13) call on a document: analyse, propose, fit, export.
//
//   "Passive": {
//     "Settings": { "Set": "spirals", "Block": "spiral_3.5t.s2p", "Topology": "spiral",
//                   "Variables": { "Cox2": { "Tie": { "To": "Cox1" } } },
//                   "Fit": { …PassiveFitSettings… },
//                   "Family": { "Geometry": "turns", "Form": "Auto", "Monotone": true, "HeldOut": 3.5 },
//                   "Export": { "Cell": "Spiral", "Library": "../lib", "Symbol": "symbols/box.csym" } },
//     "Result":   { "Topology": "spiral", "Values": { "Rs": 1.2, … }, "Cost": 1e-9, "RmsDeltaS": 2e-5, "Aic": -9000,
//                   "Family": { "Geometry": "turns", "Lo": 1.5, "Hi": 6.5, "HeldOut": 3.5, "HeldOutRms": 2e-4,
//                               "HeldOutMax": 4e-4, "Laws": { "Ls": { "Form": "Cubic", "Coefficients": […], "Mid": 4,
//                               "Half": 2.5, "LeaveOneOut": 9e-4 } }, "Locked": { … }, "Ties": { … } },
//                   "Report": [ "…" ] },
//     "Network": "…", "Data": "…" }                       (CmcStamps: what the fit read)
//
// Topology is a library id or a .csch path relative to the .cmc; Library and Symbol are relative to the .cmc too. A
// family fits every block of the set; without Family one block is fitted — the one named, or the set's only one. Every
// operation takes a RunControl, and a cancelled one leaves the document — and the file — as they were.

using System.Globalization;
using System.Text.Json.Nodes;
using CircuitRF.Engine;
using static CircuitRF.Design.Modeling.CmcExtrinsic;

namespace CircuitRF.Design.Modeling.Passive;

/// <summary>Where an exported cell goes: its name, the library folder (relative to the <c>.cmc</c>) and an optional symbol.</summary>
public sealed record PassiveExportSettings(string Cell, string Library, string? Symbol = null);

/// <summary>The passive step's settings.</summary>
public sealed record PassiveStepSettings
{
    /// <summary>The data set; null is the document's only passive set.</summary>
    public string? Set { get; init; }
    /// <summary>The block a single fit reads, by point id; null is the set's only block.</summary>
    public string? Block { get; init; }
    /// <summary>A library id, or a <c>.csch</c> relative to the <c>.cmc</c>.</summary>
    public string? Topology { get; init; }
    public IReadOnlyDictionary<string, NetworkVariableSettings> Variables { get; init; } =
        new Dictionary<string, NetworkVariableSettings>(StringComparer.Ordinal);
    public PassiveFitSettings Fit { get; init; } = PassiveFitSettings.Default;
    /// <summary>Fit the set as a family over a geometry value; null fits one block.</summary>
    public FamilySettings? Family { get; init; }
    public PassiveExportSettings? Export { get; init; }

    public static PassiveStepSettings Default { get; } = new();

    private static readonly string[] Keys = ["Set", "Block", "Topology", "Variables", "Fit", "Family", "Export"];

    public static PassiveStepSettings Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        PassiveExportSettings? export = null;
        if (o["Export"] is { } en)
        {
            string g = $"{f}.Export";
            var e = Obj(en, g);
            Only(e, g, ["Cell", "Library", "Symbol"]);
            export = new PassiveExportSettings(Str(e, "Cell", $"{g}.Cell")!, Str(e, "Library", $"{g}.Library")!,
                                               Str(e, "Symbol", $"{g}.Symbol", required: false));
            if (CircuitRF.Design.Cells.NameValidator.Validate(export.Cell) is { } why)
                throw new CmcFormatException($"{g}.Cell", $"'{export.Cell}' is not a cell name: {why}");
        }
        var s = new PassiveStepSettings
        {
            Set = Str(o, "Set", $"{f}.Set", required: false),
            Block = Str(o, "Block", $"{f}.Block", required: false),
            Topology = Str(o, "Topology", $"{f}.Topology", required: false),
            Variables = o["Variables"] is { } vn ? ParseVariables(vn, $"{f}.Variables")
                                                 : new Dictionary<string, NetworkVariableSettings>(StringComparer.Ordinal),
            Fit = o["Fit"] is { } fn ? PassiveFitSettings.Parse(fn, $"{f}.Fit") : PassiveFitSettings.Default,
            Family = o["Family"] is { } fam ? FamilySettings.Parse(fam, $"{f}.Family") : null,
            Export = export,
        };
        if (s.Topology is { } t && !t.EndsWith(".csch", StringComparison.OrdinalIgnoreCase)
            && PassiveTopologies.All.All(x => x.Id != t))
            throw new CmcFormatException($"{f}.Topology",
                $"'{t}' is neither a .csch nor a library topology; the library is {string.Join(", ", PassiveTopologies.All.Select(x => x.Id))}.");
        if (s.Family is not null && s.Block is not null)
            throw new CmcFormatException($"{f}.Block", "a family fits every block of the set; name no block.");
        return s;
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        if (Set is not null) o["Set"] = Set;
        if (Block is not null) o["Block"] = Block;
        if (Topology is not null) o["Topology"] = Topology;
        if (Variables.Count > 0) o["Variables"] = VariablesJson(Variables);
        if (Fit != PassiveFitSettings.Default) o["Fit"] = Fit.ToJson();
        if (Family is { } fam) o["Family"] = fam.ToJson();
        if (Export is { } e)
        {
            var eo = new JsonObject { ["Cell"] = e.Cell, ["Library"] = e.Library };
            if (e.Symbol is not null) eo["Symbol"] = e.Symbol;
            o["Export"] = eo;
        }
        return o;
    }
}

/// <summary>What a passive fit leaves in the document: the values (one block) or the laws (a family), and its report.</summary>
public sealed record PassiveStepRecord
{
    public required string Topology { get; init; }
    public IReadOnlyDictionary<string, double> Values { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);
    public double Cost { get; init; }
    public double RmsDeltaS { get; init; }
    public double Aic { get; init; }
    public FamilyFitResult? Family { get; init; }
    public IReadOnlyList<string> Report { get; init; } = [];

    private static readonly string[] Keys =
        ["Topology", "Values", "Cost", "RmsDeltaS", "Aic", "Family", "Report"];
    private static readonly string[] FamilyKeys =
        ["Geometry", "Lo", "Hi", "HeldOut", "HeldOutRms", "HeldOutMax", "Laws", "Locked", "Ties"];

    public static PassiveStepRecord Parse(JsonNode node, string f)
    {
        var o = Obj(node, f);
        Only(o, f, Keys);
        FamilyFitResult? family = null;
        if (o["Family"] is { } fn)
        {
            string g = $"{f}.Family";
            var fo = Obj(fn, g);
            Only(fo, g, FamilyKeys);
            double N(string k) => Num(fo, k, $"{g}.{k}") ?? throw new CmcFormatException($"{g}.{k}", "it is missing.");
            var laws = new List<ElementLaw>();
            foreach (var (name, ln) in Obj(fo["Laws"] ?? new JsonObject(), $"{g}.Laws"))
            {
                string h = $"{g}.Laws.{name}";
                var lo = Obj(ln!, h);
                Only(lo, h, ["Form", "Coefficients", "Mid", "Half", "LeaveOneOut"]);
                string form = Str(lo, "Form", $"{h}.Form")!;
                if (!Enum.TryParse<FamilyForm>(form, false, out var ff) || ff == FamilyForm.Auto || !Enum.IsDefined(ff))
                    throw new CmcFormatException($"{h}.Form", $"'{form}' is not a law's form.");
                double[] c = lo["Coefficients"] is JsonArray ca ? [.. ca.Select((x, i) => NumberOf(x, $"{h}.Coefficients[{i}]"))]
                           : throw new CmcFormatException($"{h}.Coefficients", "it is not a list of numbers.");
                int need = ff switch { FamilyForm.Constant => 1, FamilyForm.Linear => 2, FamilyForm.Cubic => 4, _ => 3 };
                if (c.Length != need) throw new CmcFormatException($"{h}.Coefficients", $"a {ff} law has {need} coefficients, not {c.Length}.");
                laws.Add(new ElementLaw(name, ff, c, Num(lo, "Mid", $"{h}.Mid") ?? 0, Num(lo, "Half", $"{h}.Half") ?? 0,
                                        Num(lo, "LeaveOneOut", $"{h}.LeaveOneOut") ?? double.NaN));
            }
            var ties = new Dictionary<string, VariableTie>(StringComparer.Ordinal);
            foreach (var (name, tn) in Obj(fo["Ties"] ?? new JsonObject(), $"{g}.Ties"))
            {
                var t = Obj(tn!, $"{g}.Ties.{name}");
                Only(t, $"{g}.Ties.{name}", ["To", "Factor"]);
                ties[name] = new VariableTie(Str(t, "To", $"{g}.Ties.{name}.To")!, Num(t, "Factor", $"{g}.Ties.{name}.Factor") ?? 1);
            }
            var settings = new FamilySettings { Geometry = Str(fo, "Geometry", $"{g}.Geometry")! };
            family = new FamilyFitResult(Str(o, "Topology", $"{f}.Topology")!, settings, N("Lo"), N("Hi"), [], laws,
                                         Numbers(fo["Locked"], $"{g}.Locked"), ties, N("HeldOut"), N("HeldOutRms"), N("HeldOutMax"),
                                         double.NaN, double.NaN, []);
        }
        return new PassiveStepRecord
        {
            Topology = Str(o, "Topology", $"{f}.Topology")!,
            Values = Numbers(o["Values"], $"{f}.Values"),
            Cost = Num(o, "Cost", $"{f}.Cost") ?? 0,
            RmsDeltaS = Num(o, "RmsDeltaS", $"{f}.RmsDeltaS") ?? 0,
            Aic = Num(o, "Aic", $"{f}.Aic") ?? 0,
            Family = family,
            Report = o["Report"] is JsonArray r ? [.. r.Select((x, i) => x is JsonValue v && v.TryGetValue<string>(out var t) ? t
                                                               : throw new CmcFormatException($"{f}.Report[{i}]", "it is not text."))] : [],
        };
    }

    private static Dictionary<string, double> Numbers(JsonNode? n, string f)
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        if (n is null) return d;
        foreach (var (k, v) in Obj(n, f)) d[k] = NumberOf(v, $"{f}.{k}");
        return d;
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject { ["Topology"] = Topology };
        if (Values.Count > 0) o["Values"] = Ordered(Values);
        o["Cost"] = Cost;
        o["RmsDeltaS"] = RmsDeltaS;
        o["Aic"] = Aic;
        if (Family is { } f)
        {
            var laws = new JsonObject();
            foreach (var l in f.Laws)
            {
                var lo = new JsonObject { ["Form"] = l.Form.ToString(), ["Coefficients"] = new JsonArray([.. l.Coefficients.Select(c => (JsonNode)c)]),
                                          ["Mid"] = l.Mid, ["Half"] = l.Half };
                if (double.IsFinite(l.LeaveOneOutError)) lo["LeaveOneOut"] = l.LeaveOneOutError;
                laws[l.Variable] = lo;
            }
            var ties = new JsonObject();
            foreach (var (name, t) in f.Ties.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                ties[name] = new JsonObject { ["To"] = t.To, ["Factor"] = t.Factor };
            o["Family"] = new JsonObject
            {
                ["Geometry"] = f.Settings.Geometry, ["Lo"] = f.Lo, ["Hi"] = f.Hi, ["HeldOut"] = f.HeldOutGeometry,
                ["HeldOutRms"] = f.HeldOutRms, ["HeldOutMax"] = f.HeldOutMax, ["Laws"] = laws,
                ["Locked"] = Ordered(f.Locked), ["Ties"] = ties,
            };
        }
        if (Report.Count > 0) o["Report"] = new JsonArray([.. Report.Select(x => (JsonNode)x)]);
        return o;
    }

    private static JsonObject Ordered(IReadOnlyDictionary<string, double> d)
    {
        var o = new JsonObject();
        foreach (var (k, v) in d.OrderBy(kv => kv.Key, StringComparer.Ordinal)) o[k] = v;
        return o;
    }
}

/// <summary>A document's passive fit: one block's, or a family's.</summary>
public sealed record PassiveStepResult(PassiveFitResult? Single, FamilyFitResult? Family, PassiveStepRecord Record);

public static class PassiveStep
{
    public const string StepName = "Passive";

    public static PassiveStepSettings Settings(CmcDocument doc) =>
        Step(doc)?["Settings"] is { } n ? PassiveStepSettings.Parse(n, $"Steps.{StepName}.Settings") : PassiveStepSettings.Default;

    public static PassiveStepRecord? Record(CmcDocument doc) =>
        Step(doc)?["Result"] is { } n ? PassiveStepRecord.Parse(n, $"Steps.{StepName}.Result") : null;

    /// <summary>The block's keys checked — <see cref="CmcFile.Validate"/>'s call.</summary>
    public static void Validate(CmcDocument doc)
    {
        if (Step(doc) is not { } o) return;
        if (doc.Kind != CmcKind.Passive)
            throw new CmcFormatException($"Steps.{StepName}", "an Active document has no passive step.");
        Only(o, $"Steps.{StepName}", ["Settings", "Result", CmcStamps.NetworkKey, CmcStamps.DataKey]);
        Settings(doc);
        Record(doc);
    }

    /// <summary>The document with <paramref name="settings"/> recorded; the previous result, if any, is kept.</summary>
    public static CmcDocument WithSettings(CmcDocument doc, PassiveStepSettings settings)
    {
        var steps = StepsCopy(doc);
        var o = steps[StepName] as JsonObject ?? new JsonObject();
        o["Settings"] = settings.ToJson();
        steps[StepName] = o;
        return doc with { Steps = steps };
    }

    public static CmcDocument Apply(CmcDocument doc, PassiveStepSettings settings, PassiveStepRecord record)
    {
        var steps = StepsCopy(doc);
        steps[StepName] = new JsonObject { ["Settings"] = settings.ToJson(), ["Result"] = record.ToJson() };
        return doc with { Steps = steps };
    }

    private static JsonObject? Step(CmcDocument doc) =>
        doc.Steps is JsonObject s && s[StepName] is { } n ? Obj(n, $"Steps.{StepName}") : null;

    private static JsonObject StepsCopy(CmcDocument doc) =>
        doc.Steps is JsonObject s ? (JsonObject)JsonNode.Parse(s.ToJsonString())! : new JsonObject();

    // ── The operations ────────────────────────────────────────────────────────

    /// <summary>The blocks of the step's data set, in the order the set lists its sources.</summary>
    public static IReadOnlyList<PassiveBlock> Blocks(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data, PassiveStepSettings s)
    {
        if (doc.Kind != CmcKind.Passive) throw new ModelingException("Kind", "an Active document has no passive model.");
        var sets = doc.DataSets.Where(d => d.Kind == DataSetKind.PassiveSParams).ToList();
        var set = s.Set is { } name
            ? sets.FirstOrDefault(d => d.Name == name) ?? throw new ModelingException($"Steps.{StepName}.Settings.Set", $"no passive data set is named '{name}'.")
            : sets.Count == 1 ? sets[0]
            : throw new ModelingException($"Steps.{StepName}.Settings.Set",
                  sets.Count == 0 ? "the document has no passive data set." : $"the document has {sets.Count} passive data sets ({string.Join(", ", sets.Select(d => d.Name))}); name one.");
        if (!data.TryGetValue(set.Name, out var nd)) throw new ModelingException(set.Name, "its data is not open.");
        return PassiveBlock.From(nd);               // the cache has applied the set's exclusions
    }

    private static PassiveBlock Single(IReadOnlyList<PassiveBlock> blocks, PassiveStepSettings s) =>
        s.Block is { } b ? blocks.FirstOrDefault(x => x.Name == b) ?? throw new ModelingException($"Steps.{StepName}.Settings.Block", $"the set has no block '{b}'.")
        : blocks.Count == 1 ? blocks[0]
        : throw new ModelingException($"Steps.{StepName}.Settings.Block",
              $"the set has {blocks.Count} blocks ({string.Join(", ", blocks.Select(x => x.Name))}); name one, or fit them as a family.");

    /// <summary>Analyses the step's block — or each of a family's — (R-mc12-2). Writes nothing.</summary>
    public static IReadOnlyList<PassiveAnalysisResult> AnalyseFile(string cmcPath, RunControl? rc = null)
    {
        var (doc, data, s) = Open(cmcPath, rc);
        var blocks = Blocks(doc, data, s);
        var chosen = s.Family is null && blocks.Count > 1 ? [Single(blocks, s)] : blocks;
        return [.. chosen.Select(b => PassiveAnalysis.Run(b, rc))];
    }

    /// <summary>Ranked topology proposals for the step's block (a family's middle one) (R-mc12-4); a user topology named
    /// by the step is fitted beside the library's. Writes nothing — choosing one is the user's act.</summary>
    public static IReadOnlyList<PassiveProposal> ProposeFile(string cmcPath, List<string>? notes = null, RunControl? rc = null)
    {
        var (doc, data, s) = Open(cmcPath, rc);
        var blocks = Blocks(doc, data, s);
        var block = s.Family is null ? Single(blocks, s) : blocks.OrderBy(b => b.Geometry).ElementAt((blocks.Count - 1) / 2);
        var drawn = s.Topology is { } t && t.EndsWith(".csch", StringComparison.OrdinalIgnoreCase)
            ? new[] { PassiveNetwork.Load(Resolve(cmcPath, t)) } : [];
        // The step's variable settings are the drawn topology's (FitFile fits it with them), not the library's.
        return PassiveFit.Propose(block, PassiveAnalysis.Run(block, rc), s.Fit, drawn, notes, rc, s.Variables);
    }

    /// <summary>
    /// Reads the <c>.cmc</c>, fits the step's topology — one block, or the set as a family — and writes the result into
    /// the document (R-mc12-5, R-mc12-6). Cancelled or refused, nothing is written.
    /// </summary>
    public static PassiveStepResult FitFile(string cmcPath, RunControl? rc = null)
    {
        var (doc, data, s) = Open(cmcPath, rc);
        var result = Fit(cmcPath, doc, data, s, rc);
        rc?.ThrowIfCancellationRequested();
        CmcFile.Write(cmcPath, CmcStamps.Stamp(Apply(doc, s, result.Record), StepName, CmcStamps.DataDigest(cmcPath, doc)));
        return result;
    }

    /// <summary>The fit <see cref="FitFile"/> runs, on an open document; nothing is written.</summary>
    public static PassiveStepResult Fit(string cmcPath, CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data,
                                        PassiveStepSettings s, RunControl? rc = null)
    {
        var blocks = Blocks(doc, data, s);
        string topo = s.Topology ?? throw new ModelingException($"Steps.{StepName}.Settings.Topology",
                                                                "no topology is chosen; the proposals rank the library's.");
        bool stock = !topo.EndsWith(".csch", StringComparison.OrdinalIgnoreCase);
        var network = stock ? PassiveTopologies.Find(topo).Load() : PassiveNetwork.Load(Resolve(cmcPath, topo));
        var lib = stock ? PassiveTopologies.Find(topo) : null;
        if (s.Family is { } fam)
        {
            var family = FamilyFit.Run(blocks, network, fam, s.Fit, s.Variables, lib?.Seed, lib?.Positive, topo, rc);
            var rec = new PassiveStepRecord
            {
                Topology = topo, Cost = 0, RmsDeltaS = family.TrainingRms, Aic = 0, Family = family, Report = family.Lines,
            };
            return new PassiveStepResult(null, family, rec);
        }
        var block = Single(blocks, s);
        var starts = lib?.Seed(PassiveAnalysis.Run(block, rc));
        var fit = PassiveFit.Fit(block, network, s.Fit, s.Variables, starts, lib?.Positive, quick: false, topology: topo, rc: rc);
        var lines = new List<string> { $"{topo}: rms |ΔS| {fit.RmsDeltaS.ToString("0.0E+0", CultureInfo.InvariantCulture)}, max {fit.MaxDeltaS.ToString("0.0E+0", CultureInfo.InvariantCulture)}" };
        lines.AddRange(fit.Report.Lines);
        lines.AddRange(fit.Notes);
        var record = new PassiveStepRecord
        {
            Topology = topo, Values = fit.Values, Cost = fit.Cost, RmsDeltaS = fit.RmsDeltaS, Aic = fit.Aic, Report = lines,
        };
        return new PassiveStepResult(fit, null, record);
    }

    /// <summary>
    /// Creates the fitted model as a cell (R-mc12-7) where <c>Settings.Export</c> says. Refuses with no fit, a stale fit
    /// (the data changed since) or an existing cell name; the document itself is not changed.
    /// </summary>
    public static PassiveExportResult ExportFile(string cmcPath)
    {
        var doc = CmcFile.Read(cmcPath);
        var s = Settings(doc);
        var e = s.Export ?? throw new ModelingException($"Steps.{StepName}.Settings.Export", "it names no cell and library to export to.");
        var record = Record(doc) ?? throw new ModelingException($"Steps.{StepName}", "nothing is fitted yet; fit before exporting.");
        if (CmcStamps.StaleSteps(cmcPath, doc).Contains(StepName))
            throw new ModelingException($"Steps.{StepName}", CmcStamps.Describe([StepName]));
        string topo = record.Topology.EndsWith(".csch", StringComparison.OrdinalIgnoreCase) ? Resolve(cmcPath, record.Topology) : record.Topology;
        string library = Resolve(cmcPath, e.Library);
        string? symbol = e.Symbol is null ? null : Resolve(cmcPath, e.Symbol);
        return record.Family is { } fam
            ? PassiveCellExport.ExportFamily(library, e.Cell, topo, fam, symbol)
            : PassiveCellExport.Export(library, e.Cell, topo, record.Values, symbol);
    }

    private static (CmcDocument, IReadOnlyDictionary<string, NormalisedData>, PassiveStepSettings) Open(string cmcPath, RunControl? rc)
    {
        var doc = CmcFile.Read(cmcPath);
        var data = new CmcCache(cmcPath).Open(doc, rc).Data;
        return (doc, data, Settings(doc));
    }

    private static string Resolve(string cmcPath, string relative) =>
        DataImport.ResolveSource(Path.GetDirectoryName(Path.GetFullPath(cmcPath))!, relative);
}
