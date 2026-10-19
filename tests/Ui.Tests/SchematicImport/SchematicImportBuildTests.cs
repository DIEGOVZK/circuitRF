// brief-schimp-1-neutral-model-and-build.md §3 — the gates of SI-1, against ForeignSchematics built by hand: the import of a
// two-page design matches the source's nets; the map's value reading; an unmatched part's symbol cell reused on a second
// import; a 50-mil source drawn where the source draws it (R-si1-6); BuildInto renaming against an open schematic.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Pdk;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Import;
using Xunit;

namespace CircuitRF.Ui.Tests.ImportSchematic;

public sealed class SchematicImportBuildTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-schimp-" + Guid.NewGuid().ToString("N")[..8]);

    public SchematicImportBuildTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── symbols, mils Y up ────────────────────────────────────────────────────────────────────────

    private static ForeignSymbol TwoPin(string key) => new(key, key, "lib",
    [
        new ForeignSymbolUnit(1,
            [new ForeignPin("1", "~", ForeignPinType.Passive, 0, 200, ForeignPinDirection.Down),
             new ForeignPin("2", "~", ForeignPinType.Passive, 0, -200, ForeignPinDirection.Up)],
            [new KitSymbolRectangle(-40, -120, 40, 120, false)]),
    ]);

    private static readonly ForeignSymbol GroundSymbol = new("GND", "GND", "power",
        [new ForeignSymbolUnit(1, [new ForeignPin("1", "GND", ForeignPinType.PowerIn, 0, 0, ForeignPinDirection.Down, Hidden: true)],
                               [new KitSymbolLine(-50, 0, 50, 0)])]);

    private static readonly IReadOnlyList<KitSymbolShape> Triangle = [new KitSymbolPath([-200, 200, 200, 0, -200, -200], true, false)];

    /// <summary>A dual op-amp: unit A carries the two hidden power pins, each joining its net by name.</summary>
    private static readonly ForeignSymbol DualOpAmp = new("DUAL", "OPAMP_DUAL", "lib",
    [
        new ForeignSymbolUnit(1,
            [new ForeignPin("3", "+", ForeignPinType.Input, -300, 100),
             new ForeignPin("2", "-", ForeignPinType.Input, -300, -100),
             new ForeignPin("1", "OUT", ForeignPinType.Output, 300, 0, ForeignPinDirection.Left),
             new ForeignPin("8", "V+", ForeignPinType.PowerIn, 0, 300, ForeignPinDirection.Down, Hidden: true, ImplicitNet: "+5V"),
             new ForeignPin("4", "V-", ForeignPinType.PowerIn, 0, -300, ForeignPinDirection.Up, Hidden: true, ImplicitNet: "GND")],
            Triangle),
        new ForeignSymbolUnit(2,
            [new ForeignPin("5", "+", ForeignPinType.Input, -300, 100),
             new ForeignPin("6", "-", ForeignPinType.Input, -300, -100),
             new ForeignPin("7", "OUT", ForeignPinType.Output, 300, 0, ForeignPinDirection.Left)],
            Triangle),
    ]);

    private static ForeignWire W(params (double X, double Y)[] points) => new(points);

    /// <summary>
    /// Page 1: R1/C1 divider from +5V into U1A's + input, U1A's output fed back and sent off the page as SIG, C1 grounded by a
    /// ground symbol. Page 2: SIG into U1B wired as a follower, loaded by R2 to a GND power label.
    /// </summary>
    private static ForeignSchematic TwoPageDesign(string path) => new("kicad_sch", path,
    [
        new ForeignSheet("Page1", 3000, 2500)
        {
            Instances =
            [
                new("R1", 1, "R", 1000, 1000, Value: "10k"),
                new("C1", 1, "C", 1000, 1800, Value: "100nF"),
                new("U1", 1, "DUAL", 1900, 1500, Value: "dual"),
                new("#PWR1", 1, "GND", 1000, 2200),
            ],
            Wires =
            [
                W((1000, 500), (1000, 800)),
                W((1000, 1200), (1000, 1600)),
                W((1000, 1400), (1600, 1400)),
                W((1000, 2000), (1000, 2200)),
                W((2200, 1500), (2200, 1900), (1500, 1900), (1500, 1600), (1600, 1600)),
                W((2200, 1500), (2600, 1500)),
            ],
            Junctions = [new(1000, 1400)],
            Labels = [new(ForeignLabelKind.Power, "+5V", 1000, 500), new(ForeignLabelKind.OffPage, "SIG", 2600, 1500)],
            Texts = [new("Divider", 400, 300)],
        },
        new ForeignSheet("Page2", 3000, 2500)
        {
            Instances = [new("U1", 2, "DUAL", 1200, 1100), new("R2", 1, "R", 2000, 1500, Value: "4k7")],
            Wires =
            [
                W((500, 1000), (900, 1000)),
                W((1500, 1100), (1500, 1500), (800, 1500), (800, 1200), (900, 1200)),
                W((1500, 1100), (2000, 1100), (2000, 1300)),
                W((2000, 1700), (2000, 1900)),
            ],
            Labels = [new(ForeignLabelKind.OffPage, "SIG", 500, 1000), new(ForeignLabelKind.Power, "GND", 2000, 1900)],
        },
    ],
    [TwoPin("R"), TwoPin("C"), GroundSymbol, DualOpAmp], "Page1");

    private static HashSet<string> Set(params string[] pins) => [.. pins];

    [Fact]
    public void ATwoPageDesign_ImportsAsACellWhoseExtractedNetsAreTheSourcesNets()
    {
        var source = TwoPageDesign(Path.Combine(_dir, "amp.kicad_sch"));
        var result = SchematicImport.Import(source, _dir, "Amp");

        Assert.True(result.Ok, result.Refusal);
        Assert.True(result.Connectivity!.Matched, string.Join("\n", result.Messages));

        // The check is not vacuous: what NetExtractor reads out of the written cell is these five nets, exactly.
        var reread = SchematicPersistence.LoadFromFile(result.SchematicPath!).model;
        var built = SchematicImportBuild.Build(source, "Amp", Path.GetDirectoryName(result.SchematicPath!)!,
                                               Path.Combine(_dir, "amp_parts"));
        var top = built.Top!;
        var extracted = ImportConnectivity.Extract(new ImportBuiltSchematic
        {
            CellName = "", Directory = top.Directory, Model = reread, TestBench = top.TestBench, Placement = top.Placement,
            InstancePins = top.InstancePins, PortNames = [], GeometricNets = [], Excluded = top.Excluded,
        });
        var nets = extracted.Select(s => s.Select(p => p.ToString()).ToHashSet()).ToList();
        HashSet<string>[] expected =
        [
            Set("R1.1", "U1.8"), Set("R1.2", "C1.1", "U1.3"), Set("U1.1", "U1.2", "U1.5"),
            Set("U1.7", "U1.6", "R2.1"), Set("C1.2", "R2.2", "U1.4"),
        ];
        Assert.Equal(expected.Length, nets.Count);
        foreach (var e in expected) Assert.Contains(nets, n => n.SetEquals(e));
        Assert.NotNull(reread.SchematicImportSource);
        Assert.Equal("amp.kicad_sch", reread.SchematicImportSource!.Source);
    }

    /// <summary>Overview D5: a hierarchical design's child sheet becomes a cell of its own — a rectangle symbol whose pins are
    /// its ports and a schematic whose Pins are its hierarchical labels — and each side's nets match the source's.</summary>
    [Fact]
    public void AHierarchicalDesign_ImportsItsChildSheetAsACellAndMatchesOnBothSides()
    {
        var source = new ForeignSchematic("kicad_sch", Path.Combine(_dir, "hier.kicad_sch"),
        [
            new ForeignSheet("Root")
            {
                Instances = [new("R1", 1, "R", 1000, 1000, Value: "1k"), new("R2", 1, "R", 2500, 1000, Value: "2k")],
                SheetInstances = [new("Stage1", "Stage", 1500, 700, 600, 400, [new("IN", 1500, 800), new("OUT", 2100, 800)])],
                Wires = [W((1000, 800), (1500, 800)), W((2100, 800), (2500, 800)), W((1000, 1200), (1000, 1400), (2500, 1400), (2500, 1200))],
                Labels = [new(ForeignLabelKind.Power, "GND", 1000, 1400)],
            },
            new ForeignSheet("Stage")
            {
                Instances = [new("R3", 1, "R", 1000, 1000, Rotation: 90, Value: "50")],
                Wires = [W((500, 1000), (800, 1000)), W((1200, 1000), (1500, 1000))],
                Labels = [new(ForeignLabelKind.Hierarchical, "IN", 500, 1000), new(ForeignLabelKind.Hierarchical, "OUT", 1500, 1000)],
            },
        ], [TwoPin("R")], "Root", Hierarchical: true);

        var result = SchematicImport.Import(source, _dir, "Top");

        Assert.True(result.Ok, result.Refusal);
        Assert.True(result.Connectivity!.Matched, string.Join("\n", result.Messages));
        string child = Path.Combine(_dir, "hier_parts", "Stage");
        Assert.Equal([child], result.CreatedCells);
        var (stage, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(child, "schematic", "Stage.csch"));
        Assert.Equal(["IN", "OUT"], stage.Components.Where(c => c.Symbol == SymbolKind.Pin)
                                         .Select(c => c.Parameters.Single(p => p.Name == "Name").Expression).Order());
    }

    private static ForeignSchematic OnePart(string reference, string symbol, string? value, int pins = 2)
    {
        var sym = pins == 2 ? TwoPin(symbol)
                            : new ForeignSymbol(symbol, symbol, "lib", [new ForeignSymbolUnit(1,
                                  [.. Enumerable.Range(1, pins).Select(k => new ForeignPin($"{k}", $"P{k}", ForeignPinType.Passive, -300, 300 - 100 * k))],
                                  [new KitSymbolRectangle(-300, 300, 300, -100 * pins, false)])]);
        return new ForeignSchematic("kicad_sch", "one.kicad_sch",
            [new ForeignSheet("S") { Instances = [new(reference, 1, symbol, 1000, 1000, Value: value)] }], [sym], "S");
    }

    [Theory]
    [InlineData("R1", "R", "10k", "R=10 kOhm")]
    [InlineData("R1", "R", "4k7", "R=4.7 kOhm")]
    [InlineData("C1", "C", "100nF", "C=100 nF")]
    [InlineData("C1", "C", "4u7", "C=4.7 uF")]
    [InlineData("R1", "R", "2R2", "R=2.2 Ohm")]
    [InlineData("L1", "L", "1.5 nH", "L=1.5 nH")]
    [InlineData("C1", "C", "100nF/10V", "C=100 nF")]   // the value with its rating: the rating is not the value
    [InlineData("R1", "R", "10k/1%", "R=10 kOhm")]
    [InlineData("C3", "C-1210", "100nF/10V", "C=100 nF, smt:1210@N")]   // named after its land pattern
    [InlineData("R7", "R-0603", "10k", "R=10 kOhm, smt:0603@N")]
    [InlineData("R1", "R", "DNP", "R=R1_R")]
    public void TheMap_ReadsATwoPinPartsValue_OrMakesItAVariable(string reference, string symbol, string value, string becomes)
    {
        var decision = Assert.Single(SchematicImportMap.Decide(OnePart(reference, symbol, value)));
        Assert.Equal(ImportPartKind.Native, decision.Becomes);
        Assert.Equal($"{reference} → {becomes}", decision.Describe());
        Assert.Equal(value == "DNP", decision.Variable is not null);
    }

    /// <summary>A symbol named after its land pattern becomes the native part WITH that footprint; a name whose suffix is
    /// no case circuitRF knows is not taken for one.</summary>
    [Fact]
    public void ACaseNamedPart_IsNative_WithItsFootprint_AndAnUnknownCaseIsNot()
    {
        Directory.CreateDirectory(_dir);
        var result = SchematicImport.Import(OnePart("C3", "C-1210", "100nF/10V"), _dir, "Cased");
        Assert.True(result.Ok, result.Refusal);
        var (model, _, _) = SchematicPersistence.LoadFromFile(result.SchematicPath!);
        var c = Assert.Single(model.Components, x => x.Symbol == SymbolKind.Capacitor);
        Assert.Equal("smt:1210@N", c.Parameters.Single(p => p.Name == "Footprint").Expression);

        var odd = Assert.Single(SchematicImportMap.Decide(OnePart("R9", "R-WIDE", "10k")));
        Assert.Equal(ImportPartKind.SymbolCell, odd.Becomes);
    }

    [Fact]
    public void AnUnmatchedPart_BecomesASymbolCell_ReusedOnTheSecondImport()
    {
        var source = OnePart("U3", "LM_X", "quad", pins: 3) with { SourcePath = Path.Combine(_dir, "board.kicad_sch") };

        var first = SchematicImport.Import(source, _dir, "First");
        var second = SchematicImport.Import(source, _dir, "Second");

        string cell = Path.Combine(_dir, "board_parts", "LM_X");
        Assert.True(first.Ok && second.Ok, first.Refusal ?? second.Refusal);
        Assert.Equal("U3 → symbol cell LM_X", Assert.Single(first.Mapping).Describe());
        Assert.Equal([cell], first.CreatedCells);
        Assert.Equal([cell], second.ReusedCells);
        Assert.Empty(second.CreatedCells);
    }

    /// <summary>
    /// R-si1-6, measured: two resistors on a 50-mil grid, every pin at an odd multiple of 50. NetlistSchematic's default exact
    /// path snaps each origin to 100 and moves the pins off the source's wires; the import's unsnapped drawing, on a
    /// 50-unit connection grid, puts every pin exactly where the source does.
    /// </summary>
    [Fact]
    public void ASourceOnA50MilGrid_IsDrawnWithEveryPinOnTheSourcesConnectionPoint()
    {
        var source = new ForeignSchematic("kicad_sch", Path.Combine(_dir, "fifty.kicad_sch"),
        [
            new ForeignSheet("S")
            {
                Instances = [new("R1", 1, "R", 1050, 1050, Value: "1k"), new("R2", 1, "R", 1650, 1050, Value: "2k")],
                Wires = [W((1050, 850), (1650, 850)), W((1050, 1250), (1650, 1250))],
            },
        ], [TwoPin("R")], "S");
        (double, double)[] sourcePins = [(1050, 850), (1050, 1250), (1650, 850), (1650, 1250)];

        var built = SchematicImportBuild.Build(source, "Fifty", Path.Combine(_dir, "Fifty", "schematic"), Path.Combine(_dir, "fifty_parts"));
        var top = built.Top!;
        Assert.Equal(50, top.Model.GridSize);
        Assert.Equal(sourcePins.Order(), PinsOf(top.Model).Order());

        var snapped = NetlistSchematic.Build(new CircuitRF.Core.Design.Library("x"), top.TestBench, exact: top.Placement with { SnapToGrid = true });
        Assert.NotEqual(sourcePins.Order(), PinsOf(snapped.Schematic!).Order());

        Assert.True(ImportConnectivity.Compare(source, built).Matched);
    }

    private static IEnumerable<(double, double)> PinsOf(SchematicEditModel m) =>
        m.Components.Where(c => c.Symbol == SymbolKind.Resistor)
         .SelectMany(c => SymbolPortDefs.For(c.Symbol).Select(p => SchematicGeometry.LocalToWorld(p.LocalX, p.LocalY, c.X, c.Y, c.Rotation, c.MirrorX)));

    [Fact]
    public void BuildInto_RenamesACollidingInstanceAndVariable_InOneEdit()
    {
        var cell = CellCreate.Create(_dir, "Open");
        var into = new SchematicEditModel { SchematicDirectory = Path.GetDirectoryName(cell.SchematicPath) };
        into.Components.Add(new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor });
        var vars = new EditableComponent { InstanceName = "VAR1", Symbol = SymbolKind.Var };
        vars.Parameters.Add(new EditableParameter { Name = "R1_R", Expression = "50" });
        into.Components.Add(vars);
        int before = into.Components.Count;

        var edit = SchematicImport.BuildInto(OnePart("R1", "R", "DNP") with { SourcePath = Path.Combine(_dir, "drop.kicad_sch") },
                                             into, (2000, 3000));

        Assert.True(edit.Ok, edit.Refusal);
        Assert.Equal("R2", edit.Renames!.Instances["R1"]);
        Assert.Equal("R1_R_2", edit.Renames.Variables["R1_R"]);
        var drawing = edit.Drawing!;
        var r = drawing.Components.Single(c => c.Symbol == SymbolKind.Resistor);
        Assert.Equal("R2", r.InstanceName);
        Assert.Equal("R1_R_2", r.Parameters.Single(p => p.Name == "R").Expression);
        Assert.Contains(drawing.Components.Where(c => c.Symbol == SymbolKind.Var).SelectMany(c => c.Parameters), p => p.Name == "R1_R_2");
        Assert.Empty(drawing.Analyses);
        Assert.Equal(before, into.Components.Count);
    }
}
