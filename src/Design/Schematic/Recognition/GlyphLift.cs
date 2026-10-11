// Glyphs lifted off the symbols they touch — brief-imf-4-regions-and-touching-labels.md R-imf4-4 (overview F7).
//
// Before the symbol regions are grown, the residue (what is left when the words and the wires are taken away) is read
// once more for type: a designator touching its symbol joins the symbol's region and spoils its match — ladder's C₁ on
// the end of its plates, L₁ standing on the coil's humps, a heavy drawing's R₃ a pen's width off its box. A piece is a
// glyph when it is the text height h tall (0.7–2 h), glyph-shaped (IMF-2's test: no narrower than 0.3 of its height,
// a sixth to six tenths of its box ink) and lies in a row with another glyph — or carries a subscript below and to its
// right, 0.25–0.8 its height (one standing on a hump is cut at its foot), which is then part of its word. Inside a closed box (R-imf4-2) a pin name or number needs
// no row: everything inside an outline is the outline's detail, and its glyphs are words tagged as being inside.
//
// A piece is one residue component, or a component CUT where it meets a symbol stroke at one short contact:
//  * a straight run longer than 1.5 h inside it is a symbol's stroke, never a letter's (a plate, a bar) — without its
//    runs the glyph touching their end is a component of its own;
//  * otherwise its skeleton's BRIDGES — an edge whose removal leaves a glyph-sized part on one side: a letter touching a
//    coil's hump hangs off the hump's skeleton by the one stroke that touches it.
// No piece a wire end reaches is lifted: a terminal's circle, a hop's half arc, a coil's end hump are a symbol's. A
// capacitor's plate is never glyph-shaped (it is too thin), and a coil's humps hang off nothing — each of them carries
// a wire end on both sides of any cut.

using Clipper2Lib;
using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Schematic.Recognition;

internal static class GlyphLift
{
    /// <summary>A glyph-sized piece of the residue: a whole component, or the part of one beyond a cut.</summary>
    /// <param name="Cut">The component it was cut from (a symbol stroke ran through it); −1 for a whole component.</param>
    private sealed record Piece(List<int> Pixels, PixelBox Box, bool Inside, int Cut = -1);

    /// <summary>The words lifted off <paramref name="residue"/> — whose pixels are painted out of it — read pixels.</summary>
    /// <param name="h">The text height, read pixels.</param>
    /// <param name="count">How many glyphs were lifted.</param>
    public static List<TextRegion> Lift(BinaryImage residue, WireGraph wires, IReadOnlyList<TextRegion> words,
                                        IReadOnlyList<OutlineBox> outlines, double h, double s, double w, SchematicImageOptions o, out int count)
    {
        count = 0;
        int W = residue.Width;
        var lab = Components.Label(residue);
        var px = new List<int>[lab.Components.Count];
        for (int k = 0; k < px.Length; k++) px[k] = [];
        for (int i = 0; i < lab.Labels.Length; i++)
            if (lab.Labels[i] > 0) px[lab.Labels[i] - 1].Add(i);

        double reach = o.Attach * w + w / 2 + 1;
        var ends = wires.Ends.Select(e => (e.X, e.Y)).ToList();
        bool NearEnd(PixelBox b) => ends.Any(e => b.Distance(e.X, e.Y) <= reach);
        var closed = outlines.Where(ob => ob.Orthogonal).ToList();
        bool InBox(PixelBox b) => closed.Any(ob => b.Left >= ob.X0 - w && b.Right <= ob.X1 + w && b.Top >= ob.Y0 - w && b.Bottom <= ob.Y1 + w);
        bool IsOutline(PixelBox b) => outlines.Any(ob => ob.X0 >= b.Left - 2 * w && ob.X1 <= b.Right + 1 + 2 * w && ob.Y0 >= b.Top - 2 * w
                                                         && ob.Y1 <= b.Bottom + 1 + 2 * w && ob.X1 - ob.X0 >= 0.6 * b.Width && ob.Y1 - ob.Y0 >= 0.6 * b.Height);

        var pieces = new List<Piece>();
        for (int k = 0; k < px.Length; k++)
        {
            var c = lab.Components[k];
            var box = new PixelBox(c.Left, c.Top, c.Right, c.Bottom);
            if (Fits(box, h))
            {
                if (!NearEnd(box)) pieces.Add(new Piece(px[k], box, InBox(box)));
                continue;
            }
            if (IsOutline(box)) continue;
            foreach (var (p, b) in Cut(px[k], box, W, h, s, w, NearEnd))
                if (!NearEnd(b)) pieces.Add(new Piece(p, b, InBox(b), k));
        }
        if (pieces.Count == 0) return [];

        // Which pieces are glyphs, and which glyphs are one word.
        int n = pieces.Count;
        var shaped = pieces.Select(p => Shaped(p, W, h)).ToArray();
        var narrow = pieces.Select(p => Narrow(p, W, h)).ToArray();
        var wordGlyphs = words.SelectMany(t => t.Glyphs).ToList();
        var root = Enumerable.Range(0, n).ToArray();
        int F(int x) { while (root[x] != x) x = root[x] = root[root[x]]; return x; }
        var accepted = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (pieces[i].Inside && (shaped[i] || narrow[i]) && pieces[i].Box.Height >= 0.5 * h) accepted[i] = true;
            if (!shaped[i]) continue;
            if (wordGlyphs.Any(g => Row(pieces[i].Box, g))) accepted[i] = true;
            for (int j = 0; j < n; j++)
            {
                if (j == i) continue;
                if ((shaped[j] || narrow[j]) && Row(pieces[i].Box, pieces[j].Box))
                {
                    accepted[i] = accepted[j] = true;
                    root[F(i)] = F(j);
                }
                else if ((shaped[j] || narrow[j] || Script(pieces[j], W)) && Subscript(pieces[i].Box, pieces[j].Box))
                {
                    accepted[i] = accepted[j] = true;
                    root[F(i)] = F(j);
                }
            }
        }

