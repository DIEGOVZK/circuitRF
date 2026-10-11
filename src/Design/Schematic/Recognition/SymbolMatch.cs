// The template matcher — brief-img-8-schematic-symbols.md R-im8-3, R-im8-4; the one IImageSymbolClassifier.
//
// A region with n attachments is compared with every template with n pins:
//
//   region ─ hollowed (ink deeper than w from the paper removed, so a filled triangle reads as its outline)
//          ─ skeleton ─ its edges as centre lines
//   template × 8 orientations (mirror, then 4 turns — the schematic's own order) × every assignment of its pins to the
//          attachments whose directions agree within ± 20° (a pin's lead leaves the body the way the wire arrives)
//          ─ scaled and moved so its pins lie on the attachments (least squares; one pin: the body's size)
//          ─ the modified-Hausdorff distance to the region's centre lines, in units of w
//
// The distance is AS-10's (Glyph.Distance — the mean distance from each one's resampled centre lines to the other's,
// the larger way), on a stroke set rather than a glyph. A template is accepted within AcceptDistance and when clear of
// the best template of a DIFFERENT kind by KindMargin; two drawings of one kind never compete. The winning orientation
// and assignment give the pin order.
//
// A structure check (R-im8-4) settles what a distance cannot: a rectangle's two long sides are as close to a
// capacitor's plates as the plates are, and only the gap between them tells the two apart. The best template failing
// its check is set aside and the next taken — reported, never silent.

using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Silkscreen;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>The template matcher's tolerances; distances in units of the stroke width w.</summary>
public sealed record SymbolMatchOptions
{
    /// <summary>A pin's lead and its wire agree within this many degrees.</summary>
    public double AngleToleranceDeg { get; init; } = 20;

    /// <summary>A match is accepted at or below this distance.</summary>
    public double AcceptDistance { get; init; } = 1.0;

    /// <summary>… and when its distance is at most (1 − this) of the best different kind's.</summary>
    public double KindMargin { get; init; } = 0.15;

    /// <summary>Two placements of one template within this distance are a tie, settled by orientation order.</summary>
    public double TieDistance { get; init; } = 0.05;

    /// <summary>Points each side's centre lines are resampled to.</summary>
    public int Samples { get; init; } = 128;
}

/// <summary>Names a region by the nearest template (R-im8-3, R-im8-4).</summary>
public sealed class TemplateSymbolClassifier : IImageSymbolClassifier
{
    public TemplateSymbolClassifier(IReadOnlyList<SymbolTemplate>? templates = null, SymbolMatchOptions? options = null)
    {
        Templates = templates ?? SymbolTemplates.All();
        Options = options ?? new SymbolMatchOptions();
    }

    public IReadOnlyList<SymbolTemplate> Templates { get; }
    public SymbolMatchOptions Options { get; }

    public SymbolClassification Classify(SymbolRegion region, double strokeWidth) => Classify(region, strokeWidth, 0);

    /// <summary>A picture whose symbol scale is not known is taken at this many stroke widths: the checks measured in s
    /// then ask what they asked in w before IMF-5.</summary>
    public const double FallbackSymbolInW = 12;

    public SymbolClassification Classify(SymbolRegion region, double strokeWidth, double symbolScale)
    {
        ArgumentNullException.ThrowIfNull(region);
        double w = Math.Max(1.0, strokeWidth);
        double s = symbolScale > 0 ? symbolScale : FallbackSymbolInW * w;
        int n = region.Attachments.Count;
        if (n == 0 || n > 5) return SymbolClassification.None;
        var plain = Read(region, w, s, out var shape);
        // A two-pin body crossed by a long oblique line — a variable resistor's arrow (R-imf5-3) — is read again with the
        // line taken off; it is a variable resistor when what is left is a resistor.
        if (n == 2 && CrossingArrow.Find(region, w, s) is { } erased)
        {
            var crossed = Read(region with { Mask = erased }, w, s, out _);
            if (crossed.Best is { Kind: ImageSymbolKind.Resistor } cb
                && (plain.Best is not { } pb || pb.Kind == ImageSymbolKind.Resistor || cb.Score < pb.Score))
                return crossed with { Crossed = true };
        }
        // A two-pin body no template took, with type touching a plate's end (a label IMF-4 left on it): read again with
        // only the ink no further from the axis than the plates themselves reach. Taken only as a capacitor — the plates
        // measured at the leads are what the trim is made from.
        if (n == 2 && plain.Best is null && PlateTrim(region, shape, w) is { } trimmed
            && Read(region with { Mask = trimmed }, w, s, out _) is { Best.Kind: ImageSymbolKind.Capacitor } plates)
            return plates;
        if (plain.Best is { Kind: ImageSymbolKind.Transistor } t && Polarity(t, region, w) is { } variant && variant != t.Template.Variant)
            plain = plain with { Variant = variant };
        return plain;
    }

