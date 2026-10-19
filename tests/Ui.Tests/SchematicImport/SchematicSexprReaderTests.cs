// brief-schimp-2-kicad-sch-reader.md §5 — the gates of SI-2: each authored fixture (testdata/schematic-import/sexpr/)
// read, imported, and SI-1's connectivity check Matched, with two named pin positions each; the older epoch's
// references read from its symbol_instances block; a missing child sheet refused by name.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Import;
using Xunit;

namespace CircuitRF.Ui.Tests.ImportSchematic;

public sealed class SchematicSexprReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-schimp2-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Fixture(string name) =>
        Path.Combine(FixturePaths.Find("testdata/schematic-import/sexpr") ?? throw new InvalidOperationException("fixtures missing"), name);

    private ForeignSchematic ReadAndImport(string name, out SchematicImportResult result)
    {
        var read = SchematicSexprReader.Read(Fixture(name));
        Assert.True(read.Ok, read.Refusal);
        Directory.CreateDirectory(_dir);
        result = SchematicImport.Import(read.Schematic!, _dir, "Imported");
        Assert.True(result.Ok, result.Refusal);
        Assert.True(result.Connectivity!.Matched, string.Join("\n", result.Messages));
        return read.Schematic!;
    }

    /// <summary>Where a placed pin lands, in sheet mils, by ForeignSchematic's own convention.</summary>
    private static (double X, double Y) PinAt(ForeignSchematic s, string sheet, string reference, string pin)
    {
        foreach (var inst in s.Sheet(sheet)!.Instances.Where(i => i.Reference == reference))
            if (s.Symbol(inst.SymbolKey)!.Unit(inst.Unit)!.Pins.FirstOrDefault(p => p.Number == pin) is { } p)
            {
                var rot = inst.Rotation switch { 90 => SymbolRotation.R90, 180 => SymbolRotation.R180, 270 => SymbolRotation.R270, _ => SymbolRotation.R0 };
                var (x, y) = SchematicGeometry.LocalToWorld((float)p.X, (float)-p.Y, inst.X, inst.Y, rot, inst.Mirror);
                return (Math.Round(x, 3), Math.Round(y, 3));
            }
        throw new InvalidOperationException($"{reference}.{pin} not placed");
    }

    private static List<HashSet<string>> GeometricNets(ForeignSchematic s, string dir) =>
        [.. SchematicImportBuild.Build(s, "x", Path.Combine(dir, "x", "schematic"), Path.Combine(dir, "x_parts"))
              .Top!.GeometricNets.Select(n => n.Select(p => p.ToString()).ToHashSet())];

    /// <summary>schematic-import.md §4, labels: a field's anchor is where the sheet shows it, its justification and angle turn with the symbol.
    /// A horizontal part's field at angle 90 reads level, centred; a left-justified one is anchored by its left end; and a
    /// mirrored part's left-justified field reads right-justified.</summary>
    [Fact]
    public void Fields_AreReadWhereTheSheetShowsThem_TurnedAndMirroredWithTheirSymbol()
    {
        var read = SchematicSexprReader.Read(Fixture("matching.kicad_sch"));
        Assert.True(read.Ok, read.Refusal);
        ForeignField Field(string reference, string name) =>
            read.Schematic!.Pages.First().Instances.Single(i => i.Reference == reference).Fields.Single(f => f.Name == name);

        Assert.Equal(new ForeignField("Reference", "L1", 2050, 1850, ForeignJustify.Center), Field("L1", "Reference"));
        Assert.Equal(new ForeignField("Reference", "C1", 2500, 2400, ForeignJustify.Left), Field("C1", "Reference"));
        Assert.Equal(ForeignJustify.Right, Field("J1", "Reference").Justify);
    }

    [Fact]
    public void Divider_ImportsMatched_WithItsPowerLabelAndGround()
    {
        var s = ReadAndImport("divider.kicad_sch", out _);

        Assert.Equal((2000, 1350), PinAt(s, "divider", "R1", "1"));
        Assert.Equal((2000, 2150), PinAt(s, "divider", "C1", "2"));
        var labels = s.Sheets[0].Labels;
        Assert.Contains(labels, l => l.Kind == ForeignLabelKind.Power && l.Text == "+5V" && (l.X, l.Y) == (2000, 1000));
        Assert.Equal(2, labels.Count(l => l.Kind == ForeignLabelKind.Power && l.Text == "GND"));
        var nets = GeometricNets(s, _dir);
        Assert.Contains(nets, n => n.SetEquals(["R1.2", "C1.1", "R2.1"]));
        Assert.Contains(nets, n => n.SetEquals(["C1.2", "R2.2"]));
    }

    /// <summary>The transform's ORDER: unit A is turned a quarter AND mirrored, and only the format's own order (turn, then
    /// mirror on screen) puts its + input on R1's wire rather than on the feedback wire. The hidden power pins join their
    /// nets by name.</summary>
    [Fact]
    public void TwoUnitPart_MirroredAndRotated_ImportsMatched_WithEachPinOnItsWire()
    {
        var s = ReadAndImport("opamp_units.kicad_sch", out _);

        Assert.Equal((3100, 2300), PinAt(s, "opamp_units", "U1", "3"));
        Assert.Equal((5000, 1700), PinAt(s, "opamp_units", "U1", "7"));
        HashSet<string>[] expected = [["U1.3", "R1.1"], ["U1.1", "U1.2", "U1.5"], ["U1.7", "U1.6"], ["R1.2", "U1.4"]];
        var nets = GeometricNets(s, _dir);
        Assert.Equal(expected.Length, nets.Count);
        foreach (var e in expected) Assert.Contains(nets, n => n.SetEquals(e));
    }

    [Fact]
    public void Hierarchy_ChildUsedTwice_IsReadOnceAndImportsMatched()
    {
        var s = ReadAndImport("hier_parent.kicad_sch", out var result);

        Assert.True(s.Hierarchical);
        Assert.Equal(["hier_parent", "hier_child"], s.Sheets.Select(x => x.Name));
        Assert.Equal(["hier_child", "hier_child"], s.Sheet("hier_parent")!.SheetInstances.Select(i => i.SheetName));
        Assert.Single(result.CreatedCells, c => Path.GetFileName(c) == "hier_child");
        Assert.Equal((1650, 1200), PinAt(s, "hier_parent", "R1", "2"));
        Assert.Equal((1850, 2000), PinAt(s, "hier_child", "R3", "1"));
    }

    [Fact]
    public void OlderEpoch_ReadsReferencesFromSymbolInstances()
    {
        var s = ReadAndImport("old_epoch.kicad_sch", out _);

        Assert.Equal(["R7", "R8"], s.Sheets[0].Instances.Select(i => i.Reference).Order());
        Assert.Contains(s.Notes, n => n.Contains("20211123", StringComparison.Ordinal));
        Assert.Contains(GeometricNets(s, _dir), n => n.SetEquals(["R7.2", "R8.1"]));
    }

    [Fact]
    public void MissingChildSheet_IsARefusalNamingIt()
    {
        Directory.CreateDirectory(_dir);
        string parent = Path.Combine(_dir, "top.kicad_sch");
        File.WriteAllText(parent, """
            (kicad_sch (version 20231120) (generator handwritten) (uuid "00000000-0000-4000-8000-000000000001")
              (lib_symbols)
              (sheet (at 50.8 25.4) (size 15.24 10.16) (uuid "00000000-0000-4000-8000-000000000002")
                (property "Sheetname" "Stage" (at 50.8 24 0))
                (property "Sheetfile" "absent.kicad_sch" (at 50.8 36 0))))
            """);

        var read = SchematicSexprReader.Read(parent);

        Assert.False(read.Ok);
        Assert.Contains("absent.kicad_sch", read.Refusal);
        Assert.Contains("'Stage'", read.Refusal);
    }
}
