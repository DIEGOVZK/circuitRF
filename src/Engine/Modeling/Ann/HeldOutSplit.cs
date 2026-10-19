using CircuitRF.Engine.Optimization;

namespace CircuitRF.Engine.Modeling.Ann;

/// <summary>
/// The deterministic, stratified held-out split of R-mc6-5: which points the trainer never trains on.
///
/// <para><b>Spread over the domain, never by array order.</b> Each point's normalised coordinates are
/// quantised to 12 bits per axis and interleaved into a Morton (Z-order) key, which keeps points that
/// are near in the domain near in the key. The points are sorted by that key — ties broken by the exact
/// coordinates and then the targets, so the order is a function of the points' CONTENT and not of
/// their positions in the array — cut into as many contiguous strata as points are to be held out, and
/// one point is drawn from each stratum by the seeded generator. Every region of the domain therefore
/// gives up its share, and shuffling the input array changes nothing about which points are held out.</para>
/// </summary>
public static class HeldOutSplit
{
    private const int BitsPerAxis = 12;

    /// <param name="xHat">Each point's coordinates, normalised to about [−1, 1].</param>
    /// <param name="targets">Each point's targets, used only to order exact coordinate duplicates.</param>
    /// <returns>True for a held-out point, by the points' original index.</returns>
    public static bool[] Select(IReadOnlyList<double[]> xHat, IReadOnlyList<double[]> targets, double fraction, ulong seed)
    {
        int n = xHat.Count;
        var held = new bool[n];
        int count = (int)Math.Round(fraction * n, MidpointRounding.AwayFromZero);
        if (count <= 0 || n < 2) return held;
        count = Math.Min(count, n - 1);

        var keys = new ulong[n];
        for (int k = 0; k < n; k++) keys[k] = Morton(xHat[k]);
        var order = Enumerable.Range(0, n).ToArray();
        Array.Sort(order, (a, b) =>
        {
            int c = keys[a].CompareTo(keys[b]);
            if (c != 0) return c;
            c = Lexicographic(xHat[a], xHat[b]);
            return c != 0 ? c : Lexicographic(targets[a], targets[b]);
        });

        var rng = new SplitMix64(SplitMix64.Mix(seed ^ 0x6A09E667F3BCC909UL));
        for (int s = 0; s < count; s++)
        {
            int lo = (int)((long)s * n / count), hi = (int)((long)(s + 1) * n / count);
            held[order[lo + rng.Next(hi - lo)]] = true;
        }
        return held;
    }

    private static ulong Morton(double[] x)
    {
        int d = x.Length;
        const ulong top = (1UL << BitsPerAxis) - 1;
        Span<ulong> q = stackalloc ulong[d];
        for (int i = 0; i < d; i++)
        {
            double t = Math.Clamp((x[i] + 1.0) * 0.5, 0.0, 1.0);
            q[i] = Math.Min(top, (ulong)(t * (top + 1)));
        }
        ulong key = 0;
        for (int bit = BitsPerAxis - 1; bit >= 0; bit--)
            for (int i = 0; i < d; i++) key = (key << 1) | ((q[i] >> bit) & 1UL);
        return key;
    }

    private static int Lexicographic(double[] a, double[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }
}