        // A lone letter cut off the symbol it touched (R-imf7-10): a textbook's C tucked against its plates, which the cut
        // at the plates' runs leaves in pieces — so the pieces a quarter of h apart or less are tried together, as one
        // glyph of the text height.
        // Only pieces cut from one component are tried together: a scatter of separate small components — a hatched
        // zig-zag's ticks — is no letter however its box reads.
        var free = Enumerable.Range(0, n).Where(i => !accepted[i] && !pieces[i].Inside && pieces[i].Box.Height <= 1.5 * h
                                                     && pieces[i].Cut >= 0).ToList();
        var lone = new HashSet<int>();
        var near = free.ToDictionary(i => i, i => i);
        int R(int x) { while (near[x] != x) x = near[x] = near[near[x]]; return x; }
        foreach (int i in free)
            foreach (int j in free)
                if (i < j && pieces[i].Cut == pieces[j].Cut && pieces[i].Box.Distance(pieces[j].Box) <= 0.25 * h) near[R(i)] = R(j);
        foreach (var cluster in free.GroupBy(R).Select(g => g.ToList()))
        {
            var box = cluster.Select(i => pieces[i].Box).Aggregate((a, b) => a.Union(b));
            if (box.Height < 0.7 * h || box.Height > 1.5 * h || box.Width > 1.2 * h) continue;
            if (!TextRegions.LoneLetter([.. cluster.SelectMany(i => pieces[i].Pixels)], box, W)) continue;
            foreach (int i in cluster) { accepted[i] = true; root[F(i)] = F(cluster[0]); }
            lone.Add(cluster[0]);
        }

