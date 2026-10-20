// Temperature and self-heating (brief-modelcomposer-9 R-mc9-1 … R-mc9-5; overview D8, D9, D13).
//
//   1. The plan: the data's distinct ambient temperatures. Two or more make T an input of every constituent and make
//      self-heating available; one gives a single-temperature model valid at T0, and the reason is the only text.
//   2. Rth ([P1] §6.6, [R11] §2.8). An isothermal Id(vgs, vds, T) is trained on pulsed I–V at two or more chuck
//      temperatures (from quiescent biases that dissipate nothing). For each DC point the junction temperature Tj is the
//      T at which that model gives the DC current; Tj − Tamb = Rth·P is then fitted by least squares — Rth constant, or
//      linear in the ambient. Points with too little power to resolve ΔT are left out and counted. Or Rth is typed, and
//      the current step "de-heats" the DC data by moving each point to its Tj.
//   3. The dynamics: Cth typed, a τth typed (Cth = τth/Rth), a Foster ladder typed as shares of Rth and time constants,
//      or τth fitted to the low-frequency roll-off of Re Y22 ([P1] Fig. 6.34) — offered only while traps are not
//      modelled, because in GaN the same roll-off also holds trap time constants ([N22] §III).
//
// The result goes to the .cmc's Steps.Thermal. Cancelled, nothing is written.

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Design.Modeling;

/// <summary>A run of the thermal step: what the document records, and the settings it ran with.</summary>
public sealed record ThermalFitResult(ThermalFitSettings Settings, ThermalFitRecord Record);

/// <summary>
/// The data's temperatures (D8, R-mc9-4): two or more make <c>T</c> an input and self-heating available; one is a
/// single-temperature model at <see cref="T0"/>.
/// </summary>
public sealed record TemperaturePlan(IReadOnlyList<double> Temperatures)
{
    /// <summary>The only text a single-temperature document shows for self-heating.</summary>
    public const string OneTemperature = "one temperature";

    public bool IsSingle => Temperatures.Count < 2;
    /// <summary>The one temperature, °C (25 when the data states none).</summary>
    public double T0 => Temperatures.Count > 0 ? Temperatures[0] : 25.0;
    public bool HasTemperatureInput => !IsSingle;
    public bool SelfHeatingAvailable => !IsSingle;
    /// <summary>Why self-heating is unavailable, or null.</summary>
    public string? SelfHeatingUnavailable => IsSingle ? OneTemperature : null;
}

/// <summary>What the current step needs from the thermal step: whether DC points sit at their junction temperatures, and
/// the thermal resistance (typed or extracted) that puts them there.</summary>
public sealed record ThermalHeating(bool SelfHeating, double? Rth, double RthSlope = 0, double Tref = 25)
{
    public static ThermalHeating None { get; } = new(false, null);

    /// <summary>The thermal resistance at an ambient, K/W; 0 while it is unknown.</summary>
    public double RthAt(double ambientC) => Rth is { } r ? r + RthSlope * (ambientC - Tref) : 0.0;
}

public static class ThermalFit
{
    public const string StepName = "Thermal";

    /// <summary>The τth fit's tooltip: in GaN the low-frequency roll-off may hold trap time constants too.</summary>
    public const string TauFitNote = "The fitted corner may include trapping.";

    // ── The document ──────────────────────────────────────────────────────────

    public static ThermalFitSettings Settings(CmcDocument doc) =>
        Step(doc)?["Settings"] is { } s ? ThermalFitSettings.Parse(s, $"Steps.{StepName}.Settings") : ThermalFitSettings.Default;

    public static ThermalFitRecord? Record(CmcDocument doc) =>
        Step(doc)?["Result"] is { } r ? ThermalFitRecord.Parse(r, $"Steps.{StepName}.Result") : null;

    /// <summary>The block's keys checked and parsed — <see cref="CmcFile.Validate"/>'s call.</summary>
    public static void Validate(CmcDocument doc)
    {
        if (Step(doc) is not { } o) return;
        CmcExtrinsic.Only(o, $"Steps.{StepName}", ["Network", "Settings", "Result", CmcStamps.DataKey]);
        Settings(doc);
        Record(doc);
    }

