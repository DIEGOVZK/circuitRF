using CircuitRF.Design.Modeling;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-11 §4 (R-mc11-2, R-mc11-3): a symbol's pins say what it can draw — a 2-pin symbol is refused, and a
/// 4-pin one is refused on a model with no thermal node — and the symbol chosen at export is the one a placement carries.
/// </summary>
public sealed class SymbolChoiceTests
{
    private static string Csym(string dir, string name, int pins)
    {
        string path = Path.Combine(dir, name);
        var symbol = new Symbol([], [.. Enumerable.Range(0, pins).Select(k => new SymbolPin(0, 100 * k, k, $"p{k + 1}"))]);
        SymbolPersistence.SaveToFile(path, symbol);
        return path;
    }

    [Fact]
    public void TwoPinAndUnheatedFourPinSymbols_AreRefused()
    {
        string cmc = ExportFixture.Document();
        string dir = Path.GetDirectoryName(cmc)!, path = Path.Combine(dir, "fet.cmodel");
        Csym(dir, "two.csym", 2);
        Csym(dir, "four.csym", 4);

        var two = Assert.Throws<ModelingException>(() => CmodelExport.Export(cmc, path, symbol: "two.csym"));
        Assert.Contains("has 2 pins; a FET symbol has pins 1, 2 and 3", two.Message);
        var four = Assert.Throws<ModelingException>(() => CmodelExport.Export(cmc, path, symbol: "four.csym"));
        Assert.Contains("fourth pin, the thermal node, and this model does not self-heat", four.Message);
        Assert.False(File.Exists(path));

        // The list a picker offers holds neither.
        var offered = ModelSymbols.Candidates(dir, dir, thermal: false).Select(c => c.Value).ToList();
        Assert.DoesNotContain("two.csym", offered);
        Assert.DoesNotContain("four.csym", offered);
        Assert.Contains("FetAngelov", offered);
        Assert.DoesNotContain("JfetN", offered);
    }

    [Fact]
    public void TheStoredSymbol_IsUsedOnPlacement()
    {
        string cmc = ExportFixture.Document();
        string dir = Path.GetDirectoryName(cmc)!;
        Csym(dir, "hemt.csym", 3);
        string path = Path.Combine(dir, "models", "fet.cmodel");

        var result = CmodelExport.Export(cmc, path, symbol: "hemt.csym");
        Assert.Equal("../hemt.csym", result.Model.Symbol);
        Assert.Equal("hemt.csym", CmodelExport.Settings(CmcFile.Read(cmc)).Symbol);

        var (comps, _, _, _) = SchematicPersistence.DeserializeSelection(ComposedFetPlacement.Payload(path));
        var c = Assert.Single(comps);
        string P(string name) => c.Parameters.Single(p => p.Name == name).Expression;
        Assert.Equal(SymbolKind.ComposedFET, c.Symbol);
        Assert.Equal(Path.GetFullPath(path).Replace('\\', '/'), P("File"));
        Assert.Equal("4", P("N"));
        Assert.Equal("../hemt.csym", P("Symbol"));
        // The provider draws it from the model's folder, where the relative path points.
        Assert.Null(ComposedFetSymbolProvider.Diagnose("X1", P("File"), P("Symbol"), dir));
    }
}
