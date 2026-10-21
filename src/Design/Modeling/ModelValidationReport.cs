// The Model Validation Report (brief-modelcomposer-11 R-mc11-6): one run that shows the model's fit to every kind of data
// the document holds and what it predicts as a power amplifier, as DataSets the Data Display draws, a one-line summary per
// result, and every testbench it ran.
//
//   DC         each DC and pulsed I–V set, measured against model (the validation's own items, R-mc11-4).
//   Thermal    with a self-heating model and DC data at one or more ambients: each DC point's equivalent junction
//              temperature — where the isothermal model carries the measured current — against its dissipated power, the
//              slope per ambient against the model's Rth there, and the DC error where the device is hottest.
//   Charge     the model's Qg and Qd against the line integral of the measured capacitances, over (vgs, vds), as a
//              difference map in % of each charge's range.
//   C–V        each capacitance, and the circuit set Cgs Cgd Cds Cm, against vgs at a family of fixed vds and against vds at
//              a family of fixed vgs — the model's curves with the measured points nearest each fixed voltage.
//   S          every S set (each at its own ambient), with the chosen biases picked out for the plots.
//   PA modes   for Class B, J, F and F⁻¹ (any of harmonicaRF's classes) — harmonicaRF's own method: each class's
//              INTRINSIC terminations to the fifth harmonic from PaClassPresets (Z0 = R_opt), carried to the extrinsic
//              plane in closed form through the model's own extrinsic network with the device's output capacitance
//              linearized at the intrinsic DC bias (ComposedClassLoads in src/Harmonica — the transform harmonicaRF's own
//              presets use for a composed DUT: the drain-side two-port with both gates AC-shorted); the
//              source's 2f0 as the user states. Then a loadpull of the fundamental over a disc about the class's own
//              fundamental load: the performance at that load, at the maximum-power and maximum-efficiency loads (MXP,
//              MXE) at the stated compression through RfCore's loadpull surface — the Data Display's performance summary —
//              and the region of the disc where HB converged up to that compression.
//
// Ahead of them, Coverage: every data set's bias points at the device plane, and the PA bias — where the model was fitted
// and checked, for the designer who did not build it. Each section also states the conditions that reproduce it and its
// scorecard rows; ModelReportCard holds the card those make, with where the model is backed by data and how it was fitted.
//
// Everything runs through the run services validation uses; the report writes a folder: report.npy (one group per
// section), report.json (the card), summary.txt (the card as text), and testbenches/ with the .cnl each section ran. The Data Display document and the
// dialog that sets these options belong to the window (MC-15); this is the headless half both the window and the model
// verb call. A cancelled report writes nothing.

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Circuit;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;
using CircuitRF.Harmonica;
using RfCore.Data;
using RfCore.Export;
using RfCore.Loadpull;

namespace CircuitRF.Design.Modeling;

/// <summary>What a report includes and how (R-mc11-6). Every field has the default the dialog opens on.</summary>
public sealed record ReportOptions
{
    public bool Dc { get; init; } = true;
    public bool Thermal { get; init; } = true;
    public bool Charge { get; init; } = true;
    public bool CapacitanceSlices { get; init; } = true;
    public bool SParams { get; init; } = true;
    public bool PaModes { get; init; } = true;

    /// <summary>The biases the S plots pick out (the nearest measured point of each set); empty: the lowest, middle and
    /// highest drain current of each set.</summary>
    public IReadOnlyList<(double Vgs, double Vds)> SBiases { get; init; } = [];

    /// <summary>The fixed drain and gate voltages of the C–V families; empty: five spread over the data.</summary>
    public IReadOnlyList<double> FixedVds { get; init; } = [];
    public IReadOnlyList<double> FixedVgs { get; init; } = [];

    public IReadOnlyList<PaClass> Modes { get; init; } = [PaClass.B, PaClass.J, PaClass.F, PaClass.FInverse];
    /// <summary>The fundamental, Hz; null: the middle of the S data's frequencies.</summary>
    public double? FrequencyHz { get; init; }
    /// <summary>The PA bias at the device plane; null: half the largest measured vds — the drain swings to about twice its
    /// bias, so the swing stays inside the data — and the vgs where the model's drain current is 2 % of Imax at that vds (a
    /// deep class-AB quiescent point).</summary>
    public double? Vgs { get; init; }
    public double? Vds { get; init; }
    public int MaxHarmonic { get; init; } = 5;
    /// <summary>The source's fundamental impedance; null: the conjugate of the model's input impedance at the bias, loaded
    /// by <see cref="RoptOhms"/>.</summary>
    public Complex? SourceZ { get; init; }
    /// <summary>The source's 2f0 termination at the extrinsic plane; null: a short. Higher harmonics are shorted.</summary>
    public Complex? Source2f0 { get; init; }
    /// <summary>The optimum load resistance R_opt each class's intrinsic terminations are scaled to (harmonicaRF's presets
    /// take it as their Z0); null: 2·(vds − vknee)/Imax, vknee taken as a tenth of vds and Imax the largest drain current in
    /// the I–V data.</summary>
    public double? RoptOhms { get; init; }
    /// <summary>The loadpull disc, centred on each class's own fundamental load: rings out to <see cref="GammaRadius"/> (in
    /// Γ renormalised to that load), each with <see cref="Spokes"/> points, and its centre.</summary>
    public double GammaRadius { get; init; } = 0.75;
    public int Rings { get; init; } = 6;
    public int Spokes { get; init; } = 12;
    public double PinStart { get; init; } = -10;
    public double PinStep { get; init; } = 1;
    public double PinMax { get; init; } = 40;
    public double Compression { get; init; } = 3;

    public static ReportOptions Default { get; } = new();

    // ── The document (Steps.Report.Settings) ─────────────────────────────────

    private static readonly string[] Keys =
    [
        "Sections", "SBiases", "FixedVds", "FixedVgs", "Modes", "FrequencyHz", "Vgs", "Vds", "MaxHarmonic", "SourceZ", "Source2f0",
        "RoptOhms", "GammaRadius", "Rings", "Spokes", "PinStart", "PinStep", "PinMax", "Compression",
    ];
    private static readonly string[] SectionNames = ["Dc", "Thermal", "Charge", "CapacitanceSlices", "SParams", "PaModes"];

