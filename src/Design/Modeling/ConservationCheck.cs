// Check Charge Conservation (brief-modelcomposer-8 R-mc8-5; overview D7): one function, three parts, each one number and,
// where it has one, a map.
//
//   (a) The data: per charge row, the curl ∂Ci1/∂vds − ∂Ci2/∂vgs of a smooth interpolant of the measured capacitances
//       ([P1] eq. 6.27) as a map over the intrinsic points and an RMS, plus the two-path line-integral difference
//       ([P1] eqs. 6.30/6.31) as a fraction of the charge's range.
//   (b) The model: the same curl on a device's own capacitance matrix, by finite differences on a fine grid. A composed
//       model reads ~0 by construction; the check is for every OTHER device — an SDD written with capacitances, a
//       compact model, an external device — any two-port nonlinear device instance in a .cnl.
//   (c) Large signal: the device driven at its terminals at f and then at 2f, through an identical trajectory. Conduction
//       DC is the same at both; DC from a non-conservative capacitance is f·∮C·dv ([P1] Fig. 6.21), so the change in DC
//       gate current isolates it without separating conduction from displacement inside a black box.
//
// A transcapacitance (Cm = C21 − C12 ≠ 0) is information, never a failure ([P1] §6.3.8). Long work goes through
// RunControl; a cancelled check returns nothing.

using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Modeling;
using CircuitRF.Engine.Modeling.Ann;

namespace CircuitRF.Design.Modeling;

/// <summary>Check (c)'s drive (R-mc8-5).</summary>
public sealed record LargeSignalSettings
{
    /// <summary>The bias cursor, intrinsic volts. Null for the composer's model takes the middle of its data; a device in a
    /// <c>.cnl</c> must be given one.</summary>
    public (double Vgs, double Vds)? Bias { get; init; }
    /// <summary>The gate drive's amplitude, V.</summary>
    public double Amplitude { get; init; } = 0.2;
    /// <summary>
    /// The drain drive's amplitude, V, in quadrature with the gate's. With the drain held at a constant voltage the
    /// trajectory is a line in (vgs, vds), every loop integral along it is zero, and no capacitance — conservative or
    /// not — makes DC; an ellipse encloses area, and the same ellipse at every frequency makes the DC exactly
    /// proportional to f.
    /// </summary>
    public double DrainAmplitude { get; init; } = 1.0;
    public double FrequencyHz { get; init; } = 1e9;
    /// <summary>The harmonic-balance order (at least 5).</summary>
    public int Order { get; init; } = 7;
    /// <summary>The gate bias resistor the consequence line is stated through, Ω.</summary>
    public double GateResistance { get; init; } = 10e3;
    /// <summary>The harmonic-balance tolerance, A. A charge-based device converges to 1e-12, which resolves a spurious DC
    /// far below the threshold; a model with trap or dispersion state nodes stops near 1e-8 (their 1 mF nodes, TrapModel),
    /// so the composer's check drives such a model at <see cref="ConservationCheck.StateNodeTolerance"/>.</summary>
    public double Tolerance { get; init; } = 1e-12;
}

/// <summary>Check (a): each row's normalised curl at each data point and its RMS, and the two-path difference as a fraction
/// of the charge's range. A point within <see cref="ConservationCheck.EdgeMargin"/> of the data's hull has no reading:
/// NaN in the maps, and outside the RMS.</summary>
public sealed record DataConservation(IReadOnlyList<(double Vgs, double Vds)> Points, double[] GateMap, double[] DrainMap,
                                      double GateRms, double DrainRms, double GatePaths, double DrainPaths);

/// <summary>Check (b): each row's normalised curl over the grid (<c>vds</c> fastest) and its RMS; the transcapacitance's RMS
/// in farads and relative to <c>C11</c>'s — information only.</summary>
public sealed record ModelConservation(double[] Vgs, double[] Vds, double[] GateMap, double[] DrainMap, double GateRms,
                                       double DrainRms, double Transcapacitance, double TranscapacitanceRelative);

/// <summary>One drive of check (c): the DC gate current, the gate current's fundamental amplitude, the DC drain current.
/// Into the device, amps.</summary>
public sealed record DriveCurrents(double GateDc, double GateFundamental, double DrainDc)
{
    /// <summary>The gate current's magnitude at DC and each harmonic, amps — the spectrum the Check step draws ([P1] Fig.
    /// 6.21's picture, MC-15).</summary>
    public IReadOnlyList<double> GateSpectrum { get; init; } = [];
}

