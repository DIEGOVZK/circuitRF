// brief-imf-8-datasheet-conventions.md — the part a datasheet's drawing is for: the IC box as an SnP in pin order with
// its grounded pins its reference (R-imf8-1), the amplifier triangle as the System Amp (R-imf8-2), coax connectors as
// ports (R-imf8-3) and open circles on a lead passed through (R-imf8-4).

using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Schematic.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

public sealed partial class ImageDatasheetStyleTests
{
    private static Instance One(TestBench tb, string reference) => Assert.Single(tb.Instances, i => i.Reference == reference);

    private static string NetOf(TestBench tb, string instance, int pin) => tb.Instances.Single(i => i.InstanceName == instance).NetBindings[pin];

    /// <summary>R-imf8-1: the RF amplifier's 8-pin box is an SnP whose three ports are RFIN, RFOUT and VCC in pin order —
    /// C1's net, C2's net, the supply rail — its four GND pins its reference and its NC pin left out. The pin names inside
    /// the box read at 2× (at 1× their caps are 6 px).</summary>
    [Theory]
    [InlineData("ds-rf-amp-1x")]
    [InlineData("ds-rf-amp-2x")]
    public void RfAmp_TheIcBox_IsAnSnP_InPinOrder_GroundsItsReference(string picture)
    {
        var r = StyleParts.Read(picture);
        var tb = r.Circuit!.TestBench;
        var u1 = One(tb, "SnP");
        Assert.Equal("3", u1.Overrides.Single(o => o.Name == "NumPorts").Expression);
        Assert.Equal("0", u1.RefNetBinding);
        Assert.Equal(NetOf(tb, "C1", 0), u1.NetBindings[0]);
        Assert.Contains(u1.NetBindings[1], tb.Instances.Single(i => i.InstanceName == "L1").NetBindings);
        // L1 runs from the supply rail to the output: RFOUT and VCC are its two nets. (At 2× the hop where L1's lead
        // crosses pin 8's wire is read as two stubs, not a hop, so VCC is a net of its own there.)
        if (picture.EndsWith("1x")) Assert.Equal(tb.Instances.Single(i => i.InstanceName == "L1").NetBindings.Order(), new[] { u1.NetBindings[1], u1.NetBindings[2] }.Order());
        var ic = r.Circuit.Ics[u1.InstanceName];
        Assert.Equal(4, ic.Reference.Count);
        Assert.Single(ic.Open);
        if (picture.EndsWith("2x")) Assert.Equal(["RFIN", "RFOUT", "VCC"], ic.Ports.Select(p => p.Name));
    }

    /// <summary>R-imf8-2, R-imf8-3, R-imf8-4: the gain block's filled triangle is an Amp — in+ on the input Cblock's net
    /// through its pin marker, in− and out− on ground — and the two coax connectors are Ports 1 and 2. At 1× out+ is on the
    /// output Cblock's net; at 2× the RFC coil beside it comes apart (IMF-5) and stands between them.</summary>
    [Theory]
    [InlineData("ds-gain-block-1x")]
    [InlineData("ds-gain-block-2x")]
    public void GainBlock_TheTriangle_IsAnAmp_AndTheCoaxConnectors_ArePorts(string picture)
    {
        var tb = StyleParts.Read(picture).Circuit!.TestBench;
        var amp = One(tb, "Amp");
        Assert.Equal("0", amp.NetBindings[1]);
        Assert.Equal("0", amp.NetBindings[3]);
        Assert.Equal(NetOf(tb, "Cblock", 0), amp.NetBindings[0]);
        if (picture.EndsWith("1x")) Assert.Contains(amp.NetBindings[2], tb.Instances.Single(i => i.InstanceName == "Cblock_2").NetBindings);
        var ports = tb.Instances.Where(i => i.Reference == "Port").ToList();
        Assert.Equal(2, ports.Count);
        Assert.Contains(ports, p => p.NetBindings[0] == NetOf(tb, "Cblock", 1));
    }

    /// <summary>R-imf8-1: the LDO's box, numbered by position (down the left, along the bottom, up the right), is VIN, EN
    /// and VOUT with GND its reference.</summary>
    [Fact]
    public void Ldo_TheIcBox_IsVinEnVout_GndItsReference()
    {
        var r = StyleParts.Read("ds-ldo-2x");
        var ic = Assert.Single(r.Circuit!.Ics).Value;
        Assert.False(ic.ByNumber);
        Assert.Equal(["VIN", "EN", "VOUT"], ic.Ports.Select(p => p.Name));
        Assert.Equal("GND", Assert.Single(ic.Reference).Name);
    }
}

/// <summary>R-imf8-1's pin numbers, in memory.</summary>
public sealed class DatasheetSymbolTests
{
    private static ImageWord Word(string text) => new(0, 0, new PixelBox(0, 0, 10, 10), null, text, 0, []);

    [Theory]
    [InlineData("8", 8)]
    [InlineData("2,4", 2)]
    [InlineData("3(1)(5)[3]", 3)]
    public void PinNumber_IsTheFirstNumber(string text, int want) => Assert.Equal(want, IcPins.Number(Word(text)));

    [Fact]
    public void PinNumber_OfAName_IsNone() => Assert.Null(IcPins.Number(Word("VCC")));

    /// <summary>Package order round a box: down the left, along the bottom, up the right, back along the top.</summary>
    [Fact]
    public void PackageOrder_RunsRoundTheBoxAnticlockwise()
    {
        var frame = new PixelBox(0, 0, 100, 100);
        SymbolAttachment At(int node, double x, double y) => new(node, x, y, 0, 0);
        var order = DatasheetSymbols.PackageOrder([At(4, 100, 20), At(1, 0, 20), At(5, 50, 0), At(2, 0, 80), At(3, 100, 80)], frame);
        Assert.Equal([1, 2, 3, 4, 5], order.Select(a => a.Node));
    }
}