    public static ReportOptions Parse(JsonNode node, string f)
    {
        var o = CmcExtrinsic.Obj(node, f);
        CmcExtrinsic.Only(o, f, Keys);
        var d = Default;
        double? N(string k) => CmcExtrinsic.Num(o, k, $"{f}.{k}");
        double[] Ns(string k) => o[k] is JsonArray a ? [.. a.Select((x, i) => CmcExtrinsic.NumberOf(x, $"{f}.{k}[{i}]"))] : [];
        Complex? Z(string k) => o[k] is { } z ? CurrentFitSettings.Pair(z, $"{f}.{k}") is var (re, im) ? new Complex(re, im) : null : null;
        var sections = o["Sections"] is { } sn ? CurrentFitSettings.Names(sn, $"{f}.Sections") : SectionNames;
        foreach (string s in sections)
            if (!SectionNames.Contains(s)) throw new CmcFormatException($"{f}.Sections", $"'{s}' is not one of {string.Join(", ", SectionNames)}.");
        var modes = o["Modes"] is { } mn
            ? [.. CurrentFitSettings.Names(mn, $"{f}.Modes").Select(m => Enum.TryParse<PaClass>(m, out var v) ? v
                : throw new CmcFormatException($"{f}.Modes", $"'{m}' is not one of {string.Join(", ", Enum.GetNames<PaClass>())}."))]
            : d.Modes;
        var biases = o["SBiases"] is JsonArray ba
            ? [.. ba.Select((x, i) => CurrentFitSettings.Pair(x!, $"{f}.SBiases[{i}]"))] : new List<(double, double)>();
        return new ReportOptions
        {
            Dc = sections.Contains("Dc"), Thermal = sections.Contains("Thermal"), Charge = sections.Contains("Charge"),
            CapacitanceSlices = sections.Contains("CapacitanceSlices"), SParams = sections.Contains("SParams"),
            PaModes = sections.Contains("PaModes"),
            SBiases = biases, FixedVds = Ns("FixedVds"), FixedVgs = Ns("FixedVgs"), Modes = modes,
            FrequencyHz = N("FrequencyHz"), Vgs = N("Vgs"), Vds = N("Vds"), MaxHarmonic = (int)(N("MaxHarmonic") ?? d.MaxHarmonic),
            SourceZ = Z("SourceZ"), Source2f0 = Z("Source2f0"), RoptOhms = N("RoptOhms"),
            GammaRadius = N("GammaRadius") ?? d.GammaRadius, Rings = (int)(N("Rings") ?? d.Rings), Spokes = (int)(N("Spokes") ?? d.Spokes),
            PinStart = N("PinStart") ?? d.PinStart, PinStep = N("PinStep") ?? d.PinStep, PinMax = N("PinMax") ?? d.PinMax,
            Compression = N("Compression") ?? d.Compression,
        };
    }

    public JsonNode ToJson()
    {
        var d = Default;
        var o = new JsonObject();
        bool[] on = [Dc, Thermal, Charge, CapacitanceSlices, SParams, PaModes];
        if (on.Any(x => !x)) o["Sections"] = new JsonArray([.. SectionNames.Where((_, i) => on[i]).Select(s => (JsonNode)s)]);
        if (SBiases.Count > 0) o["SBiases"] = new JsonArray([.. SBiases.Select(b => (JsonNode)new JsonArray(b.Vgs, b.Vds))]);
        if (FixedVds.Count > 0) o["FixedVds"] = new JsonArray([.. FixedVds.Select(v => (JsonNode)v)]);
        if (FixedVgs.Count > 0) o["FixedVgs"] = new JsonArray([.. FixedVgs.Select(v => (JsonNode)v)]);
        if (!Modes.SequenceEqual(d.Modes)) o["Modes"] = new JsonArray([.. Modes.Select(m => (JsonNode)m.ToString())]);
        void Opt(string k, double? v) { if (v is { } x) o[k] = x; }
        void Z(string k, Complex? z) { if (z is { } c) o[k] = new JsonArray(c.Real, c.Imaginary); }
        Opt("FrequencyHz", FrequencyHz); Opt("Vgs", Vgs); Opt("Vds", Vds);
        if (MaxHarmonic != d.MaxHarmonic) o["MaxHarmonic"] = MaxHarmonic;
        Z("SourceZ", SourceZ); Z("Source2f0", Source2f0); Opt("RoptOhms", RoptOhms);
        if (GammaRadius != d.GammaRadius) o["GammaRadius"] = GammaRadius;
        if (Rings != d.Rings) o["Rings"] = Rings;
        if (Spokes != d.Spokes) o["Spokes"] = Spokes;
        if (PinStart != d.PinStart) o["PinStart"] = PinStart;
        if (PinStep != d.PinStep) o["PinStep"] = PinStep;
        if (PinMax != d.PinMax) o["PinMax"] = PinMax;
        if (Compression != d.Compression) o["Compression"] = Compression;
        return o;
    }
}

/// <summary>One section of a report: its name, its DataSet, its summary lines, and the testbenches it ran (file name →
/// text).</summary>
public sealed record ReportSection(string Name, DataSet Data, IReadOnlyList<string> Lines, IReadOnlyDictionary<string, string> Files)
{
    /// <summary>What the section ran at, enough to reproduce it; a line indented two spaces belongs to the one above.</summary>
    public IReadOnlyList<string> Conditions { get; init; } = [];
    /// <summary>Its scorecard rows.</summary>
    public IReadOnlyList<ScoreRow> Scores { get; init; } = [];
}

/// <summary>A report: the model it ran, its sections in order, and its card.</summary>
public sealed record ModelReport(string ModelPath, IReadOnlyList<ReportSection> Sections)
{
    public IReadOnlyList<string> Lines => [.. Sections.SelectMany(s => s.Lines)];
    public ReportCard Card { get; init; } = new();
}

public static class ModelValidationReport
{
    public const string StepName = "Report";

    // ── The document ──────────────────────────────────────────────────────────

    public static ReportOptions Options(CmcDocument doc) =>
        doc.Steps is JsonObject s && s[StepName] is JsonObject o && o["Settings"] is { } n
            ? ReportOptions.Parse(n, $"Steps.{StepName}.Settings") : ReportOptions.Default;

    public static void Validate(CmcDocument doc)
    {
        if (doc.Steps is not JsonObject s || s[StepName] is not { } n) return;
        CmcExtrinsic.Only(CmcExtrinsic.Obj(n, $"Steps.{StepName}"), $"Steps.{StepName}", ["Settings"]);
        Options(doc);
    }

    public static CmcDocument WithOptions(CmcDocument doc, ReportOptions options)
    {
        var steps = doc.Steps is JsonObject s ? (JsonObject)JsonNode.Parse(s.ToJsonString())! : new JsonObject();
        steps[StepName] = new JsonObject { ["Settings"] = options.ToJson() };
        return doc with { Steps = steps };
    }