    private SymbolClassification Read(SymbolRegion region, double w, double s, out RegionShape shape)
    {
        shape = RegionShape.Of(region, w, Options.Samples);
        var order = Rank(region, shape, w);
        if (order.Count == 0) return SymbolClassification.None;

        var demoted = new List<SymbolDemotion>();
        var failed = new HashSet<SymbolCandidate>();
        foreach (var c in order)
            if (!Structure.Passes(c, region, shape, w, s))
                failed.Add(c);
        SymbolCandidate? best = order.FirstOrDefault(c => !failed.Contains(c));
        foreach (var c in order)
        {
            if (c == best) break;
            demoted.Add(new SymbolDemotion(c, c.Template.Check));
        }
        var runner = order.FirstOrDefault(c => !failed.Contains(c) && (best is null || c.Kind != best.Kind));
        if (best is null) return new SymbolClassification(null, runner, demoted, PartConfidence.Low);

        bool clear = runner is null || best.Score <= (1 - Options.KindMargin) * runner.Score;
        if (best.Score > Options.AcceptDistance || !clear)
            return new SymbolClassification(null, runner ?? best, demoted, PartConfidence.Low);
        bool sure = best.Score <= Options.AcceptDistance / 2 && demoted.Count == 0
                    && (runner is null || best.Score <= (1 - 2 * Options.KindMargin) * runner.Score);
        return new SymbolClassification(best, runner, demoted, sure ? PartConfidence.High : PartConfidence.Medium);
    }

    /// <summary>The region's mask with the ink further from the axis than the plates at its two leads reach — plus a pen —
    /// taken off; null when nothing stands out that far.</summary>
    private static BinaryImage? PlateTrim(SymbolRegion region, RegionShape shape, double w)
    {
        var (a, b) = (shape.Contacts[0], shape.Contacts[1]);
        double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        if (len < 2) return null;
        double ux = (b.X - a.X) / len, uy = (b.Y - a.Y) / len;
        var mask = region.Mask;
        int ox = region.Box.Left, oy = region.Box.Top;
        bool Ink(double x, double y) => mask.At((int)Math.Floor(x) - ox, (int)Math.Floor(y) - oy);
        // A plate: the ink across the axis, contiguous, a pixel inside the body from where the lead meets it.
        double Plate(double x, double y)
        {
            int lo = 0, hi = 0, max = mask.Width + mask.Height;
            while (lo > -max && Ink(x + (lo - 1) * -uy, y + (lo - 1) * ux)) lo--;
            while (hi < max && Ink(x + (hi + 1) * -uy, y + (hi + 1) * ux)) hi++;
            return Math.Max(-lo, hi);
        }
        double reach = Math.Max(Plate(a.X + ux, a.Y + uy), Plate(b.X - ux, b.Y - uy)) + w;
        if (reach < 2 * w) return null;
        var trimmed = mask.Clone();
        int cut = 0;
        for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
            {
                if (!mask[x, y]) continue;
                double across = -(x + 0.5 + ox - a.X) * uy + (y + 0.5 + oy - a.Y) * ux;
                if (Math.Abs(across) <= reach) continue;
                trimmed.Pixels[y * mask.Width + x] = 0;
                cut++;
            }
        return cut == 0 ? null : trimmed;
    }

