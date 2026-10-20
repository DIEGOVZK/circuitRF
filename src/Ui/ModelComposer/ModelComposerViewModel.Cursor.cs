// The bias cursor (brief-modelcomposer-14 R-mc14-2): Vgs, Vds, the ambient temperature and the quiescent point, on the
// toolbar, driving every plot. Each list holds MEASURED values only, so the cursor snaps to measured points by
// construction, and a ComboBox's own arrow keys step it. Kept in the .cmc's View block as view state — not an edit.
//
// The lists are replaced only when their contents change, and a selection is published after its list: Avalonia clears a
// ComboBox selection its items do not contain (src/Ui/CLAUDE.md).

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Modeling;

namespace CircuitRF.Ui.ModelComposer;

/// <summary>One cursor value: what it is, and how it reads.</summary>
public sealed record CursorChoice(double Value, string Text, QuiescentBias? Quiescent = null)
{
    public override string ToString() => Text;
}

public sealed partial class ModelComposerViewModel
{
    public IReadOnlyList<CursorChoice> VgsChoices { get; private set; } = [];
    public IReadOnlyList<CursorChoice> VdsChoices { get; private set; } = [];
    public IReadOnlyList<CursorChoice> TChoices { get; private set; } = [];
    public IReadOnlyList<CursorChoice> QChoices { get; private set; } = [];

    private CursorChoice? _selectedVgs, _selectedVds, _selectedT, _selectedQ;
    private bool _syncingCursor;

    public CursorChoice? SelectedVgs { get => _selectedVgs; set => SetCursor(ref _selectedVgs, value, nameof(SelectedVgs)); }
    public CursorChoice? SelectedVds { get => _selectedVds; set => SetCursor(ref _selectedVds, value, nameof(SelectedVds)); }
    public CursorChoice? SelectedT { get => _selectedT; set => SetCursor(ref _selectedT, value, nameof(SelectedT)); }
    public CursorChoice? SelectedQ { get => _selectedQ; set => SetCursor(ref _selectedQ, value, nameof(SelectedQ)); }

    private void SetCursor(ref CursorChoice? field, CursorChoice? value, string name)
    {
        if (value is null && !_syncingCursor) { OnPropertyChanged(name); return; }   // a ComboBox clearing itself
        if (Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
        if (_syncingCursor) return;
        SetView(Document.View with { Cursor = CurrentCursor() });
        RebuildPlots();
    }

    /// <summary>The cursor as the document stores it.</summary>
    public CmcBiasCursor CurrentCursor() =>
        new(SelectedVgs?.Value, SelectedVds?.Value, SelectedT?.Value, SelectedQ?.Quiescent);

    /// <summary>The points the cursor's lists are made from: the selected data set's, else every set's.</summary>
    private IEnumerable<(CmcDataSet Set, NormalisedData Data)> CursorSets()
    {
        var all = Document.DataSets.Where(s => Loaded.Data.ContainsKey(s.Name)).Select(s => (s, Loaded.Data[s.Name])).ToList();
        var chosen = all.Where(x => x.s.Name == SelectedDataSet?.Name).ToList();
        return chosen.Count > 0 ? chosen : all;
    }

    /// <summary>Rebuilds the four lists from the loaded data and snaps the cursor onto them.</summary>
    private void RefreshCursorChoices()
    {
        var sets = CursorSets().ToList();
        var vgs = Distinct(sets.SelectMany(x => x.Data.Points.Select(p => p.Vgs)));
        var vds = Distinct(sets.SelectMany(x => x.Data.Points.Select(p => p.Vds)));
        var t = Distinct(Document.DataSets.Select(s => s.Conditions.AmbientC ?? double.NaN));
        var q = Document.DataSets.Select(s => s.Conditions.Quiescent).OfType<QuiescentBias>()
                        .Select(b => new QuiescentBias(b.Vgs, b.Vds)).Distinct().ToList();

        var stored = Document.View.Cursor;
        _syncingCursor = true;
        try
        {
            Publish(nameof(VgsChoices), VgsChoices, Choices(vgs, "V"), l => VgsChoices = l);
            Publish(nameof(VdsChoices), VdsChoices, Choices(vds, "V"), l => VdsChoices = l);
            Publish(nameof(TChoices), TChoices, Choices(t, "°C"), l => TChoices = l);
            var qList = new List<CursorChoice> { new(double.NaN, "CW") };
            qList.AddRange(q.Select((b, i) => new CursorChoice(i, $"{ModelValueFormat.G(b.Vgs)}, {ModelValueFormat.G(b.Vds)} V", b)));
            Publish(nameof(QChoices), QChoices, q.Count == 0 ? [] : qList, l => QChoices = l);

            SelectedVgs = Snap(VgsChoices, _selectedVgs?.Value ?? stored?.Vgs);
            SelectedVds = Snap(VdsChoices, _selectedVds?.Value ?? stored?.Vds);
            SelectedT = Snap(TChoices, _selectedT?.Value ?? stored?.AmbientC);
            var wantQ = _selectedQ?.Quiescent ?? stored?.Quiescent;
            SelectedQ = QChoices.FirstOrDefault(c => c.Quiescent is { } b && wantQ is { } w && b.Vgs == w.Vgs && b.Vds == w.Vds)
                        ?? QChoices.FirstOrDefault();
        }
        finally { _syncingCursor = false; }
    }

    private void Publish(string name, IReadOnlyList<CursorChoice> old, List<CursorChoice> next, Action<IReadOnlyList<CursorChoice>> set)
    {
        if (old.Select(c => c.Text).SequenceEqual(next.Select(c => c.Text))) return;
        set(next);
        OnPropertyChanged(name);
    }

    private static List<double> Distinct(IEnumerable<double> values) =>
        [.. values.Where(double.IsFinite).Select(v => Math.Round(v, 6)).Distinct().Order()];

    private static List<CursorChoice> Choices(IEnumerable<double> values, string unit) =>
        [.. values.Select(v => new CursorChoice(v, $"{ModelValueFormat.G(v)} {unit}"))];

    /// <summary>The choice nearest <paramref name="want"/>, or the middle one when nothing is wanted.</summary>
    private static CursorChoice? Snap(IReadOnlyList<CursorChoice> choices, double? want)
    {
        if (choices.Count == 0) return null;
        if (want is not { } w || !double.IsFinite(w)) return choices[choices.Count / 2];
        return choices.MinBy(c => Math.Abs(c.Value - w));
    }

    /// <summary>
    /// The point of <paramref name="data"/> nearest the cursor's bias, or −1 when it has none. Points at another ambient
    /// temperature or quiescent point than the cursor's are passed over when the set states its own.
    /// </summary>
    public int CursorPoint(NormalisedData data)
    {
        if (data.Count == 0) return -1;
        double vgs = SelectedVgs?.Value ?? 0, vds = SelectedVds?.Value ?? 0;
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < data.Count; i++)
        {
            var p = data.Points[i];
            double d = Math.Abs(p.Vgs - vgs) + Math.Abs(p.Vds - vds);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }
}
