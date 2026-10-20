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
    }

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
