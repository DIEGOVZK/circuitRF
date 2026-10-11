// A Smith chart, calibrated — brief-img-14-sparameter-plots-to-touchstone.md R-im14-4.
//
// A Smith chart needs no calibration beyond its outer circle: its centre is Γ = 0, its radius |Γ| = 1, and the real
// axis runs through the tangency point of the constant-r circles. PlotFrames found the circle and checked it against
// the constant-r family (r = 1 must pass through the centre, and at least one other circle must lie where it should);
// this file maps a pixel to Γ and reads the reference impedance.
//
// AN ADMITTANCE CHART is the impedance chart turned half a turn: its constant-g circles are tangent at the LEFT point,
// which is Γ = −1 (a short). The point drawn is the same Γ either way — the chart only labels it as an admittance — so
// the mapping from pixels to Γ is one and the same, and an impedance and an admittance chart give the same Γ for the
// same drawn point. An overlaid Z-Y chart is read as Z.
//
// THE REFERENCE IMPEDANCE comes from a `Z0 = 50` word on the chart when one is read; else it is 50 Ω, stated as an
// assumption in the report and one click (or --smith-z0) from being changed.

using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Imaging.Plots;

/// <summary>A Smith chart's calibration (R-im14-4).</summary>
/// <param name="Z0">The reference impedance, ohms.</param>
/// <param name="Z0Stated">Read from the chart or given by the user; false when 50 Ω was assumed.</param>
public sealed record SmithCalibration(double Cx, double Cy, double Radius, bool Admittance, bool Overlaid, double Z0, bool Z0Stated,
                                      IReadOnlyList<double> CirclesChecked, double ResidualPx)
{
    /// <summary>Γ at a pixel.</summary>
    public Complex GammaAt(double x, double y) => new((x - Cx) / Radius, (Cy - y) / Radius);

    /// <summary>The pixel of Γ.</summary>
    public (double X, double Y) PixelOf(Complex g) => (Cx + g.Real * Radius, Cy - g.Imaginary * Radius);

    /// <summary>|ΔΓ| across one pixel — the chart's resolution.</summary>
    public double PerPixel => 1 / Radius;
}

public static class SmithFrame
{
    /// <summary>The reference impedance taken when the chart states none (R-im14-4).</summary>
    public const double DefaultZ0 = 50;

    private static readonly Regex Z0Word = new(@"Z[0oO]\s*=\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.CultureInvariant);

    /// <summary>Calibrates a Smith chart frame; <paramref name="z0"/> overrides the chart's own reference.</summary>
    public static SmithCalibration Calibrate(PlotFrame frame, IReadOnlyList<PlotWord> words, double? z0 = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        double? read = null;
        foreach (var w in words)
        {
            if (!w.Read || w.Box.CentreX < frame.Cx - 1.6 * frame.Radius || w.Box.CentreX > frame.Cx + 1.6 * frame.Radius
                || w.Box.CentreY < frame.Cy - 1.6 * frame.Radius || w.Box.CentreY > frame.Cy + 1.6 * frame.Radius) continue;
            var m = Z0Word.Match(w.Text.Replace(" ", ""));
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0)
            {
                read = v;
                break;
            }
        }
        double z = z0 ?? read ?? DefaultZ0;
        return new SmithCalibration(frame.Cx, frame.Cy, frame.Radius, frame.Admittance, frame.Overlaid, z, z0 is not null || read is not null,
                                    frame.CirclesChecked, frame.Residual);
    }
}
