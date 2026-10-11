// The part a datasheet's drawing is for, and the connectors round it — brief-imf-8-datasheet-conventions.md R-imf8-1,
// R-imf8-2, R-imf8-3.
//
// Three readings no template makes, because what they are is how they are wired rather than what they look like:
//  * an AMPLIFIER is a closed triangle (IMF-3 closes it, IMF-4 makes it one region) of any size, filled or hollow,
//    whose leads say which way it points: the one at a corner is its output, the ones on the side facing that corner
//    are its inputs, and any on the other two sides are a ground or a supply. One input and one output is circuitRF's
//    System Amp (O5); two of either is an IC by R-imf8-1's rules;
//  * an IC is a closed box with three or more leads (O6). Its pins are taken in the order an IC package numbers them —
//    down the left side, along the bottom, up the right side, back along the top — which IM-10 replaces with the pin
//    numbers the drawing prints when every signal pin has one;
//  * a COAX CONNECTOR is a small circle with a dot at its centre, its shell's lead (if drawn) going to ground. It is a
//    terminal, as an open circle at a wire's end is (IMF-6).
// Each is read on the region's own ink: the convex hull of a triangle has three corners and ink along all three sides;
// an IC's box is a closed outline the wire reading set aside.

using Clipper2Lib;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>A closed triangle on a picture, read pixels: its apex and the two ends of the side facing it.</summary>
internal sealed record TriangleShape(PointD Apex, PointD BaseA, PointD BaseB)
{
    public double BaseLength => WireGraph.Dist(BaseA, BaseB);
}

internal static class DatasheetSymbols
{
    /// <summary>The variant of a terminal drawn as a coax connector (R-imf8-3).</summary>
    public const string CoaxVariant = "coax";

    /// <summary>The pin names an amplifier reading gives its pins: its input, its output, and any lead on its other two
    /// sides — a ground, or a supply — which the circuit takes into its reference or leaves on its body (R-imf8-2).</summary>
    public const string AmpIn = "in", AmpOut = "out", AmpOther = "aux";

    // ── the coax connector (R-imf8-3) ───────────────────────────────────────────────────────────────────────────

    /// <summary>A region drawn as a coax connector: its largest piece a round ring 4 w to 2 s across, with a dot at its
    /// centre — no other ink inside it — and every lead reaching the ring.</summary>
    public static bool Coax(SymbolRegion r, double w, double s)
    {
        if (r.Attachments.Count is 0 or > 3) return false;
        var m = r.Mask;
        var lab = Components.Label(m);
        if (lab.Components.Count < 2) return false;
        var ring = lab.Components.MaxBy(c => c.Area)!;
        double wd = ring.Right - ring.Left + 1, ht = ring.Bottom - ring.Top + 1, d = (wd + ht) / 2;
        if (d < 4 * w || d > 2 * s || Math.Max(wd, ht) > 1.25 * Math.Min(wd, ht)) return false;
        double cx = (ring.Left + ring.Right + 1) / 2.0, cy = (ring.Top + ring.Bottom + 1) / 2.0, rad = (d - w) / 2;
        int on = 0, inner = 0, centre = 0, stray = 0;
        for (int y = 0; y < m.Height; y++)
            for (int x = 0; x < m.Width; x++)
            {
                int l = lab.Labels[y * m.Width + x];
                if (l == 0) continue;
                double dist = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                if (l == ring.Label) { if (Math.Abs(dist - rad) <= w / 2 + 1.5) on++; if (dist < 0.5 * rad) inner++; }
                else if (dist <= 0.4 * rad) centre++;
                else if (dist < rad - w) stray++;
            }
        // The dot is all there is inside: a source's sine or sign, a transistor's body reaches further out. And it is a
        // DOT, filling its own box as a disc does — a + sign fills under half of its.
        if (on < 0.85 * ring.Area || inner > 0 || stray > 0 || centre < Math.Max(2, 0.5 * w * w)) return false;
        var dots = lab.Components.Where(c => c.Label != ring.Label).ToList();
        int dx0 = dots.Min(c => c.Left), dx1 = dots.Max(c => c.Right), dy0 = dots.Min(c => c.Top), dy1 = dots.Max(c => c.Bottom);
        if (dots.Sum(c => c.Area) < 0.6 * (dx1 - dx0 + 1) * (dy1 - dy0 + 1)) return false;
        double reach = 1.5 * w + 2;
        return r.Attachments.All(a => Math.Abs(Math.Sqrt(Math.Pow(a.X - r.Box.Left - cx, 2) + Math.Pow(a.Y - r.Box.Top - cy, 2)) - rad) <= reach + w);
    }

