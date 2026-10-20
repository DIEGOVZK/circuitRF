using System.IO;
using System.Linq;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>brief-modelcomposer-1 R-mc1-9: a composed FET draws the model's symbol — a <c>.csym</c>
/// beside the <c>.cmodel</c>, pins 1/2/3 bound by number — and falls back to the FET glyph, saying so
/// once, when that symbol does not resolve.</summary>
public sealed class ComposedFetSymbolTests
{
    [Fact]
    public void TheModelsCsym_IsDrawn_AndAMissingOneFallsBackWithOneWarning()
    {
        string cmodel = ComposedTestModels.WriteTemp(ComposedTestModels.Model() with { Symbol = "gan.csym" });
        string dir = Path.GetDirectoryName(cmodel)!;
        var own = new Symbol([],
            [new SymbolPin(-300, 0, 0, "G"), new SymbolPin(0, -300, 1, "D"), new SymbolPin(0, 300, 2, "S")],
            portCount: 3);
        SymbolPersistence.SaveToFile(Path.Combine(dir, "gan.csym"), own);

        // Blank on the instance: the model's own symbol, its pins where the .csym put them.
        var drawn = ComposedFetSymbolProvider.Resolve(ComposedFetSymbolProvider.RefFor(cmodel, ""), dir);
        Assert.Equal(CellSymbolState.Resolved, drawn.State);
        Assert.Equal([-300.0, 0.0, 0.0], drawn.Symbol!.Pins.OrderBy(p => p.PortIndex).Select(p => p.LocalX));
        Assert.Null(ComposedFetSymbolProvider.Diagnose("X1", cmodel, "", dir));

        // Named per instance and missing: the standard FET glyph, and the warning names it.
        var fallback = ComposedFetSymbolProvider.Resolve(ComposedFetSymbolProvider.RefFor(cmodel, "nope.csym"), dir);
        Assert.Equal(CellSymbolState.Resolved, fallback.State);
        Assert.Equal(BuiltInSymbols.Primitives(SymbolKind.FetCurtice, 3).Pins.Select(p => (p.LocalX, p.LocalY)),
                     fallback.Symbol!.Pins.Select(p => (p.LocalX, p.LocalY)));
        Assert.Contains("'nope.csym'", ComposedFetSymbolProvider.Diagnose("X1", cmodel, "nope.csym", dir));
        // The FET glyph's shape says which lead is which: no pin name is printed beside it. The .csym keeps its names.
        Assert.DoesNotContain(fallback.Symbol.Pins, p => SymbolPinNames.IsStated(p.Name));
        Assert.Equal(["G", "D", "S"], drawn.Symbol.Pins.Select(p => p.Name));

        // A model that self-heats: the standard glyph gains the thermal pin t, on the right.
        var heated = ComposedFetSymbolProvider.Resolve(ComposedFetSymbolProvider.RefFor(HotModel, ""), Path.GetDirectoryName(HotModel));
        Assert.Equal((200.0, 0.0), heated.Symbol!.Pins.Single(p => p.PortIndex == 3) is var t ? (t.LocalX, t.LocalY) : default);
    }

    /// <summary>A model that self-heats, so the standard glyph has the thermal pin.</summary>
    private static readonly string HotModel = ComposedTestModels.WriteTemp(
        ComposedTestModels.Model(Core.Devices.Composed.ModelTemperature.Range(0, 200)) with { Thermal = Core.Devices.Composed.ModelThermal.Single(25, 1e-9) });

    private static SchematicEditModel Placed(bool wireThermalPin)
    {
        var m = new SchematicEditModel();
        var c = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.ComposedFET, X = 0, Y = 0 };
        c.Parameters.Add(new EditableParameter { Name = "File", Expression = HotModel });
        m.Components.Add(c);
        m.Wires.Add(Wire((-400, 0), (-200, 0)));
        m.Wires.Add(Wire((0, -400), (0, -200)));
        m.Wires.Add(Wire((0, 200), (0, 400)));
        if (wireThermalPin) m.Wires.Add(Wire((200, 0), (400, 0)));
        return m;

        static EditableWire Wire((double, double) a, (double, double) b)
        {
            var w = new EditableWire();
            w.Points.Add(a);
            w.Points.Add(b);
            return w;
        }
    }

    /// <summary>MC-9 R-mc9-2 on a schematic: pin 4 is the thermal node t, written only when something is wired to it — so a
    /// model that does not self-heat places with the standard glyph and extracts as g d s.</summary>
    [Fact]
    public void TheThermalPin_IsANetOnlyWhenWired()
    {
        static Core.Design.Instance X1(SchematicEditModel m) =>
            NetExtractor.Extract(m).TestBench.Instances.Single(i => i.InstanceName == "X1");
        Assert.Equal(3, X1(Placed(wireThermalPin: false)).NetBindings.Count);
        Assert.Equal(4, X1(Placed(wireThermalPin: true)).NetBindings.Count);
    }

    /// <summary>The rename to ComposedFET: a schematic saved with the old spelling still opens as a composed FET.</summary>
    [Fact]
    public void ASchematicSavedAsComposedFet_StillOpens()
    {
        string json = SchematicPersistence.Serialize(Placed(wireThermalPin: false), "Bench");
        Assert.Contains("\"ComposedFET\"", json);
        string path = Path.Combine(Path.GetTempPath(), $"crf-composed-{System.Guid.NewGuid():N}.csch");
        File.WriteAllText(path, json.Replace("\"ComposedFET\"", "\"ComposedFet\""));
        try { Assert.Equal(SymbolKind.ComposedFET, SchematicPersistence.LoadFromFile(path).model.Components.Single().Symbol); }
        finally { File.Delete(path); }
    }
}
