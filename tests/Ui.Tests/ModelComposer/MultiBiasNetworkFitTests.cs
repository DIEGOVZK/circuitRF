using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-5 R-mc5-1.2, R-mc5-2, R-mc5-3: the multi-bias fit on the GaN two-shell network. Four active
/// points — chosen from the data automatically — and the two cold points, each its own core inside the network written
/// by hand. Free: the six series elements from starts 25–40 % off, and Cpga with Cpda tied to it; every other value
/// locked at its truth. Also R-mc5-5: the result in the document, and a step computed before it is stale.
/// </summary>
public sealed class MultiBiasNetworkFitTests(ITestOutputHelper output)
{
    private static readonly double[] Freqs = [1e9, .. Enumerable.Range(1, 20).Select(i => i * 2e9)];

    private static readonly Dictionary<string, (double Truth, double Start)> Free = new()
    {
        ["rg"] = (5.2, 7), ["rd"] = (9.2, 6.5), ["rs"] = (6.6, 8.5),
        ["Lg"] = (39.5e-12, 52e-12), ["Ld"] = (46.4e-12, 33e-12), ["Ls"] = (5.25e-12, 7e-12),
        ["Cpga"] = (7e-15, 9e-15),
    };

    private static readonly NetworkFitSettings Settings = new() { Starts = 2, Budget = 300, Seed = 7 };

    private static (string, CmcDocument, Dictionary<string, NormalisedData>) Problem()
    {
        var data = SyntheticFetData.Biased(ComposedTestModels.GanNetwork, "gan", Freqs, 1e-4,
            [SyntheticFetData.PinchOffPoint, SyntheticFetData.ForwardPoint(ForwardVariant.Extreme), .. SyntheticFetData.ActivePoints]);
        var (cmc, doc) = SyntheticFetData.Document("gan-two-shell", Settings, Free.ToDictionary(e => e.Key, e => e.Value.Start),
                                                   new Dictionary<string, VariableTie> { ["Cpda"] = new("Cpga", 1) });
        return (cmc, doc, new Dictionary<string, NormalisedData> { ["bias"] = data });
    }

    /// <summary>
    /// Tolerances: 1e-4 noise on S is ~1e-4 relative on a value the data pins; Ls is an eighth of the gate's series
    /// inductance and reaches the data through a difference, so it gets 3 %. The extreme forward core's 0.02 Ω per
    /// branch is attributed to the series resistances, as in the direct method. Flatness: the noise alone leaves the
    /// de-embedded Cgs, Cgd, Cds, Gm and Gds of the on, knee and saturation points spread by 0.1–2 % of their means
    /// through the TRUE network (the starts leave them at 1.3–17 %); the fit must reach that floor, within 25 % of it
    /// plus 0.1 %.
    /// </summary>
    [Fact]
    public void OptimisedFit_RecoversTheSeriesElements_FlattensTheIntrinsic_RespectsBoundsAndTies_Repeatably()
    {
        var (cmc, doc, data) = Problem();
        var r = NetworkFit.Run(cmc, doc, data);
        output.WriteLine($"{r.Evaluations} evaluations, cost {r.Cost:E3}; active {string.Join(", ", r.Settings.Active)}");

        Assert.Equal(["bias/off", "bias/on", "bias/knee", "bias/sat"], r.Settings.Active.Select(p => p.ToString()));
        foreach (var (name, (truth, _)) in Free)
        {
            double err = Math.Abs(r.Values[name] - truth) / truth;
            double tol = name == "Ls" ? 0.03 : 0.01;
            output.WriteLine($"{name}: {r.Values[name]:G6} (truth {truth:G6}), relative error {err:E2}");
            Assert.True(err < tol, $"{name}: {r.Values[name]:G6} against {truth:G6}");
        }

        // Flat de-embedded intrinsic elements at the fitted values: at the level the true network reaches on the same
        // noisy data.
        var fitted = NetworkFit.Apply(doc, r);
        var network = SixPinNetwork.Load(Path.Combine(Path.GetDirectoryName(cmc)!, "gan-two-shell.csch"));
        double[] Flatness(IReadOnlyDictionary<string, double>? values) =>
            IntrinsicExtraction.Compute(SyntheticFetData.Set("bias", ReferencePlane.Device), data["bias"], PadDeembedding.None,
                                        network, values, band: null, notes: [])["Flatness"].RealValues;
        var atFit = Flatness(CmcExtrinsic.Network(fitted).Values);
        var atTruth = Flatness(null);
        var names = IntrinsicQuantities.Names.ToList();
        foreach (int point in new[] { 3, 4, 5 })
            foreach (string q in new[] { "Cgs", "Cgd", "Cds", "Gm", "Gds" })
            {
                int i = point * names.Count + names.IndexOf(q);
                output.WriteLine($"point {point} {q}: flatness {atFit[i]:E2} (true network {atTruth[i]:E2})");
                Assert.True(atFit[i] <= 1.25 * atTruth[i] + 1e-3, $"point {point} {q}: flatness {atFit[i]:E3} against {atTruth[i]:E3}");
            }

        // Bounds, and the tie exactly.
        var settings = CmcExtrinsic.Network(fitted);
        foreach (var v in r.Report.Variables)
            Assert.InRange(v.Value, v.Min, v.Max);
        Assert.Equal(r.Values["Cpga"], r.Values["Cpda"]);
        Assert.Equal(r.Values["Cpda"], settings.Variables["Cpda"].Value);

        // The result in the document (R-mc5-5): it round-trips, and a step computed through the old network is stale.
        var withStep = fitted with
        {
            Steps = new JsonObject { ["Current"] = new JsonObject { ["Network"] = NetworkFit.Digest(CmcExtrinsic.Network(doc)) } },
        };
        var reread = CmcFile.Parse(Encoding.UTF8.GetString(CmcFile.ToBytes(withStep)));
        Assert.Equal(CmcFile.ToBytes(withStep), CmcFile.ToBytes(reread));
        Assert.Equal(["Current"], NetworkFit.StaleSteps(reread));
        Assert.Equal(Free.Keys.Order(), CmcExtrinsic.Network(reread).Result!.Variables.Keys.Order());

        // The same seed, the same values, bit for bit.
        var again = NetworkFit.Run(cmc, doc, data);
        Assert.Equal(r.Values, again.Values);
    }
}