    /// <summary>A transistor's polarity from its arrow (R-imf5-6). The arrowhead is the region's deepest ink — a filled
    /// triangle is thicker than any stroke — and the line it lies on is the main axis of the ink round it; it tapers
    /// toward its tip. A BJT's arrow on its emitter points at the emitter's pin for an n-p-n and away for a p-n-p; a FET's
    /// on its gate points away from the gate's pin for an n-channel and at it for a p-channel. Null when no arrowhead
    /// stands out, or its taper does not say.</summary>
    private static string? Polarity(SymbolCandidate c, SymbolRegion region, double w)
    {
        bool bjt = c.Template.Variant is "npn" or "pnp";
        string pinName = bjt ? "e" : "g";
        int k = c.Template.Pins.ToList().FindIndex(p => p.Name == pinName);
        if (k < 0 || c.Template.Variant is not ("npn" or "pnp" or "nfet" or "pfet")) return null;
        var mask = region.Mask;
        int ox = region.Box.Left, oy = region.Box.Top;
        var dt = DistanceTransform.Compute(mask);
        double deep = Math.Max(1.5, 0.9 * w);
        var core = new List<(double X, double Y, double D)>();
        for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
                if (dt[y * mask.Width + x] > deep) core.Add((x + 0.5, y + 0.5, dt[y * mask.Width + x]));
        if (core.Count == 0) return null;
        // The arrowhead: the deep pixels round the deepest, within its own depth twice over.
        var top = core.MaxBy(p => p.D);
        double rIn = top.D;
        var head = core.Where(p => Math.Abs(p.X - top.X) <= 2 * rIn && Math.Abs(p.Y - top.Y) <= 2 * rIn).ToList();
        double cx = head.Average(p => p.X), cy = head.Average(p => p.Y);
        // The axis: the ink within three depths of it, by its principal direction.
        double reach = 3 * rIn + w;
        double sxx = 0, syy = 0, sxy = 0;
        int n = 0;
        for (int y = (int)(cy - reach); y <= (int)(cy + reach); y++)
            for (int x = (int)(cx - reach); x <= (int)(cx + reach); x++)
            {
                if (!mask.At(x, y)) continue;
                double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
                if (dx * dx + dy * dy > reach * reach) continue;
                sxx += dx * dx; syy += dy * dy; sxy += dx * dy; n++;
            }
        if (n == 0) return null;
        double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy), ux = Math.Cos(angle), uy = Math.Sin(angle);
        var pin = region.Attachments[c.PinAttachments[k]];
        if ((pin.X - ox - cx) * ux + (pin.Y - oy - cy) * uy < 0) (ux, uy) = (-ux, -uy);
        // The ink across the axis, contiguous through it, either side of the core.
        double Width(double t)
        {
            double px = cx + t * ux, py = cy + t * uy;
            int lo = 0, hi = 0;
            while (lo > -reach && mask.At((int)Math.Floor(px + (lo - 1) * -uy), (int)Math.Floor(py + (lo - 1) * ux))) lo--;
            while (hi < reach && mask.At((int)Math.Floor(px + (hi + 1) * -uy), (int)Math.Floor(py + (hi + 1) * ux))) hi++;
            return hi - lo + 1;
        }
        // Inside the head: its base is an inradius behind the core's centre, so beyond that the arm's own width is read.
        double Mean(int sign) => Enumerable.Range(0, 5).Average(i => Width(sign * rIn * (0.3 + 0.1 * i)));
        double toward = Mean(1), away = Mean(-1);
        if (Math.Abs(toward - away) < Math.Max(0.5, 0.15 * Math.Max(toward, away))) return null;
        bool tipAtPin = toward < away;
        return bjt ? (tipAtPin ? "npn" : "pnp") : (tipAtPin ? "pfet" : "nfet");
    }

    /// <summary>Each template with the region's pin count, at its best placement, best first (ties in template order).</summary>
    internal List<SymbolCandidate> Rank(SymbolRegion region, RegionShape shape, double w)
    {
        var ranked = new List<(SymbolCandidate C, int Order)>();
        for (int k = 0; k < Templates.Count; k++)
        {
            var t = Templates[k];
            if (t.Pins.Count != region.Attachments.Count) continue;
            if (t.Fill == TemplateFill.Solid && !shape.Solid || t.Fill == TemplateFill.Hollow && shape.Solid) continue;
            if (Best(t, region, shape, w) is { } c) ranked.Add((c, k));
        }
        return [.. ranked.OrderBy(r => r.C.Score).ThenBy(r => r.Order).Select(r => r.C)];
    }

    /// <summary>The template's best placement on the region, or null when no orientation's pins agree with the
    /// attachments.</summary>
    private SymbolCandidate? Best(SymbolTemplate t, SymbolRegion region, RegionShape shape, double w)
    {
        double cosTol = Math.Cos(Options.AngleToleranceDeg * Math.PI / 180);
        var at = region.Attachments;
        int n = at.Count;
        SymbolCandidate? best = null;
        foreach (var o in SymbolOrientation.All)
        {
            var pins = t.Pins.Select(p => (P: o.Apply(p.X, p.Y), D: o.Apply(p.DirX, p.DirY))).ToList();
            foreach (var perm in Permutations(n))
            {
                bool agree = true;
                for (int i = 0; i < n && agree; i++)
                {
                    var a = at[perm[i]];
                    if (a.DirX == 0 && a.DirY == 0) continue;
                    agree = -(pins[i].D.X * a.DirX + pins[i].D.Y * a.DirY) >= cosTol;
                }
                if (!agree) continue;
                if (Fit(t, o, pins.Select(p => p.P).ToList(), perm, region, shape, w) is not { } fit) continue;
                var glyph = new Glyph(Place(t, o, fit.S, fit.Tx, fit.Ty), Options.Samples);
                double score = Glyph.Distance(shape.Glyph, glyph) / w;
                // A later orientation must be better by more than the pixels' own noise: a symmetric drawing fits two
                // orientations equally, and the first in SymbolOrientation.All's order is the one meant.
                if (best is null || score < best.Score - Options.TieDistance)
                    best = new SymbolCandidate(t, o, [.. perm], score);
            }
        }
        return best;
    }

    /// <summary>The scale and shift putting the oriented pins on their attachments (least squares), or null when they
    /// do not fit. One pin: the scale is the region's size over the body's.</summary>
    private static (double S, double Tx, double Ty)? Fit(SymbolTemplate t, SymbolOrientation o, List<(double X, double Y)> pins,
                                                         int[] perm, SymbolRegion region, RegionShape shape, double w)
    {
        var q = perm.Select(i => shape.Contacts[i]).ToList();
        if (pins.Count == 1)
        {
            var pts = t.Strokes.SelectMany(s => Points(s)).Select(p => o.Apply(p.X, p.Y)).ToList();
            double tw = pts.Max(p => p.X) - pts.Min(p => p.X), th = pts.Max(p => p.Y) - pts.Min(p => p.Y);
            double rw = Math.Max(1, region.Box.Width - w), rh = Math.Max(1, region.Box.Height - w);
            double s1 = Math.Max(rw, rh) / Math.Max(1e-9, Math.Max(tw, th));
            return (s1, q[0].X - s1 * pins[0].X, q[0].Y - s1 * pins[0].Y);
        }
        double px = pins.Average(p => p.X), py = pins.Average(p => p.Y), qx = q.Average(p => p.X), qy = q.Average(p => p.Y);
        double num = 0, den = 0;
        for (int i = 0; i < pins.Count; i++)
        {
            num += (pins[i].X - px) * (q[i].X - qx) + (pins[i].Y - py) * (q[i].Y - qy);
            den += (pins[i].X - px) * (pins[i].X - px) + (pins[i].Y - py) * (pins[i].Y - py);
        }
        if (den <= 0 || num <= 0) return null;
        double s = num / den, tx = qx - s * px, ty = qy - s * py;
        double allowed = Math.Max(2 * w, 0.2 * s);
        for (int i = 0; i < pins.Count; i++)
            if (Math.Sqrt(Sq(s * pins[i].X + tx - q[i].X) + Sq(s * pins[i].Y + ty - q[i].Y)) > allowed) return null;
        return (s, tx, ty);
    }

    /// <summary>The template's strokes on the picture.</summary>
    internal static List<double[]> Place(SymbolTemplate t, SymbolOrientation o, double s, double tx, double ty) =>
        [.. t.Strokes.Select(st =>
        {
            var r = new double[st.Length];
            for (int i = 0; i < st.Length; i += 2)
            {
                var (x, y) = o.Apply(st[i], st[i + 1]);
                r[i] = s * x + tx;
                r[i + 1] = s * y + ty;
            }
            return r;
        })];

    private static IEnumerable<(double X, double Y)> Points(double[] s) { for (int i = 0; i < s.Length; i += 2) yield return (s[i], s[i + 1]); }

    private static IEnumerable<int[]> Permutations(int n)
    {
        var a = Enumerable.Range(0, n).ToArray();
        return Permute(a, 0);

        static IEnumerable<int[]> Permute(int[] a, int k)
        {
            if (k == a.Length) { yield return (int[])a.Clone(); yield break; }
            for (int i = k; i < a.Length; i++)
            {
                (a[k], a[i]) = (a[i], a[k]);
                foreach (var p in Permute(a, k + 1)) yield return p;
                (a[k], a[i]) = (a[i], a[k]);
            }
        }
    }

    private static double Sq(double v) => v * v;
}

