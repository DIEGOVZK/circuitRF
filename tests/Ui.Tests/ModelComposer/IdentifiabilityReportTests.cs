using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-5 R-mc5-4: the GaN network's outer and inner gate capacitances, Cpga and Cpgi, are both shunt
/// gate–source capacitance at low frequency; only data above a few GHz, where the series inductance and resistance sit
/// between them, separates them. With data to 2 GHz the report names them poorly determined with each other; with data
/// to 40 GHz it does not.
/// </summary>
public sealed class IdentifiabilityReportTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(2e9, true)]
    [InlineData(40e9, false)]
    public void OuterAndInnerGateCapacitances_ArePoorlyDetermined_OnlyWithoutHighFrequencyData(double top, bool poor)
    {
        double[] freqs = [.. Enumerable.Range(1, 20).Select(i => i * top / 20)];
        var data = SyntheticFetData.Biased(ComposedTestModels.GanNetwork, "gan", freqs, 1e-4,
            [SyntheticFetData.PinchOffPoint, SyntheticFetData.ForwardPoint(ForwardVariant.Extreme), .. SyntheticFetData.ActivePoints]);
        var (cmc, doc) = SyntheticFetData.Document("gan-two-shell", new NetworkFitSettings { Starts = 1, Budget = 40 },
                                                   new Dictionary<string, double> { ["Cpga"] = 7e-15, ["Cpgi"] = 4.9e-15 });

        var r = NetworkFit.Run(cmc, doc, new Dictionary<string, NormalisedData> { ["bias"] = data });
        var cpga = r.Report["Cpga"];
        output.WriteLine($"top {top / 1e9} GHz: ρ(Cpga, {cpga.Partner}) = {cpga.Correlation:F4}, ±{cpga.RelativeError:P1}");
        foreach (string line in r.Report.Lines) output.WriteLine(line);

        Assert.Equal("Cpgi", cpga.Partner);
        if (poor)
        {
            Assert.True(Math.Abs(cpga.Correlation) > 0.95);
            Assert.Contains(r.Report.Lines, l => l.StartsWith("Cpga poorly determined (ρ = ") && l.EndsWith(" with Cpgi)"));
            Assert.Contains(r.Report.Lines, l => l.StartsWith("Lock Cpga, or tie it to Cpgi × "));
        }
        else
        {
            Assert.True(Math.Abs(cpga.Correlation) <= 0.95);
            Assert.DoesNotContain(r.Report.Lines, l => l.Contains("with Cpgi") || l.Contains("with Cpga"));
        }
    }
}
