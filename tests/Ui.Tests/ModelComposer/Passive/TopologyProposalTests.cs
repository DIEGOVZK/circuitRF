using CircuitRF.Design.Modeling.Passive;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer.Passive;

/// <summary>
/// brief-modelcomposer-12 §4, R-mc12-4: S generated from each of three known library topologies — a parallel RLC
/// one-port, a π two-port and a MIM capacitor — ranks that topology first among every candidate with its port count, and
/// its fit recovers the generating values within 1e-3.
/// </summary>
public sealed class TopologyProposalTests(ITestOutputHelper output)
{
    private static readonly double[] F = SyntheticPassiveData.Log(0.1e9, 10e9, 81);

    public static TheoryData<string, Dictionary<string, double>> Cases => new()
    {
        { "parallel-rlc", new() { ["Rp"] = 750, ["Lp"] = 3.3e-9, ["Cp"] = 0.4e-12 } },
        { "pi", new() { ["Rs"] = 1.5, ["Ls"] = 1.2e-9, ["Cp1"] = 0.15e-12, ["Cp2"] = 0.25e-12 } },
        { "mim", new() { ["Rs"] = 0.8, ["Ls"] = 60e-12, ["Cmim"] = 2.2e-12, ["Cp1"] = 0.08e-12, ["Cp2"] = 0.12e-12 } },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void GeneratingTopology_RanksFirstWithItsValues(string id, Dictionary<string, double> truth)
    {
        var block = SyntheticPassiveData.FromTopology(id, truth, F);
        var analysis = PassiveAnalysis.Run(block);
        var proposals = PassiveFit.Propose(block, analysis);

        foreach (var p in proposals)
            output.WriteLine($"{p.Rank} {p.Fit.Topology,-20} AIC {p.Fit.Aic,12:F1}  rms|ΔS| {p.Fit.RmsDeltaS:E2}  evals {p.Fit.Evaluations}");
        var first = proposals[0];
        Assert.Equal(id, first.Fit.Topology);
        Assert.True(first.Offered);
        Assert.Equal(PassiveFit.Offered, proposals.Count(p => p.Offered));
        foreach (var (name, value) in truth)
            Assert.True(Math.Abs(first.Fit.Values[name] / value - 1) < 1e-3, $"{name}: {first.Fit.Values[name]:G6} against {value:G6}");
    }
}
