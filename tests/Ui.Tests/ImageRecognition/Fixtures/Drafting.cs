// The pen the IMF-1 style pictures are drawn with (brief-imf-1-corpus-and-scorer.md R-imf1-1, R-imf1-2): schematic
// symbols in the conventions datasheets and textbooks draw, in SkiaSharp, every coordinate in the picture's 1× pixels so
// a 2× picture is the same drawing with the canvas scaled — pen, type and spacing together.
//
// A two-pin symbol is drawn between its two pin points, axis-aligned or not: its leads to the body, then the body in a
// frame whose x runs from the first pin to the second.

using System;
using System.IO;
using CircuitRF.Ui.Tests.Imaging;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

internal sealed class Drafting(SKCanvas canvas, float pen, SKColor ink)
{
    public SKCanvas Canvas => canvas;
    public float Pen { get; set; } = pen;
    public SKColor Ink { get; set; } = ink;
    public SKColor TextInk { get; set; } = ink;

    // ── faces ───────────────────────────────────────────────────────────────────────────────────────────────────

    public static SKTypeface Sans { get; } = Shipped("DejaVuSans.ttf");
    public static SKTypeface SansBold { get; } = Shipped("DejaVuSans-Bold.ttf");
    /// <summary>A test asset only (Fixtures/fonts, overview O4): circuitRF ships no serif face.</summary>
    public static SKTypeface Serif { get; } = Asset("DejaVuSerif.ttf");
    public static SKTypeface SerifItalic { get; } = Asset("DejaVuSerif-Italic.ttf");

    private static SKTypeface Shipped(string file) =>
        SKTypeface.FromFile(Path.Combine(FixturePaths.Require("src/Render/Assets/Fonts"), file)) ?? throw new FileNotFoundException(file);

    private static SKTypeface Asset(string file) =>
        SKTypeface.FromFile(Path.Combine(FixturePaths.Require("tests/Ui.Tests/ImageRecognition/Fixtures/fonts"), file))
        ?? throw new FileNotFoundException(file);

    // ── paint ───────────────────────────────────────────────────────────────────────────────────────────────────

    private SKPaint Stroke(float? width = null) => new()
    {
        Color = Ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width ?? Pen,
        StrokeCap = SKStrokeCap.Square, StrokeJoin = SKStrokeJoin.Miter,
    };

    private SKPaint Solid(SKColor? colour = null) => Pictures.Fill(colour ?? Ink);

    // ── wire ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A polyline through x0, y0, x1, y1, …</summary>
    public void Wire(params float[] xy)
    {
        using var path = new SKPath();
        path.MoveTo(xy[0], xy[1]);
        for (int k = 2; k < xy.Length; k += 2) path.LineTo(xy[k], xy[k + 1]);
        using var paint = Stroke();
        canvas.DrawPath(path, paint);
    }

    public void Line(float x0, float y0, float x1, float y1, float? width = null)
    {
        using var paint = Stroke(width);
        paint.StrokeCap = SKStrokeCap.Butt;
        canvas.DrawLine(x0, y0, x1, y1, paint);
    }

    /// <summary>A junction: a filled dot.</summary>
    public void Dot(float x, float y, float r)
    {
        using var paint = Solid();
        canvas.DrawCircle(x, y, r, paint);
    }

    /// <summary>A junction drawn as a filled diamond, <paramref name="r"/> from centre to corner.</summary>
    public void Diamond(float x, float y, float r)
    {
        using var path = new SKPath();
        path.MoveTo(x - r, y);
        path.LineTo(x, y - r);
        path.LineTo(x + r, y);
        path.LineTo(x, y + r);
        path.Close();
        using var paint = Solid();
        canvas.DrawPath(path, paint);
    }

    /// <summary>An open circle — a terminal, a pin marker — filled with <paramref name="fill"/> when given.</summary>
    public void Circle(float x, float y, float r, SKColor? fill = null, float? width = null)
    {
        if (fill is { } f)
        {
            using var solid = Solid(f);
            canvas.DrawCircle(x, y, r, solid);
        }
        using var paint = Stroke(width);
        canvas.DrawCircle(x, y, r, paint);
    }

    public void Rect(SKRect r, SKColor? fill = null)
    {
        if (fill is { } f)
        {
            using var solid = Solid(f);
            canvas.DrawRect(r, solid);
        }
        using var paint = Stroke();
        paint.StrokeCap = SKStrokeCap.Butt;
        canvas.DrawRect(r, paint);
    }

    /// <summary>A rectangle filled top to bottom from <paramref name="top"/> to <paramref name="bottom"/>, outlined.</summary>
    public void GradientRect(SKRect r, SKColor top, SKColor bottom)
    {
        using var shader = SKShader.CreateLinearGradient(new SKPoint(r.Left, r.Top), new SKPoint(r.Left, r.Bottom), [top, bottom], SKShaderTileMode.Clamp);
        using var fill = new SKPaint { Shader = shader, IsAntialias = true };
        canvas.DrawRect(r, fill);
        Rect(r);
    }

