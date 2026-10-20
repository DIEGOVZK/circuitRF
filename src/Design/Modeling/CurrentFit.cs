// The current constituents, Id and Ig, from DC or pulsed I–V, and the optional DC/RF dispersion
// (brief-modelcomposer-7 R-mc7-1 … R-mc7-5; overview D4, D6, D13).
//
//   1. The training points are the intrinsic points of the chosen I–V sets (MC-4 R-mc4-4), excluded points removed. One
//      temperature and one quiescent point (MC-9 and MC-10 lift those); a mix is refused naming what differs.
//   2. The gate current is split into its two branches (GateSplit), and the channel current is the drain terminal's plus
//      the gate–drain branch, which leaves through the drain.
//   3. Each current is an ANN (MC-6 value training), a table (re-gridded through an ANN or an RBF, then MC-2's spline) or
//      an equation (a template or the user's own, its parameters fitted by the optimizer registry's algorithms).
//   4. The training domain's hull becomes each table or ANN's Domain, and G_x its extrapolation conductance (MC-2).
//   5. Optionally IdRF ([P1] eq. 6.52): derivative training on the intrinsic RF Gm and Gds, anchored to IdDC.
//
// The result goes to the .cmc's Steps.Current; arrays to <name>.cmc-cache/current/. Cancelled, nothing is written.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;
using CircuitRF.Engine.Modeling.Ann;
using CircuitRF.Engine.Optimization;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Modeling;

/// <summary>A fitted current step: what the document records, the arrays its tables and networks name, and the settings
/// it was fitted with.</summary>
public sealed record CurrentFitResult(CurrentFitSettings Settings, CurrentFitRecord Record,
                                      IReadOnlyDictionary<string, DataCube> Arrays);

public static class CurrentFit
{
    public const string StepName = "Current";
    public const string CacheFolder = "current";

    private static readonly IReadOnlyList<string> Inputs = [ComposedCoreProgram.Vgs, ComposedCoreProgram.Vds];

    // ── The document ──────────────────────────────────────────────────────────

    /// <summary>The step's settings in <paramref name="doc"/>, or the defaults.</summary>
    public static CurrentFitSettings Settings(CmcDocument doc) =>
        Step(doc)?["Settings"] is { } s ? CurrentFitSettings.Parse(s, $"Steps.{StepName}.Settings") : CurrentFitSettings.Default;

    /// <summary>The step's recorded result, or null when it has not been run.</summary>
    public static CurrentFitRecord? Record(CmcDocument doc) =>
        Step(doc)?["Result"] is { } r ? CurrentFitRecord.Parse(r, $"Steps.{StepName}.Result") : null;

    /// <summary>The block's keys checked and parsed — <see cref="CmcFile.Validate"/>'s call.</summary>
    public static void Validate(CmcDocument doc)
    {
        if (Step(doc) is not { } o) return;
        CmcExtrinsic.Only(o, $"Steps.{StepName}", ["Network", "Settings", "Result"]);
        Settings(doc);
        Record(doc);
    }

    /// <summary>The document with <paramref name="settings"/> as the step's settings; a recorded result is kept.</summary>
    public static CmcDocument WithSettings(CmcDocument doc, CurrentFitSettings settings)
    {
        var steps = StepsCopy(doc);
        var o = steps[StepName] as JsonObject ?? new JsonObject();
        steps[StepName] = o;
        o["Settings"] = settings.ToJson();
        return doc with { Steps = steps };
    }

    /// <summary>The document with <paramref name="result"/> recorded, through the network it was computed with (R-mc7-5).</summary>
    public static CmcDocument Apply(CmcDocument doc, CurrentFitResult result)
    {
        var steps = StepsCopy(doc);
        steps[StepName] = new JsonObject
        {
            ["Network"] = NetworkFit.Digest(CmcExtrinsic.Network(doc)),
            ["Settings"] = result.Settings.ToJson(),
            ["Result"] = result.Record.ToJson(),
        };
        return doc with { Steps = steps };
    }

    private static JsonObject? Step(CmcDocument doc) =>
        doc.Steps is JsonObject s && s[StepName] is { } n ? CmcExtrinsic.Obj(n, $"Steps.{StepName}") : null;

    private static JsonObject StepsCopy(CmcDocument doc) =>
        doc.Steps is JsonObject s ? (JsonObject)JsonNode.Parse(s.ToJsonString())! : new JsonObject();

    /// <summary>
    /// Reads the <c>.cmc</c>, fits with its settings, writes the arrays and the document — what the window and the
    /// <c>model</c> verb both call. Cancelled, nothing is written.
    /// </summary>
    public static CurrentFitResult FitFile(string cmcPath, RunControl? rc = null)
    {
        var doc = CmcFile.Read(cmcPath);
        var data = new CmcCache(cmcPath).Open(doc, rc).Data;
        var intrinsic = new IntrinsicExtraction(cmcPath).Run(doc, data, rc: rc);
        var result = Run(doc, data, intrinsic, Settings(doc), rc);
        rc?.ThrowIfCancellationRequested();
        WriteArrays(cmcPath, result);
        CmcFile.Write(cmcPath, Apply(doc, result));
        return result;
    }

