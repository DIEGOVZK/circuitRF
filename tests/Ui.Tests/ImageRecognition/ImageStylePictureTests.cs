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

    /// <summary>The smallest symbol region whose box holds (x, y), in the 1× drawing's coordinates, within 3 px; null when
    /// none does (IMF-4).</summary>
    public static SymbolRegion? RegionAt(SchematicImageRead read, double scale, double x, double y) =>
        read.Symbols.Select(r => (Region: r, Box: SourceBox(read, r.Box, scale)))
            .Where(t => x >= t.Box.X0 - 3 && x <= t.Box.X1 + 3 && y >= t.Box.Y0 - 3 && y <= t.Box.Y1 + 3)
            .OrderBy(t => (t.Box.X1 - t.Box.X0) * (t.Box.Y1 - t.Box.Y0)).Select(t => t.Region).FirstOrDefault();

    /// <summary>A read box in the 1× drawing's coordinates.</summary>
    public static (double X0, double Y0, double X1, double Y1) SourceBox(SchematicImageRead read, PixelBox b, double scale)
    {
        var (x0, y0) = read.ToSource(b.Left, b.Top);
        var (x1, y1) = read.ToSource(b.Right + 1, b.Bottom + 1);
        return (x0 / scale, y0 / scale, x1 / scale, y1 / scale);
    }

    /// <summary>Each named part is in a region of its own: every point lies in a region, no two in one (IMF-4).</summary>
    public static Dictionary<string, SymbolRegion> OneRegionEach(SchematicImageRead read, double scale, params (string Part, double X, double Y)[] parts)
    {
        var found = new Dictionary<string, SymbolRegion>();
        foreach (var (part, x, y) in parts)
        {
            var r = RegionAt(read, scale, x, y);
            Assert.True(r is not null, $"{part}: no region at ({x}, {y})");
            var other = found.FirstOrDefault(f => f.Value.Id == r!.Id);
            Assert.True(other.Key is null, $"{part} and {other.Key} are one region");
            found[part] = r!;
        }
        return found;
    }

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
    private const string RfAmp = "Measured short after IMF-8: the circuit reads (the IC an SnP in pin order) but C3's designator, " +
                                 "touching its plate, is not read";
    private const string GainBlock = "Measured short after IMF-8: the triangle is an Amp and the coax connectors ports, but Cbypass " +
                                     "reads as a supply bar at 1× and the RFC coil comes apart at 2× (IMF-5); Rbias is not read";
    private const string Regulator = "Measured short after IMF-10: the circuit reads exactly, but Ci reads as C1 at 1× — the " +
                                     "dotted stem of its i fused at the screen pen (IMF-7)";
    private const string TestCircuit = "Measured short after IMF-8: the solid DUT box drawn round the triangle is read as one part " +
                                       "with it, so no Amp — a box framing a part is not read as a frame";

    [Fact(Skip = RfAmp)] public void RfAmp_1x() => StyleScore.ReadsExactly(output, "ds-rf-amp-1x");
    [Fact(Skip = RfAmp)] public void RfAmp_2x() => StyleScore.ReadsExactly(output, "ds-rf-amp-2x");
    [Fact(Skip = GainBlock)] public void GainBlock_1x() => StyleScore.ReadsExactly(output, "ds-gain-block-1x");
    [Fact(Skip = GainBlock)] public void GainBlock_2x() => StyleScore.ReadsExactly(output, "ds-gain-block-2x");
    [Fact(Skip = Regulator)] public void Regulator_1x() => StyleScore.ReadsExactly(output, "ds-regulator-1x");
    [Fact] public void Regulator_2x() => StyleScore.ReadsExactly(output, "ds-regulator-2x");
    [Fact] public void Ldo_1x() => StyleScore.ReadsExactly(output, "ds-ldo-1x");
    [Fact] public void Ldo_2x() => StyleScore.ReadsExactly(output, "ds-ldo-2x");
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

