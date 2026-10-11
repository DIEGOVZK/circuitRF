// The words of a plot picture — brief-img-14-sparameter-plots-to-touchstone.md R-im14-3, R-im14-6, R-im14-7.
//
// THE READER IS IM-9's (StrokeTextReader), under a plot's own grammar instead of a schematic's. A tick label is read
// under a NUMERIC grammar whose alphabet is the digits, the sign, the point, an exponent's e and the multiplier
// letters: a typeset 6 that matched an "a" a little better is still a 6, because nothing else may stand there. Every
// other word — an axis title, a legend entry, a marker readout — is read as text under a lenient grammar, and what it
// SAYS is PlotQuantity's and PlotMarkers' business. An unread word is unread, never guessed (R-im14-6).
//
// A WORD is found by its shape, as IM-7 finds one: the small pieces of ink left when the long lines are set aside,
// grouped side by side on one band. Unlike a schematic's, a lone glyph is a word here — a tick label is very often
// one digit. A column of glyphs is a word turned 90° (a y-axis title) when three or more stack with letter gaps.

using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Design.Schematic.Recognition;

namespace CircuitRF.Design.Imaging.Plots;

/// <summary>One word of a plot picture.</summary>
/// <param name="Id">Its number among the picture's words, reading order.</param>
/// <param name="Box">Where it is, pixels.</param>
/// <param name="Vertical">It reads up (or down) the page.</param>
/// <param name="Text">What it says; empty when unread.</param>
/// <param name="Number">Its value when it reads as a number (a tick label), in the label's own units with any
/// multiplier applied (<c>2G</c> is 2e9); null otherwise.</param>
/// <param name="Distance">The mean distance of its glyphs to their templates, cap heights: how well it read.</param>
/// <param name="Rgb">The colour of its ink, 0xRRGGBB — a legend's or an axis title's colour is a trace's.</param>
public sealed record PlotWord(int Id, PixelBox Box, bool Vertical, string Text, double? Number, double Distance, int Rgb)
{
    public bool Read => Text.Length > 0;

    /// <summary>The word's height across its line, pixels.</summary>
    public int Height => Vertical ? Box.Width : Box.Height;

    /// <summary>A unit word on a number (<c>2GHz</c>): its SI multiplier; null when none was written.</summary>
    public double? UnitScale { get; init; }
}

/// <summary>Finds and reads the words of a plot picture.</summary>
public static class PlotText
{
    /// <summary>A glyph is at most this share of the picture's height tall (or 40 px, whichever is more).</summary>
    public const double MaxGlyphShare = 0.06;

    /// <summary>A glyph is at least this many pixels tall: below it nothing reads.</summary>
    public const int MinGlyphPx = 5;

    /// <summary>Glyphs of one word stand at most this many heights apart.</summary>
    public const double LetterGap = 0.9;

    private const string NumberCharacters = "0123456789-+.eEkKMGTmunpμµHz";

    /// <summary>A tick label: a signed decimal with an optional exponent and an optional multiplier or unit.</summary>
    private static readonly Regex NumberShape = new(
        @"^(?<n>[-+]?(\d+(\.\d*)?|\.\d+)([eE][-+]?\d{1,2})?)(?<m>[kKMGTmunpμµ]?)(?<u>Hz)?$", RegexOptions.CultureInvariant);

    /// <summary>The numeric grammar a tick label is read under.</summary>
    public static WordGrammar NumberGrammar { get; } = new(ParseNumber, c => NumberCharacters.Contains(c));

    /// <summary>The lenient grammar every other word is read under: two characters or more, one a letter.</summary>
    public static WordGrammar TitleGrammar { get; } = new(ParseTitle, AnySpacing: true, MaxWeight: 0.34);

    /// <summary>A tick label's value, or null.</summary>
    public static double? Number(string text, out double? unitScale)
    {
        unitScale = null;
        var m = NumberShape.Match(text.Replace('−', '-').Replace(" ", ""));
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
        string mult = m.Groups["m"].Value;
        double scale = mult switch
        {
            "k" or "K" => 1e3, "M" => 1e6, "G" => 1e9, "T" => 1e12, "m" => 1e-3, "u" or "μ" or "µ" => 1e-6, "n" => 1e-9, "p" => 1e-12,
            _ => 1,
        };
        if (mult.Length > 0 || m.Groups["u"].Success) unitScale = scale;
        return v * scale;
    }

