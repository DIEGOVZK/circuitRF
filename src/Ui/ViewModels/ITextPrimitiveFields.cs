namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The text rows of the Properties panel, as <c>TextPrimitiveFieldsView</c> binds them. The symbol
/// editor's inspector and the schematic's text inspector both implement it, so a text is edited with
/// the same fields in both editors — and a field added to one appears in the other.
/// </summary>
public interface ITextPrimitiveFields
{
    string           TextContent       { get; set; }
    double           TextAnchorX       { get; set; }
    double           TextAnchorY       { get; set; }
    double           TextFontSize      { get; set; }
    SymbolFontStyle  TextFontStyle     { get; set; }
    SymbolColorRole  TextColorRole     { get; set; }
    SymbolTextAlign  TextAlign         { get; set; }
    SymbolTextVAlign TextVAlign        { get; set; }
    SymbolRotation   TextRotation      { get; set; }
    bool             TextForceReadable { get; set; }

    /// <summary>Force-readable only means something for text inside a symbol, which a rotated
    /// instance can turn upside down; free text on a schematic has no instance to follow.</summary>
    bool ShowTextForceReadable { get; }
}
