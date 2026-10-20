// The equation templates of the current step (brief-modelcomposer-7 R-mc7-2): the Id forms of circuitRF's built-in FET
// families and a diode Ig, written as expressions in the intrinsic vgs and vds.
//
// They are COPIES of the laws in src/Core/Devices/Fet, not calls into those classes, so a fitted model is
// self-contained: its .cmodel carries the expression and its numbers, and a later change to a built-in model moves
// nothing that was exported. The built-ins' temperature coefficients and p-channel mirroring are not copied — a
// template is fitted at the data's one temperature (MC-9 adds temperature as an input), to an n-channel device.

namespace CircuitRF.Design.Modeling;

/// <summary>One parameter of a template: its start (estimated from the data where the form allows) and its bounds.
/// A logarithmic search coordinate is used when both bounds are positive and two decades or more apart.</summary>
public sealed record TemplateParameter(string Name, double Start, double Min, double Max, bool Lock = false);

/// <summary>The points a template's start is estimated from: intrinsic voltages, the target current, the temperature.</summary>
public sealed record TemplateData(double[] Vgs, double[] Vds, double[] Current, double TemperatureC);

/// <summary>An equation template: the expression of <c>Id</c>, or of the two gate branches, and its parameters.</summary>
public sealed record CurrentTemplate(string Name, string? Id, string? IgGs, string? IgGd,
                                     Func<TemplateData, IReadOnlyList<TemplateParameter>> Parameters)
{
    public bool IsGate => IgGs is not null;
}

public static class CurrentTemplates
{
    public const string Angelov = "Angelov", CurticeQuadratic = "CurticeQuadratic", CurticeCubic = "CurticeCubic",
                        Statz = "Statz", Materka = "Materka", Diode = "Diode";

    /// <summary>Every template by name: the five drain-current laws, then the gate diode.</summary>
    public static IReadOnlyList<CurrentTemplate> All { get; } =
    [
        new(Angelov,
            "Ipk*(1+tanh(P1*(vgs-Vpk)+P2*(vgs-Vpk)^2+P3*(vgs-Vpk)^3))*(1+Lambda*vds)*tanh(Alpha*vds)",
            null, null, AngelovStart),
        new(CurticeQuadratic,
            "if(vgs>Vto,Beta*(vgs-Vto)^2,0)*(1+Lambda*vds)*tanh(Alpha*vds)",
            null, null, d => QuadraticStart(d, statz: false)),
        new(CurticeCubic,
            "(A0+A1*vgs*(1+Beta*(Vds0-vds))+A2*(vgs*(1+Beta*(Vds0-vds)))^2+A3*(vgs*(1+Beta*(Vds0-vds)))^3)*tanh(Gamma*vds)",
            null, null, CubicStart),
        new(Statz,
            "if(vgs>Vto,Beta*(vgs-Vto)^2/(1+B*(vgs-Vto)),0)*if(vds>0,if(vds<3/Alpha,1-(1-Alpha*vds/3)^3,1),0)*(1+Lambda*vds)",
            null, null, d => QuadraticStart(d, statz: true)),
        new(Materka,
            "if(1-vgs/(Vp0+Gamma*vds)>0,Idss*(1-vgs/(Vp0+Gamma*vds))^2*tanh(Alpha*vds/(vgs-Vp0-Gamma*vds)),0)",
            null, null, MaterkaStart),
        new(Diode, null,
            "Is*(exp(vgs/(N*Vt))-1)",
            "Is*(exp((vgs-vds)/(N*Vt))-1)",
            DiodeStart),
    ];

    public static CurrentTemplate? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    public static IEnumerable<string> Names(bool gate) => All.Where(t => t.IsGate == gate).Select(t => t.Name);

    // ── Starts from the data ──────────────────────────────────────────────────

    /// <summary>The points of the top quarter of the drain-voltage range, by increasing vgs: the saturated transfer curve.</summary>
    private static (double[] Vgs, double[] I) Transfer(TemplateData d)
    {
        double lo = d.Vds.Min(), hi = d.Vds.Max();
        var idx = Enumerable.Range(0, d.Vgs.Length).Where(k => d.Vds[k] >= lo + 0.75 * (hi - lo))
                            .OrderBy(k => d.Vgs[k]).ToArray();
        return ([.. idx.Select(k => d.Vgs[k])], [.. idx.Select(k => d.Current[k])]);
    }

    private static double Largest(TemplateData d) => Math.Max(d.Current.Select(Math.Abs).DefaultIfEmpty(0).Max(), 1e-12);

    /// <summary>The lowest vgs on the transfer curve whose current exceeds 2 % of the largest.</summary>
    private static double Threshold(TemplateData d)
    {
        var (g, i) = Transfer(d);
        double imax = Largest(d);
        for (int k = 0; k < g.Length; k++) if (i[k] > 0.02 * imax) return g[k] - 0.1;
        return d.Vgs.Min();
    }

    /// <summary>The knee: <c>tanh(α·vk) = 0.9</c> at the lowest vds where the highest-vgs curve reaches 90 % of its largest current.</summary>
    private static double KneeAlpha(TemplateData d)
    {
        double gHi = d.Vgs.Max(), gSpan = gHi - d.Vgs.Min();
        var curve = Enumerable.Range(0, d.Vgs.Length).Where(k => d.Vgs[k] >= gHi - 0.1 * gSpan)
                              .OrderBy(k => d.Vds[k]).ToArray();
        if (curve.Length < 3) return 2.0;
        double top = curve.Max(k => d.Current[k]);
        foreach (int k in curve)
            if (d.Current[k] >= 0.9 * top && d.Vds[k] > 0) return Math.Clamp(1.47 / d.Vds[k], 0.05, 20);
        return 2.0;
    }

