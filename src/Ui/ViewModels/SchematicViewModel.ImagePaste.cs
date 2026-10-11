using System;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>Edit ▸ Paste Image as Schematic — where the pasted drawing goes on this sheet.</summary>
public sealed partial class SchematicViewModel
{
    /// <summary>
    /// The top-left corner a pasted picture of <paramref name="width"/> × <paramref name="height"/> takes: the middle of
    /// the view when that leaves it clear of everything already on the sheet, else to the right of it — never on it, so
    /// nothing pasted connects to a wire or pin already drawn. <c>InView</c>: the picture lies wholly in the view, so the
    /// caller need not frame it.
    /// </summary>
    public (double X, double Y, bool InView) PicturePastePlace(double width, double height)
    {
        var view = ViewportProvider?.Invoke();
        var bb = SchematicPasteGeometry.BoundingBox(EditModel.Components, EditModel.Wires, EditModel.CanvasObjects);
        // Component origins bound the sheet, not their glyphs and labels: a margin of a few grid steps covers those.
        double gap = 6 * Math.Max(EditModel.GridSize, 1);
        if (view is { } v)
        {
            double x = v.CenterX - width / 2, y = v.CenterY - height / 2;
            bool clear = bb is not { } b
                         || x + width + gap < b.MinX || x - gap > b.MaxX || y + height + gap < b.MinY || y - gap > b.MaxY;
            if (clear) return (x, y, v.Contains(x, y, x + width, y + height));
        }
        if (bb is { } sheet)
        {
            double x = sheet.MaxX + gap, y = sheet.MinY;
            return (x, y, view is { } w && w.Contains(x, y, x + width, y + height));
        }
        return (0, 0, false);
    }
}
