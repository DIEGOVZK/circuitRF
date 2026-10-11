// brief-img-12-acceptance-docs-example.md R-im12-2 — pictures drawn another way: the committed pictures beside
// Fixtures/ForeignPictures.cs, read as a person would read them, against the answer committed with each.
//
// The schematic is drawn in conventions circuitRF does not use — IEC boxes for R and L, a 4 px pen, designators in the
// oblique face and values in the condensed ones circuitRF ships — and suffers each variant once: a JPEG at quality 60,
// a 1.5° turn, salt. Each reads as its netlist: parts, values, ports and connections. At half size the pen is 2 px and
// the words a 12 px cap, below what the glyph reader holds in those faces (design note §13): the circuit — parts,
// kinds, ports and connections — still reads, and that is the claim.
//
// The layout is drawn as a dark viewer draws it: translucent layers over near-black, the top over the bottom, no
// outlines. Each colour is mapped as a person maps it — the red to the top copper, the blue to the bottom, their overlap
// to both, anything else (a JPEG's fringe) to nothing; the automatic map gives copper by area (D7) and so would put the
// larger blue on top. Each layer traces back within ½ px of its perimeter. The JPEG within 1 px: at quality 60 it keeps
// colour at half resolution (4:2:0), so an edge between two colours is a pixel wide before anyone reads it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Clipper2Lib;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Ui.Tests.Imaging;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

public sealed class ImageForeignDrawingTests(ITestOutputHelper output)
{
    private static string Fixture(string name) => Path.Combine(FixturePaths.Require(ForeignPictures.Folder), name);

    private (IReadOnlyList<string> Want, Core.Design.TestBench Got) Read(string picture)
    {
        var want = new CnlReader().Read(File.ReadAllText(Fixture("iec-schematic.cnl"))).Item2;
        var read = SchematicImageRecognition.Circuit(new SchematicImageInput { Source = ImageSource.FromFile(Fixture(picture)).Source! });
        Assert.True(read.Ok, read.Refusal);
        var got = read.Circuit!.TestBench;
        output.WriteLine(string.Join("\n", got.Instances.Select(i => $"{i.Reference}:{i.InstanceName} {string.Join(' ', i.NetBindings)} {NetlistShape.Value(i)}")));
        return (NetlistShape.Of(want), got);
    }

    [Theory]
    [InlineData("iec-schematic.png")]
    [InlineData("iec-schematic-jpeg60.png")]
    [InlineData("iec-schematic-rotated.png")]
    [InlineData("iec-schematic-salt.png")]
    public void AnIecSchematic_ReadsAsItsNetlist(string picture)
    {
        var (want, got) = Read(picture);
        Assert.Equal(want, NetlistShape.Of(got));
    }

    [Fact]
    public void AnIecSchematic_AtHalfSize_ReadsAsItsCircuit()
    {
        var (_, got) = Read("iec-schematic-half.png");
        var want = new CnlReader().Read(File.ReadAllText(Fixture("iec-schematic.cnl"))).Item2;
        Assert.Equal(NetlistShape.Of(want, values: false), NetlistShape.Of(got, values: false));
    }

    [Theory]
    [InlineData("dark-layout.png", 0.5)]
    [InlineData("dark-layout-jpeg60.png", 1.0)]
    public void ADarkTranslucentLayout_TracesBack_LayerByLayer(string picture, double tolPx)
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(Fixture("dark-layout.expected.json")));
        double umPerPx = expected.RootElement.GetProperty("umPerPx").GetDouble();
        var tech = TracePictures.Tech();
        var input = new ImageTraceInput
        {
            Source = ImageSource.FromFile(Fixture(picture)).Source!, Technology = tech,
            Scale = ImageScaleStatement.Stated(umPerPx * 1e-6),
        };

        // The colours a person sees and what they map them to.
        var first = ImageTrace.Trace(input);
        Assert.True(first.Clusters is not null, first.Refusal);
        int Over(SKColor top, int under) =>
            (int)Math.Round(top.Red * top.Alpha / 255.0 + ((under >> 16) & 255) * (1 - top.Alpha / 255.0)) << 16
            | (int)Math.Round(top.Green * top.Alpha / 255.0 + ((under >> 8) & 255) * (1 - top.Alpha / 255.0)) << 8
            | (int)Math.Round(top.Blue * top.Alpha / 255.0 + (under & 255) * (1 - top.Alpha / 255.0));
        int ground = (ForeignPictures.Ground.Red << 16) | (ForeignPictures.Ground.Green << 8) | ForeignPictures.Ground.Blue;
        int bottom = Over(ForeignPictures.Bottom, ground);
        var seen = new (int Rgb, string[] Layers)[]
        {
            (Over(ForeignPictures.Top, ground), ["Top Copper"]),
            (bottom, ["Bottom Copper"]),
            (Over(ForeignPictures.Top, bottom), ["Top Copper", "Bottom Copper"]),
        };
        var map = ImageLayerMap.Auto(first.Clusters, tech);
        foreach (var c in first.Clusters.Clusters.Where(c => c.Index != first.Clusters.BackgroundIndex()))
        {
            var near = seen.MinBy(e => c.Lab.DeltaE2(CieLab.FromRgb(e.Rgb)));
            map = c.Lab.DeltaE(CieLab.FromRgb(near.Rgb)) <= ImageLayerMap.MatchDeltaE
                ? map.WithRole(tech, c.Index, ImageLayerRole.Layer, near.Layers)
                : map.WithRole(tech, c.Index, ImageLayerRole.Ignore);
        }
        var traced = ImageTrace.Trace(input with { LayerMap = map });
        Assert.True(traced.Ok, traced.Refusal);
        var frame = traced.Frame!.Value;

        foreach (var layer in expected.RootElement.GetProperty("layers").EnumerateObject())
        {
            var want = Clipper.Union(new PathsD(layer.Value.EnumerateArray().Select(p =>
                new PathD(p.EnumerateArray().Select(q => new PointD(q[0].GetDouble(), q[1].GetDouble()))))), FillRule.NonZero);
            var key = tech.Layers.Single(l => l.Name == layer.Name).Key;
            var paths = new Paths64();
            foreach (var s in traced.Shapes.Where(s => s.Layer == key && s is not CircuitRF.Design.Layout.BitmapShape))
                paths.AddRange(CircuitRF.Design.Layout.LayoutClipper.ToClipperPaths(s, 100));
            var got = new PathsD();
            foreach (var p in Clipper.Union(paths, FillRule.NonZero))
                got.Add(new PathD(p.Select(q => { var (x, y) = frame.ToPixel(q.X, q.Y); return new PointD(x, y); })));
            got = Clipper.Union(got, FillRule.NonZero);

            double xor = Math.Abs(Clipper.Xor(want, got, FillRule.NonZero).Sum(Clipper.Area));
            double perimeter = want.Sum(p => p.Select((q, i) => Math.Sqrt(Math.Pow(q.x - p[(i + 1) % p.Count].x, 2) + Math.Pow(q.y - p[(i + 1) % p.Count].y, 2))).Sum());
            output.WriteLine($"{picture} {layer.Name}: XOR {xor:F1} px² over a {perimeter:F0} px perimeter = {xor / perimeter:F3} px");
            Assert.True(xor <= tolPx * perimeter, $"{layer.Name}: XOR {xor:F1} px² > {tolPx} × {perimeter:F0} px");
        }
    }
}
