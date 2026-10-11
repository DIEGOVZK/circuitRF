// The picture dialog's plot mode — brief-img-14-sparameter-plots-to-touchstone.md R-im14-2, R-im14-3, R-im14-8,
// R-im14-9, R-im14-12.
//
// IT DECIDES NOTHING, as the rest of the dialog decides nothing (R-im14-15). Every row is a field of PlotReadingOptions
// or TouchstoneAssemblyOptions — the records the CLI's flags fill — and the dialog hands them to PlotReading.Read and
// TouchstoneAssembly.Build through IImageRecognitionRunner. A combo here is the --trace, --fill or --phase a command line
// would have typed; a typed axis row is --x-axis in the same spelling.
//
// SEVERAL PICTURES ARE ONE SESSION. The pictures list holds every picture read; the first is the dialog's own source. A
// picture dropped, pasted or added while the dialog is in plot mode joins the session rather than replacing it.
//
// THE USER'S CHOICES OUTLIVE A RE-READ: an assignment is held by trace name (T3, the CLI's selector), a fill or phase rule
// by entry, an exclusion by plot number. A re-read rebuilds every row from what came back and lays the choices over it.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Design.Layout.Recognition;
using RfCore;
using FillRule = CircuitRF.Design.Imaging.Plots.FillRule;
using PlotType = CircuitRF.Design.Imaging.Plots.PlotType;

namespace CircuitRF.Ui.Recognition;

/// <summary>One picture of the session (R-im14-12's source list).</summary>
public sealed partial class PlotPictureRowViewModel(ImageSource source, Action<PlotPictureRowViewModel> remove, Action<PlotPictureRowViewModel> show)
    : ObservableObject
{
    public ImageSource Source { get; } = source;

    [ObservableProperty] private int _number;

    /// <summary>The first picture is the dialog's own source; it is replaced, never removed.</summary>
    public bool Removable => Number > 1;

    public string Name => Source.Origin == ImageOrigin.Bytes && Source.OriginalName == ImageSource.PastedName ? "Pasted picture" : Source.OriginalName;

    public string Label => $"{Number.ToString(CultureInfo.InvariantCulture)}. {Name}";

    partial void OnNumberChanged(int value)
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Removable));
    }

    [RelayCommand] private void Remove() => remove(this);

    [RelayCommand] private void Show() => show(this);
}

/// <summary>One plot of the session: whether it is read, and the axis rows it waits on (R-im14-2, R-im14-3).</summary>
public sealed partial class PlotFrameRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _loading = true;

    public PlotFrameRowViewModel(PlotRead plot, bool include, string xText, string yText, Action changed)
    {
        _changed = changed;
        Number = plot.Number;
        Picture = plot.Picture + 1;
        Kind = PlotKind.Name(plot.Type);
        Calibration = plot.Excluded ? "not read" : TouchstoneAssemblyText.Calibration(plot);
        NeedsX = plot.Rect is { X: null } || xText.Length > 0;
        NeedsY = plot.Rect is { Y: null } || yText.Length > 0;
        _include = include;
        _xAxisText = xText;
        _yAxisText = yText;
        _loading = false;
    }

    public int Number { get; }
    public int Picture { get; }
    public string Kind { get; }
    public string Calibration { get; }
    public string Title => $"Plot {Number.ToString(CultureInfo.InvariantCulture)} · {Kind} · picture {Picture.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The x (y) axis waits for two values — or has them typed.</summary>
    public bool NeedsX { get; }
    public bool NeedsY { get; }

    [ObservableProperty] private bool _include;

    /// <summary><c>120px=1GHz,900px=5GHz</c>: two pixel positions along the axis and their values (<c>--x-axis</c>).</summary>
    [ObservableProperty] private string _xAxisText;
    [ObservableProperty] private string _yAxisText;

    partial void OnIncludeChanged(bool value) { if (!_loading) _changed(); }
    partial void OnXAxisTextChanged(string value) { if (!_loading) _changed(); }
    partial void OnYAxisTextChanged(string value) { if (!_loading) _changed(); }
}

