// What an S-parameter block says about itself before anything is fitted to it (brief-modelcomposer-12 R-mc12-2): its
// port count, reciprocity, passivity, symmetry, low-frequency asymptotes, self-resonances and a vector fit of Y (or Z),
// each reported in one line. The asymptotes and resonances are what a topology's starting values come from
// (PassiveTopologies); the vector fit's poles say how many resonances the block has, which is what a topology is
// chosen by.

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Design.Modeling.Passive;

/// <summary>One N-port block: its S on one frequency axis at one real reference, and — in a family — its geometry value.</summary>
public sealed record PassiveBlock(string Name, double[] FrequenciesHz, double Z0, Complex[][,] S)
{
    /// <summary>The geometry value this block was measured at (<c>turns = 2.5</c>); NaN for a single block.</summary>
    public double Geometry { get; init; } = double.NaN;

    public int Ports => S[0].GetLength(0);

    /// <summary>Every block a passive data set holds, one per source, each carrying its geometry value.</summary>
    public static IReadOnlyList<PassiveBlock> From(NormalisedData d)
    {
        if (d.S is null || d.FrequenciesHz is null) throw new ModelingException("data", "a passive data set holds S-parameters; this one holds none.");
        int nf = d.FrequenciesHz.Length, n = d.Ports;
        var list = new List<PassiveBlock>();
        for (int p = 0; p < d.Count; p++)
        {
            if (d.Points[p].Excluded) continue;
            var s = new Complex[nf][,];
            for (int k = 0; k < nf; k++)
            {
                s[k] = new Complex[n, n];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                        s[k][i, j] = d.SAt(p, k, i, j);
            }
            list.Add(new PassiveBlock(d.Points[p].PointId, d.FrequenciesHz, d.Z0, s) { Geometry = d.Points[p].Geometry });
        }
        return list;
    }
}

/// <summary>A lightly damped pole pair of the vector fit: its natural frequency and quality factor.</summary>
public sealed record PassiveResonance(double FrequencyHz, double Q);

/// <summary>What <see cref="PassiveAnalysis.Run"/> found, and its one line per finding.</summary>
public sealed record PassiveAnalysisResult
{
    public required PassiveBlock Block { get; init; }
    public int Ports => Block.Ports;
    /// <summary><c>max |Sij − Sji|</c> over the band; NaN for a one-port.</summary>
    public double Reciprocity { get; init; }
    /// <summary>The largest eigenvalue of <c>SᴴS</c> over the band (σmax²); above 1 is not passive.</summary>
    public double Passivity { get; init; }
    /// <summary>The frequencies where <c>SᴴS</c> has an eigenvalue above 1.</summary>
    public IReadOnlyList<double> NonPassiveHz { get; init; } = [];
    /// <summary><c>max |S11 − S22|</c> (port order reversed for N &gt; 2); NaN for a one-port.</summary>
    public double Symmetry { get; init; }
    /// <summary>Admittance and impedance at every frequency; an entry is NaN where the matrix is singular.</summary>
    public required Complex[][,] Y { get; init; }
    public required Complex[][,] Z { get; init; }
    /// <summary>The upward zero crossings of the principal reactance (Z for a one-port, the π model's series branch
    /// <c>−1/Y21</c> otherwise): the self-resonant frequencies.</summary>
    public IReadOnlyList<double> SelfResonancesHz { get; init; } = [];
    /// <summary>The downward zero crossings of the same reactance: the parallel (anti-) resonances — an inductor's
    /// self-resonance is one of these.</summary>
    public IReadOnlyList<double> ParallelResonancesHz { get; init; } = [];
    public required VectorFitResult Fit { get; init; }
    /// <summary><c>Y</c> or <c>Z</c> — which the vector fit is of.</summary>
    public required string FitOf { get; init; }
    public IReadOnlyList<PassiveResonance> Resonances { get; init; } = [];
    public IReadOnlyList<string> Lines { get; init; } = [];

