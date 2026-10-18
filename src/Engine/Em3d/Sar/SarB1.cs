// brief-sar-6 R-sar6-4 (overview L8) — B1+ and B1− from the magnetic field, openEMS's convention exactly. The derivation is
// docs/design/sar.md §1.5; this file is its arithmetic and nothing else. Numbers only: no file, no solver (overview L5).
//
// With the e^{jωt} phasor, b(t) = Re{B e^{jωt}}, B = μ0·H, and B0 along +z, the transverse phasor splits into two circularly
// polarised parts,
//
//     Bx x̂ + By ŷ = B1+ (x̂ − j ŷ) + B1− (x̂ + j ŷ),     B1+ = (Bx + j By)/2,   B1− = (Bx − j By)/2,
//
// B1+ rotating from +x toward +y (counter-clockwise seen from +z), B1− the other way. For B0 along +x or +y the axes are
// permuted cyclically, (y, z) and (z, x) taking the place of (x, y), so the frame stays right-handed. A B0 along a negative
// axis looks at the same plane from the other side, which reverses the sense of rotation: B1+ and B1− swap.

using System.Numerics;

namespace CircuitRF.Engine.Em3d.Sar;

public static class SarB1
{
    /// <summary>μ0, H/m (CODATA 2018).</summary>
    public const double Mu0 = 1.25663706212e-6;

    /// <summary>
    /// B1+ and B1− of the flux-density phasor (<paramref name="bx"/>, <paramref name="by"/>, <paramref name="bz"/>), for B0
    /// along axis <paramref name="b0Axis"/> (0 x, 1 y, 2 z) with sign <paramref name="b0Sign"/> (+1 or −1).
    /// </summary>
    public static (Complex Plus, Complex Minus) FromB(Complex bx, Complex by, Complex bz, int b0Axis, int b0Sign)
    {
        // The two transverse components, in the order that makes (u, v, B0) right-handed.
        var (u, v) = b0Axis switch
        {
            0 => (by, bz),
            1 => (bz, bx),
            2 => (bx, by),
            _ => throw new ArgumentOutOfRangeException(nameof(b0Axis), b0Axis, "B0's axis is 0 (x), 1 (y) or 2 (z)."),
        };
        var plus = (u + Complex.ImaginaryOne * v) / 2;
        var minus = (u - Complex.ImaginaryOne * v) / 2;
        return b0Sign < 0 ? (minus, plus) : (plus, minus);
    }

    /// <summary>B1+ and B1− of the magnetic-field phasor H (A/m): B = μ0·H, then <see cref="FromB"/>.</summary>
    public static (Complex Plus, Complex Minus) FromH(Complex hx, Complex hy, Complex hz, int b0Axis, int b0Sign)
        => FromB(Mu0 * hx, Mu0 * hy, Mu0 * hz, b0Axis, b0Sign);
}
