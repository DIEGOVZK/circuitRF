// The model steps (brief-modelcomposer-15 R-mc15-1 … R-mc15-8): Current, Charge, Thermal, Traps, Check, Validate and Export,
// each a thin view over its phase's function. A setting is an edit through the step's own WithSettings (one undo step);
// a run is the file-based function the `model` verb calls — ModelCommands.Fit, ConservationCheck.RunFile,
// ModelValidation.RunFile, CmodelExport.Export, ModelCommands.ExportCell — off the UI thread (D13, D15). Nothing here
// computes a result: the readouts are the records' own numbers, formatted.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Modeling.Passive;
using CircuitRF.Design.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ModelComposer;

public sealed partial class ModelComposerViewModel
{
    // ── the seams the window fills ───────────────────────────────────────────

    /// <summary>Shows a file in the platform's file manager — Show testbench.</summary>
    public Action<string>? RevealFile { get; set; }

    /// <summary>Puts a placement payload (a schematic selection) on the clipboard — Copy instance.</summary>
    public Func<string, Task>? CopyInstanceSink { get; set; }

    /// <summary>The workspace folder whose <c>.csym</c> files the symbol picker offers; null takes the nearest workspace above
    /// the document.</summary>
    public string? WorkspaceDirectory { get; set; }

    /// <summary>The provenance timestamp an export records — <c>model export --created</c>; null records the time of the
    /// export.</summary>
    public string? ExportCreated { get; set; }

    public bool IsCurrentStep => StepName == "Current";
    public bool IsChargeStep => StepName == "Charge";
    public bool IsThermalStep => StepName == "Thermal";
    public bool IsTrapsStep => StepName == "Traps";
    public bool IsCheckStep => StepName == "Check";
    public bool IsValidateStep => StepName == "Validate";
    public bool IsExportStep => StepName == "Export";
    public bool IsFitStep => StepName == "Fit";

    private void NotifyModelSteps()
    {
        foreach (string p in new[] { nameof(IsCurrentStep), nameof(IsChargeStep), nameof(IsThermalStep), nameof(IsTrapsStep),
                                     nameof(IsCheckStep), nameof(IsValidateStep), nameof(IsExportStep), nameof(IsFitStep) })
            OnPropertyChanged(p);
    }

    /// <summary>Re-projects every model step's settings and records — after an edit, an undo or a run.</summary>
    private void RefreshModelSteps()
    {
        if (!IsActive) { RefreshExport(); return; }
        _loadingSteps = true;
        try
        {
            RefreshCurrent();
            RefreshCharge();
            RefreshThermal();
            RefreshTraps();
            RefreshValidation();
            RefreshExport();
        }
        catch (Exception ex) when (ex is CmcFormatException or ModelingException) { }
        finally { _loadingSteps = false; }
    }

    private bool _loadingSteps;