    // ── the triangle (R-imf8-2) ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The closed triangle a region draws, or null: its ink's convex hull has three corners, its longest side at
    /// least a symbol scale, and ink runs along nine tenths of each side. Its apex is the corner a lead leaves from; of
    /// two such, the one facing the side that runs nearest an axis.</summary>
    public static TriangleShape? Triangle(SymbolRegion r, double w, double s)
    {
        var m = r.Mask;
        var pts = new List<PointD>();
        for (int y = 0; y < m.Height; y++)
            for (int x = 0; x < m.Width; x++)
                if (m[x, y]) pts.Add(new PointD(r.Box.Left + x + 0.5, r.Box.Top + y + 0.5));
        if (pts.Count < 12) return null;
        var hull = Hull(pts);
        double size = Math.Max(r.Box.Width, r.Box.Height);
        if (size < Math.Max(s, 8 * w)) return null;
        var corners = Corners(hull, Math.Max(1.5 * w, 0.06 * size));
        if (corners.Count != 3) return null;
        // Ink along each side, a pen and a half either side of it.
        bool Ink(PointD p)
        {
            int rad = (int)Math.Ceiling(w / 2 + 1.5);
            for (int dy = -rad; dy <= rad; dy++)
                for (int dx = -rad; dx <= rad; dx++)
                    if (m.At((int)Math.Floor(p.x) - r.Box.Left + dx, (int)Math.Floor(p.y) - r.Box.Top + dy)) return true;
            return false;
        }
        for (int k = 0; k < 3; k++)
        {
            var (a, b) = (corners[k], corners[(k + 1) % 3]);
            int hit = 0, n = 40;
            for (int i = 0; i <= n; i++)
            {
                double t = 0.05 + 0.9 * i / n;
                if (Ink(new PointD(a.x + t * (b.x - a.x), a.y + t * (b.y - a.y)))) hit++;
            }
            if (hit < 0.9 * (n + 1)) return null;
        }
        double reach = Math.Max(2 * w, 0.15 * size);
        bool Lead(PointD c) => r.Attachments.Any(at => WireGraph.Dist(new PointD(at.X, at.Y), c) <= reach);
        // How near an axis the side facing corner k runs: 0 along one, π/4 at the diagonal.
        double OffAxis(int k)
        {
            var (a, b) = (corners[(k + 1) % 3], corners[(k + 2) % 3]);
            double ang = Math.Abs(Math.Atan2(b.y - a.y, b.x - a.x)) % (Math.PI / 2);
            return Math.Min(ang, Math.PI / 2 - ang);
        }
        var apexes = Enumerable.Range(0, 3).Where(k => Lead(corners[k])).ToList();
        if (apexes.Count == 0) return null;
        int apex = apexes.MinBy(OffAxis);
        return new TriangleShape(corners[apex], corners[(apex + 1) % 3], corners[(apex + 2) % 3]);
    }

