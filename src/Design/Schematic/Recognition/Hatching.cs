// A stroke drawn hatched, redrawn as the line it is before anything reads it — brief-imf-5-two-pin-and-device-symbols.md R-imf5-3.
//
// A textbook figure's vector export can draw a resistor's zig-zag in a patterned pen: the stroke becomes a run of short
// ticks across its path, each a few pixels long. Left as they are, the ticks are glyph-sized and evenly spaced — the
// text reader takes them for a line of type and lifts them, and the zig-zag is gone before a region is grown. So they
// are found first, on the normalised mask, and replaced by the line through them at the line work's own pen.
//
// A HATCH is a chain of at least MinTicks pieces, each linked to the next across a gap narrower than the pieces are long,
// nearly all of one size (the largest at most three times the smallest, the extremes trimmed), most of them elongated,
// 1.6 times as long as wide, and lying ACROSS the chain — a dashed line's dashes lie along it, and IMF-3 reads those. Type does not do that: a word is a handful of glyphs of different sizes and shapes,
// and a long line of type has word spaces in it wider than its glyphs.

using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Schematic.Recognition;

internal static class Hatching
{
    /// <summary>The fewest ticks a hatch has.</summary>
    public const int MinTicks = 12;

    /// <summary><paramref name="mask"/> with every hatch redrawn as the line it draws; the same image when it has none.
    /// <paramref name="w"/> is the line work's stroke width.</summary>
    public static BinaryImage Close(BinaryImage mask, double w, out int hatches)
    {
        hatches = 0;
        var labels = Components.Label(mask);
        double maxSide = Math.Max(6, 5 * w), maxArea = Math.Max(12, 6 * w * w);
        var small = labels.Components.Where(c => Math.Max(c.Right - c.Left, c.Bottom - c.Top) + 1 <= maxSide && c.Area <= maxArea)
                                     .ToDictionary(c => c.Label);
        if (small.Count < MinTicks) return mask;

        // Each small piece's long and short extents by its second moments.
        var moments = small.ToDictionary(kv => kv.Key, _ => (Sxx: 0.0, Syy: 0.0, Sxy: 0.0));
        for (int y = 0; y < labels.Height; y++)
            for (int x = 0; x < labels.Width; x++)
            {
                int l = labels.Labels[y * labels.Width + x];
                if (!small.TryGetValue(l, out var c)) continue;
                double dx = x + 0.5 - c.CentroidX, dy = y + 0.5 - c.CentroidY;
                var m = moments[l];
                moments[l] = (m.Sxx + dx * dx, m.Syy + dy * dy, m.Sxy + dx * dy);
            }
        // The long axis's direction, a unit vector.
        (double X, double Y) Axis(int l)
        {
            var (sxx, syy, sxy) = moments[l];
            double a = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            return (Math.Cos(a), Math.Sin(a));
        }
        (double Long, double Short) Extents(int l)
        {
            var (sxx, syy, sxy) = moments[l];
            int n = small[l].Area;
            double a = sxx / n, b = syy / n, cc = sxy / n, root = Math.Sqrt((a - b) * (a - b) / 4 + cc * cc);
            return (Math.Sqrt(12 * Math.Max(0, (a + b) / 2 + root)), Math.Sqrt(12 * Math.Max(0, (a + b) / 2 - root)));
        }

        // Link pieces across a gap narrower than the longer one is long; the chains are the linked groups.
        var list = small.Values.OrderBy(c => c.Left).ToList();
        var parent = list.ToDictionary(c => c.Label, c => c.Label);
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count && list[j].Left <= list[i].Right + maxSide; j++)
            {
                var (a, b) = (list[i], list[j]);
                double gx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right) - 1);
                double gy = Math.Max(0, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom) - 1);
                double reach = Math.Max(Extents(a.Label).Long, Extents(b.Label).Long);
                if (Math.Sqrt(gx * gx + gy * gy) < reach) parent[Find(a.Label)] = Find(b.Label);
            }

        var result = mask;
        foreach (var chain in list.GroupBy(c => Find(c.Label)).Select(g => g.ToList()).Where(g => g.Count >= MinTicks))
        {
            var areas = chain.Select(c => c.Area).Order().ToList();
            int trim = areas.Count / 10;
            if (areas[^(trim + 1)] > 3 * areas[trim]) continue;
            if (chain.Count(c => Extents(c.Label) is var e && e.Long >= 1.6 * Math.Max(1, e.Short)) < 0.7 * chain.Count) continue;
            // Ticks lie ACROSS the line they draw — a dashed line's dashes lie along it (IMF-3 reads those): each tick's
            // long axis at more than 40° to the way to its nearest neighbour, for most of them.
            int across = 0;
            foreach (var a in chain)
            {
                var b = chain.Where(o => o != a).MinBy(o => (o.CentroidX - a.CentroidX) * (o.CentroidX - a.CentroidX) + (o.CentroidY - a.CentroidY) * (o.CentroidY - a.CentroidY))!;
                double dx = b.CentroidX - a.CentroidX, dy = b.CentroidY - a.CentroidY, d = Math.Sqrt(dx * dx + dy * dy);
                var (ux, uy) = Axis(a.Label);
                if (d > 0 && Math.Abs(ux * dx + uy * dy) / d < Math.Cos(40 * Math.PI / 180)) across++;
            }
            if (across < 0.7 * chain.Count) continue;
            // Redrawn as the line through its ticks: the shortest tree joining their centres, each edge stroked at the line
            // work's own pen, the ticks themselves taken away — so the stroke reads at the weight of the wires it joins.
            var pts = chain.Select(c => (X: c.CentroidX, Y: c.CentroidY)).ToList();
            var inTree = new bool[pts.Count];
            var best = Enumerable.Repeat(double.MaxValue, pts.Count).ToArray();
            var from = new int[pts.Count];
            best[0] = 0;
            var edges = new List<(int A, int B)>();
            for (int step = 0; step < pts.Count; step++)
            {
                int u = -1;
                for (int i = 0; i < pts.Count; i++) if (!inTree[i] && (u < 0 || best[i] < best[u])) u = i;
                inTree[u] = true;
                if (step > 0) edges.Add((from[u], u));
                for (int i = 0; i < pts.Count; i++)
                {
                    double d = (pts[i].X - pts[u].X) * (pts[i].X - pts[u].X) + (pts[i].Y - pts[u].Y) * (pts[i].Y - pts[u].Y);
                    if (!inTree[i] && d < best[i]) { best[i] = d; from[i] = u; }
                }
            }
            if (ReferenceEquals(result, mask)) result = mask.Clone();
            var mine = chain.Select(c => c.Label).ToHashSet();
            foreach (var c in chain)
                for (int y = c.Top; y <= c.Bottom; y++)
                    for (int x = c.Left; x <= c.Right; x++)
                        if (mine.Contains(labels.Labels[y * labels.Width + x])) result.Pixels[y * result.Width + x] = 0;
            double half = Math.Max(0.5, w / 2);
            foreach (var (a, b) in edges)
            {
                var (ax, ay) = pts[a];
                var (bx, by) = pts[b];
                double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
                for (int y = Math.Max(0, (int)Math.Floor(Math.Min(ay, by) - half - 1)); y <= Math.Min(mask.Height - 1, (int)Math.Ceiling(Math.Max(ay, by) + half + 1)); y++)
                    for (int x = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - half - 1)); x <= Math.Min(mask.Width - 1, (int)Math.Ceiling(Math.Max(ax, bx) + half + 1)); x++)
                    {
                        double t = l2 < 1e-9 ? 0 : Math.Clamp(((x + 0.5 - ax) * dx + (y + 0.5 - ay) * dy) / l2, 0, 1);
                        double ex = ax + t * dx - (x + 0.5), ey = ay + t * dy - (y + 0.5);
                        if (ex * ex + ey * ey <= half * half) result.Pixels[y * result.Width + x] = 1;
                    }
            }
            hatches++;
        }
        return result;
    }
}