/// <summary>What the matcher reads off a region once: its centre lines, its ink and whether it is drawn filled.</summary>
/// <param name="Contacts">For each attachment, where its wire meets the region's ink: the attachment itself, or the first
/// ink straight ahead of it within 3 w — a wire cut away from a filled body ends short of it, and fitted there a
/// template is scaled to a span longer than the body.</param>
/// <param name="Thick">A solid region's ink deeper than 0.6 w from the paper — its filled area.</param>
/// <param name="Loops">Independent cycles of the centre lines: a looped coil's loops (R-imf5-4).</param>
internal sealed record RegionShape(Glyph Glyph, IReadOnlyList<(double X, double Y)> Ink, IReadOnlyList<(double X, double Y)> Skeleton,
                                   bool Solid, IReadOnlyList<(double X, double Y)> Contacts)
{
    public IReadOnlyList<(double X, double Y)> Thick { get; init; } = [];
    public int Loops { get; init; }

    public static RegionShape Of(SymbolRegion region, double w, int samples)
    {
        var mask = region.Mask;
        // A stroke drawn hatched or dotted (R-imf5-3) is a region of many small pieces: closed by a disc a pen in radius,
        // so its centre line is one line. A region of a few pieces — two plates, a triangle and its bar — is left alone.
        var pieces = Components.Label(mask).Components;
        if (pieces.Count >= 6 && pieces.Count(c => c.Area <= 4 * w * w) >= 0.75 * pieces.Count) mask = Morphology.Close(mask, w);
        int ox = region.Box.Left, oy = region.Box.Top;
        var dt = DistanceTransform.Compute(mask);

        // Filled: what survives an opening by a disc 1.5 w in radius — no stroke of the drawing's width does.
        var thick = Morphology.Open(mask, 1.5 * w);
        bool solid = thick.Count() >= Math.Max(4, 4 * w * w);

        // Hollowed: a filled area reduced to the one-pixel ring at its edge, every stroke left as it is — so a filled
        // shape is read by its outline, where a template draws it.
        var hollow = new BinaryImage(mask.Width, mask.Height);
        for (int i = 0; i < dt.Length; i++)
            if (mask.Pixels[i] != 0 && (thick.Pixels[i] == 0 || dt[i] <= 1.0)) hollow.Pixels[i] = 1;

        var g = SkeletonGraph.Build(hollow, new SkeletonGraphOptions { StrokeWidth = w });
        var strokes = new List<double[]>();
        var skel = new List<(double, double)>();
        foreach (var e in g.Edges)
        {
            strokes.Add([.. e.Polyline.SelectMany(p => new[] { p.x + ox, p.y + oy })]);
            foreach (var (x, y) in e.Pixels) skel.Add((x + 0.5 + ox, y + 0.5 + oy));
        }
        foreach (var nd in g.Nodes.Where(nd => nd.Degree == 0))
        {
            strokes.Add([nd.X + ox, nd.Y + oy, nd.X + ox, nd.Y + oy]);
            skel.Add((nd.X + ox, nd.Y + oy));
        }
        var ink = new List<(double, double)>();
        for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
                if (mask[x, y]) ink.Add((x + 0.5 + ox, y + 0.5 + oy));
        var contacts = region.Attachments.Select(a =>
        {
            for (double d = 0; d <= 3 * w; d += 0.5)
            {
                double x = a.X + d * a.DirX, y = a.Y + d * a.DirY;
                if (mask.At((int)Math.Floor(x) - ox, (int)Math.Floor(y) - oy)) return (x, y);
            }
            return (a.X, a.Y);
        }).ToList();
        // Deeper than 0.6 w from the paper: a filled area eroded a little, which keeps its shape — a triangle stays one,
        // where the opening above rounds it toward a disc — and no stroke of the drawing's width.
        var thickPts = new List<(double, double)>();
        if (solid)
            for (int y = 0; y < mask.Height; y++)
                for (int x = 0; x < mask.Width; x++)
                    if (dt[y * mask.Width + x] > Math.Max(1, 0.6 * w)) thickPts.Add((x + 0.5 + ox, y + 0.5 + oy));
        // Cycles = edges − nodes + components, the components by union-find over the edges.
        var root = new Dictionary<int, int>();
        int Find(int x) { while (root.TryGetValue(x, out int p) && p != x) x = p; return x; }
        foreach (var nd in g.Nodes) root[nd.Id] = nd.Id;
        foreach (var e in g.Edges) { int a = Find(e.From), b = Find(e.To); if (a != b) root[a] = b; }
        int components = g.Nodes.Select(nd => Find(nd.Id)).Distinct().Count();
        return new RegionShape(new Glyph(strokes, samples), ink, skel, solid, contacts)
        {
            Thick = thickPts, Loops = Math.Max(0, g.Edges.Count - g.Nodes.Count + components),
        };
    }
}