    /// <summary>An amplifier read off its triangle (R-imf8-2): one input on the side facing the apex and one output at it
    /// is circuitRF's Amp, its pins the input, the output and then any lead on the other two sides; anything else — two
    /// inputs, two outputs (an op amp, a differential stage) — is an IC, its pins in package order. Null when no lead
    /// reaches the apex or the side facing it.</summary>
    public static RecognisedSymbol? Amplifier(SymbolRegion r, TriangleShape t, double w)
    {
        double reach = Math.Max(2 * w, 0.15 * t.BaseLength);
        var ins = new List<SymbolAttachment>();
        var outs = new List<SymbolAttachment>();
        var others = new List<SymbolAttachment>();
        foreach (var a in r.Attachments)
        {
            var p = new PointD(a.X, a.Y);
            if (WireGraph.Dist(p, t.Apex) <= reach) { outs.Add(a); continue; }
            var (along, perp) = WireGraph.Local(t.BaseA, t.BaseB, p);
            if (Math.Abs(perp) <= reach && along >= -reach && along <= t.BaseLength + reach) ins.Add(a);
            else others.Add(a);
        }
        if (ins.Count == 0 || outs.Count == 0) return null;
        if (ins.Count > 1 || outs.Count > 1) return Ic(r, PackageOrder(r.Attachments, r.Box), PartConfidence.Medium);
        var pins = new List<RecognisedPin>
        {
            new(0, AmpIn, ins[0].X, ins[0].Y, ins[0].Node),
            new(1, AmpOut, outs[0].X, outs[0].Y, outs[0].Node),
        };
        foreach (var o in others) pins.Add(new RecognisedPin(pins.Count, AmpOther, o.X, o.Y, o.Node));
        return new RecognisedSymbol(r, ImageSymbolKind.Amplifier, SymbolDisposition.Recognised, null, default, pins, 0, null,
                                    PartConfidence.High, []);
    }

    // ── the IC box (R-imf8-1) ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>A region with three or more leads round a closed orthogonal outline that covers most of it: an IC, its
    /// pins in package order round the outline. Null otherwise.</summary>
    public static RecognisedSymbol? IcBox(SymbolRegion r, IReadOnlyList<OutlineBox> outlines, double w)
    {
        if (r.Attachments.Count < 3) return null;
        var box = r.Box;
        var outline = outlines.Where(o => o.Orthogonal && o.X0 >= box.Left - 2 * w && o.X1 <= box.Right + 2 * w
                                          && o.Y0 >= box.Top - 2 * w && o.Y1 <= box.Bottom + 2 * w)
                              .MaxBy(o => (o.X1 - o.X0) * (o.Y1 - o.Y0));
        if (outline is null || (outline.X1 - outline.X0) * (outline.Y1 - outline.Y0) < 0.5 * box.Width * box.Height) return null;
        var frame = new PixelBox((int)Math.Floor(outline.X0), (int)Math.Floor(outline.Y0), (int)Math.Ceiling(outline.X1), (int)Math.Ceiling(outline.Y1));
        return Ic(r, PackageOrder(r.Attachments, frame), PartConfidence.High);
    }

