using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>
/// MMITER's geometry and its excess-reactance table: a chamfered (mitred) microstrip bend of width
/// <c>W</c>, turn <c>θ</c> and miter fraction <c>M</c>.
///
/// <para><b>Geometry, and where the reference planes are.</b> The two arms' edge lines cross at the
/// OUTER corner O and the INNER corner I; <c>d = |OI| = W / cos(θ/2)</c>. The chamfer is the cut
/// perpendicular to OI at distance <c>M·d</c> from O — Douville &amp; James' definition of the miter
/// fraction (IEEE MTT-26, 1978). At 90°, <c>M = 0.5</c> runs the cut corner to corner across the W×W
/// corner square, and the per-edge leg of the removed triangle is <c>2·M·W/sin θ</c> (= <c>W</c> at
/// 90°, 50 %). Each reference plane is perpendicular to its arm through I, a distance
/// <see cref="PlaneOffset"/> = <c>(W/2)·tan(θ/2)</c> from the centre-line corner — the edges of the
/// corner square at 90°, exactly where MBEND's planes sit. A chamfer deeper than <c>sin²(θ/2)</c>
/// reaches past those planes into the arms; the part's PINS then move out to where the strip is full
/// width again (<see cref="PinOffset"/>) and the model adds that much full-width line on each side, so
/// an MLIN abutting a pin is always a plain rectangle that leaves the chamfer open.</para>
///
/// <para><b>Electrical model.</b> The bend is the line it is drawn as — MLIN over its own centre-line
/// length between the planes, <see cref="CornerLength"/> = <c>W·tan(θ/2)</c> — split in half around a
/// lumped EXCESS: series <c>ΔL/2</c>, shunt <c>ΔC</c>, series <c>ΔL/2</c>. The line carries the
/// dispersion and the loss; the excess is what the corner does that a straight piece of line would
/// not. Written that way the excess is close to frequency-independent (it moved by ≤ 4 % from 1 to
/// 11 GHz on every structure it was checked against), which a single L-C-L for the whole corner is
/// not (its C moves 8-9 % over the same band).</para>
///
/// <para><b>Where the excess comes from.</b> The quasi-static scaling law of the problem:
/// <c>ΔL = μ₀·h·λ(u, εr, M, θ)</c>, <c>ΔC = ε₀·h·κ(u, εr, M, θ)</c> with <c>u = W/h</c>, so a table
/// in the four dimensionless parameters covers every substrate height. The table
/// (<see cref="MicrostripMiterBendTable"/>) was generated with circuitRF's own full-wave planar MoM
/// (grounded edge ports, de-embedded through a port-free two-line fit of the same width, the
/// two-bend "dogleg" for oblique angles so both ports sit on axis-aligned edges); see that file's
/// header for the grid and the procedure. It is interpolated piecewise-cubically (monotone,
/// Fritsch-Carlson) in <c>ln u</c>, <c>M</c>, <c>θ</c> and <c>εr</c>; <c>θ = 0</c> is an exact node
/// (no corner, no excess).</para>
///
/// <para><b>Validity.</b> Inside the table: <c>0.35 ≤ W/h ≤ 7</c>, <c>2.2 ≤ εr ≤ 13</c>,
/// <c>0 &lt; |θ| ≤ 120°</c>, <c>0 ≤ M ≤ </c><see cref="MaxMiter"/>. Outside the W/h and εr ranges the
/// interpolant is continued linearly and the instance says so; an angle or a miter outside the table
/// is refused, because there is no data behind it and the geometry there (a pinched corner) is the
/// kind a closed form gets most wrong. Conductor thickness is not a table axis — the excess was
/// characterised on a thin sheet; the corner LINE section does use the instance's own T.</para>
/// </summary>
public static class MicrostripMiterBend
{
    public static readonly ValidityRange WOverHRange = new(0.35, 7.0);
    public static readonly ValidityRange EpsRRange = new(2.2, 13.0);

    /// <summary>The largest bend the table covers, degrees.</summary>
    public const double MaxAngleDeg = 120.0;

    /// <summary>The corner's centre-line length between the two reference planes, metres.</summary>
    public static double CornerLength(double wMeters, double angleDeg)
        => wMeters * Math.Tan(Math.Abs(angleDeg) * Math.PI / 360.0);

    /// <summary>Distance from the centre-line corner to each reference plane, metres.</summary>
    public static double PlaneOffset(double wMeters, double angleDeg) => CornerLength(wMeters, angleDeg) / 2.0;

