// Validation (brief-modelcomposer-11 R-mc11-4, R-mc11-5; overview D13, D16): how well the composed model predicts every
// kind of data the document holds, at the MEASUREMENT plane, through the engines a design will use.
//
//   S-parameters  every measured bias of every CW S set: the model's S at the device plane (the testbench at the data's
//                 device-plane bias, through ideal tees) with the pads re-embedded, against the data. |ΔS| per bias, and
//                 its median and 95th percentile over every entry — small entries are judged by |ΔS|, never by dB.
//   DC I–V        every point of every DC set: the model's terminal currents at the device-plane bias (a self-heating model
//                 heats itself, as the measurement did), RMS ΔId relative to the set's current range.
//   Pulsed I–V    every mapped point of every pulsed set: the channel current at the point's mapped trap state and its
//                 quiescent junction temperature (MC-10's mapping, MC-9's heating). A circuit's DC solve cannot hold a
//                 trap state, so this one is evaluated at the core, on the intrinsic data MC-4 computed.
//   Large signal  a power sweep (Pout, gain, PAE, DE, drain current against input power) and a two-tone sweep (carrier,
//                 IM3, IM5) by HB with the data's terminations; a loadpull file run through the loadpull engine over its own
//                 loads, the model interpolated to each measured drive.
//
// Every item but the pulsed one generates an ordinary .cnl testbench and runs it through the function its verb calls
// (SParameterEngine, NonlinearDcEngine, CircuitEvaluation for HB and loadpull). Results are DataSets with measured and
// model cubes on the same axes, for MC-15's overlays; the summary is one line per item. Tolerances are settings, kept in
// the .cmc (Steps.Validate). A cancelled validation reports nothing (D13): it throws.

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Engine;
using RfCore.Data;
using RfCore.Loadpull;

namespace CircuitRF.Design.Modeling;

/// <summary>What one validation item compares.</summary>
public enum ValidationKind { SParams, DcIv, PulsedIv, PowerSweep, TwoTone, Loadpull }

/// <summary>The tolerances an item passes within (R-mc11-4).</summary>
public sealed record ValidationTolerances
{
    /// <summary>The median and the 95th percentile of |ΔS| over every entry.</summary>
    public double SMedian { get; init; } = 0.01;
    public double S95 { get; init; } = 0.03;
    /// <summary>RMS ΔId over the set's current range.</summary>
    public double IvRelative { get; init; } = 0.02;
    /// <summary>RMS ΔPout (and ΔIM3, ΔIM5), dB.</summary>
    public double PowerDb { get; init; } = 0.5;
    /// <summary>RMS ΔPAE (or ΔDE), percentage points.</summary>
    public double EfficiencyPoints { get; init; } = 3;

    public static ValidationTolerances Default { get; } = new();
}

/// <summary>The validation's settings: tolerances, the data sets (empty: every one), the harmonic count.</summary>
public sealed record ValidationSettings
{
    public ValidationTolerances Tolerances { get; init; } = ValidationTolerances.Default;
    public IReadOnlyList<string> DataSets { get; init; } = [];
    public int MaxHarmonic { get; init; } = 5;

    public static ValidationSettings Default { get; } = new();

    private static readonly string[] Keys = ["Tolerances", "DataSets", "MaxHarmonic"];
    private static readonly string[] ToleranceKeys = ["SMedian", "S95", "IvRelative", "PowerDb", "EfficiencyPoints"];

    public static ValidationSettings Parse(JsonNode node, string f)
    {
        var o = CmcExtrinsic.Obj(node, f);
        CmcExtrinsic.Only(o, f, Keys);
        var t = ValidationTolerances.Default;
        if (o["Tolerances"] is { } tn)
        {
            var to = CmcExtrinsic.Obj(tn, $"{f}.Tolerances");
            CmcExtrinsic.Only(to, $"{f}.Tolerances", ToleranceKeys);
            double P(string k, double d)
            {
                double v = CmcExtrinsic.Num(to, k, $"{f}.Tolerances.{k}") ?? d;
                return v > 0 ? v : throw new CmcFormatException($"{f}.Tolerances.{k}", $"{v} is not positive.");
            }
            t = new ValidationTolerances
            {
                SMedian = P("SMedian", t.SMedian), S95 = P("S95", t.S95), IvRelative = P("IvRelative", t.IvRelative),
                PowerDb = P("PowerDb", t.PowerDb), EfficiencyPoints = P("EfficiencyPoints", t.EfficiencyPoints),
            };
        }
        int harm = (int)(CmcExtrinsic.Num(o, "MaxHarmonic", $"{f}.MaxHarmonic") ?? 5);
        if (harm < 1) throw new CmcFormatException($"{f}.MaxHarmonic", $"{harm} is not a harmonic count.");
        return new ValidationSettings
        {
            Tolerances = t,
            DataSets = o["DataSets"] is { } d ? CurrentFitSettings.Names(d, $"{f}.DataSets") : [],
            MaxHarmonic = harm,
        };
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        var t = Tolerances;
        if (t != ValidationTolerances.Default)
            o["Tolerances"] = new JsonObject
            {
                ["SMedian"] = t.SMedian, ["S95"] = t.S95, ["IvRelative"] = t.IvRelative,
                ["PowerDb"] = t.PowerDb, ["EfficiencyPoints"] = t.EfficiencyPoints,
            };
        if (DataSets.Count > 0) o["DataSets"] = new JsonArray([.. DataSets.Select(n => (JsonNode)n)]);
        if (MaxHarmonic != 5) o["MaxHarmonic"] = MaxHarmonic;
        return o;
    }
}

/// <summary>
/// One item: the data set, what it compared, whether it passed, its one line, its measured-and-model DataSet and the
/// testbench it ran (empty for the pulsed item, which runs no circuit).
/// </summary>
public sealed record ValidationItem(string Set, ValidationKind Kind, bool Passed, string Line, DataSet Data, string Testbench);

/// <summary>A validation: the model it ran, every item, and a line for each set that has no item (and why).</summary>
public sealed record ValidationResult(string ModelPath, IReadOnlyList<ValidationItem> Items, IReadOnlyList<string> Notes)
{
    public bool Passed => Items.All(i => i.Passed);
    public IReadOnlyList<string> Lines => [.. Items.Select(i => i.Line), .. Notes];
}

public static class ModelValidation
{
    public const string StepName = "Validate";

    /// <summary>The folder, in the document's cache, that the current state is exported to when no .cmodel is named.</summary>
    public const string CacheFolder = "validate";

    // ── The document ──────────────────────────────────────────────────────────

