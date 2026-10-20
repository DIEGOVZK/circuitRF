// The model steps' plot data (brief-modelcomposer-15 R-mc15-2 … R-mc15-6, R-mc15-9): what each phase computed, laid out as
// the DataSet the window's PlotControls draw. Nothing here fits anything — every number is a fitted step's model evaluated
// where its own data is, or a check's own result — and the window computes nothing at all: it groups and draws these cubes.
//
//   Current   per I–V set, the points the current step trained on: measured (extrinsic) bias, intrinsic bias, the measured
//             channel current and the model's, its gm and gds against the data's own derivatives along each measured
//             family, the error as a map over the bias plane, and the training domain's outline. With dispersion, RF gm and
//             gds of the intrinsic S sets against the model's.
//   Charge    the measured capacitances at their intrinsic bias, the model's Qg and Qd over the data's hull, and each against
//             the line integral of the data (the report's own function) in % of its range; the 1-D C–V slice at a fixed
//             voltage is read from the compiled model when the cursor moves (Slice), which is a hundred evaluations.
//   Thermal   the report's equivalent-temperature rise against dissipated power, and per DC set the measured current
//             against the model with self-heating and the isothermal model at the ambient.
//   Traps     per pulsed set, its trap-setting bias, the measured channel current against the model at each point's own
//             trap states, and the points beyond the trap-setting bias (validation only, no model value).
//   Check     the conservation report: each row's curl and the two-path line-integral difference as maps, and the HB
//             drives' gate-current spectra.
//
// A map is a square grid over the data's hull (vds fastest, NaN outside it) — the contour renderer's own shape. Cube names
// are "<set>.<quantity>" for per-set cubes, plain otherwise.

using System.Globalization;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;
using RfCore.Data;

namespace CircuitRF.Design.Modeling;

/// <summary>One step's plot data: its cubes, a note per set that could not be drawn, and — for the Charge step — the
/// compiled model the 1-D C–V slices are read from.</summary>
public sealed class StepPlotData
{
    public required string Step { get; init; }
    public required DataSet Data { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The sets the step's per-set cubes are about, in the document's order.</summary>
    public IReadOnlyList<string> Sets { get; init; } = [];

    internal ComposedCoreProgram? Program { get; init; }
    internal ChargeData? Charges { get; init; }

    /// <summary>
    /// The 1-D C–V slice (R-mc15-3): every <c>Cij</c> and the circuit set <c>Cgs Cgd Cds Cm</c> of the model along
    /// <c>vgs</c> at <c>vds = fixedValue</c> (<paramref name="sweepGate"/>) or along <c>vds</c> at <c>vgs = fixedValue</c>, over
    /// the data's span, with the measured points whose fixed voltage lies within half the data's spacing of it. Null
    /// without a fitted charge.
    /// </summary>
    public DataSet? Slice(bool sweepGate, double fixedValue)
    {
        if (Program is not { } program || Charges is not { } data) return null;
        var swept = data.X.Select(p => sweepGate ? p.Vgs : p.Vds).ToArray();
        var fixedOf = data.X.Select(p => sweepGate ? p.Vds : p.Vgs).ToArray();
        var (lo, hi) = ChargeFit.SliceSpan(swept);
        var model = ChargeFit.Slice(program, sweepGate, fixedValue, lo, hi);
        var ds = new DataSet();
        foreach (var (name, cube) in model.Cubes) ds.Add($"{name}.model", cube);
        var m = ChargeFitRecord.Capacitances.Select(c => model[c].RealValues).ToArray();
        var axis = model[ChargeFitRecord.Capacitances[0]].Axes[0];
        foreach (var (name, of) in ModelValidationReport.Circuit)
            ds.Add($"{name}.model", new DataCube([axis], [.. Enumerable.Range(0, axis.Length).Select(k => of(m[0][k], m[1][k], m[2][k], m[3][k]))]));

        double tol = 0.5 * Spacing(fixedOf);
        var near = Enumerable.Range(0, data.X.Count).Where(k => Math.Abs(fixedOf[k] - fixedValue) <= tol).OrderBy(k => swept[k]).ToArray();
        var pAxis = new Axis("point", [.. near.Select(k => (double)k)]);
        ds.Add("x.meas", new DataCube([pAxis], [.. near.Select(k => swept[k])]));
        double[][] meas = [data.C11, data.C12, data.C21, data.C22];
        for (int c = 0; c < 4; c++) ds.Add($"{ChargeFitRecord.Capacitances[c]}.meas", new DataCube([pAxis], [.. near.Select(k => meas[c][k])]));
        foreach (var (name, of) in ModelValidationReport.Circuit)
            ds.Add($"{name}.meas", new DataCube([pAxis], [.. near.Select(k => of(data.C11[k], data.C12[k], data.C21[k], data.C22[k]))]));
        return ds;
    }

    /// <summary>The spacing of a measured voltage's levels: the smallest gap between values that differ by more than 2 % of
    /// their span — intrinsic voltages scatter about each measured level by the extrinsic drops. One level is the only
    /// family there is, whatever the cursor says.</summary>
    private static double Spacing(double[] values)
    {
        var sorted = values.Order().ToArray();
        double span = sorted.Length > 0 ? sorted[^1] - sorted[0] : 0;
        if (!(span > 0)) return double.PositiveInfinity;
        double gap = double.PositiveInfinity;
        for (int k = 1; k < sorted.Length; k++)
            if (sorted[k] - sorted[k - 1] > 0.02 * span) gap = Math.Min(gap, sorted[k] - sorted[k - 1]);
        return double.IsFinite(gap) ? gap : Math.Max(span, 1e-3);
    }
}

public static class ModelStepPlots
{
    /// <summary>The steps with plot data of their own.</summary>
    public static readonly IReadOnlyList<string> Steps = ["Current", "Charge", "Thermal", "Traps"];