/// <summary>The structure checks (R-im8-4), each in the frame of the candidate's attachments: along runs from the first
/// pin's attachment to the second (from the one attachment into the region, for a ground), across at right angles.
/// What is a length on the drawing — a gap, a swing — is measured in the symbol scale s (IMF-5); what is a pen's — a
/// stroke's own thickness — in w.</summary>
internal static class Structure
{
    public static bool Passes(SymbolCandidate c, SymbolRegion region, RegionShape shape, double w, double s) => c.Template.Check switch
    {
        StructureCheck.Capacitor => Capacitor(c, shape, w, s),
        StructureCheck.ZigZag => ZigZag(c, shape, w, s),
        StructureCheck.Coil => Coil(c, shape, w),
        StructureCheck.Ground => Ground(region, shape, w),
        StructureCheck.Diode => Gap(c, shape, w, s) is not { Length: >= 1 },
        StructureCheck.FilledBox => FilledBox(c, shape),
        StructureCheck.Battery => Battery(c, shape, w, s),
        StructureCheck.Envelope => Envelope(region),
        StructureCheck.Marked => Marked(region, w),
        _ => true,
    };

    /// <summary>Two bars across the axis, each at least 2.5 w long (or 0.4 s, a hairline's), with a band of bare paper
    /// between them no wider than 1.5 s (R-imf5-1) — and neither under 0.55 of the other, which is a battery's.</summary>
    private static bool Capacitor(SymbolCandidate c, RegionShape shape, double w, double s)
    {
        if (Gap(c, shape, w, s) is not { Length: >= 1 } g || g.Length > 1.5 * s) return false;
        double plate = Math.Min(2.5 * w, 0.4 * s), a = Extent(g.Before), b = Extent(g.After);
        return a >= plate && b >= plate && Math.Min(a, b) >= 0.55 * Math.Max(a, b);
    }

    /// <summary>The widest band of bare paper across the axis whose middle lies between the two attachments, and the
    /// ink either side of it. Only ink no further from the axis than the plates reach counts — the ink across the axis,
    /// unbroken, just inside each lead — so a label standing beyond a plate's end is beside the part, not across its gap;
    /// half a symbol scale where neither lead meets a plate.</summary>
    private static (double Length, List<(double T, double A)> Before, List<(double T, double A)> After)? Gap(SymbolCandidate c, RegionShape shape, double w, double s)
    {
        if (Axis(c, shape) is not { } f) return null;
        var all = shape.Ink.Select(p => f.Local(p)).ToList();
        // A plate's reach either side of the axis: the unbroken ink from the axis outward, the lead's own hole at the
        // axis (the wire was taken off the ink there) allowed, a pen and a pixel wide.
        double Reach(double t0)
        {
            var across = all.Where(p => Math.Abs(p.T - t0) <= 1).Select(p => p.A).Order().ToList();
            double Side(int sign)
            {
                var side = across.Where(a => sign * a >= 0).Select(a => sign * a).Order().ToList();
                if (side.Count == 0 || side[0] > w / 2 + 1) return 0;
                double far = side[0];
                foreach (double a in side.Skip(1)) { if (a - far > 1.5) break; far = a; }
                return far;
            }
            return Math.Max(Side(-1), Side(1));
        }
        double plates = new[] { 1.0, 2.0, 3.0 }.Max(d => Math.Max(Reach(d), Reach(f.Length - d)));
        double window = plates >= 2 ? plates + 1 : Math.Max(0.5 * s, 2);
        var pts = all.Where(p => Math.Abs(p.A) <= window).ToList();
        if (pts.Count == 0) return null;
        int lo = (int)Math.Floor(pts.Min(p => p.T)), hi = (int)Math.Ceiling(pts.Max(p => p.T));
        var ink = new bool[hi - lo + 1];
        foreach (var p in pts) ink[(int)Math.Floor(p.T) - lo] = true;

        int bestStart = -1, bestLen = 0;
        for (int i = 0; i < ink.Length;)
        {
            if (ink[i]) { i++; continue; }
            int j = i;
            while (j < ink.Length && !ink[j]) j++;
            double mid = lo + (i + j) / 2.0;
            if (mid > 0 && mid < f.Length && j - i > bestLen) { bestStart = i; bestLen = j - i; }
            i = j;
        }
        if (bestLen == 0) return (0, pts, []);
        double cut = lo + bestStart;
        return (bestLen, pts.Where(p => p.T < cut).ToList(), pts.Where(p => p.T >= cut + bestLen).ToList());
    }