    // ── The asymptotes and helpers topologies seed from ──────────────────────

    public double[] F => Block.FrequenciesHz;
    public double W(int k) => 2 * Math.PI * Block.FrequenciesHz[k];
    public int Last => Block.FrequenciesHz.Length - 1;

    /// <summary>The principal impedance at sample k: Z for a one-port, the π series branch <c>−1/Y21</c> otherwise.</summary>
    public Complex Series(int k) => Ports == 1 ? Z[k][0, 0] : -1 / Y[k][1, 0];

    /// <summary>The π model's shunt admittance at port p (0-based): the row sum of Y.</summary>
    public Complex Shunt(int k, int p)
    {
        Complex sum = 0;
        for (int j = 0; j < Ports; j++) sum += Y[k][p, j];
        return sum;
    }

    /// <summary>Series R, L (or series C) and the shunt capacitances at the lowest frequency.</summary>
    public double LowR => Series(0).Real;
    public double LowL => Series(0).Imaginary / W(0);
    public double LowSeriesC => -1 / (W(0) * Series(0).Imaginary);
    public double LowShuntC(int p) => Shunt(0, p).Imaginary / W(0);
    public double HighL => Series(Last).Imaginary / W(Last);
    public double HighShuntC(int p) => Shunt(Last, p).Imaginary / W(Last);

    public double? SeriesResonanceHz => SelfResonancesHz.Count > 0 ? SelfResonancesHz[0] : null;
    public double? ParallelResonanceHz => ParallelResonancesHz.Count > 0 ? ParallelResonancesHz[0] : null;

    /// <summary>The resistance where the principal impedance is smallest (a series resonance's R) and largest (a
    /// parallel resonance's).</summary>
    public double RAtMinSeries => Series(Enumerable.Range(0, F.Length).MinBy(k => Finite(Series(k).Magnitude, double.MaxValue))).Real;
    public double RAtMaxSeries => Series(Enumerable.Range(0, F.Length).MaxBy(k => Finite(Series(k).Magnitude, 0))).Real;

    /// <summary>
    /// A two-port read as a uniform line at sample k: from ABCD (out of Y), <c>θ = acos(Re A)</c> and
    /// <c>Z0 = √(B/C)</c> — pads ignored, which is what a starting value may do.
    /// </summary>
    public (double Z0, double Theta) LineEstimate(int k)
    {
        var y = Y[k];
        Complex a = -y[1, 1] / y[1, 0], b = -1 / y[1, 0], c = -(y[0, 0] * y[1, 1] - y[0, 1] * y[1, 0]) / y[1, 0];
        double theta = Math.Acos(Math.Clamp(a.Real, -1, 1));
        return (Complex.Sqrt(b / c).Real, theta);
    }

    private static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;
}

public static class PassiveAnalysis
{
    /// <summary>The vector fit stops at the first order whose relative error is at most this.</summary>
    public const double DefaultFitTolerance = 1e-3;
    public const int DefaultMaxOrder = 12;
    /// <summary>An eigenvalue of SᴴS this far above 1 is not passive (measurement noise sits below it).</summary>
    public const double PassivityTolerance = 1e-6;