    /// <summary>A square map's points per axis.</summary>
    public const int MapPoints = 41;

    /// <summary>
    /// <paramref name="step"/>'s plot data for <paramref name="doc"/> (the window's document; its fitted arrays are read from
    /// the cache beside <paramref name="cmcPath"/>), over the data the window has open. Null when the step has not been fitted.
    /// </summary>
    public static StepPlotData? Build(string cmcPath, CmcDocument doc, ModelData loaded, string step, RunControl? rc = null)
    {
        cmcPath = Path.GetFullPath(cmcPath);
        var intrinsic = new IntrinsicResult(loaded.Intrinsic, []);
        return step switch
        {
            "Current" => Current(cmcPath, doc, loaded.Data, intrinsic, rc),
            "Charge" => Charge(cmcPath, doc, loaded.Data, intrinsic, rc),
            "Thermal" => Thermal(cmcPath, doc, loaded.Data, intrinsic, rc),
            "Traps" => Traps(cmcPath, doc, loaded.Data, intrinsic, rc),
            _ => null,
        };
    }

    // ── Current (R-mc15-2) ────────────────────────────────────────────────────

    private static StepPlotData? Current(string cmcPath, CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data,
                                         IntrinsicResult intrinsic, RunControl? rc)
    {
        if (CurrentFit.Load(cmcPath, doc) is not { } current) return null;
        var settings = CurrentFit.Settings(doc);
        var heating = ThermalFit.Heating(doc);
        var program = Program(current, dc: true, CurrentFit.Temperatures(CurrentFit.ChosenIv(doc, settings), data));
        var ds = new DataSet();
        var notes = new List<string>();
        var sets = new List<string>();
        foreach (var set in CurrentFit.ChosenIv(doc, settings))
        {
            rc?.ThrowIfCancellationRequested();
            try
            {
                // A DC set reads every trap state at its DC value, which DcSeeds gives; a pulsed set its own states.
                bool pulsed = set.Kind is DataSetKind.PulsedIv;
                var ip = CurrentFit.Gather(doc, data, intrinsic, settings with { DataSets = [set.Name] }, [], heating,
                                           withTraps: pulsed && TrapFit.Modelled(doc));
                AddIv(ds, set.Name, ip, data[set.Name], program);
                AddDerivatives(ds, set.Name);
                if (Map(ds, set.Name, "Error", ip.Domain.Polygon) is { } map) ds.Add($"{set.Name}.ErrorMap", map);
                sets.Add(set.Name);
            }
            catch (ModelingException ex) { notes.Add($"{set.Name}: {ex.Message}"); }
        }
        if (current.Record.Id.Domain is { Polygon.Count: > 2 } domain)
        {
            var poly = domain.Polygon.Append(domain.Polygon[0]).ToArray();
            var axis = new Axis("vertex", [.. Enumerable.Range(0, poly.Length).Select(i => (double)i)]);
            ds.Add("Domain.vgs", new DataCube([axis], [.. poly.Select(p => p.Vgs)]));
            ds.Add("Domain.vds", new DataCube([axis], [.. poly.Select(p => p.Vds)]));
        }

        // RF against the model when the currents are dispersive: the intrinsic S sets' gm and gds at their lowest frequency.
        if (current.Record.IdRF is not null)
        {
            var rf = Program(current, dc: false, CurrentFit.Temperatures(CurrentFit.ChosenIv(doc, settings), data));
            foreach (var set in doc.DataSets.Where(s => s.Kind is DataSetKind.SParams))
            {
                if (!intrinsic.Sets.TryGetValue(set.Name, out var ids) || !ids.Cubes.ContainsKey("Gm")) continue;
                double[] vgs = ids["Vgsi"].RealValues, vds = ids["Vdsi"].RealValues;
                var gm = ids["Gm"]; var gds = ids["Gds"];
                var keep = Enumerable.Range(0, vgs.Length).Where(k => double.IsFinite(vgs[k]) && double.IsFinite(vds[k])).ToArray();
                var axis = new Axis("point", [.. keep.Select(k => (double)k)]);
                double t = set.Conditions.AmbientC ?? 25;
                var model = keep.Select(k => Evaluate(rf, vgs[k], vds[k], t, null)).ToArray();
                ds.Add($"{set.Name}.Vgs", new DataCube([axis], [.. keep.Select(k => data.TryGetValue(set.Name, out var d) ? d.Points[k].Vgs : vgs[k])]));
                ds.Add($"{set.Name}.Vds", new DataCube([axis], [.. keep.Select(k => data.TryGetValue(set.Name, out var d) ? d.Points[k].Vds : vds[k])]));
                ds.Add($"{set.Name}.Vgsi", new DataCube([axis], [.. keep.Select(k => vgs[k])]));
                ds.Add($"{set.Name}.Vdsi", new DataCube([axis], [.. keep.Select(k => vds[k])]));
                ds.Add($"{set.Name}.Gm.rf", new DataCube([axis], [.. keep.Select(k => (double)gm[k, 0])]));
                ds.Add($"{set.Name}.Gds.rf", new DataCube([axis], [.. keep.Select(k => (double)gds[k, 0])]));
                ds.Add($"{set.Name}.Gm.rfmodel", new DataCube([axis], [.. model.Select(m => m.Gm)]));
                ds.Add($"{set.Name}.Gds.rfmodel", new DataCube([axis], [.. model.Select(m => m.Gds)]));
            }
        }
        return new StepPlotData { Step = "Current", Data = ds, Notes = notes, Sets = sets };
    }