    /// <summary>At least three alternating vertices: the centre line's mean offset across, binned along, swings past
    /// a third of its amplitude to each side at least three times. The swing is a drawing's length, so it is measured
    /// in s — a tenth of one at least — and the bins are the finer of w and s/16: a heavy pen and a hairline one both
    /// alternate (R-imf5-3).</summary>
    private static bool ZigZag(SymbolCandidate c, RegionShape shape, double w, double s)
    {
        if (Axis(c, shape) is not { } f) return false;
        var bins = Bins(shape.Skeleton.Select(p => f.Local(p)).Where(p => p.T >= 0 && p.T <= f.Length), Math.Max(1, Math.Min(w, s / 16)),
                        p => p.A, b => b.Average());
        double amp = bins.Count == 0 ? 0 : bins.Max(Math.Abs);
        if (amp < Math.Max(0.1 * s, 0.5 * w)) return false;
        int runs = 0, state = 0;
        foreach (var v in bins)
        {
            int sg = v > amp / 3 ? 1 : v < -amp / 3 ? -1 : 0;
            if (sg != 0 && sg != state) { runs++; state = sg; }
        }
        return runs >= 3;
    }

    /// <summary>At least three humps on one side of the axis: the centre line's NEAREST approach to the axis, binned
    /// along, comes within w/2 of its peak and drops a whole w below it three times — and the centre line reaches the
    /// other side by less than a third of that peak, which is what a zig-zag does not. The nearest, not the furthest:
    /// either side of a cusp two arcs share a bin. And the dip is a stroke width, not a fraction of the peak: where
    /// two arcs meet at a cusp their strokes run together, and the skeleton of a 6 px hump drawn 2 px wide comes no
    /// nearer the axis than 4 px. A LOOPED coil passes on its loops: its centre line crosses itself once a loop, three
    /// times at least (R-imf5-4).</summary>
    private static bool Coil(SymbolCandidate c, RegionShape shape, double w)
    {
        if (Axis(c, shape) is not { } f) return false;
        var pts = shape.Skeleton.Select(p => f.Local(p)).Where(p => p.T >= 0 && p.T <= f.Length).ToList();
        if (pts.Count == 0) return false;
        if (c.Template.Variant == "looped") return shape.Loops >= 3 || Dips(pts, w) >= 3;
        int side = pts.Sum(p => p.A) >= 0 ? 1 : -1;
        var bins = Bins(pts, 1, p => side * p.A, b => b.Min());
        double peak = bins.Count == 0 ? 0 : bins.Max();
        if (peak < 2 * w || pts.Max(p => -side * p.A) > peak / 3) return false;
        int humps = 0;
        bool up = false;
        foreach (var h in bins)
        {
            if (!up && h >= peak - w / 2) { humps++; up = true; }
            else if (up && h <= peak - w) up = false;
        }
        return humps >= 3;
    }

    /// <summary>A looped coil whose loops the skeleton merged: the centre line's nearest approach to the axis, binned
    /// along, dips by a whole w or more — on whichever side — this many times.</summary>
    private static int Dips(List<(double T, double A)> pts, double w)
    {
        int best = 0;
        foreach (int side in new[] { 1, -1 })
        {
            var bins = Bins(pts.Where(p => side * p.A >= 0), 1, p => side * p.A, b => b.Min());
            if (bins.Count == 0) continue;
            double peak = bins.Max();
            int dips = 0;
            bool down = false;
            foreach (var h in bins)
            {
                if (!down && h <= peak - w) { dips++; down = true; }
                else if (down && h >= peak - w / 2) down = false;
            }
            best = Math.Max(best, dips);
        }
        return best;
    }