/// <summary>Check (c): the drives at f and 2f; <see cref="Spurious"/> = <c>Igate,dc(2f) − Igate,dc(f)</c> — the DC a
/// non-conservative capacitance makes at f — and that relative to the fundamental gate current at f.</summary>
public sealed record LargeSignalConservation(double FrequencyHz, DriveCurrents AtF, DriveCurrents At2F, double Spurious,
                                             double Relative, double DrainSpurious, string Line);

public sealed record ConservationReport(DataConservation? Data, ModelConservation? Model, LargeSignalConservation? LargeSignal,
                                        IReadOnlyList<string> Lines);

public static class ConservationCheck
{
    /// <summary>The model check's grid step, V.</summary>
    public const double DefaultStep = 0.05;

    // ── (a) the data ──────────────────────────────────────────────────────────

    /// <summary>Check (a) on the capacitance data a charge fit trains on.</summary>
    public static DataConservation Data(ChargeData data, (double Vgs, double Vds)? anchor = null,
                                        RegridSource interpolant = RegridSource.ANN, RunControl? rc = null)
    {
        rc?.ThrowIfCancellationRequested();
        var poly = data.Domain.Polygon;
        double gLo = poly.Min(p => p.Vgs), gHi = poly.Max(p => p.Vgs), dLo = poly.Min(p => p.Vds), dHi = poly.Max(p => p.Vds);
        double hg = 1e-3 * (gHi - gLo), hd = 1e-3 * (dHi - dLo);
        var at = anchor ?? ChargeFit.Anchor(data, ChargeFitSettings.Default, []);

        // The curl is read only away from the hull's edge. A value interpolant's derivative ACROSS its edge is
        // constrained from one side only: on exactly conservative depletion data over a rectilinear grid the network read
        // 0.43 % at interior points and 54 % on the edge — a third of a grid's points — which made the RMS a false
        // failure. Edge points map to NaN (no reading); a hull with no interior point is read everywhere.
        var interior = Enumerable.Range(0, data.X.Count).Where(k => !NearEdge(poly, data.X[k], gHi - gLo, dHi - dLo)).ToList();
        if (interior.Count == 0) interior = [.. Enumerable.Range(0, data.X.Count)];
        var sites = interior.Select(k => data.X[k]).ToList();

        (double[] Map, double Rms, double Paths) Row(bool gate)
        {
            rc?.BeginStage(gate ? "Checking Qg's data" : "Checking Qd's data");
            var (c1, c2) = data.Row(gate);
            var ann = new AnnTrainingSettings { MaxIterations = ChargeConstituentSettings.DefaultMaxIterations, Restarts = 1, HeldOutFraction = 0 };
            var f1 = ChargeFit.Interpolant(c1, interpolant, data, ann, rc);
            var f2 = ChargeFit.Interpolant(c2, interpolant, data, ann, rc);
            var read = CurlMetric.Of((g, d) => (f1(g, d), f2(g, d)), sites, hg, hd);
            var map = Enumerable.Repeat(double.NaN, data.X.Count).ToArray();
            for (int i = 0; i < interior.Count; i++) map[interior[i]] = read.Map[i];
            var curl = (Map: map, read.Rms);

            double[] vgs = Regrid.Axis(gLo, gHi, ChargeConstituentSettings.DefaultGridStep);
            double[] vds = Regrid.Axis(dLo, dHi, ChargeConstituentSettings.DefaultGridStep);
            double typical = Math.Sqrt(c1.Concat(c2).Average(v => v * v));
            var (a, b) = LineIntegral.OntoGrid(f1, f2, at, vgs, vds, 1e-7 * typical * ChargeConstituentSettings.DefaultGridStep, rc);
            var inside = new List<int>();
            for (int i = 0; i < vgs.Length; i++)
                for (int j = 0; j < vds.Length; j++)
                    if (ChargeFit.Inside(poly, (vgs[i], vds[j]))) inside.Add(i * vds.Length + j);
            // A hull narrower than the grid's step can hold no site; then there is no two-path number to give.
            double range = inside.Count > 0 ? inside.Max(k => 0.5 * (a[k] + b[k])) - inside.Min(k => 0.5 * (a[k] + b[k])) : 0;
            double paths = range > 0 ? Math.Sqrt(inside.Average(k => (a[k] - b[k]) * (a[k] - b[k]))) / range : 0;
            return (curl.Map, curl.Rms, paths);
        }

        var g = Row(true);
        var d = Row(false);
        return new DataConservation(data.X, g.Map, d.Map, g.Rms, d.Rms, g.Paths, d.Paths);
    }

