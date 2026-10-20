// Traps (brief-modelcomposer-10 R-mc10-1 … R-mc10-6; overview D4, D10, D13).
//
//   1. The model is a setting: None (the default), CaptureEmission or Custom, or any model a developer registers — one
//      contract (src/Core/Devices/Composed/Traps). The settings become the .cmodel's Traps block, checked by the same
//      registry the device compiles it with, so a block the step accepts is one a ComposedFET loads.
//   2. The data's trap states (R-mc10-4). A pulsed set's trap-setting bias is its PrePulse level when it has one (double
//      pulse) and its quiescent point otherwise, both re-referenced to intrinsic like any point (IntrinsicExtraction's
//      Setting cube). Per enabled state, a pulsed point is TOWARD the setting when its own DC state lies on the setting's
//      side in the direction capture moves (pulsed no lower in vgs for gate-lag, no higher in vds for drain-lag): no
//      capture acted, only slow emission, so its state is the setting's exactly. Otherwise it is BEYOND, and the set's
//      TrapState decides — auto: validation only; setting: the setting's state; extreme: the pulse's ([X10] eqs. 1.8–1.9).
//      DC points sit at their DC state.
//   3. What the data allows: per state, the distinct trap-setting levels among the points used — two or more fit fully,
//      one fits with one warning (the level is recorded in the model), none refuses the state, naming what it needs.
//
// The current and charge steps call Map for their own sets; this step records the levels and what was left out. Its
// FitFile re-trains the currents with the trap states as inputs. Cancelled, nothing is written.
//
// Under the Shift coupling (brief-modelcomposer-19) none of that applies: Id is the trap-free current the current step fitted
// from DC, the core shifts its gate voltage by the states, and this step fits the shift's strengths from the DC/RF
// difference in gm and gds that multi-bias S shows (TrapFit.Shift.cs). No pulsed data is needed.

using System.Globalization;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Design.Modeling;

/// <summary>A run of the trap step: what the document records, and the settings it ran with.</summary>
public sealed record TrapFitResult(TrapFitSettings Settings, TrapFitRecord Record);

/// <summary>
/// The trap states of one fit's points (R-mc10-4): the block they were mapped under (its levels filled in), its compiled
/// dynamics, and per data set, per point, the enabled states' values — null for a point left as validation only.
/// </summary>
public sealed class TrapMapping
{
    public required ModelTraps Block { get; init; }
    public required TrapDynamics Dynamics { get; init; }
    public IReadOnlyList<string> States => Block.States;
    public required IReadOnlyDictionary<string, double[]?[]> Phi { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<double>> Levels { get; init; }
    /// <summary>Per state, the pulsed points beyond its trap-setting bias left as validation (TrapState auto).</summary>
    public required IReadOnlyDictionary<string, int> Beyond { get; init; }
    public required IReadOnlyList<string> Report { get; init; }

    /// <summary>A point's states, or null when it is validation only. A set the mapping does not hold is at DC.</summary>
    public double[]? At(string set, int point) => Phi.TryGetValue(set, out var p) ? p[point] : null;
}

public static partial class TrapFit
{
    public const string StepName = "Traps";

    /// <summary>The only text an unavailable trap step shows.</summary>
    public const string NoPulsedData = "no pulsed I–V";

    /// <summary>A pulse, sample delay or pre-pulse gap longer than this fraction of τ_emit lets emission move the state
    /// during the measurement.</summary>
    public const double EmissionFraction = 0.1;

    /// <summary>Two trap-setting levels within this are one, volts.</summary>
    public const double LevelTolerance = 1e-3;

    // ── The document ──────────────────────────────────────────────────────────

    public static TrapFitSettings Settings(CmcDocument doc) =>
        Step(doc)?["Settings"] is { } s ? TrapFitSettings.Parse(s, $"Steps.{StepName}.Settings") : TrapFitSettings.Default;

