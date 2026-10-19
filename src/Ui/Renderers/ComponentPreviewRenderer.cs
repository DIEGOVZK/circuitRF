// The Avalonia adapter over CircuitRF.Render's ComponentPreviewRaster — two calls and one Wrap.
//
// The drawing itself moved below the UI firewall with every other renderer (R-rnd1-6): the fit
// solver, the pad markers and the raster surface are Skia and the design model, and nothing else.
// What could not move is the last step, which produces an Avalonia Bitmap for the dialog's Image to
// blit — and that split already existed, for exactly this reason, before there was a project to move
// the Skia half into.
//
// The comment that used to head the whole file — that this runs OFF the UI thread, and why that is
// safe — belongs with the drawing and travelled with it.

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

/// <summary>
/// The two drawings the import chooser shows for the selected candidate: the symbol as the symbol
/// editor draws it, and the land pattern as the layout editor draws it.
/// </summary>
public static class ComponentPreviewRenderer
{
    /// <summary>
    /// The symbol, auto-fitted. Null when there is no symbol to draw or the pane is too small to
    /// draw one in — both of which the dialog states in words rather than showing an empty box.
    /// </summary>
    /// <param name="pxW">PHYSICAL pixels, not logical — the pane's width times the render scaling.
    /// Getting this wrong is not subtle: rendering a pane's LOGICAL width and then declaring the
    /// result high-DPI hands Avalonia a bitmap with a quarter of the pixels the pane has, which it
    /// scales up (owner, 2026-09-05: "the previews are rendered at a terribly low resolution").</param>
    public static Bitmap? RenderSymbol(Symbol? symbol, SchematicRenderTheme theme, int pxW, int pxH, double scaling)
        => Wrap(ComponentPreviewRaster.RasterSymbol(symbol, theme, pxW, pxH, scaling));

    /// <summary>
    /// One land pattern, auto-fitted, drawn through <paramref name="tech"/> — which is
    /// <see cref="ComponentPreview.Model.Technology"/>, the destination's layers plus this part's own,
    /// so a pad is already the colour it will be once imported.
    /// </summary>
    /// <param name="pxW">PHYSICAL pixels — see <see cref="RenderSymbol"/>.</param>
    public static Bitmap? RenderFootprint(
        LayoutView? view, Technology? tech, LayoutRenderTheme theme, int pxW, int pxH, double scaling)
        => Wrap(ComponentPreviewRaster.RasterFootprint(view, tech, theme, pxW, pxH, scaling));

    /// <summary>A whole schematic, fitted — Import Schematic's preview (brief-schimp-5 R-si5-2).</summary>
    /// <param name="pxW">PHYSICAL pixels — see <see cref="RenderSymbol"/>.</param>
    public static Bitmap? RenderSchematic(SchematicEditModel? model, SchematicRenderTheme theme, int pxW, int pxH)
        => Wrap(ComponentPreviewRaster.RasterSchematic(model, theme, pxW, pxH));

    /// <summary>
    /// The Skia raster as an Avalonia bitmap. The pixel buffer is COPIED by the
    /// <see cref="Bitmap"/> constructor, so the <see cref="SKBitmap"/> is disposed here and the
    /// result outlives it.
    /// </summary>
    private static Bitmap? Wrap(SKBitmap? raster)
    {
        if (raster is null) return null;
        using (raster)
        {
            // DPI 96 — deliberately NOT the supersample factor.
            //
            // Stamping a higher DPI makes Bitmap.Size report a smaller LOGICAL size, which is only
            // useful to something that lays the bitmap out from that size. PreviewImage does not: it
            // blits the whole source into its own bounds, so the only thing the stamp could do here is
            // reintroduce the scale factor this preview spent three rounds getting out of the way.
            // At 96 the reported size IS the pixel count, which is the one number that governs
            // sharpness and the one the caller actually chose.
            return new Bitmap(
                PixelFormat.Bgra8888, AlphaFormat.Premul,
                raster.GetPixels(), new PixelSize(raster.Width, raster.Height),
                new Vector(96, 96), raster.RowBytes);
        }
    }
}
