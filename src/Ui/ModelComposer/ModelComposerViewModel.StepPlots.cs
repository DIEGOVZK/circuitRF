// The model steps' plots (brief-modelcomposer-15 R-mc15-2 … R-mc15-7, R-mc15-9). Every plot is the plots panel's
// PlotControl fed from a phase's DataSet: the step's StepPlotData (ModelStepPlots, computed off the UI thread when the
// step's results or the data change), the last conservation check, or the last validation. Measured traces are symbols
// and model traces lines, in the Data Display's trace palette; a map is a contour trace over the square grid the phase
// wrote — the contour renderer's own shape. Nothing is computed here but the grouping of points into families.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design.Modeling;
using CircuitRF.Render.DataDisplay;
using CommunityToolkit.Mvvm.Input;
using RfCore;
using RfCore.Data;
using RfCore.Loadpull;

namespace CircuitRF.Ui.ModelComposer;

public sealed partial class ModelComposerViewModel
{
    public const string ErrorTab = "Error", GmTab = "Gm", GdsTab = "Gds", DomainTab = "Domain",
                        CvGateTab = "C–V (vgs)", CvDrainTab = "C–V (vds)", QgTab = "Qg", QdTab = "Qd", DQgTab = "ΔQg", DQdTab = "ΔQd",
                        TjTab = "Tj", DcIvTab = "DC I–V", PulsedTab = "Pulsed I–V",
                        CurlGateTab = "Curl Qg", CurlDrainTab = "Curl Qd", PathsGateTab = "Paths Qg", PathsDrainTab = "Paths Qd", SpectrumTab = "Spectrum",
                        IvTab = "I–V", PowerTab = "Power", LoadpullTab = "Loadpull", ImTab = "IM";

    /// <summary>The tabs of a model step, or null for a step whose tabs are MC-14's.</summary>
    private IReadOnlyList<string>? ModelStepTabs() => StepName switch
    {
        "Current" => [OutputTab, TransferTab, ErrorTab, GmTab, GdsTab, DomainTab],
        "Charge" => [CvGateTab, CvDrainTab, QgTab, QdTab, DQgTab, DQdTab],
        "Thermal" => [TjTab, DcIvTab],
        "Traps" => [PulsedTab, DcIvTab],
        "Check" => [CurlGateTab, CurlDrainTab, PathsGateTab, PathsDrainTab, SpectrumTab],
        "Validate" => [STab, SmithTab, IvTab, PowerTab, LoadpullTab, ImTab],
        "Export" => [BiasPlaneTab],
        _ => null,
    };

    // ── the step's plot data, off the UI thread ──────────────────────────────

    /// <summary>The selected step's plot data, or null before its fit.</summary>
    public StepPlotData? StepPlots { get; private set; }

    /// <summary>The plot data being computed, or null — what a test awaits.</summary>
    internal Task? PlotsTask { get; private set; }

    /// <summary>How many times a step's plot data was computed — a counter, for the gate.</summary>
    public int StepPlotBuilds { get; private set; }

    private string? _stepPlotsKey;
    private CancellationTokenSource? _plotsCts;

