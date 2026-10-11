// Symbols for the IM-8 gates, each drawn by the test in the convention it is named for — never by src/Render, so the
// gate does not compare the renderer with itself. brief-img-8-schematic-symbols.md §4. Anti-aliased, 2 px strokes.
//
// Every drawing is made in its kind's built-in frame (a two-terminal part upright with pin 1 on top, a line across, a
// terminal's wire to the right, a ground's lead up), centred on the origin, with its wires out to ±100; the picture
// turns it about the centre, so 90° here is R90 there.

using System;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Schematic.Recognition;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Imaging;

internal static class SymbolPictures
{
    public const int Size = 240;

    /// <summary>A picture of <paramref name="draw"/> turned by <paramref name="deg"/> about the centre — and then, when
    /// asked, the whole picture mirrored left to right.</summary>
    public static RasterImage Picture(Action<SKCanvas, SKPaint> draw, float deg = 0, bool mirror = false) =>
        Pictures.Draw(Size, Size, c =>
        {
            c.Translate(Size / 2f, Size / 2f);
            if (mirror) c.Scale(-1, 1);
            c.RotateDegrees(deg);
            using var pen = SchematicPictures.Ink();
            draw(c, pen);
        });

    /// <summary>The symbols the picture's reading names.</summary>
    public static SchematicSymbolsRead Read(RasterImage img) => SchematicSymbols.Read(SchematicImageReading.Read(img));

    public static void Upright(SKCanvas c, SKPaint pen, float end)
    {
        c.DrawLine(0, -100, 0, -end, pen);
        c.DrawLine(0, end, 0, 100, pen);
    }

    public static void Across(SKCanvas c, SKPaint pen, float end)
    {
        c.DrawLine(-100, 0, -end, 0, pen);
        c.DrawLine(end, 0, 100, 0, pen);
    }

