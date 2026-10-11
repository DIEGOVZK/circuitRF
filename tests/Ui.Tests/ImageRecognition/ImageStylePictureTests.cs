// brief-imf-1-corpus-and-scorer.md R-imf1-5 — the committed style pictures (Fixtures/style-pictures, drawn by
// DatasheetStylePictures and WebStylePictures), one fact per picture: each reads its circuit exactly — kinds, nets,
// ports, grounds, its IC or amplifier as one part, every designator (overview §4). Each is skipped with the phase that
// owns what it still lacks, and that phase turns it on: the skip reasons are the series' to-do list. The illustration
// is no circuit, and until IMF-9 it is only read without throwing.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Schematic.Recognition;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

internal static class StyleScore
{
    public static FieldScore Read(ITestOutputHelper output, string picture)
    {
        var score = FieldScore.Read(Path.Combine(FixturePaths.Require(StylePictures.Folder), picture), picture);
        output.WriteLine(FieldScore.Header);
        output.WriteLine(score.Line());
        return score;
    }

    public static void ReadsExactly(ITestOutputHelper output, string picture)
    {
        var score = Read(output, picture);
        Assert.True(score.Exact, score.Line());
    }
}

/// <summary>Facts about one style picture's wires, in the 1× drawing's coordinates whatever the picture's scale
/// (IMF-3).</summary>
internal static class StyleWires
{
    public static SchematicImageRead Read(string picture) =>
        SchematicImageReading.Read(ImageSource.FromFile(Path.Combine(FixturePaths.Require(StylePictures.Folder), picture, "picture.png")).Source!);

    private static ((double X, double Y) A, (double X, double Y) B) Ends(SchematicImageRead read, WireSegment s, double scale)
    {
        var (ax, ay) = read.ToSource(s.A.x, s.A.y);
        var (bx, by) = read.ToSource(s.B.x, s.B.y);
        return ((ax / scale, ay / scale), (bx / scale, by / scale));
    }

    /// <summary>The net of the wire passing within 3 px of (x, y); null when none does.</summary>
    public static int? NetAt(SchematicImageRead read, double scale, double x, double y)
    {
        foreach (var s in read.Wires.Segments)
        {
            var (a, b) = Ends(read, s, scale);
            double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-9 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / l2, 0, 1);
            double px = a.X + t * dx - x, py = a.Y + t * dy - y;
            if (px * px + py * py <= 9) return read.Wires.Net(s.Id);
        }
        return null;
    }

    /// <summary>The wires, other than ones running <paramref name="lead"/>, whose middle lies in the rectangle: a plate
    /// or a box side taken for wire.</summary>
    public static List<string> Across(SchematicImageRead read, double scale, double x0, double y0, double x1, double y1, WireOrientation lead) =>
        read.Wires.Segments.Where(s => s.Orientation != lead).Select(s => Ends(read, s, scale))
            .Where(e => (e.A.X + e.B.X) / 2 is var mx && (e.A.Y + e.B.Y) / 2 is var my && mx >= x0 && mx <= x1 && my >= y0 && my <= y1)
            .Select(e => $"({e.A.X:0},{e.A.Y:0})-({e.B.X:0},{e.B.Y:0})").ToList();

    /// <summary>A two-pin part's leads, either side of its body, are two nets: its plates are not wire.</summary>
    public static void TwoNets(SchematicImageRead read, double scale, (double X, double Y) one, (double X, double Y) other, string part)
    {
        int? a = NetAt(read, scale, one.X, one.Y), b = NetAt(read, scale, other.X, other.Y);
        Assert.True(a is not null, $"{part}: no wire at ({one.X}, {one.Y})");
        Assert.True(b is not null, $"{part}: no wire at ({other.X}, {other.Y})");
        Assert.True(a != b, $"{part}: its two leads are one net");
    }
}