    /// <summary>The fitted currents compiled with zero charges: the DC model (<paramref name="dc"/>, IdRF left out) or the RF one.</summary>
    private static ComposedCoreProgram Program(CurrentFitResult current, bool dc, IReadOnlyList<double> temperatures)
    {
        var c = dc && current.Record.IdRF is not null ? current with { Record = current.Record with { IdRF = null, Tau = null } } : current;
        var zero = Constituent.Equation("0", EquationFit.VoltageInputs);
        double t0 = temperatures.Count > 0 ? temperatures[0] : 25;
        return ComposedCoreProgram.Compile(ChargeFit.Model(zero, zero, new Dictionary<string, DataCube>(), c, t0), "plots");
    }

    private static (double Id, double Gm, double Gds) Evaluate(ComposedCoreProgram program, double vgs, double vds, double t, double[]? states)
    {
        Span<double> s = stackalloc double[RuntimeEvaluator.MaxInputs], g = stackalloc double[RuntimeEvaluator.MaxInputs];
        program.DcSeeds(vgs, vds, t, s);
        if (states is not null && program.Traps is { } traps)
            for (int j = 0; j < traps.States.Count && j < states.Length; j++) s[program.TrapLane + j] = states[j];
        double id = program.Channel(s[..program.Width], g);
        return (id, g[0], g[1]);
    }