/// <summary>One trace of the traces table (R-im14-12): swatch, plot, entry, quantity, range, points, resolution, evidence.</summary>
public sealed partial class PlotTraceRowViewModel : ObservableObject
{
    /// <summary>The entry combo: unassigned, ignore, then S11 … S44.</summary>
    public static readonly IReadOnlyList<string> Entries =
        ["unassigned", "ignore", .. Enumerable.Range(1, 4).SelectMany(i => Enumerable.Range(1, 4).Select(j => $"S{i}{j}"))];

    /// <summary>The quantity combo, in <c>--trace</c>'s spelling.</summary>
    public static readonly IReadOnlyList<string> Quantities = ["db", "mag", "phase", "phase-rad", "re", "im", "complex", "vswr", "rl", "il"];

    private readonly Action<PlotTraceRowViewModel> _changed;
    private bool _loading = true;

    public PlotTraceRowViewModel(PlotTraceRead t, PlotRead plot, string startText, string stopText, Action<PlotTraceRowViewModel> changed)
    {
        _changed = changed;
        Name = t.Name;
        Plot = t.Plot;
        Swatch = new ImmutableSolidColorBrush(Color.FromRgb((byte)(t.Curve.Rgb >> 16), (byte)(t.Curve.Rgb >> 8), (byte)t.Curve.Rgb));
        Style = t.Curve.StyleName;
        IsChart = t.Parametric;
        Points = t.Samples.Count.ToString(CultureInfo.InvariantCulture);
        ResolutionText = $"±{(t.Resolution / 2).ToString("0.####", CultureInfo.InvariantCulture)} {t.ResolutionUnit}".TrimEnd();
        var xs = t.Samples.Select(s => s.X).Where(x => !double.IsNaN(x)).ToList();
        RangeText = xs.Count == 0 ? "no frequency"
            : plot.Rect is { XIsFrequency: false } ? $"{Fmt(xs.Min())} … {Fmt(xs.Max())}"
            : $"{AssemblyGrid.Hz(xs.Min())} … {AssemblyGrid.Hz(xs.Max())}";
        Evidence = t.Assignment.Assigned ? $"from the {t.Assignment.Evidence}" : t.Evidence;
        FrequencyEvidence = t.Frequency is { } f ? $"frequency from {f.Source}; direction by {f.Direction}" : IsChart ? "no frequency yet" : "";
        Confidence = new ImmutableSolidColorBrush(Color.Parse(
            !t.Assignment.Assigned && !t.Assignment.Ignored ? "#C8463A"
            : t.Assignment.Evidence is "user" or "legend" ? "#2E9D4A"
            : "#D9A21B"));
        Unassigned = !t.Assignment.Assigned && !t.Assignment.Ignored;
        NeedsFrequency = IsChart && t.Frequency is null;
        _entryIndex = t.Assignment.Ignored ? 1 : t.Assignment.Assigned ? 2 + (t.Assignment.I - 1) * 4 + (t.Assignment.J - 1) : 0;
        _quantityIndex = Math.Max(0, Quantities.ToList().IndexOf(PlotQuantity.Name(t.Assignment.Quantity)));
        if (!t.Assignment.Assigned) _quantityIndex = IsChart ? Quantities.ToList().IndexOf("complex") : 0;
        _startText = startText;
        _stopText = stopText;
        _loading = false;
    }

    public string Name { get; }
    public int Plot { get; }
    public IBrush Swatch { get; }
    public string Style { get; }
    public bool IsChart { get; }
    public string Points { get; }
    public string ResolutionText { get; }
    public string RangeText { get; }
    public string Evidence { get; }
    public string FrequencyEvidence { get; }
    public IBrush Confidence { get; }
    public bool Unassigned { get; }
    public bool NeedsFrequency { get; }

