// The rows the Model Composer window lists (brief-modelcomposer-14 R-mc14-2, R-mc14-6, R-mc14-8, R-mc14-9): the step
// rail, the data sets, the network values and the passive proposals. Each is a projection of the document the view model
// rebuilds after an edit or a run; a settable cell hands its text back to the view model, which makes the edit — one undo
// step — so a row never holds a second copy of what the document says.

using System;
using System.Globalization;
using CircuitRF.Design.Modeling;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace CircuitRF.Ui.ModelComposer;

/// <summary>One step on the rail: its name, its glyph, whether it can be opened yet and why not, and a stale marker.</summary>
public sealed partial class ModelStepItem(string name, MaterialIconKind icon) : ObservableObject
{
    public string Name { get; } = name;
    public MaterialIconKind Icon { get; } = icon;

    [ObservableProperty] private bool _isEnabled = true;
    /// <summary>One sentence, shown as the tooltip of a disabled step; empty when the step can be opened.</summary>
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private bool _isStale;
    [ObservableProperty] private bool _isSelected;

    public string Tip => IsEnabled ? Name : Reason;

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(Tip));
    partial void OnReasonChanged(string value) => OnPropertyChanged(nameof(Tip));
}

/// <summary>One data set in the list (R-mc14-6). Read-only: a set's conditions are stated when it is imported.</summary>
public sealed record DataSetRow(string Name, string Kind, string Ambient, string Plane, string Quiescent, string PrePulse, string Points)
{
    public static DataSetRow From(CmcDataSet set, NormalisedData? data)
    {
        var c = set.Conditions;
        return new DataSetRow(
            set.Name,
            set.Kind.ToString(),
            c.AmbientC is { } t ? $"{ModelValueFormat.G(t)} °C" : "—",
            c.ReferencePlane?.ToString().ToLowerInvariant() ?? "—",
            c.Quiescent is { } q ? $"{ModelValueFormat.G(q.Vgs)}, {ModelValueFormat.G(q.Vds)} V" : "—",
            c.PrePulse is { } p ? $"{ModelValueFormat.G(p.Vgs)}, {ModelValueFormat.G(p.Vds)} V" : "",
            data is null ? "—" : data.Count.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// One network variable in the values table (R-mc14-8): value, lock, bounds, tie and scaling rule, and after a fit its
/// ± error and identifiability. Edits go back through <see cref="Commit"/>, which the view model owns.
/// </summary>
public sealed partial class NetworkValueRow : ObservableObject
{
    private readonly Action<NetworkValueRow, string, string> _commit;
    private bool _loading;

    public NetworkValueRow(string name, NetworkElementKind kind, Action<NetworkValueRow, string, string> commit)
    {
        Name = name;
        Kind = kind;
        _commit = commit;
    }

    public string Name { get; }
    public NetworkElementKind Kind { get; }
    public string Unit => ModelValueFormat.UnitOf(Kind);

    [ObservableProperty] private string _value = "";
    [ObservableProperty] private bool _lock;
    [ObservableProperty] private string _min = "";
    [ObservableProperty] private string _max = "";
    [ObservableProperty] private string _tie = "";
    [ObservableProperty] private string _scaling = "";
    /// <summary>The last fit's ± standard error, as a value; empty before a fit or for a held value.</summary>
    [ObservableProperty] private string _error = "";
    /// <summary>True when the last fit left this value poorly determined.</summary>
    [ObservableProperty] private bool _poor;
    /// <summary>The correlated partner and its correlation, or the bound it sits at — the poor marker's tooltip.</summary>
    [ObservableProperty] private string _poorTip = "";

    /// <summary>Sets every cell from the document without committing anything back.</summary>
    public void Load(string value, bool locked, string min, string max, string tie, string scaling, string error, bool poor, string poorTip)
    {
        _loading = true;
        try
        {
            Value = value; Lock = locked; Min = min; Max = max; Tie = tie; Scaling = scaling;
            Error = error; Poor = poor; PoorTip = poorTip;
        }
        finally { _loading = false; }
    }

    partial void OnValueChanged(string value) => Commit(nameof(Value), value);
    partial void OnLockChanged(bool value) => Commit(nameof(Lock), value ? "true" : "false");
    partial void OnMinChanged(string value) => Commit(nameof(Min), value);
    partial void OnMaxChanged(string value) => Commit(nameof(Max), value);
    partial void OnTieChanged(string value) => Commit(nameof(Tie), value);
    partial void OnScalingChanged(string value) => Commit(nameof(Scaling), value);

    private void Commit(string field, string text)
    {
        if (!_loading) _commit(this, field, text);
    }
}

/// <summary>One passive topology proposal (R-mc14-9): its rank, title, score and the fitted values, for its thumbnail.</summary>
public sealed record ProposalRow(int Rank, string Topology, string Title, string Score, SchematicModel? Drawing);