    private static IReadOnlyList<TemplateParameter> AngelovStart(TemplateData d)
    {
        var (g, i) = Transfer(d);
        double imax = Largest(d);
        double vpk = 0.5 * (d.Vgs.Min() + d.Vgs.Max()), ipk = 0.5 * imax, gmMax = 0;
        for (int k = 0; k + 1 < g.Length; k++)
        {
            double dv = g[k + 1] - g[k];
            if (dv < 1e-6) continue;
            double gm = (i[k + 1] - i[k]) / dv;
            if (gm > gmMax) { gmMax = gm; vpk = 0.5 * (g[k] + g[k + 1]); ipk = 0.5 * (i[k] + i[k + 1]); }
        }
        ipk = Math.Max(ipk, 1e-3 * imax);
        double p1 = gmMax > 0 ? Math.Clamp(gmMax / ipk, 0.05, 40) : 1.0;
        double gLo = d.Vgs.Min(), gHi = d.Vgs.Max();
        return
        [
            new("Ipk", ipk, 0, 10 * imax), new("Vpk", vpk, gLo - 1, gHi + 1), new("P1", p1, 0, 50),
            new("P2", 0, -50, 50), new("P3", 0, -50, 50), new("Alpha", KneeAlpha(d), 0.01, 50), new("Lambda", 0, -0.5, 0.5),
        ];
    }

    private static IReadOnlyList<TemplateParameter> QuadraticStart(TemplateData d, bool statz)
    {
        double vto = Threshold(d), imax = Largest(d), gHi = d.Vgs.Max();
        double beta = imax / Math.Max((gHi - vto) * (gHi - vto), 1e-3);
        var p = new List<TemplateParameter>
        {
            new("Vto", vto, d.Vgs.Min() - 2, gHi), new("Beta", beta, beta * 1e-3, beta * 1e3),
        };
        if (statz) p.Add(new("B", 0.3, 0, 10));
        p.Add(new("Alpha", KneeAlpha(d), 0.01, 50));
        p.Add(new("Lambda", 0, -0.5, 0.5));
        return p;
    }

    private static IReadOnlyList<TemplateParameter> CubicStart(TemplateData d)
    {
        // A cubic in vgs through the transfer curve by least squares, the coefficients' bounds ten times its scale.
        var (g, i) = Transfer(d);
        double[] a = g.Length >= 4 ? PolyFit(g, i, 3) : [0.5 * Largest(d), 0.1 * Largest(d), 0, 0];
        double s = Largest(d);
        return
        [
            new("A0", a[0], -10 * s, 10 * s), new("A1", a[1], -50 * s, 50 * s), new("A2", a[2], -50 * s, 50 * s),
            new("A3", a[3], -50 * s, 50 * s), new("Gamma", KneeAlpha(d), 0.01, 50), new("Beta", 0, -0.5, 0.5),
            new("Vds0", d.Vds.Max(), 0, 2 * d.Vds.Max() + 1, Lock: true),
        ];
    }

    private static IReadOnlyList<TemplateParameter> MaterkaStart(TemplateData d)
    {
        double vp = Math.Min(Threshold(d), -0.1), imax = Largest(d);
        return
        [
            new("Idss", imax, 1e-3 * imax, 10 * imax), new("Vp0", vp, d.Vgs.Min() - 5, -0.01),
            new("Gamma", 0, -0.5, 0.5), new("Alpha", 2.0, 0.01, 50),
        ];
    }

    private static IReadOnlyList<TemplateParameter> DiodeStart(TemplateData d)
    {
        double vt = 8.617333262e-5 * (d.TemperatureC + 273.15);
        return [new("Is", 1e-12, 1e-24, 1e-2), new("N", 1.5, 0.5, 30), new("Vt", vt, vt / 2, vt * 2, Lock: true)];
    }

    private static double[] PolyFit(double[] x, double[] y, int degree)
    {
        int m = degree + 1;
        var a = new double[m, m];
        var b = new double[m];
        for (int k = 0; k < x.Length; k++)
        {
            double pi = 1;
            for (int p = 0; p < m; p++, pi *= x[k])
            {
                b[p] += pi * y[k];
                double pj = 1;
                for (int q = 0; q < m; q++, pj *= x[k]) a[p, q] += pi * pj;
            }
        }
        for (int c = 0; c < m; c++)
        {
            int piv = c;
            for (int r = c + 1; r < m; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            if (Math.Abs(a[piv, c]) < 1e-300) return new double[m];
            for (int q = 0; q < m; q++) (a[c, q], a[piv, q]) = (a[piv, q], a[c, q]);
            (b[c], b[piv]) = (b[piv], b[c]);
            for (int r = c + 1; r < m; r++)
            {
                double l = a[r, c] / a[c, c];
                for (int q = c; q < m; q++) a[r, q] -= l * a[c, q];
                b[r] -= l * b[c];
            }
        }
        var coef = new double[m];
        for (int r = m - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int q = r + 1; q < m; q++) s -= a[r, q] * coef[q];
            coef[r] = s / a[r, r];
        }
        return coef;
    }
}