    private static string Num(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    private static bool TryNum(string text, out double v) =>
        double.TryParse(text.Trim().TrimEnd('%').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    // ── the method picker (R-mc15-1) ─────────────────────────────────────────

    public ObservableCollection<MethodPickerRow> CurrentPickers { get; } = [];
    public ObservableCollection<MethodPickerRow> ChargePickers { get; } = [];

    private static string Layers(IReadOnlyList<int> hidden) => string.Join("×", hidden);

    private static IReadOnlyList<int>? ParseLayers(string text)
    {
        var parts = text.Split([',', '×', 'x', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var layers = new List<int>();
        foreach (string p in parts)
        {
            if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1) return null;
            layers.Add(n);
        }
        return layers.Count is >= 1 and <= 4 ? layers : null;
    }

    private static IReadOnlyList<string> Fitted(Constituent? c) =>
        c is null ? [] : [.. c.Parameters.Select(p => $"{p.Key} = {ModelValueFormat.G(p.Value)}")];

    // ── Current (R-mc15-2) ───────────────────────────────────────────────────

    public IReadOnlyList<string> CurrentMethods { get; } = Enum.GetNames<CurrentMethod>();

    [ObservableProperty] private string _currentSeed = "";
    [ObservableProperty] private string _currentRestarts = "";
    [ObservableProperty] private string _currentHeldOut = "";
    [ObservableProperty] private bool _dispersion;

    public const string DispersionTip = "DC/RF dispersion as an RF current of its own — the simpler alternative to heating and traps.";

    private void RefreshCurrent()
    {
        var s = CurrentFit.Settings(Document);
        var r = CurrentFit.Record(Document);
        if (CurrentPickers.Count == 0)
        {
            CurrentPickers.Add(new MethodPickerRow("Id", CurrentMethods, [.. CurrentTemplates.Names(false)], false, CommitCurrent));
            CurrentPickers.Add(new MethodPickerRow("Ig", CurrentMethods, [.. CurrentTemplates.Names(true)], false, CommitCurrent));
        }
        CurrentPickers[0].Load(s.Id.Method.ToString(), Layers(s.Id.Hidden), s.Id.Activation.ToString(), s.Id.MaxIterations.ToString(CultureInfo.InvariantCulture),
                               Num(s.Id.GridStep), Num(s.Id.Smoothing), "", s.Id.Template ?? "", s.Id.Expression ?? "", "", Fitted(r?.Id));
        CurrentPickers[1].Load(s.Ig.Method.ToString(), Layers(s.Ig.Hidden), s.Ig.Activation.ToString(), s.Ig.MaxIterations.ToString(CultureInfo.InvariantCulture),
                               Num(s.Ig.GridStep), Num(s.Ig.Smoothing), "", s.Ig.Template ?? "", s.Ig.IgGs ?? "", s.Ig.IgGd ?? "", Fitted(r?.Ig));
        CurrentSeed = s.Seed.ToString(CultureInfo.InvariantCulture);
        CurrentRestarts = s.Restarts.ToString(CultureInfo.InvariantCulture);
        CurrentHeldOut = Num(100 * s.HeldOutFraction);
        Dispersion = s.Dispersion is not null;
    }

    private void CommitCurrent(MethodPickerRow row, string field, string text)
    {
        if (_loadingSteps) return;
        var s = CurrentFit.Settings(Document);
        var c = row.Name == "Id" ? s.Id : s.Ig;
        if (Constituent(c, row, field, text) is not { } next) return;
        var settings = row.Name == "Id" ? s with { Id = next } : s with { Ig = next };
        EditPart($"{row.Name}: {field} {text}", CurrentFit.Settings, CurrentFit.WithSettings, settings);
    }

    /// <summary>A current constituent's settings with one picker field changed, or null (and the status line) when the text is
    /// not a value of that field.</summary>
    private CurrentConstituentSettings? Constituent(CurrentConstituentSettings c, MethodPickerRow row, string field, string text)
    {
        switch (field)
        {
            case nameof(MethodPickerRow.Method):
                return Enum.TryParse<CurrentMethod>(text, out var m) ? c with { Method = m } : null;
            case nameof(MethodPickerRow.Hidden):
                if (ParseLayers(text) is { } h) return c with { Hidden = h };
                Error($"{row.Name}: hidden layers are 1 to 4 counts, e.g. 8, 8.");
                return null;
            case nameof(MethodPickerRow.Activation):
                return Enum.TryParse<AnnActivation>(text, out var a) ? c with { Activation = a } : null;
            case nameof(MethodPickerRow.Iterations):
                return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int it) && it > 0 ? c with { MaxIterations = it } : Refuse(row, field);
            case nameof(MethodPickerRow.GridStep):
                return TryNum(text, out double g) && g > 0 ? c with { GridStep = g } : Refuse(row, field);
            case nameof(MethodPickerRow.Smoothing):
                return TryNum(text, out double l) && l >= 0 ? c with { Smoothing = l } : Refuse(row, field);
            case nameof(MethodPickerRow.Template):
                return c with { Template = text.Length == 0 ? null : text };
            case nameof(MethodPickerRow.Expression):
                return row.IsGate ? c with { IgGs = text.Length == 0 ? null : text } : c with { Expression = text.Length == 0 ? null : text };
            case nameof(MethodPickerRow.ExpressionGd):
                return c with { IgGd = text.Length == 0 ? null : text };
            default:
                return null;
        }
    }

    private T? Refuse<T>(MethodPickerRow row, string field) where T : class
    {
        Error($"{row.Name}: '{field}' needs a positive number.");
        return null;
    }

    private CurrentConstituentSettings? Refuse(MethodPickerRow row, string field) => Refuse<CurrentConstituentSettings>(row, field);

    partial void OnCurrentSeedChanged(string value) =>
        EditCurrent(value, (s, t) => ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out ulong v) ? s with { Seed = v } : null, "seed");

    partial void OnCurrentRestartsChanged(string value) =>
        EditCurrent(value, (s, t) => int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int v) && v > 0 ? s with { Restarts = v } : null, "restarts");

    partial void OnCurrentHeldOutChanged(string value) =>
        EditCurrent(value, (s, t) => TryNum(t, out double v) && v is >= 0 and < 100 ? s with { HeldOutFraction = v / 100 } : null, "held-out %");

    partial void OnDispersionChanged(bool value) =>
        EditCurrent(value ? "on" : "off", (s, _) => s with { Dispersion = value ? s.Dispersion ?? new DispersionFitSettings() : null }, "dispersion");

    private void EditCurrent(string text, Func<CurrentFitSettings, string, CurrentFitSettings?> change, string what)
    {
        if (_loadingSteps || IsChoosingKind) return;
        if (change(CurrentFit.Settings(Document), text) is not { } next) { Error($"Current: '{text}' is not a {what}."); return; }
        EditPart($"Current: {what} {text}", CurrentFit.Settings, CurrentFit.WithSettings, next);
    }

    // ── Charge (R-mc15-3) ────────────────────────────────────────────────────

    public IReadOnlyList<string> ChargeMethods { get; } = Enum.GetNames<ChargeMethod>();