    public static CmcDocument WithSettings(CmcDocument doc, ThermalFitSettings settings)
    {
        var steps = StepsCopy(doc);
        var o = steps[StepName] as JsonObject ?? new JsonObject();
        steps[StepName] = o;
        o["Settings"] = settings.ToJson();
        return doc with { Steps = steps };
    }

    public static CmcDocument Apply(CmcDocument doc, ThermalFitResult result)
    {
        var steps = StepsCopy(doc);
        steps[StepName] = new JsonObject { ["Settings"] = result.Settings.ToJson(), ["Result"] = result.Record.ToJson() };
        return doc with { Steps = steps };
    }

    private static JsonObject? Step(CmcDocument doc) =>
        doc.Steps is JsonObject s && s[StepName] is { } n ? CmcExtrinsic.Obj(n, $"Steps.{StepName}") : null;

    private static JsonObject StepsCopy(CmcDocument doc) =>
        doc.Steps is JsonObject s ? (JsonObject)JsonNode.Parse(s.ToJsonString())! : new JsonObject();

    /// <summary>Whether this document models self-heating, and with what Rth: a typed one wins over a recorded one.</summary>
    public static ThermalHeating Heating(CmcDocument doc)
    {
        var s = Settings(doc);
        if (s.Rth is { } typed) return new ThermalHeating(s.SelfHeating, typed);
        return Record(doc) is { Rth: { } rth } r ? new ThermalHeating(s.SelfHeating, rth, r.RthSlope, r.Tref)
                                                 : new ThermalHeating(s.SelfHeating, null);
    }

    /// <summary>Reads the <c>.cmc</c>, runs the step with its settings and writes the document — what the window and the
    /// <c>model</c> verb both call. Cancelled, nothing is written.</summary>
    public static ThermalFitResult FitFile(string cmcPath, RunControl? rc = null)
    {
        var doc = CmcFile.Read(cmcPath);
        var data = new CmcCache(cmcPath).Open(doc, rc).Data;
        var intrinsic = new IntrinsicExtraction(cmcPath).Run(doc, data, rc: rc);
        var result = Run(doc, data, intrinsic, Settings(doc), rc);
        rc?.ThrowIfCancellationRequested();
        CmcFile.Write(cmcPath, CmcStamps.Stamp(Apply(doc, result), StepName, CmcStamps.DataDigest(cmcPath, doc)));
        return result;
    }

    // ── The plan (R-mc9-4) ────────────────────────────────────────────────────

    /// <summary>The distinct ambient temperatures of the document's I–V and S data.</summary>
    public static TemperaturePlan Plan(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data) =>
        new(CurrentFit.Temperatures(doc.DataSets.Where(s => s.Kind is DataSetKind.DcIv or DataSetKind.PulsedIv or DataSetKind.SParams
                                                                   or DataSetKind.PulsedSParams or DataSetKind.SimulatedTables), data));

    /// <summary>
    /// The <c>Temperature</c> block a model of these constituents carries (D8): <c>Single</c> at <c>T0</c> for one
    /// temperature; otherwise <c>Range</c> over the plan's temperatures and every temperature a constituent was trained at
    /// (a self-heated point's junction temperature lies above its ambient).
    /// </summary>
    public static ModelTemperature ModelTemperature(TemperaturePlan plan, IEnumerable<Constituent> constituents)
    {
        if (plan.IsSingle) return Core.Devices.Composed.ModelTemperature.Single(plan.T0);
        double lo = plan.Temperatures[0], hi = plan.Temperatures[^1];
        foreach (var c in constituents)
            if (c.Domain?.Box.FirstOrDefault(b => b.Key == ComposedCoreProgram.T) is { Key: not null } b)
                (lo, hi) = (Math.Min(lo, b.Value.Lo), Math.Max(hi, b.Value.Hi));
        return Core.Devices.Composed.ModelTemperature.Range(lo, hi);
    }

    /// <summary>The thermal network a model carries, from a recorded step; null without self-heating or before its
    /// dynamics are set.</summary>
    public static ModelThermal? Network(ThermalFitRecord? record) =>
        record is { Rth: not null, Poles.Count: > 0 } r ? new ModelThermal(r.Poles) { RthSlope = r.RthSlope, Tref = r.Tref } : null;

