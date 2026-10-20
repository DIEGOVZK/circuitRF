using CircuitRF.Design.Modeling;
using CircuitRF.Design.Modeling.Passive;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer.Passive;

/// <summary>
/// brief-modelcomposer-12 §4, R-mc12-6: six synthetic spiral inductors over turns 1.5 … 6.5, their element values smooth
/// but not polynomial in the turns (L ∝ t^1.8, R_sub ∝ 1/t). Five train — the substrate network's second side tied to
/// the first — and the held-out sixth, the middle one by default, is predicted within max |ΔS| 0.01.
/// </summary>
public sealed class FamilyFitTests(ITestOutputHelper output)
{
    private static readonly double[] F = SyntheticPassiveData.Log(0.2e9, 20e9, 41);
    private static readonly double[] Turns = [1.5, 2.5, 3.5, 4.5, 5.5, 6.5];

    private static Dictionary<string, double> Truth(double t)
    {
        double cox = 30e-15 * t + 5e-15 * t * t, rsub = 1500 / t, csub = 6e-15 * t;
        return new()
        {
            ["Rs"] = 0.9 * t + 0.3, ["Ls"] = 0.55e-9 * Math.Pow(t, 1.8), ["Cs"] = 8e-15 * t,
            ["Cox1"] = cox, ["Rsub1"] = rsub, ["Csub1"] = csub, ["Cox2"] = cox, ["Rsub2"] = rsub, ["Csub2"] = csub,
        };
    }

    [Fact]
    public void FiveSpiralsTrain_HeldOutSixthWithinStatedDeltaS()
    {
        var blocks = Turns.Select(t => SyntheticPassiveData.FromTopology("spiral", Truth(t), F, $"spiral_{t}t", t)).ToList();
        var tied = new Dictionary<string, NetworkVariableSettings>
        {
            ["Cox2"] = new() { Tie = new VariableTie("Cox1") },
            ["Rsub2"] = new() { Tie = new VariableTie("Rsub1") },
            ["Csub2"] = new() { Tie = new VariableTie("Csub1") },
        };

        var family = FamilyFit.Run(blocks, PassiveTopologies.Find("spiral"), new FamilySettings { Geometry = "turns" }, variables: tied);

        foreach (var line in family.Lines) output.WriteLine(line);
        Assert.Equal(3.5, family.HeldOutGeometry);
        Assert.Equal((1.5, 6.5), (family.Lo, family.Hi));
        Assert.Equal(5, family.PerGeometry.Count);
        Assert.Equal(6, family.Laws.Count);
        Assert.True(family.HeldOutMax < 0.01, $"held-out max |ΔS| {family.HeldOutMax:E2}");
        // The ties hold exactly through the laws.
        var at = family.ValuesAt(3.5);
        Assert.Equal(at["Cox1"], at["Cox2"]);
    }
}