    [ObservableProperty] private string _chargeSeed = "";
    [ObservableProperty] private bool _driftCharge;

    private void RefreshCharge()
    {
        var s = ChargeFit.Settings(Document);
        var r = ChargeFit.Record(Document);
        if (ChargePickers.Count == 0)
        {
            ChargePickers.Add(new MethodPickerRow("Qg", ChargeMethods, [.. ChargeTemplates.Names], true, CommitCharge));
            ChargePickers.Add(new MethodPickerRow("Qd", ChargeMethods, [.. ChargeTemplates.Names], true, CommitCharge));
        }
        foreach (var (row, c, rec) in new[] { (ChargePickers[0], s.Qg, r?.Qg), (ChargePickers[1], s.Qd, r?.Qd) })
            row.Load(c.Method.ToString(), Layers(c.Hidden), c.Activation.ToString(), c.MaxIterations.ToString(CultureInfo.InvariantCulture),
                     Num(c.GridStep), Num(c.Smoothing), c.Path.ToString(), c.Template ?? "", c.Expression ?? "", "", Fitted(rec));
        // R-mc17-6: a method the usable S points cannot support says why on hover — the sentence a fit would refuse with.
        var usable = ChargeFit.Usable(Document, Loaded.Data, new IntrinsicResult(Loaded.Intrinsic, []));
        foreach (var row in ChargePickers)
            row.SetTips(m => usable is { } u && Enum.TryParse<ChargeMethod>(m, out var method) ? ChargeFit.Refusal(method, u.Points, u.SpansArea) : null);
        ChargeSeed = s.Seed.ToString(CultureInfo.InvariantCulture);
        DriftCharge = s.Drift is not null;
    }