    /// <summary>A filled area that fills its box, three quarters at least, in the frame of the leads: a filled rectangle,
    /// where a diode's filled triangle fills half of its (R-imf5-2).</summary>
    private static bool FilledBox(SymbolCandidate c, RegionShape shape)
    {
        if (shape.Thick.Count < 4 || Axis(c, shape) is not { } f) return false;
        var pts = shape.Thick.Select(p => f.Local(p)).ToList();
        // The box by the 2nd and 98th percentiles each way, so a stray deep pixel at a corner does not widen it.
        static (double Lo, double Hi) Span(List<double> v) { v.Sort(); return (v[(int)(0.02 * (v.Count - 1))], v[(int)Math.Ceiling(0.98 * (v.Count - 1))]); }
        var (t0, t1) = Span([.. pts.Select(p => p.T)]);
        var (a0, a1) = Span([.. pts.Select(p => p.A)]);
        double area = (t1 - t0 + 1) * (a1 - a0 + 1);
        return pts.Count(p => p.T >= t0 && p.T <= t1 && p.A >= a0 && p.A <= a1) >= 0.75 * area;
    }

    /// <summary>A battery: two bars across the axis with bare paper between, one at most 0.7 of the other's length —
    /// what a capacitor's two plates are not (R-imf5-7).</summary>
    private static bool Battery(SymbolCandidate c, RegionShape shape, double w, double s)
    {
        if (Gap(c, shape, w, s) is not { Length: >= 1 } g) return false;
        double a = Extent(g.Before), b = Extent(g.After);
        return Math.Min(a, b) <= 0.7 * Math.Max(a, b);
    }

    /// <summary>The disc inside the envelope — half its radius about the region's centre — is mostly paper: a transistor's
    /// bar and arms cross it, a junction dot fills it.</summary>
    private static bool Envelope(SymbolRegion region)
    {
        var m = region.Mask;
        double cx = m.Width / 2.0, cy = m.Height / 2.0, r = 0.25 * Math.Min(m.Width, m.Height);
        int inside = 0, ink = 0;
        for (int y = (int)(cy - r); y <= (int)(cy + r); y++)
            for (int x = (int)(cx - r); x <= (int)(cx + r); x++)
            {
                if ((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy) > r * r) continue;
                inside++;
                if (m.At(x, y)) ink++;
            }
        return inside > 0 && ink < 0.6 * inside;
    }

    /// <summary>Something is drawn inside the circle: two pens' square of ink, at least, within half its radius of the
    /// region's centre.</summary>
    private static bool Marked(SymbolRegion region, double w)
    {
        var m = region.Mask;
        double cx = m.Width / 2.0, cy = m.Height / 2.0, r = 0.25 * Math.Min(m.Width, m.Height);
        int ink = 0;
        for (int y = (int)(cy - r); y <= (int)(cy + r); y++)
            for (int x = (int)(cx - r); x <= (int)(cx + r); x++)
                if ((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy) <= r * r && m.At(x, y)) ink++;
        return ink >= 2 * w * w;
    }

    /// <summary>One attachment and bars shrinking away from it: the ink's extent across, row by row going in, never
    /// grows by more than w and ends at least 2 w shorter than it starts.</summary>
    private static bool Ground(SymbolRegion region, RegionShape shape, double w)
    {
        if (region.Attachments.Count != 1) return false;
        var a = region.Attachments[0];
        var (cx, cy) = shape.Contacts[0];
        double ux = a.DirX, uy = a.DirY;
        if (ux == 0 && uy == 0)
        {
            double dx = region.Box.CentreX - cx, dy = region.Box.CentreY - cy, l = Math.Sqrt(dx * dx + dy * dy);
            if (l == 0) return false;
            (ux, uy) = (dx / l, dy / l);
        }
        var f = new Frame(cx, cy, ux, uy, 0);
        var rows = Bins(shape.Ink.Select(p => f.Local(p)).Where(p => p.T >= -w), 1, p => p.A, b => b.Max() - b.Min() + 1)
                   .Where(e => e >= 2 * w).ToList();
        if (rows.Count < 2 || rows[0] < rows[^1] + 2 * w) return false;
        double widest = rows[0];
        foreach (var e in rows)
        {
            if (e > widest + w) return false;
            widest = Math.Max(widest, e);
        }
        return true;
    }

    private readonly record struct Frame(double Ox, double Oy, double Ux, double Uy, double Length)
    {
        public (double T, double A) Local((double X, double Y) p)
        {
            double dx = p.X - Ox, dy = p.Y - Oy;
            return (dx * Ux + dy * Uy, -dx * Uy + dy * Ux);
        }
    }

    /// <summary>The frame from where the candidate's first pin's wire meets the region to where its second's does.</summary>
    private static Frame? Axis(SymbolCandidate c, RegionShape shape)
    {
        if (c.PinAttachments.Count != 2) return null;
        var a = shape.Contacts[c.PinAttachments[0]];
        var b = shape.Contacts[c.PinAttachments[1]];
        double dx = b.X - a.X, dy = b.Y - a.Y, l = Math.Sqrt(dx * dx + dy * dy);
        return l < 1 ? null : new Frame(a.X, a.Y, dx / l, dy / l, l);
    }

    /// <summary>Points grouped into bins of <paramref name="size"/> along, in order, each reduced; empty bins skipped.</summary>
    private static List<double> Bins(IEnumerable<(double T, double A)> pts, double size, Func<(double T, double A), double> value,
                                     Func<List<double>, double> reduce) =>
        [.. pts.GroupBy(p => (int)Math.Floor(p.T / size)).OrderBy(g => g.Key).Select(g => reduce([.. g.Select(value)]))];

