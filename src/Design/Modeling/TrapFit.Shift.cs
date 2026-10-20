// The shift coupling's strengths (brief-modelcomposer-19 R-mc19-3 … R-mc19-5).
//
// Under Shift the core evaluates Id₀(vgs − ΔV, vds, T), ΔV = a_d·(φ2 − vds) + a_g·(vgs − φ1). Every CW S frequency lies far
// above 1/τ_emit, so the states are frozen at their DC values there, and the small-signal conductances are
//
//     gm_RF  = ∂Id/∂vgs|φ = gm₀·(1 − ∂ΔV/∂vgs) = (1 − a_g)·gm₀
//     gds_RF = ∂Id/∂vds|φ = gds₀ − gm₀·∂ΔV/∂vds = gds₀ + a_d·gm₀
//
// with gm₀ and gds₀ the derivatives of the fitted trap-free Id₀ at the point (isothermal at its junction temperature when
// the model self-heats, since the S stimulus is above the thermal corner too). Each relation is linear in one strength
// through the origin, so each is one least-squares slope: a = Σ x·y / Σ x² with x = gm₀ and y = gds_RF − gds₀ (a_d) or
// gm₀ − gm_RF (a_g), its standard error √(Σr²/(n − 1) / Σx²). The RF conductances are read exactly as the dispersion fit
// reads them — Re Y21 and Re Y22 of the intrinsic S, averaged over the flatness band, flagged frequencies left out — and a
// point whose gm₀ is below IdFloor of the largest is left out, as the current fit's weighting floors it. A typed strength
// is not fitted; with no S and an untyped enabled state the step refuses, naming both ways out.

using System.Globalization;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Design.Modeling;

/// <summary>One CW S point as the shift fit reads it: its bias (measured and intrinsic), the temperature its gm₀ and gds₀ are
/// read at, the RF conductances and the trap-free model's.</summary>
public sealed record ShiftPoint(string Set, int Point, double Vgs, double Vds, double Vgsi, double Vdsi, double T,
                                double GmRf, double GdsRf, double Gm0, double Gds0);

public static partial class TrapFit
{
    /// <summary>The coupling's two strengths as <c>CaptureEmission</c> names them, with the state each belongs to.</summary>
    private static readonly (string Name, string State)[] Strengths =
        [(CaptureEmissionTraps.ShiftDrain, CaptureEmissionTraps.DrainLag), (CaptureEmissionTraps.ShiftGate, CaptureEmissionTraps.GateLag)];

