using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace CircuitRF.Ui.Controls;

public enum ImageMakeGlyphKind { Layout, Touchstone }

/// <summary>
/// The Create … from Image dialog's two glyphs no icon set has (brief-ptu-2 R-ptu2-2): a LAYOUT — two pads joined by
/// a routed trace — and a TOUCHSTONE file — an <c>S</c> over a 2 × 2 matrix grid. Drawn in the style of
/// <see cref="DataDisplay.Controls.PlotTypeGlyphControl"/> and themed the same way: one <see cref="Stroke"/> brush,
/// set from a style so a checked toggle can recolour it. Geometry is in a 16 × 16 design box, scaled to the bounds.
/// </summary>
public sealed class ImageMakeGlyphControl : Control
{
    public static readonly StyledProperty<ImageMakeGlyphKind> KindProperty =
        AvaloniaProperty.Register<ImageMakeGlyphControl, ImageMakeGlyphKind>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<ImageMakeGlyphControl, IBrush?>(nameof(Stroke));

    static ImageMakeGlyphControl()
    {
        AffectsRender<ImageMakeGlyphControl>(KindProperty, StrokeProperty);
    }

    public ImageMakeGlyphKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;

        double s = Math.Min(b.Width, b.Height) / 16.0;
        double ox = (b.Width - 16 * s) / 2, oy = (b.Height - 16 * s) / 2;
        var brush = Stroke ?? new SolidColorBrush(Color.FromArgb(180, 128, 128, 128));

        using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy)))
        {
            if (Kind == ImageMakeGlyphKind.Layout) DrawLayout(context, brush);
            else DrawTouchstone(context, brush);
        }
    }

    // ── Layout ─────────────────────────────────────────────────────────────
    // Two square pads, bottom-left and top-right, joined by an L-routed trace: copper, not lines.
    private static void DrawLayout(DrawingContext ctx, IBrush brush)
    {
        ctx.DrawRectangle(brush, null, new Rect(1, 10, 5, 5));          // pad 1
        ctx.DrawRectangle(brush, null, new Rect(10, 1, 5, 5));          // pad 2
        ctx.DrawRectangle(brush, null, new Rect(6, 11.5, 7.5, 2));      // trace, along
        ctx.DrawRectangle(brush, null, new Rect(11.5, 6, 2, 7.5));      // trace, up
    }

    // ── Touchstone ─────────────────────────────────────────────────────────
    // A bold S above a 2 × 2 grid: the scattering matrix the file holds.
    private static void DrawTouchstone(DrawingContext ctx, IBrush brush)
    {
        var text = new FormattedText("S", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 8.5, brush);
        ctx.DrawText(text, new Point(8 - text.Width / 2, -1.5));

        var pen = new Pen(brush, 1.0);
        var grid = new Rect(3.5, 7.5, 9, 8);
        ctx.DrawRectangle(null, pen, grid);
        ctx.DrawLine(pen, new Point(grid.Center.X, grid.Top), new Point(grid.Center.X, grid.Bottom));
        ctx.DrawLine(pen, new Point(grid.Left, grid.Center.Y), new Point(grid.Right, grid.Center.Y));
    }
}
