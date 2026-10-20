namespace CircuitRF.Engine.Modeling;

/// <summary>
/// A terminal charge from its two capacitances by line integration ([P1] eqs. 6.30, 6.31; brief-modelcomposer-8
/// R-mc8-2): <c>Q(vgs, vds) = Q(anchor) + ∫ C1 dvgs + C2 dvds</c> along a path from the anchor. The integral is
/// path-independent exactly when <c>∂C1/∂vds = ∂C2/∂vgs</c> ([P1] eq. 6.27), so the two paths of [P1] Fig. 6.16
/// disagree by the curl they enclose — check (a)'s second number.
///
/// <list type="bullet">
/// <item><b>Path A</b>: along <c>vgs</c> at the anchor's <c>vds</c>, then along <c>vds</c>.</item>
/// <item><b>Path B</b>: along <c>vds</c> at the anchor's <c>vgs</c>, then along <c>vgs</c>.</item>
/// </list>
///
/// Each leg is adaptive Simpson quadrature, cumulative over the grid's sites, so a grid of
/// <c>n × m</c> sites costs <c>2(n + m + nm)</c> short integrals, never <c>nm</c> long ones.
/// </summary>
public static class LineIntegral
{
    private const int MaxDepth = 40;

    /// <summary><c>∫ f</c> from <paramref name="a"/> to <paramref name="b"/> to an absolute
    /// <paramref name="tolerance"/>, by adaptive Simpson quadrature. The tolerance is floored a little above rounding
    /// of the leg's own size: a zero tolerance (all-zero capacitance data) would otherwise only stop at
    /// <see cref="MaxDepth"/>, 2⁴⁰ evaluations later.</summary>
    public static double Integrate(Func<double, double> f, double a, double b, double tolerance)
    {
        if (a == b) return 0;
        double fa = f(a), fb = f(b), m = 0.5 * (a + b), fm = f(m);
        double whole = (b - a) / 6 * (fa + 4 * fm + fb);
        double floor = 1e-13 * Math.Abs(b - a) * (Math.Abs(fa) + Math.Abs(fm) + Math.Abs(fb));
        return Step(f, a, b, fa, fm, fb, whole, Math.Max(tolerance, floor), MaxDepth);
    }

    private static double Step(Func<double, double> f, double a, double b, double fa, double fm, double fb,
                               double whole, double tol, int depth)
    {
        double m = 0.5 * (a + b), lm = 0.5 * (a + m), rm = 0.5 * (m + b);
        double flm = f(lm), frm = f(rm);
        double left = (m - a) / 6 * (fa + 4 * flm + fm), right = (b - m) / 6 * (fm + 4 * frm + fb);
        double delta = left + right - whole;
        if (depth <= 0 || Math.Abs(delta) <= 15 * tol) return left + right + delta / 15;
        return Step(f, a, m, fa, flm, fm, left, tol / 2, depth - 1) + Step(f, m, b, fm, frm, fb, right, tol / 2, depth - 1);
    }

    /// <summary><c>∫ f</c> from <paramref name="from"/> to each of <paramref name="sites"/> (ascending), each leg between
    /// neighbouring sites to <paramref name="tolerance"/>.</summary>
    public static double[] Cumulative(Func<double, double> f, double from, IReadOnlyList<double> sites, double tolerance)
    {
        var result = new double[sites.Count];
        int first = 0;
        while (first < sites.Count && sites[first] < from) first++;
        double sum = 0, at = from;
        for (int k = first; k < sites.Count; k++)
        {
            sum += Integrate(f, at, sites[k], tolerance);
            result[k] = sum;
            at = sites[k];
        }
        (sum, at) = (0, from);
        for (int k = first - 1; k >= 0; k--)
        {
            sum += Integrate(f, at, sites[k], tolerance);
            result[k] = sum;
            at = sites[k];
        }
        return result;
    }

    /// <summary>
    /// Both paths' charge at every grid site, row-major over (<paramref name="vgs"/>, <paramref name="vds"/>) with
    /// <c>vds</c> fastest (the layout <see cref="Regrid.Through"/> and the table fit read), zero at
    /// <paramref name="anchor"/>. <paramref name="c1"/> is <c>∂Q/∂vgs</c> and <paramref name="c2"/> <c>∂Q/∂vds</c>.
    /// </summary>
    public static (double[] A, double[] B) OntoGrid(Func<double, double, double> c1, Func<double, double, double> c2,
                                                    (double Vgs, double Vds) anchor, double[] vgs, double[] vds,
                                                    double tolerance, RunControl? rc = null)
    {
        int ng = vgs.Length, nd = vds.Length;
        var a = new double[ng * nd];
        var b = new double[ng * nd];

        var alongGate = Cumulative(v => c1(v, anchor.Vds), anchor.Vgs, vgs, tolerance);
        for (int i = 0; i < ng; i++)
        {
            rc?.ThrowIfCancellationRequested();
            double g = vgs[i];
            var up = Cumulative(v => c2(g, v), anchor.Vds, vds, tolerance);
            for (int j = 0; j < nd; j++) a[i * nd + j] = alongGate[i] + up[j];
            rc?.TickStage();
        }

        var alongDrain = Cumulative(v => c2(anchor.Vgs, v), anchor.Vds, vds, tolerance);
        for (int j = 0; j < nd; j++)
        {
            rc?.ThrowIfCancellationRequested();
            double d = vds[j];
            var across = Cumulative(v => c1(v, d), anchor.Vgs, vgs, tolerance);
            for (int i = 0; i < ng; i++) b[i * nd + j] = alongDrain[j] + across[i];
            rc?.TickStage();
        }
        return (a, b);
    }
}

/// <summary>
/// [P1] eq. 6.27 as a number (brief-modelcomposer-8 R-mc8-5): the curl <c>∂C1/∂vds − ∂C2/∂vgs</c> of one charge row's
/// capacitances, by central differences of a smooth field, normalised by the typical magnitude of its two terms —
/// the RMS over the points of <c>√((t1² + t2²)/2)</c> — so a map value and the RMS read as fractions of how fast
/// the capacitances change at all, whatever their units.
/// </summary>
public static class CurlMetric
{
    /// <summary>The normalised curl at each point, its RMS over the points, and the typical term magnitude it was
    /// normalised by (0 when the field is flat, and then every curl is 0).</summary>
    public sealed record Result(double[] Map, double Rms, double Typical);

    public static Result Of(Func<double, double, (double C1, double C2)> field, IReadOnlyList<(double Vgs, double Vds)> at,
                            double hGate, double hDrain)
    {
        int n = at.Count;
        var curl = new double[n];
        double sumT = 0;
        for (int k = 0; k < n; k++)
        {
            var (g, d) = at[k];
            double t1 = (field(g, d + hDrain).C1 - field(g, d - hDrain).C1) / (2 * hDrain);
            double t2 = (field(g + hGate, d).C2 - field(g - hGate, d).C2) / (2 * hGate);
            curl[k] = t1 - t2;
            sumT += 0.5 * (t1 * t1 + t2 * t2);
        }
        double typical = n > 0 ? Math.Sqrt(sumT / n) : 0;
        if (!(typical > 0)) return new Result(new double[n], 0, 0);
        double sum = 0;
        for (int k = 0; k < n; k++)
        {
            curl[k] /= typical;
            sum += curl[k] * curl[k];
        }
        return new Result(curl, Math.Sqrt(sum / n), typical);
    }
}
