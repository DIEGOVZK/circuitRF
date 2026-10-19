// brief-sar-12 R-sar12-3/-4/-5 — Band Sweep…: a template setup and its bands, the rated power, duty cycle and exposure, reference
// values and targets of the user's own; Create Setups writes the sweep and one setup per band (one undo entry), Run runs them in
// turn as Simulate does, and the table reads what the runs left. Every rule is C3dBandSweeps'; this is the dialog's state.
//
// A NUMBER ABOVE A REFERENCE VALUE IS COLOURED AND NAMED, NEVER CALLED A FAILURE (overview L1).

using System.Collections.ObjectModel;
using System.Globalization;
using CircuitRF.Design.ThreeD;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ThreeD;

/// <summary>One averaged peak of a band as the table shows it.</summary>
public sealed record BandSweepCell(string Text, bool Above, string? Tip);

/// <summary>One row of the band table.</summary>
public sealed record BandSweepRow(string Band, string Port, BandSweepCell Sar1g, BandSweepCell Sar10g, string Location, string Note);

public sealed partial class BandSweepDialogViewModel : ObservableObject
{
    private readonly C3dEditorViewModel _editor;
    private IReadOnlyList<string> _setups = [];

    public event Action? CloseRequested;

    public BandSweepDialogViewModel(C3dEditorViewModel editor)
    {
        _editor = editor;
        Templates = editor.BandTemplates();
        Existing = [.. editor.BandSweeps.Select(b => b.Name)];
        if (editor.BandSweeps.FirstOrDefault() is { } first) Load(first);
        else
        {
            Name = "Bands";
            Template = Templates.FirstOrDefault();
            BandsText = "0.9, 1.9, 2.45";
        }
        RefreshTable();
    }

    public string Title => "Band Sweep";
    public IReadOnlyList<string> Templates { get; }
    public IReadOnlyList<string> Existing { get; }
    public IReadOnlyList<CircuitRF.Design.ThreeD.C3dDriveReferredTo> ReferredChoices { get; } = Enum.GetValues<CircuitRF.Design.ThreeD.C3dDriveReferredTo>();
    public IReadOnlyList<C3dSarExposure> ExposureChoices { get; } = Enum.GetValues<C3dSarExposure>();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _template;
    [ObservableProperty] private string _bandsText = "";
    [ObservableProperty] private string _powerText = "";
    [ObservableProperty] private CircuitRF.Design.ThreeD.C3dDriveReferredTo _referredTo = CircuitRF.Design.ThreeD.C3dDriveReferredTo.Accepted;
    [ObservableProperty] private string _dutyText = "";
    [ObservableProperty] private C3dSarExposure _exposure = C3dSarExposure.HeadTrunk;
    [ObservableProperty] private string _limitsText = "";
    [ObservableProperty] private string _targetsText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _running;

    public ObservableCollection<BandSweepRow> Rows { get; } = [];
    public ObservableCollection<string> References { get; } = [];

    public const string LimitsTip = "One per line: value in W/kg, mass in g, then its name — 1.0, 10, my company's value.";
    public const string TargetsTip = "One per line: band in GHz, mass in g, then the target in W/kg per W accepted — 1.9, 1, 39.7.";

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    private void Load(C3dBandSweep b)
    {
        Name = b.Name;
        Template = b.Template;
        BandsText = string.Join(", ", b.BandsGHz.Select(G));
        PowerText = b.DrivePowerW is { } p ? G(p) : "";
        ReferredTo = b.DriveReferredTo ?? CircuitRF.Design.ThreeD.C3dDriveReferredTo.Accepted;
        DutyText = b.DutyCycle is { } d ? G(d) : "";
        Exposure = b.Exposure ?? C3dSarExposure.HeadTrunk;
        LimitsText = string.Join("\n", (b.Limits ?? []).Select(l => $"{G(l.ValueWkg)}, {G(l.MassGrams)}, {l.Name}"));
        TargetsText = string.Join("\n", (b.Targets ?? []).Select(t => $"{G(t.FrequencyGHz)}, {G(t.MassGrams)}, {G(t.PerWattWkg)}"));
    }