    public static TrapFitRecord? Record(CmcDocument doc) =>
        Step(doc)?["Result"] is { } r ? TrapFitRecord.Parse(r, $"Steps.{StepName}.Result") : null;

    /// <summary>The block's keys checked and parsed — <see cref="CmcFile.Validate"/>'s call.</summary>
    public static void Validate(CmcDocument doc)
    {
        if (Step(doc) is not { } o) return;
        CmcExtrinsic.Only(o, $"Steps.{StepName}", ["Network", "Settings", "Result", CmcStamps.DataKey]);
        Settings(doc);
        Record(doc);
    }

    public static CmcDocument WithSettings(CmcDocument doc, TrapFitSettings settings)
    {
        var steps = StepsCopy(doc);
        var o = steps[StepName] as JsonObject ?? new JsonObject();
        steps[StepName] = o;
        o["Settings"] = settings.ToJson();
        return doc with { Steps = steps };
    }

    public static CmcDocument Apply(CmcDocument doc, TrapFitResult result)
    {
        var steps = StepsCopy(doc);
        steps[StepName] = new JsonObject { ["Settings"] = result.Settings.ToJson(), ["Result"] = result.Record.ToJson() };
        return doc with { Steps = steps };
    }

    private static JsonObject? Step(CmcDocument doc) =>
        doc.Steps is JsonObject s && s[StepName] is { } n ? CmcExtrinsic.Obj(n, $"Steps.{StepName}") : null;

    private static JsonObject StepsCopy(CmcDocument doc) =>
        doc.Steps is JsonObject s ? (JsonObject)JsonNode.Parse(s.ToJsonString())! : new JsonObject();

    /// <summary>Whether the document models traps.</summary>
    public static bool Modelled(CmcDocument doc) => Settings(doc).Modelled;

    /// <summary>Whether <paramref name="doc"/> holds pulsed I–V, which a <see cref="ModelTraps.Trained"/> coupling trains on.</summary>
    public static bool HasPulsedIv(CmcDocument doc) => doc.DataSets.Any(s => s.Kind == DataSetKind.PulsedIv);

    /// <summary>Why the step cannot run for this document, or null: a trained coupling is characterised by pulsed I–V ([P1]
    /// §6.6.4); a shift coupling needs none (R-mc19-2).</summary>
    public static string? Unavailable(CmcDocument doc) =>
        Settings(doc).Shifted || HasPulsedIv(doc) ? null : NoPulsedData;

    /// <summary>The one line the step shows when there is no pulsed I–V, so only the shift coupling is offered (R-mc19-2).</summary>
    public const string ShiftOnly = "Shift only: trained traps need pulsed I–V; without it the states shift the threshold, with strengths typed or fitted from multi-bias S.";

    /// <summary>
    /// Reads the <c>.cmc</c>, runs the step, then re-trains the currents with the trap states as inputs (R-mc10-5, R-mc10-6)
    /// and writes both — what the window and the <c>model</c> verb call. Cancelled, nothing is written and the previous
    /// result is kept (D13).
    /// </summary>
    public static (TrapFitResult Traps, CurrentFitResult? Current) FitFile(string cmcPath, RunControl? rc = null)
    {
        var doc = CmcFile.Read(cmcPath);
        var data = new CmcCache(cmcPath).Open(doc, rc).Data;
        var intrinsic = new IntrinsicExtraction(cmcPath).Run(doc, data, rc: rc);
        if (Settings(doc).Shifted)
        {
            // R-mc19-5: the strengths alone; Id₀ is the current step's, left as it is.
            var shifted = Run(doc, data, intrinsic, Settings(doc), rc, CurrentFit.Load(cmcPath, doc));
            rc?.ThrowIfCancellationRequested();
            CmcFile.Write(cmcPath, CmcStamps.Stamp(Apply(doc, shifted), StepName, CmcStamps.DataDigest(cmcPath, doc)));
            return (shifted, null);
        }
        var traps = Run(doc, data, intrinsic, Settings(doc), rc);
        doc = Apply(doc, traps);
        var current = CurrentFit.Run(doc, data, intrinsic, CurrentFit.Settings(doc), rc);
        rc?.ThrowIfCancellationRequested();
        CurrentFit.WriteArrays(cmcPath, CurrentFit.CacheFolder, current.Record.Arrays, current.Arrays);
        string digest = CmcStamps.DataDigest(cmcPath, doc);
        CmcFile.Write(cmcPath, CmcStamps.Stamp(CmcStamps.Stamp(CurrentFit.Apply(doc, current), StepName, digest),
                                               CurrentFit.StepName, digest));
        return (traps, current);
    }