        var lifted = new List<TextRegion>();
        foreach (var g in Enumerable.Range(0, n).Where(i => accepted[i]).GroupBy(F))
        {
            var members = g.OrderBy(i => pieces[i].Box.Left).ToList();
            var ink = members.SelectMany(i => pieces[i].Pixels).ToList();
            var box = members.Select(i => pieces[i].Box).Aggregate((a, b) => a.Union(b));
            foreach (int i in ink) residue.Pixels[i] = 0;
            count += members.Count;
            bool letter = members.Any(i => lone.Contains(i));
            lifted.Add(TextRegions.Region(ink, box, W, false, letter ? [box] : [.. members.Select(i => pieces[i].Box)], [], o.NormaliseBoldType) with
            {
                Lifted = true,
                Place = members.All(i => pieces[i].Inside) ? TextPlace.InsideBox : TextPlace.Free,
            });
        }
        return lifted;
    }

    /// <summary>The tallest a glyph stands, in text heights. The brief's 1.4 h; 2 h measured: where every capital touches
    /// its symbol the profile measures h on the glyphs standing free — on ladder-dashed its subscripts — and the italic
    /// capitals there stand 1.5–1.9 h.</summary>
    private const double MaxGlyph = 2.0;

    /// <summary>Small enough to be one glyph: no taller than MaxGlyph h, no wider than 1.6 h.</summary>
    private static bool Fits(PixelBox b, double h) => b.Height <= MaxGlyph * h + 1 && b.Width <= 1.6 * h + 1;

    private static double Fill(Piece p) => p.Pixels.Count / (double)(p.Box.Width * p.Box.Height);

    private static double Aspect(PixelBox b) => Math.Min(b.Width, b.Height) / (double)Math.Max(b.Width, b.Height);

    /// <summary>A glyph's shape (IMF-2's test) at the text height: 0.7–2 h tall, no narrower than 0.3 of its height, a
    /// sixth to six tenths of its box ink.</summary>
    private static bool Shaped(Piece p, int width, double h) =>
        p.Box.Height >= 0.7 * h && p.Box.Height <= MaxGlyph * h + 1 && Aspect(p.Box) >= 0.3 && Fill(p) is >= 0.15 and <= 0.6;

    /// <summary>A narrow glyph — a 1, an l, an I — of the text height: it only joins a row.</summary>
    private static bool Narrow(Piece p, int width, double h) =>
        p.Box.Height >= 0.7 * h && p.Box.Height <= MaxGlyph * h + 1 && p.Box.Width < p.Box.Height && Aspect(p.Box) >= 0.08 && Fill(p) >= 0.15;

    /// <summary>Shaped like a glyph at any size — a subscript's.</summary>
    private static bool Script(Piece p, int width) => Aspect(p.Box) >= 0.08 && p.Box.Width <= 1.5 * p.Box.Height && Fill(p) is >= 0.15 and <= 0.75;

    /// <summary>Two glyphs side by side on one line: of a height within 1.6 of each other, overlapping across over half the
    /// shorter, a letter's gap apart along it.</summary>
    private static bool Row(PixelBox a, PixelBox b)
    {
        double ha = a.Height, hb = b.Height, hi = Math.Max(ha, hb), lo = Math.Min(ha, hb);
        if (hi > 1.6 * lo) return false;
        double overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) + 1;
        if (overlap < 0.5 * lo) return false;
        double gap = Math.Max(b.Left - a.Right - 1, a.Left - b.Right - 1);
        return gap >= -0.2 * hi && gap <= 0.6 * hi;
    }

    /// <summary><paramref name="s"/> is <paramref name="b"/>'s subscript: 0.25–0.8 its height (a subscript standing on
    /// a coil's hump is cut at its foot), starting at its right side
    /// (an italic base overhangs it), its foot at or below the base's and its top well below the base's.</summary>
    private static bool Subscript(PixelBox b, PixelBox s)
    {
        double hb = b.Height;
        if (s.Height < 0.25 * hb || s.Height > 0.8 * hb) return false;
        if (s.Left < b.Right - 0.4 * hb || s.Left > b.Right + 0.8 * hb) return false;
        return s.Bottom >= b.Bottom - 0.15 * hb && s.Bottom <= b.Bottom + 0.6 * hb && s.Top >= b.Top + 0.3 * hb && s.Top <= b.Bottom - 0.1 * hb;
    }

    /// <summary>The glyph-sized parts of a component too large to be one glyph, cut off where they meet a symbol stroke.
    /// <paramref name="anchored"/> says whether a part is held by what the symbol is held by (a wire end reaching it):
    /// a part hanging off the skeleton is a candidate only beside one that is.</summary>
    private static IEnumerable<(List<int> Pixels, PixelBox Box)> Cut(List<int> pixels, PixelBox box, int width, double h, double s, double w,
                                                                       Func<PixelBox, bool> anchored)
    {
        // A straight run longer than 1.5 h and 0.6 s is a symbol's stroke (a plate, a bar is a symbol scale long): what is
        // left without it falls into pieces. Not shorter: a letter's foot lying along a coil's humps makes a run of its own.
        int ox = box.Left - 1, oy = box.Top - 1, cw = box.Width + 2, ch = box.Height + 2;
        var crop = new BinaryImage(cw, ch);
        foreach (int i in pixels) crop.Pixels[(i / width - oy) * cw + (i % width - ox)] = 1;
        var runs = TextRegions.LongRuns(crop, (int)Math.Ceiling(Math.Max(1.5 * h, 0.6 * s)));
        bool anyRun = false;
        for (int i = 0; i < crop.Pixels.Length; i++)
            if (runs.Pixels[i] != 0) { crop.Pixels[i] = 0; anyRun = true; }
        var parts = new List<(List<int> Crop, PixelBox Box)>();
        if (anyRun)
        {
            var lab = Components.Label(crop);
            var lists = new List<int>[lab.Components.Count];
            for (int k = 0; k < lists.Length; k++) lists[k] = [];
            for (int i = 0; i < lab.Labels.Length; i++) if (lab.Labels[i] > 0) lists[lab.Labels[i] - 1].Add(i);
            for (int k = 0; k < lists.Length; k++)
            {
                var c = lab.Components[k];
                parts.Add((lists[k], new PixelBox(c.Left, c.Top, c.Right, c.Bottom)));
            }
        }
        else parts.Add(([.. Enumerable.Range(0, crop.Pixels.Length).Where(i => crop.Pixels[i] != 0)], new PixelBox(1, 1, cw - 2, ch - 2)));

        int Back(int i) => (i / cw + oy) * width + (i % cw + ox);
        PixelBox Shift(PixelBox b) => new(b.Left + ox, b.Top + oy, b.Right + ox, b.Bottom + oy);
        foreach (var (part, pb) in parts)
        {
            if (Fits(pb, h))
            {
                if (anyRun) yield return ([.. part.Select(Back)], Shift(pb));
                continue;
            }
            foreach (var (p, b) in Bridges(part, cw, ch, h, w, b => anchored(Shift(b)))) yield return ([.. p.Select(Back)], Shift(b));
        }
    }

    /// <summary>The box of skeleton pixels widened by half a pen each way.</summary>
    private static PixelBox Widened(List<(int X, int Y)> skel, double w)
    {
        int m = (int)Math.Ceiling(w / 2);
        return new PixelBox(skel.Min(p => p.X) - m, skel.Min(p => p.Y) - m, skel.Max(p => p.X) + m, skel.Max(p => p.Y) + m);
    }

    /// <summary>The glyph-sized parts of one piece (pixel indices in a <paramref name="cw"/> × <paramref name="ch"/> crop)
    /// hanging off the rest of its skeleton by a single edge.</summary>
    private static IEnumerable<(List<int> Pixels, PixelBox Box)> Bridges(List<int> part, int cw, int ch, double h, double w,
                                                                          Func<PixelBox, bool> nearEnd)
    {
        var img = new BinaryImage(cw, ch);
        foreach (int i in part) img.Pixels[i] = 1;
        var g = SkeletonGraph.Build(img, new SkeletonGraphOptions { StrokeWidth = w, MaxThreads = 1 });
        int nn = g.Nodes.Count, ne = g.Edges.Count;
        if (ne < 2) yield break;
        var adj = new List<(int To, int Edge)>[nn];
        for (int v = 0; v < nn; v++) adj[v] = [];
        foreach (var e in g.Edges)
        {
            if (e.From == e.To) continue;
            adj[e.From].Add((e.To, e.Id));
            adj[e.To].Add((e.From, e.Id));
        }
        // Bridges (Tarjan).
        var disc = new int[nn];
        var low = new int[nn];
        Array.Fill(disc, -1);
        int time = 0;
        var bridge = new bool[ne];
        void Dfs(int u, int via)
        {
            disc[u] = low[u] = time++;
            foreach (var (v, e) in adj[u])
            {
                if (e == via) continue;
                if (disc[v] < 0)
                {
                    Dfs(v, e);
                    low[u] = Math.Min(low[u], low[v]);
                    if (low[v] > disc[u]) bridge[e] = true;
                }
                else low[u] = Math.Min(low[u], disc[v]);
            }
        }
        for (int v = 0; v < nn; v++) if (disc[v] < 0) Dfs(v, -1);

        // The branches hanging off each node by a bridge — what lies beyond the bridge seen from the node — and whether a
        // wire end reaches them. The branches of one node that no wire end reaches, together, are a candidate when some
        // branch of that node is reached (the symbol they hang off) and they are glyph-sized: a letter touching a symbol
        // at its corner hangs off it by two strokes, its stem and its foot.
        var branches = new List<(int J, HashSet<int> Nodes, List<(int X, int Y)> Skeleton, bool Free)>();
        for (int e = 0; e < ne; e++)
        {
            if (!bridge[e]) continue;
            var edge = g.Edges[e];
            foreach (var (j, start) in new[] { (edge.From, edge.To), (edge.To, edge.From) })
            {
                var nodes = new HashSet<int> { start };
                var stack = new Stack<int>();
                stack.Push(start);
                while (stack.Count > 0)
                    foreach (var (v, ve) in adj[stack.Pop()])
                        if (ve != e && nodes.Add(v)) stack.Push(v);
                var skel = g.Edges.Where(x => x.Id != e && nodes.Contains(x.From) && nodes.Contains(x.To)).SelectMany(x => x.Pixels).ToList();
                // The bridge itself is the branch's, but for its last pen at the node.
                var at = g.Nodes[j];
                skel.AddRange(edge.Pixels.Where(p => Math.Sqrt((p.X + 0.5 - at.X) * (p.X + 0.5 - at.X) + (p.Y + 0.5 - at.Y) * (p.Y + 0.5 - at.Y)) > w));
                if (skel.Count == 0) continue;
                branches.Add((j, nodes, skel, !nearEnd(Widened(skel, w))));
            }
        }
        var sides = new List<(HashSet<int> Nodes, List<(int X, int Y)> Skeleton)>();
        foreach (var at in branches.GroupBy(b => b.J))
        {
            var free = at.Where(b => b.Free).ToList();
            if (free.Count == 0 || free.Count == at.Count()) continue;
            var skel = free.SelectMany(b => b.Skeleton).ToList();
            var box = Widened(skel, w);
            if (box.Height < 0.5 * h || box.Height > MaxGlyph * h + w || box.Width > 1.6 * h + w) continue;
            sides.Add(([.. free.SelectMany(b => b.Nodes)], skel));
        }
        if (sides.Count == 0) yield break;

        // The largest sides first, none overlapping another taken.
        var taken = new List<(HashSet<int> Nodes, List<(int X, int Y)> Skeleton)>();
        foreach (var s in sides.OrderByDescending(s => s.Skeleton.Count))
            if (!taken.Any(t => t.Nodes.Overlaps(s.Nodes))) taken.Add(s);

        // Each pixel goes to the skeleton it is nearest, through the ink: a taken side's, or the rest's.
        var label = new int[cw * ch];
        Array.Fill(label, -1);
        var queue = new Queue<int>();
        for (int t = 0; t < taken.Count; t++)
            foreach (var (x, y) in taken[t].Skeleton)
                if (label[y * cw + x] < 0) { label[y * cw + x] = t; queue.Enqueue(y * cw + x); }
        foreach (var e in g.Edges)
            foreach (var (x, y) in e.Pixels)
                if (img.Pixels[y * cw + x] != 0 && label[y * cw + x] < 0) { label[y * cw + x] = taken.Count; queue.Enqueue(y * cw + x); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(), x = i % cw, y = i / cw;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                    int j = ny * cw + nx;
                    if (img.Pixels[j] == 0 || label[j] >= 0) continue;
                    label[j] = label[i];
                    queue.Enqueue(j);
                }
        }
        for (int t = 0; t < taken.Count; t++)
        {
            var mine = part.Where(i => label[i] == t).ToList();
            if (mine.Count == 0) continue;
            int l = int.MaxValue, tp = int.MaxValue, r = int.MinValue, b = int.MinValue;
            foreach (int i in mine) { l = Math.Min(l, i % cw); r = Math.Max(r, i % cw); tp = Math.Min(tp, i / cw); b = Math.Max(b, i / cw); }
            yield return (mine, new PixelBox(l, tp, r, b));
        }
    }
}
