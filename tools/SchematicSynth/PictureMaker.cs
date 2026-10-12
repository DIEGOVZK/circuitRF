using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Render;
using SkiaSharp;
using Symbol = CircuitRF.Design.Symbol.Symbol;

namespace CircuitRF.SchematicSynth;

/// <summary>Everything one generated picture writes.</summary>
/// <param name="Classes">Each part's class, once per part — the histogram's input.</param>
public sealed record PictureFiles(
    string Id, string ImageName, byte[] Image, IReadOnlyDictionary<string, byte[]> Masks, MlLabel Label, string Netlist,
    IReadOnlyList<string> Classes)
{
    /// <summary>How the picture was made, one comma-separated line: layout, theme, grid, screenshot conditions.</summary>
    public string Conditions { get; init; } = "";

    /// <summary>The symbol mask, one byte a pixel at the picture's size — what the pin-on-ink check reads.</summary>
    public byte[] SymbolMask { get; init; } = [];

    /// <summary>The drawing the picture is of — what the netlist round trip extracts.</summary>
    public Drawing? Drawing { get; init; }

    /// <summary>Every drawing in the picture: one, or two side by side (brief-iml3 R-iml3-3).</summary>
    public IReadOnlyList<Drawing> Drawings { get; init; } = [];
}

/// <summary>
/// One drawing to one labelled picture (brief-iml-2 R-iml2-3, R-iml2-4).
///
/// <para><b>Every pixel is <see cref="SchematicRenderer"/>'s</b>, the renderer the application draws with, under a
/// random view: the shipped theme light or dark, the grid on or off, a zoom at which a resistor's body spans 12–300
/// px, a random origin and crop, now and then a selection and a placed picture underneath. Then the screenshot
/// conditions (<see cref="Screenshot"/>).</para>
///
/// <para><b>The labels are exact because nothing is measured off the picture.</b> A part's body box is
/// <see cref="GlyphBody"/> through the instance transform and the view; its pins are the symbol's pins; a word's quad is
/// what <see cref="SchematicRenderer.MeasureTexts"/> reports the renderer drew. The masks are the renderer drawing
/// filtered copies of the model — wires only, symbols without their labels, labels only — each thresholded against
/// the background. Each pass gets its own model instance (the trap the brief names: a renderer cache narrowed by one
/// pass must not reach the next).</para>
/// </summary>
public static class PictureMaker
{
    /// <summary>The body long side of a resistor, schematic units — what the zoom is chosen against.</summary>
    private static readonly double ResistorBody = GlyphBody.Of(SymbolKind.Resistor, 2).Height;

    private static readonly ColorTheme Shipped = LoadShippedTheme();

    /// <summary>The theme the application ships, read from the renderer's own resources — never a user's override of it,
    /// which would make a batch depend on the machine it ran on.</summary>
    private static ColorTheme LoadShippedTheme()
    {
        using var stream = typeof(ThemeResolver).Assembly.GetManifestResourceStream("CircuitRF.Render.Assets.Color.Default.ccolor")
            ?? throw new InvalidOperationException("The shipped colour theme is not in CircuitRF.Render.");
        using var reader = new StreamReader(stream);
        return ColorThemeIo.Load(reader.ReadToEnd());
    }