    /// <summary>A US resistor: a zig-zag of eight strokes.</summary>
    public static void UsResistor(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 24);
        using var p = new SKPath();
        p.MoveTo(0, -24);
        p.LineTo(6, -21);
        for (int k = 0; k < 7; k++) p.LineTo(k % 2 == 0 ? -6 : 6, -15 + 6 * k);
        p.LineTo(0, 24);
        c.DrawPath(p, pen);
    }

    /// <summary>An IEC resistor: a hollow rectangle.</summary>
    public static void IecResistor(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 24);
        c.DrawRect(-10, -24, 20, 48, pen);
    }

    /// <summary>A coil of four humps bulging to +x.</summary>
    public static void Coil(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 24);
        using var p = new SKPath();
        p.MoveTo(0, -24);
        for (int k = 0; k < 4; k++) p.ArcTo(new SKRect(-6, -24 + 12 * k, 6, -12 + 12 * k), 270, 180, false);
        c.DrawPath(p, pen);
    }

    /// <summary>An IEC inductor: a filled rectangle.</summary>
    public static void IecInductor(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 21);
        using var fill = Pictures.Fill(SKColors.Black);
        c.DrawRect(-7, -21, 14, 42, fill);
    }

    /// <summary>A capacitor of two flat plates.</summary>
    public static void FlatCapacitor(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 4);
        c.DrawLine(-14, -4, 14, -4, pen);
        c.DrawLine(-14, 4, 14, 4, pen);
    }

    /// <summary>A capacitor of a flat plate and a curved one bowing toward it.</summary>
    public static void CurvedCapacitor(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 4);
        c.DrawLine(-14, -4, 14, -4, pen);
        using var p = new SKPath();
        p.AddArc(new SKRect(-26, 4, 26, 56), 240, 60);
        c.DrawPath(p, pen);
    }

    /// <summary>A ground of three bars under its lead.</summary>
    public static void Ground(SKCanvas c, SKPaint pen)
    {
        c.DrawLine(0, -100, 0, -6, pen);
        c.DrawLine(-14, -6, 14, -6, pen);
        c.DrawLine(-9, 0, 9, 0, pen);
        c.DrawLine(-4, 6, 4, 6, pen);
    }

    /// <summary>A terminal: an open circle on the end of a wire coming from the right.</summary>
    public static void Terminal(SKCanvas c, SKPaint pen)
    {
        c.DrawCircle(0, 0, 8, pen);
        c.DrawLine(8, 0, 100, 0, pen);
    }

    /// <summary>A transmission line: a box with a line along it.</summary>
    public static void BoxLine(SKCanvas c, SKPaint pen)
    {
        Across(c, pen, 30);
        c.DrawRect(-30, -9, 60, 18, pen);
        c.DrawLine(-22, 0, 22, 0, pen);
    }

    /// <summary>Two parallel bars across the leads joined at their ends — a drawn rectangle, not a capacitor.</summary>
    public static void JoinedBars(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 4);
        c.DrawRect(-14, -4, 28, 8, pen);
    }

    /// <summary>An n-channel FET with no arrow: a gate wire from the left onto an unbroken channel bar standing out past
    /// its drain and source arms, the arms out to wires going up and down.</summary>
    public static void Fet(SKCanvas c, SKPaint pen)
    {
        c.DrawLine(-100, 0, -5, 0, pen);
        c.DrawLine(-5, -15, -5, 15, pen);
        c.DrawLine(-5, -6, 6, -6, pen);
        c.DrawLine(6, -6, 6, -100, pen);
        c.DrawLine(-5, 6, 6, 6, pen);
        c.DrawLine(6, 6, 6, 100, pen);
    }

    /// <summary>An irregular closed outline between two leads, no side of it near an axis — no symbol anyone draws.</summary>
    public static void Blob(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 12);
        using var p = new SKPath();
        p.MoveTo(0, -12);
        foreach (var (x, y) in new[] { (9, -8), (12, 2), (6, 10), (0, 12), (-8, 9), (-12, -1), (-7, -9) }) p.LineTo(x, y);
        p.Close();
        c.DrawPath(p, pen);
    }

    /// <summary>A diode, anode on top: a filled triangle pointing down at its bar.</summary>
    public static void Diode(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 9);
        using var fill = Pictures.Fill(SKColors.Black);
        using var tri = new SKPath();
        tri.MoveTo(-11, -9);
        tri.LineTo(11, -9);
        tri.LineTo(0, 9);
        tri.Close();
        c.DrawPath(tri, fill);
        c.DrawLine(-11, 9, 11, 9, pen);
    }

    // ── IMF-5: datasheet and textbook drawings (brief-imf-5-two-pin-and-device-symbols.md §4) ──────────────────────

    /// <summary>The pen a datasheet draws in at screen resolution.</summary>
    private const float Hairline = 1.2f;

    private static void ZigZag(SKCanvas c, SKPaint pen, float half, float amplitude, int peaks)
    {
        Upright(c, pen, half);
        using var p = new SKPath();
        p.MoveTo(0, -half);
        for (int k = 0; k < peaks; k++) p.LineTo(k % 2 == 0 ? -amplitude : amplitude, -half + (k + 0.5f) * 2 * half / peaks);
        p.LineTo(0, half);
        pen.StrokeJoin = SKStrokeJoin.Round;
        c.DrawPath(p, pen);
    }

    /// <summary>A capacitor of two hairline plates 24 long with a gap of 10 — a datasheet's, wide by IEC measure.</summary>
    public static void WideCapacitor(SKCanvas c, SKPaint pen)
    {
        pen.StrokeWidth = Hairline;
        Upright(c, pen, 5);
        c.DrawLine(-12, -5, 12, -5, pen);
        c.DrawLine(-12, 5, 12, 5, pen);
    }

    /// <summary>A hairline flat plate over a shallow curved one, the curved plate pin 2's — the polarised convention.</summary>
    public static void HairlineCurvedCapacitor(SKCanvas c, SKPaint pen)
    {
        pen.StrokeWidth = Hairline;
        Upright(c, pen, 4);
        c.DrawLine(-12, -4, 12, -4, pen);
        using var p = new SKPath();
        p.MoveTo(-12, 7);
        p.QuadTo(0, 1, 12, 7);
        c.DrawPath(p, pen);
    }

    /// <summary>A datasheet's filled diode, anode on top, its bar drawn in a hairline.</summary>
    public static void HairlineDiode(SKCanvas c, SKPaint pen)
    {
        pen.StrokeWidth = Hairline;
        Upright(c, pen, 7);
        using var fill = Pictures.Fill(SKColors.Black);
        using var tri = new SKPath();
        tri.MoveTo(-6, -7);
        tri.LineTo(6, -7);
        tri.LineTo(0, 7);
        tri.Close();
        c.DrawPath(tri, fill);
        c.DrawLine(-6, 7, 6, 7, pen);
    }

    /// <summary>A datasheet's hairline zig-zag of six peaks.</summary>
    public static void HairlineZigZag(SKCanvas c, SKPaint pen)
    {
        pen.StrokeWidth = Hairline;
        ZigZag(c, pen, 18, 5, 6);
    }

    /// <summary>The hairline zig-zag with an arrow drawn across it, lower left to upper right: a variable resistor.</summary>
    public static void VariableResistor(SKCanvas c, SKPaint pen)
    {
        HairlineZigZag(c, pen);
        c.DrawLine(-12, 14, 9, -11, pen);
        using var fill = Pictures.Fill(SKColors.Black);
        using var head = new SKPath();
        head.MoveTo(13, -16);
        head.LineTo(5, -12);
        head.LineTo(11, -7);
        head.Close();
        c.DrawPath(head, fill);
    }

    /// <summary>A textbook zig-zag drawn with an 8 px pen.</summary>
    public static void HeavyZigZag(SKCanvas c, SKPaint pen)
    {
        pen.StrokeWidth = 8;
        ZigZag(c, pen, 50, 18, 5);
    }

    /// <summary>A zig-zag drawn hatched: its stroke rendered as short ticks across the path, as a textbook figure's
    /// vector export draws a patterned line.</summary>
    public static void HatchedZigZag(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 30);
        using var hatched = pen.Clone();
        hatched.StrokeWidth = 4;
        hatched.StrokeCap = SKStrokeCap.Butt;
        hatched.PathEffect = SKPathEffect.CreateDash([1.3f, 1.7f], 0);
        using var p = new SKPath();
        p.MoveTo(0, -30);
        for (int k = 0; k < 6; k++) p.LineTo(k % 2 == 0 ? -9 : 9, -30 + (k + 0.5f) * 10);
        p.LineTo(0, 30);
        c.DrawPath(p, hatched);
    }

    /// <summary>A looped coil of four loops, each crossing itself, leaving its leads along their line.</summary>
    public static void LoopedCoil(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 30);
        using var p = new SKPath();
        float half = 30, pitch = 2 * half / 4, r = pitch * 0.55f;
        for (int k = 0; k <= 4 * 48; k++)
        {
            // A prolate cycloid from the top of one loop to the top of the last: there it runs along the leads' line.
            float t = MathF.PI + 2 * MathF.PI * 4 * k / (4 * 48);
            float along = -half + pitch * (t - MathF.PI) / (2 * MathF.PI) - r * MathF.Sin(t), off = r * (1 + MathF.Cos(t));
            if (k == 0) p.MoveTo(off, along); else p.LineTo(off, along);
        }
        c.DrawPath(p, pen);
    }

    /// <summary>A transformer: two coils of four humps facing each other across a core of two bars, a wire from each end
    /// of each coil out to the side.</summary>
    public static void Transformer(SKCanvas c, SKPaint pen)
    {
        foreach (int side in new[] { -1, 1 })
        {
            float x = side * 24;
            c.DrawLine(side * 100, -40, x, -40, pen);
            c.DrawLine(x, -40, x, -32, pen);
            c.DrawLine(side * 100, 40, x, 40, pen);
            c.DrawLine(x, 40, x, 32, pen);
            using var p = new SKPath();
            p.MoveTo(x, -32);
            for (int k = 0; k < 4; k++) p.ArcTo(new SKRect(x - 8, -32 + 16 * k, x + 8, -16 + 16 * k), 270, side * -180, false);
            c.DrawPath(p, pen);
            c.DrawLine(side * 4, -36, side * 4, 36, pen);
        }
    }

    /// <summary>An n-p-n BJT in its envelope circle: the base from the left, the collector up, the emitter down with the
    /// arrow pointing out of it.</summary>
    public static void NpnInCircle(SKCanvas c, SKPaint pen)
    {
        c.DrawCircle(0, 0, 30, pen);
        Bjt(c, pen, npn: true, lead: -30);
    }

    /// <summary>A p-n-p BJT in its envelope circle: the arrow on the emitter pointing into the base.</summary>
    public static void PnpInCircle(SKCanvas c, SKPaint pen)
    {
        c.DrawCircle(0, 0, 30, pen);
        Bjt(c, pen, npn: false, lead: -30);
    }

    private static void Bjt(SKCanvas c, SKPaint pen, bool npn, float lead)
    {
        c.DrawLine(-100, 0, -10, 0, pen);
        c.DrawLine(-10, -16, -10, 16, pen);
        c.DrawLine(-10, -7, 10, -20, pen);
        c.DrawLine(10, -20, 10, -100, pen);
        c.DrawLine(-10, 7, 10, 20, pen);
        c.DrawLine(10, 20, 10, 100, pen);
        // The arrow on the emitter arm, along it: its tip toward the emitter for an n-p-n, toward the base for a p-n-p.
        float ux = 20 / 23.85f, uy = 13 / 23.85f;
        var (tx, ty) = npn ? (6.5f, 17.7f) : (-4.5f, 10.3f);
        float bx = npn ? tx - 8 * ux : tx + 8 * ux, by = npn ? ty - 8 * uy : ty + 8 * uy;
        using var fill = Pictures.Fill(SKColors.Black);
        using var head = new SKPath();
        head.MoveTo(tx, ty);
        head.LineTo(bx - 4 * uy, by + 4 * ux);
        head.LineTo(bx + 4 * uy, by - 4 * ux);
        head.Close();
        c.DrawPath(head, fill);
        _ = lead;
    }

    private static void SourceCircle(SKCanvas c, SKPaint pen, bool bottomLead = true)
    {
        c.DrawLine(0, -100, 0, -20, pen);
        if (bottomLead) c.DrawLine(0, 20, 0, 100, pen);
        c.DrawCircle(0, 0, 20, pen);
    }

    /// <summary>An AC source: a circle with a sine across it.</summary>
    public static void AcSource(SKCanvas c, SKPaint pen)
    {
        SourceCircle(c, pen);
        using var p = new SKPath();
        for (int k = 0; k <= 24; k++)
        {
            float x = -11 + 22 * k / 24f, y = -5 * MathF.Sin(2 * MathF.PI * k / 24);
            if (k == 0) p.MoveTo(x, y); else p.LineTo(x, y);
        }
        c.DrawPath(p, pen);
    }

    /// <summary>A DC source: a circle with + by its top lead and − by its bottom one.</summary>
    public static void DcSource(SKCanvas c, SKPaint pen)
    {
        SourceCircle(c, pen);
        c.DrawLine(-4, -9, 4, -9, pen);
        c.DrawLine(0, -13, 0, -5, pen);
        c.DrawLine(-4, 9, 4, 9, pen);
    }

    /// <summary>A supply: a circle with a + in it on the end of one lead.</summary>
    public static void Supply(SKCanvas c, SKPaint pen)
    {
        SourceCircle(c, pen, bottomLead: false);
        c.DrawLine(-8, 0, 8, 0, pen);
        c.DrawLine(0, -8, 0, 8, pen);
    }

    /// <summary>A battery: a long plate (the positive, on top) and a short one.</summary>
    public static void Battery(SKCanvas c, SKPaint pen)
    {
        Upright(c, pen, 6);
        c.DrawLine(-22, -6, 22, -6, pen);
        c.DrawLine(-11, 6, 11, 6, pen);
    }
}