public sealed partial class ImageDatasheetStyleTests
{
    /// <summary>IMF-4, R-imf4-1, R-imf4-2: the LDO's IC box is a region bounded by its outline — its four wired pins (VIN,
    /// EN, VOUT, GND) and nothing beside it; each capacitor is a region of its own.</summary>
    [Theory]
    [InlineData("ds-ldo-1x", 1)]
    [InlineData("ds-ldo-2x", 2)]
    public void Ldo_TheIcBox_HasItsFourPins_AndTheCapacitorsAreTheirOwn(string picture, int scale)
    {
        var read = StyleWires.Read(picture);
        var parts = StyleWires.OneRegionEach(read, scale, ("U1", 260, 110), ("CIN", 120, 132), ("COUT", 400, 132));
        Assert.Equal(4, parts["U1"].Attachments.Count);
        Assert.Equal(2, parts["CIN"].Attachments.Count);
        Assert.Equal(2, parts["COUT"].Attachments.Count);
    }

    /// <summary>IMF-4, R-imf4-2, R-imf4-5: the RF amplifier's IC box has its eight pins — the GND pins tied outside it are
    /// wire, not outline — and C3, C4 and L1 beside the supply rail are three regions.</summary>
    [Theory]
    [InlineData("ds-rf-amp-1x", 1)]
    [InlineData("ds-rf-amp-2x", 2)]
    public void RfAmp_TheIcBox_HasItsEightPins_AndC3C4L1AreSeparate(string picture, int scale)
    {
        var read = StyleWires.Read(picture);
        var parts = StyleWires.OneRegionEach(read, scale, ("U1", 300, 160), ("C3", 530, 75), ("C4", 560, 75), ("L1", 420, 80));
        Assert.Equal(8, parts["U1"].Attachments.Count);
        foreach (var c in new[] { "C3", "C4", "L1" }) Assert.Equal(2, parts[c].Attachments.Count);
    }

    /// <summary>IMF-4, R-imf4-1, R-imf4-5: the regulator reads one region per part — the variable resistor and Cadj
    /// beside it are two — at both scales.</summary>
    [Theory]
    [InlineData("ds-regulator-1x", 1)]
    [InlineData("ds-regulator-2x", 2)]
    public void Regulator_OneRegionPerPart(string picture, int scale) =>
        StyleWires.OneRegionEach(StyleWires.Read(picture), scale,
            ("D1", 300, 40), ("U1", 300, 105), ("Ci", 110, 145), ("R1", 480, 157), ("D2", 560, 157), ("Co", 640, 145),
            ("R2", 380, 252), ("Cadj", 420, 245));
}

public sealed partial class ImageWebStyleTests
{
    /// <summary>IMF-4, R-imf4-1, R-imf4-3, R-imf4-4: the heavy IEC stage reads one region per part — five resistors, three
    /// capacitors, the transistor in its circle and the two sources — none with more than three wire ends.</summary>
    [Fact]
    public void HeavyIec_OneRegionPerPart_NoneWithMoreThanThreeEnds()
    {
        var read = StyleWires.Read("heavy-iec");
        StyleWires.OneRegionEach(read, 1,
            ("R1", 300, 210), ("R2", 300, 520), ("R3", 580, 130), ("R4", 580, 590), ("RL", 800, 320),
            ("C1", 200, 360), ("C2", 690, 200), ("C3", 740, 590), ("Q1", 560, 360), ("vs", 100, 520), ("VCC", 960, 250));
        Assert.All(read.Symbols, r => Assert.True(r.Attachments.Count <= 3, $"a region at {r.Box} has {r.Attachments.Count} wire ends"));
    }

    /// <summary>IMF-4, R-imf4-4: the ladder's designators touching their coils and plates are words, and no symbol region
    /// holds them.</summary>
    [Fact]
    public void LadderDashed_TheDesignators_AreWords()
    {
        var read = StyleWires.Read("ladder-dashed");
        foreach (var (name, x, y) in new[] { ("L1", 155.0, 64.0), ("L2", 355.0, 64.0), ("L3", 695.0, 64.0), ("C1", 305.0, 189.0), ("C2", 505.0, 189.0), ("C3", 845.0, 189.0) })
        {
            Assert.True(read.Words.Any(t => StyleWires.SourceBox(read, t.Box, 1) is var b && x >= b.X0 && x <= b.X1 && y >= b.Y0 && y <= b.Y1),
                        $"{name} is no word");
            Assert.True(read.Symbols.All(r => StyleWires.SourceBox(read, r.Box, 1) is var b && !(x >= b.X0 && x <= b.X1 && y >= b.Y0 && y <= b.Y1)),
                        $"{name} is in a symbol's region");
        }
    }