    /// <summary>One set's gathered points, measured against the model at each point's own inputs.</summary>
    private static void AddIv(DataSet ds, string set, CurrentFit.IvPoints ip, NormalisedData data, ComposedCoreProgram program)
    {
        int n = ip.X.Length;
        int nTrap = ip.Trapped ? ip.Traps!.States.Count : 0;
        var model = new (double Id, double Gm, double Gds)[n];
        for (int k = 0; k < n; k++)
        {
            var x = ip.X[k];
            double t = ip.HasT ? x[2] : ip.TemperatureC;
            model[k] = Evaluate(program, x[0], x[1], t, nTrap > 0 ? x[^nTrap..] : null);
        }
        var axis = new Axis("point", [.. ip.Origin.Select(o => (double)o.Point)]);
        double lo = ip.Id.Min(), hi = ip.Id.Max(), range = hi > lo ? hi - lo : 1;
        ds.Add($"{set}.Vgs", new DataCube([axis], [.. ip.Origin.Select(o => data.Points[o.Point].Vgs)]));
        ds.Add($"{set}.Vds", new DataCube([axis], [.. ip.Origin.Select(o => data.Points[o.Point].Vds)]));
        ds.Add($"{set}.Vgsi", new DataCube([axis], [.. ip.X.Select(x => x[0])]));
        ds.Add($"{set}.Vdsi", new DataCube([axis], [.. ip.X.Select(x => x[1])]));
        ds.Add($"{set}.T", new DataCube([axis], [.. ip.X.Select((x, k) => ip.HasT ? x[2] : ip.TemperatureC)]));
        ds.Add($"{set}.Id.meas", new DataCube([axis], ip.Id));
        ds.Add($"{set}.Id.model", new DataCube([axis], [.. model.Select(m => m.Id)]));
        ds.Add($"{set}.Gm.model", new DataCube([axis], [.. model.Select(m => m.Gm)]));
        ds.Add($"{set}.Gds.model", new DataCube([axis], [.. model.Select(m => m.Gds)]));
        ds.Add($"{set}.Error", new DataCube([axis], [.. model.Select((m, k) => 100 * (m.Id - ip.Id[k]) / range)]));
    }

    /// <summary>
    /// The data's own gm and gds: along each measured transfer family (one extrinsic vds) the slope of the measured current
    /// over the intrinsic vgs, and along each output family over the intrinsic vds — central differences, one-sided at the
    /// ends.
    /// </summary>
    private static void AddDerivatives(DataSet ds, string set)
    {
        double[] vgs = ds[$"{set}.Vgs"].RealValues, vds = ds[$"{set}.Vds"].RealValues;
        double[] gi = ds[$"{set}.Vgsi"].RealValues, di = ds[$"{set}.Vdsi"].RealValues, id = ds[$"{set}.Id.meas"].RealValues;
        var axis = ds[$"{set}.Vgs"].Axes[0];
        ds.Add($"{set}.Gm.data", new DataCube([axis], Slopes(vds, gi, id)));
        ds.Add($"{set}.Gds.data", new DataCube([axis], Slopes(vgs, di, id)));
    }

    private static double[] Slopes(double[] family, double[] x, double[] y)
    {
        var slope = Enumerable.Repeat(double.NaN, x.Length).ToArray();
        foreach (var g in Enumerable.Range(0, x.Length).GroupBy(k => Math.Round(family[k], 6)))
        {
            var pts = g.OrderBy(k => x[k]).ToArray();
            if (pts.Length < 2) continue;
            for (int i = 0; i < pts.Length; i++)
            {
                int a = pts[Math.Max(0, i - 1)], b = pts[Math.Min(pts.Length - 1, i + 1)];
                if (x[b] != x[a]) slope[pts[i]] = (y[b] - y[a]) / (x[b] - x[a]);
            }
        }
        return slope;
    }

    /// <summary>A per-point quantity of <paramref name="set"/> as a square map over the intrinsic bias plane, NaN outside the hull.</summary>
    private static DataCube? Map(DataSet ds, string set, string quantity, IReadOnlyList<(double Vgs, double Vds)> hull)
    {
        double[] gi = ds[$"{set}.Vgsi"].RealValues, di = ds[$"{set}.Vdsi"].RealValues, v = ds[$"{set}.{quantity}"].RealValues;
        var keep = Enumerable.Range(0, v.Length).Where(k => double.IsFinite(v[k])).ToArray();
        if (keep.Length < 3 || hull.Count < 3) return null;
        return Map(Regrid.Rbf([.. keep.Select(k => (gi[k], di[k]))], [.. keep.Select(k => v[k])]), hull);
    }