    [ObservableProperty] private int _entryIndex;
    [ObservableProperty] private int _quantityIndex;

    /// <summary>A chart trace's typed start and stop (the frequency row, R-im14-8 (3)); never pre-filled.</summary>
    [ObservableProperty] private string _startText;
    [ObservableProperty] private string _stopText;

    /// <summary>What the combos say, as <c>--trace</c> would spell it; null for <i>unassigned</i>.</summary>
    public PlotAssignment? Assignment => EntryIndex switch
    {
        <= 0 => null,
        1 => new PlotAssignment(0, 0, PlotQuantityKind.Ignored, "user"),
        _ => PlotQuantity.ParseAssignment($"{Entries[EntryIndex]}:{Quantities[Math.Clamp(QuantityIndex, 0, Quantities.Count - 1)]}"),
    };

    /// <summary>The user has chosen the combos (rather than the reading).</summary>
    public bool Edited { get; private set; }

    partial void OnEntryIndexChanged(int value) { if (!_loading) { Edited = true; _changed(this); } }
    partial void OnQuantityIndexChanged(int value) { if (!_loading) { Edited = true; _changed(this); } }
    partial void OnStartTextChanged(string value) { if (!_loading) _changed(this); }
    partial void OnStopTextChanged(string value) { if (!_loading) _changed(this); }

    private static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>One entry of the matrix grid (R-im14-12): where its data came from, and the rule that answers it.</summary>
public sealed partial class PlotMatrixCellViewModel : ObservableObject
{
    private readonly Action<PlotMatrixCellViewModel> _changed;
    private bool _loading = true;

    public PlotMatrixCellViewModel(MatrixCell cell, int ports, bool missingPhase, int selected, Action<PlotMatrixCellViewModel> changed)
    {
        _changed = changed;
        I = cell.I;
        J = cell.J;
        Source = cell.Source;
        Rule = cell.Rule.Length > 0 ? cell.Rule : cell.Phase.Length > 0 ? cell.Phase : "";
        Estimated = cell.Estimated;
        IsPhase = cell.Plotted && missingPhase;
        Choices = IsPhase ? ["min phase (estimate)", "zero phase", "delay"]
            : cell.Plotted ? []
            : FillChoices(I, J, ports);
        _selectedIndex = Choices.Count == 0 ? -1 : Math.Clamp(selected, 0, Choices.Count - 1);
        _loading = false;
    }

    public int I { get; }
    public int J { get; }
    public string Entry => $"S{I.ToString(CultureInfo.InvariantCulture)}{J.ToString(CultureInfo.InvariantCulture)}";
    public string Source { get; }
    public string Rule { get; }
    public bool Estimated { get; }

    /// <summary>A plotted magnitude with no phase: the combo is its missing-phase rule.</summary>
    public bool IsPhase { get; }

    public IReadOnlyList<string> Choices { get; }
    public bool HasChoices => Choices.Count > 0;

    [ObservableProperty] private int _selectedIndex;

    /// <summary>The delay, when the phase rule is <i>delay</i> (<c>250 ps</c>).</summary>
    [ObservableProperty] private string _delayText = "";

    public bool AsksDelay => IsPhase && SelectedIndex == 2;

    partial void OnSelectedIndexChanged(int value)
    {
        OnPropertyChanged(nameof(AsksDelay));
        if (!_loading) _changed(this);
    }

    partial void OnDelayTextChanged(string value) { if (!_loading) _changed(this); }

    /// <summary>The fill choices of an unplotted entry, the stated default first.</summary>
    public static IReadOnlyList<string> FillChoices(int i, int j, int ports)
    {
        var c = new List<string> { "default" };
        if (i != j) c.Add($"= S{j}{i}");
        if (ports == 2 && i == j) c.Add($"= S{3 - i}{3 - i}");
        c.Add("zero");
        c.Add("unassigned");
        return c;
    }