    public static ValidationSettings Settings(CmcDocument doc) =>
        Step(doc)?["Settings"] is { } s ? ValidationSettings.Parse(s, $"Steps.{StepName}.Settings") : ValidationSettings.Default;

    public static void Validate(CmcDocument doc)
    {
        if (Step(doc) is not { } o) return;
        CmcExtrinsic.Only(o, $"Steps.{StepName}", ["Settings"]);
        Settings(doc);
    }

    public static CmcDocument WithSettings(CmcDocument doc, ValidationSettings settings)
    {
        var steps = doc.Steps is JsonObject s ? (JsonObject)JsonNode.Parse(s.ToJsonString())! : new JsonObject();
        steps[StepName] = new JsonObject { ["Settings"] = settings.ToJson() };
        return doc with { Steps = steps };
    }

    private static JsonObject? Step(CmcDocument doc) =>
        doc.Steps is JsonObject s && s[StepName] is { } n ? CmcExtrinsic.Obj(n, $"Steps.{StepName}") : null;

    // ── The run ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates <paramref name="cmodelPath"/>, or — when it is null — the document's current state, exported into its
    /// cache (<c>&lt;name&gt;.cmc-cache/validate/model.cmodel</c>, derived data) — what the window and the <c>model</c> verb
    /// call (R-mc11-4). Throws <see cref="OperationCanceledException"/> when cancelled: a cancelled validation reports
    /// nothing.
    /// </summary>
    public static ValidationResult RunFile(string cmcPath, string? cmodelPath = null, ValidationSettings? settings = null,
                                           RunControl? rc = null)
    {
        cmcPath = Path.GetFullPath(cmcPath);
        var doc = CmcFile.Read(cmcPath);
        settings ??= Settings(doc);
        var (model, data, work) = Prepare(cmcPath, doc, cmodelPath, rc);
        return Run(cmcPath, doc, data, model, settings, work, rc);
    }

    /// <summary>
    /// What a run needs before its items: the document's data, the work folder, and the model's path — <paramref name="cmodelPath"/>,
    /// or the current state exported into the work folder when it is null.
    /// </summary>
    internal static (string Model, IReadOnlyDictionary<string, NormalisedData> Data, string Work) Prepare(
        string cmcPath, CmcDocument doc, string? cmodelPath, RunControl? rc)
    {
        // The cache opens first: a rebuild swaps its whole folder, and would take a work folder made before it along.
        var data = new CmcCache(cmcPath).Open(doc, rc).Data;
        string work = Path.Combine(CmcCache.FolderFor(cmcPath), CacheFolder);
        Directory.CreateDirectory(work);
        if (cmodelPath is null)
        {
            cmodelPath = Path.Combine(work, "model.cmodel");
            var model = CmodelExport.Assemble(cmcPath, doc, work, rc: rc);
            using var buffer = new MemoryStream();
            CmodelFile.Write(model with { Symbol = null }, buffer);
            File.WriteAllBytes(cmodelPath, buffer.ToArray());
        }
        return (Path.GetFullPath(cmodelPath), data, work);
    }

    /// <summary>
    /// Validates <paramref name="cmodelPath"/> against the document's data. <paramref name="work"/> is where generated
    /// files a testbench names (a loadpull grid) are written.
    /// </summary>
    public static ValidationResult Run(string cmcPath, CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data,
                                       string cmodelPath, ValidationSettings settings, string work, RunControl? rc = null)
    {
        var model = CmodelFile.Read(cmodelPath);
        var ctx = new Context(Path.GetFullPath(cmcPath), doc, data, cmodelPath, model, settings, work, rc);
        var items = new List<ValidationItem>();
        var notes = new List<string>();
        var sets = settings.DataSets.Count == 0 ? doc.DataSets
            : [.. settings.DataSets.Select(n => doc.DataSets.FirstOrDefault(s => s.Name == n)
                                                ?? throw new ModelingException(StepName, $"there is no data set '{n}'."))];
        foreach (var set in sets)
        {
            rc?.ThrowIfCancellationRequested();
            try
            {
                var item = set.Kind switch
                {
                    DataSetKind.SParams => SParams(ctx, set),
                    DataSetKind.DcIv => Dc(ctx, set),
                    DataSetKind.SimulatedTables when data.TryGetValue(set.Name, out var nd) && nd.Points.Any(p => double.IsFinite(p.Id)) => Dc(ctx, set),
                    DataSetKind.PulsedIv => Pulsed(ctx, set),
                    DataSetKind.LargeSignal => set.Conditions.LargeSignal!.Measurement switch
                    {
                        LargeSignalMeasurement.PowerSweep => PowerSweep(ctx, set),
                        LargeSignalMeasurement.TwoTone => TwoTone(ctx, set),
                        _ => Loadpull(ctx, set),
                    },
                    _ => null,
                };
                if (item is null) notes.Add($"{set.Name}: {set.Kind} data is not validated.");
                else items.Add(item);
            }
            catch (ModelingException ex) { notes.Add($"{set.Name}: not validated — {ex.Message}"); }
        }
        return new ValidationResult(cmodelPath, items, notes);
    }