    /// <summary>
    /// Computes the selected step's plot data when its results or the data changed: <see cref="ModelStepPlots.Build"/> off the
    /// UI thread, the plots redrawn when it lands. Read-only, so it runs beside a task rather than in its place.
    /// </summary>
    private void RequestStepPlots()
    {
        if (!IsActive || IsChoosingKind || DocumentPath is not { } path || !ModelStepPlots.Steps.Contains(StepName))
        {
            _stepPlotsKey = null;
            if (StepPlots is not null) { StepPlots = null; OnPropertyChanged(nameof(StepPlots)); }
            return;
        }
        string results = Document.Steps is JsonObject steps
            ? string.Join("\u0001", steps.Select(kv => kv.Key + (kv.Value is JsonObject o ? o["Result"]?.ToJsonString() : "")))
            : "";
        string key = $"{StepName}\u0002{results}\u0002{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Loaded)}";
        if (key == _stepPlotsKey) return;
        _stepPlotsKey = key;
        try { _plotsCts?.Cancel(); } catch (ObjectDisposedException) { }
        var cts = new CancellationTokenSource();
        _plotsCts = cts;
        var doc = Document;
        var loaded = Loaded;
        string step = StepName;
        PlotsTask = Compute();

        async Task Compute()
        {
            object? r;
            try { r = await RunOffThread(() => ModelStepPlots.Build(path, doc, loaded, step, new CircuitRF.Engine.RunControl { Token = cts.Token }), cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is ModelingException or CmcFormatException or ArgumentException or InvalidOperationException
                                          or KeyNotFoundException or IndexOutOfRangeException)
            {
                r = null;
                if (key == _stepPlotsKey) PostToUi(() => Warn($"{step} plots: {ex.Message}"));
            }
            if (key != _stepPlotsKey) return;
            PostToUi(() =>
            {
                if (key != _stepPlotsKey) return;
                StepPlots = (StepPlotData?)r;
                StepPlotBuilds++;
                OnPropertyChanged(nameof(StepPlots));
                RebuildPlots();
            });
        }
    }

    // ── drawing ──────────────────────────────────────────────────────────────

    /// <summary>Draws the selected tab of a model step; false for a tab this partial does not draw.</summary>
    private bool ModelStepPlot(Plot plot)
    {
        if (ModelStepTabs() is null || IsPassive) return false;
        switch (StepName)
        {
            case "Current": CurrentPlot(plot); break;
            case "Charge": ChargePlot(plot); break;
            case "Thermal": ThermalPlot(plot); break;
            case "Traps": TrapsPlot(plot); break;
            case "Check": CheckPlot(plot); break;
            case "Validate": ValidatePlot(plot); break;
            default: return false;
        }
        return true;
    }

    /// <summary>The set of the plot data the cursor's temperature and quiescent point pick: the selected data set when it is
    /// one of them, else the first at the cursor's temperature and quiescent point, else the first.</summary>
    private string? CursorSet(IReadOnlyList<string> sets)
    {
        if (sets.Count == 0) return null;
        if (SelectedDataSet?.Name is { } chosen && sets.Contains(chosen)) return chosen;
        double? t = SelectedT?.Value;
        var q = SelectedQ?.Quiescent;
        return sets.FirstOrDefault(n => Document.DataSets.FirstOrDefault(s => s.Name == n) is { } s
                                         && (t is null || s.Conditions.AmbientC is not { } a || Math.Abs(a - t.Value) < 1e-6)
                                         && (q is null || s.Conditions.Quiescent is { } sq && Math.Abs(sq.Vgs - q.Vgs) < 1e-6 && Math.Abs(sq.Vds - q.Vds) < 1e-6))
               ?? sets[0];
    }

    private static double[] R(DataSet ds, string name) => ds.Contains(name) ? ds[name].RealValues : [];

    /// <summary>
    /// One overlay family per measured level: <paramref name="y"/> measured (symbols) and each of <paramref name="model"/>
    /// (lines) against <paramref name="x"/>, one member per distinct <paramref name="family"/> value. Returns the members drawn.
    /// </summary>
    private int Overlay(Plot plot, DataSet ds, string set, string family, string x, string meas, IReadOnlyList<(string Cube, string Tag, LineType Type)> model,
                        double scale, string familyName, Func<double, bool>? memberFilter = null)
    {
        double[] f = R(ds, $"{set}.{family}"), xs = R(ds, $"{set}.{x}"), ym = R(ds, $"{set}.{meas}");
        if (f.Length == 0 || xs.Length != f.Length) return 0;
        int c = 0;
        foreach (var g in Enumerable.Range(0, f.Length).GroupBy(k => Math.Round(f[k], 6)).OrderBy(g => g.Key))
        {
            if (memberFilter is not null && !memberFilter(g.Key)) continue;
            var pts = g.OrderBy(k => xs[k]).ToArray();
            string label = $"{familyName} {ModelValueFormat.G(g.Key)} V";
            if (ym.Length == f.Length)
                plot.Traces.Add(Xy(label, [.. pts.Select(k => xs[k])], [.. pts.Select(k => ym[k] * scale)], x, "V", c, line: false, marker: MarkerType.Circle));
            foreach (var (cube, tag, type) in model)
            {
                double[] ys = R(ds, $"{set}.{cube}");
                if (ys.Length != f.Length || pts.Length < 2) continue;
                var t = Xy($"{label} {tag}", [.. pts.Select(k => xs[k])], [.. pts.Select(k => ys[k] * scale)], x, "V", c, line: true, marker: null);
                t.Properties.LineType = type;
                plot.Traces.Add(t);
            }
            c++;
        }
        return c;
    }

    private static readonly IReadOnlyList<(string, string, LineType)> ModelLine = [("Id.model", "model", LineType.Solid)];

    private void CurrentPlot(Plot plot)
    {
        if (StepPlots is not { Step: "Current" } sp || CursorSet(sp.Sets) is not { } set) return;
        var ds = sp.Data;
        switch (SelectedPlotTab)
        {
            case OutputTab:
                plot.CustomTitle = $"{set} · Id(Vds) · model lines";
                Overlay(plot, ds, set, "Vgs", "Vds", "Id.meas", ModelLine, 1e3, "Vgs");
                Labels(plot, "Vds (V)", "Id (mA)");
                break;
            case TransferTab:
                plot.CustomTitle = $"{set} · Id(Vgs) · model lines";
                Overlay(plot, ds, set, "Vds", "Vgs", "Id.meas", ModelLine, 1e3, "Vds");
                Labels(plot, "Vgs (V)", "Id (mA)");
                break;
            case ErrorTab:
                plot.CustomTitle = $"{set} · model − data, % of range";
                if (ds.Contains($"{set}.ErrorMap")) MapTrace(plot, "error %", ds[$"{set}.ErrorMap"]);
                Labels(plot, "Vds (V)", "Vgs (V)");
                break;
            case GmTab:
            {
                double? vds = SelectedVds?.Value;
                plot.CustomTitle = $"{set} · Gm at Vds {ModelValueFormat.G(vds ?? 0)} V";
                Derivative(plot, ds, set, "Gm", "Vds", "Vgs", vds);
                Labels(plot, "Vgs (V)", "Gm (mS)");
                break;
            }
            case GdsTab:
            {
                double? vgs = SelectedVgs?.Value;
                plot.CustomTitle = $"{set} · Gds at Vgs {ModelValueFormat.G(vgs ?? 0)} V";
                Derivative(plot, ds, set, "Gds", "Vgs", "Vds", vgs);
                Labels(plot, "Vds (V)", "Gds (mS)");
                break;
            }
            case DomainTab:
            {
                plot.CustomTitle = "Training domain";
                foreach (string s in sp.Sets)
                {
                    double[] g = R(ds, $"{s}.Vgsi"), d = R(ds, $"{s}.Vdsi");
                    plot.Traces.Add(Scatter(s, [.. d.Zip(g)], 0, MarkerType.Circle, 5));
                }
                if (ds.Contains("Domain.vgs"))
                    plot.Traces.Add(Xy("domain", R(ds, "Domain.vds"), R(ds, "Domain.vgs"), "Vds", "V", 3, line: true, marker: null));
                Labels(plot, "Vds (V)", "Vgs (V)");
                break;
            }
        }
    }

    /// <summary>A conductance against its own voltage at the cursor's other voltage: the data's derivative (symbols), the model
    /// (line), and RF from the S sets (diamonds) when the currents are dispersive.</summary>
    private void Derivative(Plot plot, DataSet ds, string set, string q, string fixedName, string x, double? at)
    {
        Overlay(plot, ds, set, fixedName, x, $"{q}.data", [($"{q}.model", "model", LineType.Solid)], 1e3, fixedName,
                v => at is not { } a || Math.Abs(v - a) < 1e-6);
        foreach (var s in Document.DataSets.Where(s => ds.Contains($"{s.Name}.{q}.rf")))
        {
            double[] f = R(ds, $"{s.Name}.{fixedName}"), xs = R(ds, $"{s.Name}.{x}"), rf = R(ds, $"{s.Name}.{q}.rf"), m = R(ds, $"{s.Name}.{q}.rfmodel");
            var pts = Enumerable.Range(0, f.Length).Where(k => at is not { } a || Math.Abs(f[k] - a) < 1e-6).OrderBy(k => xs[k]).ToArray();
            if (pts.Length == 0) continue;
            plot.Traces.Add(Xy($"{s.Name} RF", [.. pts.Select(k => xs[k])], [.. pts.Select(k => rf[k] * 1e3)], x, "V", 4, line: false, marker: MarkerType.Diamond));
            plot.Traces.Add(Xy($"{s.Name} RF model", [.. pts.Select(k => xs[k])], [.. pts.Select(k => m[k] * 1e3)], x, "V", 4, line: true, marker: null));
        }
    }

    private void ChargePlot(Plot plot)
    {
        if (StepPlots is not { Step: "Charge" } sp) return;
        switch (SelectedPlotTab)
        {
            case CvGateTab: CvSlice(plot, sp, sweepGate: true); break;
            case CvDrainTab: CvSlice(plot, sp, sweepGate: false); break;
            case QgTab: Surface(plot, sp.Data, "Qg.model", "Qg (model)"); break;
            case QdTab: Surface(plot, sp.Data, "Qd.model", "Qd (model)"); break;
            case DQgTab: Surface(plot, sp.Data, "dQg", "Qg − line integral, % of range"); break;
            case DQdTab: Surface(plot, sp.Data, "dQd", "Qd − line integral, % of range"); break;
        }
    }

    /// <summary>The 1-D C–V slice at the cursor's fixed voltage (R-mc15-3): measured symbols, model lines.</summary>
    private void CvSlice(Plot plot, StepPlotData sp, bool sweepGate)
    {
        double fixedValue = (sweepGate ? SelectedVds?.Value : SelectedVgs?.Value) ?? 0;
        if (sp.Slice(sweepGate, fixedValue) is not { } ds) return;
        LastSlice = (sweepGate, fixedValue, ds);
        string swept = sweepGate ? "vgs" : "vds", fixedName = sweepGate ? "Vds" : "Vgs";
        plot.CustomTitle = $"C–V against {swept} · {fixedName} {ModelValueFormat.G(fixedValue)} V";
        IReadOnlyList<string> names = CvQuantity == "Circuit set" ? ["Cgs", "Cgd", "Cds", "Cm"] : [CvQuantity];
        double[] xm = R(ds, "x.meas");
        int c = 0;
        foreach (string n in names)
        {
            var model = ds[$"{n}.model"];
            plot.Traces.Add(Xy($"{n} model", model.Axes[0].Values, [.. model.RealValues.Select(v => v * 1e15)], swept, "V", c, line: true, marker: null));
            if (xm.Length > 0)
                plot.Traces.Add(Xy(n, xm, [.. R(ds, $"{n}.meas").Select(v => v * 1e15)], swept, "V", c, line: false, marker: MarkerType.Circle));
            c++;
        }
        Labels(plot, $"{swept} (V)", "C (fF)");
    }

    /// <summary>The last 1-D C–V slice drawn: which voltage it swept, the fixed value, and its data — for the gate.</summary>
    public (bool SweepGate, double Fixed, DataSet Data)? LastSlice { get; private set; }

    /// <summary>A charge surface or a ΔQ map as a contour over (vds, vgs).</summary>
    private void Surface(Plot plot, DataSet ds, string cube, string title)
    {
        plot.CustomTitle = title;
        if (ds.Contains(cube)) MapTrace(plot, title, ds[cube]);
        Labels(plot, "vds (V)", "vgs (V)");
    }

    private void ThermalPlot(Plot plot)
    {
        if (StepPlots is not { Step: "Thermal" } sp) return;
        var ds = sp.Data;
        if (SelectedPlotTab == TjTab)
        {
            plot.CustomTitle = "Tj − Ta against dissipated power";
            double[] p = R(ds, "P"), data = R(ds, "Rise.data"), model = R(ds, "Rise.model");
            if (p.Length == 0) return;
            var order = Enumerable.Range(0, p.Length).OrderBy(k => p[k]).ToArray();
            plot.Traces.Add(Xy("data", [.. order.Select(k => p[k])], [.. order.Select(k => data[k])], "P", "W", 0, line: false, marker: MarkerType.Circle));
            plot.Traces.Add(Xy("Rth", [.. order.Select(k => p[k])], [.. order.Select(k => model[k])], "P", "W", 1, line: true, marker: null));
            Labels(plot, "P (W)", "Tj − Ta (K)");
            return;
        }
        if (CursorSet(sp.Sets) is not { } set) return;
        plot.CustomTitle = $"{set} · with heating (solid), isothermal (dashed)";
        Overlay(plot, ds, set, "Vgs", "Vds", "Id.meas", [("Id.model", "heated", LineType.Solid), ("Id.iso", "isothermal", LineType.Dashed)], 1e3, "Vgs");
        Labels(plot, "Vds (V)", "Id (mA)");
    }

    private void TrapsPlot(Plot plot)
    {
        if (StepPlots is not { Step: "Traps" } sp) return;
        var ds = sp.Data;
        var pulsed = sp.Sets.Where(s => ds.Contains($"{s}.Setting")).ToList();
        if (SelectedPlotTab == PulsedTab)
        {
            double? vgs = SelectedVgs?.Value;
            plot.CustomTitle = $"Pulsed Id(Vds) at Vgs {ModelValueFormat.G(vgs ?? 0)} V, per trap-setting level";
            int c = 0;
            foreach (string set in pulsed)
            {
                double[] level = R(ds, $"{set}.Setting");
                string tag = double.IsFinite(level[0]) ? $"{ModelValueFormat.G(level[0])}, {ModelValueFormat.G(level[1])} V" : set;
                double[] f = R(ds, $"{set}.Vgs"), x = R(ds, $"{set}.Vds"), m = R(ds, $"{set}.Id.meas"), mo = R(ds, $"{set}.Id.model");
                var pts = Enumerable.Range(0, f.Length).Where(k => vgs is not { } a || Math.Abs(f[k] - a) < 1e-6).OrderBy(k => x[k]).ToArray();
                plot.Traces.Add(Xy(tag, [.. pts.Select(k => x[k])], [.. pts.Select(k => m[k] * 1e3)], "Vds", "V", c, line: false, marker: MarkerType.Circle));
                plot.Traces.Add(Xy($"{tag} model", [.. pts.Select(k => x[k])], [.. pts.Select(k => mo[k] * 1e3)], "Vds", "V", c, line: true, marker: null));
                // Beyond the trap-setting bias: validation only, drawn hollow (an open marker of the same colour).
                double[] bg = R(ds, $"{set}.Beyond.Vgs"), bx = R(ds, $"{set}.Beyond.Vds"), bi = R(ds, $"{set}.Beyond.Id");
                var beyond = Enumerable.Range(0, bg.Length).Where(k => vgs is not { } a || Math.Abs(bg[k] - a) < 1e-6).ToArray();
                if (beyond.Length > 0)
                {
                    var t = Xy($"{tag} beyond", [.. beyond.Select(k => bx[k])], [.. beyond.Select(k => bi[k] * 1e3)], "Vds", "V", c, line: false, marker: MarkerType.Circle);
                    t.Properties.MarkerSize = 7;
                    t.Properties.MarkerOpacity = 0;
                    plot.Traces.Add(t);
                }
                c++;
            }
            Labels(plot, "Vds (V)", "Id (mA)");
            return;
        }
        var dc = sp.Sets.Except(pulsed).ToList();
        if (CursorSet(dc) is not { } dcSet) return;
        plot.CustomTitle = $"{dcSet} · DC I–V as validation";
        Overlay(plot, ds, dcSet, "Vgs", "Vds", "Id.meas", ModelLine, 1e3, "Vgs");
        Labels(plot, "Vds (V)", "Id (mA)");
    }

    private void CheckPlot(Plot plot)
    {
        if (CheckPlots is not { } cp) return;
        var ds = cp.Data;
        switch (SelectedPlotTab)
        {
            case CurlGateTab: Surface(plot, ds, "Curl.Qg", "Qg row curl, %"); break;
            case CurlDrainTab: Surface(plot, ds, "Curl.Qd", "Qd row curl, %"); break;
            case PathsGateTab: Surface(plot, ds, "Paths.Qg", "Qg path A − path B, % of range"); break;
            case PathsDrainTab: Surface(plot, ds, "Paths.Qd", "Qd path A − path B, % of range"); break;
            case SpectrumTab:
            {
                plot.CustomTitle = "Gate current spectrum, drive at f and at 2f";
                int c = 0;
                foreach (string tag in new[] { "f", "2f" })
                {
                    if (!ds.Contains($"Spectrum.{tag}")) continue;
                    var s = ds[$"Spectrum.{tag}"];
                    var t = Xy($"drive at {tag}", s.Axes[0].Values, [.. s.RealValues.Select(v => v * 1e6)], "harmonic", "", c++, line: false, marker: MarkerType.Square);
                    t.Properties.DrawStyle = TraceDrawStyle.Bars;
                    plot.Traces.Add(t);
                }
                Labels(plot, "harmonic", "|Ig| (µA)");
                break;
            }
        }
    }

    private void ValidatePlot(Plot plot)
    {
        if (Validation is not { } v) return;
        var items = v.Items;
        switch (SelectedPlotTab)
        {
            case STab or SmithTab:
            {
                if ((items.FirstOrDefault(i => i.Kind == ValidationKind.SParams && (SelectedDataSet is null || i.Set == SelectedDataSet.Name))
                     ?? items.FirstOrDefault(i => i.Kind == ValidationKind.SParams)) is not { } item) return;
                var ds = item.Data;
                double[] vgs = R(ds, "Vgs"), vds = R(ds, "Vds");
                double cg = SelectedVgs?.Value ?? 0, cd = SelectedVds?.Value ?? 0;
                int k = Enumerable.Range(0, vgs.Length).MinBy(i => Math.Abs(vgs[i] - cg) + Math.Abs(vds[i] - cd));
                var meas = ds["S.meas"]; var model = ds["S.model"];
                double[] f = meas.Axes[1].Values;
                int nf = f.Length;
                plot.CustomTitle = $"{item.Set} · Vgs {ModelValueFormat.G(vgs[k])} V, Vds {ModelValueFormat.G(vds[k])} V";
                var pairs = IsSmithTab ? new[] { (0, 0), (1, 1) } : new[] { (0, 0), (0, 1), (1, 0), (1, 1) };
                int c = 0;
                foreach (var (i, j) in pairs)
                {
                    foreach (var (cube, tag) in new[] { (meas, ""), (model, " model") })
                    {
                        var s = new Complex[nf];
                        for (int q = 0; q < nf; q++) s[q] = cube.ComplexValues[((k * nf + q) * 2 + i) * 2 + j];
                        var t = CubeTrace($"S{i + 1}{j + 1}{tag}", Style(c, LineType.Solid, 1.0, tag.Length == 0 ? MarkerType.Circle : null));
                        t.Properties.LineEnabled = tag.Length > 0;
                        if (IsSmithTab) t.SetCubeData(f, s, null, "freq", "Hz", PlotType.Smith, FreqUnit.GHz);
                        else t.SetCubeData(f, null, [.. s.Select(x => 20 * Math.Log10(Math.Max(x.Magnitude, 1e-12)))], "freq", "Hz", PlotType.Rect, FreqUnit.GHz);
                        plot.Traces.Add(t);
                    }
                    c++;
                }
                if (!IsSmithTab) Labels(plot, null, "dB");
                break;
            }
            case IvTab:
            {
                if (items.FirstOrDefault(i => i.Kind is ValidationKind.DcIv or ValidationKind.PulsedIv) is not { } item) return;
                var ds = new DataSet();
                foreach (var (name, cube) in item.Data.Cubes) ds.Add($"{item.Set}.{name}", cube);
                bool dc = ds.Contains($"{item.Set}.Vds");
                plot.CustomTitle = $"{item.Set} · Id(Vds)";
                Overlay(plot, ds, item.Set, dc ? "Vgs" : "Vgsi", dc ? "Vds" : "Vdsi", "Id.meas", ModelLine, 1e3, "Vgs");
                Labels(plot, "Vds (V)", "Id (mA)");
                break;
            }
            case PowerTab or ImTab:
            {
                var kind = SelectedPlotTab == PowerTab ? ValidationKind.PowerSweep : ValidationKind.TwoTone;
                if (items.FirstOrDefault(i => i.Kind == kind) is not { } item) return;
                plot.CustomTitle = $"{item.Set} · against input power";
                int c = 0;
                foreach (string q in item.Data.Cubes.Keys.Where(n => n.EndsWith(".model", StringComparison.Ordinal)).Select(n => n[..^6]))
                {
                    var model = item.Data[$"{q}.model"];
                    double[] pin = model.Axes[0].Values;
                    if (item.Data.Contains($"{q}.meas"))
                        plot.Traces.Add(Xy(q, pin, item.Data[$"{q}.meas"].RealValues, "Pin", "dBm", c, line: false, marker: MarkerType.Circle));
                    plot.Traces.Add(Xy($"{q} model", pin, model.RealValues, "Pin", "dBm", c, line: true, marker: null));
                    c++;
                }
                Labels(plot, "Pin (dBm)", "");
                break;
            }
            case LoadpullTab:
            {
                if (items.FirstOrDefault(i => i.Kind == ValidationKind.Loadpull) is not { } item) return;
                plot.CustomTitle = $"{item.Set} · Pout, measured against model";
                int c = 0;
                foreach (bool model in (bool[])[false, true])
                {
                    if (ModelStepPlots.LoadpullLayout(item.Data, model) is not { } lp) continue;
                    var t = CubeTrace(model ? "Pout model" : "Pout", Style(c++, model ? LineType.Dashed : LineType.Solid, 1.0, null));
                    double pavl = lp["PavlDbm"].RealValues.Where(double.IsFinite).DefaultIfEmpty(0).Max();
                    t.ContourData = new ContourData
                    {
                        MetricName = "Pout_dBm", ContourConstraintKind = ConstraintKind.ConstantMetric, ConstraintMetricName = "PavlDbm",
                        ConstraintValue = pavl, ShowIsoLines = true, DrawLabels = true, LevelMode = ContourLevelMode.Count,
                    };
                    try { ContourResolve.Rebuild(t, new LoadpullSurface(lp), PlotType.Smith); }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { continue; }
                    plot.Traces.Add(t);
                }
                break;
            }
        }
    }

    /// <summary>
    /// A square map (axes vgs, vds; vds fastest) as a filled contour on the rectangular plot — x = vds, y = vgs — with its
    /// grid's outline, which frames the autoscale.
    /// </summary>
    private static void MapTrace(Plot plot, string name, DataCube map)
    {
        double[] vgs = map.Axes[0].Values, vds = map.Axes[1].Values;
        var grid = new SurfaceGrid(vds, vgs, map.RealValues);
        var t = CubeTrace(name, Style(0, LineType.Solid, 1.0, null));
        t.ContourData = new ContourData
        {
            Grid = grid, Levels = ContourExtractor.LevelsBetween(grid, 10), ShowFill = true, ShowIsoLines = true, DrawLabels = true,
            DisplayMxp = false, DisplayMxe = false,
        };
        plot.Traces.Add(t);
        var outline = Xy("grid", [vds[0], vds[^1], vds[^1], vds[0], vds[0]], [vgs[0], vgs[0], vgs[^1], vgs[^1], vgs[0]], "Vds", "V", 9, line: true, marker: null);
        outline.Properties.LineType = LineType.Dotted;
        plot.Traces.Add(outline);
    }

    /// <summary>The DataSet the selected plot draws: the step's plot data, the check's or the validation's.</summary>
    public DataSet? PlotDataSet => StepName switch
    {
        "Check" => CheckPlots?.Data,
        "Validate" when Validation is { } v => ValidationData(v),
        _ => StepPlots?.Data,
    };

    private static DataSet ValidationData(ValidationResult v)
    {
        var ds = new DataSet();
        foreach (var item in v.Items)
            foreach (var (name, cube) in item.Data.Cubes)
                if (!ds.Contains($"{item.Set}.{name}")) ds.Add($"{item.Set}.{name}", cube);
        return ds;
    }

    /// <summary>
    /// Open in Data Display (R-mc15-9): the selected plot's DataSet written into the document's cache beside a display that
    /// selects it, and that display opened in the workspace.
    /// </summary>
    [RelayCommand]
    private void OpenInDataDisplay()
    {
        if (PlotDataSet is not { } ds || ds.Cubes.Count == 0 || DocumentPath is not { } path) { Warn("Nothing to send; run the step first."); return; }
        try
        {
            string folder = System.IO.Path.Combine(CmcCache.FolderFor(path), "plots");
            string cdd = ModelReportDisplayPreset.WritePlotDocument(ds, folder, $"{StepName}-{SelectedPlotTab}");
            OpenSchematic?.Invoke(cdd);
            Info($"Opened {System.IO.Path.GetFileName(cdd)}.");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Error(ex.Message); }
    }
}
