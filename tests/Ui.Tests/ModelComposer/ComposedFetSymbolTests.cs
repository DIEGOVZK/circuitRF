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
    }
}