    /// <summary>Analyses one block (R-mc12-2). Cancellable between vector-fit orders.</summary>
    public static PassiveAnalysisResult Run(PassiveBlock b, RunControl? rc = null)
    {
        int nf = b.FrequenciesHz.Length, n = b.Ports;
        if (nf < 3) throw new ModelingException(b.Name, "it has fewer than three frequencies; there is nothing to analyse.");
        var lines = new List<string> { n == 1 ? "1 port" : $"{n} ports" };

        // Reciprocity, passivity, symmetry.
        double recip = n == 1 ? double.NaN : 0, worst = 0, sym = n == 1 ? double.NaN : 0;
        var nonPassive = new List<double>();
        var y = new Complex[nf][,];
        var z = new Complex[nf][,];
        for (int k = 0; k < nf; k++)
        {
            var m = ToMat(b.S[k]);
            if (n > 1) recip = Math.Max(recip, TouchstoneHealth.ReciprocityError(m));
            double sigma = RFNetwork.Passivity(m);
            double ev = sigma * sigma;
            worst = Math.Max(worst, ev);
            if (ev > 1 + PassivityTolerance) nonPassive.Add(b.FrequenciesHz[k]);
            if (n > 1)
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                        sym = Math.Max(sym, (b.S[k][i, j] - b.S[k][n - 1 - i, n - 1 - j]).Magnitude);
            y[k] = Convert(() => RFNetwork.SToY(m, new Complex(b.Z0, 0)), n);
            z[k] = Convert(() => RFNetwork.SToZ(m, new Complex(b.Z0, 0)), n);
        }
        if (n > 1) lines.Add($"{(recip <= 1e-6 ? "Reciprocal" : "Not reciprocal")}: max |Sij − Sji| = {E(recip)}");
        lines.Add(nonPassive.Count == 0
            ? $"Passive: largest eigenvalue of SᴴS {G(worst)}"
            : $"Not passive: largest eigenvalue of SᴴS {G(worst)}, above 1 at {nonPassive.Count} frequenc{(nonPassive.Count == 1 ? "y" : "ies")} ({Ranges(nonPassive)})");
        if (n > 1) lines.Add($"{(sym <= 1e-6 ? "Symmetric" : "Not symmetric")}: max |{(n == 2 ? "S11 − S22" : "Sij − S(N+1−i)(N+1−j)")}| = {E(sym)}");

        // The vector fit, of Y unless Y does not exist or Z fits better.
        var fit = FitBest(b.FrequenciesHz, y, z, n, rc, out string fitOf);
        var resonances = fit.Model.Poles
            .Where(p => p.Imaginary > 0)
            .Select(p => new PassiveResonance(p.Magnitude / (2 * Math.PI), p.Magnitude / (2 * Math.Abs(p.Real))))
            .Where(r => r.Q >= 0.5 && r.FrequencyHz >= b.FrequenciesHz[0] / 2 && r.FrequencyHz <= 2 * b.FrequenciesHz[^1])
            .OrderBy(r => r.FrequencyHz)
            .ToList();

        var result = new PassiveAnalysisResult
        {
            Block = b, Reciprocity = recip, Passivity = worst, NonPassiveHz = nonPassive, Symmetry = sym,
            Y = y, Z = z, Fit = fit, FitOf = fitOf, Resonances = resonances,
        };

        // Asymptotes and self-resonance.
        var low = new List<string> { $"series R {G(result.LowR)} Ω" };
        low.Add(result.LowL > 0 ? $"L {GU(result.LowL, "H")}" : $"C {GU(result.LowSeriesC, "F")}");
        var shunts = Enumerable.Range(0, n == 1 ? 1 : n).Select(p => result.LowShuntC(p)).ToList();
        string shunt = n == 1
            ? (shunts[0] > 0 ? $"; shunt C {GU(shunts[0], "F")}" : "")
            : "; shunt C " + string.Join(", ", shunts.Select(c => GU(c, "F")));
        lines.Add($"Low frequency: {string.Join(", ", low)}{shunt}");

        var x = Enumerable.Range(0, nf).Select(k => result.Series(k)).ToArray();
        var srf = new List<double>();
        var prf = new List<double>();
        for (int k = 1; k < nf; k++)
        {
            double x0 = x[k - 1].Imaginary, x1 = x[k].Imaginary;
            if (x0 < 0 && x1 >= 0 && PassiveMetrics.SelfResonance([b.FrequenciesHz[k - 1], b.FrequenciesHz[k]], [x[k - 1], x[k]]) is { } f0)
                srf.Add(f0);
            // A downward crossing is the same interpolation on the negated reactance.
            if (x0 > 0 && x1 <= 0 && PassiveMetrics.SelfResonance([b.FrequenciesHz[k - 1], b.FrequenciesHz[k]],
                                                                  [-x[k - 1], -x[k]]) is { } fp)
                prf.Add(fp);
        }
        var res = new List<string>();
        if (srf.Count > 0) res.Add($"series {string.Join(", ", srf.Select(Hz))}");
        if (prf.Count > 0) res.Add($"parallel {string.Join(", ", prf.Select(Hz))}");
        lines.Add(res.Count == 0 ? "No self-resonance in the band" : $"Self-resonance: {string.Join("; ", res)}");
        string poles = resonances.Count == 0 ? "no resonance in the band"
                     : "poles at " + string.Join(", ", resonances.Select(r => $"{Hz(r.FrequencyHz)} (Q {G(r.Q)})"));
        lines.Add($"Vector fit of {fitOf}: order {fit.Order}, relative error {E(fit.RelativeError)}; {poles}");

        return result with { SelfResonancesHz = srf, ParallelResonancesHz = prf, Lines = lines };
    }

