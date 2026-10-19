using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-4 R-mc4-5: the equivalent-circuit elements of the known linear core are recovered through the
/// GaN network at every frequency, and a deliberately wrong Lg shows as the frequency dependence [P1] §5.6.3.1
/// describes — the flatness metric grows.
/// </summary>
public sealed class IntrinsicQuantitiesTests(ITestOutputHelper output)
{
    private static readonly double[] Freqs = [1e9, 2e9, 4e9, 6e9, 8e9, 10e9, 14e9, 18e9, 20e9, 30e9, 40e9];
    private static readonly SixPinNetwork Gan = SyntheticFetData.Stock("gan-two-shell");
    private static readonly NormalisedData Data = SyntheticFetData.CoreS(ComposedTestModels.GanNetwork, "gan", Freqs);

    private static DataSet Run(IReadOnlyDictionary<string, double>? values = null) =>
        IntrinsicExtraction.Compute(SyntheticFetData.Set("bias", ReferencePlane.Device), Data, PadDeembedding.None, Gan,
                                    values, band: null, notes: []);

    [Fact]
    public void KnownCore_ElementsRecovered_AtEveryFrequency()
    {
        var ds = Run();
        foreach (var (name, truth) in new[]
                 {
                     ("Gm", SyntheticFetData.Gm), ("Gds", SyntheticFetData.Gds), ("Cgs", SyntheticFetData.Cgs),
                     ("Cgd", SyntheticFetData.Cgd), ("Cds", SyntheticFetData.Cds), ("Cm", SyntheticFetData.Cm),
                     ("Tau", -SyntheticFetData.Cm / SyntheticFetData.Gm),
                 })
        {
            var v = ds[name].RealValues;
            double worst = v.Max(x => Math.Abs(x - truth) / Math.Abs(truth));
            output.WriteLine($"{name}: worst relative error {worst:E2}");
            Assert.True(worst < 1e-8, $"{name}: worst relative error {worst:E3}");
        }
    }

    [Fact]
    public void WrongLg_MakesTheFlatnessMetricGrow()
    {
        int cgs = IntrinsicQuantities.Names.ToList().IndexOf("Cgs");
        double right = Run()["Flatness"].RealValues[cgs];
        double wrong = Run(new Dictionary<string, double> { ["Lg"] = 1.5 * 39.5e-12 })["Flatness"].RealValues[cgs];
        output.WriteLine($"Cgs flatness: right Lg {right:E2}, Lg × 1.5 {wrong:E2}");
        Assert.True(right < 1e-8, $"right Lg: {right:E3}");
        Assert.True(wrong > 1e-3, $"wrong Lg: {wrong:E3}");
    }
}