    private static WordReading? ParseNumber(string text) =>
        Number(text, out _) is { } v ? new WordReading(text, WordClass.Value, new WordValue(v, [], false)) : null;

    private static WordReading? ParseTitle(string text)
    {
        string t = text.Trim();
        if (t.Replace(" ", "").Length < 2 || !t.Any(char.IsLetter)) return null;
        return new WordReading(t, WordClass.Name);
    }

    /// <summary>
    /// Every word on <paramref name="ink"/> outside <paramref name="lines"/> and <paramref name="exclude"/>, read.
    /// <paramref name="numberAt"/> says where a word is a tick label (beside an axis), which is read numeric first.
    /// </summary>
    public static IReadOnlyList<PlotWord> Read(PlotInk ink, IReadOnlyList<PlotLine> lines, Func<PixelBox, bool> numberAt,
                                               Func<int, int, bool>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(ink);
        int w = ink.Width, h = ink.Height;
        var mask = new BinaryImage(w, h);
        for (int i = 0; i < mask.Pixels.Length; i++) mask.Pixels[i] = ink.Mask[i];
        foreach (var l in lines)
        {
            int c0 = (int)Math.Floor(l.Position - l.Thickness / 2.0 - 0.5), c1 = (int)Math.Ceiling(l.Position + l.Thickness / 2.0 + 0.5);
            for (int a = l.From; a <= l.To; a++)
                for (int c = c0; c <= c1; c++)
                {
                    int x = l.Horizontal ? a : c, y = l.Horizontal ? c : a;
                    // Only the line's own colour: a curve crossing a grid line keeps its pixels, so it stays one piece
                    // and is never offered as a word.
                    if ((uint)x < (uint)w && (uint)y < (uint)h && PlotInk.Between(ink.Raster.Rgb(x, y), l.Rgb, ink.Background)) mask.Pixels[y * w + x] = 0;
                }
        }
        if (exclude is not null)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (mask.Pixels[y * w + x] != 0 && exclude(x, y)) mask.Pixels[y * w + x] = 0;

        // The pieces twice: of all the ink, and of the uncoloured ink alone — a dark label touching a coloured curve is
        // one piece with the curve in the first and a piece of its own in the second. A piece found both ways is kept once.
        int maxH = Math.Max(40, (int)(MaxGlyphShare * h));
        var cands = new List<Component>();
        var pixels = new Dictionary<int, List<int>>();
        var seen = new HashSet<(int, int, int, int)>();
        var plain = new BinaryImage(w, h);
        for (int i = 0; i < mask.Pixels.Length; i++)
            if (mask.Pixels[i] != 0 && !Chromatic(ink.Raster.Rgb(i % w, i / w))) plain.Pixels[i] = 1;
        foreach (var m in new[] { mask, plain })
        {
            var lab = Components.Label(m);
            // A piece of the second pass inside one of the first's is that piece's own anti-aliased fringe, not a glyph.
            var first = cands.ToList();
            var mine = lab.Components.Where(c => c.Bottom - c.Top + 1 <= maxH && c.Right - c.Left + 1 <= maxH && c.Area >= 2
                                                 && !first.Any(f => c.Left >= f.Left && c.Right <= f.Right && c.Top >= f.Top && c.Bottom <= f.Bottom)
                                                 && !IsTick(c, lines)
                                                 && seen.Add((c.Left, c.Top, c.Right, c.Bottom))).ToList();
            var at = new Dictionary<int, int>();
            foreach (var c in mine)
            {
                int key = cands.Count + 1;
                at[c.Label] = key;
                pixels[key] = [];
                cands.Add(c with { Label = key });
            }
            for (int i = 0; i < lab.Labels.Length; i++)
                if (lab.Labels[i] > 0 && at.TryGetValue(lab.Labels[i], out int key)) pixels[key].Add(i);
        }

        var groups = Group(cands);
        var words = new List<PlotWord>();
        foreach (var (members, vertical) in groups)
        {
            var ordered = members.OrderBy(k => vertical ? cands[k].Top : cands[k].Left).ToList();
            var box = Box(cands[ordered[0]]);
            foreach (int k in ordered) box = box.Union(Box(cands[k]));
            var ink1 = ordered.SelectMany(k => pixels[cands[k].Label]).ToList();
            var glyphs = ordered.Select(k => Box(cands[k])).ToList();
            int cap = vertical ? box.Width : box.Height;
            var region = cap < UpsampleBelowPx
                ? Upsampled(ink, ink1, box, vertical, glyphs, cap)
                : new TextRegion(0, box, vertical, glyphs, TextRegions.Strokes(ink1, box, w), [])
                {
                    InkWidth = SchematicImageReading.RunWidth(TextRegions.Crop(ink1, box, w)),
                };
            double pen = Math.Max(1, region.InkWidth);
            int rgb = InkColour(ink, ink1);

            (string Text, double Distance)? number = null, title = null;
            if (!vertical) number = ReadAs(region, pen, NumberGrammar);
            title = ReadAs(region, pen, TitleGrammar);
            bool atTick = numberAt(box);
            double? value = null, scale = null;
            string text = "";
            double distance = double.PositiveInfinity;
            if (number is { } n && (title is not { } t
                                     || (atTick && !PlotQuantity.IsTitleWord(t.Text))
                                     || n.Distance <= t.Distance + 0.005))
            {
                value = Number(n.Text, out scale);
                (text, distance) = n;
            }
            else if (title is { } tt) (text, distance) = tt;
            words.Add(new PlotWord(0, box, vertical, text, value, Math.Round(distance, 4), rgb) { UnitScale = scale });
        }
        return [.. words.OrderBy(x => x.Box.Top).ThenBy(x => x.Box.Left).Select((x, i) => x with { Id = i })];
    }