    /// <summary>
    /// The <c>.cmodel</c>'s <c>Traps</c> block under a shift coupling — the settings with the step's strengths — or null when
    /// the document models no traps or trains them. What export writes, since the current step's record carries no block then.
    /// </summary>
    public static ModelTraps? ShiftBlock(CmcDocument doc)
    {
        var s = Settings(doc);
        if (!s.Modelled || !s.Shifted) return null;
        var r = Record(doc) ?? throw new ModelingException(StepName, "traps are modelled and the step has not been run.");
        var block = Block(s);
        foreach (string state in block.States)
            if (TrapModels.Find(s.Model)!.ShiftParameter(state) is { } name && !r.Strengths.ContainsKey(name))
                throw new ModelingException(StepName, $"the step has no {name} for {Label(s.Model, state)}; run it again.");
        return Block(s, strengths: r.Strengths.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal));
    }

    /// <summary>The strengths a shift block carries, by name; empty for any other block.</summary>
    public static IReadOnlyDictionary<string, double> StrengthsOf(ModelTraps block) =>
        block.Shifted ? Strengths.Where(st => block.Parameter(st.Name) is not null).ToDictionary(st => st.Name, st => block.Parameter(st.Name)!.Value)
                      : new Dictionary<string, double>();

    /// <summary>
    /// The CW S points with RF conductances, each with the trap-free <c>Id₀</c>'s gm₀ and gds₀ at its intrinsic bias — at
    /// its junction temperature <c>Tamb + Rth·P</c> when the model self-heats and Rth is known, else its ambient.
    /// </summary>
    public static IReadOnlyList<ShiftPoint> ShiftPoints(CmcDocument doc, IntrinsicResult intrinsic, CurrentFitResult current)
    {
        var program = TrapFreeProgram(current);
        var heating = ThermalFit.Heating(doc);
        var points = new List<ShiftPoint>();
        Span<double> seeds = stackalloc double[RuntimeEvaluator.MaxInputs], g = stackalloc double[RuntimeEvaluator.MaxInputs];
        foreach (var set in doc.DataSets.Where(s => s.Kind == DataSetKind.SParams))
        {
            if (!intrinsic.Sets.TryGetValue(set.Name, out var ds) || !ds.Contains("Gm")) continue;
            double[] f = ds["Gm"].Axes[1].Values, band = ds["Band"].RealValues;
            double[] gm = ds["Gm"].RealValues, gds = ds["Gds"].RealValues, flagged = ds["Flagged"].RealValues;
            double[] vgs = ds["Vgsi"].RealValues, vds = ds["Vdsi"].RealValues, ex = ds["Excluded"].RealValues;
            double[] ig = ds["Ig"].RealValues, id = ds["Id"].RealValues;
            double[] mg = ds.Contains("Vgs") ? ds["Vgs"].RealValues : vgs, md = ds.Contains("Vds") ? ds["Vds"].RealValues : vds;
            int nf = f.Length;
            for (int p = 0; p < vgs.Length; p++)
            {
                if (ex[p] != 0 || !double.IsFinite(vgs[p]) || !double.IsFinite(vds[p])) continue;
                double sg = 0, sd = 0; int c = 0;
                for (int k = 0; k < nf; k++)
                {
                    if (f[k] < band[0] || f[k] > band[1] || flagged[p * nf + k] != 0) continue;
                    sg += gm[p * nf + k]; sd += gds[p * nf + k]; c++;
                }
                if (c == 0) continue;
                double t = set.Conditions.AmbientC ?? 25.0;
                if (heating.SelfHeating && heating.Rth is not null && double.IsFinite(id[p]))
                    t += heating.RthAt(t) * CurrentFit.Power(vgs[p], vds[p], double.IsNaN(ig[p]) ? 0 : ig[p], id[p]);
                program.DcSeeds(vgs[p], vds[p], t, seeds);
                program.Channel(seeds[..program.Width], g);
                points.Add(new ShiftPoint(set.Name, p, mg[p], md[p], vgs[p], vds[p], t, sg / c, sd / c, g[0], g[1]));
            }
        }
        return points;
    }

    /// <summary>The current step's <c>Id</c> and <c>Ig</c> compiled with zero charges and no traps: <c>Id₀</c>.</summary>
    private static ComposedCoreProgram TrapFreeProgram(CurrentFitResult current)
    {
        var zero = Constituent.Equation("0", EquationFit.VoltageInputs);
        double t0 = current.Record.Id.Domain?.Box.FirstOrDefault(b => b.Key == ComposedCoreProgram.T) is { Key: not null } b
            ? 0.5 * (b.Value.Lo + b.Value.Hi) : 25;
        return ComposedCoreProgram.Compile(ChargeFit.Model(zero, zero, new Dictionary<string, DataCube>(), current, t0), "shift fit");
    }

    private static TrapFitResult FitShift(CmcDocument doc, IntrinsicResult intrinsic, TrapFitSettings settings,
                                          CurrentFitResult? current, RunControl? rc)
    {
        var block = Block(settings);
        var report = new List<string>();
        var enabled = Strengths.Where(st => block.States.Contains(st.State)).ToList();
        var untyped = enabled.Where(st => !settings.Parameters.ContainsKey(st.Name)).ToList();

        if (current?.Record.Traps is not null)
            throw new ModelingException(StepName, "the currents were fitted with trained traps; fit them again for the shift coupling.");
        if (current is null && untyped.Count > 0)
            throw new ModelingException(StepName, $"{untyped[0].Name} is fitted against the trap-free Id the current step fits; fit the currents first, or type it.");

        var all = current is null ? [] : ShiftPoints(doc, intrinsic, current);
        rc?.ThrowIfCancellationRequested();
        double floor = CurrentFit.Settings(doc).IdFloor * (all.Count > 0 ? all.Max(p => p.Gm0) : 0);
        var points = all.Where(p => p.Gm0 > 0 && p.Gm0 >= floor).ToList();
        if (untyped.Count > 0 && points.Count < 2)
            throw new ModelingException(StepName, string.Join(" ", untyped.Select(st =>
                $"the {Label(settings.Model, st.State)} strength {st.Name} has {(all.Count == 0 ? "no S-parameters" : $"{points.Count} active S point(s)")} to be fitted to: type it, or add multi-bias S-parameters.")));
        if (all.Count > points.Count)
            report.Add($"{all.Count - points.Count} S point(s) with gm₀ below {G(100 * CurrentFit.Settings(doc).IdFloor)} % of its largest left out.");

        // Each strength from its own relation (the header's): x = gm₀, y the DC/RF difference it carries.
        double Y(ShiftPoint p, string name) => name == CaptureEmissionTraps.ShiftDrain ? p.GdsRf - p.Gds0 : p.Gm0 - p.GmRf;
        var strengths = new Dictionary<string, ShiftStrength>(StringComparer.Ordinal);
        foreach (var (name, state) in enabled)
        {
            string label = Label(settings.Model, state);
            if (settings.Parameters.TryGetValue(name, out double typed))
            {
                strengths[name] = new ShiftStrength(typed, null);
                report.Add($"{name} ({label}) {G(typed)} V/V, typed.");
                continue;
            }
            double sxy = 0, sxx = 0;
            foreach (var p in points) { sxy += p.Gm0 * Y(p, name); sxx += p.Gm0 * p.Gm0; }
            double a = sxy / sxx, rss = points.Sum(p => Math.Pow(Y(p, name) - a * p.Gm0, 2));
            double se = Math.Sqrt(rss / (points.Count - 1) / sxx);
            strengths[name] = new ShiftStrength(a, se);
            report.Add($"{name} ({label}) {G(a)} ± {G(se)} V/V from {points.Count} S point(s).");
        }

        ShiftResidual? residual = null;
        if (points.Count > 0)
        {
            double ad = strengths.GetValueOrDefault(CaptureEmissionTraps.ShiftDrain)?.Value ?? 0;
            double ag = strengths.GetValueOrDefault(CaptureEmissionTraps.ShiftGate)?.Value ?? 0;
            double Rms(Func<ShiftPoint, double> f) => Math.Sqrt(points.Average(p => f(p) * f(p)));
            residual = new ShiftResidual(points.Count, Rms(p => p.GmRf - (1 - ag) * p.Gm0), Rms(p => p.GdsRf - p.Gds0 - ad * p.Gm0));
            double gmSize = Rms(p => p.GmRf), gdsSize = Rms(p => p.GdsRf);
            report.Add($"RMS residual: gm {G(1e3 * residual.Gm)} mS ({G(100 * residual.Gm / gmSize)} % of RF gm), " +
                       $"gds {G(1e3 * residual.Gds)} mS ({G(100 * residual.Gds / gdsSize)} % of RF gds).");

            // R-mc19-3: the thermal part of the DC/RF difference is either taken out by Tj or left in a_d — said either way.
            var heating = ThermalFit.Heating(doc);
            if (heating.SelfHeating)
                report.Add(heating.Rth is null
                    ? "Rth is not yet extracted: gm₀ and gds₀ are read at the ambient, so the strengths hold the thermal DC/RF difference too."
                    : "gm₀ and gds₀ are isothermal at each point's junction temperature; the thermal DC/RF difference is not read as traps.");
            else if (untyped.Any(st => st.Name == CaptureEmissionTraps.ShiftDrain))
                report.Add($"self-heating is not modelled: {CaptureEmissionTraps.ShiftDrain} holds the thermal DC/RF difference too.");
        }

        // The block the strengths make, checked by the registry as the device will load it.
        Block(settings, strengths: strengths.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal));
        return new TrapFitResult(settings, new TrapFitRecord
        {
            Coupling = ModelTraps.Shift, Strengths = strengths, Residual = residual, Report = report,
        });
    }
}