    private static VectorFitResult FitBest(double[] f, Complex[][,] y, Complex[][,] z, int n, RunControl? rc, out string of)
    {
        static Complex[][]? Responses(Complex[][,] m, int n)
        {
            var r = new Complex[n * n][];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    r[i * n + j] = [.. m.Select(x => x[i, j])];
                    if (r[i * n + j].Any(c => !double.IsFinite(c.Real) || !double.IsFinite(c.Imaginary))) return null;
                }
            return r;
        }
        var ry = Responses(y, n);
        var rz = Responses(z, n);
        VectorFitResult? fy = ry is null ? null : VectorFit.FitByError(f, ry, DefaultFitTolerance, DefaultMaxOrder, rc: rc);
        VectorFitResult? fz = rz is null || fy is { RelativeError: <= DefaultFitTolerance }
            ? null : VectorFit.FitByError(f, rz, DefaultFitTolerance, DefaultMaxOrder, rc: rc);
        if (fy is null && fz is null) throw new ModelingException("data", "neither Y nor Z exists at every frequency; there is nothing to vector-fit.");
        if (fz is not null && (fy is null || fz.RelativeError < fy.RelativeError)) { of = "Z"; return fz; }
        of = "Y";
        return fy!;
    }

    internal static Mat<Complex> ToMat(Complex[,] a)
    {
        int n = a.GetLength(0);
        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = a[i, j];
        return m;
    }

    private static Complex[,] Convert(Func<Mat<Complex>> f, int n)
    {
        var r = new Complex[n, n];
        try
        {
            var m = f();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    r[i, j] = m[i, j];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    r[i, j] = new Complex(double.NaN, double.NaN);
        }
        return r;
    }

    private static string Ranges(List<double> f) =>
        f.Count == 1 ? Hz(f[0]) : $"{Hz(f[0])} to {Hz(f[^1])}";

    internal static string Hz(double f) =>
        f >= 1e9 ? $"{G(f / 1e9)} GHz" : f >= 1e6 ? $"{G(f / 1e6)} MHz" : f >= 1e3 ? $"{G(f / 1e3)} kHz" : $"{G(f)} Hz";

    /// <summary>A value in engineering notation with its unit: <c>1.98 nH</c>.</summary>
    internal static string GU(double v, string unit)
    {
        if (v == 0 || !double.IsFinite(v)) return $"{G(v)} {unit}";
        (double Scale, string Prefix)[] p = [(1e-15, "f"), (1e-12, "p"), (1e-9, "n"), (1e-6, "µ"), (1e-3, "m"), (1, ""), (1e3, "k"), (1e6, "M")];
        var pick = p.LastOrDefault(x => Math.Abs(v) >= x.Scale * 0.9999);
        if (pick.Prefix is null) pick = p[0];
        return $"{G(v / pick.Scale)} {pick.Prefix}{unit}";
    }

    internal static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
    private static string E(double v) => v.ToString("0.0E+0", CultureInfo.InvariantCulture);
}