    /// <summary>
    /// A tick mark: a thin stub at right angles to a found line, one end on it. With the line taken out it is a short
    /// dash just beside the tick label, and grouped with the label it reads as a trailing minus (<c>-25 -</c>), which no
    /// number grammar takes. A label's own minus is a label's width away from the axis, never on it.
    /// </summary>
    private static bool IsTick(Component c, IReadOnlyList<PlotLine> lines)
    {
        int cw = c.Right - c.Left + 1, ch = c.Bottom - c.Top + 1;
        bool horizontal = ch <= 3 && cw >= 3, vertical = cw <= 3 && ch >= 3;
        if (!horizontal && !vertical) return false;
        foreach (var l in lines)
        {
            if (l.Horizontal == horizontal) continue;   // a tick crosses its line at right angles
            double reach = l.Thickness / 2.0 + 2;
            if (horizontal)
            {
                if (c.Top < l.From - 1 || c.Bottom > l.To + 1) continue;
                if (Math.Abs(c.Right + 0.5 - l.Position) <= reach || Math.Abs(c.Left + 0.5 - l.Position) <= reach) return true;
            }
            else
            {
                if (c.Left < l.From - 1 || c.Right > l.To + 1) continue;
                if (Math.Abs(c.Bottom + 0.5 - l.Position) <= reach || Math.Abs(c.Top + 0.5 - l.Position) <= reach) return true;
            }
        }
        return false;
    }

    /// <summary>A word shorter than this is read from its ink upsampled: a 9 px digit binarised is a few pixels a stroke,
    /// and its skeleton loses the curves that tell a 6 from a 5; the anti-aliased grey upsampled keeps them.</summary>
    public const int UpsampleBelowPx = 24;