    /// <summary>IMF-4, R-imf4-4: typeset's R₃10kΩ touching its box is one word beside R3's two-pin region — and C1's italic
    /// 100pF across its plates no longer shorts the right-hand mesh into one region.</summary>
    [Fact]
    public void Typeset_R3sValue_IsOneWord_BesideATwoPinRegion()
    {
        var read = StyleWires.Read("typeset");
        var word = Assert.Single(read.Words, t => StyleWires.SourceBox(read, t.Box, 1) is var b && b.X0 >= 650 && b.X0 <= 656 && b.Y0 >= 195 && b.Y1 <= 225);
        Assert.True(word.Glyphs.Count >= 5, $"R₃10kΩ read as {word.Glyphs.Count} glyphs");
        var parts = StyleWires.OneRegionEach(read, 1, ("R3", 640, 210), ("C1", 540, 208));
        Assert.Equal(2, parts["R3"].Attachments.Count);
        Assert.Equal(2, parts["C1"].Attachments.Count);
        var r3 = StyleWires.SourceBox(read, parts["R3"].Box, 1);
        Assert.True(r3.X1 <= StyleWires.SourceBox(read, word.Box, 1).X0 + 1, "R3's region runs into its value");
    }
}

public sealed partial class ImageWebStyleTests(ITestOutputHelper output)
{
    private const string HeavyIec = "Measured short of IMF-7: 7 of the 9 subscripted designators — R₄'s subscript reads d (Rd) and " +
                                    "Q₁ is not read";
    private const string Ladder = "Measured short of IMF-7: L₁ and C₁ … C₃ — L₂ and L₃, lifted off their coils, come apart into pieces";
    private const string LowRes = "Measured short of IMF-7: the 7 px serif italic R is refused as heavy and C is not read";

    [Fact(Skip = HeavyIec)] public void HeavyIecStage() => StyleScore.ReadsExactly(output, "heavy-iec");
    [Fact] public void TextbookZigzag() => StyleScore.ReadsExactly(output, "textbook-zigzag");
    [Fact(Skip = Ladder)] public void LadderDashed() => StyleScore.ReadsExactly(output, "ladder-dashed");
    [Fact] public void TypesetFigure() => StyleScore.ReadsExactly(output, "typeset");
    [Fact] public void LoopCoilNames() => StyleScore.ReadsExactly(output, "loop-coil-names");
    [Fact(Skip = LowRes)] public void LowResolution() => StyleScore.ReadsExactly(output, "low-res");

    /// <summary>Paste refuses it in one sentence saying it is not a schematic (IMF-9 — ImageNothingHereTests holds that);
    /// scored here so the table carries its line.</summary>
    [Fact]
    public void Illustration_ReadsWithoutThrowing() => StyleScore.Read(output, "illustration");
}

/// <summary>IMF-5's facts, read as Paste reads: every symbol named, and the circuit made.</summary>
internal static class StyleParts
{
    public static SchematicImageResult Read(string picture)
    {
        var source = ImageSource.FromFile(Path.Combine(FixturePaths.Require(StylePictures.Folder), picture, "picture.png")).Source!;
        return SchematicImageRecognition.Circuit(new SchematicImageInput { Source = source, Kind = ImageKind.Classify(source.Raster).Force(DrawingKind.Schematic) });
    }

    public static int Count(SchematicImageResult r, ImageSymbolKind kind) =>
        r.Symbols!.Symbols.Count(s => s.Kind == kind && s.Disposition == SymbolDisposition.Recognised);
}

