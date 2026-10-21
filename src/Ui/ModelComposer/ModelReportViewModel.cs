// The Model Validation Report dialog (brief-modelcomposer-15 R-mc15-7a): every option of MC-11 R-mc11-6 — the sections, the
// S biases, the fixed voltages of the C–V families, the PA classes, the frequency, bias, R_opt and the source's
// fundamental and 2f0 terminations, the load disc and the drive ladder — each opening on the value stored in the .cmc or its
// default. Apply writes them back to Steps.Report as one undo step; Generate runs MC-11's function through the window's one
// background task, and Export PDF draws the report's display. The dialog parses text into a ReportOptions and nothing else:
// every rule about a value is the document's own, applied when the edit is validated.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Design.Modeling;
using CircuitRF.Harmonica;
using CircuitRF.Render.DataDisplay;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ModelComposer;

public sealed partial class ModelReportViewModel : ObservableObject
{
    public ModelComposerViewModel Owner { get; }

    public ModelReportViewModel(ModelComposerViewModel owner)
    {
        Owner = owner;
        Load(ModelValidationReport.Options(owner.Document));
        var c = owner.ReportChoicesNow();
        FrequencyChoices = [.. c.FrequenciesGHz.Select(N)];
        VgsChoices = [.. c.Vgs.Select(N)];
        VdsChoices = [.. c.Vds.Select(N)];
        FrequencyPlaceholder = $"{N(c.DefaultFrequencyGHz)} — the S data's median";
        VdsPlaceholder = $"{N(c.DefaultVds)} — half the largest measured";

        SBiasItems = Picks(c.SBiases.Select(b => $"{N(b.Vgs)}, {N(b.Vds)}"), SBiases, ';', () => SBiases = Join(SBiasItems!, "; "),
                           t => t.Split(',', StringSplitOptions.TrimEntries) is [var g, var d] ? $"Vgs {g} V, Vds {d} V" : t);
        CvVdsItems = Picks(c.CvVds.Select(N), FixedVds, ',', () => FixedVds = Join(CvVdsItems!, ", "), t => $"Vds {t} V");
        CvVgsItems = Picks(c.CvVgs.Select(N), FixedVgs, ',', () => FixedVgs = Join(CvVgsItems!, ", "), t => $"Vgs {t} V");

        // The S biases as the bias plane they are: drain voltage across, gate voltage down from the highest, a box where a
        // bias was measured — unless the plan is so irregular that the matrix would be mostly empty boxes, when the biases
        // are typed. A chosen bias the data does not have stays as a chip below the matrix.
        var vdsAxis = c.SBiases.Select(b => b.Vds).Distinct().Order().ToList();
        var vgsAxis = c.SBiases.Select(b => b.Vgs).Distinct().OrderDescending().ToList();
        if (vdsAxis.Count * vgsAxis.Count <= MaxMatrixCells)
        {
            var byText = SBiasItems.ToDictionary(i => i.Text);
            SBiasColumns = [.. vdsAxis.Select(N)];
            SBiasRows = [.. vgsAxis.Select(g => new BiasRow(N(g),
                [.. vdsAxis.Select(d => byText.GetValueOrDefault($"{N(g)}, {N(d)}") ?? PickItem.Absent)]))];
        }
        SBiasOthers = [.. SBiasItems.Skip(c.SBiases.Count)];
    }

    // ── choices (owner, review 3) ────────────────────────────────────────────
    //
    // The frequency and the PA bias are editable lists of the measured values — a typed value still reads — and an empty
    // one shows the value the report will take in its place.

    /// <summary>The S data's frequencies, GHz.</summary>
    public IReadOnlyList<string> FrequencyChoices { get; }
    /// <summary>The I–V data's gate and drain voltages, for the PA bias.</summary>
    public IReadOnlyList<string> VgsChoices { get; }
    public IReadOnlyList<string> VdsChoices { get; }

    /// <summary>What an empty frequency or drain voltage means, as the value the report will take.</summary>
    public string FrequencyPlaceholder { get; }
    public string VdsPlaceholder { get; }
    public const string VgsPlaceholder = "Class B: Id at 2 % of the largest";