    // ── The block (R-mc10-1 … R-mc10-3) ───────────────────────────────────────

    /// <summary>A state's name in a sentence: gate-lag and drain-lag for <c>CaptureEmission</c>, the state itself otherwise.</summary>
    public static string Label(string model, string state) =>
        model == CaptureEmissionTraps.ModelName
            ? state == CaptureEmissionTraps.GateLag ? "gate-lag" : "drain-lag"
            : state;

    /// <summary>
    /// The <c>.cmodel</c> block the settings describe, with <paramref name="levels"/> when given, checked by the registry —
    /// so the step refuses exactly what a <c>ComposedFET</c> would. Every declared parameter of an enabled state is written
    /// with its value, so the model states what it was built with.
    /// </summary>
    public static ModelTraps Block(TrapFitSettings s, IReadOnlyDictionary<string, IReadOnlyList<double>>? levels = null,
                                   IReadOnlyDictionary<string, double>? strengths = null)
    {
        if (!s.Modelled) throw new ModelingException(StepName, "no trap model is chosen.");
        var model = TrapModels.Find(s.Model)!;
        var parameters = new List<KeyValuePair<string, double>>();
        var expressions = new List<KeyValuePair<string, string>>();
        IReadOnlyList<string> states;
        // R-mc19-1: a strength is typed in the settings or fitted; one neither (a block checked before the fit) reads 0.
        var shift = model.States.Select(model.ShiftParameter).OfType<string>().ToList();
        if (model is CaptureEmissionTraps)
        {
            states = s.States.Count > 0 ? s.States : [CaptureEmissionTraps.DrainLag];
            if (s.Expressions.Count > 0) throw new ModelingException(StepName, "only a Custom model is written as expressions.");
            bool thermal = s.Emission == TrapEmission.ThermallyActivated;
            foreach (var (name, _) in s.Parameters)
                if (shift.Contains(name))
                {
                    if (!s.Shifted) throw new ModelingException(StepName, $"{name} is a strength of the {ModelTraps.Shift} coupling; the coupling is {s.Coupling}.");
                }
                else if (!model.Parameters.Any(p => p.Name == name))
                    throw new ModelingException(StepName,
                        $"{name} is not a parameter of {model.Name}; they are {string.Join(", ", model.Parameters.Select(p => p.Name))}.");
                else if (name.StartsWith("Ea", StringComparison.Ordinal) && !thermal)
                    throw new ModelingException(StepName, $"{name} is the activation energy of ThermallyActivated emission; the emission is Constant.");
            foreach (var p in model.Parameters)
            {
                string? own = p.Name.EndsWith('1') ? CaptureEmissionTraps.GateLag : p.Name.EndsWith('2') ? CaptureEmissionTraps.DrainLag : null;
                if (own is not null && !states.Contains(own)) continue;
                if (p.Name.StartsWith("Ea", StringComparison.Ordinal))
                {
                    if (!thermal) continue;
                    if (!s.Parameters.TryGetValue(p.Name, out double ea))
                        throw new ModelingException(StepName, $"ThermallyActivated emission needs {p.Name}, the {Label(model.Name, own!)} activation energy in eV.");
                    parameters.Add(new(p.Name, ea));
                    continue;
                }
                if (p.Name == "Tref" && !thermal) continue;
                parameters.Add(new(p.Name, s.Parameters.TryGetValue(p.Name, out double v) ? v : p.Default!.Value));
            }
            if (s.Shifted)
                foreach (string state in states)
                    if (model.ShiftParameter(state) is { } name)
                        parameters.Add(new(name, strengths is not null && strengths.TryGetValue(name, out double fitted) ? fitted
                                               : s.Parameters.TryGetValue(name, out double typed) ? typed : 0.0));
        }
        else
        {
            if (s.Emission != TrapEmission.Constant)
                throw new ModelingException(StepName, $"Emission is a setting of {CaptureEmissionTraps.ModelName}; write T into a {s.Model} model's dynamics.");
            states = s.States.Count > 0 ? s.States : [.. model.States.Where(s.Expressions.ContainsKey)];
            parameters.AddRange(s.Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal));
            foreach (string state in states)
                if (s.Expressions.TryGetValue(state, out var e))
                {
                    expressions.Add(new(CustomTraps.Key(state, CustomTraps.DynamicsKey), e.Dynamics));
                    expressions.Add(new(CustomTraps.Key(state, CustomTraps.DcKey), e.Dc));
                }
        }
        var block = new ModelTraps
        {
            Model = s.Model, Coupling = s.Coupling, States = states, Parameters = parameters, Expressions = expressions,
            Levels = levels is null ? [] : [.. states.Where(levels.ContainsKey).Select(st => new KeyValuePair<string, IReadOnlyList<double>>(st, levels[st]))],
        };
        try { TrapModels.Build(block, "trap step"); }
        catch (CmodelFormatException ex) { throw new ModelingException(StepName, ex.Message); }
        return block;
    }

