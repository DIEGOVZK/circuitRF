// Text between the schematic and symbol editors — PictureClipboard's arrangement, for text. Each editor
// copies its own JSON, and each paste reads its own first; when the clipboard holds the OTHER editor's
// selection instead, the texts in it — and only the texts, the one kind both documents hold in the same
// form — are converted and pasted. Everything else in the selection stays behind.
//
// The two share their units (the connection grid is P = 100 in both) and their axis (Y runs down in
// both), so a text crosses at its own size, rotation and alignment with nothing scaled or flipped. The
// one thing that does not cross is a symbol text's Force readable, which follows a rotated instance and
// has no meaning on a schematic.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input.Platform;

namespace CircuitRF.Ui.Clipboard;

public static class TextClipboard
{
    /// <summary>The texts in a symbol selection on the clipboard, as schematic texts at the coordinates
    /// they were copied from — the schematic's paste positions them in view from there. Empty when the
    /// clipboard holds no symbol selection, or one with no text.</summary>
    public static async Task<List<EditableText>> SymbolTextsAsSchematicAsync(IClipboard clipboard)
        => SymbolTextsAsSchematic(await TextOf(clipboard));

    public static List<EditableText> SymbolTextsAsSchematic(string? text)
        => (SymbolClipboard.PrimitivesIn(text) ?? [])
           .OfType<TextPrimitive>()
           .Select(EditableText.FromPrimitive)
           .ToList();

    /// <summary>The texts in a schematic selection on the clipboard, as symbol text primitives, the group
    /// moved by whole connection-grid steps so its centre is nearest the symbol's origin — a schematic's
    /// coordinates can be anywhere, and a symbol is drawn about (0, 0). Moving by whole steps keeps each
    /// text where it was relative to the grid. Empty when the clipboard holds no schematic selection.</summary>
    public static async Task<List<TextPrimitive>> SchematicTextsAsSymbolAsync(IClipboard clipboard)
        => SchematicTextsAsSymbol(await TextOf(clipboard));

    public static List<TextPrimitive> SchematicTextsAsSymbol(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || SymbolClipboard.PrimitivesIn(text) is not null
            || LayoutFragment.TryDeserialize(text, out _)) return [];
        List<EditableText> texts;
        try { texts = SchematicPersistence.DeserializeSelection(text).CanvasObjs.OfType<EditableText>().ToList(); }
        catch { return []; }
        if (texts.Count == 0) return [];

        var boxes = texts.Select(t => t.GetBoundingBox()).ToList();
        double cx = (boxes.Min(b => b.MinX) + boxes.Max(b => b.MaxX)) / 2;
        double cy = (boxes.Min(b => b.MinY) + boxes.Max(b => b.MaxY)) / 2;
        const double P = 100.0;   // the symbol's connection grid
        double dx = -Math.Round(cx / P) * P, dy = -Math.Round(cy / P) * P;

        return texts.Select(t =>
        {
            var prim = t.ToPrimitive();
            prim.AnchorX += dx;
            prim.AnchorY += dy;
            return prim;
        }).ToList();
    }

    private static Task<string?> TextOf(IClipboard clipboard) => ClipboardRead.TextAsync(clipboard);
}
