using CircuitRF.Design.Modeling;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-4 R-mc4-4: intrinsic voltages from a DC solve of the stock GaN network with the measured
/// currents equal the SDD truth's internal node voltages; an instrument-plane set with bias-path resistance lands on
/// the same intrinsic points as the device-plane set; a set carrying its own intrinsic voltages is reported against
/// the computed ones in one line.
/// </summary>
public sealed class IntrinsicReReferencingTests
{
    private static readonly IReadOnlyList<BiasPoint> Points = SyntheticFetData.DcPoints();
    private static readonly DcTransfer Gan = SyntheticFetData.Stock("gan-two-shell").Dc();

    [Fact]
    public void ComputedIntrinsicVoltages_EqualTheTruth_AndAreReportedAgainstIt()
    {
        var notes = new List<string>();
        var (vgs, vds) = IntrinsicExtraction.ReReference(SyntheticFetData.Set("dc", ReferencePlane.Device),
                                                         new NormalisedData { Points = Points }, null, Gan, false, notes);
        for (int k = 0; k < Points.Count; k++)
        {
            Assert.True(Math.Abs(vgs[k] - Points[k].VgsIntrinsic) < 1e-9, $"{Points[k].PointId}: Vgs {vgs[k]:R} vs {Points[k].VgsIntrinsic:R}");
            Assert.True(Math.Abs(vds[k] - Points[k].VdsIntrinsic) < 1e-9, $"{Points[k].PointId}: Vds {vds[k]:R} vs {Points[k].VdsIntrinsic:R}");
        }
        // Not vacuous: the drain current drops volts across rd and rs at the open channel.
        Assert.True(Points.Max(p => p.Vds - p.VdsIntrinsic) > 0.5);
        string line = Assert.Single(notes);
        Assert.Matches(@"^dc: max \|ΔV\| [0-9.eE+-]+ V between its intrinsic voltages and the computed ones\.$", line);
    }

    [Fact]
    public void InstrumentWithBiasPathR_GivesTheSameIntrinsicPoints_AsDevice()
    {
        var path = new BiasPathOhms(Gate: 2, Drain: 3, Source: 0.5);
        var instrument = Points.Select(p =>
        {
            double vs = path.Source * (p.Ig + p.Id);
            return p with { Vgs = p.Vgs + path.Gate * p.Ig + vs, Vds = p.Vds + path.Drain * p.Id + vs };
        }).ToList();

        var notes = new List<string>();
        var device = IntrinsicExtraction.ReReference(SyntheticFetData.Set("dev", ReferencePlane.Device),
                                                     new NormalisedData { Points = Points }, null, Gan, false, notes);
        var fromInstrument = IntrinsicExtraction.ReReference(SyntheticFetData.Set("ins", ReferencePlane.Instrument, path),
                                                             new NormalisedData { Points = instrument }, null, Gan, false, notes);
        for (int k = 0; k < Points.Count; k++)
        {
            Assert.True(Math.Abs(device.Vgs[k] - fromInstrument.Vgs[k]) < 1e-12, $"{Points[k].PointId} Vgs");
            Assert.True(Math.Abs(device.Vds[k] - fromInstrument.Vds[k]) < 1e-12, $"{Points[k].PointId} Vds");
        }
    }
}
