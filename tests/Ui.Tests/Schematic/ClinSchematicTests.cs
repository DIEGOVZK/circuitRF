using System.Text.RegularExpressions;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Tests.Schematic;

/// <summary>
/// CLIN, the ideal coupled-line pair, placed on a schematic: its pins, its default parameters and the
/// netlist line a placed one extracts to. Pin ORDER is the contract <c>CLineModel</c> reads — 1/2 are
/// line A's near/far ends, 3/4 line B's, with 3 beside 1 — so the test wires a distinct Term to each
/// pin and checks each Term's net lands in the CLIN line's own slot.
/// </summary>
public sealed class ClinSchematicTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-clin-" + Guid.NewGuid().ToString("N")[..12]);

    public ClinSchematicTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void ClinPins_AreFourCornersOfTheCoupledPair_InModelOrder()
    {
        var pins = SymbolPortDefs.For(SymbolKind.CLine);
        Assert.Equal(4, pins.Length);
        Assert.Equal((-200f, -100f), (pins[0].LocalX, pins[0].LocalY));   // 1: line A near, top-left
        Assert.Equal(( 200f, -100f), (pins[1].LocalX, pins[1].LocalY));   // 2: line A far, top-right
        Assert.Equal((-200f,  100f), (pins[2].LocalX, pins[2].LocalY));   // 3: line B near, bottom-left
        Assert.Equal(( 200f,  100f), (pins[3].LocalX, pins[3].LocalY));   // 4: line B far, bottom-right
    }

    [Fact]
    public void ClinRegistry_NamesTheEngineComponent_AndParsesItsCodes()
    {
        Assert.Equal("CLIN", ComponentTypeRegistry.EngineReference(SymbolKind.CLine));
        Assert.Equal("CL", ComponentTypeRegistry.Get(SymbolKind.CLine).InstancePrefix);
        Assert.True(ComponentTypeRegistry.TryParseCode("CLIN", out var k1, out _));
        Assert.True(ComponentTypeRegistry.TryParseCode("CL", out var k2, out _));
        Assert.Equal(SymbolKind.CLine, k1);
        Assert.Equal(SymbolKind.CLine, k2);
        Assert.Contains(LibraryCatalog.ByCategory(ComponentCategory.TransmissionLine), i => i.Kind == SymbolKind.CLine);

        var names = ComponentTypeRegistry.DefaultParameters(SymbolKind.CLine, 0).Select(p => p.Name);
        Assert.Equal(["Ze", "Zo", "E", "F", "Eo"], names);
    }

    /// <summary>
    /// A .csch holding one CLIN and four Terms, saved and extracted the way <c>circuitrf netlist</c>
    /// extracts it: one CLIN line, four nets in pin order 1..4, each param carrying its unit, and the
    /// blank optional <c>Eo</c> dropped rather than written as <c>Eo=</c>.
    /// </summary>
    [Fact]
    public void ClinWithFourTerms_ExtractsToOneClinLine_InPinOrder()
    {
        var m = new SchematicEditModel();

        var cl = new EditableComponent { InstanceName = "CL1", Symbol = SymbolKind.CLine, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.CLine, 0))
            cl.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        m.Components.Add(cl);

        // Term pins: + at local (0,-200), − at (0,+200); a Ground's one pin is at its origin.
        // P1/P2 sit level with line A's ends, P3/P4 lower and inboard, reaching line B with an L.
        AddTerm(m, "P1", 1, -600,  100);   // + at (-600,-100)
        AddTerm(m, "P2", 2,  600,  100);   // + at ( 600,-100)
        AddTerm(m, "P3", 3, -400,  500);   // + at (-400, 300)
        AddTerm(m, "P4", 4,  400,  500);   // + at ( 400, 300)
        Wire(m, (-600, -100), (-200, -100));
        Wire(m, ( 600, -100), ( 200, -100));
        Wire(m, (-400,  300), (-400,  100), (-200, 100));
        Wire(m, ( 400,  300), ( 400,  100), ( 200, 100));

        string csch = Path.Combine(_root, "clin.csch");
        SchematicPersistence.SaveToFile(csch, m, "clin");
        string cnl = SchematicCircuit.CnlTextOf(csch);

        var lines = cnl.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var clin = Assert.Single(lines, l => l.StartsWith("CLIN:", StringComparison.Ordinal));
        var match = Regex.Match(clin,
            @"^CLIN:CL1\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+Ze=69\.37 Ohm\s+Zo=36\.04 Ohm\s+E=90 deg\s+F=1 GHz$");
        Assert.True(match.Success, $"unexpected CLIN line: {clin}\n--- full netlist ---\n{cnl}");

        string[] clinNets = [.. Enumerable.Range(1, 4).Select(i => match.Groups[i].Value)];
        Assert.Equal(4, clinNets.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("0", clinNets);

        for (int port = 1; port <= 4; port++)
        {
            var term = Assert.Single(lines, l => Regex.IsMatch(l, $@"^\S+:P{port} "));
            string termNet = term.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
            Assert.Equal(clinNets[port - 1], termNet);
        }
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
