// Thinning — brief-img-1-raster-core.md R-im1-6.
//
// Zhang–Suen: two sub-iterations per pass, each deleting the boundary pixels that satisfy its conditions all at once
// (decided on the pass's input, applied after), until a pass deletes nothing. Deciding on the input rather than as it
// goes is what makes the result independent of the order pixels are visited in.
//
// Spurs are pruned on the graph (SkeletonGraph), where a branch's length is known; here the skeleton is only made thin.

namespace CircuitRF.Design.Imaging;

public static class Skeleton
{
    /// <summary>The one-pixel-wide centre lines of <paramref name="mask"/>.</summary>
    /// <remarks>Each sub-iteration examines only the pixels a deletion has touched since that sub-iteration last looked
    /// at them: a pixel whose neighbourhood is unchanged decides as it did then. The result is the full scan's; the
    /// cost is the boundary's, not the picture's times the passes — a filled plane 1,000 px across takes 500 passes,
    /// and a full scan of a 4 MP picture per pass is minutes (IM-12).</remarks>
    public static BinaryImage Thin(BinaryImage mask)
    {
        int w = mask.Width, h = mask.Height;
        var img = mask.Clone();
        var px = img.Pixels;
        var del = new List<int>();
        var dirty = new[] { new List<int>(), new List<int>() };
        var flagged = new[] { new byte[px.Length], new byte[px.Length] };
        for (int i = 0; i < px.Length; i++)
            if (px[i] != 0)
                for (int s = 0; s < 2; s++) { dirty[s].Add(i); flagged[s][i] = 1; }

        while (dirty[0].Count > 0 || dirty[1].Count > 0)
            for (int sub = 0; sub < 2; sub++)
            {
                del.Clear();
                var these = dirty[sub];
                // Index order, so the work list never depends on the order deletions were found in.
                these.Sort();
                foreach (int i in these)
                {
                    flagged[sub][i] = 0;
                    if (px[i] == 0) continue;
                    int x = i % w, y = i / w;
                    // P2…P9 clockwise from north.
                    int p2 = img.At(x, y - 1) ? 1 : 0, p3 = img.At(x + 1, y - 1) ? 1 : 0, p4 = img.At(x + 1, y) ? 1 : 0;
                    int p5 = img.At(x + 1, y + 1) ? 1 : 0, p6 = img.At(x, y + 1) ? 1 : 0, p7 = img.At(x - 1, y + 1) ? 1 : 0;
                    int p8 = img.At(x - 1, y) ? 1 : 0, p9 = img.At(x - 1, y - 1) ? 1 : 0;
                    int b = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                    if (b < 2 || b > 6) continue;
                    int a = (p2 == 0 && p3 == 1 ? 1 : 0) + (p3 == 0 && p4 == 1 ? 1 : 0) + (p4 == 0 && p5 == 1 ? 1 : 0)
                          + (p5 == 0 && p6 == 1 ? 1 : 0) + (p6 == 0 && p7 == 1 ? 1 : 0) + (p7 == 0 && p8 == 1 ? 1 : 0)
                          + (p8 == 0 && p9 == 1 ? 1 : 0) + (p9 == 0 && p2 == 1 ? 1 : 0);
                    if (a != 1) continue;
                    if (sub == 0 ? (p2 * p4 * p6 != 0 || p4 * p6 * p8 != 0) : (p2 * p4 * p8 != 0 || p2 * p6 * p8 != 0)) continue;
                    del.Add(i);
                }
                these.Clear();
                foreach (int i in del) px[i] = 0;
                foreach (int i in del)
                {
                    int x = i % w, y = i / w;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                            int n = ny * w + nx;
                            if (px[n] == 0) continue;
                            for (int s = 0; s < 2; s++)
                                if (flagged[s][n] == 0) { flagged[s][n] = 1; dirty[s].Add(n); }
                        }
                }
            }
        return img;
    }
}