    /// <summary>Distance from the centre-line corner to each PIN, metres: where the strip is full width
    /// again. That is the inner-corner plane (<see cref="PlaneOffset"/>) unless the chamfer is deeper
    /// than <c>sin²(θ/2)</c> (corner to corner at 90°) and runs past it, in which case it is where the
    /// chamfer meets the outer edge, <c>leg − (W/2)·tan(θ/2)</c>. An MLIN abutting the pin is then a
    /// full-width rectangle that cannot fill the chamfer back in.</summary>
    public static double PinOffset(double wMeters, double angleDeg, double miter)
    {
        double s = PlaneOffset(wMeters, angleDeg);
        return Math.Max(s, ChamferLeg(wMeters, angleDeg, miter) - s);
    }

    /// <summary>The per-outer-edge leg of the removed triangle, metres, measured back from the sharp
    /// outer corner: <c>2·M·W / sin θ</c>.</summary>
    public static double ChamferLeg(double wMeters, double angleDeg, double miter)
        => 2.0 * miter * wMeters / Math.Sin(Math.Abs(angleDeg) * Math.PI / 180.0);

    /// <summary>Douville &amp; James' optimum miter fraction for a RIGHT-ANGLE bend,
    /// <c>0.52 + 0.65·exp(−1.35·W/h)</c> (W/h ≥ 0.25, εr ≤ 25).</summary>
    public static double DouvilleJamesOptimum(double wMeters, double hMeters)
        => 0.52 + 0.65 * Math.Exp(-1.35 * wMeters / hMeters);

    /// <summary>The deepest miter the model accepts at this angle: 0.6 up to 45°, rising linearly to
    /// 0.8 at 90° and above. On a shallow bend a deeper cut reaches far down both arms (the chamfer leg is
    /// <c>2·M·W/sin θ</c>) and leaves little of the width at the corner; the planar solve could not
    /// characterise much of that region, so the table there is continued rather than measured.</summary>
    public static double MaxMiter(double angleDeg)
    {
        double a = Math.Abs(angleDeg);
        if (a <= 45.0) return 0.6;
        if (a >= 90.0) return 0.8;
        return 0.6 + 0.2 * (a - 45.0) / 45.0;
    }

    /// <summary>The refusal for geometry the model has no data for; null when it can be evaluated.</summary>
    public static Diagnostic? Refusal(double wMeters, double hMeters, double angleDeg, double miter)
    {
        if (!(wMeters > 0.0))
            return Diagnostic.Create("mmiter.width.not-positive", DiagnosticSeverity.Error,
                "MMITER: W={w} m must be positive.", ("w", wMeters));
        if (!(hMeters > 0.0))
            return Diagnostic.Create("mmiter.height.not-positive", DiagnosticSeverity.Error,
                "MMITER: substrate H={h} m must be positive.", ("h", hMeters));
        double a = Math.Abs(angleDeg);
        if (!(a > 0.0 && a <= MaxAngleDeg))
            return Diagnostic.Create("mmiter.angle.out-of-range", DiagnosticSeverity.Error,
                "MMITER: Angle={angle} deg is outside (0, {max}] deg. A 0 deg bend is an MLIN; past " +
                "{max} deg the corner was not characterised. Write the unit (Angle=90 deg): a bare " +
                "number is read in radians.", ("angle", angleDeg), ("max", MaxAngleDeg));
        if (!(miter >= 0.0 && miter <= MaxMiter(a) + 1e-12))
            return Diagnostic.Create("mmiter.miter.out-of-range", DiagnosticSeverity.Error,
                "MMITER: M={m} is outside [0, {max}] at Angle={angle} deg. M is the Douville-James " +
                "fraction of the outer-to-inner corner diagonal (0.5 cuts corner to corner at 90 deg).",
                ("m", miter), ("max", MaxMiter(a)), ("angle", a));
        return null;
    }

    /// <summary>Normalised excess (λ = ΔL/(μ₀h), κ = ΔC/(ε₀h)) at one geometry, reporting through
    /// <paramref name="reporter"/> when W/h or εr lies outside the table.</summary>
    public static (double Lambda, double Kappa) Excess(double wOverH, double epsR, double miter, double angleDeg,
        MicrostripValidityReporter reporter)
    {
        reporter.CheckRange("MMITER corner table", "W/h", wOverH, WOverHRange.Min, WOverHRange.Max);
        reporter.CheckRange("MMITER corner table", "er", epsR, EpsRRange.Min, EpsRRange.Max);
        return Interpolate(Math.Log(wOverH), epsR, miter, Math.Abs(angleDeg));
    }