    /// <summary>Within <see cref="EdgeMargin"/> of the hull's boundary, distances measured in each axis' span.</summary>
    private static bool NearEdge(IReadOnlyList<(double Vgs, double Vds)> poly, (double Vgs, double Vds) p, double gSpan, double dSpan)
    {
        double sg = gSpan > 0 ? gSpan : 1, sd = dSpan > 0 ? dSpan : 1;
        for (int k = 0; k < poly.Count; k++)
        {
            var a = poly[k]; var b = poly[(k + 1) % poly.Count];
            double ax = a.Vgs / sg, ay = a.Vds / sd, ex = b.Vgs / sg - ax, ey = b.Vds / sd - ay;
            double px = p.Vgs / sg - ax, py = p.Vds / sd - ay, len = ex * ex + ey * ey;
            double t = len > 0 ? Math.Clamp((px * ex + py * ey) / len, 0, 1) : 0;
            if (Math.Sqrt(Math.Pow(px - t * ex, 2) + Math.Pow(py - t * ey, 2)) < EdgeMargin) return true;
        }
        return false;
    }

    /// <summary>Check (a)'s edge band, as a fraction of each axis' span.</summary>
    public const double EdgeMargin = 0.02;

    // ── (b) the model ─────────────────────────────────────────────────────────

    /// <summary>Check (b) on a capacitance matrix given as a function, over a grid of <paramref name="step"/> covering the
    /// rectangle, keeping the sites <paramref name="keep"/> accepts.</summary>
    public static ModelConservation Model(Func<double, double, (double C11, double C12, double C21, double C22)> capacitance,
                                          (double VgsLo, double VgsHi, double VdsLo, double VdsHi) region, double step = DefaultStep,
                                          Func<double, double, bool>? keep = null, RunControl? rc = null)
    {
        rc?.ThrowIfCancellationRequested();
        double[] vgs = Regrid.Axis(region.VgsLo, region.VgsHi, step), vds = Regrid.Axis(region.VdsLo, region.VdsHi, step);
        var sites = new List<(double, double)>();
        foreach (double g in vgs) foreach (double d in vds) if (keep?.Invoke(g, d) ?? true) sites.Add((g, d));
        if (sites.Count == 0) throw new ModelingException("Check", "the model check's region holds no grid site.");
        // Differences a tenth of the grid's step apart: the grid says where to look, and a central difference's own error
        // (h²/6 of a third derivative, different in the two terms) falls a hundredfold below the step's.
        rc?.BeginStage("Checking the model");
        double h = step / 10;
        var gate = CurlMetric.Of((g, d) => { var c = capacitance(g, d); return (c.C11, c.C12); }, sites, h, h);
        rc?.ThrowIfCancellationRequested();
        var drain = CurlMetric.Of((g, d) => { var c = capacitance(g, d); return (c.C21, c.C22); }, sites, h, h);

        double cm = 0, c11 = 0;
        foreach (var (g, d) in sites)
        {
            var c = capacitance(g, d);
            cm += Math.Pow(c.C21 - c.C12, 2);
            c11 += c.C11 * c.C11;
        }
        cm = Math.Sqrt(cm / sites.Count);
        c11 = Math.Sqrt(c11 / sites.Count);
        return new ModelConservation([.. sites.Select(s => s.Item1)], [.. sites.Select(s => s.Item2)], gate.Map, drain.Map,
                                     gate.Rms, drain.Rms, cm, c11 > 0 ? cm / c11 : 0);
    }

    /// <summary>Check (b) on a device instance in a <c>.cnl</c> (<see cref="DeviceCapacitance"/>), at
    /// <paramref name="frequencyHz"/> for a device whose capacitance passes through internal state ports.</summary>
    public static ModelConservation Model(string cnlPath, string instance,
                                          (double VgsLo, double VgsHi, double VdsLo, double VdsHi) region, double step = DefaultStep,
                                          double frequencyHz = 1e9, RunControl? rc = null)
    {
        var (lib, tb, dir) = Bench(cnlPath, instance, "Vdc:VG g 0 V=0\nVdc:VD d 0 V=0\n");
        using var nl = new Elaborator(lib) { BaseDirectory = dir }.Elaborate(tb);
        var device = new DeviceCapacitance(nl, instance, frequencyHz);
        return Model(device.At, region, step, null, rc);
    }

