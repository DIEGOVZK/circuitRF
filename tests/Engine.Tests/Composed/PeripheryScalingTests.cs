using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Tests.Composed;
using static CircuitRF.Engine.Tests.Composed.ComposedFetEquivalenceTests;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>brief-modelcomposer-1 R-mc1-7 (D9): <c>N = 2·N0</c> is the hand-scaled netlist, and a
/// finger count that is not a whole number is refused.</summary>
public sealed class PeripheryScalingTests
{
    private static readonly string ModelPath = ComposedTestModels.WriteTemp(ComposedTestModels.Model(n0: 4));

    [Fact]
    public void TwiceTheBaseFingers_EqualsTheHandScaledNetlist()
    {
        // Core doubled, perFinger doubled, perFingerInverse halved, fixed unchanged.
        string composed = $"ComposedFet:X1 g d 0 File=\"{ModelPath}\" N=8\n";
        string byHand   = ComposedTestModels.SddGlobals
                        + ComposedTestModels.ReferenceCell("ref", coreScale: 2, resistorScale: 0.5, innerCapScale: 2)
                        + "ref:X1 g d 0\n";

        var a = Dc(Bench(-0.6, 6) + composed);
        var b = Dc(Bench(-0.6, 6) + byHand);
        foreach (string node in (string[])["X1.gi", "X1.di", "X1.si"])
            Assert.True(Math.Abs(a[node] - b[node]) <= 1e-12 * Math.Max(Math.Abs(b[node]), 1e-3), $"{node}: {a[node]:R} vs {b[node]:R}");

        double[] f = [1e9, 10e9];
        var sa = Sp(Bench(-0.6, 6) + composed, f);
        var sb = Sp(Bench(-0.6, 6) + byHand, f);
        for (int k = 0; k < f.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    Assert.True(((Complex)sa[k, i, j] - (Complex)sb[k, i, j]).Magnitude < 1e-9);

        // And the netlist holds the numbers simulated: rg halved, Cpgi doubled, Lg untouched.
        var nl = Elaborate(Bench(-0.6, 6) + composed);
        double Value(string path, string p) => nl.Components.Single(c => c.InstancePath == path).Parameters[p].AsReal();
        Assert.Equal(2.6, Value("X1.rg", "R"), 1e-15);
        Assert.Equal(9.8e-15, Value("X1.C2", "C"), 1e-27);
        Assert.Equal(39.5e-12, Value("X1.L1", "L"), 1e-24);
    }

    [Fact]
    public void ANonIntegerFingerCount_IsRefused()
    {
        var ex = Assert.Throws<ParameterRefusalException>(
            () => Elaborate(Bench(-0.6, 6) + $"ComposedFet:X1 g d 0 File=\"{ModelPath}\" N=2.5\n"));
        Assert.Contains("finger count", ex.Message);
        Assert.Contains("X1", ex.Message);
    }
}
