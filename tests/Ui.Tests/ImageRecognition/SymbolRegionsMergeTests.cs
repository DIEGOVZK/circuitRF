// brief-imf-4-regions-and-touching-labels.md — one symbol region is one part: two resistors close together on one wire
// stay two, and a transistor in its envelope circle is one region with its three leads, each drawn in memory with the
// symbol scale stated so the tolerances are known.

using System.Linq;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Ui.Tests.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

public sealed class SymbolRegionsMergeTests
{
    private const float S = 40;

    private static SchematicImageRead Read(System.Action<Drafting> draw) =>
        SchematicImageReading.Read(Pictures.Draw(320, 240, c => draw(new Drafting(c, 2, SKColors.Black))),
                                   new SchematicImageOptions { Profile = new PictureProfileOptions { SymbolScale = S } });

    /// <summary>R-imf4-1: two resistor boxes 1.5 s apart on one wire are two regions of two wire ends each. The runs
    /// either side differ in length, and the wire turns down at both ends: runs of one length on one line between boxes
    /// read as a dashed line (R-imf3-4).</summary>
    [Fact]
    public void TwoResistors_OneAndAHalfScalesApart_StayTwoRegions()
    {
        var read = Read(d =>
        {
            d.Wire(20, 220, 20, 120, 50, 120);
            d.Box(50, 120, 90, 120, S, 16);
            d.Line(90, 120, 150, 120);
            d.Box(150, 120, 190, 120, S, 16);
            d.Wire(190, 120, 300, 120, 300, 220);
        });

        var one = StyleWires.RegionAt(read, 1, 70, 120);
        var two = StyleWires.RegionAt(read, 1, 170, 120);
        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.NotEqual(one!.Id, two!.Id);
        Assert.Equal(2, one.Attachments.Count);
        Assert.Equal(2, two.Attachments.Count);
    }

    /// <summary>R-imf4-3: a transistor drawn in a circle 2.4 s across, its base, collector and emitter leads crossing it,
    /// is one region with three wire ends.</summary>
    [Fact]
    public void ATransistorInACircle_WithThreeLeads_IsOneRegion()
    {
        var read = Read(d =>
        {
            d.Circle(180, 120, 48);
            d.Line(20, 120, 158, 120);
            d.Line(158, 98, 158, 142, 5);
            d.Line(158, 108, 195, 88);
            d.Line(195, 88, 195, 10);
            d.Arrow(158, 132, 195, 152, 10);
            d.Line(195, 152, 195, 230);
        });

        var q = StyleWires.RegionAt(read, 1, 180, 120);
        Assert.NotNull(q);
        Assert.Equal(3, q!.Attachments.Count);
        Assert.Equal(1, read.Report.EnvelopeCircles);
        Assert.DoesNotContain(read.Symbols, r => r.Id != q.Id && r.Box.Distance(q.Box) == 0);
    }
}