    // ── (c) large signal ──────────────────────────────────────────────────────

    /// <summary>Check (c) on a device instance in a <c>.cnl</c>: the drive at f and at 2f.</summary>
    public static LargeSignalConservation LargeSignal(string cnlPath, string instance, LargeSignalSettings settings, RunControl? rc = null)
    {
        if (settings.Order < 5) throw new ModelingException("Check", $"order {settings.Order}; the large-signal check runs HB at order 5 or more.");
        double f = settings.FrequencyHz;
        rc?.BeginStage("Driving at f", 2, "drive(s)");
        var atF = Drive(cnlPath, instance, settings, f);
        rc?.TickStage();
        rc?.ThrowIfCancellationRequested();
        var at2F = Drive(cnlPath, instance, settings, 2 * f);
        rc?.TickStage();
        rc?.ThrowIfCancellationRequested();

        double spurious = at2F.GateDc - atF.GateDc;
        double relative = atF.GateFundamental > 0 ? Math.Abs(spurious) / atF.GateFundamental : 0;
        string line = $"spurious {Si(spurious, "A")} at the gate → {Si(spurious * settings.GateResistance, "V")} " +
                      $"through a {Si(settings.GateResistance, "Ω")} gate bias resistor";
        return new LargeSignalConservation(f, atF, at2F, spurious, relative, at2F.DrainDc - atF.DrainDc, line);
    }

    /// <summary>
    /// One drive: the device on an intrinsic-only testbench, its gate at <c>Vgs + A·cos ωt</c> and its drain at
    /// <c>Vds + B·sin ωt</c> (both through 1 mΩ, which keeps the harmonic-balance interface finite and moves the
    /// trajectory by ~10⁻⁵ of itself), solved by harmonic balance at <paramref name="frequencyHz"/>.
    /// </summary>
    public static DriveCurrents Drive(string cnlPath, string instance, LargeSignalSettings s, double frequencyHz)
    {
        var bias = s.Bias ?? throw new ModelingException("Check", "the large-signal check needs a bias.");
        string f = R(frequencyHz);
        string sources =
            $"V_1Tone:VGCHK chk_vg 0 Vdc={R(bias.Vgs)} Freq={f} V={R(s.Amplitude)}\nIProbe:IGCHK chk_vg chk_ga\nR:RGCHK chk_ga g R=1e-3\n" +
            $"V_1Tone:VDCHK chk_vd 0 Vdc={R(bias.Vds)} Freq={f} V={R(s.DrainAmplitude)} Phase=-90\nIProbe:IDCHK chk_vd chk_da\nR:RDCHK chk_da d R=1e-3\n" +
            $"analysis HBCHK type=hb Tone={f} MaxHarm={s.Order} Tol={R(s.Tolerance)}\n";
        var (lib, tb, dir) = Bench(cnlPath, instance, sources);
        using var nl = new Elaborator(lib) { BaseDirectory = dir }.Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
        var p = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var result = new HbEngine(nl, tb).Run(p);
        if (!result.Converged)
            throw new ModelingException("Check", $"harmonic balance did not converge at {Si(frequencyHz, "Hz")}; lower the drive.");
        var cube = result["I"];
        var labels = cube.Axes[0].Labels!;
        int Branch(string name) => Array.FindIndex(labels, l => l == name || l.EndsWith("." + name, StringComparison.Ordinal)) is var k and >= 0
            ? k : throw new ModelingException("Check", $"the drive's probe {name} is not in the result.");
        int ig = Branch("IGCHK"), id = Branch("IDCHK");
        var spectrum = Enumerable.Range(0, cube.Axes[1].Length).Select(h => ((Complex)cube[ig, h]).Magnitude).ToArray();
        return new DriveCurrents(((Complex)cube[ig, 0]).Real, ((Complex)cube[ig, 1]).Magnitude, ((Complex)cube[id, 0]).Real)
        {
            GateSpectrum = spectrum,
        };
    }

    // ── The composer's model ──────────────────────────────────────────────────