    // ── The run ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the step over the current step's I–V sets: maps every point to its trap state and records the levels, the
    /// points left as validation, and the warnings. Throws <see cref="ModelingException"/> for a refusal.
    /// </summary>
    public static TrapFitResult Run(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data, IntrinsicResult intrinsic,
                                    TrapFitSettings settings, RunControl? rc = null, CurrentFitResult? current = null)
    {
        rc?.ThrowIfCancellationRequested();
        if (!settings.Modelled) return new TrapFitResult(settings, new TrapFitRecord());
        if (settings.Shifted) return FitShift(doc, intrinsic, settings, current, rc);
        if (!HasPulsedIv(doc)) throw new ModelingException(StepName, $"traps are unavailable: {NoPulsedData}.");
        var mapping = Map(doc, data, intrinsic, settings, CurrentFit.ChosenIv(doc, CurrentFit.Settings(doc)), "pulsed I–V");
        return new TrapFitResult(settings, new TrapFitRecord { Levels = mapping.Levels, Beyond = mapping.Beyond, Report = mapping.Report });
    }

    /// <summary>
    /// The trap states of <paramref name="sets"/>' points (R-mc10-4). <paramref name="kind"/> names the pulsed data a
    /// refusal asks for. Throws <see cref="ModelingException"/> when an enabled state is left with no trap-setting level
    /// (unless <paramref name="requireLevels"/> is false: then that state's levels are empty, for the caller to decide), a
    /// pulsed set states no trap-setting bias, or a dissipating one states no drain current.
    /// </summary>
    public static TrapMapping Map(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data, IntrinsicResult intrinsic,
                                  TrapFitSettings settings, IReadOnlyList<CmcDataSet> sets, string kind, bool requireLevels = true)
    {
        if (settings.Shifted) throw new ModelingException(StepName, $"a {ModelTraps.Shift} coupling maps no data to trap states.");
        var block = Block(settings);
        var dyn = TrapModels.Build(block, "trap step");
        var states = block.States;
        int n = states.Count;
        var report = new List<string>();
        var phi = new Dictionary<string, double[]?[]>(StringComparer.Ordinal);
        var levels = new List<double>[n];
        var beyond = new int[n];
        for (int k = 0; k < n; k++) levels[k] = [];
        var scale = Dissipation(sets, data);

        foreach (var set in sets)
        {
            if (!intrinsic.Sets.TryGetValue(set.Name, out var ds))
                throw new ModelingException(set.Name, "it has no intrinsic data; de-embed it first.");
            double[] vgs = ds["Vgsi"].RealValues, vds = ds["Vdsi"].RealValues, ex = ds["Excluded"].RealValues;
            var points = data.TryGetValue(set.Name, out var nd) && nd.Points.Count == vgs.Length ? nd.Points : null;
            var rows = new double[]?[vgs.Length];
            bool pulsed = set.Kind is DataSetKind.PulsedIv or DataSetKind.PulsedSParams;
            if (!pulsed)
            {
                for (int p = 0; p < rows.Length; p++)
                {
                    double t = CurrentFit.AmbientOf(set, points?[p]);
                    rows[p] = [.. Enumerable.Range(0, n).Select(k => dyn.DcValue(k, vgs[p], vds[p], t))];
                }
                phi[set.Name] = rows;
                continue;
            }

            var (sg, sd) = SettingBias(set, ds, scale);
            Timing(set, dyn, states, block.Model, report);
            var mode = set.Conditions.TrapState ?? TrapStateMode.Auto;
            var used = new bool[n];
            for (int p = 0; p < rows.Length; p++)
            {
                // An excluded point trains nothing, so it neither sets a level nor counts as beyond.
                if (ex[p] != 0) continue;
                double t = CurrentFit.AmbientOf(set, points?[p]);
                var row = new double[n];
                bool validation = false;
                for (int k = 0; k < n; k++)
                {
                    double at = dyn.DcValue(k, sg, sd, t), own = dyn.DcValue(k, vgs[p], vds[p], t);
                    int sign = dyn.CaptureSign(k);
                    bool toward = sign * (own - at) <= 0;
                    if (toward) { row[k] = at; continue; }
                    switch (mode)
                    {
                        case TrapStateMode.Setting: row[k] = at; break;
                        case TrapStateMode.Extreme: row[k] = sign > 0 ? Math.Max(at, own) : Math.Min(at, own); break;
                        default:
                            // Counted for every state it lies beyond, not only the first.
                            beyond[k]++;
                            validation = true;
                            break;
                    }
                }
                rows[p] = validation ? null : row;
                if (!validation) for (int k = 0; k < n; k++) used[k] = true;
            }
            phi[set.Name] = rows;
            for (int k = 0; k < n; k++)
            {
                if (!used[k]) continue;
                double level = dyn.DcValue(k, sg, sd, set.Conditions.AmbientC ?? 25.0);
                if (!levels[k].Any(l => Math.Abs(l - level) <= LevelTolerance)) levels[k].Add(level);
            }
        }

        var byState = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
        var beyondByState = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int k = 0; k < n; k++)
        {
            string label = Label(block.Model, states[k]);
            if (beyond[k] > 0)
            {
                beyondByState[states[k]] = beyond[k];
                report.Add($"{label}: {beyond[k]} points beyond the trap-setting bias, validation only");
            }
            if (levels[k].Count == 0 && !requireLevels) { byState[states[k]] = []; continue; }
            if (levels[k].Count == 0)
                throw new ModelingException(StepName, beyond[k] > 0
                    ? $"{label} has no trap level: every {kind} point lies beyond its trap-setting bias; pulse from a setting bias beyond the pulses, or set the sets' TrapState to setting or extreme."
                    : $"{label} needs {kind} with a Quiescent or PrePulse bias; there is none.");
            levels[k].Sort();
            byState[states[k]] = levels[k];
            if (levels[k].Count == 1) report.Add($"{label} from one trap level, {G(levels[k][0])} V");
        }
        return new TrapMapping
        {
            Block = block with { Levels = [.. states.Where(s => byState[s].Count > 0).Select(s => new KeyValuePair<string, IReadOnlyList<double>>(s, byState[s]))] },
            Dynamics = dyn, Phi = phi, Levels = byState, Beyond = beyondByState, Report = report,
        };
    }

    /// <summary>
    /// The trap-setting bias at the intrinsic plane: the pre-pulse level of a double pulse, else the quiescent point. A
    /// bias whose drain current is not stated is re-referenced at zero current, which is right only when it dissipates
    /// nothing — so one the data shows to be conducting is refused.
    /// </summary>
    private static (double Vgs, double Vds) SettingBias(CmcDataSet set, DataSet ds, Func<double, double, double> power)
    {
        var c = set.Conditions;
        if (c.Quiescent is null && c.PrePulse is null)
            throw new ModelingException(set.Name, "a pulsed set states its Quiescent bias (and its PrePulse level for a double pulse); the trap states are mapped from it.");
        if (!ds.Contains(IntrinsicExtraction.SettingCube))
            throw new ModelingException(set.Name, "its intrinsic data predates the trap-setting biases; de-embed it again.");
        var v = ds[IntrinsicExtraction.SettingCube].RealValues;
        int row = c.PrePulse is not null ? 1 : 0;
        string what = row == 1 ? "PrePulse" : "Quiescent";
        double vgs = row == 1 ? c.PrePulse!.Vgs : c.Quiescent!.Vgs, vds = row == 1 ? c.PrePulse!.Vds : c.Quiescent!.Vds;
        if (v[3 * row + 2] == 0 && power(vgs, vds) > 0)
            throw new ModelingException(set.Name,
                $"its {what} bias ({G(vgs)} V, {G(vds)} V) conducts and states no drain current, which re-referencing it needs; give {what}.Id.");
        return (v[3 * row], v[3 * row + 1]);
    }

    /// <summary>
    /// Whether a bias conducts, judged from the data: the drain current of the nearest measured point (both voltages
    /// normalised by the data's spans) times the bias's drain voltage, against 1 % of the largest point power. Returns that
    /// power, or 0 below the threshold.
    /// </summary>
    private static Func<double, double, double> Dissipation(IReadOnlyList<CmcDataSet> sets, IReadOnlyDictionary<string, NormalisedData> data)
    {
        var points = sets.Where(s => data.ContainsKey(s.Name)).SelectMany(s => data[s.Name].Points)
                         .Where(p => !p.Excluded && double.IsFinite(p.Id) && double.IsFinite(p.Vgs) && double.IsFinite(p.Vds)).ToList();
        if (points.Count == 0) return (_, _) => 0;
        double gs = Math.Max(points.Max(p => p.Vgs) - points.Min(p => p.Vgs), 1e-3);
        double dsSpan = Math.Max(points.Max(p => p.Vds) - points.Min(p => p.Vds), 1e-3);
        double largest = points.Max(p => Math.Abs(p.Id * p.Vds));
        return (vgs, vds) =>
        {
            var near = points.MinBy(p => Math.Pow((p.Vgs - vgs) / gs, 2) + Math.Pow((p.Vds - vds) / dsSpan, 2))!;
            double pw = Math.Abs(near.Id * vds);
            return pw > 0.01 * largest ? pw : 0;
        };
    }

    /// <summary>One warning per timing that is not ≪ τ_emit: emission then moves the state during the measurement.</summary>
    private static void Timing(CmcDataSet set, TrapDynamics dyn, IReadOnlyList<string> states, string model, List<string> report)
    {
        var c = set.Conditions;
        double t = c.AmbientC ?? 25.0;
        for (int k = 0; k < states.Count; k++)
        {
            if (dyn.EmissionTime(k, t) is not { } tau) continue;
            foreach (var (name, value) in new[] { ("SampleDelay", c.SampleDelay), ("PulseWidth", c.PulseWidth), ("PrePulse.Gap", c.PrePulse?.Gap) })
                if (value is { } x && x > EmissionFraction * tau)
                    report.Add($"{set.Name}: {name} {G(x)} s is not ≪ {Label(model, states[k])} τ_emit {G(tau)} s; emission moves the state during the measurement.");
        }
    }

    private static string G(double v) => v.ToString("G3", CultureInfo.InvariantCulture);
}