public sealed partial class ImageDatasheetStyleTests(ITestOutputHelper output)
{
    private const string RfAmp = "IMF-8 reads the 8-pin IC box (pin-number boxes, names inside) and the filled-dot RF connectors; " +
                                 "it needs IMF-4's region stopping at the box and IMF-7's annotation phrases first";
    private const string GainBlock = "IMF-8 reads the filled triangle as an Amp and the coax connectors as ports; it needs IMF-4's region, " +
                                     "IMF-6's terminals and IMF-7's word designators (Cblock, Rbias, RFC) first";
    private const string Regulator = "IMF-8 reads the IC box with its pin names outside; it needs IMF-5's filled diodes and variable resistor " +
                                     "and IMF-7's bold labels first";
    private const string Ldo = "IMF-8 reads the IC box with its pin names inside; it needs IMF-4's region stopping at the box and " +
                               "IMF-6's named open-circle terminals first";
    private const string TestCircuit = "IMF-8 reads the triangle as an Amp and the coax connectors as ports; it needs IMF-4's solid " +
                                       "block box first";

    [Fact(Skip = RfAmp)] public void RfAmp_1x() => StyleScore.ReadsExactly(output, "ds-rf-amp-1x");
    [Fact(Skip = RfAmp)] public void RfAmp_2x() => StyleScore.ReadsExactly(output, "ds-rf-amp-2x");
    [Fact(Skip = GainBlock)] public void GainBlock_1x() => StyleScore.ReadsExactly(output, "ds-gain-block-1x");
    [Fact(Skip = GainBlock)] public void GainBlock_2x() => StyleScore.ReadsExactly(output, "ds-gain-block-2x");
    [Fact(Skip = Regulator)] public void Regulator_1x() => StyleScore.ReadsExactly(output, "ds-regulator-1x");
    [Fact(Skip = Regulator)] public void Regulator_2x() => StyleScore.ReadsExactly(output, "ds-regulator-2x");
    [Fact(Skip = Ldo)] public void Ldo_1x() => StyleScore.ReadsExactly(output, "ds-ldo-1x");
    [Fact(Skip = Ldo)] public void Ldo_2x() => StyleScore.ReadsExactly(output, "ds-ldo-2x");
    [Fact(Skip = TestCircuit)] public void TestCircuit_1x() => StyleScore.ReadsExactly(output, "ds-test-circuit-1x");
    [Fact(Skip = TestCircuit)] public void TestCircuit_2x() => StyleScore.ReadsExactly(output, "ds-test-circuit-2x");

    /// <summary>IMF-2, R-imf2-4: the gain block's grey amplifier fill is no ink — no wire crosses the triangle and no
    /// junction stands in it (drawn at (210, 85), (210, 155), (280, 120) at 1×; its outline and leads are ink).</summary>
    [Theory]
    [InlineData("ds-gain-block-1x", 1)]
    [InlineData("ds-gain-block-2x", 2)]
    public void GainBlock_TheTrianglesFill_MakesNoWireAndNoJunction(string picture, int scale)
    {
        var read = SchematicImageReading.Read(ImageSource.FromFile(Path.Combine(FixturePaths.Require(StylePictures.Folder), picture, "picture.png")).Source!);
        // Inside the triangle by more than 4 px of the 1× drawing: clear of its outline and of the leads at its corners.
        bool Inside(double x, double y)
        {
            x /= scale; y /= scale;
            return x > 214 && x < 270 && Math.Abs(y - 120) < 0.5 * (280 - x) - 4;
        }
        foreach (var s in read.Wires.Segments)
            for (int k = 0; k <= 20; k++)
            {
                var (x, y) = read.ToSource(s.A.x + (s.B.x - s.A.x) * k / 20.0, s.A.y + (s.B.y - s.A.y) * k / 20.0);
                Assert.False(Inside(x, y), $"wire {s.Id} crosses the triangle at ({x:0}, {y:0})");
            }
        foreach (var n in read.Wires.Nodes.Where(n => n.Degree >= 3 || n.Dot))
        {
            var (x, y) = read.ToSource(n.X, n.Y);
            Assert.False(Inside(x, y), $"a junction in the triangle at ({x:0}, {y:0})");
        }
    }
}

public sealed partial class ImageDatasheetStyleTests
{
    /// <summary>IMF-3, R-imf3-3: the regulator's double-headed Vref arrow across R1 is a dimension — no wire, and the
    /// output rail and the adjust node it spans stay two nets.</summary>
    [Theory]
    [InlineData("ds-regulator-1x", 1)]
    [InlineData("ds-regulator-2x", 2)]
    public void Regulator_TheVrefArrow_IsNoWire_AndTheNetsItSpansStayTwo(string picture, int scale)
    {
        var read = StyleWires.Read(picture);
        Assert.Contains(read.Annotations, a => a.Kind == StrokeAnnotationKind.DimensionArrow);
        Assert.Empty(StyleWires.Across(read, scale, 448, 108, 462, 207, WireOrientation.Horizontal));
        StyleWires.TwoNets(read, scale, (465, 105), (465, 210), "Vref");
    }

