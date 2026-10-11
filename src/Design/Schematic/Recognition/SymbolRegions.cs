// The symbols of a schematic picture, as regions with the wire ends that reach them —
// brief-img-7-schematic-wires-and-regions.md R-im7-5 (hops), R-im7-6, R-im7-7.
//
// A symbol region is what is left when the words and the wires are taken away: the residue's pieces, grown by Grow w,
// then MERGED when two face each other across a gap shorter than the larger one's extent along the faces — which
// joins a capacitor's two plates, a ground's bars and an inductor's separate humps into one region. Two pieces with a
// wire in the gap between them are never merged: that gap is a lead, and the pieces are two parts.
//
// One region is one part (brief-imf-4-regions-and-touching-labels.md): with the symbol scale s known, the gap is also
// under 1 s, no wire end arrives at either piece from the gap's side, the pair is no more than 4 s across its shorter
// side and gets no more than four wire ends unless both halves already have three, and the closest pair merges first.
// A closed outline or an envelope circle takes in what lies inside it and nothing beside it. A region that still runs
// away — more than eight wire ends and no outline, two outlines, two capacitors, two two-pin parts side by side — is cut
// at its widest gaps.
//
// A region's attachments are the wire ends stopping within Attach w of it, each with the direction its wire arrives
// in. Three shapes of region are not symbols:
//  * a HOP — two attachments, collinear and opposite, a few w apart, with another wire passing between them: the two
//    wire halves are one wire, crossing the other without connecting (R-im7-5);
//  * a SUPPLY MARK — one attachment, and a short bar across the end or an arrow whose apex the wire reaches (R-im7-7);
//  * a DECORATION — no attachment at all (a logo, a frame, a title block's lines), reported and dropped.
// A word at a dangling end, in line with its wire, is that wire's NET LABEL.

using Clipper2Lib;
using CircuitRF.Design.Imaging;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>Where a wire meets a symbol region: the wire's end node — −1 for a lead that ends in paper, an open pin
/// (R-imf5-4) — where it is, and the direction the wire arrives in.</summary>
public sealed record SymbolAttachment(int Node, double X, double Y, double DirX, double DirY);

/// <summary>One symbol on the picture, not yet named (IM-8 names it).</summary>
/// <param name="Mask">The region's own pixels, cropped to <paramref name="Box"/>.</param>
public sealed record SymbolRegion(int Id, PixelBox Box, int Area, IReadOnlyList<SymbolAttachment> Attachments, BinaryImage Mask)
{
    /// <summary>The large filled areas inside it — a shaded triangle, a filled box — kept as fills, not ink (IMF-2,
    /// R-imf2-4): only their rim is in <see cref="Mask"/>.</summary>
    public IReadOnlyList<PixelBox> Fills { get; init; } = [];
}

/// <summary>A word naming the net of the wire ending at <paramref name="Node"/> (R-im7-7).</summary>
/// <param name="Text">The word's <see cref="TextRegion.Id"/>.</param>
public sealed record NetLabel(int Node, int Text);

public enum SupplyShape { Bar, Arrow }

/// <summary>A supply mark at the end of the wire ending at <paramref name="Node"/>; the word beside it names its net.</summary>
public sealed record SupplyMark(int Node, PixelBox Box, SupplyShape Shape, int? Text);

/// <summary>A residue piece group before it is known what it is.</summary>
internal sealed record RawRegion(PixelBox Box, List<int> Pixels);

/// <summary>What the regions are grown in (R-imf4-1 … R-imf4-5): the symbol scale s, read pixels, the closed outlines
/// the wire reading set aside, and the centres of its plate pairs (a capacitor each).</summary>
internal sealed record RegionScales(double Symbol, IReadOnlyList<OutlineBox> Outlines, IReadOnlyList<PointD> PlatePairs)
{
    /// <summary>The envelope circles (R-imf4-3).</summary>
    public IReadOnlyList<EnvelopeCircle> Circles { get; init; } = [];
}

/// <summary>A circle round a part (R-imf4-3), read pixels.</summary>
internal sealed record EnvelopeCircle(double X, double Y, double R, PixelBox Box)
{
    /// <summary>The point lies on or inside it, within a pen of its outline.</summary>
    public bool Holds(PointD p, double w) => WireGraph.Dist(p, new PointD(X, Y)) <= R + w;
}

/// <summary>Why two pieces were not merged (R-imf4-1, R-imf4-2).</summary>
internal enum MergeRefusal { None, Gap, Lead, Size, Pins, Outline }

/// <summary>What the region growing did, for the report (R-imf4-6).</summary>
internal sealed class RegionCounts
{
    public int Boxes, Triangles, Circles, Merged, RefusedGap, RefusedLead, RefusedSize, RefusedPins, RefusedOutline, Split, SplitInto,
               Blocks, Glyphs;
}

internal sealed record AttachResult(List<SymbolRegion> Regions, List<PixelBox> Decorations, List<NetLabel> Labels,
                                    List<SupplyMark> Supplies, List<int> Dangling);