    public FillRule? Fill => IsPhase || SelectedIndex < 0 ? null : Choices[SelectedIndex] switch
    {
        "default" => FillRule.Default,
        "zero" => FillRule.Zero,
        "unassigned" => FillRule.Unassigned,
        var s when s.StartsWith('=') && I != J => FillRule.Reciprocal,
        _ => FillRule.Symmetric,
    };
}

/// <summary>The plot mode's state, and the options it spells.</summary>
public sealed partial class PlotSessionViewModel : ObservableObject
{
    private readonly Dictionary<string, PlotAssignment> _assign = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Start, string Stop)> _freq = new(StringComparer.Ordinal);
    private readonly HashSet<int> _excluded = [];
    private readonly Dictionary<(int Plot, char Axis), string> _axis = [];
    private readonly Dictionary<(int I, int J), FillRule> _fill = [];
    private readonly Dictionary<(int I, int J), (int Index, string Delay)> _phase = [];

    /// <summary>Raised when anything the reading or the file depends on changed: the dialog reads again.</summary>
    public event Action? Changed;

    /// <summary>Raised when the canvas should show another picture.</summary>
    public event Action? ShownChanged;

    public ObservableCollection<PlotPictureRowViewModel> Pictures { get; } = [];
    public ObservableCollection<PlotFrameRowViewModel> Frames { get; } = [];
    public ObservableCollection<PlotTraceRowViewModel> Traces { get; } = [];
    public ObservableCollection<PlotMatrixCellViewModel> Matrix { get; } = [];
    public ObservableCollection<string> ReportLines { get; } = [];

    public PlotSession? Session { get; private set; }
    public TouchstoneAssemblyResult? Assembly { get; private set; }

    /// <summary>The picture the canvas draws, 0-based.</summary>
    [ObservableProperty] private int _shownPicture;

    /// <summary>The trace chosen in the table or on the canvas — one selection, shown on both (R-ptu3-1, R-ptu3-2).</summary>
    [ObservableProperty] private PlotTraceRowViewModel? _selectedTrace;

    /// <summary>The row of the trace called <paramref name="name"/>, or null.</summary>
    public PlotTraceRowViewModel? TraceRow(string? name) =>
        name is null ? null : Traces.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>The 0-based picture a trace was read on, or −1.</summary>
    public int PictureOf(PlotTraceRowViewModel row) =>
        Session is { } s && row.Plot >= 1 && row.Plot <= s.Plots.Count ? s.Plots[row.Plot - 1].Picture : -1;

    [ObservableProperty] private string _smithZ0Text = "";
    [ObservableProperty] private string _gridStartText = "";
    [ObservableProperty] private string _gridStopText = "";
    [ObservableProperty] private string _gridPointsText = "";
    [ObservableProperty] private bool _gridLog;

    /// <summary>0 dB, 1 MA, 2 RI.</summary>
    [ObservableProperty] private int _formatIndex;

    /// <summary>Keep the source pictures beside the Touchstone (D4).</summary>
    [ObservableProperty] private bool _keepSources = true;

    partial void OnSmithZ0TextChanged(string value) => Changed?.Invoke();
    partial void OnGridStartTextChanged(string value) => Changed?.Invoke();
    partial void OnGridStopTextChanged(string value) => Changed?.Invoke();
    partial void OnGridPointsTextChanged(string value) => Changed?.Invoke();
    partial void OnGridLogChanged(bool value) => Changed?.Invoke();
    partial void OnFormatIndexChanged(int value) => Changed?.Invoke();

    partial void OnShownPictureChanged(int value) => ShownChanged?.Invoke();

    public static IReadOnlyList<string> Formats { get; } = ["DB", "MA", "RI"];