    public static PictureFiles Make(string id, Drawing d, Random r, Underlays underlays)
    {
        var model = d.Model;
        var (rm, _) = model.BuildRenderModel();
        var ext = DocumentExtents.SchematicBox(model, rm) ?? throw new InvalidOperationException("nothing to draw");

        // ── the view ─────────────────────────────────────────────────────────
        double dpr = r.NextDouble() < 0.4 ? 2 : 1;
        double shrink = r.NextDouble() < 0.5 ? 1 : 0.5 + r.NextDouble() * 0.45;
        double bodyPx = Math.Exp(Math.Log(12) + r.NextDouble() * (Math.Log(300) - Math.Log(12)));
        double zoom = bodyPx / (ResistorBody * dpr * shrink);

        double extW = ext.X1 - ext.X0, extH = ext.Y1 - ext.Y0;
        int w = r.Next(480, 1401), h = r.Next(360, 1001);
        if (r.NextDouble() < 0.5)
        {
            w = Math.Clamp((int)(extW * zoom) + r.Next(16, 240), 160, w);
            h = Math.Clamp((int)(extH * zoom) + r.Next(16, 200), 120, h);
        }
        w = Math.Min(w, (int)(2800 / dpr));
        h = Math.Min(h, (int)(2000 / dpr));
        double ww = w / zoom, wh = h / zoom;
        // A window smaller than the drawing is centred near a part, so a close-up shows a part rather than the empty
        // canvas between two; a larger one holds the whole drawing somewhere inside it.
        var anchor = rm.Components.Count > 0 ? rm.Components[r.Next(rm.Components.Count)] : null;
        double ax = anchor?.X ?? (ext.X0 + ext.X1) / 2, ay = anchor?.Y ?? (ext.Y0 + ext.Y1) / 2;
        double panX = extW <= ww ? ext.X0 - r.NextDouble() * (ww - extW)
                    : Math.Clamp(ax - ww * (0.2 + 0.6 * r.NextDouble()), ext.X0 - ww * 0.1, ext.X1 - ww * 0.9);
        double panY = extH <= wh ? ext.Y0 - r.NextDouble() * (wh - extH)
                    : Math.Clamp(ay - wh * (0.2 + 0.6 * r.NextDouble()), ext.Y0 - wh * 0.1, ext.Y1 - wh * 0.9);
        if (r.NextDouble() < 0.2)
        {
            // A crop that cuts parts at the edge even when the drawing would fit.
            panX += (r.NextDouble() - 0.5) * 0.5 * Math.Min(ww, extW);
            panY += (r.NextDouble() - 0.5) * 0.5 * Math.Min(wh, extH);
        }

        var variant = r.NextDouble() < 0.6 ? ColorVariant.Light : ColorVariant.Dark;
        var theme = SchematicRenderTheme.FromTheme(Shipped, variant);
        bool grid = r.NextDouble() < 0.6;

        SchematicOverlay? overlay = null;
        if (r.NextDouble() < 0.08)
        {
            var parts = rm.Components.Where(c => c.Symbol != SymbolKind.Ground).ToList();
            var chosen = parts.OrderBy(_ => r.Next()).Take(r.Next(1, 3)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var wires = rm.Wires.Count > 0 && r.NextDouble() < 0.4 ? new HashSet<string>([rm.Wires[r.Next(rm.Wires.Count)].Id]) : [];
            overlay = new SchematicOverlay { SelectedComponentIds = chosen, SelectedWireIds = wires };
        }

        var main = rm;
        if (r.NextDouble() < 0.05)
        {
            string path = underlays.Pick(r);
            double bw = Math.Max(400, extW * (0.3 + r.NextDouble() * 0.8)), bh = bw * (0.5 + r.NextDouble() * 0.5);
            main = Clone(rm, bitmaps: [new SchematicBitmap("underlay", path, ext.X0 + r.NextDouble() * Math.Max(1, extW - bw),
                ext.Y0 + r.NextDouble() * Math.Max(1, extH - bh), bw, bh, 0.25 + r.NextDouble() * 0.75)]);
        }

        // ── the passes ───────────────────────────────────────────────────────
        int pw = (int)Math.Round(w * dpr), ph = (int)Math.Round(h * dpr);
        SKBitmap Draw(SchematicModel m, SchematicOverlay? o, bool withGrid) =>
            Render(pw, ph, dpr, (w, h), m, panX, panY, zoom, theme, o, withGrid);

        using var picture = Draw(main, overlay, grid);
        using var wirePass = Draw(WiresOnly(rm), null, false);
        using var symbolPass = Draw(SymbolsOnly(rm), null, false);
        using var textPass = Draw(TextsOnly(rm), null, false);
        byte[] wireMask = Ink(wirePass, theme.Background);
        byte[] symbolMask = Ink(symbolPass, theme.Background);
        byte[] textMask = Ink(textPass, theme.Background);
        byte[] annotationMask = new byte[pw * ph];
        if (overlay is not null)
        {
            using var without = Draw(main, null, grid);
            Diff(picture, without, annotationMask);
        }

        var placedTexts = SchematicRenderer.MeasureTexts((w, h), main, null, panX, panY, zoom, theme, overlay);

        // ── screenshot conditions ────────────────────────────────────────────
        var shot = Screenshot.Take(picture, [wireMask, symbolMask, textMask, annotationMask], dpr, shrink, variant, r);

        // canvas pixels → final pixels
        Px Dev(double dx, double dy) => new((dx + shot.OffsetX) * shot.ScaleX, (dy + shot.OffsetY) * shot.ScaleY);
        var map = new ViewMap(
            World: (wx, wy) => Dev((wx - panX) * zoom * dpr, (wy - panY) * zoom * dpr),
            Logical: (lx, ly) => Dev(lx * dpr, ly * dpr),
            Canvas: [shot.OffsetX * shot.ScaleX, shot.OffsetY * shot.ScaleY, (shot.OffsetX + pw) * shot.ScaleX, (shot.OffsetY + ph) * shot.ScaleY],
            Pen: Math.Max(1.0, zoom * 4) * dpr * shot.ScaleX);

        string style = variant == ColorVariant.Light ? "crf-default-light" : "crf-default-dark";
        var label = Labels.Build(id + shot.Extension, d, rm, placedTexts, map, shot, style);

        var masks = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [id + ".wire.png"] = shot.Masks[0], [id + ".symbol.png"] = shot.Masks[1],
            [id + ".text.png"] = shot.Masks[2], [id + ".annotation.png"] = shot.Masks[3],
        };
        return new PictureFiles(id, id + shot.Extension, shot.Image, masks, label.Label, Netlists.Text(d.Truth), label.Classes)
        {
            SymbolMask = shot.RawMasks[1],
            Drawing = d,
            Conditions = $"{d.Mode.ToString().ToLowerInvariant()},{style},{(grid ? "grid" : "nogrid")},{shot.Conditions}",
        };
    }

