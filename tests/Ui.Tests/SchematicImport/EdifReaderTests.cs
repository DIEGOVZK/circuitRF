// brief-schimp-3-edif-reader.md §5 — the gates of SI-3: each hand-written fixture (testdata/schematic-import/edif/) read,
// imported, and SI-1's check Matched against BOTH the drawing and the file's declared nets, with two named pin positions
// each; the eight orientations against the standard's own matrices; the escape decoding; and a hierarchical design.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Import;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ImportSchematic;

public sealed class EdifReaderTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-schimp3-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Fixture(string name) =>
        Path.Combine(FixturePaths.Find("testdata/schematic-import/edif") ?? throw new InvalidOperationException("fixtures missing"), name);

    /// <summary>Read, import, and both comparisons Matched (R-si3-3): the drawing and the declared nets.</summary>
    private ForeignSchematic ReadAndImport(string path)
    {
        Assert.Equal(SchematicFileKind.Edif200, SchematicFileClassifier.Classify(path).Kind);
        var read = EdifReader.Read(path);
        Assert.True(read.Ok, read.Refusal);
        Directory.CreateDirectory(_dir);
        var result = SchematicImport.Import(read.Schematic!, _dir, "Imported");
        Assert.True(result.Ok, result.Refusal);
        foreach (var m in result.Messages) output.WriteLine(m);
        Assert.True(result.Connectivity!.Matched, string.Join("\n", result.Messages));
        if (!read.Schematic!.Hierarchical)
        {
            Assert.NotNull(result.Connectivity.Declared);
            Assert.True(result.Connectivity.SourceAgreement!.Matched);
        }
        return read.Schematic!;
    }

    private static (double X, double Y) PinAt(ForeignSchematic s, string sheet, string reference, string pin)
    {
        foreach (var inst in s.Sheet(sheet)!.Instances.Where(i => i.Reference == reference))
            if (s.Symbol(inst.SymbolKey)!.Unit(inst.Unit)!.Pins.FirstOrDefault(p => p.Number == pin) is { } p)
            {
                var (x, y) = SchematicGeometry.LocalToWorld((float)p.X, (float)-p.Y, inst.X, inst.Y, RotationOf(inst.Rotation), inst.Mirror);
                return (Math.Round(x, 3), Math.Round(y, 3));
            }
        throw new InvalidOperationException($"{reference}.{pin} not placed");
    }

    private static SymbolRotation RotationOf(int degrees) => degrees switch
    {
        90 => SymbolRotation.R90, 180 => SymbolRotation.R180, 270 => SymbolRotation.R270, _ => SymbolRotation.R0,
    };

    private static void AssertDeclared(ForeignSchematic s, params string[][] nets)
    {
        var declared = s.DeclaredNets!.Select(n => n.Select(p => p.ToString()).ToHashSet()).Where(n => n.Count >= 2).ToList();
        Assert.Equal(nets.Length, declared.Count);
        foreach (var e in nets) Assert.Contains(declared, n => n.SetEquals(e));
    }

    [Fact]
    public void Divider_ImportsMatched_TitleBlockSkipped_NamedNetLabelled()
    {
        var s = ReadAndImport(Fixture("divider.edf"));

        Assert.Equal((1000, 1100), PinAt(s, "divider", "R1", "1"));
        Assert.Equal((1400, 1400), PinAt(s, "divider", "C1", "2"));
        AssertDeclared(s, ["R1.1", "R2.1", "C1.1"], ["R2.2", "C1.2"]);
        var labels = s.Sheets[0].Labels;
        Assert.Contains(labels, l => l.Kind == ForeignLabelKind.Power && l.Text == "+5V" && (l.X, l.Y) == (1000, 600));
        Assert.Single(labels, l => l.Kind == ForeignLabelKind.Local && l.Text == "VOUT");
        Assert.DoesNotContain(labels, l => l.Text.StartsWith("N0", StringComparison.Ordinal));
        Assert.Contains(s.Notes, n => n.Contains("1 placed object(s) with no designator", StringComparison.Ordinal));
    }

    /// <summary>Unit A is placed MX, unit B R90; the units' pin numbers come from PackagePortNumbers, and the hidden power
    /// pin the file's nets do not list joins its net by name.</summary>
    [Fact]
    public void TwoUnitPart_MxAndR90_ImportsMatched_WithEachPinOnItsWire()
    {
        var s = ReadAndImport(Fixture("opamp_units.edf"));

        Assert.Equal((900, 950), PinAt(s, "opamp_units", "U1", "3"));
        Assert.Equal((2000, 700), PinAt(s, "opamp_units", "U1", "7"));
        AssertDeclared(s, ["U1.3", "R1.1"], ["U1.1", "U1.2", "U1.5"], ["U1.7", "U1.6"], ["R1.2", "U1.4"]);
        Assert.Contains(s.Notes, n => n.Contains("from PackagePortNumbers", StringComparison.Ordinal));
        Assert.Contains(s.Notes, n => n.StartsWith("2 hidden power pin(s) are in no declared net", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoPages_OffPageConnector_JoinsTheDeclaredNetAcrossPages()
    {
        var s = ReadAndImport(Fixture("two_pages.edf"));

        Assert.Equal(["PAGE1", "PAGE2"], s.Sheets.Select(x => x.Name));
        Assert.All(s.Sheets, p => Assert.Contains(p.Labels, l => l.Kind == ForeignLabelKind.OffPage && l.Text == "SIG"));
        Assert.Equal((1000, 1000), PinAt(s, "PAGE1", "R1", "1"));
        Assert.Equal((1200, 1000), PinAt(s, "PAGE2", "R2", "2"));
        AssertDeclared(s, ["R1.1", "R2.1"], ["R1.2", "R2.2"]);
    }

    [Fact]
    public void Escapes_DecodeAsOneRunEach_AndSayHow()
    {
        var s = ReadAndImport(Fixture("escapes.edf"));

        Assert.Equal((1000, 1000), PinAt(s, "escapes", "R1", "1"));
        Assert.Equal((1600, 1000), PinAt(s, "escapes", "C1", "2"));
        Assert.Equal(["First line\nSecond line", "R = 50 \u03A9"], s.Sheets[0].Texts.Select(t => t.Text));
        Assert.Equal("100\u00B5F", s.Sheets[0].Instances.Single(i => i.Reference == "C1").Value);
        Assert.Contains("1 escaped text run(s) decoded as UTF-8.", s.Notes);
        Assert.Contains("1 escaped text run(s) decoded as code page 1252 (not valid UTF-8).", s.Notes);
    }

    /// <summary>The standard mirrors first (MX: y → −y, MY: x → −x), then turns counter-clockwise, Y up; a sheet point is
    /// that negated. ForeignInstance's convention must land every symbol point in the same place.</summary>
    [Theory]
    [InlineData("R0", 0, false)]
    [InlineData("R90", 90, false)]
    [InlineData("R180", 180, false)]
    [InlineData("R270", 270, false)]
    [InlineData("MX", 0, true)]
    [InlineData("MY", 0, false, true)]
    [InlineData("MXR90", 90, true)]
    [InlineData("MYR90", 90, false, true)]
    public void Orientation_MatchesTheStandardsMatrix(string name, int ccw, bool mirrorX, bool mirrorY = false)
    {
        var (rotation, mirror) = EdifReader.Orientation(name)!.Value;
        foreach (var (x, y) in new[] { (3.0, 1.0), (-2.0, 5.0) })
        {
            double mx = mirrorY ? -x : x, my = mirrorX ? -y : y;
            double t = ccw * Math.PI / 180;
            double wx = Math.Round(mx * Math.Cos(t) - my * Math.Sin(t), 9), wy = Math.Round(mx * Math.Sin(t) + my * Math.Cos(t), 9);
            var (fx, fy) = SchematicGeometry.LocalToWorld((float)x, (float)-y, 0, 0, RotationOf(rotation), mirror);
            Assert.Equal((wx, -wy + 0.0), (Math.Round(fx, 9), Math.Round(fy, 9) + 0.0));
        }
    }

    [Theory]
    [InlineData("a%13%%10%b", "a\nb")]
    [InlineData("a%13 10%b", "a\nb")]
    [InlineData("a%10%b%13%c", "a\nb\nc")]
    [InlineData("x%7%y", "xy")]
    [InlineData("line%13%%10%", "line")]
    [InlineData("%206%%169%", "\u03A9")]
    [InlineData("%181%", "\u00B5")]
    [InlineData("%128%", "\u20AC")]
    [InlineData("50% off", "50% off")]
    [InlineData(@"C:\lib\parts.lib", @"C:\lib\parts.lib")]
    public void Escapes_Decode(string raw, string expected)
    {
        Assert.Equal(expected, new EdifText().Decode(raw));
    }

    [Fact]
    public void Hierarchy_SheetInstance_ImportsMatched_AsACell()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "top.edf");
        File.WriteAllText(path, """
            (edif top (edifVersion 2 0 0) (edifLevel 0) (keywordMap (keywordLevel 0))
              (library PARTS (edifLevel 0) (technology (numberDefinition (scale 1 (e 254 -6) (unit DISTANCE))))
                (cell R (cellType GENERIC) (view (rename &Normal "Normal") (viewType SCHEMATIC)
                  (interface (port &1 (designator "1")) (port &2 (designator "2")))
                  (contents
                    (portImplementation &1 (connectLocation (figure DOT (dot (pt 0 0)))) (figure LEAD (path (pointList (pt 0 0) (pt 5 0)))))
                    (portImplementation &2 (connectLocation (figure DOT (dot (pt 20 0)))) (figure LEAD (path (pointList (pt 20 0) (pt 15 0)))))))))
              (library DESIGN (edifLevel 0) (technology (numberDefinition (scale 1 (e 254 -6) (unit DISTANCE))))
                (cell STAGE (cellType GENERIC) (view (rename &Normal "Normal") (viewType SCHEMATIC)
                  (interface (port &IN (direction INPUT)) (port &OUT (direction OUTPUT))
                    (symbol (figure BODY (rectangle (pt 0 10) (pt 40 -10)))
                      (portImplementation &IN (connectLocation (figure DOT (dot (pt 0 0)))))
                      (portImplementation &OUT (connectLocation (figure DOT (dot (pt 40 0)))))))
                  (contents (page &P1
                    (instance &R2 (viewRef &Normal (cellRef R (libraryRef PARTS))) (transform (origin (pt 100 -100))) (designator "R2"))
                    (portImplementation &IN (connectLocation (figure DOT (dot (pt 80 -100)))) (instance &H1 (viewRef &Normal (cellRef HPORT_IN (libraryRef PARTS)))))
                    (portImplementation &OUT (connectLocation (figure DOT (dot (pt 140 -100)))) (instance &H2 (viewRef &Normal (cellRef HPORT_OUT (libraryRef PARTS)))))
                    (net N1 (joined (portRef &1 (instanceRef &R2)) (portRef &IN)) (figure WIRE (path (pointList (pt 80 -100) (pt 100 -100)))))
                    (net N2 (joined (portRef &2 (instanceRef &R2)) (portRef &OUT)) (figure WIRE (path (pointList (pt 120 -100) (pt 140 -100)))))))))
                (cell top (cellType GENERIC) (view (rename &Normal "Normal") (viewType SCHEMATIC) (interface)
                  (contents (page &P1
                    (instance &R1 (viewRef &Normal (cellRef R (libraryRef PARTS))) (transform (origin (pt 100 -100))) (designator "R1"))
                    (instance &S1 (viewRef &Normal (cellRef STAGE)) (transform (origin (pt 140 -100))) (designator "S1"))
                    (portImplementation &GND (connectLocation (figure DOT (dot (pt 80 -100)))) (instance &G1 (viewRef &Normal (cellRef GND_SYM (libraryRef PARTS)))))
                    (portImplementation &GND (connectLocation (figure DOT (dot (pt 200 -100)))) (instance &G2 (viewRef &Normal (cellRef GND_SYM (libraryRef PARTS)))))
                    (net N1 (joined (portRef &1 (instanceRef &R1)) (portRef &GND)) (figure WIRE (path (pointList (pt 80 -100) (pt 100 -100)))))
                    (net N2 (joined (portRef &2 (instanceRef &R1)) (portRef &IN (instanceRef &S1))) (figure WIRE (path (pointList (pt 120 -100) (pt 140 -100)))))
                    (net N3 (joined (portRef &OUT (instanceRef &S1)) (portRef &GND)) (figure WIRE (path (pointList (pt 180 -100) (pt 200 -100))))))))))
              (design top (cellRef top (libraryRef DESIGN))))
            """);

        var read = EdifReader.Read(path);
        Assert.True(read.Ok, read.Refusal);
        var s = read.Schematic!;
        Assert.True(s.Hierarchical);
        Assert.Equal(["top", "STAGE"], s.Sheets.Select(x => x.Name));
        var block = Assert.Single(s.Sheet("top")!.SheetInstances);
        Assert.Equal("STAGE", block.SheetName);
        Assert.Equal([("IN", 1400.0, 1000.0), ("OUT", 1800.0, 1000.0)], block.Pins.Select(p => (p.Name, p.X, p.Y)));
        Assert.Equal(2, s.Sheet("STAGE")!.Labels.Count(l => l.Kind == ForeignLabelKind.Hierarchical));

        var result = SchematicImport.Import(s, _dir, "Imported");
        Assert.True(result.Ok, result.Refusal);
        Assert.True(result.Connectivity!.Matched, string.Join("\n", result.Messages));
        Assert.Single(result.CreatedCells, c => Path.GetFileName(c) == "STAGE");
    }
}