    // ── the pictures ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The session's first picture: a new session, every choice forgotten.</summary>
    public void SetPrimary(ImageSource source)
    {
        Pictures.Clear();
        _assign.Clear();
        _freq.Clear();
        _excluded.Clear();
        _axis.Clear();
        _fill.Clear();
        _phase.Clear();
        Session = null;
        Assembly = null;
        SelectedTrace = null;
        Frames.Clear();
        Traces.Clear();
        Matrix.Clear();
        ReportLines.Clear();
        Add(source, notify: false);
        ShownPicture = 0;
        RefreshDerived();
    }

    /// <summary>A picture joins the session (R-im14-12, R-im14-13).</summary>
    public void Add(ImageSource source, bool notify = true)
    {
        Pictures.Add(new PlotPictureRowViewModel(source, RemovePicture, p => ShownPicture = Pictures.IndexOf(p)));
        Renumber();
        if (notify)
        {
            ShownPicture = Pictures.Count - 1;
            Changed?.Invoke();
        }
    }

    private void RemovePicture(PlotPictureRowViewModel row)
    {
        int i = Pictures.IndexOf(row);
        if (i <= 0) return;
        Pictures.RemoveAt(i);
        Renumber();
        // Plot and trace numbers move when a picture goes; choices held by number would land on the wrong one.
        _assign.Clear();
        _freq.Clear();
        _excluded.Clear();
        _axis.Clear();
        if (ShownPicture >= Pictures.Count) ShownPicture = Pictures.Count - 1;
        Changed?.Invoke();
    }

    private void Renumber()
    {
        for (int i = 0; i < Pictures.Count; i++) Pictures[i].Number = i + 1;
        OnPropertyChanged(nameof(HasSeveral));
    }

    public bool HasSeveral => Pictures.Count > 1;

    /// <summary>The pictures as the reading takes them.</summary>
    public IReadOnlyList<PlotPictureInput> Inputs() =>
        [.. Pictures.Select(p => new PlotPictureInput(p.Source, p.Name == "Pasted picture" ? ImageSource.PastedName : p.Source.OriginalName))];

    // ── the options (the CLI's flags, field for field) ──────────────────────────────────────────────

    /// <summary>Why a typed field cannot be read, or null.</summary>
    public string? Blocked()
    {
        if (SmithZ0Text.Trim().Length > 0 && Z0() is null) return "Smith Z0 needs an impedance in ohms (50).";
        foreach (var ((plot, axis), text) in _axis)
            if (text.Trim().Length > 0 && Clicks(text, axis) is null)
                return $"Plot {plot}'s {axis} axis needs two pixel positions and their values: 120px=1GHz,900px=5GHz.";
        foreach (var (trace, (start, stop)) in _freq)
            if ((start.Trim().Length > 0 || stop.Trim().Length > 0) && Range(start, stop) is null)
                return $"{trace}'s frequency needs a start and a stop with units, the stop above the start (1 GHz, 5 GHz).";
        if (GridText() is { } g && Grid() is null && g.Length > 0)
            return "The output grid needs a start, a stop (with units) and a number of points, at least 2.";
        foreach (var ((i, j), (index, delay)) in _phase)
            if (index == 2 && Seconds(delay) is null) return $"S{i}{j}'s delay needs a time with its unit (250 ps).";
        return null;
    }

    public PlotReadingOptions ReadingOptions() => new()
    {
        Plots = _excluded.Count == 0 || Session is null ? null : [.. Session.Plots.Select(p => p.Number).Where(n => !_excluded.Contains(n))],
        Axes = [.. _axis.Where(a => a.Value.Trim().Length > 0)
                        .Select(a => (a.Key, Clicks: Clicks(a.Value, a.Key.Axis)))
                        .Where(a => a.Clicks is not null)
                        .Select(a => new PlotAxisOverride(a.Key.Plot, a.Key.Axis, a.Clicks!))],
        Traces = [.. _assign.Select(a => new PlotTraceOverride(a.Key, a.Value))],
        SmithZ0 = Z0(),
        Frequencies = [.. _freq.Select(f => (f.Key, R: Range(f.Value.Start, f.Value.Stop))).Where(f => f.R is not null)
                               .Select(f => new PlotFrequencyOverride(f.Key, f.R!.Value.Start, f.R.Value.Stop))],
    };

