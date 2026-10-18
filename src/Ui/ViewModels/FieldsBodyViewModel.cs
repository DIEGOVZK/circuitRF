using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Core.Design;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// brief-sar-9 — the Add/Edit Analysis dialog's body for a fields analysis: the S-parameter block whose per-port fields are
/// recombined, the frequency (one its 3D run kept), the driving circuit port, what the results are per watt of, and whether the
/// averaged SAR is computed. Every value is the directive's own text, as <see cref="FieldsAnalysis"/> stores it.
/// </summary>
public sealed partial class FieldsBodyViewModel : ObservableObject
{
    public static IReadOnlyList<string> FreqUnits { get; } = CircuitRF.Core.Expressions.FreqUnit.Units;
    public static IReadOnlyList<string> Norms { get; } = [FieldsAnalysis.NormAccepted, FieldsAnalysis.NormAvailable];

    [ObservableProperty] private string _block = "";
    [ObservableProperty] private string _freq = "";
    [ObservableProperty] private string _freqUnit = "MHz";
    [ObservableProperty] private string _drive = "1";
    [ObservableProperty] private string _norm = FieldsAnalysis.NormAccepted;
    [ObservableProperty] private bool _average = true;

    public static FieldsBodyViewModel From(FieldsAnalysis fa) => new()
    {
        Block = fa.Block, Freq = fa.FreqExpr, FreqUnit = fa.FreqUnit, Drive = fa.DriveExpr,
        Norm = fa.PerAvailable ? FieldsAnalysis.NormAvailable : FieldsAnalysis.NormAccepted, Average = fa.Average,
    };

    /// <summary>A block and a frequency are what the directive cannot run without.</summary>
    public bool IsValid => Block.Trim().Length > 0 && Freq.Trim().Length > 0;

    public FieldsAnalysis BuildAnalysis(string name, bool enabled) => new(name)
    {
        Enabled = enabled, Block = Block.Trim(), FreqExpr = Freq.Trim(), FreqUnit = FreqUnit,
        DriveExpr = Drive.Trim().Length > 0 ? Drive.Trim() : "1", Norm = Norm, AverageExpr = Average ? "yes" : "no",
    };
}