    /// <summary>
    /// All three parts for a <c>.cmc</c>: (a) on its charge data, (b) on the fitted charges (over the data's hull), (c) on
    /// the fitted charges and currents written as a model of the core alone. Needs the charge step's record.
    /// Read-only: the model it drives is written to a temporary folder and removed.
    /// </summary>
    public static ConservationReport RunFile(string cmcPath, LargeSignalSettings settings, RunControl? rc = null)
    {
        var doc = CmcFile.Read(cmcPath);
        var data = new CmcCache(cmcPath).Open(doc, rc).Data;
        var intrinsic = new IntrinsicExtraction(cmcPath).Run(doc, data, rc: rc);
        var charges = ChargeFit.Load(cmcPath, doc)
            ?? throw new ModelingException("Check", "there is no fitted charge in the document; fit the charges first.");
        var current = CurrentFit.Load(cmcPath, doc);
        // Typed charges have no capacitances to read (a) on; (b) and (c) read them over the I–V points (R-mc18-4).
        var cd = charges.Record.Typed is not null
            ? ChargeFit.TypedRegion(doc, data, intrinsic)
            : ChargeFit.Gather(doc, data, intrinsic, charges.Settings, []);
        return Run(cd, charges, current, settings, rc);
    }

    /// <summary>
    /// Check (c) on any two-port device instance in a <c>.cnl</c> (R-mc8-5's applicability) — and check (b) over
    /// <paramref name="region"/> when one is given, since a <c>.cnl</c> states no data hull to take it from. The bias is
    /// required: a device outside a document has no data whose middle to take.
    /// </summary>
    public static ConservationReport RunDevice(string cnlPath, string instance, LargeSignalSettings settings,
                                               (double VgsLo, double VgsHi, double VdsLo, double VdsHi)? region = null,
                                               RunControl? rc = null)
    {
        if (settings.Bias is null) throw new ModelingException("Check", "the large-signal check needs a bias for a device in a .cnl.");
        var lines = new List<string>();
        ModelConservation? b = null;
        if (region is { } r)
        {
            b = Model(cnlPath, instance, r, DefaultStep, settings.FrequencyHz, rc);
            lines.Add($"model: curl {Pct(b.GateRms)} (Qg), {Pct(b.DrainRms)} (Qd)");
            lines.Add($"transcapacitance Cm {Si(b.Transcapacitance, "F")} RMS ({Pct(b.TranscapacitanceRelative)} of C11)");
        }
        var c = LargeSignal(cnlPath, instance, settings, rc);
        lines.Add($"large signal: {c.Line} ({Pct(c.Relative)} of the fundamental)");
        return new ConservationReport(null, b, c, lines);
    }

    /// <summary>The spurious DC of check (c), relative to the fundamental gate current, above which the check fails (D16's
    /// exit 3). A charge-based device reads ~1e-20; a gate current written as capacitances times dV/dt with a planted
    /// curl reads 0.4 % (ConservationCheckLargeSignalTests).</summary>
    public const double DefaultThreshold = 1e-3;

    /// <summary>Check (c)'s tolerance for a model with trap or dispersion state nodes, A: above their residual floor, and
    /// against the default drive's fundamental gate current (~0.5 mA on a 0.4 pF gate at 1 GHz) a resolution of 2e-4,
    /// below <see cref="DefaultThreshold"/>.</summary>
    public const double StateNodeTolerance = 1e-7;

    /// <summary>
    /// The report in one line, each part's number (MC-15 R-mc15-6): <c>Data gate 0.4 % · drain 3.8 % · Model 0.0 % · HB 0.2 µA</c>
    /// — the data's and the model's curl RMS and the large-signal check's spurious DC gate current. A part not run is left out.
    /// </summary>
    public static string Summary(ConservationReport report) => string.Join(" · ", SummaryLines(report));

    /// <summary>The summary one part to a line — the Check step's rows: the data's gate and drain, the model, HB.</summary>
    public static IReadOnlyList<string> SummaryLines(ConservationReport report)
    {
        var parts = new List<string>();
        if (report.Data is { } a) { parts.Add($"Data gate {Pct(a.GateRms)}"); parts.Add($"Data drain {Pct(a.DrainRms)}"); }
        if (report.Model is { } b) parts.Add($"Model {Pct(Math.Max(b.GateRms, b.DrainRms))}");
        if (report.LargeSignal is { } c) parts.Add($"HB {Si(Math.Abs(c.Spurious), "A")}");
        return parts;
    }

    /// <summary>Whether <paramref name="report"/>'s large-signal check is above <paramref name="threshold"/> — D16's failed check.</summary>
    public static bool Exceeds(ConservationReport report, double threshold = DefaultThreshold) =>
        report.LargeSignal is { } c && c.Relative > threshold;

