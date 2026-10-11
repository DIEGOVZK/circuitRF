// brief-img-8-schematic-symbols.md R-im8-1 — the built-in templates are circuitRF's own symbols, pins and all.

using System.Linq;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class SymbolTemplatesTests
{
    [Fact]
    public void EveryBuiltInTemplate_HasThePinCountOfItsBuiltInSymbolsEntry()
    {
        var builtIn = SymbolTemplates.BuiltIn();

        Assert.NotEmpty(builtIn);
        foreach (var t in builtIn.Where(t => !t.Composed))
            Assert.Equal(BuiltInSymbols.Primitives(t.Source!.Value).Pins.Count, t.Pins.Count);
        // The stubs are the TLIN drawing with one lead left off; a transistor in its envelope circle keeps its pins.
        Assert.All(builtIn.Where(t => t.Composed), t => Assert.Equal(
            t.Kind is ImageSymbolKind.OpenStub or ImageSymbolKind.ShortStub ? 1 : BuiltInSymbols.Primitives(t.Source!.Value).Pins.Count, t.Pins.Count));
    }
}
