// The plots panel (brief-modelcomposer-14 R-mc14-6 … R-mc14-9; overview §3): PlotControls of the Data Display, fed from
// the loaded data — never a bespoke chart. A tab strip per step; the bias cursor picks the point every frequency plot
// is drawn at. The traces are this window's, rebuilt from the data on every change (Plot.IsFixedReadout), exactly as
// railRF's results plot is.
//
// Two containers, because a plot's type is fixed when it is made: a rectangular one, and a Smith chart for the tabs that
// show reflection.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Modeling;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Ui.ModelComposer;

public sealed partial class ModelComposerViewModel
{
    /// <summary>The plots' host — not a Data Display document: nothing is persisted and no plot is added or deleted.</summary>
    public DataDisplayViewModel PlotHost { get; private set; } = null!;

    public PlotContainerViewModel RectContainer { get; private set; } = null!;
    public PlotContainerViewModel SmithContainer { get; private set; } = null!;
    public Plot RectPlot => RectContainer.PlotVM.Plot;
    public Plot SmithPlot => SmithContainer.PlotVM.Plot;

    /// <summary>The bias plane's click-and-lasso, set on the rectangular plot while the bias plane shows.</summary>
    public BiasPlaneLasso BiasPlaneLasso { get; private set; } = null!;

    /// <summary>How many times the plots were rebuilt — a counter, for the gate.</summary>
    public int PlotBuilds { get; private set; }

