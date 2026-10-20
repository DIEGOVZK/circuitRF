// brief-dfr22-3 R-dfr22-3-5 — the ratsnest over the artwork: a thin straight line between pins that share a net and
// are not yet joined by copper, with a small dot at each end. Device-pixel widths, constant at any zoom — a pointer
// to where a connection is missing, never a drawing of copper.
//
// R-em-15's contract, as for every overlay here: never layer geometry, never counted in LayoutFrameCounters, never
// reachable by an exporter (every export path passes Overlay = null).

using CircuitRF.Design.Layout.Extraction;
using SkiaSharp;

namespace CircuitRF.Render;

public static partial class LayoutRenderer
{
    private const float RatsnestLineDevicePixels = 1.25f;
    private const float RatsnestDotDevicePixels  = 2.5f;

    /// <summary>A mid-tone that reads on both the light and the dark canvas, and is no layer's colour.</summary>
    private static readonly SKColor RatsnestInk = new(0x1e, 0x9b, 0xd7, 0xe0);

    internal static void DrawRatsnest(SKCanvas canvas, IReadOnlyList<RatsnestLine> lines, PathSpace ps, double scaleUm)
    {
        using var line = new SKPaint
        {
            IsStroke = true, IsAntialias = true, Color = RatsnestInk,
            StrokeWidth = DevicePixelsToPathSpace(scaleUm, RatsnestLineDevicePixels),
        };
        using var dot = new SKPaint { IsStroke = false, IsAntialias = true, Color = RatsnestInk };
        float r = DevicePixelsToPathSpace(scaleUm, RatsnestDotDevicePixels);
        foreach (var l in lines)
        {
            canvas.DrawLine(ps.X(l.X0), ps.Y(l.Y0), ps.X(l.X1), ps.Y(l.Y1), line);
            canvas.DrawCircle(ps.X(l.X0), ps.Y(l.Y0), r, dot);
            canvas.DrawCircle(ps.X(l.X1), ps.Y(l.Y1), r, dot);
        }
    }
}