public sealed partial class ImageDatasheetStyleTests
{
    /// <summary>IMF-5, R-imf5-1 … R-imf5-3: the regulator's two resistors — R2 variable, its arrow across it — its three
    /// curved-plate capacitors and its two filled diodes, at both scales.</summary>
    [Theory]
    [InlineData("ds-regulator-1x")]
    [InlineData("ds-regulator-2x")]
    public void Regulator_Reads2R_OneVariable_3C_2D(string picture)
    {
        var r = StyleParts.Read(picture);
        Assert.Equal(2, StyleParts.Count(r, ImageSymbolKind.Resistor));
        Assert.Single(r.Symbols!.Symbols, s => s.Kind == ImageSymbolKind.Resistor && s.Variable);
        Assert.Equal(3, StyleParts.Count(r, ImageSymbolKind.Capacitor));
        Assert.Equal(2, StyleParts.Count(r, ImageSymbolKind.Diode));
        Assert.Equal(2, r.Circuit!.TestBench.Instances.Count(i => i.Reference == "Diode"));
    }

    /// <summary>IMF-5, R-imf5-1, R-imf5-4: the RF amplifier's four hairline capacitors (wide gaps; C3's label touching its
    /// plate) and its coil, at both scales. (The brief counts five capacitors; the picture draws four.)</summary>
    [Theory]
    [InlineData("ds-rf-amp-1x")]
    [InlineData("ds-rf-amp-2x")]
    public void RfAmp_Reads4C_1L(string picture)
    {
        var r = StyleParts.Read(picture);
        Assert.Equal(4, StyleParts.Count(r, ImageSymbolKind.Capacitor));
        Assert.Equal(1, StyleParts.Count(r, ImageSymbolKind.Inductor));
    }

    /// <summary>IMF-5, R-imf5-1: the LDO's two hairline capacitors, at both scales.</summary>
    [Theory]
    [InlineData("ds-ldo-1x")]
    [InlineData("ds-ldo-2x")]
    public void Ldo_Reads2C(string picture) => Assert.Equal(2, StyleParts.Count(StyleParts.Read(picture), ImageSymbolKind.Capacitor));
}

public sealed partial class ImageWebStyleTests
{
    /// <summary>IMF-5, R-imf5-6, R-imf5-7 (O1, O2): the heavy IEC stage's transistor in its circle is a BJT_NPN whose base
    /// is the wire from the left, collector the one up and emitter the one down; the sine circle is a P1Tone, the first
    /// port, its minus on ground; the + circle a Vdc from the supply rail to ground.</summary>
    [Fact]
    public void HeavyIec_TheBjtIsNpn_OnItsPins_TheSineIsAP1Tone_TheplusCircleAVdc()
    {
        var r = StyleParts.Read("heavy-iec");
        var c = r.Circuit!;
        var read = r.Read!;
        (double X, double Y) At(string instance, int pin) => read.ToSource(read.Wires.Nodes[c.PinNodes[instance][pin]].X, read.Wires.Nodes[c.PinNodes[instance][pin]].Y);

        var q = Assert.Single(c.TestBench.Instances, i => i.Reference == "BJT_NPN");
        Assert.DoesNotContain(c.TestBench.Instances, i => i.Reference == "SnP");
        var (cx, cy) = At(q.InstanceName, 0);
        var (bx, by) = At(q.InstanceName, 1);
        var (ex, ey) = At(q.InstanceName, 2);
        Assert.True(cy < 320 && Math.Abs(cx - 580) < 15, $"collector at ({cx:0}, {cy:0})");
        Assert.True(bx < 500 && Math.Abs(by - 360) < 15, $"base at ({bx:0}, {by:0})");
        Assert.True(ey > 400 && Math.Abs(ex - 580) < 15, $"emitter at ({ex:0}, {ey:0})");

        var tone = Assert.Single(c.TestBench.Instances, i => i.Reference == "P1Tone");
        Assert.Equal("0", tone.NetBindings[1]);
        Assert.Equal("1", tone.Overrides.Single(o => o.Name == "Num").Expression);
        var vdc = Assert.Single(c.TestBench.Instances, i => i.Reference == "Vdc");
        Assert.Equal("0", vdc.NetBindings[1]);
        Assert.Contains(c.TestBench.Instances, i => i.Reference == "R" && i.NetBindings.Contains(vdc.NetBindings[0]));
    }

