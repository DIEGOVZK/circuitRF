// brief-img-13-board-photos.md §4 — four picked corners put a photographed board square (R-im13-2).

using System;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition.Image;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class PerspectiveTests(ITestOutputHelper output)
{
    /// <summary>The photo seen through a known perspective, rectified on its four corners: each of the board's four
    /// edges, found on the rectified picture where the mask meets the bench, lies within 1 px of where the rectangle
    /// says it is.</summary>
    [Fact]
    public void FourPickedCorners_RecoverTheBoard_ToWithinAPixelAtItsEdges()
    {
        var reading = BoardPhoto.Read(BoardPhotoPictures.Source(), BoardPhotoPictures.Corners());
        Assert.True(reading.Ok, reading.Refusal);
        var img = reading.Rectified!.Raster;
        double pxPerMm = 1e-3 / reading.MetresPerPixel;

        double worst = 0;
        void Edge(string name, double expected, double measured)
        {
            output.WriteLine($"{name}: expected {expected:0.00} px, found {measured:0.00} px");
            worst = Math.Max(worst, Math.Abs(measured - expected));
        }
        // Rows across the left and right edges, columns across the top and bottom, away from the copper.
        foreach (double yMm in new[] { 8.0, 32.0 })
        {
            double y = reading.RectangleToPixel(0, yMm * 1e-3).Y;
            Edge($"left at {yMm} mm", reading.Origin.X, Crossing(img, reading.Origin.X, y, 1, 0, 2 * pxPerMm));
            double right = reading.RectangleToPixel(BoardPhotoPictures.WidthMm * 1e-3, 0).X;
            Edge($"right at {yMm} mm", right, Crossing(img, right, y, 1, 0, 2 * pxPerMm));
        }
        foreach (double xMm in new[] { 15.0, 45.0 })
        {
            double x = reading.RectangleToPixel(xMm * 1e-3, 0).X;
            Edge($"top at {xMm} mm", reading.Origin.Y, Crossing(img, x, reading.Origin.Y, 0, 1, 2 * pxPerMm));
            double bottom = reading.RectangleToPixel(0, BoardPhotoPictures.HeightMm * 1e-3).Y;
            Edge($"bottom at {xMm} mm", bottom, Crossing(img, x, bottom, 0, 1, 2 * pxPerMm));
        }
        Assert.InRange(worst, 0, 1.0);
    }

    /// <summary>Where the luminance along a line through (x, y) in direction (dx, dy) crosses halfway between its two
    /// ends, <paramref name="span"/> pixels either side — linearly interpolated between samples.</summary>
    private static double Crossing(RasterImage img, double x, double y, int dx, int dy, double span)
    {
        double Lum(double t)
        {
            int px = (int)Math.Floor(x + t * dx), py = (int)Math.Floor(y + t * dy);
            int i = (py * img.Width + px) * 4;
            return 0.2126 * img.Rgba[i] + 0.7152 * img.Rgba[i + 1] + 0.0722 * img.Rgba[i + 2];
        }
        int n = (int)Math.Ceiling(span);
        double lo = 0, hi = 0;
        for (int k = 0; k < 3; k++) { lo += Lum(-n + k); hi += Lum(n - k); }
        double mid = (lo + hi) / 6;
        for (int t = -n; t < n; t++)
        {
            double a = Lum(t), b = Lum(t + 1);
            if ((a - mid) * (b - mid) <= 0 && a != b)
            {
                // Sample t covers [t, t + 1) from the pixel edge nearest; its value is at its centre.
                double along = Math.Floor(dx != 0 ? x : y) + t + 0.5 + (mid - a) / (b - a);
                return along;
            }
        }
        return double.NaN;
    }
}
