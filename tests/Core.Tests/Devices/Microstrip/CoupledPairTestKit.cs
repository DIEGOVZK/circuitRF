using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>Shared arithmetic for the coupled-pair tests: a 4-port's Y from its stamp, and S from Y.</summary>
internal static class CoupledPairTestKit
{
    public static Complex[,] Y(ComponentModel m, double fHz)
    {
        var mna = new CapturingMnaContext();
        m.Stamp(mna, new ElaboratedComponent("X", "X1", [1, 2, 3, 4], new Dictionary<string, Value>(), m), 2 * Math.PI * fHz);
        var y = new Complex[4, 4];
        foreach (var ((r, c), v) in mna.Entries) y[r - 1, c - 1] += v;
        return y;
    }

    /// <summary>4×4 S (reference <paramref name="z0"/>) = (I − Z0·Y)(I + Z0·Y)⁻¹.</summary>
    public static Complex[,] S(ComponentModel m, double fHz, double z0 = 50.0)
    {
        var y = Y(m, fHz);
        var a = new Complex[4, 8];
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++) a[i, j] = (i == j ? 1 : 0) + z0 * y[i, j];
            a[i, 4 + i] = 1;
        }
        for (int p = 0; p < 4; p++)
        {
            int best = p;
            for (int r = p + 1; r < 4; r++) if (a[r, p].Magnitude > a[best, p].Magnitude) best = r;
            for (int k = 0; k < 8; k++) (a[p, k], a[best, k]) = (a[best, k], a[p, k]);
            Complex d = a[p, p];
            for (int k = 0; k < 8; k++) a[p, k] /= d;
            for (int r = 0; r < 4; r++)
            {
                if (r == p) continue;
                Complex f = a[r, p];
                for (int k = 0; k < 8; k++) a[r, k] -= f * a[p, k];
            }
        }
        var s = new Complex[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                Complex sum = 0;
                for (int k = 0; k < 4; k++) sum += ((i == k ? 1 : 0) - z0 * y[i, k]) * a[k, 4 + j];
                s[i, j] = sum;
            }
        return s;
    }

    /// <summary>Largest eigenvalue of SᴴS (the square of S's largest singular value), by power iteration.</summary>
    public static double MaxSingularSquared(Complex[,] s)
    {
        var m = new Complex[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                for (int k = 0; k < 4; k++) m[i, j] += Complex.Conjugate(s[k, i]) * s[k, j];
        var v = new Complex[] { 1, 0.7, 0.3, 0.1 };
        double lambda = 0;
        for (int it = 0; it < 500; it++)
        {
            var w = new Complex[4];
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) w[i] += m[i, j] * v[j];
            double n = Math.Sqrt(w.Sum(x => x.Magnitude * x.Magnitude));
            lambda = n / Math.Sqrt(v.Sum(x => x.Magnitude * x.Magnitude));
            for (int i = 0; i < 4; i++) v[i] = w[i] / n;
        }
        return lambda;
    }

    /// <summary>A 2-port's Y block of a line (Z, γl) as the shared uniform-line formula gives it.</summary>
    public static (Complex Diag, Complex Off) LineY(double z, Complex gl)
        => (Complex.Cosh(gl) / (z * Complex.Sinh(gl)), -1.0 / (z * Complex.Sinh(gl)));
}
