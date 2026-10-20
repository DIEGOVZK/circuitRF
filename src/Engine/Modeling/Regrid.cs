using RfCore.Loadpull;

namespace CircuitRF.Engine.Modeling;

/// <summary>
/// Scattered intrinsic points onto the rectilinear grid a table constituent needs (R-mc7-2). Intrinsic
/// points do not lie on a grid ([P1] Fig. 6.5), so a table is built by evaluating a smooth function of the
/// scattered data at grid sites: a trained network (preferred — [P1] §6.2.7), or a radial-basis
/// interpolant through the points themselves.
/// </summary>
public static class Regrid
{
    /// <summary>
    /// Sites from <paramref name="lo"/> at exactly <paramref name="step"/>, up to the first site at or beyond
    /// <paramref name="hi"/> — so the grid covers [lo, hi] and the spacing is the one asked for. At least
    /// two sites.
    /// </summary>
    public static double[] Axis(double lo, double hi, double step)
    {
        if (!(step > 0) || !double.IsFinite(step))
            throw new ArgumentOutOfRangeException(nameof(step), "the grid step is a positive number.");
        if (!(hi >= lo)) throw new ArgumentException("the axis runs from lo up to hi.");
        int n = Math.Max(2, (int)Math.Ceiling((hi - lo) / step - 1e-9) + 1);
        var sites = new double[n];
        for (int k = 0; k < n; k++) sites[k] = lo + k * step;
        return sites;
    }

    /// <summary><paramref name="f"/> at every grid site, row-major over (vgs, vds), vds fastest.</summary>
    public static double[] Through(Func<double, double, double> f, double[] vgs, double[] vds)
    {
        var values = new double[vgs.Length * vds.Length];
        for (int i = 0; i < vgs.Length; i++)
            for (int j = 0; j < vds.Length; j++)
                values[i * vds.Length + j] = f(vgs[i], vds[j]);
        return values;
    }

    /// <summary>
    /// A multiquadric interpolant through the scattered points, each coordinate mapped onto [0, 1] over the
    /// points first so a volt of vgs and a volt of vds weigh what their spans make them weigh. Exact at the
    /// points (no smoothing).
    /// </summary>
    public static Func<double, double, double> Rbf(IReadOnlyList<(double Vgs, double Vds)> points, IReadOnlyList<double> values)
    {
        if (points.Count != values.Count || points.Count < 3)
            throw new ArgumentException("a radial-basis interpolant needs at least three points, one value each.");
        double gLo = points.Min(p => p.Vgs), gHi = points.Max(p => p.Vgs);
        double dLo = points.Min(p => p.Vds), dHi = points.Max(p => p.Vds);
        double gs = gHi > gLo ? 1 / (gHi - gLo) : 1, ds = dHi > dLo ? 1 / (dHi - dLo) : 1;
        var x = points.Select(p => (p.Vgs - gLo) * gs).ToArray();
        var y = points.Select(p => (p.Vds - dLo) * ds).ToArray();
        var rbf = new Rbf2D(x, y, values.ToArray(), RbfKernel.Multiquadric, smooth: 0.0);
        return (vgs, vds) => rbf.Evaluate((vgs - gLo) * gs, (vds - dLo) * ds);
    }
}
