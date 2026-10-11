// brief-imf-8-datasheet-conventions.md — the part a datasheet's drawing is for: the IC box as an SnP in pin order with
// its grounded pins its reference (R-imf8-1), the amplifier triangle as the System Amp (R-imf8-2), coax connectors as
// ports (R-imf8-3), open circles on a lead passed through (R-imf8-4) and the block-diagram symbols as System
// components (R-imf8-7).

using System;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Ui.Tests.Imaging;
using SkiaSharp;
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

    /// <summary>R-imf8-7: each block symbol, drawn between coax connectors, is its System component with what the drawing
    /// settles — the struck waves' form, the switch's throws and position.</summary>
    [Theory]
    [InlineData("filter-lp", "Filter", "Form=Lowpass")]
    [InlineData("filter-bp", "Filter", "Form=Bandpass")]
    [InlineData("atten", "Atten", "")]
    [InlineData("circulator", "Circulator", "")]
    [InlineData("switch", "Switch", "Throws=2 State=1")]
    [InlineData("coupler", "Coupler", "")]
    [InlineData("balun", "Balun", "")]
    public void BlockSymbol_IsItsSystemComponent(string picture, string reference, string settings)
    {
        var raster = Pictures.Draw(480, 240, c => BlockPictures.Draw(c, picture));
        var source = ImageSource.FromBytes(Pictures.Png(raster), "block.png").Source!;
        var r = SchematicImageRecognition.Circuit(new SchematicImageInput { Source = source, Kind = ImageKind.Classify(source.Raster).Force(DrawingKind.Schematic) });
        var block = One(r.Circuit!.TestBench, reference);
        Assert.Equal(settings, string.Join(" ", block.Overrides.Select(o => $"{o.Name}={o.Expression}")));
        Assert.Equal("0", block.NetBindings[1]);
        Assert.Contains(r.Circuit.TestBench.Instances, i => i.Reference == "Port" && i.NetBindings[0] == block.NetBindings[0]);
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

/// <summary>Each block symbol between coax connectors, drawn in a datasheet's hairline pen.</summary>
internal static class BlockPictures
{
    public static void Draw(SKCanvas c, string which)
    {
        var d = new Drafting(c, 1.1f, SKColors.Black);
        var sans = Drafting.Sans;
        void Coax(float x, float y, string name)
        {
            d.Circle(x, y, 8);
            d.Dot(x, y, 2);
            d.Line(x, y + 8, x, y + 30);
            d.Ground(x, y + 30, 18, 4);
            d.Text(sans, name, x - 14, y + 52, 8);
        }
        void Wave(float x0, float x1, float y, float amp)
        {
            var pts = new float[2 * 41];
            for (int i = 0; i <= 40; i++) { float t = i / 40f; pts[2 * i] = x0 + t * (x1 - x0); pts[2 * i + 1] = y - amp * MathF.Sin(t * 2 * MathF.PI); }
            d.Wire(pts);
        }
        void Strike(float x, float y) => d.Line(x - 4, y + 6, x + 4, y - 6);
        switch (which)
        {
            case "filter-bp":
            case "filter-lp":
                Coax(60, 120, "IN"); Coax(420, 120, "OUT");
                d.Line(68, 120, 210, 120); d.Line(270, 120, 412, 120);
                d.Rect(new SKRect(210, 90, 270, 150));
                Wave(222, 258, 102, 4); Wave(222, 258, 120, 4); Wave(222, 258, 138, 4);
                if (which == "filter-bp") { Strike(240, 102); Strike(240, 138); }
                else { Strike(240, 102); Strike(240, 120); }
                break;
            case "atten":
                Coax(60, 120, "IN"); Coax(420, 120, "OUT");
                d.Line(68, 120, 210, 120); d.Line(270, 120, 412, 120);
                d.Rect(new SKRect(210, 95, 270, 145));
                d.Text(sans, "10 dB", 222, 124, 8);
                break;
            case "circulator":
                Coax(60, 120, "IN"); Coax(420, 120, "OUT"); Coax(240, 190, "ANT");
                d.Line(68, 120, 224, 120); d.Line(256, 120, 412, 120); d.Line(240, 136, 240, 182);
                d.Circle(240, 120, 16);
                using (var p = new SKPath())
                {
                    p.AddArc(new SKRect(232, 112, 248, 128), 200, 140);
                    using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.1f };
                    c.DrawPath(p, paint);
                }
                // arrowhead at the arc's end (340°)
                {
                    float ax = 240 + 8 * MathF.Cos(340 * MathF.PI / 180), ay = 120 + 8 * MathF.Sin(340 * MathF.PI / 180);
                    d.Polygon(SKColors.Black, ax + 1.5f, ay + 3, ax - 3, ay - 1, ax + 2, ay - 3);
                }
                break;
            case "switch":
                Coax(60, 120, "COM"); Coax(420, 95, "A"); Coax(420, 165, "B");
                d.Line(68, 120, 220, 120);
                d.Circle(223, 120, 3);
                d.Line(226, 119, 262, 98);
                d.Circle(265, 95, 3); d.Circle(265, 145, 3);
                d.Line(268, 95, 412, 95);
                d.Line(268, 145, 330, 145); d.Line(330, 145, 330, 165); d.Line(330, 165, 412, 165);
                break;
            case "coupler":
                Coax(60, 100, "IN"); Coax(420, 100, "OUT"); Coax(150, 200, "CPL");
                d.Line(68, 100, 200, 100); d.Line(280, 100, 412, 100);
                d.Rect(new SKRect(200, 85, 280, 150));
                d.Line(200, 100, 280, 100);
                d.Line(212, 125, 268, 125);
                d.Line(212, 125, 212, 150); d.Line(268, 125, 268, 150);
                d.Line(212, 150, 212, 175); d.Line(212, 175, 150, 175); d.Line(150, 175, 150, 192);
                d.Line(268, 150, 268, 175); d.Line(268, 175, 300, 175);
                d.Line(300, 175, 300, 185); d.Line(292, 185, 308, 185); // a resistor-ish load stub
                break;
            case "balun":
                Coax(60, 120, "IN"); Coax(420, 90, "OUT+"); Coax(420, 170, "OUT-");
                d.Line(68, 120, 210, 120);
                d.Rect(new SKRect(210, 85, 280, 155));
                d.Text(sans, "BALUN", 222, 124, 8);
                d.Line(280, 100, 330, 100); d.Line(330, 100, 330, 90); d.Line(330, 90, 412, 90);
                d.Line(280, 140, 330, 140); d.Line(330, 140, 330, 170); d.Line(330, 170, 412, 170);
                break;
        }
    }
}