    public TouchstoneAssemblyOptions AssemblyOptions() => new()
    {
        Fill = new Dictionary<(int, int), FillRule>(_fill),
        Phase = _phase.ToDictionary(p => p.Key, p => p.Value.Index switch
        {
            1 => new PhaseRule(MissingPhase.Zero),
            2 => new PhaseRule(MissingPhase.Delay, Seconds(p.Value.Delay) ?? 0),
            _ => PhaseRule.Default,
        }),
        Grid = Grid(),
        Format = FormatIndex switch { 1 => MatrixFormat.MA, 2 => MatrixFormat.RI, _ => MatrixFormat.DB },
    };

    private double? Z0()
    {
        string t = SmithZ0Text.Trim();
        foreach (string unit in new[] { "Ohm", "ohm", "Ω" })
            if (t.EndsWith(unit, StringComparison.Ordinal)) { t = t[..^unit.Length].Trim(); break; }
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double z) && z > 0 && double.IsFinite(z) ? z : null;
    }

    private string? GridText() =>
        GridStartText.Trim().Length + GridStopText.Trim().Length + GridPointsText.Trim().Length == 0 ? null : GridStartText + GridStopText + GridPointsText;

    private AssemblyGrid? Grid()
    {
        if (GridText() is null) return null;
        if (Range(GridStartText, GridStopText) is not { } r
            || !int.TryParse(GridPointsText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 2) return null;
        return new AssemblyGrid(r.Start, r.Stop, n, GridLog);
    }

    private static (double Start, double Stop)? Range(string start, string stop) =>
        RecognitionSweep.Parse(start.Replace(" ", "")) is { } a && RecognitionSweep.Parse(stop.Replace(" ", "")) is { } b && b.Hz > a.Hz
            ? (a.Hz, b.Hz) : null;

    /// <summary><c>120px=1GHz,900px=5GHz</c> as two clicks; a y value is a plain number in the axis's own units.</summary>
    internal static AxisClicks? Clicks(string text, char axis)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return null;
        var v = new List<(double P, double V)>();
        foreach (string part in parts)
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) return null;
            string px = part[..eq].Trim();
            if (px.EndsWith("px", StringComparison.OrdinalIgnoreCase)) px = px[..^2];
            string value = part[(eq + 1)..].Replace(" ", "");
            if (!double.TryParse(px, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)) return null;
            double? val = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d
                        : axis == 'x' && RecognitionSweep.Parse(value) is { } f ? f.Hz : null;
            if (val is not { } got || !double.IsFinite(got)) return null;
            v.Add((p, got));
        }
        return v[0].P == v[1].P ? null : new AxisClicks(v[0].P, v[0].V, v[1].P, v[1].V);
    }

    private static double? Seconds(string s)
    {
        s = s.Replace(" ", "");
        (string Suffix, double Scale)[] units = [("ps", 1e-12), ("ns", 1e-9), ("us", 1e-6), ("µs", 1e-6), ("ms", 1e-3), ("fs", 1e-15), ("s", 1)];
        foreach (var (suffix, scale) in units)
            if (s.EndsWith(suffix, StringComparison.Ordinal)
                && double.TryParse(s[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
                return v * scale;
        return null;
    }

    // ── a reading arrived ────────────────────────────────────────────────────────────────────────────

    /// <summary>The rows, rebuilt from what came back, with the user's choices laid over them.</summary>
    public void Apply(PlotSession session, TouchstoneAssemblyResult assembly)
    {
        Session = session;
        Assembly = assembly;

        Frames.Clear();
        foreach (var p in session.Plots)
            Frames.Add(new PlotFrameRowViewModel(p, !_excluded.Contains(p.Number), _axis.GetValueOrDefault((p.Number, 'x'), ""),
                                                 _axis.GetValueOrDefault((p.Number, 'y'), ""), () => OnFrameEdited(p.Number)));

        // The selection is held by name across a re-read, as the choices are: the rows are new objects.
        string? chosen = SelectedTrace?.Name;
        Traces.Clear();
        foreach (var t in session.Traces)
        {
            var (start, stop) = _freq.GetValueOrDefault(t.Name, ("", ""));
            Traces.Add(new PlotTraceRowViewModel(t, session.Plots[t.Plot - 1], start, stop, OnTraceEdited));
        }
        SelectedTrace = TraceRow(chosen);

        Matrix.Clear();
        foreach (var c in assembly.Matrix)
        {
            bool missingPhase = c.Phase.Length > 0;
            int selected = missingPhase
                ? _phase.TryGetValue((c.I, c.J), out var ph) ? ph.Index : 0
                : _fill.TryGetValue((c.I, c.J), out var fr) ? FillIndex(c.I, c.J, assembly.Ports, fr) : 0;
            var cell = new PlotMatrixCellViewModel(c, assembly.Ports, missingPhase, selected, OnMatrixEdited);
            if (missingPhase && _phase.TryGetValue((c.I, c.J), out var held)) cell.DelayText = held.Delay;
            Matrix.Add(cell);
        }

        ReportLines.Clear();
        foreach (string line in session.Lines()) ReportLines.Add(line);
        foreach (string line in assembly.Report) ReportLines.Add(line);
        if (ShownPicture >= Pictures.Count) ShownPicture = Math.Max(0, Pictures.Count - 1);
        RefreshDerived();
    }

    private static int FillIndex(int i, int j, int ports, FillRule rule)
    {
        var choices = PlotMatrixCellViewModel.FillChoices(i, j, ports);
        string want = rule switch
        {
            FillRule.Zero => "zero",
            FillRule.Unassigned => "unassigned",
            FillRule.Reciprocal => $"= S{j}{i}",
            FillRule.Symmetric => $"= S{3 - i}{3 - i}",
            _ => "default",
        };
        return Math.Max(0, choices.ToList().IndexOf(want));
    }

    private void OnFrameEdited(int plot)
    {
        var row = Frames.FirstOrDefault(f => f.Number == plot);
        if (row is null) return;
        if (row.Include) _excluded.Remove(plot); else _excluded.Add(plot);
        _axis[(plot, 'x')] = row.XAxisText;
        _axis[(plot, 'y')] = row.YAxisText;
        Changed?.Invoke();
    }

    private void OnTraceEdited(PlotTraceRowViewModel row)
    {
        if (row.Edited)
        {
            if (row.Assignment is { } a) _assign[row.Name] = a;
            else _assign.Remove(row.Name);
        }
        _freq[row.Name] = (row.StartText, row.StopText);
        Changed?.Invoke();
    }

    private void OnMatrixEdited(PlotMatrixCellViewModel cell)
    {
        if (cell.IsPhase) _phase[(cell.I, cell.J)] = (cell.SelectedIndex, cell.DelayText);
        else if (cell.Fill is { } f) _fill[(cell.I, cell.J)] = f;
        Changed?.Invoke();
    }

    // ── what the dialog shows of it ──────────────────────────────────────────────────────────────────

    /// <summary>Why no Touchstone can be made yet — the refusal's sentence and the control that answers it — or null.</summary>
    public string? Refusal => Assembly?.Refusal is { } r ? $"{r.Sentence} — {r.Control}." : null;

    /// <summary>The refusal's kind, so its control is highlighted.</summary>
    public string RefusalKind => Assembly?.Refusal?.Kind ?? "";

    public bool HighlightTraces => RefusalKind is "unassigned" or "frequency" or "empty";
    public bool HighlightMatrix => RefusalKind == "fill";
    public bool HighlightGrid => RefusalKind == "range";
    public bool HighlightFrames => RefusalKind == "axis";

    /// <summary>The file's ports, for the matrix grid's columns.</summary>
    public int Ports => Assembly?.Ports ?? 0;

    public bool HasMatrix => Matrix.Count > 0;
    public bool HasSmith => Session?.Plots.Any(p => p.Type == PlotType.Smith) == true;
    public bool HasTraces => Session?.Traces.Count > 0;

    /// <summary>The default file name: the first picture's, with the extension the reading makes.</summary>
    public string DefaultFileName(bool csv) =>
        (Pictures.Count == 0 ? "plot" : System.IO.Path.GetFileNameWithoutExtension(Pictures[0].Source.OriginalName))
        + (csv ? ".csv" : Assembly?.Extension ?? ".s2p");

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(Refusal));
        OnPropertyChanged(nameof(RefusalKind));
        OnPropertyChanged(nameof(HighlightTraces));
        OnPropertyChanged(nameof(HighlightMatrix));
        OnPropertyChanged(nameof(HighlightGrid));
        OnPropertyChanged(nameof(HighlightFrames));
        OnPropertyChanged(nameof(Ports));
        OnPropertyChanged(nameof(HasMatrix));
        OnPropertyChanged(nameof(HasSmith));
        OnPropertyChanged(nameof(HasTraces));
    }
}