    /// <summary><paramref name="f"/> on a <see cref="MapPoints"/>-square grid over <paramref name="hull"/>'s box, NaN outside it.</summary>
    internal static DataCube Map(Func<double, double, double> f, IReadOnlyList<(double Vgs, double Vds)> hull)
    {
        var (vgs, vds) = Axes(hull);
        var values = Regrid.Through((g, d) => ChargeFit.Inside(hull, (g, d)) ? f(g, d) : double.NaN, vgs, vds);
        return new DataCube([new Axis("vgs", vgs, "V"), new Axis("vds", vds, "V")], values);
    }

    private static (double[] Vgs, double[] Vds) Axes(IReadOnlyList<(double Vgs, double Vds)> hull)
    {
        double[] Line(double lo, double hi) => [.. Enumerable.Range(0, MapPoints).Select(k => lo + (hi - lo) * k / (MapPoints - 1))];
        return (Line(hull.Min(p => p.Vgs), hull.Max(p => p.Vgs)), Line(hull.Min(p => p.Vds), hull.Max(p => p.Vds)));
    }

    // ── Charge (R-mc15-3) ─────────────────────────────────────────────────────

    private static StepPlotData? Charge(string cmcPath, CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data,
                                        IntrinsicResult intrinsic, RunControl? rc)
    {
        if (ChargeFit.Load(cmcPath, doc) is not { } charges) return null;
        var cd = ChargeFit.Gather(doc, data, intrinsic, charges.Settings, []);
        var program = ComposedCoreProgram.Compile(ChargeFit.Model(charges, CurrentFit.Load(cmcPath, doc), cd.TemperatureC), "plots");
        var ds = new DataSet();
        IReadOnlyList<string> notes = [];
        // Points that span no area have no surface to draw: their charges are equations, and the C–V slices show them.
        if (cd.SpansArea)
        {
            var (vgs, vds) = Axes(cd.Domain.Polygon);
            var surfaces = ModelValidationReport.ChargeSurface(program, cd, charges.Record.Anchor, vgs, vds, rc);
            foreach (var (name, cube) in surfaces.Data.Cubes) ds.Add(name, cube);
            notes = surfaces.Lines;
        }
        var axis = new Axis("point", [.. Enumerable.Range(0, cd.X.Count).Select(i => (double)i)]);
        ds.Add("Vgsi", new DataCube([axis], [.. cd.X.Select(p => p.Vgs)]));
        ds.Add("Vdsi", new DataCube([axis], [.. cd.X.Select(p => p.Vds)]));
        return new StepPlotData { Step = "Charge", Data = ds, Notes = notes, Program = program, Charges = cd };
    }

    // ── Thermal (R-mc15-4) ────────────────────────────────────────────────────

    private static StepPlotData? Thermal(string cmcPath, CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data,
                                         IntrinsicResult intrinsic, RunControl? rc)
    {
        if (ThermalFit.Network(ThermalFit.Record(doc)) is not { } thermal || CurrentFit.Load(cmcPath, doc) is not { } current) return null;
        var settings = CurrentFit.Settings(doc);
        var program = Program(current, dc: true, CurrentFit.Temperatures(CurrentFit.ChosenIv(doc, settings), data));
        if (!program.HasT) return null;
        var ds = new DataSet();
        var notes = new List<string>();
        var sets = new List<string>();
        if (ModelValidationReport.Thermal(doc, data, intrinsic, program, thermal) is { } rise)
        {
            foreach (var (name, cube) in rise.Data.Cubes) ds.Add(name, cube);
            notes.AddRange(rise.Lines);
        }
        var heating = ThermalFit.Heating(doc);
        foreach (var set in doc.DataSets.Where(s => s.Kind == DataSetKind.DcIv))
        {
            rc?.ThrowIfCancellationRequested();
            try
            {
                var s = settings with { DataSets = [set.Name] };
                var hot = CurrentFit.Gather(doc, data, intrinsic, s, [], heating, withTraps: false);
                var iso = CurrentFit.Gather(doc, data, intrinsic, s, [], ThermalHeating.None, withTraps: false);
                AddIv(ds, set.Name, hot, data[set.Name], program);
                var axis = ds[$"{set.Name}.Vgs"].Axes[0];
                ds.Add($"{set.Name}.Id.iso", new DataCube([axis], [.. iso.X.Select(x => Evaluate(program, x[0], x[1], iso.HasT ? x[2] : iso.TemperatureC, null).Id)]));
                sets.Add(set.Name);
            }
            catch (ModelingException ex) { notes.Add($"{set.Name}: {ex.Message}"); }
        }
        return new StepPlotData { Step = "Thermal", Data = ds, Notes = notes, Sets = sets };
    }

