// Network-parameter algebra for de-embedding (brief-modelcomposer-4 R-mc4-1, R-mc4-3; overview D14).
//
// Numerics only — no domain types. Every matrix here is 2×2 complex: a FET's two ports, a pad per side,
// and the four 2×2 blocks of a six-pin network solved as a 4-port. Closed forms throughout, so nothing
// is inverted that a formula can avoid, and the condition numbers the de-embedding reports are exact.

using System.Numerics;

namespace CircuitRF.Engine.Modeling;

/// <summary>A 2×2 complex matrix <c>[[A, B], [C, D]]</c>.</summary>
public readonly record struct C2(Complex A, Complex B, Complex C, Complex D)
{
    public static C2 Identity => new(Complex.One, Complex.Zero, Complex.Zero, Complex.One);

    public Complex this[int i, int j] => (i, j) switch
    {
        (0, 0) => A, (0, 1) => B, (1, 0) => C, (1, 1) => D,
        _ => throw new ArgumentOutOfRangeException(nameof(i)),
    };

    public Complex Det => A * D - B * C;

    /// <summary>The inverse; a singular matrix gives infinities, never an exception — the caller reads <see cref="Condition"/>.</summary>
    public C2 Inverse()
    {
        var d = Det;
        return new C2(D / d, -B / d, -C / d, A / d);
    }

    public static C2 operator +(C2 x, C2 y) => new(x.A + y.A, x.B + y.B, x.C + y.C, x.D + y.D);
    public static C2 operator -(C2 x, C2 y) => new(x.A - y.A, x.B - y.B, x.C - y.C, x.D - y.D);
    public static C2 operator *(Complex k, C2 x) => new(k * x.A, k * x.B, k * x.C, k * x.D);
    public static C2 operator *(C2 x, C2 y) => new(
        x.A * y.A + x.B * y.C, x.A * y.B + x.B * y.D,
        x.C * y.A + x.D * y.C, x.C * y.B + x.D * y.D);

    /// <summary>The largest entry's magnitude.</summary>
    public double MaxAbs => Math.Max(Math.Max(A.Magnitude, B.Magnitude), Math.Max(C.Magnitude, D.Magnitude));

    /// <summary>
    /// The 2-norm condition number <c>σmax/σmin</c>, from the Frobenius norm and the determinant
    /// (<c>σmax² + σmin² = ‖M‖F²</c>, <c>σmax·σmin = |det|</c>) — no eigen-solve, and no cancellation in
    /// <c>σmin</c>. Infinity for a singular matrix.
    /// </summary>
    public double Condition()
    {
        double f2 = A.Magnitude * A.Magnitude + B.Magnitude * B.Magnitude
                  + C.Magnitude * C.Magnitude + D.Magnitude * D.Magnitude;
        double det = Det.Magnitude;
        if (det == 0 || !double.IsFinite(det)) return double.PositiveInfinity;
        double disc = Math.Sqrt(Math.Max(f2 * f2 - 4 * det * det, 0));
        double smax2 = (f2 + disc) / 2;
        return smax2 / det;
    }
}

/// <summary>Conversions and de-embedding on 2×2 network matrices (R-mc4-1, R-mc4-3).</summary>
public static class NetworkAlgebra
{
    /// <summary>The ceiling above which a de-embedding is flagged and left out of later averaging (R-mc4-3).</summary>
    public const double ConditionLimit = 1e10;

    /// <summary><c>Y = (I − S)(I + S)⁻¹ / Z0</c> for one real reference impedance.</summary>
    public static C2 SToY(C2 s, double z0) => (1.0 / z0) * ((C2.Identity - s) * (C2.Identity + s).Inverse());

    /// <summary><c>S = (I − Z0·Y)(I + Z0·Y)⁻¹</c>.</summary>
    public static C2 YToS(C2 y, double z0)
    {
        var zy = (Complex)z0 * y;
        return (C2.Identity - zy) * (C2.Identity + zy).Inverse();
    }

    /// <summary>ABCD from S at one real Z0 — finite for a pure series or a pure shunt element, where Y or Z is not.</summary>
    public static C2 SToAbcd(C2 s, double z0)
    {
        Complex s11 = s.A, s12 = s.B, s21 = s.C, s22 = s.D, x = s12 * s21, d = 2 * s21;
        return new C2(
            ((1 + s11) * (1 - s22) + x) / d,
            z0 * ((1 + s11) * (1 + s22) - x) / d,
            ((1 - s11) * (1 - s22) - x) / (d * z0),
            ((1 - s11) * (1 + s22) + x) / d);
    }

