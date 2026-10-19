// Rasterizes an import-chooser preview OFF THE UI THREAD (owner, 2026-09-05: the dialog has to stay
// responsive while a preview is produced).
//
// ── The whole point of this file: the UI thread does not draw the preview ─────────────────────────
//
// A custom-draw control would put every one of these calls on the render pass of the dialog's own
// window — and a preview is not a cheap frame. Reading a component library, reconciling its layers
// and building a spatial index over a land pattern happen once per selection change, and the LAST
// thing that should stutter while the user arrows down a list of ninety candidates is the list
// itself. So the pane is a plain Image: this file produces a finished bitmap on a worker thread and
// the UI thread only blits it.
//
// That is safe because every object involved is preview-private. ComponentPreview.Build returns a
// freshly built Symbol and LayoutView reachable from nothing else, so the spatial index built here,
// the SKPath cache and the scratch Technology are touched by one thread only. The two static caches
// the renderers do share are a ConditionalWeakTable and a ConcurrentDictionary; the typefaces are
// Lazy<T> at its thread-safe default. Nothing here reads live editor state, and nothing here writes
// any.

using CircuitRF.Design.Layout;
using SkiaSharp;

namespace CircuitRF.Render;

/// <summary>
/// The two drawings the import chooser shows for the selected candidate: the symbol as the symbol
/// editor draws it, and the land pattern as the layout editor draws it.
/// </summary>
public static class ComponentPreviewRaster
{
    /// <summary>Fraction of the pane left empty around the drawing, per side. Matches the palette
    /// glyph's own inset — a footprint framed edge to edge reads as clipped.</summary>
    private const double Padding = 0.10;

    /// <summary>Half-side of a pad marker, in LOGICAL pixels — multiplied by the render scaling at
    /// use, like every other screen-space constant here. Constant on screen, exactly as the layout
    /// editor's own pin overlay is.</summary>
    private const double PinMarkerHalfPixels = 3.0;

    /// <summary>Pad-identifier type size, in logical pixels.</summary>
    private const double PinLabelPixels = 10.0;

    /// <summary>The symbol raster itself, with no Avalonia in it — which is what lets a headless test
    /// measure where the drawing actually landed inside the bitmap.</summary>
    internal static SKBitmap? RasterSymbol(Symbol? symbol, SchematicRenderTheme theme, int pxW, int pxH, double scaling)
    {
        if (symbol is null || pxW < 8 || pxH < 8) return null;
        if (symbol.Primitives.Count == 0 && symbol.Pins.Count == 0) return null;

        // The BODY's own box. The pins' marks are added below, measured rather than padded — a pin
        // on a stub sits outside the primitive bbox, and its port label sits outside that again.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        if (symbol.Primitives.Count > 0)
        {
            var (p0, q0, p1, q1) = SymbolGeometry.ComputeBb(symbol.Primitives);
            minX = p0; minY = q0; maxX = p1; maxY = q1;
        }
        foreach (var pin in symbol.Pins)
        {
            minX = Math.Min(minX, pin.LocalX); minY = Math.Min(minY, pin.LocalY);
            maxX = Math.Max(maxX, pin.LocalX); maxY = Math.Max(maxY, pin.LocalY);
        }
        if (minX == double.MaxValue) return null;

        // ── The pin marks, which are drawn in PIXELS and cannot be put in a world-space box ─────
        //
        // DrawPinMarkersPlain draws a dot of at least 3 px and writes the pin's name beside it at a
        // size with an 8 px floor — to the RIGHT, the LEFT or straddling it, as that pin's own
        // NameAlign says. Neither is a function of the geometry, so the fit has to be solved rather
        // than computed: each pass measures the marks at the current zoom, widens the box by what they
        // need, and re-fits at the smaller zoom that results. It converges downward in two or three
        // passes and is capped.
        //
        // <b>The side is measured, not assumed.</b> Reserving room on the right for a name that is
        // drawn on the left is the same off-centre error the per-pin measurement below already exists
        // to avoid, made twice: room reserved where nothing is drawn AND none where something is.
        //
        // <b>Measured PER PIN, at that pin's own position.</b> Reserving the widest label at the
        // right-hand edge is what the first attempt did, and it is wrong whenever the longest name
        // does not belong to the rightmost pin — a part with a THERMAL pad in the middle of a row of
        // pads called 1..8 reserved ~25 px of empty space on one side, which threw the drawing off
        // centre by that much at every pane size and held it under 63% of the width (measured, not
        // inferred: ComponentPreviewTests' probe over four pane sizes).
        double zoom = Fit(minX, minY, maxX, maxY, pxW, pxH);
        if (zoom <= 0) return null;

        for (int pass = 0; pass < 3 && symbol.Pins.Count > 0; pass++)
        {
            float fontSize = (float)Math.Max(8.0, zoom * 12.0);
            float dot = (float)Math.Max(3.0, zoom * 5.0);
            using var font = new SKFont(SkiaFonts.PlexBold, fontSize);

            double x0 = minX, y0 = minY, x1 = maxX, y1 = maxY;
            foreach (var pin in symbol.Pins)
            {
                string label = pin.Name is { Length: > 0 } n ? n : $"P{pin.PortIndex + 1}";
                double text = font.MeasureText(label);
                // Room is reserved along the axis the name actually runs on. A Top/Bottom name is
                // turned a quarter turn, so its LENGTH is vertical and its height horizontal — box it
                // the other way round or the label leaves the preview at the bottom of the fit.
                var (left, right, up, down) = pin.NameAlign switch
                {
                    SymbolPinNameAlign.Right  => (dot + 2 + text, (double)dot, (double)dot, (double)(fontSize * 0.85)),
                    SymbolPinNameAlign.Center => (text / 2, text / 2, (double)dot, (double)(fontSize * 0.85)),
                    SymbolPinNameAlign.Top    => ((double)(fontSize * 0.85), (double)(fontSize * 0.85), (double)dot, dot + 2 + text),
                    SymbolPinNameAlign.Bottom => ((double)(fontSize * 0.85), (double)(fontSize * 0.85), dot + 2 + text, (double)dot),
                    _                         => ((double)dot, dot + 2 + text, (double)dot, (double)(fontSize * 0.85)),
                };
                x0 = Math.Min(x0, pin.LocalX - left / zoom);
                y0 = Math.Min(y0, pin.LocalY - up   / zoom);
                x1 = Math.Max(x1, pin.LocalX + right / zoom);
                y1 = Math.Max(y1, pin.LocalY + down  / zoom);
            }

            double next = Fit(x0, y0, x1, y1, pxW, pxH);
            minX = x0; minY = y0; maxX = x1; maxY = y1;
            if (next <= 0) return null;
            if (Math.Abs(next - zoom) / zoom < 0.01) { zoom = next; break; }
            zoom = next;
        }

        // Pan so the drawing's centre lands on the pane's centre — off the SOLVED box, marks folded
        // in. Y is not flipped: the symbol canvas is screen-sense (+y down) and DrawSymbol maps both
        // axes the same way.
        double panX = (minX + maxX) * 0.5 - pxW / (2.0 * zoom);
        double panY = (minY + maxY) * 0.5 - pxH / (2.0 * zoom);

        return Raster(pxW, pxH, canvas =>
        {
            canvas.Clear(theme.Background);
            SchematicRenderer.DrawSymbol(
                canvas, symbol.Primitives,
                compX: 0, compY: 0, rotation: SymbolRotation.R0, mirrorX: false,
                panX: panX, panY: panY, zoom: zoom, theme: theme);

            // The same dot-and-port-label pass the editor and the symbol export both use, so a part
            // previewed here and the same part opened afterwards look alike.
            SymbolEditorRenderer.DrawPinMarkersPlain(canvas, symbol.Pins, panX, panY, zoom, theme);
        });
    }