    /// <summary>The S data's biases, "vgs, vds", then any chosen earlier that the data does not have.</summary>
    public IReadOnlyList<PickItem> SBiasItems { get; }
    /// <summary>The S data's drain and gate voltages, for the C–V families.</summary>
    public IReadOnlyList<PickItem> CvVdsItems { get; }
    public IReadOnlyList<PickItem> CvVgsItems { get; }

    /// <summary>The S-bias matrix: its drain-voltage columns, and a row per gate voltage, highest first. Empty when the plan
    /// would need more than <see cref="MaxMatrixCells"/> boxes; the dialog then shows the typed field.</summary>
    public IReadOnlyList<string> SBiasColumns { get; } = [];
    public IReadOnlyList<BiasRow> SBiasRows { get; } = [];
    /// <summary>Chosen S biases the data does not have — kept, and shown so they can be cleared.</summary>
    public IReadOnlyList<PickItem> SBiasOthers { get; }

    /// <summary>The most boxes the matrix builds (50 × 50). It scrolls inside a capped area; beyond this, a plan is
    /// irregular enough that most boxes would be empty, and the biases are typed instead.</summary>
    public const int MaxMatrixCells = 2500;
    /// <summary>The most voltages offered as chips; beyond it the list is typed.</summary>
    public const int MaxChips = 24;

    public bool SBiasAsMatrix => SBiasRows.Count > 0;
    public bool SBiasAsText => SBiasRows.Count == 0 && SBiasItems.Count > SBiasOthers.Count;
    public bool HasSBiasOthers => SBiasOthers.Count > 0;
    public bool CvVdsAsChips => CvVdsItems.Count is > 0 and <= MaxChips;
    public bool CvVdsAsText => CvVdsItems.Count > MaxChips;
    public bool CvVgsAsChips => CvVgsItems.Count is > 0 and <= MaxChips;
    public bool CvVgsAsText => CvVgsItems.Count > MaxChips;

    public const string SBiasesDefault = "lowest, middle and highest current", SpreadDefault = "five spread over the data";

    /// <summary>What each choice means now, under it: how many are chosen, or the default nothing chosen falls back to.</summary>
    public string SBiasesSummary => Count(SBiases, ';') is var n and > 0 ? $"{n} chosen" : $"None chosen: {SBiasesDefault}";
    public string FixedVdsSummary => Count(FixedVds, ',') is var n and > 0 ? $"{n} chosen" : $"None chosen: {SpreadDefault}";
    public string FixedVgsSummary => Count(FixedVgs, ',') is var n and > 0 ? $"{n} chosen" : $"None chosen: {SpreadDefault}";

    private static int Count(string text, char separator) => text.Split(separator, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Whether each choice has anything to clear: its Clear shows only then.</summary>
    public bool AnySBiases => SBiases.Trim().Length > 0;
    public bool AnyFixedVds => FixedVds.Trim().Length > 0;
    public bool AnyFixedVgs => FixedVgs.Trim().Length > 0;

    partial void OnSBiasesChanged(string value) { OnPropertyChanged(nameof(SBiasesSummary)); OnPropertyChanged(nameof(AnySBiases)); }
    partial void OnFixedVdsChanged(string value) { OnPropertyChanged(nameof(FixedVdsSummary)); OnPropertyChanged(nameof(AnyFixedVds)); }
    partial void OnFixedVgsChanged(string value) { OnPropertyChanged(nameof(FixedVgsSummary)); OnPropertyChanged(nameof(AnyFixedVgs)); }

    /// <summary>Clears one choice — its boxes or chips, and the text they write.</summary>
    [RelayCommand]
    private void ClearPicks(string which)
    {
        var items = which switch { "s" => SBiasItems, "vds" => CvVdsItems, _ => CvVgsItems };
        foreach (var i in items) i.IsChecked = false;
        switch (which)
        {
            case "s": SBiases = ""; break;
            case "vds": FixedVds = ""; break;
            default: FixedVgs = ""; break;
        }
    }

    /// <summary>
    /// A checklist of <paramref name="offered"/>, each checked when <paramref name="stored"/> names it. A stored value the
    /// data does not offer is kept as a checked item of its own, so opening the dialog never drops an option silently.
    /// </summary>
    private static List<PickItem> Picks(IEnumerable<string> offered, string stored, char separator, Action changed, Func<string, string> tip)
    {
        var chosen = stored.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var items = offered.Select(o => new PickItem(o, chosen.Any(c => Same(c, o)), changed) { Tip = tip(o) }).ToList();
        foreach (string c in chosen.Where(c => !items.Any(i => Same(c, i.Text))))
            items.Add(new PickItem(c, true, changed) { Tip = $"{tip(c)} — chosen earlier; not in the data" });
        return items;
    }

    /// <summary>Two spellings of one value or pair: compared number by number.</summary>
    private static bool Same(string a, string b)
    {
        static double[] Parts(string t) => [.. t.Split(',', StringSplitOptions.TrimEntries)
            .Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN)];
        double[] x = Parts(a), y = Parts(b);
        return x.Length == y.Length && x.Zip(y).All(p => Math.Abs(p.First - p.Second) <= 1e-6 * Math.Max(1, Math.Abs(p.First)));
    }