    /// <summary>The sweep the fields make, or why not.</summary>
    public (C3dBandSweep? Sweep, string? Error) Sweep()
    {
        static bool Num(string s, out double v) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
        var bands = new List<double>();
        foreach (string part in BandsText.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
            if (Num(part, out double f)) bands.Add(f); else return (null, $"'{part}' is not a band in GHz.");
        double? power = null, duty = null;
        if (PowerText.Trim().Length > 0) { if (Num(PowerText, out double p)) power = p; else return (null, $"'{PowerText}' is not a power in W."); }
        if (DutyText.Trim().Length > 0) { if (Num(DutyText, out double d)) duty = d; else return (null, $"'{DutyText}' is not a duty cycle."); }
        var limits = new List<C3dSarLimit>();
        foreach (string line in LimitsText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(',', 3, StringSplitOptions.TrimEntries);
            if (f.Length < 3 || !Num(f[0], out double v) || !Num(f[1], out double m) || f[2].Length == 0) return (null, $"'{line}': {LimitsTip}");
            limits.Add(new C3dSarLimit { Name = f[2], ValueWkg = v, MassGrams = m, Exposure = Exposure, Source = "the user's own", Date = "" });
        }
        var targets = new List<C3dSarTarget>();
        foreach (string line in TargetsText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(',', StringSplitOptions.TrimEntries);
            if (f.Length != 3 || !Num(f[0], out double g) || !Num(f[1], out double m) || !Num(f[2], out double t)) return (null, $"'{line}': {TargetsTip}");
            targets.Add(new C3dSarTarget { FrequencyGHz = g, MassGrams = m, PerWattWkg = t });
        }
        return (new C3dBandSweep
        {
            Name = Name.Trim(), Template = Template ?? "", BandsGHz = bands, DrivePowerW = power,
            DriveReferredTo = ReferredTo == CircuitRF.Design.ThreeD.C3dDriveReferredTo.Accepted ? null : ReferredTo, DutyCycle = duty,
            Exposure = Exposure == C3dSarExposure.HeadTrunk ? null : Exposure,
            Limits = limits.Count > 0 ? limits : null, Targets = targets.Count > 0 ? targets : null,
        }, null);
    }

    /// <summary>Writes the sweep and its setups: one undo entry.</summary>
    [RelayCommand]
    private void CreateSetups()
    {
        var (sweep, error) = Sweep();
        if (error is null) (_setups, error) = _editor.SaveBandSweep(sweep!);
        Error = error;
        if (error is null) Status = $"Wrote {_setups.Count} setup(s): {string.Join(", ", _setups)}.";
        RefreshTable();
    }

    /// <summary>Writes the sweep and its setups, then runs every band in turn.</summary>
    [RelayCommand]
    private async Task Run()
    {
        CreateSetups();
        if (Error is not null || _setups.Count == 0) return;
        Running = true;
        try { await _editor.RunBandSweepAsync(_setups); }
        finally { Running = false; }
        RefreshTable();
    }

    [RelayCommand]
    private void RefreshTable()
    {
        Rows.Clear();
        References.Clear();
        var (sweep, _) = Sweep();
        if (sweep is null) return;
        foreach (var r in _editor.BandTable(sweep)) Rows.Add(Row(r));
        foreach (var l in C3dBandSweeps.Limits(sweep)) References.Add($"{l.Name}: {l.Source}" + (l.Date.Length > 0 ? $", {l.Date}" : ""));
    }

    /// <summary>A table row: the value at the rated power, coloured and named when above a reference value.</summary>
    public static BandSweepRow Row(C3dBandSarRow r)
    {
        string N(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
        BandSweepCell Cell(double mass)
        {
            if (r.Values.FirstOrDefault(v => v.MassGrams == mass) is not { } v) return new BandSweepCell("", false, null);
            string text = $"{N(v.AtRatedWkg)} W/kg ({N(v.PerWattWkg)} per W)" +
                          (v.FromTarget is { } t ? $", {(t >= 0 ? "+" : "")}{(t * 100).ToString("0.0", CultureInfo.InvariantCulture)} % from target" : "");
            return new BandSweepCell(text, v.Above.Count > 0, v.Above.Count > 0 ? "Above the reference value " + string.Join("; ", v.Above.Select(l => l.Name)) : null);
        }
        var peak = r.Values.FirstOrDefault(v => v.MassGrams == 1) ?? r.Values.FirstOrDefault();
        string where = peak is null ? "" : "(" + string.Join(", ", peak.CentreM.Select(m => (m * 1e3).ToString("0.#", CultureInfo.InvariantCulture))) + ") mm" +
                                         (peak.Object is { } o ? $", {o}" : "");
        string note = r.Missing ?? string.Join("; ", r.Values.SelectMany(v => v.Above).Select(l => "above " + l.Name).Distinct());
        return new BandSweepRow(CircuitRF.Design.Layout.MaterialDispersion.Hz(r.FrequencyHz), r.Port > 0 ? r.Port.ToString(CultureInfo.InvariantCulture) : "",
                                Cell(1), Cell(10), where, note);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
