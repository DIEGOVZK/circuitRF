// brief-imf-6-ports-terminals-names.md R-imf6-2, R-imf6-3, R-imf6-5, R-imf6-6 — terminal pairs, the return rail, port
// numbering and the part of no known kind, on pictures drawn in memory in the textbook convention (a 4 px pen, open
// terminal circles 9 px across the radius, the low-pass's own coordinates).

using System;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Ui.Tests.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

public sealed class PortPairingTests
{
    private static SchematicImageResult Read(Action<Drafting> draw, IImageSymbolClassifier? classifier = null)
    {
        var image = Pictures.Draw(800, 400, c => draw(new Drafting(c, 4, SKColors.Black)));
        var source = ImageSource.FromBytes(Pictures.Png(image), "picture.png").Source!;
        var r = SchematicImageRecognition.Circuit(new SchematicImageInput
        {
            Source = source, Kind = ImageKind.Classify(source.Raster).Force(DrawingKind.Schematic), Classifier = classifier,
        });
        Assert.True(r.Ok, r.Refusal);
        return r;
    }

    /// <summary>Terminal circles at the four corners, the top wire through a coil to a shunt capacitor, and — unless
    /// <paramref name="rail"/> is false — the bottom terminals joined by a rail.</summary>
    private static void Ladder(Drafting d, bool rail = true)
    {
        foreach (var (x, y) in new (float, float)[] { (60, 100), (60, 320), (740, 100), (740, 320) }) d.Circle(x, y, 9);
        d.Line(69, 100, 250, 100);
        d.Coil(250, 100, 450, 100, 120, 4);
        d.Line(450, 100, 731, 100);
        d.Dot(560, 100, 8);
        if (rail)
        {
            d.Capacitor(560, 100, 560, 320, 24, 90, 7);
            d.Dot(560, 320, 8);
        }
        d.Line(69, 320, 731, 320);
    }

    private static Instance Port(SchematicImageResult r, int number) =>
        Assert.Single(r.Circuit!.TestBench.Instances, i => i.Reference == "Port" && i.Overrides.Any(o => o.Name == "Num" && o.Expression == $"{number}"));

    [Fact]
    public void TwoStackedCircles_AVoltageArrowSpans_AreOnePort()
    {
        var r = Read(d =>
        {
            d.Circle(60, 100, 9);
            d.Circle(60, 320, 9);
            d.Line(69, 100, 400, 100);
            d.Capacitor(400, 100, 400, 320, 24, 90, 7);
            d.Line(69, 320, 400, 320);
            d.Arrow(60, 290, 60, 132, 16);
        });
        var port = Assert.Single(r.Circuit!.TestBench.Instances, i => i.Reference == "Port");
        Assert.Equal(PicturePortSource.ArrowPair, Assert.Single(r.Circuit.Ports).Source);
        Assert.Equal("0", port.NetBindings[1]);
        var c = Assert.Single(r.Circuit.TestBench.Instances, i => i.Reference == "C");
        Assert.Contains(port.NetBindings[0], c.NetBindings);
    }

    [Fact]
    public void FourCirclesOnALadder_AreTwoPorts_AndTheBottomRailIsGround()
    {
        var r = Read(d => Ladder(d));
        var circuit = r.Circuit!;
        Assert.Equal(2, circuit.TestBench.Instances.Count(i => i.Reference == "Port"));
        Assert.All(circuit.Ports, p => Assert.Equal(PicturePortSource.TerminalPair, p.Source));
        Assert.All(circuit.TestBench.Instances.Where(i => i.Reference == "Port"), p => Assert.Equal("0", p.NetBindings[1]));
        Assert.Contains("0", Assert.Single(circuit.TestBench.Instances, i => i.Reference == "C").NetBindings);
        // One ground symbol, at the rail's left end — the only thing added.
        var (node, _, _) = Assert.Single(circuit.Grounds);
        var (gx, gy) = r.Read!.ToSource(r.Read.Wires.Nodes[node].X, r.Read.Wires.Nodes[node].Y);
        Assert.True(gx < 100 && Math.Abs(gy - 320) < 6, $"the ground is at ({gx:0}, {gy:0})");
        Assert.Contains("common return was taken as ground", r.Report.Of(RecognitionFindingClass.PictureEmitNotes)?.Sentence ?? "");
    }

    [Fact]
    public void ADrawnGround_KeepsTheRailANetOfItsOwn()
    {
        var r = Read(d =>
        {
            Ladder(d, rail: false);
            d.Capacitor(560, 100, 560, 230, 24, 90, 7);
            d.Ground(560, 230, 48, 10);
        });
        var circuit = r.Circuit!;
        var ports = circuit.TestBench.Instances.Where(i => i.Reference == "Port").ToList();
        Assert.Equal(2, ports.Count);
        Assert.Single(ports.Select(p => p.NetBindings[1]).Distinct());
        Assert.NotEqual("0", ports[0].NetBindings[1]);
        Assert.Single(circuit.Grounds);
        Assert.DoesNotContain("common return", string.Join(" ", r.Report.Findings.Select(f => f.Sentence)));
    }

    [Fact]
    public void Out_DrawnLeftOfIn_IsStillPort2()
    {
        var r = Read(d =>
        {
            d.Circle(60, 100, 9);
            d.Circle(740, 100, 9);
            d.Line(69, 100, 300, 100);
            d.Resistor(300, 100, 500, 100, 120, 18, 6);
            d.Line(500, 100, 731, 100);
            d.Text(Drafting.Sans, "OUT", 40, 70, 20);
            d.Text(Drafting.Sans, "IN", 728, 70, 20);
        });
        Assert.Equal("IN", Port(r, 1).InstanceName);
        Assert.Equal("OUT", Port(r, 2).InstanceName);
        Assert.All(r.Circuit!.Ports, p => Assert.Equal(PicturePortSource.NamedTerminal, p.Source));
    }

    private sealed class NoMatch : IImageSymbolClassifier
    {
        public SymbolClassification Classify(SymbolRegion region, double strokeWidth) => SymbolClassification.None;
    }

    /// <summary>R-imf6-6 (O3): a two-pin part of no known kind is a 0 Ω resistor designated ?1, reported.</summary>
    [Fact]
    public void APartOfNoKnownKind_IsAZeroOhmResistor_DesignatedQuestionMark1()
    {
        var r = Read(d =>
        {
            d.Line(40, 200, 300, 200);
            d.Capacitor(300, 200, 360, 200, 12, 60);
            d.Line(360, 200, 760, 200);
        }, new NoMatch());
        var part = Assert.Single(r.Circuit!.TestBench.Instances, i => i.InstanceName == "?1");
        Assert.Equal("R", part.Reference);
        Assert.Equal("0", part.Overrides.Single(o => o.Name == "R").Expression);
        Assert.Equal(["?1"], r.Circuit.UnknownParts);
        Assert.Contains(r.Report.Findings, f => f.Sentence.StartsWith("?1 is a part of no known kind", StringComparison.Ordinal));
    }
}