    private static string Join(IEnumerable<PickItem> items, string separator) =>
        string.Join(separator, items.Where(i => i.IsChecked).Select(i => i.Text));

    [ObservableProperty] private bool _dc, _thermal, _charge, _capacitanceSlices, _sParams, _paModes;
    [ObservableProperty] private bool _classB, _classJ, _classJStar, _classF, _classFInverse;
    [ObservableProperty] private string _sBiases = "", _fixedVds = "", _fixedVgs = "";
    [ObservableProperty] private string _frequencyGHz = "", _vgs = "", _vds = "", _ropt = "", _sourceZ = "", _source2f0 = "";
    [ObservableProperty] private string _gammaRadius = "", _rings = "", _spokes = "";
    [ObservableProperty] private string _pinStart = "", _pinStep = "", _pinMax = "", _compression = "";
    /// <summary>A text that is not a value, as one line; empty when every field reads.</summary>
    [ObservableProperty] private string _problem = "";

    private static string N(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    private static string N(double? v) => v is { } x ? N(x) : "";
    private static string Z(Complex? z) => z is { } c ? $"{N(c.Real)}, {N(c.Imaginary)}" : "";

    private void Load(ReportOptions o)
    {
        Dc = o.Dc; Thermal = o.Thermal; Charge = o.Charge; CapacitanceSlices = o.CapacitanceSlices; SParams = o.SParams; PaModes = o.PaModes;
        ClassB = o.Modes.Contains(PaClass.B); ClassJ = o.Modes.Contains(PaClass.J); ClassJStar = o.Modes.Contains(PaClass.JStar);
        ClassF = o.Modes.Contains(PaClass.F); ClassFInverse = o.Modes.Contains(PaClass.FInverse);
        SBiases = string.Join("; ", o.SBiases.Select(b => $"{N(b.Vgs)}, {N(b.Vds)}"));
        FixedVds = string.Join(", ", o.FixedVds.Select(N));
        FixedVgs = string.Join(", ", o.FixedVgs.Select(N));
        FrequencyGHz = o.FrequencyHz is { } f ? N(f / 1e9) : "";
        Vgs = N(o.Vgs); Vds = N(o.Vds); Ropt = N(o.RoptOhms); SourceZ = Z(o.SourceZ); Source2f0 = Z(o.Source2f0);
        GammaRadius = N(o.GammaRadius); Rings = o.Rings.ToString(CultureInfo.InvariantCulture); Spokes = o.Spokes.ToString(CultureInfo.InvariantCulture);
        PinStart = N(o.PinStart); PinStep = N(o.PinStep); PinMax = N(o.PinMax); Compression = N(o.Compression);
    }

    /// <summary>The options the fields say, or null with <see cref="Problem"/> naming the first field that does not read.</summary>
    public ReportOptions? Options()
    {
        Problem = "";
        try
        {
            var modes = new List<PaClass>();
            if (ClassB) modes.Add(PaClass.B);
            if (ClassJ) modes.Add(PaClass.J);
            if (ClassJStar) modes.Add(PaClass.JStar);
            if (ClassF) modes.Add(PaClass.F);
            if (ClassFInverse) modes.Add(PaClass.FInverse);
            return ReportOptions.Default with
            {
                Dc = Dc, Thermal = Thermal, Charge = Charge, CapacitanceSlices = CapacitanceSlices, SParams = SParams, PaModes = PaModes,
                Modes = modes,
                SBiases = [.. SBiases.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                     .Select(b => Pair(b, "S biases"))],
                FixedVds = List(FixedVds, "fixed Vds"),
                FixedVgs = List(FixedVgs, "fixed Vgs"),
                FrequencyHz = Opt(FrequencyGHz, "frequency") is { } f ? f * 1e9 : null,
                Vgs = Opt(Vgs, "Vgs"), Vds = Opt(Vds, "Vds"), RoptOhms = Opt(Ropt, "R_opt"),
                SourceZ = Complex(SourceZ, "source Z"), Source2f0 = Complex(Source2f0, "source 2f0"),
                GammaRadius = Req(GammaRadius, "disc radius"), Rings = Int(Rings, "rings"), Spokes = Int(Spokes, "spokes"),
                PinStart = Req(PinStart, "drive start"), PinStep = Req(PinStep, "drive step"), PinMax = Req(PinMax, "drive maximum"),
                Compression = Req(Compression, "compression"),
            };
        }
        catch (FormatException ex)
        {
            Problem = ex.Message;
            return null;
        }
    }

    private static double Num(string text, string what) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
            ? v : throw new FormatException($"'{text}' is not a number for the {what}.");

    private static double Req(string text, string what) => Num(text, what);
    private static int Int(string text, string what) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : throw new FormatException($"'{text}' is not a whole number for the {what}.");
    private static double? Opt(string text, string what) => text.Trim().Length == 0 ? null : Num(text, what);
    private static double[] List(string text, string what) =>
        [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => Num(t, what))];