    private static double Extent(List<(double T, double A)> pts) => pts.Count == 0 ? 0 : pts.Max(p => p.A) - pts.Min(p => p.A);
}

/// <summary>A variable resistor's arrow across a two-pin body (R-imf5-3): the longest straight run of ink crossing the
/// axis between the leads at 20°–70° to it, longer than 1.2 s or half the body — no zig-zag leg is, no box side crosses
/// the axis — taken
/// off the region's mask with the arrowhead at its wider end.</summary>
internal static class CrossingArrow
{
    public static BinaryImage? Find(SymbolRegion region, double w, double s)
    {
        if (region.Attachments.Count != 2) return null;
        var mask = region.Mask;
        int ox = region.Box.Left, oy = region.Box.Top;
        var (a, b) = (region.Attachments[0], region.Attachments[1]);
        double ax = a.X - ox, ay = a.Y - oy, len = Math.Sqrt(Sq(b.X - a.X) + Sq(b.Y - a.Y));
        if (len < 4) return null;
        double ux = (b.X - a.X) / len, uy = (b.Y - a.Y) / len;
        bool Ink(double x, double y) => mask.At((int)Math.Floor(x), (int)Math.Floor(y));

        // The run of ink along (dx, dy) through (px, py), allowing gaps of a pixel and a half: its two ends, as distances
        // from the point (negative behind it), or null when the point is not on it.
        (double Lo, double Hi)? Run(double px, double py, double dx, double dy)
        {
            double reach = len + s, step = 0.5;
            double Walk(int sign)
            {
                double last = 0, gap = 0;
                for (double t = step; t <= reach; t += step)
                {
                    if (Ink(px + sign * t * dx, py + sign * t * dy)) { last = t; gap = 0; }
                    else if ((gap += step) > 1.5) break;
                }
                return last;
            }
            bool on = false;
            for (double e = -1; e <= 1 && !on; e += 0.5) on = Ink(px + e * -dy, py + e * dx);
            return on ? (-Walk(-1), Walk(1)) : null;
        }

        // Longer than 1.2 s, or than half the body where s is only derived: a zig-zag's leg is a third of the body, and
        // what is left of a shaft whose tail was wire still crosses the whole of it.
        double best = Math.Min(1.2 * s, 0.5 * len);
        (double Px, double Py, double Dx, double Dy, double Lo, double Hi)? line = null;
        for (int deg = 20; deg <= 160; deg += 2)
        {
            if (deg > 70 && deg < 110) continue;
            double r = deg * Math.PI / 180, dx = ux * Math.Cos(r) - uy * Math.Sin(r), dy = ux * Math.Sin(r) + uy * Math.Cos(r);
            for (double t = 0.2 * len; t <= 0.8 * len; t += 1)
            {
                double px = ax + t * ux, py = ay + t * uy;
                if (Run(px, py, dx, dy) is not { } run || run.Hi - run.Lo <= best) continue;
                best = run.Hi - run.Lo;
                line = (px, py, dx, dy, run.Lo, run.Hi);
            }
        }
        if (line is not { } l) return null;

        // The ink across the line at t, contiguous through it: its two ends across.
        (int Lo, int Hi) Across(double t)
        {
            double cx = l.Px + t * l.Dx, cy = l.Py + t * l.Dy;
            int lo = 0, hi = 0;
            while (lo > -2 * s && Ink(cx + (lo - 1) * -l.Dy, cy + (lo - 1) * l.Dx)) lo--;
            while (hi < 2 * s && Ink(cx + (hi + 1) * -l.Dy, cy + (hi + 1) * l.Dx)) hi++;
            return (lo, hi);
        }
        double Wide(double from, double to) =>
            Enumerable.Range(0, 8).Select(k => Across(from + (to - from) * k / 7.0)).Average(c => c.Hi - c.Lo + 1);

        var erased = mask.Clone();
        void Clear(double x, double y)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            if (erased.At(ix, iy)) erased.Pixels[iy * erased.Width + ix] = 0;
        }
        // The shaft: a corridor half a pen and a pixel and a half either side.
        double half = w / 2 + 1.5;
        for (double t = l.Lo; t <= l.Hi; t += 0.5)
            for (double e = -half; e <= half; e += 0.5)
                Clear(l.Px + t * l.Dx + e * -l.Dy, l.Py + t * l.Dy + e * l.Dx);
        // The head: at the wider end, every cross-section wider than 1.2 w, inward from the tip until one is not.
        double tail = 0.35 * (l.Hi - l.Lo);
        bool atHi = Wide(l.Hi - tail, l.Hi) >= Wide(l.Lo, l.Lo + tail);
        bool seen = false;
        for (double k = 0; k <= tail; k += 0.5)
        {
            double t = atHi ? l.Hi - k : l.Lo + k;
            var (lo, hi) = Across(t);
            if (hi - lo + 1 > 1.2 * w) seen = true;
            else if (seen) break;
            else continue;
            for (double e = lo - 1; e <= hi + 1; e += 0.5) Clear(l.Px + t * l.Dx + e * -l.Dy, l.Py + t * l.Dy + e * l.Dx);
        }
        return erased;
    }

    private static double Sq(double v) => v * v;
}