    // ── rendering ────────────────────────────────────────────────────────────

    private static SKBitmap Render(int pw, int ph, double dpr, (double W, double H) logical, SchematicModel m,
                                   double panX, double panY, double zoom, SchematicRenderTheme theme,
                                   SchematicOverlay? overlay, bool grid)
    {
        var bmp = new SKBitmap(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Scale((float)dpr);
        SchematicRenderer.Draw(canvas, logical, m, null, panX, panY, zoom, theme, overlay,
            useTransparentBackground: false, excludeGrid: !grid);
        canvas.Flush();
        return bmp;
    }

    /// <summary>A pixel is ink when any channel is far from the background — pixel-exact for a pass drawn on the
    /// background alone, anti-aliased fringe included down to a fifth of full contrast.</summary>
    private static byte[] Ink(SKBitmap bmp, SKColor bg)
    {
        var px = bmp.GetPixelSpan();
        var mask = new byte[bmp.Width * bmp.Height];
        for (int i = 0, k = 0; k < mask.Length; i += 4, k++)
        {
            int d = Math.Max(Math.Abs(px[i] - bg.Red), Math.Max(Math.Abs(px[i + 1] - bg.Green), Math.Abs(px[i + 2] - bg.Blue)));
            mask[k] = d > 48 ? (byte)255 : (byte)0;
        }
        return mask;
    }

    private static void Diff(SKBitmap a, SKBitmap b, byte[] into)
    {
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        for (int i = 0, k = 0; k < into.Length; i += 4, k++)
        {
            int d = Math.Max(Math.Abs(pa[i] - pb[i]), Math.Max(Math.Abs(pa[i + 1] - pb[i + 1]), Math.Abs(pa[i + 2] - pb[i + 2])));
            if (d > 24) into[k] = 255;
        }
    }

    // ── filtered copies of the model ─────────────────────────────────────────

    private static SchematicModel Clone(SchematicModel m, IReadOnlyList<SchematicBitmap>? bitmaps = null) => new()
    {
        Components = m.Components, Wires = m.Wires, ConnectionDots = m.ConnectionDots, NetLabels = m.NetLabels,
        Bitmaps = bitmaps ?? m.Bitmaps, GridSize = m.GridSize,
        BbMinX = m.BbMinX, BbMinY = m.BbMinY, BbMaxX = m.BbMaxX, BbMaxY = m.BbMaxY,
    };

    private static SchematicModel WiresOnly(SchematicModel m) => new()
    {
        Wires = m.Wires, ConnectionDots = m.ConnectionDots, GridSize = m.GridSize,
        BbMinX = m.BbMinX, BbMinY = m.BbMinY, BbMaxX = m.BbMaxX, BbMaxY = m.BbMaxY,
    };

    private static SchematicModel SymbolsOnly(SchematicModel m) => new()
    {
        Components = [.. m.Components.Select(c => Copy(c, labels: false, art: Art.Drawing))], GridSize = m.GridSize,
        BbMinX = m.BbMinX, BbMinY = m.BbMinY, BbMaxX = m.BbMaxX, BbMaxY = m.BbMaxY,
    };

    private static SchematicModel TextsOnly(SchematicModel m) => new()
    {
        Components = [.. m.Components.Select(c => Copy(c, labels: true, art: Art.Text))], NetLabels = m.NetLabels,
        GridSize = m.GridSize, BbMinX = m.BbMinX, BbMinY = m.BbMinY, BbMaxX = m.BbMaxX, BbMaxY = m.BbMaxY,
    };

    private enum Art { Drawing, Text }

    /// <summary>A polarity mark is drawn as text but is part of the glyph — another editor draws it as strokes.</summary>
    internal static bool IsGlyphMark(string text) => text is "+" or "−" or "-";

    /// <summary>
    /// A copy of <paramref name="c"/> drawing only part of itself: its glyph's drawing (polarity marks included, port
    /// markers kept, pin names dropped) or its words (labels, and its artwork's text but the polarity marks). The glyph
    /// is handed over as the instance's own symbol, the path the renderer takes for a per-instance glyph, so both passes
    /// draw each primitive exactly as the full picture drew it.
    /// </summary>
    private static SchematicComponent Copy(SchematicComponent c, bool labels, Art art)
    {
        var symbol = c.InstanceSymbol ?? BuiltInSymbols.Primitives(c.Symbol, c.Ports.Count);
        var prims = symbol.Primitives.Where(p => p is TextPrimitive t
            ? (art == Art.Text) != IsGlyphMark(t.Content)
            : art == Art.Drawing).ToList();
        return new SchematicComponent
        {
            Id = c.Id, InstanceName = c.InstanceName, Symbol = c.Symbol, X = c.X, Y = c.Y,
            Rotation = c.Rotation, MirrorX = c.MirrorX, DisableState = c.DisableState,
            Ports = art == Art.Drawing ? [.. c.Ports.Select(p => p with { ShowName = false })] : [],
            Labels = labels ? c.Labels : [], LabelOffsets = c.LabelOffsets,
            BbMinX = c.BbMinX, BbMinY = c.BbMinY, BbMaxX = c.BbMaxX, BbMaxY = c.BbMaxY,
            GlyphBbMinX = c.GlyphBbMinX, GlyphBbMinY = c.GlyphBbMinY, GlyphBbMaxX = c.GlyphBbMaxX, GlyphBbMaxY = c.GlyphBbMaxY,
            FullBbMinX = c.FullBbMinX, FullBbMinY = c.FullBbMinY, FullBbMaxX = c.FullBbMaxX, FullBbMaxY = c.FullBbMaxY,
            InstanceSymbol = new Symbol(prims, symbol.Pins, symbol.PortCount),
        };
    }
}