    private static (double, double) Pair(string text, string what)
    {
        var p = text.Split(',', StringSplitOptions.TrimEntries);
        return p.Length == 2 ? (Num(p[0], what), Num(p[1], what)) : throw new FormatException($"'{text}' is not a pair for the {what}.");
    }

    private static Complex? Complex(string text, string what) =>
        text.Trim().Length == 0 ? null : Pair(text, what) is var (re, im) ? new Complex(re, im) : null;

    /// <summary>Writes the options to <c>Steps.Report</c> — one undo step. False when a field does not read or the document
    /// refuses them.</summary>
    public bool Apply() => Options() is { } o && Owner.SetReportOptions(o);

    [RelayCommand]
    private void ApplyOptions() => Apply();

    [RelayCommand]
    private Task Generate() => GenerateAsync();

    /// <summary>Applies the options, then generates the report off the UI thread (MC-11's function) and writes its folder and
    /// its display.</summary>
    public async Task<bool> GenerateAsync()
    {
        if (Options() is not { } o) return false;
        if (!Owner.SetReportOptions(o)) return false;
        bool ok = await Owner.GenerateReportAsync();
        ExportPdfCommand.NotifyCanExecuteChanged();
        return ok;
    }

    public bool HasReport => Owner.ReportDocument is not null;

    [RelayCommand(CanExecute = nameof(HasReport))]
    private Task ExportPdf() => Owner.ExportReportPdfAsync();
}

/// <summary>One row of the S-bias matrix: its gate voltage and a box per drain voltage.</summary>
public sealed record BiasRow(string Vgs, IReadOnlyList<PickItem> Cells);

/// <summary>One choosable value — a matrix box or a chip — as the option's text spells it, and whether it is chosen.</summary>
public sealed partial class PickItem(string text, bool on, Action changed) : ObservableObject
{
    /// <summary>A matrix box where nothing was measured: drawn as a dot, never chosen.</summary>
    public static PickItem Absent { get; } = new("", false, () => { }) { IsPresent = false };

    public string Text { get; } = text;
    /// <summary>The value as a reader names it — a box's bias point, "Vgs −0.4 V, Vds 10 V".</summary>
    public string Tip { get; init; } = text;
    public bool IsPresent { get; private init; } = true;

    [ObservableProperty] private bool _isChecked = on;

    partial void OnIsCheckedChanged(bool value) => changed();
}