internal static class SymbolRegions
{
    /// <summary>The residue: <paramref name="mask"/> without the wires of <paramref name="wires"/> and the junction
    /// dots on them, and without specks.</summary>
    public static BinaryImage Residue(BinaryImage mask, WireGraph wires, IReadOnlyList<JunctionBlob> blobs, double w)
    {
        int W = mask.Width, H = mask.Height;
        var r = mask.Clone();
        foreach (var s in wires.Segments)
        {
            double rad = Math.Max(w, s.Width) / 2 + 1;
            // At a bare end the stroke's own cap is wire too; at any other end the next stroke starts there.
            double before = wires.Nodes[s.From].IsEnd && wires.Nodes[s.From].Free ? rad : 0;
            double after = wires.Nodes[s.To].IsEnd && wires.Nodes[s.To].Free ? rad : 0;
            double pad = 2 * rad;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(s.A.x, s.B.x) - pad)), x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(s.A.x, s.B.x) + pad));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(s.A.y, s.B.y) - pad)), y1 = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(s.A.y, s.B.y) + pad));
            double len = s.Length;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var (along, perp) = WireGraph.Local(s.A, s.B, new PointD(x + 0.5, y + 0.5));
                    if (along >= -before && along <= len + after && Math.Abs(perp) <= rad) r.Pixels[y * W + x] = 0;
                }
        }
        foreach (var b in blobs)
        {
            if (!wires.Nodes.Any(n => n.Degree >= 3 && WireGraph.Dist(new PointD(n.X, n.Y), b.Centre) <= b.Radius + w)) continue;
            double rad = b.Radius + 1;
            for (int y = Math.Max(0, (int)(b.Centre.y - rad)); y <= Math.Min(H - 1, (int)(b.Centre.y + rad)); y++)
                for (int x = Math.Max(0, (int)(b.Centre.x - rad)); x <= Math.Min(W - 1, (int)(b.Centre.x + rad)); x++)
                    if (WireGraph.Dist(new PointD(x + 0.5, y + 0.5), b.Centre) <= rad) r.Pixels[y * W + x] = 0;
        }
        return Morphology.Despeckle(r, Math.Max(4, (int)Math.Round(1.5 * w * w)));
    }

    /// <summary>The residue's pieces, grown and merged into regions (R-im7-6, R-imf4-1 … R-imf4-5).</summary>
    /// <param name="scales">The symbol scale and what the wire reading closed; null reads as IM-7 did (every rule off but
    /// the facing gap and the wire in it).</param>
    public static List<RawRegion> Group(BinaryImage residue, WireGraph wires, double w, SchematicImageOptions o,
                                        RegionScales? scales = null, RegionCounts? counts = null)
    {
        int W = residue.Width;
        var grown = Morphology.Dilate(residue, o.Grow * w, o.MaxThreads);
        var lab = Components.Label(grown);
        int n = lab.Components.Count;
        var pixels = new List<int>[n];
        for (int k = 0; k < n; k++) pixels[k] = [];
        for (int i = 0; i < residue.Pixels.Length; i++)
            if (residue.Pixels[i] != 0) pixels[lab.Labels[i] - 1].Add(i);
        var pieces = pixels.Where(p => p.Count > 0).Select(p => new RawRegion(BoxOf(p, W), p)).ToList();
        n = pieces.Count;
        var boxes = pieces.Select(p => p.Box).ToArray();
        var alive = Enumerable.Repeat(true, n).ToArray();
        var members = Enumerable.Range(0, n).Select(k => new List<int> { k }).ToArray();
        counts ??= new RegionCounts();

        // What each piece is before anything merges: its wire ends, whether it is a closed outline (R-imf4-2) or an
        // envelope circle (R-imf4-3) — a region that takes in what lies inside it and nothing beside it.
        var ends = Attachments(pieces, W, wires, w, o);
        var pins0 = ends.Select(l => l.ToList()).ToArray();
        var pins = ends.Select(l => l.Count).ToArray();
        var at = pins.ToArray();
        var closed = new PixelBox?[n];
        if (scales is not null)
            for (int k = 0; k < n; k++)
            {
                if (Outline(boxes[k], scales.Outlines, w) is { } ob)
                {
                    closed[k] = new PixelBox((int)Math.Floor(ob.X0), (int)Math.Floor(ob.Y0), (int)Math.Ceiling(ob.X1), (int)Math.Ceiling(ob.Y1));
                    if (ob.Orthogonal) counts.Boxes++; else counts.Triangles++;
                }
                // Every piece inside an envelope circle is the circle's: its outline is often cut into arcs where its
                // leads cross it.
                else if (scales.Circles.FirstOrDefault(c => Inside(boxes[k], c.Box, 2 * w)) is { } circle) closed[k] = circle.Box;
            }
        if (scales is not null)
            counts.Circles = scales.Circles.Count(c => closed.Any(b => b == c.Box));

        // Merged closest first (R-imf4-1): a capacitor's two plates join before either joins the part beside it, so a
        // runaway's cut (R-imf4-5) falls between parts, not through one.
        var refused = new Dictionary<(int, int), MergeRefusal>();
        while (true)
        {
            (int I, int J, double Gap) best = (-1, -1, double.MaxValue);
            for (int i = 0; i < n; i++)
            {
                if (!alive[i]) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (!alive[j]) continue;
                    double? gap;
                    if (scales is null) gap = Facing(boxes[i], boxes[j], wires) ? 0 : null;
                    else
                    {
                        if (boxes[i].Distance(boxes[j]) >= scales.Symbol) continue;
                        (gap, var why) = Merge(i, j);
                        if (why != MergeRefusal.None) refused[(i, j)] = why;
                        else refused.Remove((i, j));
                    }
                    if (gap is { } g && g < best.Gap) best = (i, j, g);
                    if (scales is null && gap is not null) break;
                }
                if (scales is null && best.I >= 0) break;
            }
            if (best.I < 0) break;
            var (bi, bj, _) = best;
            members[bi].AddRange(members[bj]);
            boxes[bi] = boxes[bi].Union(boxes[bj]);
            at[bi] += at[bj];
            ends[bi].AddRange(ends[bj]);
            closed[bi] ??= closed[bj];
            alive[bj] = false;
            counts.Merged++;
        }
        foreach (var ((i, j), why) in refused)
        {
            if (!alive[i] || !alive[j]) continue;
            switch (why)
            {
                case MergeRefusal.Gap: counts.RefusedGap++; break;
                case MergeRefusal.Lead: counts.RefusedLead++; break;
                case MergeRefusal.Size: counts.RefusedSize++; break;
                case MergeRefusal.Pins: counts.RefusedPins++; break;
                case MergeRefusal.Outline: counts.RefusedOutline++; break;
            }
        }

        // Whether i and j are one part, and the gap between them; null and why when they are two (R-imf4-1, R-imf4-2).
        (double? Gap, MergeRefusal Why) Merge(int i, int j)
        {
            var (a, b) = (boxes[i], boxes[j]);
            // A closed outline takes in what lies inside it, and nothing outside merges into it.
            if (closed[i] is not null || closed[j] is not null)
            {
                if (closed[i] is { } oi && Inside(b, oi, w)) return (0, MergeRefusal.None);
                if (closed[j] is { } oj && Inside(a, oj, w)) return (0, MergeRefusal.None);
                return (null, FacingGap(a, b, wires) is not null ? MergeRefusal.Outline : MergeRefusal.None);
            }
            if (Inside(a, b, 0) || Inside(b, a, 0)) return (0, MergeRefusal.None);
            if (FacingGap(a, b, wires) is not { } face) return (null, MergeRefusal.None);
            // Shorter than a symbol scale, and still shorter than the larger one's extent along the faces, as IM-7 had it:
            // a label beside a small part is no part of it.
            if (face.Gap >= Math.Min(scales!.Symbol, face.Extent)) return (null, MergeRefusal.Gap);
            if (LeadFacing(ends[i], a, face.Side) || LeadFacing(ends[j], b, (-face.Side.X, -face.Side.Y))) return (null, MergeRefusal.Lead);
            var u = a.Union(b);
            if (Math.Min(u.Width, u.Height) > MaxRegionScales * scales.Symbol) return (null, MergeRefusal.Size);
            if (at[i] + at[j] > MaxMergedPins && (at[i] < 3 || at[j] < 3)) return (null, MergeRefusal.Pins);
            return (face.Gap, MergeRefusal.None);
        }

        var r = new List<RawRegion>();
        for (int k = 0; k < n; k++)
        {
            if (!alive[k]) continue;
            if (scales is null)
            {
                var all = members[k].SelectMany(m => pieces[m].Pixels).OrderBy(i => i).ToList();
                r.Add(new RawRegion(boxes[k], all));
                continue;
            }
            // A runaway region (R-imf4-5) is cut back along its weakest merges.
            foreach (var part in Recut(members[k], counts))
            {
                var px = part.SelectMany(m => pieces[m].Pixels).OrderBy(i => i).ToList();
                r.Add(new RawRegion(BoxOf(px, W), px));
            }
        }
        return [.. r.OrderBy(x => x.Box.Top).ThenBy(x => x.Box.Left)];

        // The pieces of one region, split at the widest gaps between them until no part is a runaway: more than eight
        // wire ends and no single closed outline, more than one outline or capacitor (a plate pair) inside it, or two
        // two-pin parts side by side.
        List<List<int>> Recut(List<int> region, RegionCounts c)
        {
            var edges = new List<(int A, int B, double Gap)>();
            for (int x = 0; x < region.Count; x++)
                for (int y = x + 1; y < region.Count; y++)
                {
                    var (a, b) = (pieces[region[x]].Box, pieces[region[y]].Box);
                    if (Inside(a, b, w) || Inside(b, a, w)) edges.Add((region[x], region[y], 0));
                    else if (FacingGap(a, b, wires) is { } f && f.Gap < scales!.Symbol) edges.Add((region[x], region[y], f.Gap));
                }
            edges = [.. edges.OrderBy(e => e.Gap)];
            bool Runaway(List<int> part)
            {
                var box = part.Select(m => pieces[m].Box).Aggregate((p, q) => p.Union(q));
                int ends = part.Sum(m => pins[m]);
                bool closedHere = part.Any(m => closed[m] is not null);
                int outlines = Clusters(scales!.Outlines.Where(ob => Inside(ob, box, w)).ToList(), w);
                int pairs = scales.PlatePairs.Count(p => box.Distance(p.x, p.y) == 0);
                return (ends > MaxRegionPins && !closedHere) || outlines > 1 || pairs > 1
                       || (!closedHere && TwoAxes(part.SelectMany(m => pins0[m]).ToList(), scales.Symbol, w));
            }
            List<List<int>> Parts(List<(int A, int B, double Gap)> es)
            {
                var root = region.ToDictionary(m => m, m => m);
                int F(int x) { while (root[x] != x) x = root[x] = root[root[x]]; return x; }
                foreach (var e in es) root[F(e.A)] = F(e.B);
                return [.. region.GroupBy(F).Select(g => g.ToList())];
            }
            if (!Runaway(region)) return [region];
            var parts = Parts(edges);
            while (edges.Count > 0 && parts.Any(Runaway))
            {
                edges.RemoveAt(edges.Count - 1);
                parts = Parts(edges);
            }
            if (parts.Count > 1) { c.Split++; c.SplitInto += parts.Count; }
            if (parts.Any(Runaway)) c.Blocks++;
            return parts;
        }
    }

    /// <summary>Two two-pin parts side by side (R-imf4-5): two pairs of wire ends, each pair arriving from opposite sides on
    /// one line, on parallel lines at least 1.5 s apart. A transformer's two coils stand closer.</summary>
    private static bool TwoAxes(List<SymbolAttachment> ends, double s, double w)
    {
        var axes = new List<(bool Vertical, double At, int A, int B)>();
        for (int i = 0; i < ends.Count; i++)
            for (int j = i + 1; j < ends.Count; j++)
            {
                var (a, b) = (ends[i], ends[j]);
                if (a.DirX * b.DirX + a.DirY * b.DirY > -0.9) continue;
                bool vertical = Math.Abs(a.DirY) >= Math.Abs(a.DirX);
                double off = vertical ? Math.Abs(a.X - b.X) : Math.Abs(a.Y - b.Y);
                if (off > 1.5 * w) continue;
                axes.Add((vertical, vertical ? (a.X + b.X) / 2 : (a.Y + b.Y) / 2, i, j));
            }
        for (int i = 0; i < axes.Count; i++)
            for (int j = i + 1; j < axes.Count; j++)
            {
                var (p, q) = (axes[i], axes[j]);
                if (p.Vertical != q.Vertical || p.A == q.A || p.A == q.B || p.B == q.A || p.B == q.B) continue;
                if (Math.Abs(p.At - q.At) >= 1.5 * s) return true;
            }
        return false;
    }

    /// <summary>How many outlines there are, counting outlines that touch as one — an IC's pin-number boxes on its
    /// sides are its own.</summary>
    private static int Clusters(List<OutlineBox> outlines, double w)
    {
        var root = Enumerable.Range(0, outlines.Count).ToArray();
        int F(int x) { while (root[x] != x) x = root[x] = root[root[x]]; return x; }
        for (int i = 0; i < outlines.Count; i++)
            for (int j = i + 1; j < outlines.Count; j++)
            {
                var (a, b) = (outlines[i], outlines[j]);
                if (a.X0 <= b.X1 + 2 * w && b.X0 <= a.X1 + 2 * w && a.Y0 <= b.Y1 + 2 * w && b.Y0 <= a.Y1 + 2 * w) root[F(i)] = F(j);
            }
        return Enumerable.Range(0, outlines.Count).Select(F).Distinct().Count();
    }

    /// <summary>The widest a region's box may be on its shorter side, in symbol scales (R-imf4-1).</summary>
    private const double MaxRegionScales = 4;

    /// <summary>A merge giving a region more wire ends than this is refused unless both halves have three or more.</summary>
    private const int MaxMergedPins = 4;

    /// <summary>A region with more wire ends than this is a runaway unless it is one closed box (R-imf4-5).</summary>
    private const int MaxRegionPins = 8;

    private static bool Inside(PixelBox i, PixelBox o, double tol) =>
        i.Left >= o.Left - tol && i.Right <= o.Right + tol && i.Top >= o.Top - tol && i.Bottom <= o.Bottom + tol;

    private static bool Inside(OutlineBox i, PixelBox o, double tol) =>
        i.X0 >= o.Left - tol && i.X1 <= o.Right + 1 + tol && i.Y0 >= o.Top - tol && i.Y1 <= o.Bottom + 1 + tol;

    /// <summary>The closed outline a piece is (R-imf4-2): one of the wire reading's outlines lying in the piece's box and
    /// filling most of it — the piece's extent beyond it is only what is drawn on it (a pin-number box).</summary>
    private static OutlineBox? Outline(PixelBox piece, IReadOnlyList<OutlineBox> outlines, double w)
    {
        foreach (var ob in outlines)
        {
            if (!Inside(ob, piece, 2 * w)) continue;
            if (ob.X1 - ob.X0 < 0.6 * piece.Width || ob.Y1 - ob.Y0 < 0.6 * piece.Height) continue;
            return ob;
        }
        return null;
    }

    /// <summary>The envelope circles of the residue (R-imf4-3): circles 1–4 s across round which the ink of
    /// <paramref name="ink"/> runs (85 % of the way, within a pen), with ink inside — a transistor, a source with its
    /// mark — found from a piece's box or from a circle fitted through an arc of one. A terminal's circle is smaller and
    /// empty. Brief's 3–8 s, measured: the symbol scale is a plate's length, and the corpus' circles are 1.6–2.8 of
    /// them.</summary>
    public static List<EnvelopeCircle> Envelopes(BinaryImage residue, BinaryImage ink, double s, double w)
    {
        var r = new List<EnvelopeCircle>();
        var lab = Components.Label(residue);
        int W = residue.Width;
        foreach (var c in lab.Components)
        {
            // The piece is the circle with what is drawn in it, or — where a short run of its outline was taken for wire —
            // an arc of it: as wide as the circle across one way, and lying against one side of it the other.
            var pb = new PixelBox(c.Left, c.Top, c.Right, c.Bottom);
            var tries = new List<(double X, double Y, double D)>();
            double d = Math.Max(pb.Width, pb.Height);
            if (d >= MinEnvelopeScales * s && d <= MaxEnvelopeScales * s)
            {
                if (pb.Width >= pb.Height) tries.AddRange([(pb.CentreX, pb.Top + d / 2, d), (pb.CentreX, pb.Bottom + 1 - d / 2, d)]);
                else tries.AddRange([(pb.Left + d / 2, pb.CentreY, d), (pb.Right + 1 - d / 2, pb.CentreY, d)]);
            }
            // Or an arc of it cut off where a lead crosses it: the circle through its ink.
            if (Math.Max(pb.Width, pb.Height) >= 0.5 * MinEnvelopeScales * s && c.Area >= 4 * w)
            {
                var pts = new List<PointD>(c.Area);
                for (int y = c.Top; y <= c.Bottom; y++)
                    for (int x = c.Left; x <= c.Right; x++)
                        if (lab.Labels[y * W + x] == c.Label) pts.Add(new PointD(x + 0.5, y + 0.5));
                var fit = Fit.Circle(pts);
                double fd = 2 * fit.R + w;
                if (fit.Residual <= w && fd >= MinEnvelopeScales * s && fd <= MaxEnvelopeScales * s) tries.Add((fit.Cx, fit.Cy, fd));
            }
            foreach (var (cx, cy, cd) in tries)
            {
                if (r.Any(e => Math.Abs(e.X - cx) <= w && Math.Abs(e.Y - cy) <= w)) continue;
                if (Round(ink, cx, cy, cd, w)) r.Add(new EnvelopeCircle(cx, cy, cd / 2,
                    new PixelBox((int)Math.Round(cx - cd / 2), (int)Math.Round(cy - cd / 2), (int)Math.Round(cx + cd / 2) - 1, (int)Math.Round(cy + cd / 2) - 1)));
            }
        }
        return r;
    }

    /// <summary>Ink runs round a circle of diameter <paramref name="d"/> at (cx, cy) — 85 % of the way round, within a pen —
    /// with ink inside it.</summary>
    private static bool Round(BinaryImage ink, double cx, double cy, double d, double w)
    {
        double rad = d / 2 - w / 2;
        int on = 0, rays = 72;
        for (int k = 0; k < rays; k++)
        {
            double t = 2 * Math.PI * k / rays, ux = Math.Cos(t), uy = Math.Sin(t);
            for (double q = -w; q <= w; q += 0.5)
                if (ink.At((int)Math.Floor(cx + (rad + q) * ux), (int)Math.Floor(cy + (rad + q) * uy))) { on++; break; }
        }
        if (on < 0.85 * rays) return false;
        int inner = 0;
        double r2 = 0.7 * rad * 0.7 * rad;
        for (int y = (int)(cy - rad); y <= (int)(cy + rad); y++)
            for (int x = (int)(cx - rad); x <= (int)(cx + rad); x++)
                if (ink.At(x, y) && (x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy) < r2) inner++;
        return inner >= w * w;
    }

    /// <summary>An envelope circle's diameter, in symbol scales.</summary>
    private const double MinEnvelopeScales = 1, MaxEnvelopeScales = 4;

    /// <summary>The side of a wire end's piece facing the gap: a wire arriving from that side (R-imf4-1) — +1 is right of or
    /// below the piece, along the axis the two pieces face on.</summary>
    private static bool LeadFacing(List<SymbolAttachment> ends, PixelBox box, (int X, int Y) side)
    {
        foreach (var e in ends)
        {
            // The wire arrives travelling against the side it comes from.
            double dot = -(e.DirX * side.X + e.DirY * side.Y);
            if (dot < 0.7) continue;
            double rel = side.X != 0 ? (e.X - box.CentreX) * side.X : (e.Y - box.CentreY) * side.Y;
            if (rel > 0) return true;
        }
        return false;
    }

    /// <summary>Two boxes facing each other across a gap shorter than the larger one's extent along the faces, with no
    /// wire in the gap — or one inside the other: an outline and what is drawn in it, the zig-zag in a termination's box
    /// (IM-12), are one symbol. IM-7's rule, kept for a reading with no symbol scale.</summary>
    private static bool Facing(PixelBox a, PixelBox b, WireGraph wires)
    {
        if (Inside(a, b, 0) || Inside(b, a, 0)) return true;
        return FacingGap(a, b, wires) is { } f && f.Gap < f.Extent;
    }

    /// <summary>Two boxes facing each other, over half the smaller one's extent, across a gap no wire crosses: the gap, the
    /// larger one's extent along the faces, and the side of <paramref name="a"/> that faces <paramref name="b"/>.</summary>
    private static (double Gap, double Extent, (int X, int Y) Side)? FacingGap(PixelBox a, PixelBox b, WireGraph wires)
    {
        int ox = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) + 1;
        int oy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) + 1;
        if (ox >= 0.5 * Math.Min(a.Width, b.Width))
        {
            int gap = Math.Max(b.Top - a.Bottom - 1, a.Top - b.Bottom - 1);
            if (gap < 0) return null;
            int top = Math.Min(a.Bottom, b.Bottom) + 1, bottom = Math.Max(a.Top, b.Top);
            if (WireIn(Math.Max(a.Left, b.Left), top, Math.Min(a.Right, b.Right) + 1, bottom, wires)) return null;
            return (gap, Math.Max(a.Width, b.Width), (0, b.Top > a.Bottom ? 1 : -1));
        }
        if (oy >= 0.5 * Math.Min(a.Height, b.Height))
        {
            int gap = Math.Max(b.Left - a.Right - 1, a.Left - b.Right - 1);
            if (gap < 0) return null;
            int left = Math.Min(a.Right, b.Right) + 1, right = Math.Max(a.Left, b.Left);
            if (WireIn(left, Math.Max(a.Top, b.Top), right, Math.Min(a.Bottom, b.Bottom) + 1, wires)) return null;
            return (gap, Math.Max(a.Height, b.Height), (b.Left > a.Right ? 1 : -1, 0));
        }
        return null;
    }

    /// <summary>Whether any wire crosses the rectangle [x0, x1] × [y0, y1] (pixel edges).</summary>
    private static bool WireIn(double x0, double y0, double x1, double y1, WireGraph wires)
    {
        if (x1 <= x0 || y1 <= y0) return false;
        foreach (var s in wires.Segments)
        {
            // Liang–Barsky.
            double t0 = 0, t1 = 1, dx = s.B.x - s.A.x, dy = s.B.y - s.A.y;
            bool Clip(double p, double q)
            {
                if (Math.Abs(p) < 1e-12) return q >= 0;
                double t = q / p;
                if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
                return true;
            }
            if (Clip(-dx, s.A.x - x0) && Clip(dx, x1 - s.A.x) && Clip(-dy, s.A.y - y0) && Clip(dy, y1 - s.A.y) && t0 <= t1)
                return true;
        }
        return false;
    }

    /// <summary>The regions that are hops (R-im7-5), as joins of the two wire halves, and which regions they were. The
    /// wire hopped cuts a hop's arc in two where it passes under its apex, so a hop is one region with both ends or two
    /// regions with one end each — never merged across that wire, which is the rule that keeps two parts apart.</summary>
    public static (List<HopJoin> Hops, HashSet<int> Regions) Hops(List<RawRegion> regions, int width, WireGraph wires,
                                                                    double w, SchematicImageOptions o)
    {
        var hops = new List<HopJoin>();
        var used = new HashSet<int>();
        var at = Attachments(regions, width, wires, w, o);
        var tries = new List<(int A, int B, SymbolAttachment P, SymbolAttachment Q)>();
        for (int k = 0; k < regions.Count; k++)
            if (at[k].Count == 2) tries.Add((k, k, at[k][0], at[k][1]));
        for (int i = 0; i < regions.Count; i++)
            for (int j = i + 1; j < regions.Count; j++)
                if (at[i].Count == 1 && at[j].Count == 1)
                {
                    var u = regions[i].Box.Union(regions[j].Box);
                    if (u.Width <= o.MaxHopSpan * w + 2 && u.Height <= o.MaxHopSpan * w + 2) tries.Add((i, j, at[i][0], at[j][0]));
                }
        foreach (var (ra, rb, p, q) in tries)
        {
            if (used.Contains(ra) || used.Contains(rb)) continue;
            if (Hop(p, q, wires, w, o.MaxHopSpan) is not { } hop) continue;
            hops.Add(hop);
            used.Add(ra);
            used.Add(rb);
        }
        return (hops, used);
    }

    /// <summary>Two wire ends facing each other a few w apart, on one line, with another wire passing between them.</summary>
    private static HopJoin? Hop(SymbolAttachment p, SymbolAttachment q, WireGraph wires, double w, double span)
    {
        if (p.DirX * q.DirX + p.DirY * q.DirY > -0.95) return null;
        var (a, b) = (new PointD(p.X, p.Y), new PointD(q.X, q.Y));
        double d = WireGraph.Dist(a, b);
        if (d < 2 * w || d > span * w) return null;
        if (Math.Abs(WireGraph.Local(a, new PointD(a.x + p.DirX, a.y + p.DirY), b).Perp) > w) return null;
        var sa = wires.SegmentAt(p.Node).Id;
        var sb = wires.SegmentAt(q.Node).Id;
        foreach (var s in wires.Segments)
        {
            if (s.Id == sa || s.Id == sb) continue;
            if (Intersect(a, b, s.A, s.B) is { } x) return new HopJoin(a, b, x);
        }
        return null;
    }

    /// <summary>The attachments, supply marks, net labels and decorations, against the final wires.</summary>
    public static AttachResult Attach(List<RawRegion> regions, int width, WireGraph wires,
                                      IReadOnlyList<TextRegion> words, double w, SchematicImageOptions o)
    {
        var at = Attachments(regions, width, wires, w, o);
        var result = new AttachResult([], [], [], [], []);
        var reached = new HashSet<int>();
        for (int k = 0; k < regions.Count; k++)
        {
            var reg = regions[k];
            if (at[k].Count == 0)
            {
                result.Decorations.Add(reg.Box);
                continue;
            }
            foreach (var a in at[k]) reached.Add(a.Node);
            if (at[k].Count == 1 && Supply(reg.Box, at[k][0], w) is { } shape)
            {
                result.Supplies.Add(new SupplyMark(at[k][0].Node, reg.Box, shape, Beside(reg.Box, words, 3 * w)));
                continue;
            }
            var mask = new BinaryImage(reg.Box.Width, reg.Box.Height);
            foreach (int i in reg.Pixels) mask.Pixels[(i / width - reg.Box.Top) * mask.Width + (i % width - reg.Box.Left)] = 1;
            if (at[k].Count == 1 && FreeLead(reg, width, at[k][0], w) is { } free) at[k].Add(free);
            result.Regions.Add(new SymbolRegion(result.Regions.Count, reg.Box, reg.Pixels.Count, at[k], mask));
        }

        foreach (var end in wires.Ends)
        {
            if (reached.Contains(end.Id)) continue;
            var (dx, dy) = wires.Incoming(end.Id);
            int best = -1;
            double bd = o.LabelGap * w + w / 2 + 1;
            foreach (var t in words)
            {
                double dist = t.Box.Distance(end.X, end.Y);
                if (dist > bd) continue;
                // In line with the wire: its line passes through the word's box, widened across by 1.5 w.
                double pad = 1.5 * w;
                bool inLine = Math.Abs(dx) >= Math.Abs(dy)
                    ? end.Y >= t.Box.Top - pad && end.Y <= t.Box.Bottom + 1 + pad
                    : end.X >= t.Box.Left - pad && end.X <= t.Box.Right + 1 + pad;
                if (!inLine) continue;
                bd = dist;
                best = t.Id;
            }
            if (best >= 0) result.Labels.Add(new NetLabel(end.Id, best));
            else result.Dangling.Add(end.Id);
        }
        return result;
    }

    /// <summary>A lead with no wire on it (R-imf5-4): the far end of a region drawn along its one wire — twice as long
    /// that way as it is across — when it ends in a straight stub on that wire's line, a pen and a half wide, a pen and a
    /// half or a fourteenth of the part long. Its node is −1: the pin is open. A pointed flag's tip is narrow for less than a pen,
    /// and a ground's bars are wider than long.</summary>
    private static SymbolAttachment? FreeLead(RawRegion reg, int width, SymbolAttachment a, double w)
    {
        double ux = a.DirX, uy = a.DirY;
        if (ux == 0 && uy == 0) return null;
        var pts = reg.Pixels.Select(i =>
        {
            double dx = i % width + 0.5 - a.X, dy = i / width + 0.5 - a.Y;
            return (T: dx * ux + dy * uy, A: -dx * uy + dy * ux);
        }).ToList();
        double tMax = pts.Max(p => p.T), across = pts.Max(p => p.A) - pts.Min(p => p.A) + 1;
        if (tMax < 2 * across) return null;
        double stub = Math.Max(1.5 * w, tMax / 14);
        var tip = pts.Where(p => p.T >= tMax - stub).ToList();
        if (tip.Count == 0 || tip.Max(p => p.A) - tip.Min(p => p.A) + 1 > 1.5 * w + 1 || Math.Abs(tip.Average(p => p.A)) > w) return null;
        // Every slice of the stub carries ink: a straight run, not a scatter of ends.
        for (double t = tMax - stub; t <= tMax - 1; t += 1)
            if (!tip.Any(p => p.T >= t && p.T < t + 1.5)) return null;
        return new SymbolAttachment(-1, a.X + ux * tMax, a.Y + uy * tMax, -ux, -uy);
    }

    /// <summary>For each region, the wire ends stopping within Attach w of one of its pixels — each end going to the
    /// region nearest it.</summary>
    private static List<SymbolAttachment>[] Attachments(List<RawRegion> regions, int width, WireGraph wires, double w,
                                                        SchematicImageOptions o)
    {
        var r = new List<SymbolAttachment>[regions.Count];
        for (int k = 0; k < regions.Count; k++) r[k] = [];
        if (regions.Count == 0) return r;
        int height = regions.Max(g => g.Box.Bottom) + 1;
        var owner = new Dictionary<int, int>();
        for (int k = 0; k < regions.Count; k++) foreach (int i in regions[k].Pixels) owner[i] = k;
        double reach = o.Attach * w + w / 2 + 1;
        foreach (var end in wires.Ends)
        {
            int best = -1;
            double bd = reach * reach;
            int x0 = (int)Math.Floor(end.X - reach), x1 = (int)Math.Ceiling(end.X + reach);
            int y0 = (int)Math.Floor(end.Y - reach), y1 = (int)Math.Ceiling(end.Y + reach);
            for (int y = Math.Max(0, y0); y <= Math.Min(height - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(width - 1, x1); x++)
                {
                    if (!owner.TryGetValue(y * width + x, out int k)) continue;
                    double d2 = (x + 0.5 - end.X) * (x + 0.5 - end.X) + (y + 0.5 - end.Y) * (y + 0.5 - end.Y);
                    if (d2 < bd || (d2 == bd && k < best)) { bd = d2; best = k; }
                }
            if (best < 0) continue;
            var (dx, dy) = wires.Incoming(end.Id);
            r[best].Add(new SymbolAttachment(end.Id, end.X, end.Y, dx, dy));
        }
        return r;
    }

    /// <summary>A supply mark's shape, or null: a short bar across the end, or an arrow whose apex the wire reaches.</summary>
    private static SupplyShape? Supply(PixelBox box, SymbolAttachment a, double w)
    {
        bool horizontal = Math.Abs(a.DirX) >= Math.Abs(a.DirY);
        double alongMin = horizontal ? box.Left : box.Top, alongMax = horizontal ? box.Right + 1 : box.Bottom + 1;
        double acrossMin = horizontal ? box.Top : box.Left, acrossMax = horizontal ? box.Bottom + 1 : box.Right + 1;
        double end = horizontal ? a.X : a.Y, axis = horizontal ? a.Y : a.X;
        double dir = horizontal ? Math.Sign(a.DirX) : Math.Sign(a.DirY);
        double along = alongMax - alongMin, across = acrossMax - acrossMin;
        if (Math.Abs((acrossMin + acrossMax) / 2 - axis) > 1.5 * w) return null;
        if (along <= 2 * w + 2 && across >= 3 * w && across <= 16 * w) return SupplyShape.Bar;
        // An arrow: the box lies behind the end, the wire reaching its apex.
        double front = dir > 0 ? alongMax : -alongMin, back = dir > 0 ? alongMin : -alongMax, e = dir * end;
        if (across >= 2 * w && across <= 10 * w && along >= 2 * w && along <= 10 * w && front <= e + 1.5 * w && back < e - w)
            return SupplyShape.Arrow;
        return null;
    }

    /// <summary>The word nearest <paramref name="box"/> within <paramref name="within"/> pixels, or null.</summary>
    private static int? Beside(PixelBox box, IReadOnlyList<TextRegion> words, double within)
    {
        int? best = null;
        double bd = within;
        foreach (var t in words)
        {
            double d = box.Distance(t.Box);
            if (d <= bd) { bd = d; best = t.Id; }
        }
        return best;
    }

    private static PixelBox BoxOf(List<int> pixels, int width)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (int i in pixels)
        {
            int x = i % width, y = i / width;
            l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y);
        }
        return new PixelBox(l, t, r, b);
    }

    private static PointD? Intersect(PointD a, PointD b, PointD c, PointD d)
    {
        double rx = b.x - a.x, ry = b.y - a.y, sx = d.x - c.x, sy = d.y - c.y;
        double den = rx * sy - ry * sx;
        if (Math.Abs(den) < 1e-12) return null;
        double t = ((c.x - a.x) * sy - (c.y - a.y) * sx) / den;
        double u = ((c.x - a.x) * ry - (c.y - a.y) * rx) / den;
        if (t < 0 || t > 1 || u < 0 || u > 1) return null;
        return new PointD(a.x + t * rx, a.y + t * ry);
    }
}