    // ── The run ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The report of <paramref name="cmodelPath"/> — or, when null, of the document's current state exported into its
    /// cache — against the document's data (R-mc11-6). Throws <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static ModelReport Generate(string cmcPath, string? cmodelPath = null, ReportOptions? options = null, RunControl? rc = null)
    {
        cmcPath = Path.GetFullPath(cmcPath);
        var doc = CmcFile.Read(cmcPath);
        options ??= Options(doc);

        // The validation's items carry the DC, pulsed and S sections; it also exports the current state when asked to.
        var fitSets = doc.DataSets.Where(s => s.Kind is DataSetKind.DcIv or DataSetKind.PulsedIv or DataSetKind.SParams)
                                  .Where(s => s.Kind == DataSetKind.SParams ? options.SParams : options.Dc).Select(s => s.Name).ToList();
        // An empty DataSets would mean every set — the large-signal ones included — so with none to validate there is no run.
        var (modelPath, data, work) = ModelValidation.Prepare(cmcPath, doc, cmodelPath, rc);
        var validation = fitSets.Count > 0
            ? ModelValidation.Run(cmcPath, doc, data, modelPath, ValidationSettings.Default with { DataSets = fitSets }, work, rc)
            : new ValidationResult(modelPath, [], []);
        var ctx = new ModelValidation.Context(cmcPath, doc, data, validation.ModelPath, CmodelFile.Read(validation.ModelPath),
                                              ValidationSettings.Default with { MaxHarmonic = options.MaxHarmonic }, work, rc);
        var program = ComposedCoreProgram.Compile(ctx.Model, "report");

        var sections = new List<ReportSection>();
        PaBiasPoint? paBias = null;
        if (options.Dc && validation.Items.Any(i => i.Kind is ValidationKind.DcIv or ValidationKind.PulsedIv))
            sections.Add(FromItems("DC", validation.Items.Where(i => i.Kind is ValidationKind.DcIv or ValidationKind.PulsedIv)));
        if (options.Thermal && ctx.Model.Thermal is { } th && Thermal(ctx.Doc, ctx.Data, ctx.Intrinsic, program, th) is { } thermal)
            sections.Add(thermal);
        if (options.Charge || options.CapacitanceSlices)
        {
            ChargeData? charges = null;
            // Typed charges (MC-18) are constants with no measurement to draw them against: one line says what they are.
            if (ChargeFit.Record(doc)?.Typed is { } typed) sections.Add(Note("Charge", $"Charge: typed — {typed.Text}."));
            else
            {
                try { charges = ChargeFit.Gather(doc, data, ctx.Intrinsic, ChargeFit.Settings(doc), []); }
                catch (ModelingException ex) { sections.Add(Note("Charge", $"Charge: not reported — {ex.Message}")); }
            }
            if (charges is not null)
            {
                if (options.Charge && charges.SpansArea)
                {
                    ctx.Rc?.BeginStage("Report: charge surfaces");
                    var poly = charges.Domain.Polygon;
                    double[] vgs = Regrid.Axis(poly.Min(p => p.Vgs), poly.Max(p => p.Vgs), ChargeConstituentSettings.DefaultGridStep);
                    double[] vds = Regrid.Axis(poly.Min(p => p.Vds), poly.Max(p => p.Vds), ChargeConstituentSettings.DefaultGridStep);
                    var anchor = ChargeFit.Record(ctx.Doc)?.Anchor ?? ChargeFit.Anchor(charges, ChargeFitSettings.Default, []);
                    var cs = ChargeSurface(program, charges, anchor, vgs, vds, ctx.Rc);
                    sections.Add(cs with { Conditions = [ChargeSets(ctx), .. cs.Conditions] });
                }
                if (options.CapacitanceSlices && Slices(program, charges, options) is var cv)
                    sections.Add(cv with { Conditions = [ChargeSets(ctx), .. cv.Conditions] });
            }
        }
        if (options.SParams && validation.Items.Any(i => i.Kind == ValidationKind.SParams))
            sections.Add(SPicked(validation.Items.Where(i => i.Kind == ValidationKind.SParams), options));
        if (options.PaModes)
        {
            var bias = paBias = PaBias(ctx, program, options);
            int n0 = ctx.Model.BasePeriphery.Fingers;
            var drain = ComposedClassLoads.DrainTwoPort(ComposedNetworkTemplate.Build(ctx.Model.Network), n0, n0, 1.0,
                                                        bias.FrequencyHz, options.MaxHarmonic);
            foreach (var mode in options.Modes)
            {
                rc?.ThrowIfCancellationRequested();
                sections.Add(Mode(ctx, program, mode, bias, drain, options));
            }
        }
        if (validation.Notes.Count > 0)
            sections.Add(Note("Notes", [.. validation.Notes]) with
            {
                Scores = [.. validation.Notes.Where(n => n.Contains(" — ", StringComparison.Ordinal))
                    .Select(n => new ScoreRow("Notes", n[..n.IndexOf(':')], "not checked", Grade.Yellow,
                                              $"This data could not be compared: {n[(n.IndexOf(" — ", StringComparison.Ordinal) + 3)..]}"))],
            });
        var validated = validation.Items.Select(i => i.Set).ToHashSet(StringComparer.Ordinal);
        sections.Insert(0, Coverage(ctx, paBias, ReportGrades.Roles(ctx, validated)));

        var (validity, methods, simulation, coverage) = ReportGrades.FrontMatter(ctx, validated, options.MaxHarmonic);
        var card = new ReportCard
        {
            Model = cmodelPath is null ? $"the current state of {Path.GetFileName(cmcPath)}" : Path.GetFileName(validation.ModelPath),
            Document = Path.GetFileName(cmcPath),
            Scores = [.. sections.SelectMany(s => s.Scores)],
            Validity = validity, Methods = methods, Simulation = simulation, Coverage = coverage,
            Sections = [.. sections.Select(s => new SectionText(s.Name, s.Conditions, s.Lines, [.. s.Files.Keys]))],
        };
        return new ModelReport(validation.ModelPath, sections) { Card = card };
    }

    /// <summary>The sets the capacitances came from, as a condition line.</summary>
    private static string ChargeSets(ModelValidation.Context ctx) =>
        $"Data sets: {string.Join(", ", ChargeFit.Chosen(ctx.Doc, ctx.Intrinsic, ChargeFit.Settings(ctx.Doc)).Select(s => s.Name))}, " +
        "their capacitances at the intrinsic plane";

    // ── Coverage ──────────────────────────────────────────────────────────────

    /// <summary>The Coverage section's groups, in the order they are drawn: the cube prefix and what its points are.</summary>
    public static readonly IReadOnlyList<(string Group, string Meaning)> CoverageGroups =
    [
        ("IV fit", "the I–V fit's points (DC and pulsed)"), ("CV fit", "the C–V fit's points (S data)"),
        ("Checked", "points used only to check the model"), ("Large signal", "large-signal measurements"), ("PA bias", "the PA sections' bias"),
    ];

    /// <summary>Every data set's bias points at the device plane, as the validation's testbenches apply them, grouped by what
    /// they were used for, and the PA sections' bias — the picture of where the model was fitted and checked.</summary>
    private static ReportSection Coverage(ModelValidation.Context ctx, PaBiasPoint? pa, (IReadOnlySet<string> Iv, IReadOnlySet<string> Cv, IReadOnlySet<string> Checked) roles)
    {
        var groups = CoverageGroups.ToDictionary(g => g.Group, _ => (Vgs: new List<double>(), Vds: new List<double>()));
        foreach (var set in ctx.Doc.DataSets)
        {
            string? group = roles.Iv.Contains(set.Name) ? "IV fit" : roles.Cv.Contains(set.Name) ? "CV fit"
                          : roles.Checked.Contains(set.Name) ? (set.Kind == DataSetKind.LargeSignal ? "Large signal" : "Checked") : null;
            if (group is null) continue;
            if (set.Conditions.LargeSignal is { Vgs: { } lg, Vds: { } ld })
            {
                groups[group].Vgs.Add(lg); groups[group].Vds.Add(ld);
                continue;
            }
            if (!ctx.Data.TryGetValue(set.Name, out var nd) || nd.Count == 0) continue;
            double[] vgs, vds;
            try { (vgs, vds, _) = ModelValidation.DevicePlane(ctx, set, nd); }
            catch (ModelingException) { continue; }
            foreach (int k in Enumerable.Range(0, nd.Count).Where(k => !nd.Points[k].Excluded && double.IsFinite(vgs[k]) && double.IsFinite(vds[k])))
            {
                groups[group].Vgs.Add(vgs[k]); groups[group].Vds.Add(vds[k]);
            }
        }
        if (pa is not null) { groups["PA bias"].Vgs.Add(pa.Vgs); groups["PA bias"].Vds.Add(pa.Vds); }
        var ds = new DataSet();
        foreach (var (group, _) in CoverageGroups)
        {
            var (vgs, vds) = groups[group];
            if (vgs.Count == 0) continue;
            var axis = new Axis("point", [.. Enumerable.Range(0, vgs.Count).Select(i => (double)i)]);
            ds.Add($"{group}.Vgs", new DataCube([axis], [.. vgs]));
            ds.Add($"{group}.Vds", new DataCube([axis], [.. vds]));
        }
        var conditions = new List<string>
        {
            "Every data set's bias points at the device plane, excluded points left out, as the testbenches apply them; " +
            "the Data table says which sets fitted the model and which checked it",
            "Markers: " + string.Join("; ", CoverageGroups.Where(g => ds.Contains($"{g.Group}.Vgs"))
                                                          .Select(g => $"{ReportMarkers.Symbol(g.Group)} {g.Meaning}")),
        };
        if (pa is not null) conditions.Add($"PA bias (the PA class sections): Vgs {G(pa.Vgs)} V, Vds {G(pa.Vds)} V");
        return new ReportSection("Coverage", ds, [], new Dictionary<string, string>()) { Conditions = conditions };
    }

