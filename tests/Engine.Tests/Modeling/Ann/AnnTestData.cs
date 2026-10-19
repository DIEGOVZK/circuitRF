using CircuitRF.Engine.Optimization;

namespace CircuitRF.Engine.Tests.Modeling.Ann;

/// <summary>Scattered points for the MC-6 gates, drawn from a fixed seed.</summary>
internal static class AnnTestData
{
    public static double[][] Scatter(int count, ulong seed, params (double Lo, double Hi)[] box)
    {
        var rng = new SplitMix64(seed);
        var pts = new double[count][];
        for (int k = 0; k < count; k++)
            pts[k] = [.. box.Select(b => b.Lo + (b.Hi - b.Lo) * rng.NextDouble())];
        return pts;
    }

    public static double Rms(IEnumerable<double> errors)
    {
        var e = errors.ToArray();
        return Math.Sqrt(e.Sum(x => x * x) / e.Length);
    }

    public static double Range(IEnumerable<double> values)
    {
        var v = values.ToArray();
        return v.Max() - v.Min();
    }
}
