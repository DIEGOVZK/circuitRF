// The Data step (brief-modelcomposer-14 R-mc14-6): the data-set list, the importer, exclusion on the bias plane, and the
// base periphery. An exclusion and the periphery are edits (one undo step each); an import is a run.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.ModelComposer;

public sealed partial class ModelComposerViewModel
{
    /// <summary>Opens the importer for the document; null when the user cancelled it. Supplied by the window.</summary>
    public Func<ModelImportViewModel, Task<CmcDataSet?>>? ShowImporter { get; set; }

    public ObservableCollection<DataSetRow> DataSetRows { get; } = [];

    private DataSetRow? _selectedDataSet;

    /// <summary>The data set the plots and the cursor's lists follow.</summary>
    public DataSetRow? SelectedDataSet
    {
        get => _selectedDataSet;
        set
        {
            if (value is null && DataSetRows.Count > 0 && _selectedDataSet is not null && DataSetRows.Contains(_selectedDataSet)) { OnPropertyChanged(); return; }
            if (Equals(_selectedDataSet, value)) return;
            _selectedDataSet = value;
            OnPropertyChanged();
            RefreshCursorChoices();
            RebuildPlots();
        }
    }

    private void RefreshDataSets()
    {
        var rows = Document.DataSets.Select(s => DataSetRow.From(s, Loaded.Data.GetValueOrDefault(s.Name))).ToList();
        if (!rows.SequenceEqual(DataSetRows))
        {
            string? keep = _selectedDataSet?.Name;
            DataSetRows.Clear();
            foreach (var r in rows) DataSetRows.Add(r);
            _selectedDataSet = DataSetRows.FirstOrDefault(r => r.Name == keep) ?? DataSetRows.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedDataSet));
        }
        OnPropertyChanged(nameof(HasDataSets));
    }

    public bool HasDataSets => DataSetRows.Count > 0;

    /// <summary>The importer, then the import as a run through <see cref="ModelCommands.Import"/>.</summary>
    public async Task<bool> ImportAsync()
    {
        if (IsRunning && !_isRefresh)
        {
            Warn($"{TaskStage} is running; cancel it or wait for it to finish.");
            return false;
        }
        if (DocumentPath is null && !await SaveAsync(saveAs: false)) return false;
        if (ShowImporter is null) return false;
        var set = await ShowImporter(new ModelImportViewModel(DocumentPath!, Document));
        if (set is null) return false;
        return await ImportAsync(set);
    }

    /// <summary>Imports <paramref name="set"/> — the dialog's answer, or a test's.</summary>
    public async Task<bool> ImportAsync(CmcDataSet set)
    {
        bool ok = await RunOnFile($"Importing {set.Name}", (path, rc) => ModelCommands.Import(path, set, rc));
        if (ok && DataSetRows.FirstOrDefault(r => r.Name == set.Name) is { } row) SelectedDataSet = row;
        return ok;
    }

    // ── exclusion (R-mc14-6) ─────────────────────────────────────────────────

    /// <summary>What a click or lasso marks a point valid with, rather than excluding it (owner, review 4: putting a run of
    /// excluded points back was one click per point).</summary>
    public const string ValidReason = "Valid";

    /// <summary>The short list a reason is chosen from, and <see cref="ValidReason"/> first.</summary>
    public IReadOnlyList<string> ExclusionReasons { get; } = [ValidReason, "Outlier", "Oscillation", "Compliance", "Drift", "Breakdown", "Other"];

    [ObservableProperty] private string _exclusionReason = "Outlier";

    /// <summary>True while a click or lasso on the bias plane excludes points (the × lamp).</summary>
    [ObservableProperty] private bool _excludeArmed;

    partial void OnExcludeArmedChanged(bool value) => BiasPlaneLasso.Armed = value;

    /// <summary>
    /// Excludes <paramref name="points"/> with the chosen reason — or, when every one of them is already excluded or the
    /// reason is <see cref="ValidReason"/>, includes them again. One undo step either way.
    /// </summary>
    public bool ToggleExclusion(IReadOnlyList<(string Set, string Point)> points)
    {
        if (points.Count == 0) return false;
        var bySet = points.GroupBy(p => p.Set).ToList();
        bool allExcluded = bySet.All(g => Document.DataSets.FirstOrDefault(s => s.Name == g.Key) is { } set
                                          && g.All(p => set.Exclusions.Any(x => x.Point == p.Point)));
        bool include = allExcluded || ExclusionReason == ValidReason;
        string label = include ? $"Include {points.Count} point(s)" : $"Exclude {points.Count} point(s): {ExclusionReason}";
        string reason = ExclusionReason;
        return EditPart(label, d => d.DataSets, (d, sets) => d with { DataSets = sets },
            [.. Document.DataSets.Select(s =>
            {
                var mine = bySet.FirstOrDefault(g => g.Key == s.Name);
                if (mine is null) return s;
                var ids = mine.Select(p => p.Point).ToHashSet(StringComparer.Ordinal);
                var kept = s.Exclusions.Where(x => !ids.Contains(x.Point)).ToList();
                if (!include) kept.AddRange(ids.Order(StringComparer.Ordinal).Select(id => new PointExclusion(id, reason)));
                return s with { Exclusions = kept };
            })]);
    }

    /// <summary>A point's exclusion, or null.</summary>
    public PointExclusion? ExclusionOf(string set, string point) =>
        Document.DataSets.FirstOrDefault(s => s.Name == set)?.Exclusions.FirstOrDefault(x => x.Point == point);

    // ── base periphery (R-mc14-6; D9) ────────────────────────────────────────

    [ObservableProperty] private string _peripheryFingers = "";
    [ObservableProperty] private string _peripheryWidth = "";
    private bool _loadingPeriphery;

    /// <summary>The one warning the periphery's absence causes; empty when it is set or the document is Passive.</summary>
    public string PeripheryWarning => IsActive && Document.BasePeriphery is null ? "N0 and W0 are not set; export needs them." : "";

    private void RefreshPeriphery()
    {
        _loadingPeriphery = true;
        try
        {
            PeripheryFingers = Document.BasePeriphery?.Fingers.ToString(CultureInfo.InvariantCulture) ?? "";
            PeripheryWidth = Document.BasePeriphery?.UnitWidth ?? "";
        }
        finally { _loadingPeriphery = false; }
        OnPropertyChanged(nameof(PeripheryWarning));
    }

    partial void OnPeripheryFingersChanged(string value) => CommitPeriphery();
    partial void OnPeripheryWidthChanged(string value) => CommitPeriphery();

    private void CommitPeriphery()
    {
        if (_loadingPeriphery) return;
        string f = PeripheryFingers.Trim(), w = PeripheryWidth.Trim();
        if (f.Length == 0 && w.Length == 0)
        {
            if (Document.BasePeriphery is not null)
                EditPart("Clear N0 and W0", d => d.BasePeriphery, (d, v) => d with { BasePeriphery = v }, (BasePeriphery?)null);
            return;
        }
        if (f.Length == 0 || w.Length == 0) return;      // the other half is still to be typed
        if (!int.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0)
        {
            Error($"N0: '{f}' is not a positive whole number.");
            RefreshPeriphery();
            return;
        }
        var next = new BasePeriphery(n, w);
        if (next == Document.BasePeriphery) return;
        EditPart($"N0 {n}, W0 {w}", d => d.BasePeriphery, (d, v) => d with { BasePeriphery = v }, (BasePeriphery?)next);
    }
}