    /// <summary>Tensor-product monotone-cubic interpolation over (M, θ, ln u, εr), in that order.</summary>
    public static (double Lambda, double Kappa) Interpolate(double lnU, double epsR, double miter, double angleDeg)
    {
        var t = MicrostripMiterBendTable.Instance;
        int nu = t.LnU.Length, ne = t.EpsR.Length, nt = t.Theta.Length, nm = t.Miter.Length;
        var byU = new double[2][]; byU[0] = new double[nu]; byU[1] = new double[nu];
        var byE = new double[2][]; byE[0] = new double[ne]; byE[1] = new double[ne];
        var byT = new double[2][]; byT[0] = new double[nt]; byT[1] = new double[nt];
        var byM = new double[2][]; byM[0] = new double[nm]; byM[1] = new double[nm];
        for (int ie = 0; ie < ne; ie++)
        {
            for (int iu = 0; iu < nu; iu++)
            {
                for (int it = 0; it < nt; it++)
                {
                    for (int im = 0; im < nm; im++)
                    {
                        byM[0][im] = t.Lambda[iu, ie, it, im];
                        byM[1][im] = t.Kappa[iu, ie, it, im];
                    }
                    byT[0][it] = Pchip(t.Miter, byM[0], miter);
                    byT[1][it] = Pchip(t.Miter, byM[1], miter);
                }
                byU[0][iu] = Pchip(t.Theta, byT[0], angleDeg);
                byU[1][iu] = Pchip(t.Theta, byT[1], angleDeg);
            }
            byE[0][ie] = Pchip(t.LnU, byU[0], lnU);
            byE[1][ie] = Pchip(t.LnU, byU[1], lnU);
        }
        return (Pchip(t.EpsR, byE[0], epsR), Pchip(t.EpsR, byE[1], epsR));
    }

    /// <summary>Fritsch-Carlson monotone piecewise-cubic Hermite interpolation on strictly increasing
    /// <paramref name="x"/>; continued LINEARLY (end slope) outside the nodes.</summary>
    public static double Pchip(double[] x, double[] y, double xq)
    {
        int n = x.Length;
        if (n == 1) return y[0];
        var d = Slopes(x, y);
        if (xq <= x[0]) return y[0] + d[0] * (xq - x[0]);
        if (xq >= x[n - 1]) return y[n - 1] + d[n - 1] * (xq - x[n - 1]);
        int k = 0;
        while (xq > x[k + 1]) k++;
        double h = x[k + 1] - x[k], s = (xq - x[k]) / h;
        double h00 = (1 + 2 * s) * (1 - s) * (1 - s), h10 = s * (1 - s) * (1 - s);
        double h01 = s * s * (3 - 2 * s), h11 = s * s * (s - 1);
        return h00 * y[k] + h10 * h * d[k] + h01 * y[k + 1] + h11 * h * d[k + 1];
    }

    private static double[] Slopes(double[] x, double[] y)
    {
        int n = x.Length;
        var del = new double[n - 1];
        for (int i = 0; i < n - 1; i++) del[i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]);
        var d = new double[n];
        if (n == 2) { d[0] = d[1] = del[0]; return d; }
        for (int i = 1; i < n - 1; i++)
        {
            if (del[i - 1] * del[i] <= 0) { d[i] = 0; continue; }
            double h0 = x[i] - x[i - 1], h1 = x[i + 1] - x[i];
            double w1 = 2 * h1 + h0, w2 = h1 + 2 * h0;
            d[i] = (w1 + w2) / (w1 / del[i - 1] + w2 / del[i]);
        }
        d[0] = EndSlope(x[1] - x[0], x[2] - x[1], del[0], del[1]);
        d[n - 1] = EndSlope(x[n - 1] - x[n - 2], x[n - 2] - x[n - 3], del[n - 2], del[n - 3]);
        return d;
    }

    private static double EndSlope(double h0, double h1, double del0, double del1)
    {
        double d = ((2 * h0 + h1) * del0 - h0 * del1) / (h0 + h1);
        if (Math.Sign(d) != Math.Sign(del0)) return 0;
        if (Math.Sign(del0) != Math.Sign(del1) && Math.Abs(d) > Math.Abs(3 * del0)) return 3 * del0;
        return d;
    }
}