    /// <summary>IMF-5, R-imf5-4: the bias tee's looped coil is an inductor.</summary>
    [Fact]
    public void LoopCoilNames_ReadsAnL() =>
        Assert.Equal(1, StyleParts.Count(StyleParts.Read("loop-coil-names"), ImageSymbolKind.Inductor));
}

/// <summary>IMF-6's facts: the ports a picture's terminals and names make, read as Paste reads.</summary>
internal static class StylePorts
{
    /// <summary>The ports by number: each one's name as the picture writes it and how it was found.</summary>
    public static (string Name, PicturePortSource Source)[] Of(string picture)
    {
        var r = StyleParts.Read(picture);
        return [.. r.Circuit!.Ports.OrderBy(p => p.Number).Select(p => (p.Name, p.Source))];
    }
}

public sealed partial class ImageDatasheetStyleTests
{
    /// <summary>IMF-6, R-imf6-1, R-imf6-5: the LDO's open-circle terminals named VIN and VOUT are Port 1 and Port 2.</summary>
    [Theory]
    [InlineData("ds-ldo-1x")]
    [InlineData("ds-ldo-2x")]
    public void Ldo_TheNamedTerminals_ArePort1Vin_AndPort2Vout(string picture) =>
        Assert.Equal([("VIN", PicturePortSource.NamedTerminal), ("VOUT", PicturePortSource.NamedTerminal)], StylePorts.Of(picture));

    /// <summary>IMF-6, R-imf6-1: the RF amplifier's filled dots named RF In and RF Out are its two ports — at 1× the left dot
    /// is the junction blob on the wire's end, the right one a region of its own.</summary>
    [Theory]
    [InlineData("ds-rf-amp-1x")]
    [InlineData("ds-rf-amp-2x")]
    public void RfAmp_TheFilledDots_ArePortsRfIn_AndRfOut(string picture) =>
        Assert.Equal([("RF_In", PicturePortSource.NamedTerminal), ("RF_Out", PicturePortSource.NamedTerminal)], StylePorts.Of(picture));
}

public sealed partial class ImageWebStyleTests
{
    /// <summary>IMF-6, R-imf6-2, R-imf6-3: the low-pass's two terminal pairs, each spanned by a voltage arrow, are two ports;
    /// with no ground drawn, the bottom rail holding both references is ground, one ground symbol at its left end.</summary>
    [Fact]
    public void TextbookZigzag_TwoPorts_AndAGroundOnTheBottomRail()
    {
        var r = StyleParts.Read("textbook-zigzag");
        var c = r.Circuit!;
        var ports = c.TestBench.Instances.Where(i => i.Reference == "Port").ToList();
        Assert.Equal(2, ports.Count);
        Assert.All(ports, p => Assert.Equal("0", p.NetBindings[1]));
        var (node, _, _) = Assert.Single(c.Grounds);
        var (x, y) = r.Read!.ToSource(r.Read.Wires.Nodes[node].X, r.Read.Wires.Nodes[node].Y);
        Assert.True(x < 100 && Math.Abs(y - 320) < 6, $"the ground is at ({x:0}, {y:0}), not the bottom rail's left end");
    }

    /// <summary>IMF-6, R-imf6-4: the bias tee's three names, 4-6 text heights beyond the open ends they name, are its three
    /// ports — DC's end a lead ending in paper; RF+DC's port is RF_DC, a name the netlist can carry.</summary>
    [Fact]
    public void LoopCoilNames_ThreePorts_NamedByTheWords()
    {
        var ports = StylePorts.Of("loop-coil-names");
        Assert.Equal(["DC", "RF", "RF_DC"], ports.Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(ports, p => Assert.Equal(PicturePortSource.FarName, p.Source));
    }

    /// <summary>IMF-6, R-imf6-2: the ladder's terminal pairs, stacked at its left and right edges, are two ports.</summary>
    [Fact]
    public void LadderDashed_TwoPorts() =>
        Assert.Equal(2, StylePorts.Of("ladder-dashed").Length);
}

public sealed partial class ImageDatasheetStyleTests
{
    /// <summary>IMF-7, R-imf7-1, R-imf7-3, R-imf7-4: the regulator's bold labels — the word designators Ci, Co and Cadj,
    /// every value under its designator, the diodes' ratings. At 2× every one; at 1× (9 px caps) Ci's dot runs into its
    /// stem and it reads C1, so C1's value is not Ci's.</summary>
    [Theory]
    [InlineData("ds-regulator-1x", 6)]
    [InlineData("ds-regulator-2x", 7)]
    public void Regulator_ReadsItsBoldDesignatorsAndValues(string picture, int right)
    {
        var score = StyleScore.Read(output, picture);
        Assert.Equal(new Tally(right, 7), score.Designators);
        Assert.Equal(new Tally(right, 7), score.Values);
    }