    /// <summary>As <see cref="RasterSymbol"/>: the Skia half, testable without an Avalonia host.</summary>
    internal static SKBitmap? RasterFootprint(
        LayoutView? view, Technology? tech, LayoutRenderTheme theme, int pxW, int pxH, double scaling)
    {
        if (view is null || pxW < 8 || pxH < 8) return null;
        if (view.Shapes.Count == 0 && view.Pins.Count == 0) return null;

        var bb = Bbox.Empty;
        foreach (var shape in view.Shapes)
        {
            // A LabelShape's own bbox is its ANCHOR, not the glyphs it paints — which is why the
            // renderer has a separate measurement for it. Framing on the anchor alone puts a
            // silkscreen designator half outside the pane and shifts everything else off centre.
            bb = bb.Union(shape is LabelShape label
                ? LayoutRenderer.MeasureLabelWorldBbox(label) ?? LayoutGeometry.BboxOf(shape)
                : LayoutGeometry.BboxOf(shape));
        }

        // A pad with no copper of its own is still a terminal and still has to be framed — the
        // reconciliation can legitimately leave a pin on a layer whose shapes went elsewhere.
        foreach (var pin in view.Pins)
        {
            long r = Math.Max(pin.WidthDbu / 2, 1);
            bb = bb.Union(new Bbox(pin.X - r, pin.Y - r, pin.X + r, pin.Y + r));
        }
        if (bb.IsEmpty) return null;

        var vp = LayoutViewport.ZoomToFit(bb, pxW, pxH, marginFrac: Padding);

        // The pad marks, solved rather than padded — the same problem the symbol half has and the
        // same answer: each pad's dot and identifier are drawn at a FIXED pixel size beside that
        // pad's own position, so the world box they need depends on the zoom, which depends on the
        // box. Measured per pad, at that pad's position: reserving the widest name at the right-hand
        // edge is wrong the moment the longest name is not the rightmost pad, which is the ordinary
        // case (a THERMAL pad among pads called 1..8).
        for (int pass = 0; pass < 3 && view.Pins.Count > 0; pass++)
        {
            float half = (float)(PinMarkerHalfPixels * scaling);
            float fontSize = (float)(PinLabelPixels * scaling);
            using var font = new SKFont(SkiaFonts.PlexRegular, fontSize);

            var grown = bb;
            foreach (var pin in view.Pins)
            {
                double right = pin.Name.Length > 0 ? half + 2 + font.MeasureText(pin.Name) : half;
                double up = pin.Name.Length > 0 ? half + 2 + fontSize : half;
                grown = grown.Union(new Bbox(
                    pin.X - (long)Math.Ceiling(half / vp.Zoom),
                    pin.Y - (long)Math.Ceiling(half / vp.Zoom),
                    pin.X + (long)Math.Ceiling(right / vp.Zoom),
                    pin.Y + (long)Math.Ceiling(up / vp.Zoom)));
            }

            var next = LayoutViewport.ZoomToFit(grown, pxW, pxH, marginFrac: Padding);
            bb = grown;
            if (Math.Abs(next.Zoom - vp.Zoom) / vp.Zoom < 0.01) { vp = next; break; }
            vp = next;
        }

        return Raster(pxW, pxH, canvas =>
        {
            // No grid: this is a picture of a part, not an editing surface, and at a footprint's scale
            // the grid is the loudest thing in the pane.
            LayoutRenderer.Draw(canvas, view, tech, vp, new LayoutRenderOptions { Theme = theme, ShowGrid = false });
            DrawPadMarkers(canvas, view, vp, theme, scaling);
        });
    }

