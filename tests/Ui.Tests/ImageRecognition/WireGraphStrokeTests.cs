// brief-imf-3-strokes-heavy-pens-dashes.md — what the wire graph takes for wire is wire and nothing else: a hairline
// plate pair set far apart, a dimension arrow between two wires, a current arrow on a wire, a dashed rectangle and a
// diamond junction mark, each drawn in memory with the symbol scale stated so the tolerances are known.

using System.Linq;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Ui.Tests.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

public sealed class WireGraphStrokeTests
{
    private const float S = 40;

    private static SchematicImageRead Read(System.Action<Drafting> draw, float pen = 2) =>
        SchematicImageReading.Read(Pictures.Draw(320, 240, c => draw(new Drafting(c, pen, SKColors.Black))),
                                   new SchematicImageOptions { Profile = new PictureProfileOptions { SymbolScale = S } });

    private static int? NetAt(SchematicImageRead read, double x, double y) => StyleWires.NetAt(read, 1, x, y);

    /// <summary>R-imf3-1: hairline plates 1.2 s apart — a datasheet's wide gap — are plates: no wire between or along
    /// them, the two leads two nets.</summary>
    [Fact]
    public void AHairlinePlatePair_FarApart_IsPlates()
    {
        float gap = 1.2f * S;
        var read = Read(d => d.Capacitor(20, 120, 300, 120, gap, 40), pen: 1.2f);

        const float mid = 160;
        Assert.Empty(StyleWires.Across(read, 1, mid - gap / 2 - 6, 96, mid + gap / 2 + 6, 144, WireOrientation.Horizontal));
        Assert.Empty(StyleWires.Across(read, 1, mid - gap / 2 + 6, 110, mid + gap / 2 - 6, 130, WireOrientation.Vertical));
        StyleWires.TwoNets(read, 1, (60, 120), (280, 120), "C");
    }

    /// <summary>R-imf3-3: a double-headed arrow between two wires is a dimension — removed, and the wires stay two
    /// nets.</summary>
    [Fact]
    public void ADoubleHeadedArrow_BetweenTwoWires_IsNoWire()
    {
        var read = Read(d =>
        {
            d.Line(20, 60, 300, 60);
            d.Line(20, 180, 300, 180);
            d.Arrow(160, 64, 160, 176, 12, both: true);
        });

        Assert.Contains(read.Annotations, a => a.Kind == StrokeAnnotationKind.DimensionArrow && a.Removed);
        Assert.Empty(StyleWires.Across(read, 1, 150, 66, 170, 174, WireOrientation.Horizontal));
        StyleWires.TwoNets(read, 1, (100, 60), (100, 180), "the arrow");
    }

    /// <summary>R-imf3-3: a current arrow's head drawn on a wire is removed and the wire kept through it — one net, no
    /// symbol where the head was.</summary>
    [Fact]
    public void ACurrentArrow_OnAWire_KeepsTheWire()
    {
        var read = Read(d =>
        {
            d.Line(20, 120, 300, 120);
            d.Polygon(SKColors.Black, 172, 120, 156, 113, 156, 127);
        });

        Assert.Contains(read.Annotations, a => a.Kind == StrokeAnnotationKind.CurrentArrow);
        var net = NetAt(read, 40, 120);
        Assert.NotNull(net);
        Assert.Equal(net, NetAt(read, 280, 120));
        Assert.Equal(net, NetAt(read, 164, 120));
        Assert.DoesNotContain(read.Symbols, s => s.Box.Distance(164, 120) == 0);
    }

    /// <summary>R-imf3-4: a dashed rectangle is an annotation — removed, never wire — and the wire crossing it is one.</summary>
    [Fact]
    public void ADashedRectangle_MakesNoWire()
    {
        var read = Read(d =>
        {
            d.Line(20, 130, 300, 130);
            d.DashedRect(new SKRect(70, 50, 250, 200), 8, 6);
        });

        Assert.Contains(read.Annotations, a => a.Kind == StrokeAnnotationKind.DashedBox && a.Removed);
        Assert.Empty(StyleWires.Across(read, 1, 66, 50, 74, 200, WireOrientation.Horizontal));
        Assert.Empty(StyleWires.Across(read, 1, 246, 50, 254, 200, WireOrientation.Horizontal));
        Assert.Empty(StyleWires.Across(read, 1, 70, 46, 250, 54, WireOrientation.Vertical));
        Assert.Empty(StyleWires.Across(read, 1, 70, 196, 250, 204, WireOrientation.Vertical));
        Assert.Equal(1, read.Wires.NetCount);
    }

    /// <summary>R-imf3-6: a filled diamond at a T is a junction mark, and reported as a diamond.</summary>
    [Fact]
    public void AFilledDiamond_AtAT_IsAJunction()
    {
        var read = Read(d =>
        {
            d.Line(20, 100, 300, 100);
            d.Line(160, 100, 160, 220);
            d.Diamond(160, 100, 9);
        }, pen: 3);

        Assert.Single(read.Wires.Nodes, n => n.Degree == 3 && n.Dot);
        Assert.Equal(1, read.Report.DiamondJunctions);
        Assert.Equal(1, read.Wires.NetCount);
    }
}
