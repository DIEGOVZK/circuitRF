namespace CircuitRF.Engine.Modeling.Ann;

/// <summary>
/// <c>exp</c>, <c>tanh</c> and the logistic function built from IEEE arithmetic alone (R-mc6-6).
///
/// <para><b>Why not <see cref="Math.Tanh"/>.</b> It calls the platform's C library, and the C libraries
/// of macOS, glibc and Windows round differently in the last place. A trainer that runs thousands of
/// iterations amplifies one ulp into different weights, so "the same seed gives the same weights on
/// every platform" would be false. Addition, multiplication, division, <see cref="Math.Sqrt"/> and
/// <see cref="Math.ScaleB"/> are exactly specified (and the JIT fuses no multiply-add on its own), so
/// everything here gives the same bits everywhere.</para>
///
/// <para>Accurate to a few ulp, which is all training needs; the runtime evaluator keeps using the
/// platform's <c>tanh</c>, and the difference between the two is far below any fit's error.</para>
/// </summary>
internal static class DeterministicMath
{
    private const double Ln2Hi  = 6.93147180369123816490e-01;   // ln 2's leading 32 bits: k·Ln2Hi is exact
    private const double Ln2Lo  = 1.90821492927058770002e-10;
    private const double InvLn2 = 1.44269504088896338700e+00;

    /// <summary>1/n! for n = 0…20, the Taylor coefficients both series below use.</summary>
    private static readonly double[] InvFactorial = BuildInvFactorial();

    private static double[] BuildInvFactorial()
    {
        var c = new double[21];
        double f = 1.0;
        c[0] = 1.0;
        for (int n = 1; n < c.Length; n++) { f *= n; c[n] = 1.0 / f; }
        return c;
    }

    /// <summary>eˣ. Reduced to <c>x = k·ln2 + r</c>, |r| ≤ ln2/2, then a degree-13 Taylor series and an exact scale by 2ᵏ.</summary>
    public static double Exp(double x)
    {
        if (double.IsNaN(x)) return x;
        if (x > 709.78) return double.PositiveInfinity;
        if (x < -745.2) return 0.0;
        double k = Math.Round(x * InvLn2);
        double r = (x - k * Ln2Hi) - k * Ln2Lo;
        double p = InvFactorial[13];
        for (int n = 12; n >= 0; n--) p = p * r + InvFactorial[n];
        return Math.ScaleB(p, (int)k);
    }

    /// <summary>eˣ − 1 for −1 ≤ x ≤ 0, by its Taylor series — the small-argument half of <see cref="Tanh"/>.</summary>
    private static double Expm1Small(double x)
    {
        double p = InvFactorial[20];
        for (int n = 19; n >= 1; n--) p = p * x + InvFactorial[n];
        return p * x;
    }

    public static double Tanh(double z)
    {
        double a = Math.Abs(z);
        if (a > 20.0) return z > 0 ? 1.0 : -1.0;
        double t = -2.0 * a;
        double m = a < 0.5 ? Expm1Small(t) : Exp(t) - 1.0;
        double y = -m / (2.0 + m);
        return z < 0 ? -y : y;
    }

    public static double Sigmoid(double z) => 1.0 / (1.0 + Exp(-z));
}