    private static RecognisedSymbol Ic(SymbolRegion r, IReadOnlyList<SymbolAttachment> ordered, PartConfidence confidence) =>
        new(r, ImageSymbolKind.Ic, SymbolDisposition.CutOut, null, default,
            [.. ordered.Select((a, i) => new RecognisedPin(i, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), a.X, a.Y, a.Node))],
            0, null, confidence, []);

    /// <summary>The attachments in the order a package numbers its pins (R-imf8-1): down the left side, along the bottom,
    /// up the right side, back along the top — each by the side of <paramref name="frame"/> it is nearest.</summary>
    internal static List<SymbolAttachment> PackageOrder(IEnumerable<SymbolAttachment> attachments, PixelBox frame)
    {
        double cx = frame.CentreX, cy = frame.CentreY, hw = Math.Max(1, frame.Width / 2.0), hh = Math.Max(1, frame.Height / 2.0);
        static int Side(double nx, double ny) => Math.Abs(nx) >= Math.Abs(ny) ? (nx < 0 ? 0 : 2) : (ny > 0 ? 1 : 3);
        return [.. attachments.Select(a => (A: a, Side: Side((a.X - cx) / hw, (a.Y - cy) / hh)))
                              .OrderBy(t => t.Side)
                              .ThenBy(t => t.Side switch { 0 => t.A.Y, 1 => t.A.X, 2 => -t.A.Y, _ => -t.A.X })
                              .Select(t => t.A)];
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The convex hull, counter-clockwise (monotone chain).</summary>
    private static List<PointD> Hull(List<PointD> pts)
    {
        var p = pts.Distinct().OrderBy(q => q.x).ThenBy(q => q.y).ToList();
        if (p.Count < 3) return p;
        static double Cross(PointD o, PointD a, PointD b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        var h = new List<PointD>();
        foreach (var q in p) { while (h.Count >= 2 && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
        int lower = h.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--) { while (h.Count >= lower && Cross(h[^2], h[^1], p[i]) <= 0) h.RemoveAt(h.Count - 1); h.Add(p[i]); }
        h.RemoveAt(h.Count - 1);
        return h;
    }

    /// <summary>A closed polygon's corners: the fewest of its vertices it stays within <paramref name="tol"/> of
    /// (Douglas–Peucker on the ring, cut at its two farthest points).</summary>
    private static List<PointD> Corners(List<PointD> ring, double tol)
    {
        if (ring.Count < 3) return ring;
        int a = 0, b = 0;
        double far = -1;
        for (int i = 0; i < ring.Count; i++)
            for (int j = i + 1; j < ring.Count; j++)
                if (WireGraph.Dist(ring[i], ring[j]) is var d && d > far) { far = d; a = i; b = j; }
        var keep = new SortedSet<int> { a, b };
        void Split(int from, int to)
        {
            int n = ring.Count, best = -1;
            double bd = tol;
            for (int k = (from + 1) % n; k != to; k = (k + 1) % n)
            {
                double d = WireGraph.DistToSegment(ring[k], ring[from], ring[to]);
                if (d > bd) { bd = d; best = k; }
            }
            if (best < 0) return;
            keep.Add(best);
            Split(from, best);
            Split(best, to);
        }
        Split(a, b);
        Split(b, a);
        return [.. keep.Select(k => ring[k])];
    }
}

/// <summary>An IC's pin numbers and pin names, read off the words round its box (R-imf8-1).</summary>
internal static class IcPins
{
    /// <summary>Each pin's number and name, by <see cref="RecognisedPin.Index"/>. A pin's number is the nearest word that
    /// reads as one — inside the box (in a pin-number box on its outline, or beside the lead's end) or just outside along
    /// its lead; its name the nearest pin name inside the box on its side, else one just outside beside its lead. Each
    /// word is one pin's, the nearest pin's.</summary>
    public static IcPin[] Read(RecognisedSymbol ic, IReadOnlyList<ImageWord> words, double h)
    {
        var pins = ic.Pins;
        var inward = pins.Select(p => ic.Region.Attachments.FirstOrDefault(a => a.Node == p.Node && a.X == p.X && a.Y == p.Y) is { } a
                                          ? (X: a.DirX, Y: a.DirY) : (X: 0.0, Y: 0.0)).ToArray();
        double half = Math.Max(ic.Region.Box.Width, ic.Region.Box.Height) / 2.0;
        // A word in a pin's frame: how far in from the pin's end along its lead (negative outside), how far off it.
        (double Along, double Across) Frame(int k, ImageWord t)
        {
            double dx = t.Box.CentreX - pins[k].X, dy = t.Box.CentreY - pins[k].Y;
            return (dx * inward[k].X + dy * inward[k].Y, Math.Abs(-dx * inward[k].Y + dy * inward[k].X));
        }
        int?[] numbers = new int?[pins.Count];
        string?[] names = new string?[pins.Count];
        var numberClaims = new List<(double D, int Pin, ImageWord Word, int N)>();
        var nameClaims = new List<(double D, int Pin, ImageWord Word, string Name)>();
        for (int k = 0; k < pins.Count; k++)
        {
            if (inward[k] == (0, 0)) continue;
            foreach (var t in words)
            {
                var (along, across) = Frame(k, t);
                if (Number(t) is { } n && along >= -2.5 * h && along <= 3 * h && across <= 1.2 * h)
                    numberClaims.Add((Math.Abs(along) + 2 * across, k, t, n));
                if (Name(t) is { } name)
                {
                    // Inside on the pin's side first; else outside, beside the lead.
                    if (along >= 0 && along <= half && across <= 1.0 * h) nameClaims.Add((along + 2 * across, k, t, name));
                    else if (along < 0 && along >= -4 * h && across <= 2.2 * h) nameClaims.Add((1000 + Math.Abs(along) + across, k, t, name));
                }
            }
        }
        var used = new HashSet<int>();
        foreach (var c in numberClaims.OrderBy(c => c.D))
            if (numbers[c.Pin] is null && used.Add(c.Word.Id)) numbers[c.Pin] = c.N;
        foreach (var c in nameClaims.OrderBy(c => c.D))
            if (names[c.Pin] is null && used.Add(c.Word.Id)) names[c.Pin] = c.Name;
        return [.. Enumerable.Range(0, pins.Count).Select(k => new IcPin(numbers[k], names[k]))];
    }

    /// <summary>The pin number a word is: its first run of digits (<c>2,4</c> → 2, <c>3(1)(5)[3]</c> → 3), or — for a
    /// word of up to three glyphs, the small digits in a pin-number box — each glyph's nearest digit when that is within
    /// the port vocabulary's margin of its best or a character the matcher confuses with it (an L for a 1, an R for an
    /// 8).</summary>
    internal static int? Number(ImageWord t)
    {
        var m = System.Text.RegularExpressions.Regex.Match(t.Text.Trim(), @"^(\d{1,3})(?:[,()\[\]\s]|$)");
        if (m.Success) return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (t.Glyphs.Count is 0 or > 3 || t.Reading is { Class: WordClass.Value or WordClass.Designator }) return null;
        int n = 0;
        foreach (var g in t.Glyphs)
        {
            double reach = Math.Max(g.Best.Distance / 0.6, g.Best.Distance + 0.04);
            var digits = g.Ranked.Where(r => char.IsAsciiDigit(r.Char) && r.Distance <= StrokeTextReader.MaxGlyphDistance
                                             && (r.Distance <= reach || KnownWords.Confused(g.Best.Char, r.Char))).Take(1).ToList();
            if (digits.Count == 0) return null;
            n = 10 * n + (digits[0].Char - '0');
        }
        return n;
    }

    /// <summary>A pin name a word is: a known word (VCC, RFIN, EN, ADJ …), as written or as its glyphs spell it, or any
    /// other name of two letters or more — never a designator, a value or a number.</summary>
    internal static string? Name(ImageWord t)
    {
        string text = t.Text.Trim();
        if (KnownWords.IsKnown(text)) return text.ToUpperInvariant();
        if (t.Reading is { Class: WordClass.Designator or WordClass.Value or WordClass.PartNumber }) return null;
        if (t.Glyphs.Count >= 2 && KnownWords.Snap(t.Glyphs) is { } snap) return snap.Text.ToUpperInvariant();
        return t.Reading is { Class: WordClass.Name } && text.Length >= 2 && text.Count(char.IsLetter) >= 2 ? text : null;
    }

    /// <summary>A pin name that says the pin is ground.</summary>
    public static bool IsGround(string name) => PortWords.Normal(name) is "GND" or "AGND" or "PGND" or "DGND" or "VSS" or "GROUND";

    /// <summary>A pin name that says the pin is wired to nothing.</summary>
    public static bool IsNoConnect(string name) => PortWords.Normal(name) is "NC" or "DNC" or "N/C";
}