    // ── Traps (R-mc15-5) ──────────────────────────────────────────────────────

    private static StepPlotData? Traps(string cmcPath, CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data,
                                       IntrinsicResult intrinsic, RunControl? rc)
    {
        if (!TrapFit.Modelled(doc) || TrapFit.Record(doc) is null || CurrentFit.Load(cmcPath, doc) is not { } current) return null;
        var settings = CurrentFit.Settings(doc);
        var heating = ThermalFit.Heating(doc);
        var program = Program(current, dc: true, CurrentFit.Temperatures(CurrentFit.ChosenIv(doc, settings), data));
        var ds = new DataSet();
        var notes = new List<string>();
        var sets = new List<string>();
        foreach (var set in doc.DataSets.Where(s => s.Kind is DataSetKind.PulsedIv or DataSetKind.DcIv))
        {
            rc?.ThrowIfCancellationRequested();
            try
            {
                bool pulsed = set.Kind == DataSetKind.PulsedIv;
                var ip = CurrentFit.Gather(doc, data, intrinsic, settings with { DataSets = [set.Name] }, [], heating, withTraps: pulsed);
                AddIv(ds, set.Name, ip, data[set.Name], program);
                if (pulsed) AddBeyond(ds, set, ip, data[set.Name], intrinsic, settings);
                sets.Add(set.Name);
            }
            catch (ModelingException ex) { notes.Add($"{set.Name}: {ex.Message}"); }
        }
        return new StepPlotData { Step = "Traps", Data = ds, Notes = notes, Sets = sets };
    }

    /// <summary>A pulsed set's points beyond its trap-setting bias — validation only, so measured and nothing else — and the
    /// trap-setting level the set was measured from, as the label of its curves.</summary>
    private static void AddBeyond(DataSet ds, CmcDataSet set, CurrentFit.IvPoints ip, NormalisedData data, IntrinsicResult intrinsic,
                                  CurrentFitSettings settings)
    {
        var bias = set.Conditions.PrePulse is { } pp ? (pp.Vgs, pp.Vds) : set.Conditions.Quiescent is { } q ? (q.Vgs, q.Vds) : (double.NaN, double.NaN);
        ds.Add($"{set.Name}.Setting", new DataCube([new Axis("bias", [0, 1])], [bias.Item1, bias.Item2]));
        if (ip.Traps is not { } traps || !traps.Phi.TryGetValue(set.Name, out var phi) || !intrinsic.Sets.TryGetValue(set.Name, out var ids)) return;
        double[] vgs = ids["Vgsi"].RealValues, vds = ids["Vdsi"].RealValues, id = ids["Id"].RealValues, ig = ids["Ig"].RealValues,
                 ex = ids["Excluded"].RealValues;
        var beyond = Enumerable.Range(0, phi.Length)
                               .Where(k => phi[k] is null && ex[k] == 0 && double.IsFinite(vgs[k]) && double.IsFinite(vds[k]) && double.IsFinite(id[k]))
                               .ToArray();
        var axis = new Axis("point", [.. beyond.Select(k => (double)k)]);
        ds.Add($"{set.Name}.Beyond.Vgs", new DataCube([axis], [.. beyond.Select(k => data.Points[k].Vgs)]));
        ds.Add($"{set.Name}.Beyond.Vds", new DataCube([axis], [.. beyond.Select(k => data.Points[k].Vds)]));
        ds.Add($"{set.Name}.Beyond.Id", new DataCube([axis],
            [.. beyond.Select(k => id[k] + CurrentFit.GateDrainBranch(double.IsNaN(ig[k]) ? 0 : ig[k], vds[k], settings.GateSplit))]));
    }

    // ── Check (R-mc15-6) ──────────────────────────────────────────────────────

