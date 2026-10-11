// A polar chart, calibrated — brief-img-14-sparameter-plots-to-touchstone.md R-im14-4.
//
// A polar chart's centre is the origin and its angle labels give the zero direction and the sense of rotation; its ring
// labels give the radial scale — linear magnitude (0.2, 0.4 … 1) or dB (−30, −20, −10, 0). The ring labels are read
// where the rings cross the chart's horizontal axis; with none read, the outer ring is |S| = 1 on a linear scale, which
// is how an S-parameter polar chart is drawn, and the report says so.
//
// ANGLES: a label reading 90 above the centre means counter-clockwise from the right (the mathematician's sense, and
// nearly every chart's); one reading 90 below the centre means clockwise. Without angle labels, 0° is at the right and
// angles grow counter-clockwise, stated.

using System.Numerics;

namespace CircuitRF.Design.Imaging.Plots;

/// <summary>A polar chart's calibration (R-im14-4).</summary>
/// <param name="OuterValue">The outer ring's value — |S| on a linear chart, dB on a dB chart.</param>
/// <param name="InnerValue">The centre's value: 0 on a linear chart, the floor on a dB chart.</param>
/// <param name="Clockwise">Angles grow clockwise on the picture.</param>
/// <param name="ZeroDeg">The picture direction of 0°, degrees counter-clockwise from the right.</param>
/// <param name="Stated">The scale was read from ring labels, not assumed.</param>
public sealed record PolarCalibration(double Cx, double Cy, double Radius, bool Db, double OuterValue, double InnerValue,
                                      bool Clockwise, double ZeroDeg, bool Stated, bool AnglesStated)
{
    /// <summary>The complex value at a pixel.</summary>
    public Complex ValueAt(double x, double y)
    {
        double dx = x - Cx, dy = Cy - y;
        double share = Math.Sqrt(dx * dx + dy * dy) / Radius;
        double ang = Math.Atan2(dy, dx) - ZeroDeg * Math.PI / 180;
        if (Clockwise) ang = -ang;
        double v = InnerValue + share * (OuterValue - InnerValue);
        double mag = Db ? Math.Pow(10, v / 20) : v;
        return Complex.FromPolarCoordinates(Math.Max(0, mag), ang);
    }

    /// <summary>The pixel of a complex value.</summary>
    public (double X, double Y) PixelOf(Complex v)
    {
        double mag = v.Magnitude;
        double val = Db ? 20 * Math.Log10(Math.Max(mag, 1e-12)) : mag;
        double share = (val - InnerValue) / (OuterValue - InnerValue);
        double ang = (Clockwise ? -v.Phase : v.Phase) + ZeroDeg * Math.PI / 180;
        return (Cx + share * Radius * Math.Cos(ang), Cy - share * Radius * Math.Sin(ang));
    }

    /// <summary>|ΔS| across one pixel at the outer ring.</summary>
    public double PerPixel => Db ? Math.Pow(10, OuterValue / 20) * Math.Abs(OuterValue - InnerValue) / Radius * Math.Log(10) / 20
                                 : Math.Abs(OuterValue - InnerValue) / Radius;
}

public static class PolarFrame
{
    public static PolarCalibration Calibrate(PlotFrame frame, IReadOnlyList<PlotWord> words)
    {
        ArgumentNullException.ThrowIfNull(frame);
        double cx = frame.Cx, cy = frame.Cy, r = frame.Radius;

        // Ring labels: numbers near the horizontal axis, inside the circle, at a ring's radius (or the outer one).
        var rings = frame.Rings.Append(1.0).ToList();
        var pairs = new List<(double Share, double Value)>();
        foreach (var w in words.Where(w => w.Number is not null && !w.Vertical))
        {
            double dx = w.Box.CentreX - cx, dy = w.Box.CentreY - cy;
            if (Math.Abs(dy) > 2.5 * w.Height || Math.Abs(dx) > r * 1.05) continue;
            double share = Math.Abs(dx) / r;
            var near = rings.OrderBy(s => Math.Abs(s - share)).First();
            if (Math.Abs(near - share) * r > Math.Max(1.2 * w.Box.Width, 0.06 * r)) continue;
            if (pairs.Any(p => Math.Abs(p.Share - near) < 1e-9)) continue;
            pairs.Add((near, w.Number!.Value));
        }
        bool stated = false, db = false;
        double outer = 1, inner = 0;
        if (pairs.Count >= 2)
        {
            // value = inner + share·(outer − inner), least squares.
            double sx = pairs.Sum(p => p.Share), sy = pairs.Sum(p => p.Value), sxx = pairs.Sum(p => p.Share * p.Share),
                   sxy = pairs.Sum(p => p.Share * p.Value);
            int n = pairs.Count;
            double b = (n * sxy - sx * sy) / (n * sxx - sx * sx), a = (sy - b * sx) / n;
            inner = a;
            outer = a + b;
            db = pairs.Any(p => p.Value < 0) || outer > 1.5 && Math.Abs(inner) > 1.5;
            stated = true;
        }

        // Angle labels: a 90 above or below the centre, just outside the circle.
        bool clockwise = false, anglesStated = false;
        foreach (var w in words.Where(w => w.Number is { } v && Math.Abs(Math.Abs(v) - 90) < 1e-9))
        {
            double dx = w.Box.CentreX - cx, dy = w.Box.CentreY - cy;
            if (Math.Abs(dx) > 0.3 * r || Math.Abs(dy) < 0.85 * r) continue;
            clockwise = (dy > 0) == (w.Number > 0);
            anglesStated = true;
            break;
        }
        return new PolarCalibration(cx, cy, r, db, outer, inner, clockwise, 0, stated, anglesStated);
    }
}