    private void BuildPlotHost()
    {
        PlotHost = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false, selectEmptyPlot: false);
        RectContainer = PlotHost.AddPlot(PlotType.Rect, FreqUnit.GHz, left: 0, top: 0, width: 320, height: 198);
        SmithContainer = PlotHost.AddPlot(PlotType.Smith, FreqUnit.GHz, left: 0, top: 0, width: 320, height: 320);
        foreach (var p in new[] { RectPlot, SmithPlot })
        {
            p.IsFixedReadout = true;
            p.Axes.LockedPanning = true;
            p.Axes.FontSizeLabel = TitleFontSizeLabel(p.Axes);
        }
        PlotHost.SelectOnly((PlotContainerViewModel?)null);
        BiasPlaneLasso = new BiasPlaneLasso(() => _biasPlanePoints, OnLasso);
        RebuildPlotTabs();
    }

    /// <summary>
    /// The title a tenth larger than the axis labels. The renderer draws a title at <c>FontSizeLabel × 1.4</c> and an axis
    /// label at <c>FontSizeTicks × 0.9</c>, which left the title twice the labels' size in a panel this small; harmonicaRF
    /// sets <c>FontSizeLabel</c> for its own title for the same reason (<c>HarmonicaPanelRenderer.RectTitleFontSizeLabel</c>).
    /// </summary>
    private static double TitleFontSizeLabel(Axes axes) => axes.FontSizeTicks * 0.9 * 1.1 / 1.4;

    // ── tabs ─────────────────────────────────────────────────────────────────

    public const string OutputTab = "Output", TransferTab = "Transfer", BiasPlaneTab = "Bias plane", STab = "S", SmithTab = "Smith",
                        CapacitanceTab = "C", ConductanceTab = "G", FlatnessTab = "Flatness";

    public IReadOnlyList<string> PlotTabs { get; private set; } = [];

    private string _selectedPlotTab = "";

    public string SelectedPlotTab
    {
        get => _selectedPlotTab;
        set
        {
            if (value is null || value == _selectedPlotTab || !PlotTabs.Contains(value)) { OnPropertyChanged(); return; }
            _selectedPlotTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSmithTab));
            OnPropertyChanged(nameof(IsBiasPlaneTab));
            RectContainer.Overlay = IsBiasPlaneTab ? BiasPlaneLasso : null;
            RebuildPlots();
        }
    }

    public bool IsSmithTab => SelectedPlotTab == SmithTab;
    public bool IsBiasPlaneTab => SelectedPlotTab == BiasPlaneTab;

    private void RebuildPlotTabs()
    {
        IReadOnlyList<string> tabs = IsPassive ? [STab, SmithTab] : ModelStepTabs() ?? StepName switch
        {
            "Data" => [OutputTab, TransferTab, BiasPlaneTab, STab, SmithTab],
            "Pads" => [STab, SmithTab],
            "Network" => [CapacitanceTab, ConductanceTab, FlatnessTab, BiasPlaneTab],
            _ => [BiasPlaneTab],
        };
        if (tabs.SequenceEqual(PlotTabs)) return;
        PlotTabs = tabs;
        OnPropertyChanged(nameof(PlotTabs));
        string keep = tabs.Contains(_selectedPlotTab) ? _selectedPlotTab : tabs[0];
        _selectedPlotTab = "";
        SelectedPlotTab = keep;
    }

    // ── bias plane options ───────────────────────────────────────────────────

    private bool _showExtrinsic = true, _showIntrinsic = true;

    public bool ShowExtrinsic { get => _showExtrinsic; set { if (SetProperty(ref _showExtrinsic, value)) RebuildPlots(); } }
    public bool ShowIntrinsic { get => _showIntrinsic; set { if (SetProperty(ref _showIntrinsic, value)) RebuildPlots(); } }

    [CommunityToolkit.Mvvm.Input.RelayCommand] private void ToggleExtrinsic() => ShowExtrinsic = !ShowExtrinsic;
    [CommunityToolkit.Mvvm.Input.RelayCommand] private void ToggleIntrinsic() => ShowIntrinsic = !ShowIntrinsic;
    [CommunityToolkit.Mvvm.Input.RelayCommand] private void ToggleExclude() => ExcludeArmed = !ExcludeArmed;
    [CommunityToolkit.Mvvm.Input.RelayCommand] private void SelectPlotTab(string tab) => SelectedPlotTab = tab;

    private IReadOnlyList<BiasPlanePoint> _biasPlanePoints = [];

    /// <summary>What a click or lasso on the bias plane took: the network fit's points on the Network step, else exclusions.</summary>
    private void OnLasso(IReadOnlyList<(string Set, string Point)> points) =>
        PostToUi(() => { if (IsNetworkStep) ToggleFitPoints(points); else ToggleExclusion(points); });

    // ── building ─────────────────────────────────────────────────────────────

    /// <summary>Redraws the selected tab from the loaded data at the cursor.</summary>
    private void RebuildPlots()
    {
        if (RectContainer is null || IsChoosingKind) return;
        PlotBuilds++;
        var plot = IsSmithTab ? SmithPlot : RectPlot;
        plot.Traces.Clear();
        plot.CustomTitleOn = true;
        plot.CustomXLabelOn = false;
        plot.CustomYLabelOn = false;
        _biasPlanePoints = [];
        try
        {
            if (!ModelStepPlot(plot))
            switch (SelectedPlotTab)
            {
                case OutputTab: IvFamily(plot, byVgs: true); break;
                case TransferTab: IvFamily(plot, byVgs: false); break;
                case BiasPlaneTab: BiasPlane(plot); break;
                case STab: SPlot(plot); break;
                case SmithTab: SPlot(plot); break;
                case CapacitanceTab: Intrinsic(plot, ["Cgs", "Cgd", "Cds"], 1e15, "fF"); break;
                case ConductanceTab: Intrinsic(plot, ["Gm", "Gds"], 1e3, "mS"); break;
                case FlatnessTab: Flatness(plot); break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException)
        {
            plot.Traces.Clear();
        }
        if (!plot.CustomTitleOn || plot.CustomTitle.Length == 0) plot.CustomTitle = SelectedPlotTab;
        plot.SetAxesViewport();
        plot.Autoscale(force: true);
        var container = IsSmithTab ? SmithContainer : RectContainer;
        container.OnPlotChanged(this, EventArgs.Empty);
        container.RequestPlotRedraw();
        OnPropertyChanged(nameof(RectPlot));
        OnPropertyChanged(nameof(SmithPlot));
    }

    /// <summary>The data set the plots follow, by kind: the selected one when it holds what the tab needs, else the first that does.</summary>
    private (CmcDataSet Set, NormalisedData Data)? SetFor(Func<CmcDataSet, NormalisedData, bool> holds)
    {
        var all = Document.DataSets.Where(s => Loaded.Data.ContainsKey(s.Name)).Select(s => (s, Loaded.Data[s.Name])).ToList();
        var pick = all.FirstOrDefault(x => x.s.Name == SelectedDataSet?.Name && holds(x.s, x.Item2));
        if (pick.s is null) pick = all.FirstOrDefault(x => holds(x.s, x.Item2));
        return pick.s is null ? null : (pick.s, pick.Item2);
    }

    private static bool HasIv(CmcDataSet s, NormalisedData d) => d.Points.Any(p => double.IsFinite(p.Id) && p.Id != 0);

    /// <summary>The output (Id against Vds, one curve per Vgs) or transfer (Id against Vgs, one per Vds) family.</summary>
    private void IvFamily(Plot plot, bool byVgs)
    {
        if ((SetFor((s, d) => s.Kind is DataSetKind.DcIv or DataSetKind.PulsedIv or DataSetKind.SimulatedTables && HasIv(s, d))
             ?? SetFor(HasIv)) is not { } sd) return;
        plot.CustomTitle = $"{sd.Set.Name} · {(byVgs ? "Id(Vds)" : "Id(Vgs)")}";
        var groups = sd.Data.Points.Where(p => !p.Excluded)
                       .GroupBy(p => Math.Round(byVgs ? p.Vgs : p.Vds, 6)).OrderBy(g => g.Key).ToList();
        foreach (var g in groups)
        {
            var pts = g.OrderBy(p => byVgs ? p.Vds : p.Vgs).ToList();
            if (pts.Count < 2) continue;
            var t = Xy($"{(byVgs ? "Vgs" : "Vds")} {ModelValueFormat.G(g.Key)} V",
                       [.. pts.Select(p => byVgs ? p.Vds : p.Vgs)], [.. pts.Select(p => p.Id * 1e3)],
                       byVgs ? "Vds" : "Vgs", "V", FamilyColour, line: true, marker: MarkerType.Circle);
            t.Properties.MarkerSize = SmallMarker;
            t.Properties.Custom = false;
            plot.Traces.Add(t);
        }
        var excluded = sd.Data.Points.Where(p => p.Excluded).ToList();
        if (excluded.Count > 0)
            plot.Traces.Add(Xy("excluded", [.. excluded.Select(p => byVgs ? p.Vds : p.Vgs)], [.. excluded.Select(p => p.Id * 1e3)],
                               byVgs ? "Vds" : "Vgs", "V", 9, line: false, marker: MarkerType.Cross));
        Labels(plot, byVgs ? "Vds (V)" : "Vgs (V)", "Id (mA)");
    }

    /// <summary>
    /// Every bias point of every set: extrinsic ○ where it was measured, intrinsic ● where the network puts it ([P1] Fig.
    /// 6.5), excluded ×, the hygiene flags ▲, the network fit's points ■ and the cursor ✶. x = Vds, y = Vgs.
    /// </summary>
    private void BiasPlane(Plot plot)
    {
        var drawn = new List<BiasPlanePoint>();
        var ext = new List<(double X, double Y)>();
        var intr = new List<(double X, double Y)>();
        var excl = new List<(double X, double Y)>();
        var flagged = new List<(double X, double Y)>();
        var fit = new List<(double X, double Y)>();
        var flags = Loaded.Flags.Select(f => (f.DataSet, f.Point)).ToHashSet();
        // Excluded as the DOCUMENT says, not as the loaded data last said: a click shows at once, while the data's reload
        // behind it is still running — a second click in that gap read the point as still included and put it back.
        var excluded = Document.DataSets.SelectMany(s => s.Exclusions.Select(x => (s.Name, x.Point))).ToHashSet();
        var fitPoints = IsNetworkStep ? FitPoints.Select(r => (r.Set, r.Point)).ToHashSet() : [];
        foreach (var set in Document.DataSets)
        {
            if (!Loaded.Data.TryGetValue(set.Name, out var d)) continue;
            var ds = Loaded.Intrinsic.GetValueOrDefault(set.Name);
            double[]? vgsi = ds?.Cubes.TryGetValue("Vgsi", out var gi) == true ? Values(gi) : null;
            double[]? vdsi = ds?.Cubes.TryGetValue("Vdsi", out var di) == true ? Values(di) : null;
            for (int i = 0; i < d.Count; i++)
            {
                var p = d.Points[i];
                (double X, double Y) e = (p.Vds, p.Vgs);
                (double X, double Y)? n = vgsi is not null && vdsi is not null && i < vgsi.Length && double.IsFinite(vgsi[i]) ? (vdsi[i], vgsi[i]) : null;
                if (!double.IsFinite(e.X) || !double.IsFinite(e.Y)) continue;
                if (ShowExtrinsic) ext.Add(e);
                if (ShowIntrinsic && n is { } nn) intr.Add(nn);
                var at = ShowIntrinsic && n is { } m ? m : e;
                drawn.Add(new BiasPlanePoint(set.Name, p.PointId, at.X, at.Y));
                if (excluded.Contains((set.Name, p.PointId))) excl.Add(at);
                if (flags.Contains((set.Name, p.PointId))) flagged.Add(at);
                if (fitPoints.Contains((set.Name, p.PointId))) fit.Add(at);
            }
        }
        _biasPlanePoints = drawn;
        plot.CustomTitle = "Bias plane";
        // Sizes in the Data Display's marker units (a fraction of the plot's size): small enough that a dense bias grid
        // still reads as points, the cursor's star only a little larger than a point.
        if (ext.Count > 0) plot.Traces.Add(Scatter("extrinsic", ext, 1, MarkerType.Circle, 0.8));
        if (intr.Count > 0) plot.Traces.Add(Scatter("intrinsic", intr, 0, MarkerType.Circle, 1.0));
        if (fit.Count > 0) plot.Traces.Add(Scatter("fit points", fit, 4, MarkerType.Square, 1.4));
        if (flagged.Count > 0) plot.Traces.Add(Scatter("flagged", flagged, 3, MarkerType.TriangleUp, 1.4));
        if (excl.Count > 0) plot.Traces.Add(Scatter("excluded", excl, 9, MarkerType.Cross, 1.6));
        if (SelectedVgs is { } cg && SelectedVds is { } cd)
            plot.Traces.Add(Scatter("cursor", [(cd.Value, cg.Value)], 2, MarkerType.Star, 1.8));
        Labels(plot, "Vds (V)", "Vgs (V)");
    }

    /// <summary>S at the cursor's bias: dB on the rectangular tab, the reflections on the Smith tab. On the Pads step, before
    /// and after the pads come off.</summary>
    private void SPlot(Plot plot)
    {
        bool passive = IsPassive;
        if (SetFor((s, d) => d.S is not null) is not { } sd) return;
        var data = sd.Data;
        int point = passive ? Math.Max(0, Math.Min(data.Count - 1, PassiveBlockIndex(data))) : CursorPoint(data);
        if (point < 0 || data.FrequenciesHz is not { } f) return;
        var p = data.Points[point];
        plot.CustomTitle = passive ? $"{sd.Set.Name} · {p.PointId}" : $"{sd.Set.Name} · Vgs {ModelValueFormat.G(p.Vgs)} V, Vds {ModelValueFormat.G(p.Vds)} V";
        var layers = new List<(string Tag, NormalisedData D, bool Dashed)>();
        if (IsPadsStep && Loaded.PadsRemoved.TryGetValue(sd.Set.Name, out var removed))
        {
            layers.Add(("before", data, true));
            layers.Add(("after", removed, false));
        }
        else layers.Add(("", data, false));

        int ports = data.Ports;
        var pairs = IsSmithTab
            ? Enumerable.Range(0, ports).Select(i => (i, i)).ToList()
            : Enumerable.Range(0, ports).SelectMany(i => Enumerable.Range(0, ports).Select(j => (i, j))).ToList();
        foreach (var (tag, d, dashed) in layers)
        {
            int c = 0;
            foreach (var (i, j) in pairs)
            {
                var s = new Complex[f.Length];
                for (int k = 0; k < f.Length; k++) s[k] = d.SAt(point, k, i, j);
                string name = $"S{i + 1}{j + 1}{(tag.Length > 0 ? " " + tag : "")}";
                var trace = CubeTrace(name, Style(c++, dashed ? LineType.Dashed : LineType.Solid, 1.0, null));
                if (IsSmithTab) trace.SetCubeData(f, s, null, "freq", "Hz", PlotType.Smith, FreqUnit.GHz);
                else trace.SetCubeData(f, null, [.. s.Select(x => 20 * Math.Log10(Math.Max(x.Magnitude, 1e-12)))], "freq", "Hz", PlotType.Rect, FreqUnit.GHz);
                plot.Traces.Add(trace);
            }
        }
        if (!IsSmithTab) Labels(plot, null, "dB");
    }

    /// <summary>The passive block the cursor's Vgs list stands in for: blocks have no bias, so the first is shown.</summary>
    private static int PassiveBlockIndex(NormalisedData d) => 0;

    /// <summary>Intrinsic quantities against frequency at the cursor's bias — flat when the network is right ([P1] §5.6.3).</summary>
    private void Intrinsic(Plot plot, IReadOnlyList<string> names, double scale, string unit)
    {
        var set = Document.DataSets.FirstOrDefault(s => s.Name == SelectedDataSet?.Name && Loaded.Intrinsic.TryGetValue(s.Name, out var x) && x.Cubes.ContainsKey("Y"))
               ?? Document.DataSets.FirstOrDefault(s => Loaded.Intrinsic.TryGetValue(s.Name, out var x) && x.Cubes.ContainsKey("Y"));
        if (set is null || !Loaded.Data.TryGetValue(set.Name, out var data)) return;
        var ds = Loaded.Intrinsic[set.Name];
        int point = CursorPoint(data);
        if (point < 0) return;
        double[] f = ds.Cubes["Y"].Axes[1].Values;
        int c = 0;
        foreach (string q in names)
        {
            if (!ds.Cubes.TryGetValue(q, out var cube)) continue;
            var y = new double[f.Length];
            for (int k = 0; k < f.Length; k++) y[k] = (double)cube[point, k] * scale;
            plot.Traces.Add(Xy(q, f, y, "freq", "Hz", c++, line: true, marker: null));
        }
        var p = data.Points[point];
        plot.CustomTitle = $"{set.Name} · Vgs {ModelValueFormat.G(p.Vgs)} V, Vds {ModelValueFormat.G(p.Vds)} V";
        Labels(plot, null, unit);
    }

    /// <summary>The flatness metric across bias: one curve per Vds, the mean over Cgs, Cgd and Cds against Vgs, in %.</summary>
    private void Flatness(Plot plot)
    {
        var set = Document.DataSets.FirstOrDefault(s => Loaded.Intrinsic.TryGetValue(s.Name, out var x) && x.Cubes.ContainsKey("Flatness"));
        if (set is null || !Loaded.Data.TryGetValue(set.Name, out var data)) return;
        var cube = Loaded.Intrinsic[set.Name].Cubes["Flatness"];
        int[] q = [.. new[] { "Cgs", "Cgd", "Cds" }.Select(n => IntrinsicQuantities.Names.ToList().IndexOf(n))];
        var rows = Enumerable.Range(0, data.Count)
                             .Select(i => (P: data.Points[i], F: q.Select(k => (double)cube[i, k]).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Average()))
                             .Where(r => double.IsFinite(r.F) && !r.P.Excluded).ToList();
        int c = 0;
        foreach (var g in rows.GroupBy(r => Math.Round(r.P.Vds, 6)).OrderBy(g => g.Key))
        {
            var pts = g.OrderBy(r => r.P.Vgs).ToList();
            plot.Traces.Add(Xy($"Vds {ModelValueFormat.G(g.Key)} V", [.. pts.Select(r => r.P.Vgs)], [.. pts.Select(r => 100 * r.F)],
                               "Vgs", "V", c++, line: pts.Count > 1, marker: MarkerType.Circle));
        }
        plot.CustomTitle = $"{set.Name} · flatness";
        Labels(plot, "Vgs (V)", "%");
    }

    // ── trace helpers (railRF's own shapes) ──────────────────────────────────

    /// <summary>The one colour an I–V family is drawn in — blue, the palette's second, so it never reads as the Data
    /// Display's red first trace — with every member the same: a family is one quantity drawn several times.</summary>
    private const int FamilyColour = 1;

    /// <summary>The marker size a family's data points are drawn at: present, not prominent.</summary>
    private const double SmallMarker = 0.8;

    private static double[] Values(DataCube c) => [.. Enumerable.Range(0, c.Axes[0].Values.Length).Select(i => (double)c[i])];

    private static void Labels(Plot plot, string? x, string y)
    {
        if (x is not null) { plot.CustomXLabelOn = true; plot.CustomXLabel = x; }
        plot.CustomYLabelOn = true;
        plot.CustomYLabel = y;
    }

    private static Trace Xy(string name, double[] x, double[] y, string xName, string xUnit, int colour, bool line, MarkerType? marker)
    {
        var t = CubeTrace(name, Style(colour, LineType.Solid, 1.0, marker));
        t.Properties.LineEnabled = line;
        t.Properties.Custom = false;
        t.SetCubeData(x, null, y, xName, xUnit, PlotType.Rect, xName == "freq" ? FreqUnit.GHz : FreqUnit.Hz);
        return t;
    }

    private static Trace Scatter(string name, IReadOnlyList<(double X, double Y)> pts, int colour, MarkerType marker, double size)
    {
        var t = Xy(name, [.. pts.Select(p => p.X)], [.. pts.Select(p => p.Y)], "Vds", "V", colour, line: false, marker: marker);
        t.Properties.MarkerSize = size;
        t.Properties.Custom = false;
        return t;
    }

    /// <summary>A cube-bound trace with nothing in it yet — <c>Trace</c> has no constructor without an <c>SNP</c>.</summary>
    private static Trace CubeTrace(string name, TraceProperties style) =>
        new(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Real, secondaryAxis: false, style)
        {
            CubeName = name,
            Expression = name,
            QuantityName = name,
        };

    private static TraceProperties Style(int order, LineType type, double width, MarkerType? marker)
    {
        int index = TraceProperties.LineColorOrder[order % TraceProperties.LineColorOrder.Length];
        var props = new TraceProperties
        {
            LineColorIndex = index,
            MarkerColorIndex = index,
            FillColorIndex = index,
            LineEnabled = true,
            LineType = type,
            LineWidth = width,
        };
        if (marker is { } m)
        {
            props.MarkerEnabled = true;
            props.MarkerType = m;
        }
        props.Custom = false;
        return props;
    }
}
