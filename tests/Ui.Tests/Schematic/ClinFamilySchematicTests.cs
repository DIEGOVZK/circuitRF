using System.Text.RegularExpressions;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.Schematic;

/// <summary>
/// The physical coupled pairs — MCLIN, BCLIN, MBCLIN — placed on a schematic. They keep CLIN's pins
/// exactly (so a CLIN is swapped for one without rewiring), and a placed one extracts to one line whose
/// four nets are its pins in model order; a broadside pair's empty cross-section rows never reach the
/// netlist, and a stackup fills them in.
/// </summary>
public sealed class ClinFamilySchematicTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-clinfam-" + Guid.NewGuid().ToString("N")[..12]);

    public ClinFamilySchematicTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    public static TheoryData<SymbolKind> Kinds => new() { SymbolKind.MCLine, SymbolKind.BCLine, SymbolKind.MBCLine };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Pins_AreClinsPins_InClinsOrder(SymbolKind kind)
    {
        var clin = SymbolPortDefs.For(SymbolKind.CLine);
        var pins = SymbolPortDefs.For(kind);
        Assert.Equal(clin.Select(p => (p.LocalX, p.LocalY)), pins.Select(p => (p.LocalX, p.LocalY)));
    }

    [Theory]
    [InlineData("MCLIN", "MCL", SymbolKind.MCLine, ComponentCategory.Microstrip)]
    [InlineData("BCLIN", "BCL", SymbolKind.BCLine, ComponentCategory.TransmissionLine)]
    [InlineData("MBCLIN", "MBCL", SymbolKind.MBCLine, ComponentCategory.Microstrip)]
    public void Registry_NamesTheEngineComponent_AndParsesItsCodes(string engine, string prefix, SymbolKind kind,
                                                                   ComponentCategory category)
    {
        Assert.Equal(engine, ComponentTypeRegistry.EngineReference(kind));
        Assert.Equal(prefix, ComponentTypeRegistry.Get(kind).InstancePrefix);
        Assert.True(ComponentTypeRegistry.TryParseCode(engine, out var k1, out _));
        Assert.True(ComponentTypeRegistry.TryParseCode(prefix, out var k2, out _));
        Assert.Equal(kind, k1);
        Assert.Equal(kind, k2);
        Assert.Contains(LibraryCatalog.ByCategory(category), i => i.Kind == kind);
        Assert.NotEqual("", ComponentTypeRegistry.TerminalNote(kind));
        foreach (var p in ComponentTypeRegistry.DefaultParameters(kind, 0).Where(p => p.Name is not ("SignalLayer" or "GroundReference")))
            Assert.NotEqual("", ComponentTypeRegistry.ParameterDescription(kind, p.Name));
    }

    [Fact]
    public void BroadsideLayerRows_AreSignalLayerChoices()
    {
        foreach (var kind in new[] { SymbolKind.BCLine, SymbolKind.MBCLine })
        {
            Assert.Equal(ComponentTypeRegistry.LayerChoiceKind.Signal, ComponentTypeRegistry.LayerChoiceKindFor(kind, "LayerA"));
            Assert.Equal(ComponentTypeRegistry.LayerChoiceKind.Signal, ComponentTypeRegistry.LayerChoiceKindFor(kind, "LayerB"));
            Assert.Null(ComponentTypeRegistry.LayerChoiceKindFor(kind, "Hs"));
        }
    }

    [Theory]
    [InlineData(SymbolKind.MCLine, @"^MCLIN:MCL1\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+W=2\.9 mm\s+S=0\.5 mm\s+L=10 mm$")]
    [InlineData(SymbolKind.BCLine, @"^BCLIN:BCL1\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+W=0\.4 mm\s+O=0 mm\s+L=10 mm$")]
    [InlineData(SymbolKind.MBCLine, @"^MBCLIN:MBCL1\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+W=0\.4 mm\s+O=0 mm\s+L=10 mm$")]
    public void PairWithFourTerms_ExtractsToOneLine_InPinOrder(SymbolKind kind, string pattern)
    {
        var m = new SchematicEditModel();
        string prefix = ComponentTypeRegistry.Get(kind).InstancePrefix;
        var cl = new EditableComponent { InstanceName = prefix + "1", Symbol = kind, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
            cl.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        m.Components.Add(cl);
        AddTerm(m, "P1", 1, -600,  100);
        AddTerm(m, "P2", 2,  600,  100);
        AddTerm(m, "P3", 3, -400,  500);
        AddTerm(m, "P4", 4,  400,  500);
        Wire(m, (-600, -100), (-200, -100));
        Wire(m, ( 600, -100), ( 200, -100));
        Wire(m, (-400,  300), (-400,  100), (-200, 100));
        Wire(m, ( 400,  300), ( 400,  100), ( 200, 100));

        string csch = Path.Combine(_root, $"{prefix}.csch");
        SchematicPersistence.SaveToFile(csch, m, prefix);
        string cnl = SchematicCircuit.CnlTextOf(csch);

        var lines = cnl.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        string engine = ComponentTypeRegistry.EngineReference(kind);
        var line = Assert.Single(lines, l => l.StartsWith(engine + ":", StringComparison.Ordinal));
        var match = Regex.Match(line, pattern);
        Assert.True(match.Success, $"unexpected {engine} line: {line}\n--- full netlist ---\n{cnl}");

        string[] nets = [.. Enumerable.Range(1, 4).Select(i => match.Groups[i].Value)];
        Assert.Equal(4, nets.Distinct(StringComparer.Ordinal).Count());
        for (int port = 1; port <= 4; port++)
        {
            var term = Assert.Single(lines, l => Regex.IsMatch(l, $@"^\S+:P{port} "));
            Assert.Equal(nets[port - 1], term.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
        }
    }

    /// <summary>
    /// The stackup fills every EMPTY cross-section row of a BCLIN from the layers it resolves; a row the
    /// user typed keeps its own value and is not overridden.
    /// </summary>
    [Fact]
    public void Bclin_TakesItsCrossSectionFromTheStackup_UnlessARowIsTyped()
    {
        var parameters = ComponentTypeRegistry.DefaultParameters(SymbolKind.BCLine, 0)
            .Select(d => new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit }).ToList();
        parameters.Single(p => p.Name == "Hs").Expression = "80";

        var inj = BroadsideSubstrateInjection.Build(CoupledClinPCellTests.StriplineTech(), SymbolKind.BCLine, parameters);
        var o = inj.Overrides.ToDictionary(x => x.Name, x => double.Parse(x.Expression, System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(o.ContainsKey("Hs"));                     // typed: the user's 80 µm stands
        Assert.Equal(73e-6, o["Ha"], 12);
        Assert.Equal(77e-6, o["Hb"], 12);
        Assert.Equal(25e-6, o["Ta"], 12);
        Assert.Equal(18e-6, o["Tb"], 12);
        Assert.Equal(3.0, o["Er"], 12);
        Assert.Equal(0.002, o["TanD"], 12);
        Assert.Empty(inj.Messages);

        // No technology: nothing injected, and the message names what takes a default.
        var none = BroadsideSubstrateInjection.Build(null, SymbolKind.BCLine, parameters);
        Assert.Empty(none.Overrides);
        Assert.Contains(none.Messages, msg => msg.Contains("Ha", StringComparison.Ordinal) && msg.Contains("default", StringComparison.Ordinal));
    }

    private static void AddTerm(SchematicEditModel m, string name, int num, double x, double y)
    {
        var t = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Term, X = x, Y = y };
        t.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        t.Parameters.Add(new EditableParameter { Name = "Z",   Expression = "50", Unit = "Ω" });
        m.Components.Add(t);
        m.Components.Add(new EditableComponent
            { InstanceName = "G" + name, Symbol = SymbolKind.Ground, X = x, Y = y + 200 });
    }

    private static void Wire(SchematicEditModel m, params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        m.Wires.Add(w);
    }
}