    // ── The run ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the thermal step (R-mc9-2 … R-mc9-4). Throws <see cref="ModelingException"/> for a refusal and
    /// <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static ThermalFitResult Run(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data, IntrinsicResult intrinsic,
                                       ThermalFitSettings settings, RunControl? rc = null)
    {
        rc?.ThrowIfCancellationRequested();
        var plan = Plan(doc, data);
        var report = new List<string>();
        if (!settings.SelfHeating)
            return new ThermalFitResult(settings, new ThermalFitRecord { Temperatures = plan.Temperatures });
        if (plan.IsSingle)
            throw new ModelingException(StepName, $"self-heating is unavailable: {TemperaturePlan.OneTemperature}.");

        double rth, slope = 0, tref = 25;
        string source;
        ThermalExtractionRecord? extraction = null;
        if (settings.Rth is { } typed)
        {
            (rth, source) = (typed, ThermalFitRecord.Typed);
        }
        else
        {
            var e = ExtractRth(doc, data, intrinsic, settings, report, rc);
            (rth, slope, tref, extraction, source) = (e.Rth, e.Slope, e.Tref, e.Record, ThermalFitRecord.Extracted);
        }

        TauFitRecord? tauFit = null;
        double? tau = settings.TauTh;
        if (settings.FitTau)
        {
            if (TrapsModelled(doc))
                throw new ModelingException(StepName, "τth is not fitted while traps are modelled: the roll-off holds trap time constants too.");
            tauFit = FitTau(TauCurves(doc, intrinsic, settings));
            tau = tauFit.Tau;
            report.Add($"τth {G(tauFit.Tau)} s from the Re Y22 roll-off of {tauFit.Points} S point(s), misfit {G(100 * tauFit.Residual)} %.");
        }

        IReadOnlyList<ThermalPole> poles =
            settings.Ladder.Count > 0 ? [.. settings.Ladder.Select(l => new ThermalPole(l.Fraction * rth, l.Tau / (l.Fraction * rth)))]
            : settings.Cth is { } cth ? [new ThermalPole(rth, cth)]
            : tau is { } t            ? [new ThermalPole(rth, t / rth)]
            : [];
        if (poles.Count == 0) report.Add("Cth is not set: type Cth, τth or a ladder, or fit τth.");

        var record = new ThermalFitRecord
        {
            Temperatures = plan.Temperatures, Rth = rth, RthSlope = slope, Tref = tref, RthSource = source, Poles = poles,
            TauTh = poles.Count == 1 ? poles[0].Rth * poles[0].Cth : null, Extraction = extraction, TauFit = tauFit, Report = report,
        };
        return new ThermalFitResult(settings, record);
    }

    /// <summary>Whether the document models traps (MC-10).</summary>
    internal static bool TrapsModelled(CmcDocument doc) => TrapFit.Modelled(doc);

    // ── Rth (R-mc9-3) ─────────────────────────────────────────────────────────

    private sealed record Extraction(double Rth, double Slope, double Tref, ThermalExtractionRecord Record);

    private static Extraction ExtractRth(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data, IntrinsicResult intrinsic,
                                         ThermalFitSettings settings, List<string> report, RunControl? rc)
    {
        CmcDataSet Named(string n, string what) => doc.DataSets.FirstOrDefault(s => s.Name == n)
            ?? throw new ModelingException(n, $"the thermal step names it as {what}, and the document has no such data set.");

        var pulsed = settings.PulsedSets.Count > 0
            ? [.. settings.PulsedSets.Select(n => Named(n, "a pulsed set"))]
            : doc.DataSets.Where(s => s.Kind == DataSetKind.PulsedIv && CurrentFit.QuiescentPower(s) is not > 0).ToList();
        foreach (var s in pulsed.Where(s => CurrentFit.QuiescentPower(s) is > 0))
            throw new ModelingException(s.Name, "it pulses from a dissipating quiescent bias, so it is not isothermal at its chuck temperature.");
        if (CurrentFit.Temperatures(pulsed, data).Count < 2)
            throw new ModelingException(StepName, "extracting Rth needs pulsed I–V at two or more chuck temperatures; type Rth instead.");
        var dcSets = settings.DcSets.Count > 0
            ? [.. settings.DcSets.Select(n => Named(n, "a DC set"))]
            : doc.DataSets.Where(s => s.Kind == DataSetKind.DcIv).ToList();
        if (dcSets.Count == 0) throw new ModelingException(StepName, "extracting Rth needs DC I–V at the pulsed biases.");

        // The isothermal model: the channel current as a function of (vgs, vds, T), from the pulsed sets alone.
        var current = CurrentFit.Settings(doc);
        rc?.BeginStage("Training the isothermal Id");
        var iso = CurrentFit.IsothermalId(doc, data, intrinsic, [.. pulsed.Select(s => s.Name)], settings.Isothermal ?? current.Id,
                                          report, rc);

        // Each DC point's junction temperature, where the isothermal model gives its current.
        var points = new List<(double Vgs, double Vds, double P, double Tamb, double Rise)>();
        var all = new List<(double Vgs, double Vds, double Ig, double Id, double Tamb)>();
        foreach (var set in dcSets)
        {
            if (!intrinsic.Sets.TryGetValue(set.Name, out var ds))
                throw new ModelingException(set.Name, "it has no intrinsic data; de-embed it first.");
            double[] vgs = ds["Vgsi"].RealValues, vds = ds["Vdsi"].RealValues, ig = ds["Ig"].RealValues,
                     id = ds["Id"].RealValues, ex = ds["Excluded"].RealValues;
            var pts = data.TryGetValue(set.Name, out var d) && d.Points.Count == vgs.Length ? d.Points : null;
            for (int k = 0; k < vgs.Length; k++)
            {
                if (ex[k] != 0 || !double.IsFinite(vgs[k]) || !double.IsFinite(vds[k]) || double.IsNaN(id[k])) continue;
                all.Add((vgs[k], vds[k], double.IsNaN(ig[k]) ? 0 : ig[k], id[k], CurrentFit.AmbientOf(set, pts?[k])));
            }
        }
        double pMax = all.Count > 0 ? all.Max(p => CurrentFit.Power(p.Vgs, p.Vds, p.Ig, p.Id)) : 0;
        if (!(pMax > 0)) throw new ModelingException(StepName, "the DC data dissipates no power, so it carries no temperature rise.");
        int low = 0, unresolved = 0;
        rc?.BeginStage("Finding junction temperatures", all.Count, "point(s)");
        foreach (var p in all)
        {
            rc?.ThrowIfCancellationRequested();
            rc?.TickStage();
            double power = CurrentFit.Power(p.Vgs, p.Vds, p.Ig, p.Id);
            if (power < settings.MinPower * pMax) { low++; continue; }
            double channel = p.Id + CurrentFit.GateDrainBranch(p.Ig, p.Vds, current.GateSplit);
            if (JunctionTemperature(iso, p.Vgs, p.Vds, channel, p.Tamb) is { } tj) points.Add((p.Vgs, p.Vds, power, p.Tamb, tj - p.Tamb));
            else unresolved++;
        }
        int need = settings.RthModel == RthModel.Linear ? 3 : 2;
        if (points.Count < need)
            throw new ModelingException(StepName,
                $"{points.Count} DC point(s) resolve a temperature rise ({low} below {G(100 * settings.MinPower)} % of the largest power, " +
                $"{unresolved} unresolved); the Rth fit needs at least {need}.");

        // Tj − Tamb = Rth·P, through the origin; or (Rth + slope·(Tamb − Tref))·P.
        double tref = points.Min(p => p.Tamb);
        double rth, slope = 0;
        if (settings.RthModel == RthModel.Constant)
            rth = points.Sum(p => p.P * p.Rise) / points.Sum(p => p.P * p.P);
        else
        {
            double a11 = 0, a12 = 0, a22 = 0, b1 = 0, b2 = 0;
            foreach (var p in points)
            {
                double u = p.P, w = p.P * (p.Tamb - tref);
                a11 += u * u; a12 += u * w; a22 += w * w; b1 += u * p.Rise; b2 += w * p.Rise;
            }
            double det = a11 * a22 - a12 * a12;
            if (!(Math.Abs(det) > 1e-12 * a11 * a22))
                throw new ModelingException(StepName, "the DC data is at one ambient, so a thermal resistance linear in the ambient is not determined.");
            rth = (b1 * a22 - b2 * a12) / det;
            slope = (a11 * b2 - a12 * b1) / det;
        }
        if (!(rth > 0)) throw new ModelingException(StepName, $"the fit gives Rth = {G(rth)} °C/W, which is not a thermal resistance.");

        double ss = 0, size = 0;
        foreach (var p in points)
        {
            double r = p.Rise - (rth + slope * (p.Tamb - tref)) * p.P;
            ss += r * r;
            size += p.Rise * p.Rise;
        }
        double residual = Math.Sqrt(ss / points.Count), relative = size > 0 ? Math.Sqrt(ss / size) : 0;
        var record = new ThermalExtractionRecord(residual, relative, points.Count, low, unresolved,
            (points.Min(p => p.Vgs), points.Max(p => p.Vgs)), (points.Min(p => p.Vds), points.Max(p => p.Vds)),
            (points.Min(p => p.P), points.Max(p => p.P)));
        report.Add($"Rth {G(rth)} °C/W from {points.Count} DC point(s), residual {G(residual)} K; " +
                   $"{low} below {G(100 * settings.MinPower)} % of the largest power left out, {unresolved} unresolved.");
        return new Extraction(rth, slope, tref, record);
    }

    /// <summary>
    /// The temperature at which the isothermal model gives <paramref name="target"/> at a bias: the sign change nearest
    /// above the ambient on a 5 K scan (from 50 K below it to 500 K above), then bisection to a millikelvin. Null when the
    /// model never reaches the target there.
    /// </summary>
    internal static double? JunctionTemperature(Func<double[], (double F, double Dg, double Dd)> iso, double vgs, double vds,
                                                double target, double tAmb)
    {
        double G(double t) => iso([vgs, vds, t]).F - target;
        const double step = 5;
        double lo = tAmb - 50, glo = G(lo);
        for (double hi = lo + step; hi <= tAmb + 500 + 1e-9; hi += step)
        {
            double ghi = G(hi);
            if (glo == 0) return lo;
            if (Math.Sign(ghi) != Math.Sign(glo))
            {
                double a = lo, b = hi, ga = glo;
                while (b - a > 1e-3)
                {
                    double m = 0.5 * (a + b), gm = G(m);
                    if (Math.Sign(gm) == Math.Sign(ga)) { a = m; ga = gm; } else b = m;
                }
                return 0.5 * (a + b);
            }
            (lo, glo) = (hi, ghi);
        }
        return null;
    }

    // ── τth from Re Y22 (R-mc9-3) ─────────────────────────────────────────────

    /// <summary>Each chosen S point's Re Y22 over frequency, its excluded points left out.</summary>
    private static List<(double[] F, double[] ReY22)> TauCurves(CmcDocument doc, IntrinsicResult intrinsic, ThermalFitSettings settings)
    {
        var sets = settings.TauSets.Count > 0
            ? [.. settings.TauSets.Select(n => doc.DataSets.FirstOrDefault(s => s.Name == n)
                                               ?? throw new ModelingException(n, "the τth fit names it, and the document has no such data set."))]
            : doc.DataSets.Where(s => s.Kind == DataSetKind.SParams).ToList();
        var curves = new List<(double[], double[])>();
        foreach (var set in sets)
        {
            if (!intrinsic.Sets.TryGetValue(set.Name, out var ds) || !ds.Contains("Y"))
                throw new ModelingException(set.Name, $"the τth fit reads its intrinsic Y22, and {IntrinsicExtraction.NoAdmittance(ds)}");
            var y = ds["Y"];
            double[] f = y.Axes[1].Values, ex = ds["Excluded"].RealValues;
            var values = y.ComplexValues;
            int nf = f.Length, np = ex.Length;
            for (int p = 0; p < np; p++)
            {
                if (ex[p] != 0) continue;
                var re = new double[nf];
                for (int k = 0; k < nf; k++) re[k] = values[((p * nf + k) * 2 + 1) * 2 + 1].Real;
                if (re.All(double.IsFinite)) curves.Add((f, re));
            }
        }
        if (curves.Count == 0) throw new ModelingException(StepName, "there is no intrinsic S data to fit τth to.");
        return curves;
    }

    /// <summary>
    /// One τth for every curve <c>Re Y22(f) = a + b/(1 + (2πfτ)²)</c> — each curve its own level <c>a</c> and roll-off
    /// height <c>b</c>, solved exactly for each trial τ — searched in log τ: a 61-point scan over the decade below the
    /// lowest frequency's corner to the decade above the highest's, then golden-section refinement. The residual is the RMS
    /// misfit relative to the RMS roll-off height.
    /// </summary>
    public static TauFitRecord FitTau(IReadOnlyList<(double[] F, double[] ReY22)> curves)
    {
        if (curves.Count == 0) throw new ModelingException(StepName, "there is no Re Y22 to fit τth to.");
        double fLo = curves.Min(c => c.F.Where(x => x > 0).DefaultIfEmpty(double.NaN).Min());
        double fHi = curves.Max(c => c.F.Max());
        if (!(fLo > 0 && fHi > fLo)) throw new ModelingException(StepName, "the τth fit needs Re Y22 over a span of frequencies.");
        double uLo = Math.Log(1 / (2 * Math.PI * fHi * 10)), uHi = Math.Log(10 / (2 * Math.PI * fLo));

        (double Cost, double Height) Cost(double u)
        {
            double tau = Math.Exp(u), cost = 0, height = 0;
            foreach (var (f, y) in curves)
            {
                // Linear least squares for (a, b) against the basis (1, g), g = 1/(1 + (ωτ)²).
                double s1 = 0, sg = 0, sgg = 0, sy = 0, sgy = 0;
                int n = f.Length;
                for (int k = 0; k < n; k++)
                {
                    double w = 2 * Math.PI * f[k] * tau, g = 1 / (1 + w * w);
                    s1 += 1; sg += g; sgg += g * g; sy += y[k]; sgy += g * y[k];
                }
                double det = s1 * sgg - sg * sg;
                double a, b;
                if (Math.Abs(det) > 1e-14 * s1 * sgg) { a = (sy * sgg - sg * sgy) / det; b = (s1 * sgy - sg * sy) / det; }
                else { a = sy / s1; b = 0; }
                for (int k = 0; k < n; k++)
                {
                    double w = 2 * Math.PI * f[k] * tau, r = y[k] - a - b / (1 + w * w);
                    cost += r * r;
                }
                height += b * b;
            }
            return (cost, height);
        }

        int best = 0;
        var scan = new double[61];
        for (int k = 0; k < scan.Length; k++) scan[k] = Cost(uLo + (uHi - uLo) * k / 60).Cost;
        for (int k = 1; k < scan.Length; k++) if (scan[k] < scan[best]) best = k;
        double lo = uLo + (uHi - uLo) * Math.Max(best - 1, 0) / 60, hi = uLo + (uHi - uLo) * Math.Min(best + 1, 60) / 60;
        double phi = (Math.Sqrt(5) - 1) / 2;
        double x1 = hi - phi * (hi - lo), x2 = lo + phi * (hi - lo), c1 = Cost(x1).Cost, c2 = Cost(x2).Cost;
        for (int it = 0; it < 80 && hi - lo > 1e-9; it++)
        {
            if (c1 < c2) { hi = x2; x2 = x1; c2 = c1; x1 = hi - phi * (hi - lo); c1 = Cost(x1).Cost; }
            else { lo = x1; x1 = x2; c1 = c2; x2 = lo + phi * (hi - lo); c2 = Cost(x2).Cost; }
        }
        double uBest = 0.5 * (lo + hi);
        var (cost, h) = Cost(uBest);
        int points = curves.Sum(c => c.F.Length);
        return new TauFitRecord(Math.Exp(uBest), h > 0 ? Math.Sqrt(cost / points) / Math.Sqrt(h / curves.Count) : 0, curves.Count);
    }

    private static string G(double v) => v.ToString("G3", CultureInfo.InvariantCulture);
}