    /// <summary>
    /// A filled square per pad plus its identifier — the layout editor's own pin glyph
    /// (<c>LayoutRenderer</c>'s constant-pixel square), drawn here because <c>Draw</c> renders a
    /// view's pins only through a placed INSTANCE of it and the preview places nothing.
    ///
    /// <para>The identifier is drawn, not just the dot: two land patterns can be the same copper with
    /// the pads numbered differently, and that difference is invisible without it — which is one of
    /// the cases this whole preview exists to catch.</para>
    /// </summary>
    private static void DrawPadMarkers(
        SKCanvas canvas, LayoutView view, LayoutViewport vp, LayoutRenderTheme theme, double scaling)
    {
        if (view.Pins.Count == 0) return;

        float half = (float)(PinMarkerHalfPixels * scaling);
        using var dot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.PCellPin };
        using var text = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = theme.PCellPin };
        using var font = new SKFont(SkiaFonts.PlexRegular, (float)(PinLabelPixels * scaling));

        foreach (var pin in view.Pins)
        {
            float x = (float)vp.WorldToScreenX(pin.X);
            float y = (float)vp.WorldToScreenY(pin.Y);
            canvas.DrawRect(x - half, y - half, half * 2, half * 2, dot);
            if (pin.Name.Length > 0)
                canvas.DrawText(pin.Name, x + half + 2, y - half - 2, SKTextAlign.Left, font, text);
        }
    }

    /// <summary>Pixels per world unit that fits the box in the pane with <see cref="Padding"/> clear
    /// on each side. The symbol's counterpart to <see cref="LayoutViewport.ZoomToFit"/>.</summary>
    /// <summary>
    /// A whole schematic, fitted to the pane — Import Schematic's preview (brief-schimp-5 R-si5-2), drawn by the
    /// renderer the editor draws with, so what the dialog shows is what the opened cell shows. No grid: a preview is a
    /// picture of the drawing, not a place to draw. Null when there is nothing to draw or no room.
    /// </summary>
    public static SKBitmap? RasterSchematic(SchematicEditModel? model, SchematicRenderTheme theme, int pxW, int pxH)
    {
        if (model is null || pxW < 8 || pxH < 8) return null;
        var (rm, idx) = model.BuildRenderModel();
        if (DocumentExtents.SchematicBox(model, rm) is not { } bb) return null;
        double zoom = Fit(bb.X0, bb.Y0, bb.X1, bb.Y1, pxW, pxH);
        if (zoom <= 0) return null;
        double panX = (bb.X0 + bb.X1) * 0.5 - pxW / (2.0 * zoom);
        double panY = (bb.Y0 + bb.Y1) * 0.5 - pxH / (2.0 * zoom);
        return Raster(pxW, pxH, canvas =>
            SchematicRenderer.Draw(canvas, (pxW, pxH), rm, idx, panX, panY, zoom, theme, excludeGrid: true));
    }

    private static double Fit(double minX, double minY, double maxX, double maxY, int pxW, int pxH)
    {
        double w = Math.Max(maxX - minX, 1e-9), h = Math.Max(maxY - minY, 1e-9);
        return Math.Min(pxW / w, pxH / h) * (1.0 - 2.0 * Padding);
    }

    /// <summary>Runs <paramref name="draw"/> onto an off-screen raster surface.</summary>
    private static SKBitmap Raster(int pxW, int pxH, Action<SKCanvas> draw)
    {
        var bitmap = new SKBitmap(new SKImageInfo(pxW, pxH, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap)) draw(canvas);
        return bitmap;
    }
}
