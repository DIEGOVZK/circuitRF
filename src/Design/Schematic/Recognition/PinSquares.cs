// A pin drawn as a small filled square on its lead, redrawn as the lead it sits on before anything reads the picture.
//
// Some schematic editors mark every pin with a filled square a few pens across — where a part's lead meets its wire, and
// at both ends of a shunt part's lead. Left in, the square is neither wire nor a junction dot: at a hairline drawing's
// thickened weight it is barely wider than the line, so it is read as a tiny part ("?n", a 0 Ω resistor in series), and
// one beside a ground's bars is grouped with them and the ground is read as a capacitor.
//
// A PIN SQUARE is a filled square at most 6 w on a side, deeper than a stroke of the line work's weight, wider than the
// lines leaving it by half again, standing clear of other ink, with exactly two lines leaving it, in line, out of
// opposite sides. It is redrawn as the one line it sits on. A mark with a third line is a junction and is left to be
// read as one (a T joins with or without it), and a filled triangle — an arrowhead, a diode's — runs on past the
// square inside it. A round dot on a straight wire is redrawn too, and loses nothing: it marks no junction there.

using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Schematic.Recognition;

internal static class PinSquares
{
    /// <summary><paramref name="mask"/> with every pin square redrawn as the line through it; the same image when it has
    /// none. <paramref name="w"/> is the line work's stroke width.</summary>
    public static BinaryImage Flatten(BinaryImage mask, double w, out int count)
    {
        count = 0;
        var dt = DistanceTransform.Compute(mask);
        // Deeper than a stroke of the line work's own weight, whose middle is w/2 from the paper; a square's centre is
        // half its side. Not much deeper: a drawing's leads can be thinner than its wires, and its pin squares then
        // only twice the wires' width.
        double depth = Math.Max(2, 0.65 * w);
        var core = new BinaryImage(mask.Width, mask.Height);
        bool any = false;
        for (int i = 0; i < dt.Length; i++)
            if (dt[i] >= depth) { core.Pixels[i] = 1; any = true; }
        if (!any) return mask;
        var result = mask;
        var labels = Components.Label(core);
        // Each core's deepest pixels: their centroid is the square's centre, their depth half its side — the core itself
        // runs out along the wires a little way, where they meet the square.
        var deepest = new (double Depth, double X, double Y, int N)[labels.Components.Count + 1];
        for (int i = 0; i < labels.Labels.Length; i++)
        {
            int l = labels.Labels[i];
            if (l == 0) continue;
            ref var d = ref deepest[l];
            if (dt[i] > d.Depth + 0.5) d = (dt[i], 0, 0, 0);
            if (dt[i] >= d.Depth - 0.5) d = (Math.Max(d.Depth, dt[i]), d.X + i % mask.Width, d.Y + i / mask.Width, d.N + 1);
        }
        foreach (var c in labels.Components)
        {
            var (radius, sx, sy, n) = deepest[c.Label];
            if (n == 0) continue;
            double mx = sx / n + 0.5, my = sy / n + 0.5;
            int x0 = (int)Math.Round(mx - radius), y0 = (int)Math.Round(my - radius), x1 = (int)Math.Round(mx + radius) - 1, y1 = (int)Math.Round(my + radius) - 1;
            int sw = x1 - x0 + 1, sh = y1 - y0 + 1;
            if (Math.Max(sw, sh) > 6 * w || Math.Max(sw, sh) > 1.3 * Math.Min(sw, sh)) continue;
            if (Rays(mask, x0, y0, x1, y1, w) is not { Count: 2 } rays || rays[0].Horizontal != rays[1].Horizontal) continue;
            // And it stands clear: two pixels out, paper all round but where its line passes and a tick — an
            // arrowhead's or a diode's filled triangle runs on past the square inside it.
            if (!Clear(mask, x0, y0, x1, y1, rays[0], w)) continue;
            // Wider than the line work's pen, and than any line leaving it, by half again: a glyph's thick joint is not.
            if (Math.Min(sw, sh) < Math.Max(5, 1.5 * Math.Max(w, rays.Max(r => r.Width)))) continue;
            int ink = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (mask.At(x, y)) ink++;
            if (ink < 0.84 * sw * sh) continue;
            if (ReferenceEquals(result, mask)) result = mask.Clone();
            for (int y = y0 - 2; y <= y1 + 2; y++)
                for (int x = x0 - 2; x <= x1 + 2; x++)
                    if ((uint)x < (uint)result.Width && (uint)y < (uint)result.Height) result.Pixels[y * result.Width + x] = 0;
            int cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            foreach (var r in rays)
            {
                // From where the line leaves the square to its centre, at the line's own width, and on past the centre
                // by that width so that the two meet. They are drawn as one band, the union of the two: a one-pixel jog
                // would cut the run a column of the lead makes, and a ground's lead with it.
                var (lo, hi) = (r.Lo, r.Hi);
                if (rays.FirstOrDefault(q => q.Horizontal == r.Horizontal && q.Side == -r.Side) is { Width: > 0 } across)
                    (lo, hi) = (Math.Min(lo, across.Lo), Math.Max(hi, across.Hi));
                int centre = r.Horizontal ? cx : cy, outer = r.Horizontal ? (r.Side < 0 ? x0 - 3 : x1 + 3) : (r.Side < 0 ? y0 - 3 : y1 + 3);
                int from = Math.Min(outer, centre - r.Side * r.Width), to = Math.Max(outer, centre - r.Side * r.Width);
                for (int a = from; a <= to; a++)
                    for (int t = lo; t <= hi; t++)
                    {
                        int x = r.Horizontal ? a : t, y = r.Horizontal ? t : a;
                        if ((uint)x < (uint)result.Width && (uint)y < (uint)result.Height) result.Pixels[y * result.Width + x] = 1;
                    }
            }
            count++;
        }
        return result;
    }