    private void CommitCharge(MethodPickerRow row, string field, string text)
    {
        if (_loadingSteps) return;
        var s = ChargeFit.Settings(Document);
        var c = row.Name == "Qg" ? s.Qg : s.Qd;
        ChargeConstituentSettings? next = field switch
        {
            nameof(MethodPickerRow.Method) => Enum.TryParse<ChargeMethod>(text, out var m) ? c with { Method = m } : null,
            nameof(MethodPickerRow.Hidden) => ParseLayers(text) is { } h ? c with { Hidden = h } : null,
            nameof(MethodPickerRow.Activation) => Enum.TryParse<AnnActivation>(text, out var a) ? c with { Activation = a } : null,
            nameof(MethodPickerRow.Iterations) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int it) && it > 0 ? c with { MaxIterations = it } : null,
            nameof(MethodPickerRow.GridStep) => TryNum(text, out double g) && g > 0 ? c with { GridStep = g } : null,
            nameof(MethodPickerRow.Smoothing) => TryNum(text, out double l) && l >= 0 ? c with { Smoothing = l } : null,
            nameof(MethodPickerRow.Path) => Enum.TryParse<IntegrationPath>(text, out var p) ? c with { Path = p } : null,
            nameof(MethodPickerRow.Template) => c with { Template = text.Length == 0 ? null : text },
            nameof(MethodPickerRow.Expression) => c with { Expression = text.Length == 0 ? null : text },
            _ => null,
        };
        if (next is null) { Error($"{row.Name}: '{text}' is not a value of {field}."); return; }
        var settings = row.Name == "Qg" ? s with { Qg = next } : s with { Qd = next };
        EditPart($"{row.Name}: {field} {text}", ChargeFit.Settings, ChargeFit.WithSettings, settings);
    }

    partial void OnChargeSeedChanged(string value)
    {
        if (_loadingSteps || IsChoosingKind) return;
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed)) { Error($"Charge: '{value}' is not a seed."); return; }
        EditPart($"Charge: seed {value}", ChargeFit.Settings, ChargeFit.WithSettings, ChargeFit.Settings(Document) with { Seed = seed });
    }

    partial void OnDriftChargeChanged(bool value)
    {
        if (_loadingSteps || IsChoosingKind) return;
        var s = ChargeFit.Settings(Document);
        EditPart($"Charge: drift {(value ? "on" : "off")}", ChargeFit.Settings, ChargeFit.WithSettings,
                 s with { Drift = value ? s.Drift ?? new DriftChargeSettings() : null });
    }

    /// <summary>What the 1-D C–V plot shows: one capacitance, or the circuit set.</summary>
    public IReadOnlyList<string> CvQuantities { get; } = ["Circuit set", "C11", "C12", "C21", "C22"];

    private string _cvQuantity = "Circuit set";

    public string CvQuantity
    {
        get => _cvQuantity;
        set { if (value is not null && SetProperty(ref _cvQuantity, value)) RebuildPlots(); }
    }

    // ── Thermal (R-mc15-4) ───────────────────────────────────────────────────

    [ObservableProperty] private bool _selfHeating;
    [ObservableProperty] private string _rth = "";
    [ObservableProperty] private string _cth = "";
    [ObservableProperty] private string _ladder = "";
    /// <summary>The extraction's residual, or the record's Rth, as one line.</summary>
    [ObservableProperty] private string _thermalResidual = "";
    /// <summary>True when the data is at one temperature: T0 shows in place of the controls (D8).</summary>
    [ObservableProperty] private bool _oneTemperature;
    [ObservableProperty] private string _t0Text = "";

    private void RefreshThermal()
    {
        var s = ThermalFit.Settings(Document);
        SelfHeating = s.SelfHeating;
        Rth = s.Rth is { } rth ? Num(rth) : "";
        Cth = s.Cth is { } cth ? ModelValueFormat.Eng(cth, "J/K") : "";
        Ladder = string.Join(", ", s.Ladder.Select(p => $"{Num(p.Fraction)}:{ModelValueFormat.Eng(p.Tau, "s")}"));
        var plan = ThermalFit.Plan(Document, Loaded.Data);
        OneTemperature = Loaded.Data.Count > 0 && plan.IsSingle;
        T0Text = $"T0 {ModelValueFormat.G(plan.T0)} °C";
        var r = ThermalFit.Record(Document);
        ThermalResidual = r?.Extraction is { } e
            ? $"Rth {(r.Rth is { } x ? ModelValueFormat.G(x) : "—")} K/W · residual {ModelValueFormat.G(100 * e.ResidualRelative)} % over {e.Used} pts"
            : r?.Rth is { } rr ? $"Rth {ModelValueFormat.G(rr)} K/W" : "";
    }

    partial void OnSelfHeatingChanged(bool value) => EditThermal(s => s with { SelfHeating = value }, $"self-heating {(value ? "on" : "off")}");

    partial void OnRthChanged(string value)
    {
        if (value.Trim().Length == 0) { EditThermal(s => s with { Rth = null }, "Rth extracted"); return; }
        if (TryNum(value, out double v) && v > 0) EditThermal(s => s with { Rth = v }, $"Rth {value}");
        else if (!_loadingSteps) Error($"Thermal: '{value}' is not a thermal resistance in K/W.");
    }

    partial void OnCthChanged(string value)
    {
        if (value.Trim().Length == 0) { EditThermal(s => s with { Cth = null }, "Cth none"); return; }
        if (ModelValueFormat.TryParse(value, "J/K", out double v) && v > 0) EditThermal(s => s with { Cth = v }, $"Cth {value}");
        else if (!_loadingSteps) Error($"Thermal: '{value}' is not a thermal capacitance in J/K.");
    }

    partial void OnLadderChanged(string value)
    {
        var poles = new List<LadderPoleSettings>();
        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fr = part.Split(':', StringSplitOptions.TrimEntries);
            if (fr.Length != 2 || !TryNum(fr[0], out double f) || !ModelValueFormat.TryParse(fr[1], "s", out double tau))
            {
                if (!_loadingSteps) Error("Thermal: the ladder is fraction:τ pairs, e.g. 0.6:1 µs, 0.4:100 µs.");
                return;
            }
            poles.Add(new LadderPoleSettings(f, tau));
        }
        EditThermal(s => s with { Ladder = poles }, $"ladder {value}");
    }

    private void EditThermal(Func<ThermalFitSettings, ThermalFitSettings> change, string label)
    {
        if (_loadingSteps || IsChoosingKind || !IsActive) return;
        EditPart($"Thermal: {label}", ThermalFit.Settings, ThermalFit.WithSettings, change(ThermalFit.Settings(Document)));
    }

    // ── Traps (R-mc15-5) ─────────────────────────────────────────────────────

    public IReadOnlyList<string> TrapModelNames => TrapModels.Names;

    [ObservableProperty] private string _trapModel = ModelTraps.None;
    public ObservableCollection<TrapStateRow> TrapStates { get; } = [];
    public ObservableCollection<ParameterRow> TrapParameters { get; } = [];
    public ObservableCollection<ParameterRow> TrapExpressions { get; } = [];
    /// <summary>The custom model's fixed-point check, as one line.</summary>
    [ObservableProperty] private string _trapCheck = "";
    public bool IsCustomTraps => TrapModel == CustomTraps.ModelName;

    private void RefreshTraps()
    {
        var s = TrapFit.Settings(Document);
        TrapModel = s.Model;
        OnPropertyChanged(nameof(IsCustomTraps));
        TrapStates.Clear();
        TrapParameters.Clear();
        TrapExpressions.Clear();
        TrapCheck = "";
        if (!s.Modelled) return;
        ModelTraps? block = null;
        try { block = TrapFit.Block(s); }
        catch (Exception ex) when (ex is ModelingException or CmodelFormatException or CmcFormatException) { TrapCheck = ex.Message; }
        var record = TrapFit.Record(Document);
        var def = TrapModels.Find(s.Model);
        var states = s.Model == CustomTraps.ModelName ? s.Expressions.Keys.ToList() : def?.States.ToList() ?? [];
        var on = block?.States ?? s.States;
        foreach (string state in states)
        {
            var row = new TrapStateRow(state, TrapFit.Label(s.Model, state), ToggleTrapState);
            var levels = record?.Levels.GetValueOrDefault(state);
            string reason = record is not null && on.Contains(state) && levels is { Count: 0 } ? "no pulsed set reaches its trap-setting level" : "";
            string level = levels is { Count: 1 } ? $"from {ModelValueFormat.G(levels[0])} V" : "";
            row.Load(on.Contains(state), reason, level);
            TrapStates.Add(row);
        }
        foreach (var p in def?.Parameters ?? [])
        {
            var row = new ParameterRow(p.Name, p.Unit, p.Meaning, CommitTrapParameter);
            row.Load(s.Parameters.TryGetValue(p.Name, out double v) ? Num(v) : p.Default is { } d ? Num(d) : "");
            TrapParameters.Add(row);
        }
        foreach (var (state, (dynamics, dc)) in s.Expressions)
        {
            var d = new ParameterRow($"{state} dynamics", "", "d(state)/dt", CommitTrapExpression);
            d.Load(dynamics);
            var c = new ParameterRow($"{state} DC", "", "the state at DC", CommitTrapExpression);
            c.Load(dc);
            TrapExpressions.Add(d);
            TrapExpressions.Add(c);
        }
        if (block is not null && s.Model == CustomTraps.ModelName)
        {
            try { TrapModels.Build(block, "trap step"); TrapCheck = "fixed point ✓"; }
            catch (Exception ex) when (ex is CmodelFormatException or ModelingException or ArgumentException) { TrapCheck = ex.Message; }
        }
    }

    partial void OnTrapModelChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomTraps));
        if (_loadingSteps || IsChoosingKind || value is null) return;
        EditPart($"Traps: {value}", TrapFit.Settings, TrapFit.WithSettings, TrapFit.Settings(Document) with { Model = value, States = [] });
    }

    private void ToggleTrapState(TrapStateRow row, bool on)
    {
        if (_loadingSteps) return;
        var s = TrapFit.Settings(Document);
        var current = TrapStates.Where(r => r.IsOn).Select(r => r.State).ToList();
        EditPart($"Traps: {row.Label} {(on ? "on" : "off")}", TrapFit.Settings, TrapFit.WithSettings, s with { States = current });
    }

    private void CommitTrapParameter(ParameterRow row, string text)
    {
        if (_loadingSteps) return;
        var s = TrapFit.Settings(Document);
        var p = new Dictionary<string, double>(s.Parameters, StringComparer.Ordinal);
        if (text.Trim().Length == 0) p.Remove(row.Name);
        else if (TryNum(text, out double v) || ModelValueFormat.TryParse(text, row.Unit, out v)) p[row.Name] = v;
        else { Error($"Traps: '{text}' is not a value of {row.Name}."); return; }
        EditPart($"Traps: {row.Name} {text}", TrapFit.Settings, TrapFit.WithSettings, s with { Parameters = p });
    }

    private void CommitTrapExpression(ParameterRow row, string text)
    {
        if (_loadingSteps) return;
        var s = TrapFit.Settings(Document);
        int space = row.Name.LastIndexOf(' ');
        string state = row.Name[..space];
        bool dynamics = row.Name.EndsWith("dynamics", StringComparison.Ordinal);
        var e = new Dictionary<string, (string Dynamics, string Dc)>(s.Expressions, StringComparer.Ordinal);
        var (d, c) = e.GetValueOrDefault(state);
        e[state] = dynamics ? (text, c ?? "") : (d ?? "", text);
        EditPart($"Traps: {row.Name}", TrapFit.Settings, TrapFit.WithSettings, s with { Expressions = e });
    }

    // ── Check (R-mc15-6) ─────────────────────────────────────────────────────

    /// <summary>The check's result, one line per part: data, model, HB; then the transcapacitance as information.</summary>
    public ObservableCollection<string> CheckLines { get; } = [];

    /// <summary>The last check's plot data — the maps and the spectrum.</summary>
    public StepPlotData? CheckPlots { get; private set; }

    /// <summary>The last check's report.</summary>
    public ConservationReport? CheckReport { get; private set; }

    [RelayCommand]
    private Task CheckCharge() => CheckChargeAsync();

    /// <summary>
    /// Check Charge Conservation (R-mc15-6, MC-8 R-mc8-5): <see cref="ConservationCheck.RunFile"/> off the UI thread at the
    /// middle of the data — <c>model check-charge</c> without <c>--bias</c> — then the maps of its result. Read-only: the
    /// document is written first, nothing after.
    /// </summary>
    public async Task<bool> CheckChargeAsync()
    {
        if (!IsActive) return false;
        if (IsRunning && !_isRefresh) { Warn($"{TaskStage} is running; cancel it or wait for it to finish."); return false; }
        if (!await EnsureWritten()) return false;
        string path = DocumentPath!;
        var doc = Document;
        var loaded = Loaded;
        var settings = new LargeSignalSettings();
        return await StartTask("Checking charge conservation", refresh: false, writesFile: false, rc =>
        {
            var report = ConservationCheck.RunFile(path, settings, rc);
            return (report, ModelStepPlots.Check(path, doc, loaded, report, rc));
        }, r =>
        {
            var (report, plots) = ((ConservationReport, StepPlotData))r!;
            CheckReport = report;
            CheckPlots = plots;
            CheckLines.Clear();
            CheckLines.Add(ConservationCheck.Summary(report));
            foreach (string line in report.Lines.Where(l => l.StartsWith("transcapacitance", StringComparison.Ordinal))) CheckLines.Add(line);
            if (ConservationCheck.Exceeds(report)) Warn(ConservationCheck.Summary(report));
            else Info(ConservationCheck.Summary(report));
            RebuildPlots();
        });
    }

    // ── Validate (R-mc15-7) ──────────────────────────────────────────────────

    public ObservableCollection<ValidationRow> ValidationRows { get; } = [];
    public ObservableCollection<ParameterRow> Tolerances { get; } = [];

    /// <summary>The last validation's result — what the overlays draw.</summary>
    public ValidationResult? Validation { get; private set; }

    private void RefreshValidation()
    {
        var settings = ModelValidation.Settings(Document);
        var t = settings.Tolerances;
        string Kind(CmcDataSet s) => s.Kind switch
        {
            DataSetKind.SParams or DataSetKind.PulsedSParams => "S",
            DataSetKind.DcIv => "DC I–V",
            DataSetKind.PulsedIv => "pulsed I–V",
            DataSetKind.LargeSignal => "large signal",
            _ => s.Kind.ToString(),
        };
        string Tol(CmcDataSet s) => s.Kind switch
        {
            DataSetKind.SParams or DataSetKind.PulsedSParams => $"|ΔS| median {Num(t.SMedian)}, 95 % {Num(t.S95)}",
            DataSetKind.DcIv or DataSetKind.PulsedIv => $"Id {Num(100 * t.IvRelative)} % of range",
            DataSetKind.LargeSignal => $"{Num(t.PowerDb)} dB, {Num(t.EfficiencyPoints)} pts",
            _ => "",
        };
        var rows = Document.DataSets.Where(s => s.Kind is not DataSetKind.SimulatedTables and not DataSetKind.PassiveSParams).ToList();
        if (!rows.Select(s => s.Name).SequenceEqual(ValidationRows.Select(r => r.Set))
            || rows.Select(Tol).Zip(ValidationRows, (a, b) => a != b.Tolerance).Any(x => x))
        {
            ValidationRows.Clear();
            foreach (var s in rows) ValidationRows.Add(new ValidationRow(s.Name, Kind(s), Tol(s)));
        }
        if (Validation is { } v)
            foreach (var row in ValidationRows)
                if (v.Items.Where(i => i.Set == row.Set).ToList() is { Count: > 0 } items)
                {
                    row.Result = string.Join(" · ", items.Select(i => i.Line));
                    row.Passed = items.All(i => i.Passed);
                }

        if (Tolerances.Count == 0)
            foreach (var (key, unit, tip) in new[]
                     {
                         ("SMedian", "", "Median |ΔS| over every point and frequency"), ("S95", "", "95th percentile of |ΔS|"),
                         ("IvRelative", "%", "RMS drain-current error, % of its range"), ("PowerDb", "dB", "Pout and gain error"),
                         ("EfficiencyPoints", "pts", "PAE and efficiency error, percentage points"),
                     })
                Tolerances.Add(new ParameterRow(key, unit, tip, CommitTolerance));
        Tolerances[0].Load(Num(t.SMedian));
        Tolerances[1].Load(Num(t.S95));
        Tolerances[2].Load(Num(100 * t.IvRelative));
        Tolerances[3].Load(Num(t.PowerDb));
        Tolerances[4].Load(Num(t.EfficiencyPoints));
    }

    private void CommitTolerance(ParameterRow row, string text)
    {
        if (_loadingSteps) return;
        if (!TryNum(text, out double v) || v <= 0) { Error($"Validate: '{text}' is not a tolerance."); return; }
        var s = ModelValidation.Settings(Document);
        var t = s.Tolerances;
        t = row.Name switch
        {
            "SMedian" => t with { SMedian = v },
            "S95" => t with { S95 = v },
            "IvRelative" => t with { IvRelative = v / 100 },
            "PowerDb" => t with { PowerDb = v },
            _ => t with { EfficiencyPoints = v },
        };
        EditPart($"Validate: {row.Name} {text}", ModelValidation.Settings, ModelValidation.WithSettings, s with { Tolerances = t });
    }

    [RelayCommand]
    private Task ValidateAll() => ValidateAsync(null);

    [RelayCommand]
    private Task ValidateOne(string set) => ValidateAsync(set);

    /// <summary>
    /// Runs <see cref="ModelValidation.RunFile"/> — every item, or the one of <paramref name="set"/> — off the UI thread, on the
    /// document's current state (exported into its cache). Read-only.
    /// </summary>
    public async Task<bool> ValidateAsync(string? set)
    {
        if (!IsActive) return false;
        if (IsRunning && !_isRefresh) { Warn($"{TaskStage} is running; cancel it or wait for it to finish."); return false; }
        if (!await EnsureWritten()) return false;
        string path = DocumentPath!;
        var settings = ModelValidation.Settings(Document);
        if (set is not null) settings = settings with { DataSets = [set] };
        return await StartTask(set is null ? "Validating" : $"Validating {set}", refresh: false, writesFile: false,
            rc => ModelValidation.RunFile(path, null, settings, rc), r =>
            {
                var result = (ValidationResult)r!;
                Validation = set is null || Validation is null
                    ? result
                    : new ValidationResult(result.ModelPath, [.. Validation.Items.Where(i => i.Set != set), .. result.Items], result.Notes);
                RefreshValidation();
                int failed = result.Items.Count(i => !i.Passed);
                if (failed > 0) Warn($"{failed} of {result.Items.Count} item(s) outside their tolerances.");
                else Info(result.Items.Count > 0 ? $"{result.Items.Count} item(s) within their tolerances." : result.Notes.FirstOrDefault() ?? "Nothing to validate.");
                RebuildPlots();
            });
    }

    /// <summary>Show testbench: writes each item's generated <c>.cnl</c> beside the <c>.cmc</c> and reveals the first.</summary>
    [RelayCommand]
    private void ShowTestbench()
    {
        if (Validation is not { } v || DocumentPath is not { } path) { Warn("Validate first; the testbench is the one a validation ran."); return; }
        try
        {
            var written = ModelValidation.WriteTestbenches(path, v);
            if (written.Count == 0) { Info("No testbench to show."); return; }
            Info($"Wrote {string.Join(", ", written.Select(Path.GetFileName))}.");
            RevealFile?.Invoke(written[0]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(ex.Message); }
    }

    // ── Export (R-mc15-8) ────────────────────────────────────────────────────

    public ObservableCollection<SymbolRow> Symbols { get; } = [];
    [ObservableProperty] private SymbolRow? _selectedSymbol;
    [ObservableProperty] private string _exportPath = "";
    /// <summary>The one line naming the stale steps that block Export; empty when nothing is stale.</summary>
    [ObservableProperty] private string _exportBlocked = "";
    /// <summary>The last export written, which Copy instance places.</summary>
    [ObservableProperty] private string? _exportedModel;
    [ObservableProperty] private string _peripheryText = "";

    /// <summary>Passive: the cell name, the library (or workspace) folder, the cell's parameters.</summary>
    [ObservableProperty] private string _cellName = "";
    [ObservableProperty] private string _cellLibrary = "";
    public ObservableCollection<string> CellParameters { get; } = [];

    public bool CanExport => ExportBlocked.Length == 0;

    partial void OnExportBlockedChanged(string value)
    {
        OnPropertyChanged(nameof(CanExport));
        ExportCommand.NotifyCanExecuteChanged();
    }

    private string? _symbolsKey;

    private void RefreshExport()
    {
        var stale = Loaded.Stale;
        ExportBlocked = stale.Count == 0 ? "" : $"{string.Join(", ", stale)} {(stale.Count == 1 ? "is" : "are")} stale; run {(stale.Count == 1 ? "it" : "them")} first.";
        RunStaleCommand.NotifyCanExecuteChanged();
        if (IsPassive)
        {
            var s = PassiveStep.Settings(Document);
            if (s.Export is { } e) { CellName = e.Cell; CellLibrary = e.Library; }
            CellParameters.Clear();
            if (PassiveStep.Record(Document) is { } r) foreach (string line in r.Report.Take(12)) CellParameters.Add(line);
            return;
        }
        PeripheryText = Document.BasePeriphery is { } p ? $"N0 {p.Fingers} · W0 {p.UnitWidth}" : "N0 and W0 are not set.";
        if (DocumentPath is { } path && ExportPath.Length == 0)
            ExportPath = Path.ChangeExtension(path, CmodelFile.Extension);
        string outDir = ExportPath.Length > 0 ? Path.GetDirectoryName(Path.GetFullPath(ExportPath))! : DocumentFolder ?? "";
        bool thermal = ThermalFit.Heating(Document).SelfHeating;
        string? ws = WorkspaceDirectory ?? (DocumentFolder is { } dir ? WBondSymbolProvider.WorkspaceRootOf(dir) ?? dir : null);
        string key = $"{ws}|{outDir}|{thermal}";
        if (key == _symbolsKey || outDir.Length == 0) return;
        _symbolsKey = key;
        string want = SelectedSymbol?.Value ?? CmodelExport.Settings(Document).Symbol ?? "";
        Symbols.Clear();
        foreach (var c in ModelSymbols.Candidates(ws, outDir, thermal))
            Symbols.Add(new SymbolRow(c.Value, c.Label, c.Pins, SymbolDrawing(c.Value, outDir)));
        SelectedSymbol = Symbols.FirstOrDefault(s => s.Value == want) ?? Symbols.FirstOrDefault();
    }

    /// <summary>A symbol drawn as one placed composed FET carrying it, as the schematic canvas would draw the instance.</summary>
    private static SchematicModel? SymbolDrawing(string value, string outDir)
    {
        try
        {
            var c = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.ComposedFet };
            foreach (var p in ComponentTypeRegistry.DefaultParameters(SymbolKind.ComposedFet, 3))
                c.Parameters.Add(new EditableParameter
                {
                    Name = p.Name, Unit = p.Unit, ShowOnSchematic = false, Dimension = p.Dimension,
                    Expression = p.Name == ComposedFetSymbolProvider.SymbolParameter
                        ? value.EndsWith(".csym", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(Path.Combine(outDir, value)) : value
                        : p.Expression,
                });
            return SchematicPersistence.Deserialize(SchematicPersistence.SerializeSelection([c], [], [])).model.BuildRenderModel().Model;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>Asks where the <c>.cmodel</c> goes, given its suggested name; null when cancelled.</summary>
    public Func<string, Task<string?>>? PickModelPath { get; set; }

    [RelayCommand]
    private async Task PickExportPath()
    {
        if (PickModelPath is null) return;
        string name = ExportPath.Length > 0 ? Path.GetFileNameWithoutExtension(ExportPath) : Path.GetFileNameWithoutExtension(FileName);
        if (await PickModelPath(name) is { } picked)
            ExportPath = picked.EndsWith(CmodelFile.Extension, StringComparison.OrdinalIgnoreCase) ? picked : picked + CmodelFile.Extension;
    }

    partial void OnExportPathChanged(string value) => RefreshExport();

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task Export() => ExportAsync();

    /// <summary>
    /// Export (R-mc15-8): an Active document through <see cref="CmodelExport.Export"/> to <see cref="ExportPath"/> with the
    /// chosen symbol; a Passive one as a cell through <see cref="ModelCommands.ExportCell"/>. A stale step blocks it.
    /// </summary>
    public async Task<bool> ExportAsync()
    {
        if (!CanExport) { Warn(ExportBlocked); return false; }
        if (IsPassive)
        {
            if (CellName.Trim().Length == 0 || CellLibrary.Trim().Length == 0) { Warn("Name the cell and the library it goes in."); return false; }
            string cell = CellName.Trim(), library = CellLibrary.Trim();
            return await RunOnFile("Exporting", (path, _) =>
            {
                string lib = Path.IsPathRooted(library) ? library : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, library));
                var r = ModelCommands.ExportCell(path, lib, cell);
                return [.. r.Lines.Select(l => new ModelLine("export", cell, l))];
            });
        }
        if (ExportPath.Trim().Length == 0) { Warn("Say where the model goes."); return false; }
        string output = Path.GetFullPath(ExportPath.Trim());
        string? symbol = SelectedSymbol is { Value.Length: > 0 } s ? s.Value : null;
        string? created = ExportCreated;
        bool ok = await RunOnFile("Exporting", (path, rc) =>
        {
            var r = CmodelExport.Export(path, output, symbol, created, rc);
            return [.. r.Lines.Select(l => new ModelLine("export", Path.GetFileName(r.Path), l))];
        });
        if (ok) ExportedModel = output;
        return ok;
    }

    /// <summary>Copy instance (R-mc15-8, MC-11 R-mc11-3): the placement payload of the exported model, on the clipboard.</summary>
    [RelayCommand]
    private async Task CopyInstance()
    {
        if (ExportedModel is not { } model || !File.Exists(model)) { Warn("Export the model first; the instance places the exported file."); return; }
        try
        {
            string payload = ComposedFetPlacement.Payload(model, SelectedSymbol is { Value.Length: > 0 } s ? s.Value : null);
            if (CopyInstanceSink is { } sink) await sink(payload);
            Info($"Copied an instance of {Path.GetFileName(model)}.");
        }
        catch (Exception ex) when (ex is CmodelFormatException or IOException) { Error(ex.Message); }
    }

    [RelayCommand(CanExecute = nameof(HasStale))]
    private Task RunStale() => RunStaleAsync();

    private bool HasStale() => Loaded.Stale.Count > 0;

    /// <summary>Run stale (R-mc15-8): re-runs each stale step through <see cref="ModelCommands.Fit"/>, in the order a full fit
    /// runs them, as one task.</summary>
    public Task<bool> RunStaleAsync()
    {
        var stale = Loaded.Stale.Select(s => s.ToLowerInvariant()).ToHashSet();
        var steps = ModelCommands.Steps.Where(stale.Contains).ToList();
        if (steps.Count == 0) { Info("Nothing is stale."); return Task.FromResult(false); }
        return RunOnFile("Running stale steps", (path, rc) =>
        {
            var lines = new List<ModelLine>();
            foreach (string step in steps)
            {
                rc.ThrowIfCancellationRequested();
                lines.AddRange(ModelCommands.Fit(path, step, null, rc));
            }
            return lines;
        });
    }
}