    /// <summary>Every item's testbench written beside the <c>.cmc</c> as <c>&lt;name&gt;.validate.&lt;set&gt;.cnl</c> —
    /// "show testbench" (R-mc11-4). Returns the paths.</summary>
    public static IReadOnlyList<string> WriteTestbenches(string cmcPath, ValidationResult result)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(cmcPath))!, stem = Path.GetFileNameWithoutExtension(cmcPath);
        var paths = new List<string>();
        foreach (var item in result.Items.Where(i => i.Testbench.Length > 0))
        {
            string path = Path.Combine(dir, $"{stem}.validate.{Safe(item.Set)}.cnl");
            File.WriteAllText(path, item.Testbench);
            paths.Add(path);
        }
        return paths;
    }

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    internal sealed record Context(string CmcPath, CmcDocument Doc, IReadOnlyDictionary<string, NormalisedData> Data,
                                   string ModelPath, ComposedModel Model, ValidationSettings Settings, string Work, RunControl? Rc)
    {
        public string Dir => Path.GetDirectoryName(CmcPath)!;
        public int N => Model.BasePeriphery.Fingers;
        public ValidationTolerances Tol => Settings.Tolerances;
        private IntrinsicResult? _intrinsic;
        public IntrinsicResult Intrinsic => _intrinsic ??= new IntrinsicExtraction(CmcPath).Run(Doc, Data, rc: Rc);
        public string Device(bool intrinsic, string g, string d) => ValidationBench.Device(ModelPath, N, intrinsic, g, d);
    }

    private static NormalisedData DataOf(Context ctx, CmcDataSet set) =>
        ctx.Data.TryGetValue(set.Name, out var nd) ? nd : throw new ModelingException(set.Name, "its data did not import.");

    /// <summary>The set's points at the device plane: the bias moved through the bias path and the pads (MC-4's own
    /// re-referencing with no network), and whether the set is stated at the intrinsic plane.</summary>
    private static (double[] Vgs, double[] Vds, bool Intrinsic) DevicePlane(Context ctx, CmcDataSet set, NormalisedData nd)
    {
        var plane = set.Conditions.ReferencePlane ?? ReferencePlane.Probe;
        var pads = PadDeembedding.Load(CmcExtrinsic.Pads(ctx.Doc), ctx.Dir);
        bool beforePads = plane is ReferencePlane.Instrument or ReferencePlane.Probe;
        var (vgs, vds) = IntrinsicExtraction.ReReference(set, nd, beforePads ? pads.DcDrop() : null, DcTransfer.Identity,
                                                         beforePads && pads.DcNeedsCurrents, []);
        return (vgs, vds, plane == ReferencePlane.Intrinsic);
    }

    private static double AmbientOf(CmcDataSet set, BiasPoint p) =>
        double.IsFinite(p.AmbientC) ? p.AmbientC : set.Conditions.AmbientC ?? 25.0;

    private static ParallelOptions Parallel(Context ctx) =>
        new() { CancellationToken = ctx.Rc?.Token ?? CancellationToken.None, MaxDegreeOfParallelism = Environment.ProcessorCount };

    private static (Core.Design.Library Lib, Core.Design.TestBench Tb) Read(string cnl) => new CnlReader().Read(cnl);

    // ── S-parameters ──────────────────────────────────────────────────────────

    private static ValidationItem SParams(Context ctx, CmcDataSet set)
    {
        var nd = DataOf(ctx, set);
        if (nd.S is null || nd.Ports != 2) throw new ModelingException(set.Name, "it holds no 2-port S-parameters.");
        var (vgs, vds, intrinsic) = DevicePlane(ctx, set, nd);
        double[] f = nd.FrequenciesHz!;
        var points = Enumerable.Range(0, nd.Count).Where(k => !nd.Points[k].Excluded).ToArray();
        if (points.Length == 0) throw new ModelingException(set.Name, "every bias point is excluded.");
        ctx.Rc?.BeginStage($"Validating S of {set.Name}", points.Length);

        int nf = f.Length;
        var model = new Complex[nd.Count * nf * 4];
        string device = ctx.Device(intrinsic, "g", "d");
        System.Threading.Tasks.Parallel.ForEach(points, Parallel(ctx), k =>
        {
            var p = nd.Points[k];
            var (lib, tb) = Read(ValidationBench.SParams(set.Name, device, AmbientOf(set, p), vgs[k], vds[k], f, nd.Z0));
            using var nl = new Elaborator(lib).Elaborate(tb);
            var s = SParameterEngine.Run(nl, lib, tb, null, f, HbCircuitRun.Settings())["S"];
            for (int q = 0; q < nf; q++)
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++)
                        model[(k * nf + q) * 4 + i * 2 + j] = (Complex)s[q, i, j];
            ctx.Rc?.TickStage();
        });
        ctx.Rc?.ThrowIfCancellationRequested();

        // Back to the measurement plane: the same pads the data was de-embedded through.
        var plane = set.Conditions.ReferencePlane ?? ReferencePlane.Probe;
        var modelData = new NormalisedData { Points = nd.Points, Ports = 2, FrequenciesHz = f, S = model, Z0 = nd.Z0 };
        if (plane is ReferencePlane.Instrument or ReferencePlane.Probe)
            modelData = PadDeembedding.Load(CmcExtrinsic.Pads(ctx.Doc), ctx.Dir).Embed(modelData, set.Name, ctx.Rc);

        var all = new List<double>();
        var perBias = Enumerable.Repeat(double.NaN, nd.Count).ToArray();
        foreach (int k in points)
        {
            double ss = 0;
            for (int q = 0; q < nf * 4; q++)
            {
                double d = Complex.Abs(modelData.S![k * nf * 4 + q] - nd.S[k * nf * 4 + q]);
                all.Add(d);
                ss += d * d;
            }
            perBias[k] = Math.Sqrt(ss / (nf * 4));
        }
        all.Sort();
        double median = Quantile(all, 0.5), p95 = Quantile(all, 0.95);
        bool pass = median <= ctx.Tol.SMedian && p95 <= ctx.Tol.S95;

        var point = PointAxis(nd);
        // i and j, not two axes called "port": a Data Display slice binds to an axis by NAME, so two of one name pin together.
        Axis Port(string name) => new(name, [1, 2], "port");
        var freq = new Axis("freq", f, "Hz");
        var ds = new DataSet();
        ds.Add("S.meas", new DataCube([point, freq, Port("i"), Port("j")], nd.S));
        ds.Add("S.model", new DataCube([point, freq, Port("i"), Port("j")], modelData.S!));
        ds.Add("dS", new DataCube([point], perBias));
        ds.Add("Vgs", new DataCube([point], [.. nd.Points.Select(p => p.Vgs)]));
        ds.Add("Vds", new DataCube([point], [.. nd.Points.Select(p => p.Vds)]));
        string line = $"S {set.Name}: |ΔS| median {G(median)}, 95 % {G(p95)} {Mark(pass)}";
        int k0 = points[0];
        return new ValidationItem(set.Name, ValidationKind.SParams, pass, line, ds,
            ValidationBench.SParams(set.Name, device, AmbientOf(set, nd.Points[k0]), vgs[k0], vds[k0], f, nd.Z0));
    }

    // ── DC I–V ────────────────────────────────────────────────────────────────

    private static ValidationItem Dc(Context ctx, CmcDataSet set)
    {
        var nd = DataOf(ctx, set);
        var (vgs, vds, intrinsic) = DevicePlane(ctx, set, nd);
        var points = Enumerable.Range(0, nd.Count).Where(k => !nd.Points[k].Excluded && double.IsFinite(nd.Points[k].Id)).ToArray();
        if (points.Length == 0) throw new ModelingException(set.Name, "it has no point with a drain current.");
        ctx.Rc?.BeginStage($"Validating DC of {set.Name}", points.Length);

        string device = ctx.Device(intrinsic, "g", "d");
        var id = Enumerable.Repeat(double.NaN, nd.Count).ToArray();
        var ig = Enumerable.Repeat(double.NaN, nd.Count).ToArray();
        var settings = HbCircuitRun.Settings();
        System.Threading.Tasks.Parallel.ForEach(points, Parallel(ctx), k =>
        {
            var (lib, tb) = Read(ValidationBench.Dc(set.Name, device, AmbientOf(set, nd.Points[k]), vgs[k], vds[k]));
            using var nl = new Elaborator(lib).Elaborate(tb);
            var r = NonlinearDcEngine.Run(nl, settings);
            if (r.Converged) (ig[k], id[k]) = (r.ProbeCurrents["IPG"], r.ProbeCurrents["IPD"]);
            ctx.Rc?.TickStage();
        });
        ctx.Rc?.ThrowIfCancellationRequested();

        var (rms, rel, n) = Error(points.Select(k => (nd.Points[k].Id, id[k])));
        int failed = points.Length - n;
        bool pass = failed == 0 && rel <= ctx.Tol.IvRelative;

        var point = PointAxis(nd);
        var ds = new DataSet();
        ds.Add("Vgs", new DataCube([point], [.. nd.Points.Select(p => p.Vgs)]));
        ds.Add("Vds", new DataCube([point], [.. nd.Points.Select(p => p.Vds)]));
        ds.Add("Id.meas", new DataCube([point], [.. nd.Points.Select(p => p.Id)]));
        ds.Add("Id.model", new DataCube([point], id));
        ds.Add("Ig.meas", new DataCube([point], [.. nd.Points.Select(p => p.Ig)]));
        ds.Add("Ig.model", new DataCube([point], ig));
        ds.Add("T", new DataCube([point], [.. nd.Points.Select(p => AmbientOf(set, p))]));
        string line = $"DC {set.Name}: Id RMS {Pct(rel)} of range" + (failed > 0 ? $", {failed} point(s) did not converge" : "") + $" {Mark(pass)}";
        int k0 = points[0];
        return new ValidationItem(set.Name, ValidationKind.DcIv, pass, line, ds,
            ValidationBench.Dc(set.Name, device, AmbientOf(set, nd.Points[k0]), vgs[k0], vds[k0]));
    }

    // ── Pulsed I–V ────────────────────────────────────────────────────────────

    private static ValidationItem Pulsed(Context ctx, CmcDataSet set)
    {
        ctx.Rc?.BeginStage($"Validating pulsed I–V of {set.Name}", 1);
        var report = new List<string>();
        var settings = CurrentFit.Settings(ctx.Doc) with { DataSets = [set.Name] };
        var ip = CurrentFit.Gather(ctx.Doc, ctx.Data, ctx.Intrinsic, settings, report, ThermalFit.Heating(ctx.Doc));
        var program = ComposedCoreProgram.Compile(ctx.Model, "validation");
        Span<double> s = stackalloc double[RuntimeEvaluator.MaxInputs], g = stackalloc double[RuntimeEvaluator.MaxInputs];
        int nTrap = program.Traps?.States.Count ?? 0;
        var model = new double[ip.X.Length];
        for (int k = 0; k < ip.X.Length; k++)
        {
            var x = ip.X[k];
            double t = ip.HasT ? x[2] : ip.TemperatureC;
            program.DcSeeds(x[0], x[1], t, s);
            if (ip.Trapped && nTrap > 0)
                for (int j = 0; j < nTrap; j++) s[program.TrapLane + j] = x[x.Length - nTrap + j];
            model[k] = program.Channel(s[..program.Width], g);
        }
        var (rms, rel, _) = Error(ip.Id.Select((m, k) => (m, model[k])));
        bool pass = rel <= ctx.Tol.IvRelative;
        var axis = new Axis("point", [.. Enumerable.Range(0, ip.X.Length).Select(i => (double)i)]);
        var ds = new DataSet();
        ds.Add("Vgsi", new DataCube([axis], [.. ip.X.Select(x => x[0])]));
        ds.Add("Vdsi", new DataCube([axis], [.. ip.X.Select(x => x[1])]));
        ds.Add("Id.meas", new DataCube([axis], ip.Id));
        ds.Add("Id.model", new DataCube([axis], model));
        ctx.Rc?.TickStage();
        string line = $"Pulsed {set.Name}: Id RMS {Pct(rel)} of range over {ip.X.Length} point(s) {Mark(pass)}";
        return new ValidationItem(set.Name, ValidationKind.PulsedIv, pass, line, ds, "");
    }

    // ── Large signal ──────────────────────────────────────────────────────────

    /// <summary>A large-signal CSV's rows: the input power axis, and each mapped quantity by name (NaN where absent).</summary>
    private static (double[] Pin, Dictionary<string, double[]> Columns) Sweep(Context ctx, CmcDataSet set)
    {
        // A logarithmic quantity is read in its own unit as written; only a current is scaled by its column's unit.
        var im = set.Import;
        var plain = set with { Import = im with { Columns = [.. im.Columns!.Select(c => CmcFile.LargeSignalUnit(c.Quantity) is null ? c : c with { Unit = null })] } };
        var rows = DataImport.CsvRows(plain, ctx.Dir, null).Select(r => r.Values).OrderBy(r => r["pin"]).ToList();
        if (rows.Count == 0) throw new ModelingException(set.Name, "it has no rows.");
        var cols = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (string q in CmcFile.LargeSignalQuantities)
            if (rows.Any(r => r.ContainsKey(q))) cols[q] = [.. rows.Select(r => r.TryGetValue(q, out var v) ? v : double.NaN)];
        return (cols["pin"], cols);
    }

    /// <summary>Runs a generated HB testbench through the run service; its HB result.</summary>
    private static DataSet RunHb(Context ctx, string cnl) => Evaluate(ctx, cnl, "HB", "V");

    /// <summary>Runs a generated testbench through the run service (what its verb calls); the result holding
    /// <paramref name="cube"/>.</summary>
    private static DataSet Evaluate(Context ctx, string cnl, string what, string cube)
    {
        var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, ctx.Work, ctx.Work),
                                           new CircuitEvaluationRequest { Measurements = false, Control = ctx.Rc?.Child() });
        if (r.Status == RunStatus.Cancelled) throw new OperationCanceledException();
        if (r.Status != RunStatus.Success) throw new ModelingException(what, r.StatusMessage);
        return r.Results.FirstOrDefault(x => x.Data.Contains(cube))?.Data
               ?? throw new ModelingException(what, $"the run produced no {cube}.");
    }

    /// <summary>
    /// What the HB result says at every drive level and spectral index: the power into the load and the RF power the gate
    /// takes (W, at <paramref name="spectral"/>), and the DC currents and supply power. Pout and Pin come from the probes
    /// at the device's terminals, so a passive network around them cannot be mistaken for the device.
    /// </summary>
    internal static (double[] Pout, double[] PinDelivered, double[] Id, double[] Ig, double[] Pdc) Powers(DataSet ds, int spectral)
    {
        var v = ds["V"]; var i = ds["I"];
        int nodeAx = AxisIndex(v, "node"), branchAx = AxisIndex(i, "branch");
        int sweeps = v.Axes[0].Name is "node" ? 1 : v.Axes[0].Length;
        var pout = new double[sweeps]; var pin = new double[sweeps]; var id = new double[sweeps]; var ig = new double[sweeps]; var pdc = new double[sweeps];
        for (int k = 0; k < sweeps; k++)
        {
            Complex V(string node, int h) => Cell(v, k, nodeAx, Label(v, nodeAx, node), h);
            Complex I(string probe, int h) => Cell(i, k, branchAx, Label(i, branchAx, probe), h);
            pout[k] = 0.5 * (V("dx", spectral) * Complex.Conjugate(I("IPD", spectral))).Real;
            pin[k] = 0.5 * (V("gx", spectral) * Complex.Conjugate(I("IPG", spectral))).Real;
            id[k] = -I("IPD", 0).Real;
            ig[k] = I("IPG", 0).Real;
            pdc[k] = V("dx", 0).Real * id[k] + V("gx", 0).Real * ig[k];
        }
        return (pout, pin, id, ig, pdc);
    }

    private static int AxisIndex(DataCube c, string name)
    {
        for (int a = 0; a < c.Rank; a++) if (c.Axes[a].Name == name) return a;
        throw new ModelingException("HB", $"the result has no {name} axis.");
    }

    private static int Label(DataCube c, int axis, string label) =>
        Array.IndexOf(c.Axes[axis].Labels ?? [], label) is >= 0 and var i ? i
        : throw new ModelingException("HB", $"the result has no '{label}'.");

    /// <summary>One element of a [sweep?, named, spectral] cube.</summary>
    private static Complex Cell(DataCube c, int sweep, int namedAxis, int named, int spectral)
    {
        int nNamed = c.Axes[namedAxis].Length, nSpec = c.Axes[namedAxis + 1].Length;
        int offset = namedAxis == 0 ? 0 : sweep * nNamed * nSpec;
        return c.ComplexValues[offset + named * nSpec + spectral];
    }

    private static ValidationItem PowerSweep(Context ctx, CmcDataSet set)
    {
        var c = set.Conditions.LargeSignal!;
        var (pin, cols) = Sweep(ctx, set);
        ctx.Rc?.BeginStage($"Validating power sweep {set.Name}", 1);
        double t = set.Conditions.AmbientC ?? 25;
        string cnl = ValidationBench.PowerSweep(set.Name, ctx.Device(false, "gx", "dx"), t, c, pin, ctx.Settings.MaxHarmonic);
        var (pout, pinDel, id, ig, pdc) = Powers(RunHb(ctx, cnl), 1);
        ctx.Rc?.TickStage();

        double[] poutDbm = [.. pout.Select(Dbm)];
        double[] gain = [.. poutDbm.Select((p, k) => p - pin[k])];
        double[] pae = [.. pout.Select((p, k) => 100 * (p - pinDel[k]) / pdc[k])];
        double[] de = [.. pout.Select((p, k) => 100 * p / pdc[k])];
        var axis = new Axis("Pin", pin, "dBm");
        var ds = new DataSet();
        void Pair(string name, string q, double[] model)
        {
            if (cols.TryGetValue(q, out var meas)) ds.Add($"{name}.meas", new DataCube([axis], meas));
            ds.Add($"{name}.model", new DataCube([axis], model));
        }
        Pair("Pout", "pout", poutDbm); Pair("Gain", "gain", gain); Pair("PAE", "pae", pae); Pair("DE", "de", de);
        Pair("Id", "id", id); Pair("Ig", "ig", ig);

        var parts = new List<string>();
        bool pass = true;
        if (cols.TryGetValue("pout", out var mp))
        {
            double e = Rms(mp, poutDbm);
            pass &= e <= ctx.Tol.PowerDb;
            parts.Add($"Pout RMS {G(e)} dB");
        }
        else if (cols.TryGetValue("gain", out var mg))
        {
            double e = Rms(mg, gain);
            pass &= e <= ctx.Tol.PowerDb;
            parts.Add($"gain RMS {G(e)} dB");
        }
        foreach (var (q, model, label) in new[] { ("pae", pae, "PAE"), ("de", de, "DE") })
            if (cols.TryGetValue(q, out var me))
            {
                double e = Rms(me, model);
                pass &= e <= ctx.Tol.EfficiencyPoints;
                parts.Add($"{label} RMS {G(e)} pp");
                break;
            }
        if (parts.Count == 0) throw new ModelingException(set.Name, "it has no pout, gain, pae or de column to compare.");
        return new ValidationItem(set.Name, ValidationKind.PowerSweep, pass,
                                  $"Power {set.Name}: {string.Join(" · ", parts)} {Mark(pass)}", ds, cnl);
    }

    private static ValidationItem TwoTone(Context ctx, CmcDataSet set)
    {
        var c = set.Conditions.LargeSignal!;
        var (pin, cols) = Sweep(ctx, set);
        ctx.Rc?.BeginStage($"Validating two-tone {set.Name}", 1);
        double t = set.Conditions.AmbientC ?? 25;
        string cnl = ValidationBench.TwoTone(set.Name, ctx.Device(false, "gx", "dx"), t, c, pin, ctx.Settings.MaxHarmonic);
        var ds0 = RunHb(ctx, cnl);
        double f1 = c.FrequencyHz!.Value - c.SpacingHz!.Value / 2, f2 = c.FrequencyHz.Value + c.SpacingHz.Value / 2;
        int Mix(double hz)
        {
            var mix = ds0["V"].Axes[AxisIndex(ds0["V"], "node") + 1].Values;
            for (int m = 0; m < mix.Length; m++) if (Math.Abs(mix[m] - hz) < 1) return m;
            throw new ModelingException(set.Name, $"the spectrum does not hold {G(hz / 1e9)} GHz; raise MaxHarmonic.");
        }
        double[] carrier = [.. Powers(ds0, Mix(f1)).Pout.Select(Dbm)];
        double[] im3 = [.. Powers(ds0, Mix(2 * f1 - f2)).Pout.Select(Dbm)];
        double[] im5 = [.. Powers(ds0, Mix(3 * f1 - 2 * f2)).Pout.Select(Dbm)];
        ctx.Rc?.TickStage();

        var axis = new Axis("Pin", pin, "dBm");
        var ds = new DataSet();
        var parts = new List<string>();
        bool pass = true;
        foreach (var (q, model, label) in new[] { ("pout", carrier, "Pout"), ("im3", im3, "IM3"), ("im5", im5, "IM5") })
        {
            ds.Add($"{label}.model", new DataCube([axis], model));
            if (!cols.TryGetValue(q, out var meas)) continue;
            ds.Add($"{label}.meas", new DataCube([axis], meas));
            double e = Rms(meas, model);
            pass &= e <= ctx.Tol.PowerDb;
            parts.Add($"{label} RMS {G(e)} dB");
        }
        if (parts.Count == 0) throw new ModelingException(set.Name, "it has no pout, im3 or im5 column to compare.");
        return new ValidationItem(set.Name, ValidationKind.TwoTone, pass,
                                  $"Two-tone {set.Name}: {string.Join(" · ", parts)} {Mark(pass)}", ds, cnl);
    }

    private static ValidationItem Loadpull(Context ctx, CmcDataSet set)
    {
        var c = set.Conditions.LargeSignal!;
        string src = DataImport.ResolveSource(ctx.Dir, set.Sources[0]);
        if (!File.Exists(src)) throw new ModelingException(set.Sources[0], "the file does not exist.");
        DataSet meas;
        try
        {
            meas = Path.GetExtension(src).Equals(".lpcwave", StringComparison.OrdinalIgnoreCase)
                ? LpcwaveReader.ReadLpcwave(src) : SplReader.ReadSpl(src);
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException)
        { throw new ModelingException(set.Sources[0], $"it is not a readable loadpull file ({ex.Message})."); }
        if (!meas.Contains("GammaLoad") || !meas.Contains("PavlDbm"))
            throw new ModelingException(set.Sources[0], "it carries no load grid or drive levels.");
        var (one, fFile) = OneFrequency(meas, c.FrequencyHz, set.Name);
        meas = one;

        double f0 = c.FrequencyHz ?? fFile ?? throw new ModelingException(set.Name, "the frequency is not stated and the file does not carry one.");
        var zSource = c.SourceZ ?? (meas.Contains("ZSource") ? meas["ZSource"].ComplexValues[0] : 50);
        double vgs = c.Vgs ?? OperatingPoint(meas, "BiasVSrc") ?? throw new ModelingException(set.Name, "the gate bias is not stated.");
        double vds = c.Vds ?? OperatingPoint(meas, "BiasVLoad") ?? throw new ModelingException(set.Name, "the drain bias is not stated.");
        var gamma = meas["GammaLoad"].ComplexValues;
        var pavl = meas["PavlDbm"].RealValues;
        int nGrid = gamma.Length, nPin = pavl.Length / nGrid;
        var zl = meas.Contains("ZLoad") ? meas["ZLoad"].ComplexValues : [.. gamma.Select(g => 50 * (1 + g) / (1 - g))];
        var finite = pavl.Where(double.IsFinite).ToArray();
        if (finite.Length == 0) throw new ModelingException(set.Name, "no drive level is stated.");
        // The model's ladder steps by the smallest gap between the measured drives, so a regular measured grid (a tickle
        // below it included) is hit exactly and only an irregular one is interpolated; at most 200 rungs.
        double[] levels = [.. finite.Select(v => Math.Round(v, 6)).Distinct().Order()];
        double lo = levels[0], hi = levels[^1];
        double gap = levels.Length > 1 ? levels.Zip(levels.Skip(1), (a, b) => b - a).Min() : 1;
        double step = Math.Max(Math.Max(gap, 0.1), (hi - lo) / 199);

        // The model load-pulled over the file's own loads, through the loadpull engine `circuitrf lp` runs: the grid is a
        // .gam beside the generated testbench, which names it by its full path so the testbench runs unchanged anywhere.
        string gam = Path.Combine(ctx.Work, $"{Safe(set.Name)}.gam");
        File.WriteAllLines(gam, ["# impedance Z0=50 re_im", .. zl.Select(z => $"{ValidationBench.R(z.Real)} {ValidationBench.R(z.Imaginary)}")]);
        string cnl = ValidationBench.Loadpull(set.Name, ctx.Device(false, "g", "d"), set.Conditions.AmbientC ?? 25, f0, vgs, vds,
                                              zSource, c.SourceHarmonics, c.LoadHarmonics, gam, lo, step, hi, ctx.Settings.MaxHarmonic);
        ctx.Rc?.BeginStage($"Validating loadpull {set.Name}", 1);
        var model = Evaluate(ctx, cnl, "loadpull", "StopCode");
        ctx.Rc?.TickStage();

        // Each measured (load, drive) against the model at the same load, interpolated to the same drive.
        var mPout = Watts(meas); var mPae = meas.Contains("PAE") ? meas["PAE"].RealValues : null;
        var sPavl = model["PavlDbm"].RealValues; var sPout = Watts(model); var sPae = model.Contains("PAE") ? model["PAE"].RealValues : null;
        var sConv = model["Converged"].RealValues; var sTickle = model.Contains("IsTickle") ? model["IsTickle"].RealValues : null;
        int sPin = sPavl.Length / nGrid;
        var dPout = new List<(double, double)>(); var dPae = new List<(double, double)>();
        var outModel = Enumerable.Repeat(double.NaN, nGrid * nPin).ToArray();
        var paeModel = Enumerable.Repeat(double.NaN, nGrid * nPin).ToArray();
        for (int g = 0; g < nGrid; g++)
        {
            var ladder = Enumerable.Range(g * sPin, sPin)
                .Where(j => sConv[j] == 1 && (sTickle is null || sTickle[j] != 1) && double.IsFinite(sPavl[j]) && sPout[j] > 0)
                .OrderBy(j => sPavl[j]).ToArray();
            if (ladder.Length == 0) continue;
            double[] x = [.. ladder.Select(j => sPavl[j])];
            double[] yPout = [.. ladder.Select(j => Dbm(sPout[j]))];
            double[]? yPae = sPae is null ? null : [.. ladder.Select(j => sPae[j])];
            for (int k = 0; k < nPin; k++)
            {
                int i = g * nPin + k;
                double p = pavl[i];
                if (!double.IsFinite(p) || !(mPout[i] > 0)) continue;
                outModel[i] = Interpolate(x, yPout, p);
                if (!double.IsFinite(outModel[i])) continue;
                dPout.Add((Dbm(mPout[i]), outModel[i]));
                if (yPae is not null)
                {
                    paeModel[i] = Interpolate(x, yPae, p);
                    if (mPae is not null && double.IsFinite(mPae[i]) && double.IsFinite(paeModel[i])) dPae.Add((mPae[i], paeModel[i]));
                }
            }
        }
        if (dPout.Count == 0) throw new ModelingException(set.Name, "no measured point has a converged model point at its drive.");
        double ePout = Math.Sqrt(dPout.Average(x => (x.Item1 - x.Item2) * (x.Item1 - x.Item2)));
        double? ePae = dPae.Count > 0 ? Math.Sqrt(dPae.Average(x => (x.Item1 - x.Item2) * (x.Item1 - x.Item2))) : null;
        bool pass = ePout <= ctx.Tol.PowerDb && (ePae is null || ePae <= ctx.Tol.EfficiencyPoints);

        var gridAx = new Axis("gridPoint", [.. Enumerable.Range(0, nGrid).Select(i => (double)i)]);
        var pinAx = new Axis("pinStep", [.. Enumerable.Range(0, nPin).Select(i => (double)i)]);
        var ds = new DataSet();
        ds.Add("GammaLoad", new DataCube([gridAx], gamma));
        ds.Add("PavlDbm", new DataCube([gridAx, pinAx], pavl));
        ds.Add("Pout.meas", new DataCube([gridAx, pinAx], [.. mPout.Select(Dbm)]));
        ds.Add("Pout.model", new DataCube([gridAx, pinAx], outModel));
        if (mPae is not null) ds.Add("PAE.meas", new DataCube([gridAx, pinAx], mPae));
        if (sPae is not null) ds.Add("PAE.model", new DataCube([gridAx, pinAx], paeModel));
        string line = $"Loadpull {set.Name}: Pout RMS {G(ePout)} dB" + (ePae is { } e ? $" · PAE RMS {G(e)} pp" : "") +
                      $" over {dPout.Count} point(s) {Mark(pass)}";
        return new ValidationItem(set.Name, ValidationKind.Loadpull, pass, line, ds, cnl);
    }

    /// <summary>
    /// A loadpull file's DataSet at one frequency: a multi-frequency file (every cube on a leading <c>freq</c> axis) sliced
    /// at <paramref name="hz"/> — refused when it holds several and none is named, or not the one named — and the
    /// frequency the file states, if it states one.
    /// </summary>
    private static (DataSet Data, double? Hz) OneFrequency(DataSet meas, double? hz, string set)
    {
        var g = meas["GammaLoad"];
        if (g.Rank < 2 || g.Axes[0].Name != "freq")
        {
            double? f = meas.Contains("__Freq") ? meas["__Freq"].RealValues[0]
                      : meas.Contains("ZSource") ? meas["ZSource"].Axes[0].Values[0] : null;
            return (meas, f);
        }
        double[] freqs = g.Axes[0].Values;
        int fi;
        if (hz is { } want)
        {
            fi = Array.FindIndex(freqs, v => Math.Abs(v - want) <= 1e-6 * want);
            if (fi < 0) throw new ModelingException(set, $"the file holds no data at {G(want / 1e9)} GHz; it holds {string.Join(", ", freqs.Select(v => G(v / 1e9)))} GHz.");
        }
        else if (freqs.Length == 1) fi = 0;
        else throw new ModelingException(set, $"the file holds {freqs.Length} frequencies ({string.Join(", ", freqs.Select(v => G(v / 1e9)))} GHz); state FrequencyHz.");
        var one = new DataSet();
        foreach (var (name, cube) in meas.Cubes)
            one.Add(name, cube.Rank > 0 && cube.Axes[0].Name == "freq" ? cube.At("freq", fi) : cube);
        return (one, freqs[fi]);
    }

    /// <summary>Linear interpolation of <paramref name="y"/> over ascending <paramref name="x"/> at <paramref name="at"/>; within
    /// 0.05 dB of an end, that end's value; NaN beyond.</summary>
    internal static double Interpolate(double[] x, double[] y, double at)
    {
        if (x.Length == 0 || at < x[0] - 0.05 || at > x[^1] + 0.05) return double.NaN;
        if (at <= x[0]) return y[0];
        if (at >= x[^1]) return y[^1];
        int j = Array.FindIndex(x, v => v >= at);
        if (x[j] == at || x[j] == x[j - 1]) return y[j];
        double t = (at - x[j - 1]) / (x[j] - x[j - 1]);
        return y[j - 1] + t * (y[j] - y[j - 1]);
    }

    /// <summary>
    /// A loadpull of the model over <paramref name="gammas"/> (50 Ω), as the loadpull DataSet RfCore's surface and the Data
    /// Display read, under the canonical names a measured file and an enriched run carry (<c>gridPoint × pinStep</c>:
    /// Pout_W, Pout_dBm, Gt_dB, Gp_dB, Efficiency and PAE in %, Pdc_W, PavlDbm, the bias, Converged; GammaLoad, ZLoad,
    /// StopCode).
    ///
    /// <para><b>A fixed ladder, not the loadpull engine's drive-up</b> — the report's region of convergence is the share of
    /// loads where HB converged on one and the same drive ladder, and the engine's continuation and cold retries would
    /// rescue loads that ladder does not reach. Each load is one HB power sweep through the HB run service — the
    /// power-sweep testbench with that load — measured by probes at the device's terminals. The drive is the whole ladder
    /// (an HB sweep cannot stop early); with <paramref name="compression"/> a point is cut, as the engine's drive-up stops,
    /// one step past the gain falling that far below its running maximum. Validation against a loadpull FILE runs the
    /// engine itself (<c>circuitrf lp</c>).</para>
    /// </summary>
    internal static DataSet LoadSweep(Context ctx, string set, double ambientC, double f0, double vgs, double vds, Complex sourceZ,
                                      IReadOnlyList<Complex> sourceHarmonics, IReadOnlyList<Complex> loadHarmonics,
                                      IReadOnlyList<Complex> gammas, double[] pin, double? compression, out string testbench)
    {
        int nG = gammas.Count, nP = pin.Length;
        var zl = gammas.Select(g => 50 * (1 + g) / (1 - g)).ToArray();
        LargeSignalConditions At(int g) => new()
        {
            Measurement = LargeSignalMeasurement.PowerSweep, FrequencyHz = f0, Vgs = vgs, Vds = vds, SourceZ = sourceZ, LoadZ = zl[g],
            SourceHarmonics = sourceHarmonics, LoadHarmonics = loadHarmonics,
        };
        string device = ctx.Device(false, "gx", "dx");
        testbench = ValidationBench.PowerSweep(set, device, ambientC, At(0), pin, ctx.Settings.MaxHarmonic);
        string[] names = ["Converged", "Pout_W", "Pout_dBm", "Gt_dB", "Gp_dB", "Efficiency", "PAE", "Pdc_W", "BiasILoad", "BiasISrc"];
        var cubes = names.ToDictionary(n => n, n => n == "Converged" ? new double[nG * nP] : Enumerable.Repeat(double.NaN, nG * nP).ToArray(),
                                       StringComparer.Ordinal);
        var stop = new double[nG];
        ctx.Rc?.BeginStage($"Load-pulling {set}", nG);
        System.Threading.Tasks.Parallel.For(0, nG, Parallel(ctx), g =>
        {
            DataSet hb;
            try { hb = RunHb(ctx with { Rc = null }, ValidationBench.PowerSweep(set, device, ambientC, At(g), pin, ctx.Settings.MaxHarmonic)); }
            catch (ModelingException) { stop[g] = 3; ctx.Rc?.TickStage(); return; }
            var (pout, pinDel, id, ig, pdc) = Powers(hb, 1);
            var conv = hb["Converged"].RealValues;
            double g0 = double.NaN;
            stop[g] = 0;
            for (int k = 0; k < nP; k++)
            {
                int i = g * nP + k;
                if (conv[k] != 1 || !(pout[k] > 0) || !(pdc[k] > 0)) { stop[g] = double.IsNaN(g0) ? 3 : 2; break; }
                double gt = Dbm(pout[k]) - pin[k];
                cubes["Converged"][i] = 1;
                cubes["Pout_W"][i] = pout[k];
                cubes["Pout_dBm"][i] = Dbm(pout[k]);
                cubes["Gt_dB"][i] = gt;
                cubes["Gp_dB"][i] = Dbm(pout[k]) - Dbm(pinDel[k]);
                cubes["Efficiency"][i] = 100 * pout[k] / pdc[k];
                cubes["PAE"][i] = 100 * (pout[k] - pinDel[k]) / pdc[k];
                cubes["Pdc_W"][i] = pdc[k];
                cubes["BiasILoad"][i] = id[k];
                cubes["BiasISrc"][i] = ig[k];
                // Compression from the running gain maximum — harmonicaRF's rule (PinSearch), which a device whose gain
                // expands before it compresses needs: measured from the first step it would fall too late.
                g0 = double.IsNaN(g0) ? gt : Math.Max(g0, gt);
                if (compression is { } c && g0 - gt >= c) { stop[g] = 1; break; }
            }
            ctx.Rc?.TickStage();
        });
        ctx.Rc?.ThrowIfCancellationRequested();

        var gridAxis = new Axis("gridPoint", [.. Enumerable.Range(0, nG).Select(i => (double)i)]);
        var pinAxis = new Axis("pinStep", pin, "dBm");
        var ds = new DataSet();
        foreach (string n in names) ds.Add(n, new DataCube([gridAxis, pinAxis], cubes[n]));
        ds.Add("PavlDbm", new DataCube([gridAxis, pinAxis], [.. Enumerable.Range(0, nG * nP).Select(i => pin[i % nP])]));
        ds.Add("BiasVLoad", new DataCube([gridAxis, pinAxis], [.. Enumerable.Repeat(vds, nG * nP)]));
        ds.Add("BiasVSrc", new DataCube([gridAxis, pinAxis], [.. Enumerable.Repeat(vgs, nG * nP)]));
        ds.Add("ZLoad", new DataCube([gridAxis], zl));
        ds.Add("GammaLoad", new DataCube([gridAxis], [.. gammas]));
        ds.Add("StopCode", new DataCube([gridAxis], stop));
        return ds;
    }

    private static double? OperatingPoint(DataSet ds, string cube) =>
        ds.Contains(cube) && ds[cube].RealValues.FirstOrDefault(double.IsFinite) is var v && double.IsFinite(v) ? v : null;

    /// <summary>A loadpull result's output power in watts: <c>Pout_W</c> or <c>Pout</c>, or <c>Pout_dBm</c> converted.</summary>
    private static double[] Watts(DataSet ds) =>
        ds.Contains("Pout_W") ? ds["Pout_W"].RealValues
        : ds.Contains("Pout") ? ds["Pout"].RealValues
        : ds.Contains("Pout_dBm") ? [.. ds["Pout_dBm"].RealValues.Select(p => Math.Pow(10, (p - 30) / 10))]
        : throw new ModelingException("loadpull", "the result carries no output power.");

    // ── Arithmetic ────────────────────────────────────────────────────────────

    internal static double Dbm(double watts) => watts > 0 ? 10 * Math.Log10(watts * 1e3) : double.NaN;

    /// <summary>RMS of measured − model over the pairs where both are finite; RMS relative to the measured range; count.</summary>
    internal static (double Rms, double Relative, int Count) Error(IEnumerable<(double Meas, double Model)> pairs)
    {
        var list = pairs.Where(p => double.IsFinite(p.Meas) && double.IsFinite(p.Model)).ToList();
        if (list.Count == 0) return (double.NaN, double.NaN, 0);
        double rms = Math.Sqrt(list.Average(p => (p.Meas - p.Model) * (p.Meas - p.Model)));
        double range = list.Max(p => p.Meas) - list.Min(p => p.Meas);
        return (rms, range > 0 ? rms / range : rms, list.Count);
    }

    private static double Rms(double[] meas, double[] model) => Error(meas.Zip(model)).Rms;

    internal static double Quantile(List<double> sorted, double q)
    {
        if (sorted.Count == 0) return double.NaN;
        double x = q * (sorted.Count - 1);
        int i = (int)Math.Floor(x);
        return i + 1 < sorted.Count ? sorted[i] + (x - i) * (sorted[i + 1] - sorted[i]) : sorted[i];
    }

    private static Axis PointAxis(NormalisedData nd) =>
        new("point", [.. Enumerable.Range(0, nd.Count).Select(i => (double)i)], labels: [.. nd.Points.Select(p => p.PointId)]);

    internal static string G(double v) => v.ToString("G3", CultureInfo.InvariantCulture);
    private static string Pct(double v) => (100 * v).ToString("G3", CultureInfo.InvariantCulture) + " %";
    internal static string Mark(bool pass) => pass ? "✓" : "✗";
}