    /// <summary>IMF-3, R-imf3-1: the regulator's curved-plate capacitors (Ci, Co, Cadj) — plates, not wire.</summary>
    [Theory]
    [InlineData("ds-regulator-1x", 1)]
    [InlineData("ds-regulator-2x", 2)]
    public void Regulator_TheCurvedPlates_ArePlates(string picture, int scale)
    {
        var read = StyleWires.Read(picture);
        foreach (var (name, x, mid) in new[] { ("Ci", 110.0, 145.0), ("Co", 640.0, 145.0), ("Cadj", 420.0, 245.0) })
        {
            Assert.Empty(StyleWires.Across(read, scale, x - 12, mid - 6, x + 12, mid + 9, WireOrientation.Vertical));
            StyleWires.TwoNets(read, scale, (x, mid - 12), (x, mid + 14), name);
        }
    }

    /// <summary>IMF-3, R-imf3-1: the RF amplifier's hairline plates — 20 px long, 8 apart at 1× — are plates at both
    /// scales, whatever their length in pens and whether a label touches them.</summary>
    [Theory]
    [InlineData("ds-rf-amp-1x", 1)]
    [InlineData("ds-rf-amp-2x", 2)]
    public void RfAmp_TheHairlinePlates_ArePlates(string picture, int scale)
    {
        var read = StyleWires.Read(picture);
        foreach (var (name, x, y) in new[] { ("C1", 155.0, 115.0), ("C2", 490.0, 145.0) })
        {
            Assert.Empty(StyleWires.Across(read, scale, x - 10, y - 12, x + 10, y + 12, WireOrientation.Horizontal));
            StyleWires.TwoNets(read, scale, (x - 20, y), (x + 20, y), name);
        }
        foreach (var (name, x, y) in new[] { ("C3", 530.0, 75.0), ("C4", 560.0, 75.0) })
        {
            Assert.Empty(StyleWires.Across(read, scale, x - 12, y - 6, x + 12, y + 6, WireOrientation.Vertical));
            StyleWires.TwoNets(read, scale, (x, y - 15), (x, y + 17), name);
        }
    }

    /// <summary>IMF-3, R-imf3-4: the test circuit's dashed fixture box makes no wire, and the wire crossing its side is
    /// still one.</summary>
    [Theory]
    [InlineData("ds-test-circuit-1x", 1)]
    [InlineData("ds-test-circuit-2x", 2)]
    public void TestCircuit_TheDashedBox_MakesNoWire(string picture, int scale)
    {
        var read = StyleWires.Read(picture);
        Assert.Empty(StyleWires.Across(read, scale, 86, 60, 94, 215, WireOrientation.Horizontal));
        Assert.Empty(StyleWires.Across(read, scale, 256, 60, 264, 215, WireOrientation.Horizontal));
        Assert.Empty(StyleWires.Across(read, scale, 90, 56, 260, 64, WireOrientation.Vertical));
        Assert.Empty(StyleWires.Across(read, scale, 90, 211, 260, 219, WireOrientation.Vertical));
        Assert.Equal(StyleWires.NetAt(read, scale, 70, 150), StyleWires.NetAt(read, scale, 108, 150));
        Assert.NotNull(StyleWires.NetAt(read, scale, 70, 150));
    }
}

public sealed partial class ImageWebStyleTests
{
    /// <summary>IMF-3, R-imf3-5: the textbook low-pass's zig-zag is a chain, left out of the curved-lines count — the
    /// picture is read, not refused.</summary>
    [Fact]
    public void TextbookZigzag_IsNotRefused()
    {
        var read = StyleWires.Read("textbook-zigzag");
        Assert.Null(read.Refusal);
        Assert.Equal(1, read.Report.ZigzagChains);
    }