    /// <summary>A closed polygon through x0, y0, x1, y1, …, filled with <paramref name="fill"/> when given.</summary>
    public void Polygon(SKColor? fill, params float[] xy)
    {
        using var path = new SKPath();
        path.MoveTo(xy[0], xy[1]);
        for (int k = 2; k < xy.Length; k += 2) path.LineTo(xy[k], xy[k + 1]);
        path.Close();
        if (fill is { } f)
        {
            using var solid = Solid(f);
            canvas.DrawPath(path, solid);
        }
        using var paint = Stroke();
        canvas.DrawPath(path, paint);
    }

    /// <summary>A horizontal wire from <paramref name="x0"/> to <paramref name="x1"/> hopping over a crossing wire at
    /// <paramref name="hx"/> with a semicircle of radius <paramref name="r"/>.</summary>
    public void Hop(float x0, float x1, float y, float hx, float r)
    {
        Line(x0, y, hx - r, y);
        using var path = new SKPath();
        path.ArcTo(SKRect.Create(hx - r, y - r, 2 * r, 2 * r), 180, 180, true);
        using var paint = Stroke();
        canvas.DrawPath(path, paint);
        Line(hx + r, y, x1, y);
    }

    /// <summary>A dashed line: <paramref name="on"/> drawn, <paramref name="off"/> left out, from the first end.</summary>
    public void Dashed(float x0, float y0, float x1, float y1, float on, float off)
    {
        float len = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)), ux = (x1 - x0) / len, uy = (y1 - y0) / len;
        for (float s = 0; s < len; s += on + off)
        {
            float e = MathF.Min(s + on, len);
            Line(x0 + ux * s, y0 + uy * s, x0 + ux * e, y0 + uy * e);
        }
    }

    public void DashedRect(SKRect r, float on, float off)
    {
        Dashed(r.Left, r.Top, r.Right, r.Top, on, off);
        Dashed(r.Right, r.Top, r.Right, r.Bottom, on, off);
        Dashed(r.Right, r.Bottom, r.Left, r.Bottom, on, off);
        Dashed(r.Left, r.Bottom, r.Left, r.Top, on, off);
    }

    /// <summary>A line with a filled arrowhead at its second end — and at its first when <paramref name="both"/>.</summary>
    public void Arrow(float x0, float y0, float x1, float y1, float head, bool both = false)
    {
        float len = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)), ux = (x1 - x0) / len, uy = (y1 - y0) / len;
        Line(both ? x0 + ux * head : x0, both ? y0 + uy * head : y0, x1 - ux * head, y1 - uy * head);
        Head(x1, y1, ux, uy, head);
        if (both) Head(x0, y0, -ux, -uy, head);
    }

    private void Head(float x, float y, float ux, float uy, float head)
    {
        float w = head * 0.45f;
        Polygon(Ink, x, y, x - ux * head - uy * w, y - uy * head + ux * w, x - ux * head + uy * w, y - uy * head - ux * w);
    }

    // ── ground ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A ground hanging from (<paramref name="x"/>, <paramref name="y"/>): three bars, each shorter.</summary>
    public void Ground(float x, float y, float width, float pitch)
    {
        for (int k = 0; k < 3; k++)
        {
            float half = width / 2 * (1 - 0.3f * k);
            Line(x - half, y + k * pitch, x + half, y + k * pitch);
        }
    }

    // ── two-pin symbols ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The leads from both pins to a body <paramref name="body"/> long at the middle, then
    /// <paramref name="draw"/> in the body's frame: x along the part from −body/2 to +body/2, y across it.</summary>
    private void Along(float x0, float y0, float x1, float y1, float body, Action<float> draw)
    {
        float len = MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)), ux = (x1 - x0) / len, uy = (y1 - y0) / len;
        float cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, half = body / 2;
        if (len > body)
        {
            Line(x0, y0, cx - ux * half, cy - uy * half);
            Line(cx + ux * half, cy + uy * half, x1, y1);
        }
        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.RotateRadians(MathF.Atan2(uy, ux));
        draw(half);
        canvas.Restore();
    }

    /// <summary>A zig-zag resistor: <paramref name="peaks"/> peaks <paramref name="amplitude"/> either side.</summary>
    public void Resistor(float x0, float y0, float x1, float y1, float body, float amplitude, int peaks = 6, float? width = null) =>
        Along(x0, y0, x1, y1, body, half =>
        {
            using var path = new SKPath();
            path.MoveTo(-half, 0);
            for (int k = 0; k < peaks; k++) path.LineTo(-half + (k + 0.5f) * 2 * half / peaks, k % 2 == 0 ? -amplitude : amplitude);
            path.LineTo(half, 0);
            using var paint = Stroke(width);
            paint.StrokeJoin = SKStrokeJoin.Round;
            canvas.DrawPath(path, paint);
        });

    /// <summary>A zig-zag resistor with an arrow drawn diagonally through it: a variable resistor.</summary>
    public void VariableResistor(float x0, float y0, float x1, float y1, float body, float amplitude)
    {
        Resistor(x0, y0, x1, y1, body, amplitude);
        Along(x0, y0, x1, y1, body, half => Arrow(-half * 0.8f, amplitude * 2.4f, half * 0.8f, -amplitude * 2.4f, amplitude * 1.4f));
    }

    /// <summary>An IEC box <paramref name="body"/> long and <paramref name="across"/> wide; filled for an inductor.</summary>
    public void Box(float x0, float y0, float x1, float y1, float body, float across, bool filled = false) =>
        Along(x0, y0, x1, y1, body, half => Rect(new SKRect(-half, -across / 2, half, across / 2), filled ? Ink : null));

    /// <summary>A capacitor: two plates <paramref name="plate"/> long, <paramref name="gap"/> apart, stroked
    /// <paramref name="plateWidth"/>; the second plate curved when <paramref name="curved"/>.</summary>
    public void Capacitor(float x0, float y0, float x1, float y1, float gap, float plate, float? plateWidth = null, bool curved = false) =>
        Along(x0, y0, x1, y1, gap, half =>
        {
            using var paint = Stroke(plateWidth);
            paint.StrokeCap = SKStrokeCap.Butt;
            canvas.DrawLine(-half, -plate / 2, -half, plate / 2, paint);
            if (!curved)
            {
                canvas.DrawLine(half, -plate / 2, half, plate / 2, paint);
                return;
            }
            float bow = plate * 0.22f;
            using var arc = new SKPath();
            arc.MoveTo(half + bow, -plate / 2);
            arc.QuadTo(half - bow, 0, half + bow, plate / 2);
            canvas.DrawPath(arc, paint);
        });

    /// <summary>A coil of <paramref name="turns"/> humps on one side; overlapping loops when <paramref name="looped"/>.</summary>
    public void Coil(float x0, float y0, float x1, float y1, float body, int turns, bool looped = false, float? width = null) =>
        Along(x0, y0, x1, y1, body, half =>
        {
            using var path = new SKPath();
            float pitch = 2 * half / turns;
            if (!looped)
            {
                path.MoveTo(-half, 0);
                for (int k = 0; k < turns; k++)
                    path.ArcTo(SKRect.Create(-half + k * pitch, -pitch / 2, pitch, pitch), 180, 180, false);
            }
            else
            {
                // A prolate cycloid: the pen runs on round loops faster than it advances, so each loop crosses itself.
                float r = pitch * 0.55f;
                int n = turns * 48;
                for (int k = 0; k <= n; k++)
                {
                    float t = 2 * MathF.PI * turns * k / n;
                    float x = -half + pitch * t / (2 * MathF.PI) - r * MathF.Sin(t), y = -r * (1 - MathF.Cos(t));
                    if (k == 0) path.MoveTo(x, y); else path.LineTo(x, y);
                }
            }
            using var paint = Stroke(width);
            paint.StrokeCap = SKStrokeCap.Butt;
            canvas.DrawPath(path, paint);
        });

    /// <summary>A filled diode, anode at the first pin, cathode (the bar) at the second.</summary>
    public void Diode(float x0, float y0, float x1, float y1, float body) =>
        Along(x0, y0, x1, y1, body, half =>
        {
            Polygon(Ink, -half, -half * 0.85f, -half, half * 0.85f, half, 0);
            Line(half, -half * 0.85f, half, half * 0.85f);
        });

    // ── text ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>DejaVu's cap height is 0.729 em in every face used here.</summary>
    private static SKFont Font(SKTypeface face, float cap) => new(face, cap / 0.729f);

    public float Width(SKTypeface face, string text, float cap)
    {
        using var font = Font(face, cap);
        return font.MeasureText(text);
    }

    /// <summary><paramref name="text"/> with its advance starting at <paramref name="left"/>.</summary>
    public void Text(SKTypeface face, string text, float left, float baseline, float cap)
    {
        using var font = Font(face, cap);
        using var paint = Solid(TextInk);
        canvas.DrawText(text, left, baseline, SKTextAlign.Left, font, paint);
    }

    public void TextRight(SKTypeface face, string text, float right, float baseline, float cap) =>
        Text(face, text, right - Width(face, text, cap), baseline, cap);

    public void TextCentred(SKTypeface face, string text, float centre, float baseline, float cap) =>
        Text(face, text, centre - Width(face, text, cap) / 2, baseline, cap);

    /// <summary><paramref name="main"/> with <paramref name="sub"/> set as a subscript after it, then
    /// <paramref name="after"/> (a value, a space ahead of it) on the baseline; returns the right edge.</summary>
    public float Subscripted(SKTypeface face, string main, string sub, float left, float baseline, float cap, string? after = null)
    {
        Text(face, main, left, baseline, cap);
        float x = left + Width(face, main, cap);
        Text(face, sub, x, baseline + cap * 0.3f, cap * 0.65f);
        x += Width(face, sub, cap * 0.65f);
        if (after is null) return x;
        x += Width(face, " ", cap);
        Text(face, after, x, baseline, cap);
        return x + Width(face, after, cap);
    }
}
