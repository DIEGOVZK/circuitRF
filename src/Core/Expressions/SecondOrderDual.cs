namespace CircuitRF.Core.Expressions;

/// <summary>
/// Second-order forward-mode AD over two seeds: a value, its gradient <c>(g0, g1)</c> and its Hessian
/// <c>(h00, h01, h11)</c> — what a composed FET's branch linearisation (brief-modelcomposer-22 R-mc22-2) reads from an
/// equation charge, where <see cref="Dual"/> stops at the gradient. Evaluated through the same compiled node tree
/// (<see cref="SddCompiler.Eval{T}"/>), so the value is the equation's own. Each guard (<c>exp</c>'s cap, <c>log</c>'s and
/// <c>sqrt</c>'s domain clamp) is <see cref="Dual"/>'s, so the derivatives belong to the same function.
/// </summary>
internal readonly struct SecondOrderDual : IAdScalar<SecondOrderDual>
{
    private const double ExpCap = 700.0;
    private const double LogFloor = 1e-300;

    public readonly double Value, G0, G1, H00, H01, H11;

    public SecondOrderDual(double value, double g0, double g1, double h00, double h01, double h11)
    {
        Value = value; G0 = g0; G1 = g1; H00 = h00; H01 = h01; H11 = h11;
    }

    /// <summary>Seed <paramref name="lane"/> (0 or 1) at <paramref name="value"/>.</summary>
    public static SecondOrderDual Seed(double value, int lane)
        => new(value, lane == 0 ? 1 : 0, lane == 1 ? 1 : 0, 0, 0, 0);

    private bool IsConstant => G0 == 0 && G1 == 0 && H00 == 0 && H01 == 0 && H11 == 0;

    /// <summary><c>f(a)</c> from <c>f</c>, <c>f′</c> and <c>f″</c> at <c>a</c>: <c>∇f = f′∇a</c>,
    /// <c>Hf = f′Ha + f″∇a∇aᵀ</c>.</summary>
    private static SecondOrderDual Chain(in SecondOrderDual a, double f, double f1, double f2)
        => new(f, f1 * a.G0, f1 * a.G1,
               f1 * a.H00 + f2 * a.G0 * a.G0, f1 * a.H01 + f2 * a.G0 * a.G1, f1 * a.H11 + f2 * a.G1 * a.G1);

    public static SecondOrderDual Add(in SecondOrderDual a, in SecondOrderDual b)
        => new(a.Value + b.Value, a.G0 + b.G0, a.G1 + b.G1, a.H00 + b.H00, a.H01 + b.H01, a.H11 + b.H11);

    public static SecondOrderDual Sub(in SecondOrderDual a, in SecondOrderDual b)
        => new(a.Value - b.Value, a.G0 - b.G0, a.G1 - b.G1, a.H00 - b.H00, a.H01 - b.H01, a.H11 - b.H11);

    public static SecondOrderDual Mul(in SecondOrderDual a, in SecondOrderDual b)
        => new(a.Value * b.Value,
               a.G0 * b.Value + a.Value * b.G0,
               a.G1 * b.Value + a.Value * b.G1,
               a.H00 * b.Value + a.Value * b.H00 + 2 * a.G0 * b.G0,
               a.H01 * b.Value + a.Value * b.H01 + a.G0 * b.G1 + a.G1 * b.G0,
               a.H11 * b.Value + a.Value * b.H11 + 2 * a.G1 * b.G1);

    public static SecondOrderDual Div(in SecondOrderDual a, in SecondOrderDual b)
    {
        double bv = b.Value;
        var inverse = Chain(b, 1 / bv, -1 / (bv * bv), 2 / (bv * bv * bv));
        return Mul(a, inverse);
    }

    public static SecondOrderDual Neg(in SecondOrderDual a) => new(-a.Value, -a.G0, -a.G1, -a.H00, -a.H01, -a.H11);

    /// <summary>A constant exponent is <c>b·a^(b−1)</c>; a varying one goes through <c>exp(b·ln a)</c> where
    /// <c>a &gt; 0</c> and, like <see cref="Dual.Pow"/>, ignores its own derivatives elsewhere.</summary>
    public static SecondOrderDual Pow(in SecondOrderDual a, in SecondOrderDual b)
    {
        double av = a.Value, bv = b.Value;
        if (!b.IsConstant && av > 0) return Exp(Mul(b, Log(a)));
        double f1 = bv * Math.Pow(av, bv - 1);
        double f2 = bv * (bv - 1) == 0 ? 0 : bv * (bv - 1) * Math.Pow(av, bv - 2);
        return Chain(a, Math.Pow(av, bv), f1, f2);
    }

    public static SecondOrderDual Exp(in SecondOrderDual a)
    {
        double ev = Math.Exp(a.Value > ExpCap ? ExpCap : a.Value);
        return Chain(a, ev, ev, ev);
    }

    public static SecondOrderDual Log(in SecondOrderDual a)
    {
        double av = a.Value <= 0 ? LogFloor : a.Value;
        return Chain(a, Math.Log(av), 1 / av, -1 / (av * av));
    }

    public static SecondOrderDual Sqrt(in SecondOrderDual a)
    {
        double av = a.Value < 0 ? 0 : a.Value;
        double sv = Math.Sqrt(av);
        double f1 = 1 / (sv > 0 ? 2 * sv : 1e-150);
        return Chain(a, sv, f1, av > 0 ? -f1 / (2 * av) : 0);
    }

    public static SecondOrderDual Tanh(in SecondOrderDual a)
    {
        double t = Math.Tanh(a.Value), d = 1 - t * t;
        return Chain(a, t, d, -2 * t * d);
    }

    public static SecondOrderDual Sin(in SecondOrderDual a)
        => Chain(a, Math.Sin(a.Value), Math.Cos(a.Value), -Math.Sin(a.Value));

    public static SecondOrderDual Cos(in SecondOrderDual a)
        => Chain(a, Math.Cos(a.Value), -Math.Sin(a.Value), -Math.Cos(a.Value));

    public static SecondOrderDual Abs(in SecondOrderDual a)
        => Chain(a, Math.Abs(a.Value), a.Value >= 0 ? 1 : -1, 0);

    public static SecondOrderDual Constant(double d) => new(d, 0, 0, 0, 0, 0);

    public static double ValueOf(in SecondOrderDual a) => a.Value;
}