    /// <summary>The recorded step and its arrays from the cache; null when there is no record, or its arrays are not in the
    /// cache (derived data: run the step again).</summary>
    public static CurrentFitResult? Load(string cmcPath, CmcDocument doc)
    {
        if (Record(doc) is not { } record) return null;
        var arrays = new Dictionary<string, DataCube>(StringComparer.Ordinal);
        if (record.Arrays.Length > 0)
        {
            string file = Path.Combine(CmcCache.FolderFor(cmcPath), CacheFolder, record.Arrays + ".npy");
            if (!File.Exists(file)) return null;
            try
            {
                var ds = DataSetImporter.Import(file).DataSet;
                foreach (var (name, cube) in ds.Cubes) arrays[name] = cube;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException)
            { return null; }
        }
        return new CurrentFitResult(Settings(doc), record, arrays);
    }

    /// <summary>The content key of a result's arrays: what the record names and the cache file is called.</summary>
    private static string ArraysKey(IReadOnlyDictionary<string, DataCube> arrays, out byte[] bytes)
    {
        var ds = new DataSet();
        foreach (var (name, cube) in arrays.OrderBy(kv => kv.Key, StringComparer.Ordinal)) ds.Add(name, cube);
        using var m = new MemoryStream();
        DataSetExporter.ExportNpy(ds, m);
        bytes = m.ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..32];
    }

    private static void WriteArrays(string cmcPath, CurrentFitResult result)
    {
        string folder = Path.Combine(CmcCache.FolderFor(cmcPath), CacheFolder);
        if (result.Record.Arrays.Length == 0) return;
        ArraysKey(result.Arrays, out var bytes);
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, result.Record.Arrays + ".npy");
        string tmp = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, target, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        foreach (string file in Directory.EnumerateFiles(folder, "*.npy"))
            if (Path.GetFileNameWithoutExtension(file) != result.Record.Arrays)
                try { File.Delete(file); } catch (IOException) { }
    }

    // ── The fit ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fits the current constituents (R-mc7-1 … R-mc7-5). <paramref name="data"/> is the open document's normalised data
    /// and <paramref name="intrinsic"/> its intrinsic data (<see cref="IntrinsicExtraction"/>). Throws
    /// <see cref="ModelingException"/> for a refusal and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static CurrentFitResult Run(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data,
                                       IntrinsicResult intrinsic, CurrentFitSettings settings, RunControl? rc = null)
    {
        rc?.ThrowIfCancellationRequested();
        var report = new List<string>();
        var iv = Gather(doc, data, intrinsic, settings, report);
        var arrays = new Dictionary<string, DataCube>(StringComparer.Ordinal);
        var gx = new Dictionary<string, double>(StringComparer.Ordinal);
        var errors = new Dictionary<string, FitErrorRecord>(StringComparer.Ordinal);

        // Id (R-mc7-2): relative weighting with a floor.
        double idMax = iv.Id.Max(Math.Abs);
        double[] idW = [.. iv.Id.Select(y => Weight(y, settings.IdFloor * idMax))];
        var idFit = FitPayloads("Id", settings.Id,
            [new("Id", iv.Id, idW, Own(0, 1))], iv, settings, report, rc);
        var idConstituent = idFit.Constituent;

        // Ig: log-magnitude weighting, each branch on its own split target.
        Constituent igConstituent;
        PayloadFit? igFit = null;
        if (iv.Ig.Length == 0)
        {
            igConstituent = Constituent.GateEquations("0", "0", Inputs);
            report.Add("no gate current in the data; Ig is zero.");
        }
        else
        {
            // A table or network fits u = asinh(Ig/I0) unweighted — an equal error in u is an equal ratio in Ig above I0;
            // an equation, whose form already spans the decades, fits Ig itself weighted by 1/max(|Ig|, I0)².
            double igMax = Math.Max(iv.Ig.Max(Math.Abs), 1e-300);
            double floor = settings.IgFloor * igMax;
            bool transformed = settings.Ig.Method != CurrentMethod.Equation;
            double[] W(double[] y) => [.. y.Select(v => transformed ? 1.0 : Weight(v, floor))];
            igFit = FitPayloads("Ig", settings.Ig,
                [new("IgGs", iv.IgGs, W(iv.IgGs), Own(1, 0), transformed ? floor : null),
                 new("IgGd", iv.IgGd, W(iv.IgGd), Own(1, -1), transformed ? floor : null)],
                iv.ForGate, settings, report, rc);
            igConstituent = igFit.Constituent;
        }

        foreach (var f in igFit is null ? [idFit] : new[] { idFit, igFit })
            foreach (var p in f.Payloads)
            {
                foreach (var (k, v) in p.Arrays) arrays[k] = v;
                if (p.Gx is { } g) gx[p.Name] = g;
                errors[p.Name] = p.Error;
            }

        // The report (R-mc7-5): the fitted Gm and Gds against the data's own DC derivatives.
        var conductance = Conductances(iv, idFit.Payloads[0].Eval);
        SymmetryRecord? symmetry = settings.Symmetry == DrainSourceSymmetry.Exchange
            ? Jumps(iv, idFit.Payloads[0].Eval, igFit?.Payloads[0].Eval, igFit?.Payloads[1].Eval)
            : null;

        // DC/RF dispersion (R-mc7-4).
        Constituent? idRf = null;
        double? eq649 = null;
        (double, double)? reference = null;
        if (settings.Dispersion is { } disp)
        {
            var rf = Dispersion(doc, intrinsic, settings, disp, iv, idFit.Payloads[0].Eval, report, rc);
            idRf = rf.Constituent;
            foreach (var (k, v) in rf.Payload.Arrays) arrays[k] = v;
            gx["IdRF"] = rf.Payload.Gx!.Value;
            errors["IdRF"] = rf.Payload.Error;
            eq649 = rf.Eq649;
            reference = rf.Reference;
        }

        string key = arrays.Count > 0 ? ArraysKey(arrays, out _) : "";
        var record = new CurrentFitRecord
        {
            Id = idConstituent, Ig = igConstituent, IdRF = idRf, Gx = gx, Tau = settings.Dispersion?.Tau, Arrays = key,
            Errors = errors, Conductance = conductance, Symmetry = symmetry, Eq649 = eq649, Reference = reference,
            Report = report,
        };
        return new CurrentFitResult(settings, record, arrays);
    }

    private static double Weight(double y, double floor)
    {
        double m = Math.Max(Math.Abs(y), floor);
        return m > 0 ? 1.0 / (m * m) : 1.0;
    }

    /// <summary>A current's own branch voltage, <c>a·vgs + b·vds</c> (MC-2's G_x term).</summary>
    private static double[] Own(double vgs, double vds) => [vgs, vds];

    // ── Training points (R-mc7-1) ─────────────────────────────────────────────

    /// <summary>The intrinsic points one fit trains on, and the domain they span.</summary>
    internal sealed class IvPoints
    {
        public required double[][] X { get; init; }
        public required double[] Id { get; init; }
        public required double[] Ig { get; init; }
        public required double[] IgGs { get; init; }
        public required double[] IgGd { get; init; }
        /// <summary>The points that carry a gate current, by index into the gate arrays — the Ig fit's own points.</summary>
        public required IvPoints ForGate { get; init; }
        public required TrainingDomain Domain { get; init; }
        public double TemperatureC { get; init; } = 25;
    }

    internal static IvPoints Gather(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data, IntrinsicResult intrinsic,
                                    CurrentFitSettings settings, List<string> report)
    {
        var chosen = settings.DataSets.Count > 0
            ? [.. settings.DataSets.Select(n => doc.DataSets.FirstOrDefault(s => s.Name == n)
                                               ?? throw new ModelingException(n, "the current step names it, and the document has no such data set."))]
            : doc.DataSets.Where(s => s.Kind is DataSetKind.DcIv or DataSetKind.PulsedIv or DataSetKind.SimulatedTables).ToList();
        if (chosen.Count == 0)
            throw new ModelingException("Current", "there is no I–V data set (DcIv, PulsedIv or SimulatedTables) to fit the currents to.");
        double t = OneCondition(chosen, data);

        var x = new List<double[]>();
        var id = new List<double>();
        var ig = new List<double?>();
        int noId = 0, reverse = 0;
        foreach (var set in chosen)
        {
            if (!intrinsic.Sets.TryGetValue(set.Name, out var ds))
                throw new ModelingException(set.Name, "it has no intrinsic data; de-embed it first.");
            double[] vgs = ds["Vgsi"].RealValues, vds = ds["Vdsi"].RealValues, igs = ds["Ig"].RealValues,
                     ids = ds["Id"].RealValues, ex = ds["Excluded"].RealValues;
            for (int k = 0; k < vgs.Length; k++)
            {
                if (ex[k] != 0 || !double.IsFinite(vgs[k]) || !double.IsFinite(vds[k])) continue;
                if (double.IsNaN(ids[k])) { noId++; continue; }
                if (settings.Symmetry == DrainSourceSymmetry.Exchange && vds[k] < 0) { reverse++; continue; }
                x.Add([vgs[k], vds[k]]);
                id.Add(ids[k]);
                ig.Add(double.IsNaN(igs[k]) ? null : igs[k]);
            }
        }
        if (noId > 0) report.Add($"{noId} point(s) without a drain current left out.");
        if (reverse > 0) report.Add($"{reverse} point(s) at vds < 0 left out: with Exchange the fit reads vds ≥ 0.");
        if (x.Count < 4) throw new ModelingException("Current", $"{x.Count} usable I–V point(s); a current fit needs at least 4.");

        // The gate split, then the channel current: the drain terminal carries Id − IgGd.
        int n = x.Count;
        var gs = new double[n];
        var gd = new double[n];
        for (int k = 0; k < n; k++)
        {
            double g = ig[k] ?? 0.0;
            bool forward = x[k][1] >= 0;
            (gs[k], gd[k]) = settings.GateSplit switch
            {
                GateSplit.GateSource => (g, 0.0),
                GateSplit.GateDrain  => (0.0, g),
                _ => (g >= 0) == forward ? (g, 0.0) : (0.0, g),
            };
        }
        double[] channel = [.. Enumerable.Range(0, n).Select(k => id[k] + gd[k])];

        var gateIdx = Enumerable.Range(0, n).Where(k => ig[k] is not null).ToArray();
        if (gateIdx.Length > 0 && gateIdx.Length < n) report.Add($"{n - gateIdx.Length} point(s) without a gate current left out of Ig.");
        var domain = Hull(x);
        var gate = new IvPoints
        {
            X = [.. gateIdx.Select(k => x[k])], Id = [.. gateIdx.Select(k => channel[k])], Ig = [.. gateIdx.Select(k => ig[k]!.Value)],
            IgGs = [.. gateIdx.Select(k => gs[k])], IgGd = [.. gateIdx.Select(k => gd[k])],
            ForGate = null!, Domain = domain, TemperatureC = t,
        };
        return new IvPoints
        {
            X = [.. x], Id = channel, Ig = gate.Ig, IgGs = gate.IgGs, IgGd = gate.IgGd, ForGate = gate,
            Domain = domain, TemperatureC = t,
        };
    }

    /// <summary>
    /// One temperature and one quiescent point across the chosen sets, or a refusal naming the condition that differs
    /// (MC-9 and MC-10 handle the mixes). Returns the temperature, °C (25 when none is stated).
    /// </summary>
    private static double OneCondition(IReadOnlyList<CmcDataSet> sets, IReadOnlyDictionary<string, NormalisedData> data)
    {
        var temps = new List<(string Set, double T)>();
        foreach (var s in sets)
        {
            if (s.Conditions.AmbientC is { } a) temps.Add((s.Name, a));
            if (data.TryGetValue(s.Name, out var d))
                foreach (var p in d.Points) if (!p.Excluded && double.IsFinite(p.AmbientC)) temps.Add((s.Name, p.AmbientC));
        }
        foreach (var other in temps)
            if (Math.Abs(other.T - temps[0].T) > 0.01)
                throw new ModelingException("Current",
                    $"the data differs in AmbientC ({G(temps[0].T)} °C in {temps[0].Set}, {G(other.T)} °C in {other.Set}); " +
                    "one temperature per fit until temperature is an input.");

        var pulsed = sets.Where(s => s.Kind is DataSetKind.PulsedIv or DataSetKind.PulsedSParams).ToList();
        if (pulsed.Count > 0 && pulsed.Count < sets.Count)
            throw new ModelingException("Current",
                $"the data differs in Quiescent ({sets.First(s => !pulsed.Contains(s)).Name} is not pulsed, {pulsed[0].Name} is); " +
                "one trap state per fit until traps are modelled.");
        for (int k = 1; k < pulsed.Count; k++)
        {
            var a = pulsed[0].Conditions; var b = pulsed[k].Conditions;
            bool same = (a.Quiescent, b.Quiescent) switch
            {
                (null, null) => true,
                ({ } qa, { } qb) => Math.Abs(qa.Vgs - qb.Vgs) <= 1e-3 && Math.Abs(qa.Vds - qb.Vds) <= 1e-3,
                _ => false,
            };
            if (!same)
                throw new ModelingException("Current",
                    $"the data differs in Quiescent ({pulsed[0].Name} and {pulsed[k].Name}); one quiescent point per fit until traps are modelled.");
            if ((a.PrePulse is null) != (b.PrePulse is null)
                || a.PrePulse is { } pa && b.PrePulse is { } pb && (Math.Abs(pa.Vgs - pb.Vgs) > 1e-3 || Math.Abs(pa.Vds - pb.Vds) > 1e-3))
                throw new ModelingException("Current",
                    $"the data differs in PrePulse ({pulsed[0].Name} and {pulsed[k].Name}); one trap state per fit until traps are modelled.");
        }
        return temps.Count > 0 ? temps[0].T : 25.0;
    }

    /// <summary>The convex hull of the points, as a training domain (R-mc7-3); refused when it encloses no area.</summary>
    internal static TrainingDomain Hull(IReadOnlyList<double[]> x)
    {
        var hull = DataHygiene.Coverage([new NormalisedData { Points = [.. x.Select((p, k) => new BiasPoint($"{k}", p[0], p[1], 0, 0))] }]);
        double area = 0;
        for (int k = 0; k < hull.Count; k++)
            area += hull[k].Vgs * hull[(k + 1) % hull.Count].Vds - hull[(k + 1) % hull.Count].Vgs * hull[k].Vds;
        double gSpan = x.Max(p => p[0]) - x.Min(p => p[0]), dSpan = x.Max(p => p[1]) - x.Min(p => p[1]);
        if (hull.Count < 3 || !(Math.Abs(area) > 1e-9 * gSpan * dSpan) || gSpan <= 0 || dSpan <= 0)
            throw new ModelingException("Current", "the points span no area in (vgs, vds); a fit needs both voltages to vary.");
        return new TrainingDomain(hull, []);
    }

    // ── The methods (R-mc7-2) ─────────────────────────────────────────────────

    /// <summary>One payload's targets: a name (Id, IgGs, IgGd), a target and a weight per point, its own branch, and the
    /// scale of an <see cref="OutputTransform.Asinh"/> output when the payload is fitted in <c>u = asinh(Y/scale)</c>.</summary>
    private sealed record Target(string Name, double[] Y, double[] W, double[] Own, double? Transform = null)
    {
        public double[] U => Transform is { } s ? [.. Y.Select(y => Math.Asinh(y / s))] : Y;

        public RuntimeEvaluator Output(RuntimeEvaluator u) => Transform is { } s ? new AsinhOutput(u, s) : u;
    }

    /// <summary>A fitted payload: its function (value and gradient, extrapolated as the runtime will), arrays, G_x, error.</summary>
    private sealed record Payload(string Name, Func<double, double, (double F, double Dg, double Dd)> Eval,
                                  IReadOnlyList<KeyValuePair<string, DataCube>> Arrays, double? Gx, FitErrorRecord Error);

    private sealed record PayloadFit(Constituent Constituent, IReadOnlyList<Payload> Payloads);

    private static PayloadFit FitPayloads(string constituent, CurrentConstituentSettings s, IReadOnlyList<Target> targets,
                                          IvPoints iv, CurrentFitSettings settings, List<string> report, RunControl? rc)
    {
        if (s.Method == CurrentMethod.Equation) return FitEquation(constituent, s, targets, iv, settings, report, rc);

        var payloads = new List<Payload>();
        foreach (var t in targets)
        {
            rc?.ThrowIfCancellationRequested();
            payloads.Add(s.Method == CurrentMethod.ANN ? FitAnn(t, s, iv, settings, report, rc) : FitTable(t, s, iv, settings, report, rc));
        }
        string method = s.Method == CurrentMethod.ANN ? Constituent.AnnMethod : Constituent.TableMethod;
        Constituent c = targets.Count == 1
            ? s.Method == CurrentMethod.ANN ? Constituent.Ann(targets[0].Name, Inputs, iv.Domain, s.Activation)
                                            : Constituent.Table(targets[0].Name, Inputs, iv.Domain, s.Smoothing)
            : Constituent.Gate(method, targets[0].Name, targets[1].Name, Inputs, iv.Domain) with
              {
                  Smoothing = s.Method == CurrentMethod.Table ? s.Smoothing : null,
                  Activation = s.Method == CurrentMethod.ANN ? s.Activation : null,
              };
        if (targets[0].Transform is { } scale) c = c with { Transform = new OutputTransform(OutputTransform.Asinh, scale) };
        return new PayloadFit(c, payloads);
    }

    private static AnnTrainingSettings AnnSettings(IReadOnlyList<int> hidden, AnnActivation activation, int iterations,
                                                   CurrentFitSettings s, double heldOut) =>
        new() { Hidden = hidden, Activation = activation, MaxIterations = iterations, Restarts = s.Restarts, Seed = s.Seed,
                HeldOutFraction = heldOut };

    private static Payload FitAnn(Target t, CurrentConstituentSettings s, IvPoints iv, CurrentFitSettings settings,
                                  List<string> report, RunControl? rc)
    {
        var (held, trainX, trainU, trainW) = Training(t, iv, settings);
        rc?.BeginStage($"Training {t.Name}");
        var trained = AnnTrainer.Train(new AnnTrainingProblem(2) { Values = new AnnValueData(trainX, trainU, trainW) },
                                       AnnSettings(s.Hidden, s.Activation, s.MaxIterations, settings, 0.0), rc);
        foreach (string w in trained.Warnings) report.Add($"{t.Name}: {w}");
        var f = t.Output(trained.Network);
        double gx = Gx(f, iv.X, t.Own);
        var eval = Guided(f, iv.Domain, gx, t.Own);
        return new Payload(t.Name, eval, [.. trained.Arrays(t.Name)], gx, Error(eval, iv.X, t.Y, held));
    }

    /// <summary>The held-out split, and the training points' coordinates, targets (in the payload's own domain) and weights.
    /// Every method holds out the same points, so the errors compare.</summary>
    private static (bool[] Held, double[][] X, double[] U, double[] W) Training(Target t, IvPoints iv, CurrentFitSettings settings)
    {
        var held = Split(iv.X, t.Y, settings);
        var u = t.U;
        return (held, [.. iv.X.Where((_, k) => !held[k])], [.. u.Where((_, k) => !held[k])], [.. t.W.Where((_, k) => !held[k])]);
    }

    private static Payload FitTable(Target t, CurrentConstituentSettings s, IvPoints iv, CurrentFitSettings settings,
                                    List<string> report, RunControl? rc)
    {
        var (held, trainX, trainU, trainW) = Training(t, iv, settings);

        // The function the grid is sampled from, in the payload's own domain: a network trained on the training points
        // (held-out ones never seen), or a radial-basis interpolant through the points. The grid covers the hull's
        // bounding box, so some sites lie outside the hull: there the network is continued LINEARLY (C¹, no G_x term).
        // The G_x term grows with the square of the distance — amps a few volts out — and a spline through it rings
        // back into the cells inside the hull (measured: 7.7 % held-out error, against 1.5 % for the network itself);
        // the runtime adds G_x outside the domain on its own.
        Func<double, double, double> source;
        if (s.Regrid == RegridSource.ANN)
        {
            rc?.BeginStage($"Training {t.Name} for its table");
            var trained = AnnTrainer.Train(new AnnTrainingProblem(2) { Values = new AnnValueData(trainX, trainU, trainW) },
                                           AnnSettings(s.Hidden, s.Activation, s.MaxIterations, settings, 0.0), rc);
            var linear = new GuidedExtrapolation(trained.Network, iv.Domain, Inputs, 0.0, null);
            source = (a, b) =>
            {
                Span<double> p = [a, b];
                Span<double> g = stackalloc double[2];
                return linear.Evaluate(p, g);
            };
        }
        else
            source = Regrid.Rbf([.. trainX.Select(p => (p[0], p[1]))], trainU);

        rc?.BeginStage($"Re-gridding {t.Name}");
        var poly = iv.Domain.Polygon;
        double[] vgsSites = Regrid.Axis(poly.Min(p => p.Vgs), poly.Max(p => p.Vgs), s.GridStep);
        double[] vdsSites = Regrid.Axis(poly.Min(p => p.Vds), poly.Max(p => p.Vds), s.GridStep);
        double[] values = Regrid.Through(source, vgsSites, vdsSites);
        IReadOnlyList<double[]> sites = [vgsSites, vdsSites];
        var coefficients = TensorSplineFit.Fit(sites, values, s.Smoothing);
        var arrays = ComposedArrays.Table(t.Name, Inputs, sites, values, coefficients).ToList();

        if (s.GridStep / 2 > settings.SmallestSwing)
            report.Add($"{t.Name}: table step {G(s.GridStep)} V: low-level IM below ~{G(s.GridStep / 2)} V swing unreliable.");
        var f = t.Output(new TableEvaluator(sites, coefficients));
        double gx = Gx(f, iv.X, t.Own);
        var eval = Guided(f, iv.Domain, gx, t.Own);
        return new Payload(t.Name, eval, arrays, gx, Error(eval, iv.X, t.Y, held));
    }

    private static PayloadFit FitEquation(string constituent, CurrentConstituentSettings s, IReadOnlyList<Target> targets,
                                          IvPoints iv, CurrentFitSettings settings, List<string> report, RunControl? rc)
    {
        bool gate = targets.Count == 2;
        string[] expressions;
        IReadOnlyList<TemplateParameter> parameters;
        if (s.Template is { } name)
        {
            var tpl = CurrentTemplates.Find(name);
            if (tpl is null || tpl.IsGate != gate)
                throw new ModelingException(constituent,
                    $"'{name}' is not a template here; the templates are {string.Join(", ", CurrentTemplates.Names(gate))}.");
            expressions = gate ? [tpl.IgGs!, tpl.IgGd!] : [tpl.Id!];
            parameters = tpl.Parameters(new TemplateData([.. iv.X.Select(p => p[0])], [.. iv.X.Select(p => p[1])],
                                                         gate ? iv.Ig : iv.Id, iv.TemperatureC));
            if (gate)    // the two branches share one list, so each branch's own parameters are named for it
            {
                expressions = [Rename(expressions[0], "Gs", parameters), Rename(expressions[1], "Gd", parameters)];
                parameters = [.. parameters.Where(p => !p.Lock).SelectMany(p => new[] { p with { Name = p.Name + "Gs" }, p with { Name = p.Name + "Gd" } }),
                              .. parameters.Where(p => p.Lock)];
            }
        }
        else
        {
            expressions = gate
                ? [s.IgGs ?? throw new ModelingException(constituent, "an Ig equation names a template or both IgGs and IgGd."),
                   s.IgGd ?? throw new ModelingException(constituent, "an Ig equation names a template or both IgGs and IgGd.")]
                : [s.Expression ?? throw new ModelingException(constituent, "an Id equation names a template or an Expression.")];
            parameters = [];
        }
        parameters = Overrides(constituent, parameters, s.Parameters);

        var held = Split(iv.X, targets[0].Y, settings);
        var terms = targets.Select((t, i) => new EquationTerm(expressions[i],
            [.. iv.X.Where((_, k) => !held[k])], [.. t.Y.Where((_, k) => !held[k])], [.. t.W.Where((_, k) => !held[k])])).ToList();
        rc?.BeginStage($"Fitting {constituent}", 0, "evaluation(s)");
        var fit = EquationFit.Fit(terms, parameters, s.Algorithm, settings.Seed, constituent, rc);

        var payloads = targets.Select((t, i) =>
        {
            var f = EquationFit.Function(expressions[i], fit.Values, constituent);
            return new Payload(t.Name, f, [], null, Error(f, iv.X, t.Y, held));
        }).ToList();
        var pairs = fit.Values.Select(kv => (kv.Key, kv.Value)).ToArray();
        var c = gate ? Constituent.GateEquations(expressions[0], expressions[1], Inputs, pairs)
                     : Constituent.Equation(expressions[0], Inputs, pairs);
        return new PayloadFit(c, payloads);
    }

    /// <summary>A template's free parameters renamed with a branch suffix in its expression.</summary>
    private static string Rename(string expression, string suffix, IReadOnlyList<TemplateParameter> parameters)
    {
        foreach (var p in parameters.Where(p => !p.Lock))
            expression = System.Text.RegularExpressions.Regex.Replace(expression, $@"\b{p.Name}\b", p.Name + suffix);
        return expression;
    }

    /// <summary>The document's per-parameter settings over the template's: a start, bounds, a lock. A parameter the template
    /// does not have is the user's own and must state all three numbers.</summary>
    private static IReadOnlyList<TemplateParameter> Overrides(string constituent, IReadOnlyList<TemplateParameter> template,
                                                              IReadOnlyDictionary<string, EquationParameterSettings> overrides)
    {
        var list = template.ToList();
        foreach (var (name, o) in overrides)
        {
            int i = list.FindIndex(p => p.Name == name);
            if (i < 0)
            {
                if (o.Value is not { } v || (!o.Lock && (o.Min is null || o.Max is null)))
                    throw new ModelingException(constituent, $"parameter {name} needs a Value, and a Min and Max unless locked.");
                list.Add(new TemplateParameter(name, v, o.Min ?? v, o.Max ?? v, o.Lock));
                continue;
            }
            var p = list[i];
            list[i] = new TemplateParameter(name, o.Value ?? p.Start, o.Min ?? p.Min, o.Max ?? p.Max, o.Lock || p.Lock);
        }
        foreach (var p in list)
            if (!p.Lock && !(p.Min < p.Max && p.Start >= p.Min && p.Start <= p.Max))
                throw new ModelingException(constituent, $"parameter {p.Name} starts at {G(p.Start)}, outside [{G(p.Min)}, {G(p.Max)}].");
        return list;
    }

    /// <summary>The held-out points of a table or equation fit, by <see cref="HeldOutSplit"/> — spread over the domain.</summary>
    private static bool[] Split(double[][] x, double[] y, CurrentFitSettings s)
    {
        double gLo = x.Min(p => p[0]), gHi = x.Max(p => p[0]), dLo = x.Min(p => p[1]), dHi = x.Max(p => p[1]);
        double[][] hat = [.. x.Select(p => new[] { Norm(p[0], gLo, gHi), Norm(p[1], dLo, dHi) })];
        return HeldOutSplit.Select(hat, [.. y.Select(v => new[] { v })], s.HeldOutFraction, SplitMix64.Mix(s.Seed));
    }

    private static double Norm(double v, double lo, double hi) => hi > lo ? 2 * (v - lo) / (hi - lo) - 1 : 0;

    private static FitErrorRecord Error(Func<double, double, (double F, double Dg, double Dd)> f, double[][] x, double[] y, bool[] held)
    {
        var train = new List<double>(); var hold = new List<double>();
        for (int k = 0; k < x.Length; k++) (held[k] ? hold : train).Add(f(x[k][0], x[k][1]).F - y[k]);
        static double Rms(List<double> r) => r.Count == 0 ? 0 : Math.Sqrt(r.Sum(e => e * e) / r.Count);
        double range = y.Max() - y.Min();
        double scale = range > 0 ? range : 1;
        return new FitErrorRecord(train.Count, Rms(train), Rms(train) / scale, hold.Count, Rms(hold), Rms(hold) / scale);
    }

    /// <summary>MC-2's default G_x: ten times the largest own-branch conductance over the training points, never zero.</summary>
    private static double Gx(RuntimeEvaluator f, IEnumerable<double[]> x, double[] own)
    {
        Span<double> g = stackalloc double[2];
        double largest = 0;
        foreach (var p in x)
        {
            f.Evaluate(p, g);
            largest = Math.Max(largest, Math.Abs(g[0] * own[0] + g[1] * own[1]));
        }
        return GuidedExtrapolation.DefaultGx(Math.Max(largest, 1e-12));
    }

    private static Func<double, double, (double F, double Dg, double Dd)> Guided(RuntimeEvaluator f, TrainingDomain domain,
                                                                                 double gx, double[] own)
    {
        var guided = new GuidedExtrapolation(f, domain, Inputs, gx, own);
        return (vgs, vds) =>
        {
            Span<double> p = [vgs, vds];
            Span<double> g = stackalloc double[2];
            double v = guided.Evaluate(p, g);
            return (v, g[0], g[1]);
        };
    }

    // ── The report (R-mc7-3, R-mc7-5) ─────────────────────────────────────────

    /// <summary>The fitted Id's Gm and Gds against the data's DC derivatives at every point.</summary>
    private static ConductanceRecord? Conductances(IvPoints iv, Func<double, double, (double F, double Dg, double Dd)> f)
    {
        if (iv.X.Length < 7) return null;
        var data = LocalGradients.Of([.. iv.X.Select(p => (p[0], p[1]))], iv.Id);
        double sgm = 0, sgds = 0, rgm = 0, rgds = 0;
        int n = 0;
        for (int k = 0; k < iv.X.Length; k++)
        {
            var (dg, dd) = data[k];
            if (!double.IsFinite(dg) || !double.IsFinite(dd)) continue;
            var m = f(iv.X[k][0], iv.X[k][1]);
            sgm += (m.Dg - dg) * (m.Dg - dg); sgds += (m.Dd - dd) * (m.Dd - dd);
            rgm += dg * dg; rgds += dd * dd;
            n++;
        }
        if (n == 0) return null;
        double gm = Math.Sqrt(sgm / n), gds = Math.Sqrt(sgds / n);
        return new ConductanceRecord(gm, rgm > 0 ? gm / Math.Sqrt(rgm / n) : 0, gds, rgds > 0 ? gds / Math.Sqrt(rgds / n) : 0);
    }

    /// <summary>With Exchange, the jumps at vds = 0 over the domain's vgs: Id's value and vds-slope, and the gate–source branch.</summary>
    private static SymmetryRecord Jumps(IvPoints iv, Func<double, double, (double F, double Dg, double Dd)> id,
                                        Func<double, double, (double F, double Dg, double Dd)>? igGs,
                                        Func<double, double, (double F, double Dg, double Dd)>? igGd)
    {
        double lo = iv.Domain.Polygon.Min(p => p.Vgs), hi = iv.Domain.Polygon.Max(p => p.Vgs);
        double value = 0, slope = 0, gate = 0;
        for (int k = 0; k <= 100; k++)
        {
            double vgs = lo + (hi - lo) * k / 100.0;
            var m = id(vgs, 0);
            value = Math.Max(value, Math.Abs(2 * m.F));
            slope = Math.Max(slope, Math.Abs(m.Dg));
            if (igGs is not null && igGd is not null) gate = Math.Max(gate, Math.Abs(igGs(vgs, 0).F - igGd(vgs, 0).F));
        }
        return new SymmetryRecord(value, slope, gate);
    }

    // ── DC/RF dispersion (R-mc7-4) ────────────────────────────────────────────

    private sealed record DispersionFit(Constituent Constituent, Payload Payload, double? Eq649, (double, double) Reference);

    private static DispersionFit Dispersion(CmcDocument doc, IntrinsicResult intrinsic, CurrentFitSettings settings,
                                            DispersionFitSettings disp, IvPoints iv,
                                            Func<double, double, (double F, double Dg, double Dd)> idDc,
                                            List<string> report, RunControl? rc)
    {
        var sets = disp.DataSets.Count > 0
            ? [.. disp.DataSets.Select(n => doc.DataSets.FirstOrDefault(s => s.Name == n)
                                           ?? throw new ModelingException(n, "the dispersion names it, and the document has no such data set."))]
            : doc.DataSets.Where(s => s.Kind is DataSetKind.SParams or DataSetKind.PulsedSParams).ToList();

        var x = new List<double[]>();
        var targets = new List<double[]>();
        foreach (var set in sets)
        {
            if (!intrinsic.Sets.TryGetValue(set.Name, out var ds) || !ds.Contains("Gm"))
                throw new ModelingException(set.Name, "the dispersion reads its RF Gm and Gds, and it has no intrinsic S data.");
            double[] f = ds["Gm"].Axes[1].Values, band = ds["Band"].RealValues;
            double[] gm = ds["Gm"].RealValues, gds = ds["Gds"].RealValues, flagged = ds["Flagged"].RealValues;
            double[] vgs = ds["Vgsi"].RealValues, vds = ds["Vdsi"].RealValues, ex = ds["Excluded"].RealValues;
            int nf = f.Length;
            for (int p = 0; p < vgs.Length; p++)
            {
                if (ex[p] != 0) continue;
                if (settings.Symmetry == DrainSourceSymmetry.Exchange && vds[p] < 0) continue;
                double sg = 0, sd = 0; int c = 0;
                for (int k = 0; k < nf; k++)
                {
                    if (f[k] < band[0] || f[k] > band[1] || flagged[p * nf + k] != 0) continue;
                    sg += gm[p * nf + k]; sd += gds[p * nf + k]; c++;
                }
                if (c == 0) continue;
                x.Add([vgs[p], vds[p]]);
                targets.Add([sg / c, sd / c]);
            }
        }
        if (x.Count < 4)
            throw new ModelingException("Dispersion", $"{x.Count} S-parameter point(s) with RF conductances; IdRF needs at least 4.");

        var domain = Hull(x);
        (double Vgs, double Vds) reference = disp.Reference ?? Middle(x);
        double anchor = idDc(reference.Vgs, reference.Vds).F;

        rc?.BeginStage("Training IdRF");
        var trained = AnnTrainer.Train(new AnnTrainingProblem(2)
        {
            Derivatives = new AnnDerivativeData(x, [0, 1], targets),
            Anchor = new AnnAnchor([reference.Vgs, reference.Vds], anchor),
        }, AnnSettings(disp.Hidden, AnnActivation.Tanh, CurrentConstituentSettings.DefaultMaxIterations, settings,
                       settings.HeldOutFraction), rc);
        foreach (string w in trained.Warnings) report.Add($"IdRF: {w}");
        double gx = Gx(trained.Network, x, Own(0, 1));
        var e = trained.Derivatives!;
        var payload = new Payload("IdRF", Guided(trained.Network, domain, gx, Own(0, 1)), [.. trained.Arrays("IdRF")], gx,
                                  new FitErrorRecord(e.TrainCount, e.TrainRms, e.TrainRelative, e.HeldOutCount, e.HeldOutRms, e.HeldOutRelative));

        // [P1] eq. 6.49: RF conductances come from one current only if ∂Gm/∂vds = ∂Gds/∂vgs. Differencing measured
        // conductances to test that directly is too noisy on any realistic grid (it reported 86 % on exactly
        // conservative data), so the residual is the part of the RF conductances the least-squares current IdRF cannot
        // carry — zero when 6.49 holds, and what [P1] §6.4.2 says the adjoint fit trades off: RMS |∇IdRF − (Gm, Gds)|
        // relative to RMS |(Gm, Gds)| over every S point.
        double miss = 0, size = 0;
        Span<double> grad = stackalloc double[2];
        for (int k = 0; k < x.Count; k++)
        {
            trained.Network.Evaluate(x[k], grad);
            miss += Math.Pow(grad[0] - targets[k][0], 2) + Math.Pow(grad[1] - targets[k][1], 2);
            size += targets[k][0] * targets[k][0] + targets[k][1] * targets[k][1];
        }
        double? eq649 = size > 0 ? Math.Sqrt(miss / size) : null;
        if (eq649 is { } r649) report.Add($"IdRF: eq. 6.49 residual {G(100 * r649)} %.");
        return new DispersionFit(Constituent.Ann("IdRF", Inputs, domain), payload, eq649, reference);
    }

    /// <summary>The point nearest the middle of the points' span.</summary>
    private static (double, double) Middle(List<double[]> x)
    {
        double gLo = x.Min(p => p[0]), gHi = x.Max(p => p[0]), dLo = x.Min(p => p[1]), dHi = x.Max(p => p[1]);
        var best = x.MinBy(p => Math.Pow(Norm(p[0], gLo, gHi), 2) + Math.Pow(Norm(p[1], dLo, dHi), 2))!;
        return (best[0], best[1]);
    }

    private static string G(double v) => v.ToString("G3", CultureInfo.InvariantCulture);
}