/// <summary>How an overlay item names its trace (R-ptu3-1): <c>trace:T3</c> is T3, and a bridged gap <c>gap:T3:17</c> is
/// T3's too. The reader builds the ids (ImageOverlay.BuildPlot); this side only reads them.</summary>
internal static class PlotTraceLink
{
    public static string? TraceName(ImageOverlayItem? item)
    {
        if (item is not { Class: ImageOverlayClass.Trace }) return null;
        if (item.Id.StartsWith(ImageOverlay.TracePrefix, StringComparison.Ordinal)) return item.Id[ImageOverlay.TracePrefix.Length..];
        if (!item.Id.StartsWith(ImageOverlay.GapPrefix, StringComparison.Ordinal)) return null;
        string rest = item.Id[ImageOverlay.GapPrefix.Length..];
        int colon = rest.LastIndexOf(':');
        return colon > 0 ? rest[..colon] : null;
    }

    /// <summary>The whole trace's item on <paramref name="overlay"/>, or null when it is not drawn there.</summary>
    public static ImageOverlayItem? ItemFor(ImageOverlay overlay, string? name) =>
        name is null ? null : overlay.Items.FirstOrDefault(i => i.Class == ImageOverlayClass.Trace && i.Id == ImageOverlay.TracePrefix + name);
}

/// <summary>A plot's calibration in words, as the matrix grid and the frames list show it.</summary>
internal static class TouchstoneAssemblyText
{
    public static string Calibration(PlotRead p) => p.Type switch
    {
        PlotType.Rectangular when p.Rect is { } r =>
            $"x {Axis(r.X)} · y {Axis(r.Y)}" + (r.XIsFrequency && !r.XUnitStated ? " · GHz assumed" : ""),
        PlotType.Smith when p.Smith is { } s => $"Z0 {s.Z0.ToString("0.###", CultureInfo.InvariantCulture)} Ω{(s.Z0Stated ? "" : " (assumed)")}"
                                                + (s.Admittance ? " · admittance chart" : ""),
        PlotType.Polar when p.Polar is { } pl => pl.Db ? "dB radius" : "linear radius",
        _ => "",
    };

    private static string Axis(AxisCalibration? a) => a is null ? "uncalibrated"
        : $"{a.Scale.ToString().ToLowerInvariant()}{(a.Manual ? " (typed)" : $", {a.Used.ToString(CultureInfo.InvariantCulture)} labels")}";
}
