using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-5 R-mc5-1.1: the cold-FET direct method recovers every element of the classic shell from a
/// pinch-off point and a forward-gate point, both [P1] §5.5.2 cores written by hand, from starts 25–40 % off. A missing
/// cold bias is refused, naming the bias it needs.
/// </summary>
public sealed class ColdFetExtractionTests(ITestOutputHelper output)
{
    private static readonly double[] Freqs = [1e9, .. Enumerable.Range(1, 20).Select(i => i * 2e9)];

    // The classic shell as SyntheticFetData.ClassicNetwork writes it, and each element's start.
    private static readonly Dictionary<string, (double Truth, double Start)> Elements = new()
    {
        ["Cpg"] = (20e-15, 26e-15), ["Cpd"] = (30e-15, 22e-15), ["Cpgd"] = (2e-15, 2.8e-15),
        ["Lg"] = (40e-12, 52e-12), ["Ld"] = (45e-12, 33e-12), ["Ls"] = (5e-12, 7e-12),
        ["Rg"] = (5, 6.5), ["Rd"] = (9, 6.5), ["Rs"] = (6, 8),
    };

    /// <summary>
    /// The noise is 1e-4 on each part of S, ~1e-4 of |S|. Through the cold models that is ~1e-4 relative on a value
    /// the data pins directly, and more on one it reaches through a small difference — Ls, an eighth of the gate's
    /// series inductance, and Cpgd beside an intrinsic Cgd fifty times larger. The extreme variant also moves each
    /// series resistance by the 0.02 Ω its collapsed core really has, which the method attributes to the network
    /// ([P1] §5.5.2.4's own drawback): 0.4 % of Rg.
    /// </summary>
    [Theory]
    [InlineData(ForwardVariant.Extreme)]
    [InlineData(ForwardVariant.Moderate)]
    public void DirectMethod_RecoversEveryElement_OfTheClassicShell(ForwardVariant variant)
    {
        var data = SyntheticFetData.Biased(SyntheticFetData.ClassicNetwork, "classic", Freqs, 1e-4,
                                           SyntheticFetData.PinchOffPoint, SyntheticFetData.ForwardPoint(variant));
        var fit = new NetworkFitSettings
        {
            Method = NetworkFitMethod.ColdFet, ForwardVariant = variant,
            ChannelResistance = variant == ForwardVariant.Moderate ? 1.5 : null,
        };
        var (cmc, doc) = SyntheticFetData.Document("classic-shell", fit, Elements.ToDictionary(e => e.Key, e => e.Value.Start));

        var r = NetworkFit.Run(cmc, doc, new Dictionary<string, NormalisedData> { ["bias"] = data });

        output.WriteLine($"{r.Evaluations} evaluations, cost {r.Cost:E3}; " + string.Join(" | ", r.Report.Lines));
        foreach (var (name, (truth, _)) in Elements)
            output.WriteLine($"{variant} {name}: {r.Values[name]:G6} (truth {truth:G6}), relative error {Math.Abs(r.Values[name] - truth) / truth:E2}");
        foreach (var (name, (truth, _)) in Elements)
        {
            double err = Math.Abs(r.Values[name] - truth) / truth;
            double tol = name is "Ls" or "Cpgd" ? 0.05 : 0.01;
            Assert.True(err < tol, $"{name}: {r.Values[name]:G6} against {truth:G6}");
        }
    }

    [Fact]
    public void MissingColdBias_IsRefused_NamingIt()
    {
        var data = SyntheticFetData.Biased(SyntheticFetData.ClassicNetwork, "classic", Freqs, 0, SyntheticFetData.ActivePoints);
        var (cmc, doc) = SyntheticFetData.Document("classic-shell", new NetworkFitSettings { Method = NetworkFitMethod.ColdFet },
                                                   Elements.ToDictionary(e => e.Key, e => e.Value.Start));

        var ex = Assert.Throws<ModelingException>(() =>
            NetworkFit.Run(cmc, doc, new Dictionary<string, NormalisedData> { ["bias"] = data }));
        Assert.Equal("Cold-FET: it needs a pinch-off point: S at Vds = 0 V with Vgs below threshold; none of the data is one.", ex.Message);
    }
}
