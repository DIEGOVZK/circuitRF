// Designer feedback round 21: MLIN, CPWG and SLIN take their length as E at F. The claim: a line stated by its
// angle stamps exactly what the same line stated by the length that angle is (through its own εeff at F).

using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

public sealed class PlanarLineElectricalLengthTests
{
    [Fact]
    public void AnMlinStatedByE_StampsAsTheLengthEIsAtF()
    {
        const double fRef = 2e9, eRad = Math.PI / 3, w = 0.7e-3;
        var byAngle = ComponentModelFactory.TryCreate("MLIN", new Dictionary<string, Value>
            { ["W"] = new Value(w), ["E"] = new Value(eRad), ["F"] = new Value(fRef) })!;
        double l = PlanarLineAngle.LengthMeters((IPlanarLineModel)byAngle, eRad, fRef);
        var byLength = ComponentModelFactory.TryCreate("MLIN", new Dictionary<string, Value>
            { ["W"] = new Value(w), ["L"] = new Value(l) })!;

        var a = new CapturingMnaContext();
        var b = new CapturingMnaContext();
        const double omega = 2 * Math.PI * 3.1e9;
        byAngle.Stamp(a, new ElaboratedComponent("MLIN", "ML1", [1, 2], new Dictionary<string, Value>(), byAngle), omega);
        byLength.Stamp(b, new ElaboratedComponent("MLIN", "ML1", [1, 2], new Dictionary<string, Value>(), byLength), omega);

        Assert.Equal(b.Entries.Keys.OrderBy(k => k), a.Entries.Keys.OrderBy(k => k));
        foreach (var (k, y) in b.Entries)
            Assert.True((a.Entries[k] - y).Magnitude <= 1e-12 * y.Magnitude, $"{k}: {a.Entries[k]} vs {y}");
    }
}
