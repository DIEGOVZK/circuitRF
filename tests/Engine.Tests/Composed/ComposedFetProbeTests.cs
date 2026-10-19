using System.Text;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;
using static CircuitRF.Engine.Tests.Composed.ComposedFetEquivalenceTests;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>brief-modelcomposer-1 R-mc1-4/R-mc1-5: the elaborated cell is an ordinary hierarchy, and
/// the model file is read once however many instances name it.</summary>
public sealed class ComposedFetProbeTests
{
    private static readonly string ModelPath = ComposedTestModels.WriteTemp(ComposedTestModels.Model());

    [Fact]
    public void TheIntrinsicNetAndANetworkElement_ResolveByPath()
    {
        var nl = Elaborate(Bench(-0.6, 6) + $"ComposedFet:X1 g d 0 File=\"{ModelPath}\"\n");

        Assert.True(nl.Nodes.TryGetIndex("X1.gi", out _));
        Assert.Contains(nl.Components, c => c.InstancePath == "X1.rg" && c.ComponentType == "R");
        Assert.Contains(nl.Components, c => c.InstancePath == "X1.core" && c.Model is ComposedCoreModel);

        // V(X1.gi) is a probe like any other: the DC solution reports it by that name.
        var v = Dc(Bench(-0.6, 6) + $"ComposedFet:X1 g d 0 File=\"{ModelPath}\"\n");
        Assert.InRange(v["X1.gi"], -0.61, -0.59);
    }

    [Fact]
    public void AHundredInstances_LoadTheFileOnce()
    {
        // A file of its own, so classes running alongside cannot touch its count.
        string path = ComposedTestModels.WriteTemp(ComposedTestModels.Model());
        var cnl = new StringBuilder(Bench(-0.6, 6));
        for (int k = 0; k < 100; k++)
            cnl.Append($"ComposedFet:X{k} g d 0 File=\"{path}\" N={1 + k % 3}\n");

        var nl = Elaborate(cnl.ToString());

        Assert.Equal(1, ComposedModelCache.LoadCountFor(path));
        Assert.Equal(100, nl.Components.Count(c => c.Model is ComposedCoreModel));
    }
}
