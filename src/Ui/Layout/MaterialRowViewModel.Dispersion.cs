using System.Globalization;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Layout;

// brief-sar-2 R-sar2-7 — a frequency-dependent material's readout: εr and σ "at f", and a small εr/σ-against-frequency plot.
// Every number comes from MaterialDispersion, the rule a 3D run evaluates with, so the panel cannot show one value while
// Simulate uses another. The model itself is edited in the file; the panel reads it.

public sealed partial class MaterialsTableViewModel
{
    /// <summary>The frequency every row's readout is shown at — one for the table, so it stays put as the selection moves. A
    /// host that knows the active setup's evaluation frequency sets it; otherwise 1 GHz, where the plain numbers are stated.</summary>
    [ObservableProperty] private double _readoutFrequencyHz = MaterialDispersion.PlainNumbersFrequencyHz;
}

public sealed partial class MaterialRowViewModel
{
    /// <summary>The plot's drawing box, in device-independent pixels.</summary>
    public const double PlotWidth = 300, PlotHeight = 90;

    /// <summary>A Cole-Cole record is plotted over these, a table over its own rows.</summary>
    private const double PlotLoHz = 10e6, PlotHiHz = 10e9;

    private const int PlotSamples = 60;

    /// <summary>The material states a frequency-dependent εr and σ.</summary>
    public bool IsDispersive => MaterialDispersion.IsDispersive(Material);

    /// <summary>What the model is, in one line.</summary>
    public string DispersionSummary => Material switch
    {
        { ColeCole: { } cc } => $"A {cc.Terms.Count}-term Cole-Cole model: εr and σ follow frequency. A 3D run evaluates it once, " +
                                "at the setup's frequency; εr and σ₂₀ above are its values at 1 GHz, for a build that does not read it.",
        { DielectricVsFrequency: { Count: > 0 } t } =>
            $"Target values at {t.Count} frequencies, {MaterialDispersion.Hz(t[0].FrequencyHz)} to {MaterialDispersion.Hz(t[^1].FrequencyHz)}, " +
            "interpolated between rows and refused outside them. εr and σ₂₀ above are the values at 1 GHz.",
        _ => "",
    };

    /// <summary>The readout's frequency, as typed: <c>298M</c>, <c>298 MHz</c>, <c>1.8e9</c>.</summary>
    public string ReadoutFrequencyText
    {
        get => MaterialDispersion.Hz(_table.ReadoutFrequencyHz);
        set
        {
            string t = (value ?? "").Trim();
            if (t.EndsWith("hz", StringComparison.OrdinalIgnoreCase)) t = t[..^2].TrimEnd();
            if (MaterialsTableViewModel.TryParse(t, out double? f) && f is > 0) _table.ReadoutFrequencyHz = f.Value;
            OnPropertyChanged(nameof(ReadoutFrequencyText));
            OnPropertyChanged(nameof(ReadoutText));
            OnPropertyChanged(nameof(MarkerX));
            OnPropertyChanged(nameof(HasMarker));
        }
    }

    /// <summary>εr and σ at the readout frequency, or why there are none.</summary>
    public string ReadoutText
        => MaterialDispersion.At(Material, _table.ReadoutFrequencyHz, out string? refusal) is { } v
            ? string.Create(CultureInfo.InvariantCulture, $"εr {v.Epsr:G4}    σ {v.SigmaSm:G4} S/m")
            : refusal ?? "";

    private (double Lo, double Hi) PlotRange
        => Material.DielectricVsFrequency is { Count: > 1 } t && t[^1].FrequencyHz > t[0].FrequencyHz
            ? (t[0].FrequencyHz, t[^1].FrequencyHz) : (PlotLoHz, PlotHiHz);

    private (double F, double Epsr, double Sigma)[] Samples()
    {
        var (lo, hi) = PlotRange;
        var list = new List<(double, double, double)>();
        for (int i = 0; i < PlotSamples; i++)
        {
            double f = lo * Math.Pow(hi / lo, i / (double)(PlotSamples - 1));
            if (MaterialDispersion.At(Material, f, out _) is { } v) list.Add((f, v.Epsr, v.SigmaSm));
        }
        return [.. list];
    }

    private double X(double f) { var (lo, hi) = PlotRange; return PlotWidth * Math.Log(f / lo) / Math.Log(hi / lo); }

    private static IList<Point> Line(IEnumerable<(double X, double V)> pts, double min, double max)
        => [.. pts.Select(p => new Point(p.X, max > min ? PlotHeight * (1 - (p.V - min) / (max - min)) : PlotHeight / 2))];

    /// <summary>εr against log frequency, scaled to its own range.</summary>
    public IList<Point> EpsrPoints
    {
        get { var s = Samples(); return s.Length == 0 ? [] : Line(s.Select(p => (X(p.F), p.Epsr)), s.Min(p => p.Epsr), s.Max(p => p.Epsr)); }
    }

    /// <summary>σ against log frequency, scaled to its own range.</summary>
    public IList<Point> SigmaPoints
    {
        get { var s = Samples(); return s.Length == 0 ? [] : Line(s.Select(p => (X(p.F), p.Sigma)), s.Min(p => p.Sigma), s.Max(p => p.Sigma)); }
    }

    /// <summary>The plot's legend: each curve's range, and the frequency span.</summary>
    public string PlotLegend
    {
        get
        {
            var s = Samples();
            if (s.Length == 0) return "";
            var (lo, hi) = PlotRange;
            return string.Create(CultureInfo.InvariantCulture,
                $"εr {s.Min(p => p.Epsr):G3} to {s.Max(p => p.Epsr):G3}  ·  σ {s.Min(p => p.Sigma):G3} to {s.Max(p => p.Sigma):G3} S/m  ·  {MaterialDispersion.Hz(lo)} to {MaterialDispersion.Hz(hi)}, log");
        }
    }

    /// <summary>The readout frequency on the plot, while it is inside the plotted span.</summary>
    public bool HasMarker { get { var (lo, hi) = PlotRange; return _table.ReadoutFrequencyHz >= lo && _table.ReadoutFrequencyHz <= hi; } }

    public double MarkerX => HasMarker ? X(_table.ReadoutFrequencyHz) : 0;
}