    /// <summary>IMF-3, R-imf3-1, R-imf3-2: the heavy IEC drawing's plates (16 px thick, 152 long, a lead in the
    /// middle) are plates and its resistor boxes' sides (an 8 px pen, corners filled in) are a box — none is wire.</summary>
    [Fact]
    public void HeavyIec_ThePlatesAndBoxSides_AreNotWire()
    {
        var read = StyleWires.Read("heavy-iec");
        StyleWires.TwoNets(read, 1, (150, 360), (250, 360), "C1");
        Assert.Empty(StyleWires.Across(read, 1, 178, 280, 222, 440, WireOrientation.Horizontal));
        StyleWires.TwoNets(read, 1, (630, 200), (750, 200), "C2");
        Assert.Empty(StyleWires.Across(read, 1, 668, 120, 712, 280, WireOrientation.Horizontal));
        StyleWires.TwoNets(read, 1, (740, 540), (740, 640), "C3");
        Assert.Empty(StyleWires.Across(read, 1, 660, 568, 820, 612, WireOrientation.Vertical));
        foreach (var (name, x, top, bottom) in new[]
                 { ("R1", 300.0, 160.0, 260.0), ("R2", 300.0, 470.0, 570.0), ("R3", 580.0, 90.0, 170.0), ("R4", 580.0, 545.0, 635.0), ("RL", 800.0, 270.0, 370.0) })
        {
            Assert.True(StyleWires.Across(read, 1, x - 28, top, x - 12, bottom, WireOrientation.Horizontal).Count == 0, $"{name}'s left side is wire");
            Assert.True(StyleWires.Across(read, 1, x + 12, top, x + 28, bottom, WireOrientation.Horizontal).Count == 0, $"{name}'s right side is wire");
        }
    }

    /// <summary>IMF-3, R-imf3-4: the ladder's dashed continuation in each rail is read as a wire — each rail one net.</summary>
    [Fact]
    public void LadderDashed_EachRail_IsOneNet()
    {
        var read = StyleWires.Read("ladder-dashed");
        Assert.Contains(read.Wires.Segments, s => s.Dashed);
        var top = StyleWires.NetAt(read, 1, 470, 90);
        Assert.NotNull(top);
        Assert.Equal(top, StyleWires.NetAt(read, 1, 620, 90));
        var bottom = StyleWires.NetAt(read, 1, 300, 290);
        Assert.NotNull(bottom);
        Assert.Equal(bottom, StyleWires.NetAt(read, 1, 700, 290));
        Assert.NotEqual(top, bottom);
    }
}

public sealed partial class ImageWebStyleTests(ITestOutputHelper output)
{
    private const string HeavyIec = "IMF-5 reads the transistor in its circle and the two source circles; it needs " +
                                    "IMF-4's region, IMF-6's terminal dot and IMF-7's subscripts first";
    private const string Textbook = "IMF-6 makes the terminal pairs ports and the bottom rail ground; it needs IMF-7's " +
                                    "serif italic first";
    private const string Ladder = "IMF-6 makes the terminal pairs ports and the bottom rail ground; it needs IMF-4's labels " +
                                  "touching their symbols first";
    private const string Typeset = "IMF-7 reads the serif italic designator-then-value runs; it needs IMF-5's ± source circle first";
    private const string BiasTee = "IMF-6 makes the names at a distance ports; it needs IMF-5's looped coil first";
    private const string LowRes = "IMF-6 makes the terminal pairs ports";

    [Fact(Skip = HeavyIec)] public void HeavyIecStage() => StyleScore.ReadsExactly(output, "heavy-iec");
    [Fact(Skip = Textbook)] public void TextbookZigzag() => StyleScore.ReadsExactly(output, "textbook-zigzag");
    [Fact(Skip = Ladder)] public void LadderDashed() => StyleScore.ReadsExactly(output, "ladder-dashed");
    [Fact(Skip = Typeset)] public void TypesetFigure() => StyleScore.ReadsExactly(output, "typeset");
    [Fact(Skip = BiasTee)] public void LoopCoilNames() => StyleScore.ReadsExactly(output, "loop-coil-names");
    [Fact(Skip = LowRes)] public void LowResolution() => StyleScore.ReadsExactly(output, "low-res");

    /// <summary>IMF-9 refuses it in one sentence saying it is not a schematic; until then it is read and scored.</summary>
    [Fact]
    public void Illustration_ReadsWithoutThrowing() => StyleScore.Read(output, "illustration");
}