    /// <summary>Where a report is written when no folder is named: <c>&lt;name&gt;-report/</c> beside the <c>.cmc</c>.</summary>
    public static string DefaultFolder(string cmcPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cmcPath))!, Path.GetFileNameWithoutExtension(cmcPath) + "-report");

    /// <summary>
    /// Writes <paramref name="report"/> into <paramref name="folder"/>: <c>report.npy</c> (one group per section),
    /// <c>summary.txt</c> and <c>testbenches/</c>. Written into a sibling folder and swapped in, so a failure leaves the
    /// previous report whole. Returns the paths written.
    /// </summary>
    public static IReadOnlyList<string> Write(ModelReport report, string folder)
    {
        folder = Path.GetFullPath(folder);
        string tmp = folder + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, "testbenches"));
            var ds = new DataSet();
            foreach (var s in report.Sections)
                foreach (var (name, cube) in s.Data.Cubes) ds.AddToGroup(Group(s.Name), name, cube);
            using (var fs = File.Create(Path.Combine(tmp, "report.npy"))) DataSetExporter.ExportNpy(ds, fs);
            File.WriteAllText(Path.Combine(tmp, "summary.txt"), report.Card.ToText());
            File.WriteAllText(Path.Combine(tmp, ReportCard.FileName), report.Card.ToJson());
            foreach (var s in report.Sections)
                foreach (var (name, text) in s.Files) File.WriteAllText(Path.Combine(tmp, "testbenches", name), text);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.Move(tmp, folder);
        }
        catch
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch (IOException) { }
            throw;
        }
        return [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
    }

    private static string Group(string section) => string.Concat(section.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    private static ReportSection Note(string name, params string[] lines) => new(name, new DataSet(), lines, new Dictionary<string, string>());

    /// <summary>A section of validation items: their cubes under <c>&lt;set&gt;.&lt;cube&gt;</c>, their lines, their testbenches.</summary>
    private static ReportSection FromItems(string name, IEnumerable<ValidationItem> items)
    {
        var ds = new DataSet();
        var lines = new List<string>();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var conditions = new List<string>();
        var scores = new List<ScoreRow>();
        foreach (var item in items)
        {
            foreach (var (cube, value) in item.Data.Cubes) ds.Add($"{item.Set}.{cube}", value);
            lines.Add(item.Line);
            string bench = $"{Group(name)}.{Group(item.Set)}.cnl";
            if (item.Testbench.Length > 0) files[bench] = item.Testbench;
            conditions.Add($"'{item.Set}'" + (item.Testbench.Length > 0 ? $" — testbench {bench}" : "") + ":");
            conditions.AddRange(item.Conditions.Select(c => "  " + c));
            scores.Add(ReportGrades.Row(name, item));
        }
        return new ReportSection(name, ds, lines, files) { Conditions = conditions, Scores = scores };
    }

    // ── S at the chosen biases ────────────────────────────────────────────────

    private static ReportSection SPicked(IEnumerable<ValidationItem> items, ReportOptions o)
    {
        var section = FromItems("S", items);
        var ds = section.Data;
        var lines = section.Lines.ToList();
        foreach (var item in items)
        {
            double[] vgs = item.Data["Vgs"].RealValues, vds = item.Data["Vds"].RealValues, dS = item.Data["dS"].RealValues;
            var picked = new List<int>();
            if (o.SBiases.Count > 0)
                foreach (var (g, d) in o.SBiases)
                    picked.Add(Enumerable.Range(0, vgs.Length).MinBy(k => Math.Pow(vgs[k] - g, 2) + Math.Pow(vds[k] - d, 2)));
            else
            {
                // The lowest, middle and highest |S21| at the lowest frequency stand in for the drain current's range.
                var s = item.Data["S.meas"];
                int nf = s.Axes[1].Length;
                var order = Enumerable.Range(0, vgs.Length).Where(k => double.IsFinite(dS[k]))
                                      .OrderBy(k => s.ComplexValues[k * nf * 4 + 2].Magnitude).ToList();
                if (order.Count > 0) picked.AddRange([order[0], order[order.Count / 2], order[^1]]);
            }
            picked = [.. picked.Distinct()];
            ds.Add($"{item.Set}.Picked", new DataCube([new Axis("pick", [.. picked.Select(k => (double)k)])], [.. picked.Select(k => (double)k)]));
            foreach (int k in picked)
                lines.Add($"S {item.Set} at Vgs {G(vgs[k])} V, Vds {G(vds[k])} V: |ΔS| RMS {G(dS[k])}");
        }
        return section with { Lines = lines };
    }

    // ── Thermal ───────────────────────────────────────────────────────────────

    /// <summary>
    /// [R11] §2.8's equivalent-temperature check: at each self-heated DC point, the temperature at which the isothermal
    /// model carries the measured channel current; its rise over the ambient against the dissipated power is a line whose
    /// slope is the data's Rth at that ambient. Each ambient's slope is set against the model's <c>RthAt</c>.
    /// </summary>
    internal static ReportSection? Thermal(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data, IntrinsicResult intrinsic,
                                           ComposedCoreProgram program, ModelThermal thermal)
    {
        var dcSets = doc.DataSets.Where(s => s.Kind == DataSetKind.DcIv).ToList();
        if (dcSets.Count == 0 || !program.HasT) return null;
        var lines = new List<string>();
        var ds = new DataSet();
        var allP = new List<double>(); var allRise = new List<double>(); var allT = new List<double>();
        var scores = new List<ScoreRow>();
        foreach (var set in dcSets)
        {
            CurrentFit.IvPoints ip;
            try { ip = CurrentFit.Gather(doc, data, intrinsic, CurrentFit.Settings(doc) with { DataSets = [set.Name] }, [], ThermalHeating.None, withTraps: false); }
            catch (ModelingException) { continue; }
            var iso = Isothermal(program);
            for (int k = 0; k < ip.X.Length; k++)
            {
                double ta = ip.HasT ? ip.X[k][2] : ip.TemperatureC;
                double p = CurrentFit.Power(ip.X[k][0], ip.X[k][1], double.IsFinite(ip.Ig[k]) ? ip.Ig[k] : 0, ip.Id[k]);
                if (ThermalFit.JunctionTemperature(iso, ip.X[k][0], ip.X[k][1], ip.Id[k], ta) is not { } tj) continue;
                allP.Add(p); allRise.Add(tj - ta); allT.Add(ta);
            }
        }
        if (allP.Count == 0) return Note("Thermal", "Thermal: no DC point resolved an equivalent junction temperature.");
        var axis = new Axis("point", [.. Enumerable.Range(0, allP.Count).Select(i => (double)i)]);
        ds.Add("P", new DataCube([axis], [.. allP]));
        ds.Add("Rise.data", new DataCube([axis], [.. allRise]));
        ds.Add("Rise.model", new DataCube([axis], [.. allP.Select((p, k) => thermal.RthAt(allT[k]) * p)]));
        ds.Add("Tambient", new DataCube([axis], [.. allT]));
        foreach (var group in allT.Select((t, k) => (t, k)).GroupBy(x => Math.Round(x.t, 2)))
        {
            var idx = group.Select(x => x.k).Where(k => allP[k] > 0.1 * allP.Max()).ToList();
            if (idx.Count < 2) continue;
            double rth = idx.Sum(k => allP[k] * allRise[k]) / idx.Sum(k => allP[k] * allP[k]);
            double model = thermal.RthAt(group.Key);
            lines.Add($"Rth at {G(group.Key)} °C: data {G(rth)} °C/W, model {G(model)} °C/W ({G(100 * (model - rth) / rth)} %)");
            double dev = Math.Abs(model - rth) / rth;
            var grade = ReportGrades.Banded(dev, ReportGrades.RthGreen, ReportGrades.RthYellow);
            scores.Add(new ScoreRow("Thermal", $"Rth at {G(group.Key)} °C", $"model {G(model)} °C/W against the data's {G(rth)} °C/W ({G(100 * dev)} %)", grade,
                grade switch
                {
                    Grade.Green => "Self-heating matches the data: the channel temperature, and the current droop it causes, can be trusted.",
                    Grade.Yellow => "Self-heating is somewhat off: expect the hot-channel current and efficiency at high dissipation to be a little wrong.",
                    _ => "Self-heating disagrees with the data: currents at high dissipation, and anything thermal, are not reliable.",
                }));
        }
        if (lines.Count == 0) lines.Add("Thermal: too few heated DC points to estimate Rth from the data.");
        return new ReportSection("Thermal", ds, lines, new Dictionary<string, string>())
        {
            Conditions =
            [
                $"DC sets: {string.Join(", ", dcSets.Select(s => s.Name))}; {allP.Count} point(s) resolved an equivalent junction temperature",
                "Each DC point's equivalent junction temperature is where the isothermal model carries its measured current; its rise " +
                "over the ambient against the dissipated power is the data's Rth — fitted over points above 10 % of the largest power",
                ModelValidation.Ambient(thermal, allT),
            ],
            Scores = scores,
        };
    }

    /// <summary>The model's channel current at a temperature, with no self-heating — what the equivalent temperature solves.</summary>
    private static Func<double[], (double F, double Dg, double Dd)> Isothermal(ComposedCoreProgram program) => x =>
    {
        Span<double> s = stackalloc double[RuntimeEvaluator.MaxInputs], g = stackalloc double[RuntimeEvaluator.MaxInputs];
        program.DcSeeds(x[0], x[1], x[2], s);
        double id = program.Channel(s[..program.Width], g);
        return (id, g[0], g[1]);
    };

    // ── Charge surface difference ─────────────────────────────────────────────

    /// <summary>The model's Qg and Qd over the grid (<paramref name="vgs"/>, <paramref name="vds"/>) against the line integral of the
    /// measured capacitances from <paramref name="anchor"/>, and their difference in % of each charge's range inside the data's
    /// hull — the report's Charge section and the Charge step's maps (MC-15).</summary>
    internal static ReportSection ChargeSurface(ComposedCoreProgram program, ChargeData data, (double Vgs, double Vds) anchor,
                                                double[] vgs, double[] vds, RunControl? rc)
    {
        var poly = data.Domain.Polygon;
        var axes = new[] { new Axis("vgs", vgs, "V"), new Axis("vds", vds, "V") };
        var ds = new DataSet();
        var lines = new List<string>();
        var scores = new List<ScoreRow>();
        foreach (bool gate in (bool[])[true, false])
        {
            var (c1, c2) = data.Row(gate);
            var f1 = Regrid.Rbf(data.X, c1); var f2 = Regrid.Rbf(data.X, c2);
            double typical = Math.Sqrt(c1.Concat(c2).Average(v => v * v));
            var (a, b) = LineIntegral.OntoGrid(f1, f2, anchor, vgs, vds, 1e-7 * typical * ChargeConstituentSettings.DefaultGridStep, rc);
            var measured = new double[a.Length]; var model = new double[a.Length]; var diff = new double[a.Length];
            double q0 = Charge(program, anchor.Vgs, anchor.Vds, gate);
            var inside = new HashSet<int>();
            for (int i = 0; i < vgs.Length; i++)
                for (int j = 0; j < vds.Length; j++)
                {
                    int k = i * vds.Length + j;
                    measured[k] = 0.5 * (a[k] + b[k]);
                    model[k] = Charge(program, vgs[i], vds[j], gate) - q0;
                    if (ChargeFit.Inside(poly, (vgs[i], vds[j]))) inside.Add(k);
                }
            double range = inside.Count > 0 ? inside.Max(k => measured[k]) - inside.Min(k => measured[k]) : 0;
            for (int k = 0; k < diff.Length; k++)
                diff[k] = inside.Contains(k) && range > 0 ? 100 * (model[k] - measured[k]) / range : double.NaN;
            string q = gate ? "Qg" : "Qd";
            ds.Add($"{q}.data", new DataCube(axes, measured));
            ds.Add($"{q}.model", new DataCube(axes, model));
            ds.Add($"d{q}", new DataCube(axes, diff));
            double rms = inside.Count > 0 ? Math.Sqrt(inside.Average(k => diff[k] * diff[k])) : double.NaN;
            double largest = inside.Count > 0 ? inside.Max(k => Math.Abs(diff[k])) : double.NaN;
            lines.Add($"Charge {q}: model against the data's line integral, RMS {G(rms)} % of range, largest {G(largest)} %");
            var grade = ReportGrades.Banded(rms, ReportGrades.ChargeGreen, ReportGrades.ChargeYellow);
            scores.Add(new ScoreRow("Charge", q, $"RMS {G(rms)} % of range, largest {G(largest)} % (Green ≤ {G(ReportGrades.ChargeGreen)} %, Yellow ≤ {G(ReportGrades.ChargeYellow)} %)", grade,
                grade switch
                {
                    Grade.Green => $"{q} follows the measured capacitances: gain roll-off, matching and harmonic content can be trusted.",
                    Grade.Yellow => $"{q} departs from the measured capacitances in places — see the Charge plots; matching and harmonics there are approximate.",
                    _ => $"{q} does not follow the measured capacitances: matching, bandwidth and harmonic terminations are not reliable.",
                }));
        }
        return new ReportSection("Charge", ds, lines, new Dictionary<string, string>())
        {
            Conditions =
            [
                $"Grid: Vgs {G(vgs[0])} … {G(vgs[^1])} V, Vds {G(vds[0])} … {G(vds[^1])} V, {G(vgs.Length > 1 ? vgs[1] - vgs[0] : 0)} V steps, intrinsic plane; " +
                "only points inside the data's hull are compared",
                $"The data's charge: the line integral of the measured capacitances from Vgs {G(anchor.Vgs)} V, Vds {G(anchor.Vds)} V, where both charges are 0",
                "The model's charges at 25 °C",
            ],
            Scores = scores,
        };
    }

    private static double Charge(ComposedCoreProgram program, double vgs, double vds, bool gate)
    {
        Span<double> s = stackalloc double[RuntimeEvaluator.MaxInputs];
        Span<double> gg = stackalloc double[RuntimeEvaluator.MaxInputs], gd = stackalloc double[RuntimeEvaluator.MaxInputs];
        program.DcSeeds(vgs, vds, 25, s);
        var (qg, qd) = program.Charges(s[..program.Width], gg, gd);
        return gate ? qg : qd;
    }

    // ── C–V families ──────────────────────────────────────────────────────────

    /// <summary>The circuit capacitances from the matrix: Cgs = C11 + C12, Cgd = −C12, Cds = C22 + C12, written as [P1] does,
    /// with the transcapacitance Cm = C21 − C12.</summary>
    internal static readonly (string Name, Func<double, double, double, double, double> Of)[] Circuit =
    [
        ("Cgs", (c11, c12, _, _) => c11 + c12), ("Cgd", (_, c12, _, _) => -c12),
        ("Cds", (_, c12, _, c22) => c22 + c12), ("Cm", (_, c12, c21, _) => c21 - c12),
    ];

    private static ReportSection Slices(ComposedCoreProgram program, ChargeData data, ReportOptions o)
    {
        var ds = new DataSet();
        var lines = new List<string>();
        foreach (bool sweepGate in (bool[])[true, false])
        {
            var swept = data.X.Select(p => sweepGate ? p.Vgs : p.Vds).ToArray();
            var fixedOf = data.X.Select(p => sweepGate ? p.Vds : p.Vgs).ToArray();
            var family = (sweepGate ? o.FixedVds : o.FixedVgs) is { Count: > 0 } given ? given.ToArray() : Spread(fixedOf, 5);
            var (lo, hi) = ChargeFit.SliceSpan(swept);
            const int n = 101;
            var x = Enumerable.Range(0, n).Select(k => lo + (hi - lo) * k / (n - 1)).ToArray();
            string sweptName = sweepGate ? "vgs" : "vds", fixedName = sweepGate ? "vds" : "vgs";
            var familyAxis = new Axis(fixedName, family, "V");
            var sweepAxis = new Axis(sweptName, x, "V");
            var cubes = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (string c in ChargeFitRecord.Capacitances.Concat(Circuit.Select(c => c.Name))) cubes[c] = new double[family.Length * n];
            for (int fi = 0; fi < family.Length; fi++)
            {
                var slice = ChargeFit.Slice(program, sweepGate, family[fi], lo, hi, n);
                double[][] m = [.. ChargeFitRecord.Capacitances.Select(c => slice[c].RealValues)];
                for (int k = 0; k < n; k++)
                {
                    for (int c = 0; c < 4; c++) cubes[ChargeFitRecord.Capacitances[c]][fi * n + k] = m[c][k];
                    foreach (var (name, of) in Circuit) cubes[name][fi * n + k] = of(m[0][k], m[1][k], m[2][k], m[3][k]);
                }
            }
            string tag = sweepGate ? "vsVgs" : "vsVds";
            foreach (var (name, values) in cubes) ds.Add($"{tag}.{name}.model", new DataCube([familyAxis, sweepAxis], values));

            // The measured points nearest each fixed voltage — within half the family's spacing.
            double half = family.Length > 1 ? 0.5 * family.Zip(family.Skip(1), (a, b) => Math.Abs(b - a)).Min() : double.PositiveInfinity;
            var member = fixedOf.Select(v => family.Select((f, i) => (D: Math.Abs(v - f), i)).MinBy(t => t.D) is var t && t.D <= half ? t.i : -1).ToArray();
            var pts = Enumerable.Range(0, data.X.Count).Where(k => member[k] >= 0).ToArray();
            var pAxis = new Axis("point", [.. pts.Select(k => (double)k)]);
            ds.Add($"{tag}.member", new DataCube([pAxis], [.. pts.Select(k => family[member[k]])]));
            ds.Add($"{tag}.{sweptName}", new DataCube([pAxis], [.. pts.Select(k => swept[k])]));
            double[][] meas = [data.C11, data.C12, data.C21, data.C22];
            for (int c = 0; c < 4; c++) ds.Add($"{tag}.{ChargeFitRecord.Capacitances[c]}.meas", new DataCube([pAxis], [.. pts.Select(k => meas[c][k])]));
            foreach (var (name, of) in Circuit)
                ds.Add($"{tag}.{name}.meas", new DataCube([pAxis], [.. pts.Select(k => of(data.C11[k], data.C12[k], data.C21[k], data.C22[k]))]));
            lines.Add($"C–V against {sweptName}: {family.Length} curves at {fixedName} = {string.Join(", ", family.Select(G))} V, {pts.Length} measured point(s)");
        }
        return new ReportSection("C-V", ds, lines, new Dictionary<string, string>())
        {
            Conditions =
            [
                "The measured points shown are those within half the family's spacing of each fixed voltage",
            ],
        };
    }

    /// <summary><paramref name="count"/> of the distinct values, spread from the lowest to the highest.</summary>
    private static double[] Spread(IEnumerable<double> values, int count)
    {
        var distinct = values.Select(v => Math.Round(v, 6)).Distinct().Order().ToArray();
        if (distinct.Length <= count) return distinct;
        return [.. Enumerable.Range(0, count).Select(i => distinct[(int)Math.Round(i * (distinct.Length - 1) / (double)(count - 1))]).Distinct()];
    }

    // ── PA modes ──────────────────────────────────────────────────────────────

    internal sealed record PaBiasPoint(double FrequencyHz, double Vgs, double Vds, double Ropt, Complex SourceZ, double AmbientC, double C22);

    /// <summary>The PA conditions: the stated ones, and the defaults the options describe where none is stated.</summary>
    private static PaBiasPoint PaBias(ModelValidation.Context ctx, ComposedCoreProgram program, ReportOptions o)
    {
        var points = ctx.Data.Values.SelectMany(d => d.Points).Where(p => !p.Excluded).ToList();
        double f0 = o.FrequencyHz ?? DefaultFrequencyHz(ctx.Doc, ctx.Data);
        double vds = o.Vds ?? DefaultVds(ctx.Data);
        double t = ctx.Model.Temperature.Mode == TemperatureMode.Single ? ctx.Model.Temperature.T0 : 25;
        double gLo = points.Count > 0 ? points.Min(p => p.Vgs) : -5, gHi = points.Count > 0 ? points.Max(p => p.Vgs) : 0;
        // Imax from the data, not the model at (the highest vgs, the highest vds): the I–V data never reaches that corner —
        // the current saturates and the junction heats first — so a network is unconstrained there, and one read 34 A
        // where the data's largest current was 0.16 A, which made R_opt 1 Ω and every class a −60 dB attenuator.
        var iv = ctx.Doc.DataSets.Where(s => s.Kind is DataSetKind.DcIv or DataSetKind.PulsedIv or DataSetKind.SimulatedTables && ctx.Data.ContainsKey(s.Name))
                                 .SelectMany(s => ctx.Data[s.Name].Points).Where(p => !p.Excluded && double.IsFinite(p.Id)).ToList();
        double imax = iv.Count > 0 ? iv.Max(p => Math.Abs(p.Id)) : Math.Abs(Id(program, gHi, vds, t));
        double vgs = o.Vgs ?? Bisect(v => Math.Abs(Id(program, v, vds, t)) - 0.02 * imax, gLo, gHi);
        double ropt = o.RoptOhms ?? (imax > 0 ? 2 * 0.9 * vds / imax : 50);   // vknee = vds/10
        var zs = o.SourceZ ?? InputMatch(ctx, f0, vgs, vds, ropt, t);
        return new PaBiasPoint(f0, vgs, vds, ropt, zs, t, OutputCapacitance(ctx, program, vgs, vds, t).C22);
    }

    /// <summary>The PA classes' frequency when none is stated: the S data's median frequency, else 2 GHz. Public so the
    /// report dialog shows the value the report will use.</summary>
    public static double DefaultFrequencyHz(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data)
    {
        var f = doc.DataSets.Where(s => s.Kind == DataSetKind.SParams && data.ContainsKey(s.Name))
                   .SelectMany(s => data[s.Name].FrequenciesHz ?? []).ToList();
        return f.Count > 0 ? Median(f) : 2e9;
    }

    /// <summary>The PA classes' drain voltage when none is stated: half the largest measured, else 10 V.</summary>
    public static double DefaultVds(IReadOnlyDictionary<string, NormalisedData> data)
    {
        var points = data.Values.SelectMany(d => d.Points).Where(p => !p.Excluded).ToList();
        return points.Count > 0 ? 0.5 * points.Max(p => p.Vds) : 10;
    }

    private static double Id(ComposedCoreProgram program, double vgs, double vds, double t)
    {
        Span<double> s = stackalloc double[RuntimeEvaluator.MaxInputs], g = stackalloc double[RuntimeEvaluator.MaxInputs];
        program.DcSeeds(vgs, vds, t, s);
        return program.Channel(s[..program.Width], g);
    }

    private static double Bisect(Func<double, double> f, double lo, double hi)
    {
        double flo = f(lo);
        if (Math.Sign(flo) == Math.Sign(f(hi))) return lo;
        for (int i = 0; i < 60; i++)
        {
            double m = 0.5 * (lo + hi), fm = f(m);
            if (Math.Sign(fm) == Math.Sign(flo)) { lo = m; flo = fm; } else hi = m;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>The conjugate of the model's input impedance at the bias with <paramref name="rl"/> on the drain — the
    /// source a small-signal match would present.</summary>
    private static Complex InputMatch(ModelValidation.Context ctx, double f0, double vgs, double vds, double rl, double t)
    {
        var (lib, tb) = new Core.Netlist.CnlReader().Read(ValidationBench.SParams("report", ctx.Device(false, "g", "d"), t, vgs, vds, [f0], 50));
        using var nl = new Core.Elaboration.Elaborator(lib).Elaborate(tb);
        var s = SParameterEngine.Run(nl, [f0], HbCircuitRun.Settings())["S"];
        Complex s11 = (Complex)s[0, 0, 0], s12 = (Complex)s[0, 0, 1], s21 = (Complex)s[0, 1, 0], s22 = (Complex)s[0, 1, 1];
        Complex gl = (rl - 50) / (rl + 50), gin = s11 + s12 * s21 * gl / (1 - s22 * gl);
        Complex zin = 50 * (1 + gin) / (1 - gin);
        return new Complex(Math.Max(zin.Real, 0.5), -zin.Imaginary);
    }

    /// <summary>
    /// The model's drain self-capacitance C22 = ∂Qd/∂vds at the intrinsic DC bias of (<paramref name="b"/>'s) vgs and vds —
    /// harmonicaRF's linearized output capacitance (R9D §3.4: a nonlinear capacitor at its DC operating point).
    /// </summary>
    private static (double C22, double Vgsi, double Vdsi) OutputCapacitance(ModelValidation.Context ctx, ComposedCoreProgram program,
                                                                           double vgs, double vds, double t)
    {
        var (lib, tb) = new Core.Netlist.CnlReader().Read(ValidationBench.Dc("report", ctx.Device(false, "g", "d"), t, vgs, vds));
        using var nl = new Core.Elaboration.Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl, HbCircuitRun.Settings());
        if (!dc.Converged) throw new ModelingException(StepName, $"the DC solve at Vgs {G(vgs)} V, Vds {G(vds)} V did not converge.");
        double V(string node) => nl.Nodes.IndexOf(node) is var i and > 0 ? dc.NodeVoltages[i - 1] : 0.0;
        double vgsi = V("X1.gi") - V("X1.si"), vdsi = V("X1.di") - V("X1.si");
        double tj = t + (ctx.Model.Thermal is null ? 0 : V("X1.t"));
        return (ChargeFit.Capacitances(program, vgsi, vdsi, tj).C22, vgsi, vdsi);
    }

    /// <summary>The loadpull disc about <paramref name="centre"/>: its centre, then <c>rings × spokes</c> points out to
    /// <paramref name="radius"/> in Γ renormalised to the centre — the disc's own Möbius automorphism, so every point stays
    /// on the chart.</summary>
    internal static IReadOnlyList<Complex> Disc(Complex centre, double radius, int rings, int spokes) =>
        [centre, .. from r in Enumerable.Range(1, rings) from s in Enumerable.Range(0, spokes)
                    let g = Complex.FromPolarCoordinates(radius * r / rings, 2 * Math.PI * s / spokes)
                    select (g + centre) / (1 + Complex.Conjugate(centre) * g)];

    internal static string Label(PaClass cls) => cls switch
    {
        PaClass.B => "Class B", PaClass.J => "Class J", PaClass.JStar => "Class J*", PaClass.F => "Class F", _ => "Class F-1",
    };

    private static ReportSection Mode(ModelValidation.Context ctx, ComposedCoreProgram program, PaClass cls, PaBiasPoint b,
                                      C2[] drain, ReportOptions o)
    {
        string label = Label(cls), stem = Group(label);
        var notes = new List<string>();
        var loads = ComposedClassLoads.ExtrinsicLoads(cls, b.Ropt, b.FrequencyHz, b.C22, drain, notes);
        Complex g1 = (loads[0] - 50) / (loads[0] + 50);
        var grid = Disc(g1, o.GammaRadius, o.Rings, o.Spokes);
        int steps = (int)Math.Floor((o.PinMax - o.PinStart) / o.PinStep + 1e-9) + 1;
        double[] pin = [.. Enumerable.Range(0, steps).Select(k => o.PinStart + k * o.PinStep)];
        var lp = ModelValidation.LoadSweep(ctx, label, b.AmbientC, b.FrequencyHz, b.Vgs, b.Vds, b.SourceZ,
                                           [o.Source2f0 ?? ValidationBench.Short], loads[1..], grid, pin, o.Compression, out string cnl);
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { [$"{stem}.cnl"] = cnl };

        // The region of convergence: the loads whose drive-up converged to the target compression or the drive cap.
        var stop = lp["StopCode"].RealValues;
        var gamma = lp["GammaLoad"].ComplexValues;
        var converged = stop.Select(c => c is 0 or 1 ? 1.0 : 0.0).ToArray();
        double share = converged.Average();
        var failed = Enumerable.Range(0, stop.Length).Where(k => converged[k] == 0).ToList();

        var ds = new DataSet();
        foreach (var (name, cube) in lp.Cubes)
            if (!name.StartsWith("__", StringComparison.Ordinal) || name == "__Freq") ds.Add(name, cube);
        var gridAxis = lp["StopCode"].Axes[0];
        ds.Add("Converged", new DataCube([gridAxis], converged));
        var nan = new Complex(double.NaN, double.NaN);
        ds.Add("GammaLoad.converged", new DataCube([gridAxis], [.. gamma.Select((g, k) => converged[k] == 1 ? g : nan)]));
        ds.Add("GammaLoad.failed", new DataCube([gridAxis], [.. gamma.Select((g, k) => converged[k] == 1 ? nan : g)]));
        var band = new Axis("harmonic", [.. Enumerable.Range(1, loads.Length).Select(k => (double)k)]);
        ds.Add("ZLoad.extrinsic", new DataCube([band], loads));
        ds.Add("ZLoad.intrinsic", new DataCube([band], [.. Enumerable.Range(1, loads.Length).Select(k => PaClassPresets.IntrinsicLoad(cls, k, b.Ropt))]));
        string roc = $"converged at {G(100 * share)} % of the disc" +
                     (failed.Count > 0 ? $", nearest failure |ΔΓ| {G(failed.Min(k => Complex.Abs((gamma[k] - g1) / (1 - Complex.Conjugate(g1) * gamma[k]))))}" : "");

        var surface = new LoadpullSurface(lp);
        var c = ConstraintSpec.AtCompression(o.Compression);
        string At(Complex? g, string what)
        {
            if (g is not { } gg) return $"{what}: no load reached P-{G(o.Compression)}dB";
            double pout = surface.MetricAtCoord(0, "Pout_dBm", gg, c, SurfacePlane.Gamma);
            if (double.IsNaN(pout)) return $"{what} did not reach P-{G(o.Compression)}dB";
            double gain = surface.MetricAtCoord(0, "Gt_dB", gg, c, SurfacePlane.Gamma);
            double pae = surface.MetricAtCoord(0, "PAE", gg, c, SurfacePlane.Gamma);
            double de = surface.MetricAtCoord(0, "Efficiency", gg, c, SurfacePlane.Gamma);
            ds.Add($"{what}.Gamma", DataCube.Scalar(gg));
            ds.Add($"{what}.Summary", new DataCube([new Axis("metric", [0, 1, 2, 3], labels: ["Pout_dBm", "Gt_dB", "PAE", "Efficiency"])], [pout, gain, pae, de]));
            Complex z = 50 * (1 + gg) / (1 - gg);
            return $"{what} Pout {G(pout)} dBm, Gt {G(gain)} dB, PAE {G(pae)} %, DE {G(de)} % at {Zs(z)} Ω";
        }
        var lines = new List<string>
        {
            $"{label} ({G(b.FrequencyHz / 1e9)} GHz, Vgs {G(b.Vgs)} V, Vds {G(b.Vds)} V, R_opt {G(b.Ropt)} Ω, C22 {G(b.C22 * 1e15)} fF, " +
            $"P-{G(o.Compression)}dB): {At(converged[0] == 1 ? g1 : null, "Preset")} · " +
            $"{At(surface.MaxPower(0, c, SurfacePlane.Gamma)?.Interpolated, "MXP")} · " +
            $"{At(surface.MaxEfficiency(0, c, SurfacePlane.Gamma)?.Interpolated, "MXE")} · {roc}",
        };
        lines.AddRange(notes.Select(n => $"{label}: {n}"));

        int h = o.MaxHarmonic;
        var conditions = new List<string>
        {
            $"Frequency: {ModelValidation.GHz(b.FrequencyHz)} GHz; bias Vgs {G(b.Vgs)} V, Vds {G(b.Vds)} V at the device's terminals",
            $"Class load: R_opt {G(b.Ropt)} Ω at the intrinsic plane, carried out through the extrinsic network with the output capacitance " +
            $"C22 {G(b.C22 * 1e15)} fF linearised at the bias",
            $"Load (extrinsic): {string.Join(", ", loads.Select((z, k) => $"{(k == 0 ? "f0" : $"{k + 1}f0")} {ModelValidation.Ohms(z)}"))}" +
            (loads.Length < h ? $", above {loads.Length}f0 short" : ""),
            $"Source: {ModelValidation.Terminations(b.SourceZ, [o.Source2f0 ?? ValidationBench.Short], h)}",
            $"Loadpull disc: {grid.Count} loads — the class load and {o.Rings} ring(s) × {o.Spokes} spoke(s) out to |Γ| {G(o.GammaRadius)} about it",
            $"Drive: available power {G(o.PinStart)} … {G(o.PinMax)} dBm in {G(o.PinStep)} dB steps, each load stopped one step past {G(o.Compression)} dB " +
            "compression from its running gain maximum",
            ModelValidation.Ambient(ctx, [b.AmbientC]),
            $"Solver: {ValidationBench.HbText(h)}; one fixed drive ladder per load, no continuation or cold retries, so the region of " +
            "convergence is the solver's own",
        };
        bool presetFailed = converged[0] != 1;
        var mxp = surface.MaxPower(0, c, SurfacePlane.Gamma);
        var grade = presetFailed ? Grade.Red : ReportGrades.Banded(1 - share, 1 - ReportGrades.DiscGreen, 1 - ReportGrades.DiscYellow);
        if (grade == Grade.Green && mxp is null) grade = Grade.Yellow;
        string meaning = grade switch
        {
            Grade.Green => $"HB converged at the class load and across the disc around it: the model drives to P-{G(o.Compression)}dB reliably here.",
            Grade.Yellow when mxp is null => $"No load in the disc reached P-{G(o.Compression)}dB within the drive ladder — raise the drive to see compression.",
            Grade.Yellow => "HB failed at some loads in the disc (red crosses on the region-of-convergence chart): expect convergence trouble there in a design.",
            _ when presetFailed => "HB did not converge at the class's own load — the model is not usable for this class as set up.",
            _ => "HB failed across much of the disc: the model's convergence in this class is poor.",
        };
        var scores = new List<ScoreRow>
        {
            new(label, "HB convergence over the loadpull disc",
                $"{G(100 * share)} % of {grid.Count} loads" + (presetFailed ? ", not at the class load" : ""), grade, meaning),
        };
        // Converging is not amplifying: a class whose best load has no gain says the set-up — or the model at this bias — is
        // wrong, and the section's numbers are not a prediction a design can use.
        if (mxp?.Interpolated is { } best)
        {
            double gt = surface.MetricAtCoord(0, "Gt_dB", best, c, SurfacePlane.Gamma), pae = surface.MetricAtCoord(0, "PAE", best, c, SurfacePlane.Gamma);
            if (!(gt > 0) || !(pae > 0))
                scores.Add(new ScoreRow(label, "PA prediction at MXP", $"Gt {G(gt)} dB, PAE {G(pae)} %", Grade.Red,
                    $"The model does not amplify at this set-up (R_opt {G(b.Ropt)} Ω, Vgs {G(b.Vgs)} V, Vds {G(b.Vds)} V, {ModelValidation.GHz(b.FrequencyHz)} GHz): " +
                    "check the report's PA options and the model's current at this bias before using this class's results."));
        }
        return new ReportSection(label, ds, lines, files) { Conditions = conditions, Scores = scores };
    }

    // ── Arithmetic ────────────────────────────────────────────────────────────

    private static double Median(IEnumerable<double> values)
    {
        var v = values.Order().ToList();
        return v.Count == 0 ? double.NaN : v[v.Count / 2];
    }

    private static string G(double v) => ModelValidation.G(v);
    private static string Zs(Complex z) => $"{G(z.Real)}{(z.Imaginary < 0 ? "−" : "+")}j{G(Math.Abs(z.Imaginary))}";
}