    /// <summary>S from ABCD at one real Z0.</summary>
    public static C2 AbcdToS(C2 t, double z0)
    {
        Complex a = t.A, b = t.B / z0, c = t.C * z0, d = t.D, den = a + b + c + d;
        return new C2((a + b - c - d) / den, 2 * t.Det / den, 2 / den, (-a + b - c + d) / den);
    }

    /// <summary>ABCD from Y (<c>Y21 ≠ 0</c>).</summary>
    public static C2 YToAbcd(C2 y) => new(-y.D / y.C, -1 / y.C, -y.Det / y.C, -y.A / y.C);

    /// <summary>Y from ABCD (<c>B ≠ 0</c>).</summary>
    public static C2 AbcdToY(C2 t) => new(t.D / t.B, -t.Det / t.B, -1 / t.B, t.A / t.B);

    /// <summary>The same two-port turned round, port 1 and port 2 exchanged, as ABCD.</summary>
    public static C2 Reverse(C2 t) => (1 / t.Det) * new C2(t.D, t.B, t.C, t.A);

    /// <summary>
    /// The device between two cascaded launches: <c>T_dev = T_left⁻¹ · T_meas · T_right⁻¹</c>, all ABCD, the
    /// left launch's port 2 and the right launch's port 1 facing the device.
    /// </summary>
    public static C2 RemoveCascade(C2 measured, C2 left, C2 right) => left.Inverse() * measured * right.Inverse();

    /// <summary>
    /// Open-short de-embedding: the open dummy's admittance is subtracted in parallel, then the short
    /// dummy's (itself open-corrected) impedance in series — <c>Y_dev = ((Y_m − Y_o)⁻¹ − (Y_s − Y_o)⁻¹)⁻¹</c>.
    /// </summary>
    public static C2 OpenShort(C2 yMeasured, C2 yOpen, C2 yShort) =>
        ((yMeasured - yOpen).Inverse() - (yShort - yOpen).Inverse()).Inverse();

    /// <summary>
    /// The extrinsic admittance of a core <paramref name="yInt"/> inside a network whose 4-port Y has the
    /// 2×2 blocks given: <c>Y_ext = Y_aa − Y_ab (Y_bb + Y_int)⁻¹ Y_ba</c>. Holds because the core takes its
    /// current on ports C and D and returns it on their shared reference.
    /// </summary>
    public static C2 Embed(C2 yAa, C2 yAb, C2 yBa, C2 yBb, C2 yInt) => yAa - yAb * (yBb + yInt).Inverse() * yBa;

    /// <summary>The intrinsic core and the condition numbers the inversion went through.</summary>
    public readonly record struct Deembedded(C2 YInt, double CondOuter, double CondAb, double CondBa)
    {
        /// <summary>The worst of the three.</summary>
        public double Condition => Math.Max(CondOuter, Math.Max(CondAb, CondBa));

        /// <summary>True when any exceeds <see cref="ConditionLimit"/> — the point is reported, never averaged.</summary>
        public bool Flagged => !(Condition <= ConditionLimit);
    }

    /// <summary>
    /// The exact inverse of <see cref="Embed"/>: <c>Y_int = Y_ba (Y_aa − Y_ext)⁻¹ Y_ab − Y_bb</c>, with the
    /// condition numbers of <c>(Y_aa − Y_ext)</c>, <c>Y_ab</c> and <c>Y_ba</c> (R-mc4-3).
    /// </summary>
    public static Deembedded Deembed(C2 yAa, C2 yAb, C2 yBa, C2 yBb, C2 yExt)
    {
        var outer = yAa - yExt;
        return new Deembedded(yBa * outer.Inverse() * yAb - yBb, outer.Condition(), yAb.Condition(), yBa.Condition());
    }

    /// <summary>
    /// Complex-linear interpolation of a sampled 2×2 at <paramref name="f"/>. The caller has already
    /// refused a frequency outside <paramref name="freqs"/>; an exact sample is returned as stored.
    /// </summary>
    public static C2 Interpolate(double[] freqs, IReadOnlyList<C2> values, double f)
    {
        int hi = Array.BinarySearch(freqs, f);
        if (hi >= 0) return values[hi];
        hi = ~hi;
        if (hi == 0) return values[0];
        if (hi >= freqs.Length) return values[^1];
        int lo = hi - 1;
        double t = (f - freqs[lo]) / (freqs[hi] - freqs[lo]);
        return values[lo] + (Complex)t * (values[hi] - values[lo]);
    }
}