    /// <summary>
    /// The Check step's plots from a conservation report and the document's charge data: each row's data curl and the
    /// two-path line-integral difference (path A against path B, % of the charge's range) as maps over the data's hull, and
    /// the gate current's spectrum at f and at 2f.
    /// </summary>
    public static StepPlotData Check(string cmcPath, CmcDocument doc, ModelData loaded, ConservationReport report, RunControl? rc = null)
    {
        var ds = new DataSet();
        if (report.Data is { } a && a.Points.Count >= 3)
        {
            var hull = CurrentFit.Hull([.. a.Points.Select(p => new[] { p.Vgs, p.Vds })]).Polygon;
            foreach (var (row, map) in new[] { ("Qg", a.GateMap), ("Qd", a.DrainMap) })
            {
                var keep = Enumerable.Range(0, map.Length).Where(k => double.IsFinite(map[k])).ToArray();
                if (keep.Length < 3) continue;
                ds.Add($"Curl.{row}", Map(Regrid.Rbf([.. keep.Select(k => a.Points[k])], [.. keep.Select(k => 100 * map[k])]), hull));
            }
        }
        if (ChargeFit.Load(cmcPath, doc) is { } charges
            && ChargeFit.Gather(doc, loaded.Data, new IntrinsicResult(loaded.Intrinsic, []), charges.Settings, []) is { SpansArea: true } cd)
        {
            var poly = cd.Domain.Polygon;
            var (vgs, vds) = Axes(poly);
            foreach (bool gate in (bool[])[true, false])
            {
                rc?.ThrowIfCancellationRequested();
                var (c1, c2) = cd.Row(gate);
                double typical = Math.Sqrt(c1.Concat(c2).Average(v => v * v));
                var (pa, pb) = LineIntegral.OntoGrid(Regrid.Rbf(cd.X, c1), Regrid.Rbf(cd.X, c2), charges.Record.Anchor, vgs, vds,
                                                     1e-7 * typical * (vgs[1] - vgs[0]), rc);
                var inside = Enumerable.Range(0, pa.Length).Where(k => ChargeFit.Inside(poly, (vgs[k / vds.Length], vds[k % vds.Length]))).ToHashSet();
                double range = inside.Count > 0 ? inside.Max(k => 0.5 * (pa[k] + pb[k])) - inside.Min(k => 0.5 * (pa[k] + pb[k])) : 0;
                var map = Enumerable.Range(0, pa.Length).Select(k => inside.Contains(k) && range > 0 ? 100 * (pa[k] - pb[k]) / range : double.NaN).ToArray();
                ds.Add($"Paths.{(gate ? "Qg" : "Qd")}", new DataCube([new Axis("vgs", vgs, "V"), new Axis("vds", vds, "V")], map));
            }
        }
        if (report.LargeSignal is { } c)
            foreach (var (tag, drive) in new[] { ("f", c.AtF), ("2f", c.At2F) })
            {
                if (drive.GateSpectrum.Count == 0) continue;
                var axis = new Axis("harmonic", [.. Enumerable.Range(0, drive.GateSpectrum.Count).Select(h => (double)h)]);
                ds.Add($"Spectrum.{tag}", new DataCube([axis], [.. drive.GateSpectrum]));
            }
        return new StepPlotData { Step = "Check", Data = ds, Notes = report.Lines };
    }

    // ── Validate (R-mc15-7) ───────────────────────────────────────────────────

    /// <summary>
    /// A loadpull validation item's measured (or, with <paramref name="model"/>, modelled) side in the loadpull layout the
    /// Data Display's contours read (<c>Pout_dBm</c>, <c>Gt_dB</c>, <c>PAE</c> over <c>[gridPoint, pinStep]</c>, with
    /// <c>GammaLoad</c>, <c>ZLoad</c> and <c>PavlDbm</c>) — so the window draws it through the existing loadpull contour
    /// plots. Null for an item that is not a loadpull.
    /// </summary>
    public static DataSet? LoadpullLayout(DataSet item, bool model)
    {
        string side = model ? "model" : "meas";
        if (!item.Contains("GammaLoad") || !item.Contains("PavlDbm") || !item.Contains($"Pout.{side}")) return null;
        var gamma = item["GammaLoad"];
        var pavl = item["PavlDbm"];
        var pout = item[$"Pout.{side}"];
        var ds = new DataSet();
        ds.Add("GammaLoad", gamma);
        ds.Add("ZLoad", new DataCube([.. gamma.Axes], [.. gamma.ComplexValues.Select(g => 50 * (1 + g) / (1 - g))]));
        ds.Add("PavlDbm", pavl);
        ds.Add("Pout_dBm", pout);
        ds.Add("Gt_dB", new DataCube([.. pout.Axes], [.. pout.RealValues.Select((p, k) => p - pavl.RealValues[k])]));
        if (item.Contains($"PAE.{side}")) ds.Add("PAE", item[$"PAE.{side}"]);
        return ds;
    }

    internal static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}