    /// <summary><see cref="RunFile"/> on data and fitted steps already in hand.</summary>
    public static ConservationReport Run(ChargeData data, ChargeFitResult charges, CurrentFitResult? current,
                                         LargeSignalSettings settings, RunControl? rc = null)
    {
        // Without the currents a drift charge reads Id = 0 everywhere and checks a model it is not.
        if (charges.Record.Qg.Method == Constituent.DriftMethod && current is null)
            throw new ModelingException("Check", "the drift charge reads the fitted Id, and the currents are not fitted (or their cache is gone); fit the currents first.");
        var lines = new List<string>();
        // (a) reads a curl across the bias plane, which a handful of points — or points along a line — do not span (R-mc17-4):
        // not run, and not a failure.
        DataConservation? a = null;
        bool typed = charges.Record.Typed is not null;
        bool surface = !typed && data.X.Count >= ChargeFit.SurfaceMinimum && data.SpansArea;
        if (typed)
        {
            lines.Add("data: not run — the charges are typed; there is no S data.");
        }
        else if (surface)
        {
            a = Data(data, charges.Record.Anchor, rc: rc);
            lines.Add($"data: curl {Pct(a.GateRms)} (Qg), {Pct(a.DrainRms)} (Qd); paths differ {Pct(a.GatePaths)}, {Pct(a.DrainPaths)}");
        }
        else
        {
            lines.Add($"data: not run — {data.X.Count} bias point(s); the data's curl needs at least {ChargeFit.SurfaceMinimum} across the bias plane.");
        }

        var model = ChargeFit.Model(charges, current, data.TemperatureC);
        var program = ComposedCoreProgram.Compile(model, "check");
        var poly = data.Domain.Polygon;
        // Read away from the hull's edge, as (a) is: beyond it each charge is a linear continuation, and a curl read across
        // the boundary measures the continuation rather than the fit. Fewer points, and typed charges, carry only equations,
        // defined everywhere, so the model is read over a volt either side of the points.
        double gLo = poly.Min(p => p.Vgs), gHi = poly.Max(p => p.Vgs), dLo = poly.Min(p => p.Vds), dHi = poly.Max(p => p.Vds);
        double gSpan = gHi - gLo, dSpan = dHi - dLo;
        var b = surface
            ? Model((g, d) => ChargeFit.Capacitances(program, g, d, data.TemperatureC), (gLo, gHi, dLo, dHi),
                    DefaultStep, (g, d) => ChargeFit.Inside(poly, (g, d)) && !NearEdge(poly, (g, d), gSpan, dSpan), rc)
            : Model((g, d) => ChargeFit.Capacitances(program, g, d, data.TemperatureC), (gLo - 1, gHi + 1, dLo - 1, dHi + 1),
                    DefaultStep, null, rc);
        lines.Add($"model: curl {Pct(b.GateRms)} (Qg), {Pct(b.DrainRms)} (Qd)");
        lines.Add($"transcapacitance Cm {Si(b.Transcapacitance, "F")} RMS ({Pct(b.TranscapacitanceRelative)} of C11)");

        string dir = Directory.CreateTempSubdirectory("crf-mc8-check-").FullName;
        try
        {
            CmodelFile.Write(model, Path.Combine(dir, "model.cmodel"));
            string cnl = Path.Combine(dir, "check.cnl");
            File.WriteAllText(cnl, "ComposedFET:X1 g d s File=model.cmodel\n");
            var s = settings with
            {
                Bias = settings.Bias ?? CurrentFit.Middle([.. data.X.Select(p => new[] { p.Vgs, p.Vds })]),
                Tolerance = model.Traps is not null || model.Core.Dispersion is not null
                    ? Math.Max(settings.Tolerance, StateNodeTolerance) : settings.Tolerance,
            };
            var c = LargeSignal(cnl, "X1", s, rc);
            lines.Add($"large signal: {c.Line} ({Pct(c.Relative)} of the fundamental)");
            return new ConservationReport(a, b, c, lines);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ── The intrinsic-only testbench ──────────────────────────────────────────

    /// <summary>
    /// The user's library and globals with a testbench of <paramref name="sources"/> and the one device: its gate, drain
    /// and source nets bound to <c>g</c>, <c>d</c> and ground, its other nets kept (they are its own). The terminals are
    /// an SDD's ports 1 and 2 (<c>g s d s</c>) or a FET's pins (<c>g d s</c>).
    /// </summary>
    private static (Library Library, TestBench Bench, string Directory) Bench(string cnlPath, string instance, string sources)
    {
        string full = Path.GetFullPath(cnlPath);
        var (lib, user) = CnlReader.ReadFile(full);
        var inst = user.Instances.FirstOrDefault(i => i.InstanceName == instance)
            ?? throw new ModelingException("Check", $"'{instance}' is not an instance at the top of {Path.GetFileName(full)}.");
        var nets = inst.NetBindings;
        bool sdd = inst.Reference == "SDD";
        if (nets.Count < (sdd ? 4 : 3))
            throw new ModelingException("Check", $"'{instance}' has {nets.Count} nets; a two-port device has gate, drain and source.");
        string gate = nets[0], drain = sdd ? nets[2] : nets[1], source = sdd ? nets[1] : nets[2];
        if (sdd && nets[3] != source)
            throw new ModelingException("Check", $"'{instance}''s two ports do not share a source net ({source}, {nets[3]}).");
        string Map(string n) => n == gate ? "g" : n == drain ? "d" : n == source ? "0" : n;

        string dir = Path.GetDirectoryName(full)!;
        var (_, bench) = new CnlReader().Read(sources, "check", dir);
        bench.GlobalVariables.AddRange(user.GlobalVariables);
        bench.Functions.AddRange(user.Functions);
        bench.Instances.Add(new Instance(inst.InstanceName, inst.Reference, nets.Select(Map), inst.Overrides)
        {
            RefNetBinding = inst.RefNetBinding is { } r ? Map(r) : null,
        });
        return (lib, bench, dir);
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A fraction as a percentage to two decimals, three significant figures above 10 %; below 0.01 % it is
    /// "&lt; 0.01 %" — a residual that small is zero for the purpose, and its digits would only be noise.</summary>
    internal static string Pct(double v)
    {
        double p = 100 * Math.Abs(v);
        if (!double.IsFinite(p)) return "— %";
        if (p < 0.005) return "< 0.01 %";
        return (p < 10 ? p.ToString("0.00", CultureInfo.InvariantCulture) : p.ToString("G3", CultureInfo.InvariantCulture)) + " %";
    }

    /// <summary>A value with an SI prefix, three significant figures: <c>12.3 µA</c>.</summary>
    internal static string Si(double v, string unit)
    {
        if (!(Math.Abs(v) >= 1e-18)) return $"0 {unit}";
        string[] prefixes = ["a", "f", "p", "n", "µ", "m", "", "k", "M", "G"];
        int e = Math.Clamp((int)Math.Floor(Math.Log10(Math.Abs(v)) / 3), -6, 3);
        return (v / Math.Pow(10, 3 * e)).ToString("G3", CultureInfo.InvariantCulture) + " " + prefixes[e + 6] + unit;
    }
}

/// <summary>
/// The capacitance matrix of any two-port nonlinear device, as check (b) reads it: the one nonlinear component an
/// instance elaborates to, its ports 1 and 2 taken as gate–source and drain–source, any further ports its own internal
/// state nodes. At each bias the state nodes are solved at DC, the device is linearised, its internal ports are
/// eliminated at the check's frequency, and <c>C = Im Y / ω</c> — so a capacitance written through an auxiliary node
/// (an SDD's <c>C·dv/dt</c>) is read like a charge's <c>∂q/∂v</c>, which is all a charge-based device returns.
/// </summary>
public sealed class DeviceCapacitance
{
    private readonly ElaboratedComponent _c;
    private readonly int _n;
    private readonly double _w;
    private readonly double[] _state;

    public DeviceCapacitance(ElaboratedNetlist nl, string instance, double frequencyHz)
    {
        var found = nl.Components.Where(c => c.IsNonlinear &&
            (c.InstancePath == instance || c.InstancePath.StartsWith(instance + ".", StringComparison.Ordinal))).ToList();
        if (found.Count != 1)
            throw new ModelingException("Check", $"'{instance}' holds {found.Count} nonlinear components; the model check reads one.");
        _c = found[0];
        _n = _c.Model.PortCount;
        if (_n < 2) throw new ModelingException("Check", $"'{_c.InstancePath}' has {_n} port; a FET has a gate and a drain port.");
        if (_c.Nodes[1] != _c.Nodes[3])
            throw new ModelingException("Check", $"'{_c.InstancePath}''s first two ports do not share a source.");
        var own = new HashSet<int>();
        for (int p = 2; p < _n; p++) { own.Add(_c.Nodes[2 * p]); own.Add(_c.Nodes[2 * p + 1]); }
        own.Remove(0);
        foreach (var other in nl.Components)
            if (!ReferenceEquals(other, _c) && other.Nodes.Any(own.Contains))
                throw new ModelingException("Check",
                    $"'{_c.InstancePath}''s state ports reach '{other.InstancePath}'; ports after the second must be its own nodes.");
        _w = 2 * Math.PI * frequencyHz;
        _state = new double[_n];
    }

    /// <summary>(C11, C12, C21, C22) at an intrinsic bias.</summary>
    public (double C11, double C12, double C21, double C22) At(double vgs, double vds)
    {
        var v = new double[_n];
        v[0] = vgs; v[1] = vds;
        for (int p = 2; p < _n; p++) v[p] = _state[p];
        var r = _c.Evaluate(new PortVoltages(v));
        int m = _n - 2;
        for (int iter = 0; iter < 50 && m > 0; iter++)
        {
            double size = 0;
            for (int p = 2; p < _n; p++) size = Math.Max(size, Math.Abs(r.I[p]));
            if (size < 1e-15) break;
            var a = new Complex[m, m];
            var rhs = new Complex[m];
            for (int p = 0; p < m; p++)
            {
                rhs[p] = -r.I[p + 2];
                for (int q = 0; q < m; q++) a[p, q] = r.Dg[p + 2, q + 2];
            }
            var dx = Solve(a, rhs);
            for (int p = 0; p < m; p++) v[p + 2] += dx[p].Real;
            r = _c.Evaluate(new PortVoltages(v));
        }
        for (int p = 2; p < _n; p++) _state[p] = v[p];

        // Y = G + jωC over every port, the state ports eliminated: Y_ee − Y_ei·Y_ii⁻¹·Y_ie.
        var y = new Complex[_n, _n];
        for (int p = 0; p < _n; p++)
            for (int q = 0; q < _n; q++) y[p, q] = new Complex(r.Dg[p, q], _w * r.Dc[p, q]);
        var c = new double[2, 2];
        for (int col = 0; col < 2; col++)
        {
            var ie = new Complex[m];
            for (int p = 0; p < m; p++) ie[p] = y[p + 2, col];
            var x = m > 0 ? Solve(Sub(y, m), ie) : [];
            for (int row = 0; row < 2; row++)
            {
                Complex e = y[row, col];
                for (int p = 0; p < m; p++) e -= y[row, p + 2] * x[p];
                c[row, col] = e.Imaginary / _w;
            }
        }
        return (c[0, 0], c[0, 1], c[1, 0], c[1, 1]);
    }

    private static Complex[,] Sub(Complex[,] y, int m)
    {
        var s = new Complex[m, m];
        for (int p = 0; p < m; p++) for (int q = 0; q < m; q++) s[p, q] = y[p + 2, q + 2];
        return s;
    }

    /// <summary>Gaussian elimination with partial pivoting; the systems here are a handful of state ports.</summary>
    private static Complex[] Solve(Complex[,] a, Complex[] b)
    {
        int n = b.Length;
        a = (Complex[,])a.Clone();
        b = (Complex[])b.Clone();
        for (int k = 0; k < n; k++)
        {
            int piv = k;
            for (int i = k + 1; i < n; i++) if (a[i, k].Magnitude > a[piv, k].Magnitude) piv = i;
            if (a[piv, k] == Complex.Zero)
                throw new ModelingException("Check", "a state port of the device has no conductance; its state is undefined.");
            if (piv != k)
            {
                for (int j = 0; j < n; j++) (a[k, j], a[piv, j]) = (a[piv, j], a[k, j]);
                (b[k], b[piv]) = (b[piv], b[k]);
            }
            for (int i = k + 1; i < n; i++)
            {
                var f = a[i, k] / a[k, k];
                for (int j = k; j < n; j++) a[i, j] -= f * a[k, j];
                b[i] -= f * b[k];
            }
        }
        var x = new Complex[n];
        for (int i = n - 1; i >= 0; i--)
        {
            var s = b[i];
            for (int j = i + 1; j < n; j++) s -= a[i, j] * x[j];
            x[i] = s / a[i, i];
        }
        return x;
    }
}