    /// <summary>
    /// The word's skeleton from its ink upsampled: each pixel's inkness (its distance from the background, as a share of
    /// the word's darkest) over the word's pieces and the pixel around them, interpolated bilinearly at
    /// <c>UpsampleBelowPx / cap</c> times (2 … 4), thresholded at half, skeletonised, and scaled back to picture pixels.
    /// </summary>
    private static TextRegion Upsampled(PlotInk ink, List<int> pixels, PixelBox box, bool vertical, List<PixelBox> glyphs, int cap)
    {
        int s = Math.Clamp((int)Math.Ceiling((double)UpsampleBelowPx / Math.Max(1, cap)), 2, 4);
        int ox = box.Left - 2, oy = box.Top - 2, bw = box.Width + 4, bh = box.Height + 4;
        var near = new bool[bw * bh];
        foreach (int i in pixels)
        {
            int x = i % ink.Width - ox, y = i / ink.Width - oy;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if ((uint)xx < (uint)bw && (uint)yy < (uint)bh) near[yy * bw + xx] = true;
                }
        }
        var v = new float[bw * bh];
        float max = 1;
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                if (!near[y * bw + x]) continue;
                int px = x + ox, py = y + oy;
                if ((uint)px >= (uint)ink.Width || (uint)py >= (uint)ink.Height) continue;
                float d = PlotInk.Distance(ink.Raster.Rgb(px, py), ink.Background);
                v[y * bw + x] = d;
                max = Math.Max(max, d);
            }
        int uw = bw * s, uh = bh * s;
        var up = new BinaryImage(uw, uh);
        for (int y = 0; y < uh; y++)
        {
            double sy = (y + 0.5) / s - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, bh - 1), y1 = Math.Min(bh - 1, y0 + 1);
            double fy = Math.Clamp(sy - y0, 0, 1);
            for (int x = 0; x < uw; x++)
            {
                double sx = (x + 0.5) / s - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, bw - 1), x1 = Math.Min(bw - 1, x0 + 1);
                double fx = Math.Clamp(sx - x0, 0, 1);
                double val = (v[y0 * bw + x0] * (1 - fx) + v[y0 * bw + x1] * fx) * (1 - fy) + (v[y1 * bw + x0] * (1 - fx) + v[y1 * bw + x1] * fx) * fy;
                if (val >= 0.5 * max) up.Pixels[y * uw + x] = 1;
            }
        }
        var g = SkeletonGraph.Build(up, new SkeletonGraphOptions { MaxThreads = 1 });
        var strokes = new List<Clipper2Lib.PathD>(g.Edges.Count);
        foreach (var e in g.Edges)
        {
            var path = new Clipper2Lib.PathD(e.Polyline.Count);
            foreach (var q in e.Polyline) path.Add(new Clipper2Lib.PointD(q.x / s + ox, q.y / s + oy));
            strokes.Add(path);
        }
        return new TextRegion(0, box, vertical, glyphs, strokes, [])
        {
            InkWidth = SchematicImageReading.RunWidth(up) / s,
        };
    }

    private static (string Text, double Distance)? ReadAs(TextRegion region, double pen, WordGrammar grammar)
    {
        var read = new StrokeTextReader(null, grammar).Read(region, pen);
        if (read.Count == 0 || read.Any(r => r.Reading is null)) return null;
        string text = string.Join(grammar.AnySpacing ? " " : "", read.Select(r => r.Reading!.Text));
        if (grammar.Parse(text) is null) return null;
        int glyphs = Math.Max(1, read.Sum(r => r.Glyphs.Count));
        return (text, read.Sum(r => r.Distance * Math.Max(1, r.Glyphs.Count)) / glyphs);
    }

    /// <summary>The ink's colour: the pixel farthest from the background's, of the word's own ink.</summary>
    private static int InkColour(PlotInk ink, List<int> pixels)
    {
        int best = ink.Background, far = -1;
        foreach (int i in pixels)
        {
            int rgb = ink.Raster.Rgb(i % ink.Width, i / ink.Width);
            int d = PlotInk.Distance(rgb, ink.Background);
            if (d > far) { far = d; best = rgb; }
        }
        return best;
    }

    private static PixelBox Box(Component c) => new(c.Left, c.Top, c.Right, c.Bottom);

    /// <summary>A strongly coloured pixel — a curve's, not the grey or black of text and grid.</summary>
    public static bool Chromatic(int rgb)
    {
        int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 60;
    }

    /// <summary>Words: tall pieces side by side on one band, with the short ones (a minus, a point) in their band; then
    /// the pieces left alone that stack three or more in a column (a turned title).</summary>
    private static List<(List<int> Members, bool Vertical)> Group(List<Component> c)
    {
        int n = c.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
        double H(int i) => c[i].Bottom - c[i].Top + 1;
        double W(int i) => c[i].Right - c[i].Left + 1;

        var tall = Enumerable.Range(0, n).Where(i => H(i) >= MinGlyphPx).OrderBy(i => c[i].Left).ToList();
        for (int a = 0; a < tall.Count; a++)
        {
            int i = tall[a];
            for (int b = a + 1; b < tall.Count; b++)
            {
                int j = tall[b];
                double hi = Math.Max(H(i), H(j)), lo = Math.Min(H(i), H(j));
                if (c[j].Left - c[i].Right - 1 > LetterGap * hi) { if (c[j].Left > c[i].Right + 3 * hi) break; continue; }
                if (hi > 1.8 * lo) continue;
                double ov = Math.Min(c[i].Bottom, c[j].Bottom) - Math.Max(c[i].Top, c[j].Top) + 1;
                if (ov < 0.6 * lo) continue;
                if (c[j].Left - c[i].Right - 1 < -0.3 * hi) continue;
                parent[Find(j)] = Find(i);
            }
        }
        // Short pieces join the group whose band they sit in, beside it.
        var groupOf = tall.GroupBy(Find).ToDictionary(g => g.Key, g => g.ToList());
        foreach (int i in Enumerable.Range(0, n).Where(i => H(i) < MinGlyphPx || W(i) > 1.6 * H(i)).OrderBy(i => c[i].Left))
        {
            if (groupOf.ContainsKey(Find(i)) && groupOf[Find(i)].Count > 1) continue;
            int best = -1;
            double bestGap = double.MaxValue;
            foreach (var (root, members) in groupOf)
            {
                if (root == Find(i) || members.Contains(i)) continue;
                int top = members.Min(m => c[m].Top), bottom = members.Max(m => c[m].Bottom);
                int left = members.Min(m => c[m].Left), right = members.Max(m => c[m].Right);
                double gh = bottom - top + 1;
                if (H(i) > 0.7 * gh || W(i) > 1.3 * gh) continue;
                double cy = (c[i].Top + c[i].Bottom) / 2.0;
                if (cy < top - 0.1 * gh || cy > bottom + 0.1 * gh) continue;
                double gap = Math.Max(c[i].Left - right - 1, left - c[i].Right - 1);
                if (gap > LetterGap * gh) continue;
                if (gap < bestGap) { bestGap = gap; best = root; }
            }
            if (best < 0) continue;
            parent[Find(i)] = best;
            groupOf.Remove(i);
            groupOf[best].Add(i);
        }

        var result = new List<(List<int>, bool)>();
        var single = new List<int>();
        foreach (var g in Enumerable.Range(0, n).GroupBy(Find))
        {
            var members = g.ToList();
            if (members.All(m => H(m) < MinGlyphPx && W(m) < MinGlyphPx)) continue;
            if (members.Count == 1) single.Add(members[0]);
            else result.Add((members, false));
        }

        // A column: lone pieces of one width, overlapping across, a letter gap apart down the page.
        var vparent = single.ToDictionary(i => i, i => i);
        int VFind(int a) { while (vparent[a] != a) a = vparent[a] = vparent[vparent[a]]; return a; }
        var byTop = single.OrderBy(i => c[i].Top).ToList();
        for (int a = 0; a < byTop.Count; a++)
            for (int b = a + 1; b < byTop.Count; b++)
            {
                int i = byTop[a], j = byTop[b];
                double hi = Math.Max(W(i), W(j)), lo = Math.Min(W(i), W(j));
                if (c[j].Top - c[i].Bottom - 1 > LetterGap * hi) { if (c[j].Top > c[i].Bottom + 3 * hi) break; continue; }
                double ov = Math.Min(c[i].Right, c[j].Right) - Math.Max(c[i].Left, c[j].Left) + 1;
                if (ov < 0.6 * lo || hi > 1.8 * lo && Math.Min(H(i), H(j)) >= MinGlyphPx && lo >= MinGlyphPx) continue;
                vparent[VFind(j)] = VFind(i);
            }
        foreach (var g in single.GroupBy(VFind))
        {
            var members = g.ToList();
            if (members.Count >= 3) result.Add((members, true));
            else foreach (int m in members) if (H(m) >= MinGlyphPx) result.Add(([m], false));
        }
        return result;
    }
}