    /// <summary>IMF-7, R-imf7-2, R-imf7-3, R-imf7-4: the LDO's CIN and COUT (read C1N and CΩUT glyph by glyph) and their
    /// values, written beside the drawing as CIN = 1µF, at both scales.</summary>
    [Theory]
    [InlineData("ds-ldo-1x")]
    [InlineData("ds-ldo-2x")]
    public void Ldo_ReadsCinAndCout_AndTheirValues(string picture)
    {
        var score = StyleScore.Read(output, picture);
        Assert.Equal(new Tally(2, 2), score.Designators);
        Assert.Equal(new Tally(2, 2), score.Values);
    }

    /// <summary>IMF-7, R-imf7-1, R-imf7-2: the RF amplifier's pin names inside its box read as written at 2×. (At 1× their
    /// caps are 6 px and RFIN and RFOUT are not read.)</summary>
    [Fact]
    public void RfAmp_2x_ThePinNamesInsideTheBox_Read()
    {
        var r = StyleParts.Read("ds-rf-amp-2x");
        var inside = r.Words.Where(w => r.Read!.Words.Single(t => t.Id == w.Region).Place == TextPlace.InsideBox
                                        && w.Text.Length >= 2 && w.Text.All(char.IsLetter)).Select(w => w.Text).ToList();
        Assert.Equal(["GND", "NC", "RFIN", "RFOUT", "VCC"], inside.Distinct().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(4, inside.Count(t => t == "GND"));
    }

    /// <summary>IMF-7, R-imf7-5: the RF amplifier's two-line notes under its coupling capacitors are comments, each
    /// attached to the capacitor above it — never a net name — at both scales.</summary>
    [Theory]
    [InlineData("ds-rf-amp-1x")]
    [InlineData("ds-rf-amp-2x")]
    public void RfAmp_TheNotes_AreComments_OnTheCouplingCapacitors(string picture)
    {
        var r = StyleParts.Read(picture);
        var comments = r.Words.Where(w => w.Class == WordClass.Comment).ToList();
        Assert.Equal(2, comments.Count);
        var noted = r.Labels!.Symbols.Where(s => s.Comments.Count > 0).ToList();
        Assert.Equal(2, noted.Count);
        Assert.All(noted, s => Assert.Equal(ImageSymbolKind.Capacitor, s.Kind));
        Assert.DoesNotContain(r.Labels.Names, n => comments.Contains(n.Word));
    }
}

public sealed partial class ImageWebStyleTests
{
    /// <summary>IMF-7, R-imf7-7 … R-imf7-11: typeset's serif italic designator-then-value runs — every designator, four of
    /// the five passive values (C1's 100pF, set across its plates, is not read) and V1's 12V on the source.</summary>
    [Fact]
    public void Typeset_ReadsEveryDesignator_FourValues_AndTheSourcesVolts()
    {
        var score = StyleScore.Read(output, "typeset");
        Assert.Equal(new Tally(6, 6), score.Designators);
        Assert.True(score.Values is { Right: >= 4 }, $"values {score.Values}");
        var v1 = Assert.Single(StyleParts.Read("typeset").Circuit!.TestBench.Instances, i => i.Reference == "Vdc");
        Assert.Equal("12", v1.Overrides.Single(o => o.Name == "Vdc").Expression);
    }

    /// <summary>IMF-7, R-imf7-10: the low-pass's single-letter italic designators R and C, each beside its symbol.</summary>
    [Fact]
    public void TextbookZigzag_ReadsRAndC() =>
        Assert.Equal(new Tally(2, 2), StyleScore.Read(output, "textbook-zigzag").Designators);
}