    /// <summary>A line leaving a square: out of a horizontal side (left −1, right +1) or a vertical one (top −1, bottom
    /// +1), its pixels across it <see cref="Lo"/> to <see cref="Hi"/>.</summary>
    private readonly record struct Ray(bool Horizontal, int Side, int Lo, int Hi)
    {
        public int Width => Hi - Lo + 1;
    }

    /// <summary>Whether the frame two pixels outside the square is paper but for the line's band where it leaves, and a
    /// tick's worth of pixels — the slanting mark some editors draw across a lead beside its pin square; a triangle's
    /// side is far more.</summary>
    private static bool Clear(BinaryImage mask, int x0, int y0, int x1, int y1, Ray line, double w)
    {
        int off = 0;
        for (int x = x0 - 2; x <= x1 + 2; x++)
            for (int y = y0 - 2; y <= y1 + 2; y++)
            {
                if (x != x0 - 2 && x != x1 + 2 && y != y0 - 2 && y != y1 + 2) continue;
                int across = line.Horizontal ? y : x;
                if (mask.At(x, y) && (across < line.Lo - 1 || across > line.Hi + 1)) off++;
            }
        return off <= 2 * w;
    }

    /// <summary>The lines leaving the square's four sides on its centre lines — a pin square is centred on its lead —
    /// each followed half the square's side out; null when one is wider than a line. A stray tick or speck beside the
    /// lead is no line out of the square.</summary>
    private static List<Ray>? Rays(BinaryImage mask, int x0, int y0, int x1, int y1, double w)
    {
        var rays = new List<Ray>();
        int cx = (x0 + x1) / 2, cy = (y0 + y1) / 2, reach = Math.Max(3, (Math.Min(x1 - x0, y1 - y0) + 1) / 2);
        foreach (var (horizontal, side) in new[] { (true, -1), (true, 1), (false, -1), (false, 1) })
        {
            int start = horizontal ? (side < 0 ? x0 - 2 : x1 + 2) : (side < 0 ? y0 - 2 : y1 + 2);
            bool Ink(int along, int across) => horizontal ? mask.At(along, across) : mask.At(across, along);
            int axis = horizontal ? cy : cx, inked = 0;
            for (int d = 0; d < reach; d++) if (Ink(start + side * d, axis)) inked++;
            if (inked < 0.8 * reach) continue;
            // Its width across, where it has left the square: the narrowest of a few, so that a tick crossing it there
            // does not widen it.
            var (wlo, whi) = (0, int.MaxValue / 2);
            for (int d = reach / 3; d < reach; d++)
            {
                int at = start + side * d, lo = axis, hi = axis;
                if (!Ink(at, axis)) continue;
                while (hi - lo < 4 * w && Ink(at, lo - 1)) lo--;
                while (hi - lo < 4 * w && Ink(at, hi + 1)) hi++;
                if (hi - lo < whi - wlo) (wlo, whi) = (lo, hi);
            }
            if (whi - wlo + 1 > 1.6 * w) return null;
            rays.Add(new Ray(horizontal, side, wlo, whi));
        }
        return rays;
    }
}
