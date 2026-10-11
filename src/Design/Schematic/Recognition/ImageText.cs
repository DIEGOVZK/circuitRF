// A schematic picture's words, read — brief-img-9-text-and-values.md R-im9-1, R-im9-3, R-im9-5 (overview D1, D10).
//
// THE READER IS AS-10's. A word region's skeleton (TextRegions, R-im7-3) is a set of centre lines, which is exactly what
// StrokeGlyphs reads a silkscreen pen's strokes as: the same split into glyphs, the same normalisation to the line's cap
// height, the same modified-Hausdorff distance to the same Hershey templates — with the value glyphs added
// (GlyphTemplates.Text). Typeset sans-serif text skeletonises close to a single-stroke font, which is why this works;
// a serif or script face reads worse, and says so word by word through the distance — it is never refused.
//
// THE GRAMMAR DECIDES A TIE. Each glyph offers every character within AS-10's runner-up margin of its best; the
// combinations are read by ValueGrammar, and the best-ranked grammatical one wins (WordReading.Rank, then the total
// distance). The skeleton of a sans-serif l, I and a flagless 1 is one bar, so "l0pF" is three strings and only
// "10pF" is grammatical. A designator keeps AS-10's own rule on top: a letter is never read where a digit fits better,
// and a digit is read where a letter fits better only after a known prefix.
//
// A line of words. Glyphs farther apart than SpaceGap cap heights have a space between them. The line is read whole
// first — a line set is written with spaces — and, when that has no reading, word by word.

using Clipper2Lib;
using CircuitRF.Design.Layout.Recognition.Silkscreen;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>One word of a schematic picture, read (R-im9-1, R-im9-3).</summary>
/// <param name="Id">Its number among the picture's words, reading order.</param>
/// <param name="Region">The <see cref="TextRegion.Id"/> it came from (a region may hold several words).</param>
/// <param name="Box">Where it is, in the read picture's pixels.</param>
/// <param name="Reading">What it says, or null when it is unread.</param>
/// <param name="Seen">Each glyph's nearest character, as matched before the grammar — what an unread word looked like.</param>
/// <param name="Distance">The mean distance of the glyphs read to their templates, cap heights: how well it read.</param>
/// <param name="Glyphs">The glyphs, for teaching (R-im9-5); empty from a reader that has none.</param>
public sealed record ImageWord(int Id, int Region, PixelBox Box, WordReading? Reading, string Seen, double Distance,
                               IReadOnlyList<GlyphReading> Glyphs)
{
    public WordClass Class => Reading?.Class ?? WordClass.Unread;

    /// <summary>Unread because its type is too heavy for its size (<see cref="StrokeTextReader.MaxWeight"/>).</summary>
    public bool TooHeavy { get; init; }

    /// <summary>The reading's text, or what was seen when there is none.</summary>
    public string Text => Reading?.Text ?? Seen;

    /// <summary>A value written with the designator of the part it is for — <c>CIN = 1µF</c>, as a datasheet lists its
    /// parts' values beside the drawing (R-imf7-4): that designator, as read; null for any other word.</summary>
    public string? For { get; init; }
}

/// <summary>The words of a schematic picture.</summary>
public static class ImageText
{
    /// <summary>Reads every word region of <paramref name="read"/> with <paramref name="reader"/> (the stroke-glyph
    /// reader with the user's glyphs when null) and numbers the words in reading order. Pure.</summary>
    public static IReadOnlyList<ImageWord> Read(SchematicImageRead read, IImageTextReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        // An italic style's words are read against the serif and italic faces too (R-imf7-8); every other word against
        // the stroke font alone.
        var plain = reader ?? new StrokeTextReader();
        var faces = reader ?? new StrokeTextReader(GlyphTemplates.ForUserTypeset());
        var words = new List<ImageWord>();
        double pen = read.TextStrokeWidth > 0 ? read.TextStrokeWidth : read.StrokeWidth;
        var upright = Upright(read.Words);
        for (int i = 0; i < read.Words.Count; i++)
        {
            var region = read.Words[i];
            var read1 = plain.Read(region, pen);
            // An italic style's word is read in the faces too, sheared upright and as drawn, and the best kept (R-imf7-7,
            // R-imf7-8): the shear is the style's, and a glyph that leans less than its style — a subscript's digit —
            // reads worse for it.
            if (!ReferenceEquals(upright[i], region))
            {
                foreach (var other in new[] { faces.Read(upright[i], pen), faces.Read(region, pen) })
                    if (StrokeTextReader.Better(other, read1)) read1 = other;
            }
            // A bold word thinned to a regular stem (R-imf7-1) is read in the faces too: its centre line keeps the
            // bold face's wide D and open 4, nearer the complex faces' than the simplex font's (a 2× bold D1 read Ω1
            // and 240Ω read 2d0Ω against the stroke font alone).
            else if (region.Thinned > 0 && faces.Read(region, pen) is var bold && StrokeTextReader.Better(bold, read1))
                read1 = bold;
            foreach (var word in read1)
                // A word standing on its wire is that wire's name: read as anything else it is a misreading — circuitRF's
                // italic "in" matches 1Ω — and would take a part's place in the assignment.
                words.Add(region.OnWire && word.Reading is { Class: not WordClass.Name } ? word with { Reading = null } : word);
        }
        words = JoinLineSets(words, read.Words);
        words = JoinNamedValues(words);
        words = JoinPhrases(words);
        return [.. words.Select((w, i) => w with { Id = i })];
    }

    // ── slant (R-imf7-7) ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The slant a style is set upright from, degrees: an italic face leans 8–20°.</summary>
    public const double MinSlant = 8, MaxSlant = 20;

    /// <summary>The spread of a style's stems, degrees, under which its slant is one lean and not a mix of shapes.</summary>
    public const double MaxSlantSpread = 4;

    /// <summary>
    /// <paramref name="regions"/> with each italic style's words sheared upright (R-imf7-7). A two-glyph word's own lean
    /// is too noisy to go by (IM-12, design note §13.3), so the slant is measured once per text STYLE — the words of one
    /// height and stroke weight together — from every near-vertical straight stem in every one of its words; a style
    /// leaning 8–20° with a spread under 4° is sheared about each word's baseline. An upright picture is untouched, and
    /// so is a turned word.
    /// </summary>
    internal static IReadOnlyList<TextRegion> Upright(IReadOnlyList<TextRegion> regions)
    {
        var level = regions.Where(r => !r.Vertical && r.Glyphs.Count > 0 && TextRegions.CapHeight(r) > 0).ToList();
        if (level.Count == 0) return regions;
        // Styles: words within 1.3 of each other's cap height and 0.05 of each other's weight, chained.
        var order = level.OrderBy(TextRegions.CapHeight).ToList();
        var styles = new List<List<TextRegion>>();
        foreach (var r in order)
        {
            double h = TextRegions.CapHeight(r), wt = StrokeTextReader.Weight(r);
            var into = styles.FirstOrDefault(st => st.Any(o => h <= 1.3 * TextRegions.CapHeight(o) && Math.Abs(wt - StrokeTextReader.Weight(o)) <= 0.05));
            if (into is null) styles.Add([r]); else into.Add(r);
        }
        var shear = new Dictionary<TextRegion, double>();
        foreach (var style in styles)
        {
            var leans = style.Select(r => (Degrees: Lean(r), Weight: (double)r.Glyphs.Count)).Where(t => !double.IsNaN(t.Degrees)).ToList();
            if (leans.Sum(t => t.Weight) < 6) continue;   // a word or two of three glyphs says too little
            double mean = WeightedMedian(leans);
            double spread = 1.4826 * WeightedMedian([.. leans.Select(t => (Math.Abs(t.Degrees - mean), t.Weight))]);
            if (mean < MinSlant || mean > MaxSlant || spread >= MaxSlantSpread) continue;
            foreach (var r in style) shear[r] = Math.Tan(mean * Math.PI / 180);
        }
        if (shear.Count == 0) return regions;
        return [.. regions.Select(r => shear.TryGetValue(r, out double k) ? Sheared(r, k) : r)];
    }

    private static double WeightedMedian(IReadOnlyList<(double Degrees, double Weight)> xs)
    {
        var sorted = xs.OrderBy(t => t.Degrees).ToList();
        double half = sorted.Sum(t => t.Weight) / 2, run = 0;
        foreach (var t in sorted)
        {
            run += t.Weight;
            if (run >= half) return t.Degrees;
        }
        return sorted[^1].Degrees;
    }

    /// <summary>
    /// A word's lean, degrees (positive leaning right): the shear, −10° to 30° by whole degrees, under which its glyphs are
    /// narrowest — the sum over its glyphs of the width of their skeleton's points, each glyph its own. A stem stands
    /// narrowest upright, and an italic face's stems are what lean; a round letter is as wide at any shear and says
    /// nothing. NaN for a word with no glyph two points tall.
    /// </summary>
    private static double Lean(TextRegion r)
    {
        var points = r.Glyphs.Select(g => r.Strokes.SelectMany(p => p)
                                       .Where(v => v.x >= g.Left - 0.5 && v.x <= g.Right + 0.5 && v.y >= g.Top - 0.5 && v.y <= g.Bottom + 0.5)
                                       .ToList())
                             .Where(ps => ps.Count >= 2 && ps.Max(v => v.y) - ps.Min(v => v.y) >= 2).ToList();
        if (points.Count == 0) return double.NaN;
        double best = double.MaxValue, at = 0;
        for (int deg = -10; deg <= 30; deg++)
        {
            double k = Math.Tan(deg * Math.PI / 180), width = 0;
            foreach (var ps in points)
            {
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (var v in ps) { double x = v.x + k * v.y; lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
                width += hi - lo;
            }
            if (width < best - 1e-9) (best, at) = (width, deg);
        }
        return at;
    }

    /// <summary>A word sheared by <paramref name="k"/> (tan of its slant) about its baseline — its glyphs' median foot:
    /// a point <c>d</c> above it moves <c>k·d</c> left. The glyph boxes move with their centres.</summary>
    private static TextRegion Sheared(TextRegion r, double k)
    {
        var feet = r.Glyphs.Select(g => (double)g.Bottom).Order().ToList();
        double baseline = feet[feet.Count / 2];
        double X(double x, double y) => x - k * (baseline - y);
        var strokes = r.Strokes.Select(p => new PathD(p.Select(v => new PointD(X(v.x, v.y), v.y)))).ToList();
        PixelBox Move(PixelBox g)
        {
            int dx = (int)Math.Round(X(g.CentreX, g.CentreY) - g.CentreX);
            return g with { Left = g.Left + dx, Right = g.Right + dx };
        }
        var glyphs = r.Glyphs.Select(Move).ToList();
        var box = glyphs.Aggregate((a, b) => a.Union(b));
        return r with { Strokes = strokes, Glyphs = glyphs, Box = r.Box with { Left = Math.Min(r.Box.Left, box.Left), Right = Math.Max(r.Box.Right, box.Right) },
                        Splits = [.. r.Splits.Select(sp => sp with { Box = Move(sp.Box) })], Slant = Math.Atan(k) * 180 / Math.PI };
    }

    /// <summary>Word <paramref name="b"/> follows <paramref name="a"/> on its line: level, overlapping by half the shorter
    /// across, a gap no wider than the taller's height.</summary>
    private static bool Follows(ImageWord a, ImageWord b)
    {
        double h = Math.Max(a.Box.Height, b.Box.Height), gap = b.Box.Left - a.Box.Right;
        double overlap = Math.Min(a.Box.Bottom, b.Box.Bottom) - Math.Max(a.Box.Top, b.Box.Top);
        return gap >= -1 && gap <= h && overlap >= 0.5 * Math.Min(a.Box.Height, b.Box.Height);
    }

    private static ImageWord Joined(IReadOnlyList<ImageWord> parts, WordReading reading, string sep = " ")
    {
        int glyphs = parts.Sum(x => x.Glyphs.Count);
        double d = glyphs == 0 ? parts.Max(x => x.Distance) : parts.Sum(x => x.Distance * x.Glyphs.Count) / glyphs;
        var box = parts.Skip(1).Aggregate(parts[0].Box, (b, x) => b.Union(x.Box));
        return new ImageWord(0, parts[0].Region, box, reading, string.Join(sep, parts.Select(x => x.Seen)), d, [.. parts.SelectMany(x => x.Glyphs)]);
    }

    /// <summary>
    /// A value written with the designator of its part — <c>CIN = 1µF</c>, the list a datasheet prints beside its drawing
    /// (R-imf7-4): a designator or a name, an <c>=</c>, a value, one after the other on a line. One word, the value, whose
    /// <see cref="ImageWord.For"/> is the designator: LabelAssociation gives it to the part of that designator, and to
    /// none by distance.
    /// </summary>
    private static List<ImageWord> JoinNamedValues(List<ImageWord> words)
    {
        static bool IsEquals(ImageWord w) => w.Text.Trim() == "=" || w.Seen.Trim() == "=";
        for (int v = 0; v < words.Count; v++)
        {
            var value = words[v];
            if (value.Class != WordClass.Value) continue;
            int e = words.FindIndex(x => IsEquals(x) && Follows(x, value));
            if (e < 0) continue;
            int a = words.FindIndex(x => x.Class is WordClass.Designator or WordClass.Name && Follows(x, words[e]));
            if (a < 0) continue;
            var joined = Joined([words[a], words[e], value], value.Reading!) with { For = words[a].Text };
            foreach (int k in new[] { a, e, v }.OrderDescending()) words.RemoveAt(k);
            words.Insert(Math.Min(Math.Min(a, Math.Min(e, v)), words.Count), joined);
            v = -1;
        }
        return words;
    }

    /// <summary>
    /// Annotations (R-imf7-5): two or more words on one line, or on two lines the second under the first (left-aligned or
    /// centred, at most 1.2 of their height apart), none of them a designator, a value, a line set, a part number, a known
    /// word or a part's word designator, and at least one a name of three letters or more — <c>DC Blocking</c> /
    /// <c>Capacitor</c>, <c>Test Fixture</c>, <c>Vref = 1.25 V</c> — are one comment. A line that is a known word as a
    /// whole (<c>RF In</c>) is not.
    /// </summary>
    private static List<ImageWord> JoinPhrases(List<ImageWord> words)
    {
        // What a phrase may hold: names that are no part's word designator, words unread for no fault of weight, and
        // other comments; and what it must hold: a word of three letters or more that is nothing but a name.
        static bool Plain(ImageWord w) => w.For is null && w.Class switch
        {
            WordClass.Unread => !w.TooHeavy && w.Seen.Length > 0,
            WordClass.Name => w.Reading!.Designates.Count == 0,
            WordClass.Comment => true,
            _ => false,
        };
        static bool Wordy(ImageWord w) => w.Class == WordClass.Name && w.Text.Count(char.IsLetter) >= 3
                                          && !w.Reading!.IsKnownWord && !w.Reading.IsPortWord;
        var used = new HashSet<ImageWord>();
        var phrases = new List<List<ImageWord>>();
        List<ImageWord> Run(ImageWord from)
        {
            var run = new List<ImageWord> { from };
            while (words.FirstOrDefault(x => !used.Contains(x) && !run.Contains(x) && Plain(x) && Follows(run[^1], x)) is { } next) run.Add(next);
            return run;
        }
        foreach (var start in words.OrderBy(w => w.Box.Top).ThenBy(w => w.Box.Left))
        {
            if (used.Contains(start) || !Plain(start)) continue;
            if (words.Any(x => x != start && !used.Contains(x) && Plain(x) && Follows(x, start))) continue;   // not a line's first
            var phrase = Run(start);
            var box = phrase.Skip(1).Aggregate(phrase[0].Box, (b, x) => b.Union(x.Box));
            double h = phrase.Max(x => x.Box.Height);
            // The next line: below this one's caps (its top may reach up past a descender of this one), at most 1.2 h under.
            var under = words.Where(x => !used.Contains(x) && !phrase.Contains(x) && Plain(x) && x.Box.Top >= box.Top + 0.6 * h
                                         && x.Box.Top - box.Bottom <= 1.2 * h
                                         && !words.Any(y => y != x && Plain(y) && Follows(y, x))
                                         && (Math.Abs(x.Box.Left - box.Left) <= 0.5 * h || Math.Abs(x.Box.CentreX - box.CentreX) <= 0.5 * h))
                             .OrderBy(x => x.Box.Top).FirstOrDefault();
            if (under is not null) phrase.AddRange(Run(under).Where(x => !phrase.Contains(x)));
            string text = string.Join(" ", phrase.Select(x => x.Text));
            if (phrase.Count < 2 || !phrase.Any(Wordy) || KnownWords.IsKnown(text) || PortWords.Snap(phrase) is not null) continue;
            used.UnionWith(phrase);
            phrases.Add(phrase);
        }
        if (phrases.Count == 0) return words;
        var result = new List<ImageWord>(words.Count);
        foreach (var w in words)
        {
            if (!used.Contains(w)) { result.Add(w); continue; }
            if (phrases.FirstOrDefault(p => p[0] == w) is { } phrase)
                result.Add(Joined(phrase, new WordReading(string.Join(" ", phrase.Select(x => x.Text)), WordClass.Comment)));
        }
        return result;
    }

    /// <summary>
    /// Words one after the other on one line, each no further from the next than their height, that read together as
    /// one value or one line set, holding no designator and at most one value: a line set written across a comma and a
    /// space (<c>Z=50Ω, E=90°</c>), and a value written with spaces — <c>L = 5 nH</c>, <c>10 pF</c> — which the
    /// spaces, wider than a letter gap, cut into words that each read as nothing (IM-12). The longest such run is
    /// joined first; joined without the spaces, except line sets, which keep one.
    /// </summary>
    private static List<ImageWord> JoinLineSets(List<ImageWord> words, IReadOnlyList<TextRegion> regions)
    {
        bool Vertical(ImageWord x) => regions.FirstOrDefault(r => r.Id == x.Region)?.Vertical ?? false;

        // The word that follows a on its line, across a space, or -1.
        int Next(int i)
        {
            ImageWord a = words[i];
            bool vertical = Vertical(a);
            int best = -1;
            double bestLo = double.MaxValue;
            for (int j = 0; j < words.Count; j++)
            {
                ImageWord b = words[j];
                if (i == j || Vertical(b) != vertical) continue;
                double h = vertical ? Math.Max(a.Box.Width, b.Box.Width) : Math.Max(a.Box.Height, b.Box.Height);
                var (aLo, aHi, bLo, aP0, aP1, bP0, bP1) = vertical
                    ? (a.Box.Top, a.Box.Bottom, b.Box.Top, a.Box.Left, a.Box.Right, b.Box.Left, b.Box.Right)
                    : (a.Box.Left, a.Box.Right, b.Box.Left, a.Box.Top, a.Box.Bottom, b.Box.Top, b.Box.Bottom);
                if (bLo <= aLo || bLo - aHi > h || bLo - aHi < 0) continue;
                if (Math.Min(aP1, bP1) - Math.Max(aP0, bP0) < 0.5 * Math.Min(aP1 - aP0, bP1 - bP0)) continue;
                if (bLo < bestLo) (best, bestLo) = (j, bLo);
            }
            return best;
        }

        bool joined = true;
        while (joined)
        {
            joined = false;
            var next = Enumerable.Range(0, words.Count).Select(Next).ToArray();
            (List<int> Run, WordReading Reading, bool Sets)? found = null;
            for (int i = 0; i < words.Count; i++)
            {
                var run = new List<int> { i };
                while (next[run[^1]] is var k and >= 0 && !run.Contains(k) && run.Count < 8) run.Add(k);
                for (int n = run.Count; n >= 2; n--)
                {
                    var part = run.Take(n).Select(k => words[k]).ToList();
                    if (part.Any(x => x.Class == WordClass.Designator || x.Text.Length == 0)) continue;
                    if (part.Count(x => x.Class == WordClass.Value) > 1) continue;
                    bool sets = part.All(x => x.Class == WordClass.LineParameters);
                    string text = string.Join(sets ? " " : "", part.Select(x => x.Text));
                    if (ValueGrammar.Parse(text) is not { Class: WordClass.LineParameters or WordClass.Value } r) continue;
                    if (found is null || n > found.Value.Run.Count) found = (run.Take(n).ToList(), r, sets);
                    break;
                }
            }
            if (found is not { } f) break;
            var parts = f.Run.Select(k => words[k]).ToList();
            int glyphs = parts.Sum(x => x.Glyphs.Count);
            double d = glyphs == 0 ? parts.Max(x => x.Distance) : parts.Sum(x => x.Distance * x.Glyphs.Count) / glyphs;
            var box = parts.Skip(1).Aggregate(parts[0].Box, (b, x) => b.Union(x.Box));
            var joinedWord = new ImageWord(0, parts[0].Region, box, f.Reading, string.Join(f.Sets ? " " : "", parts.Select(x => x.Seen)), d,
                                           [.. parts.SelectMany(x => x.Glyphs)]);
            int at = f.Run.Min();
            foreach (int k in f.Run.OrderByDescending(k => k)) words.RemoveAt(k);
            words.Insert(Math.Min(at, words.Count), joinedWord);
            joined = true;
        }
        return words;
    }

    /// <summary>
    /// What correcting a word teaches (R-im9-5): each glyph the corrected text says is another character, as a template
    /// of that character. A designator teaches designator glyphs, as AS-10's Learn These Glyphs does; anything else —
    /// a value, a line set, a name — teaches value glyphs, which the silkscreen reader never consults. Null when nothing
    /// differs, the word has no glyphs, or the correction has a different number of characters than the word has glyphs
    /// (then which glyph is which cannot be told).
    /// </summary>
    public static IReadOnlyList<(char Char, Glyph Glyph, GlyphClass Class)>? Lesson(ImageWord word, string corrected)
    {
        ArgumentNullException.ThrowIfNull(word);
        string said = new([.. (corrected ?? "").Where(c => !char.IsWhiteSpace(c))]);
        string read = new([.. word.Text.Where(c => !char.IsWhiteSpace(c))]);
        if (word.Glyphs.Count == 0 || said.Length != word.Glyphs.Count || read.Length != said.Length) return null;
        var cls = ValueGrammar.Parse(said)?.Class == WordClass.Designator ? GlyphClass.Designator : GlyphClass.Value;
        var lesson = new List<(char, Glyph, GlyphClass)>();
        for (int i = 0; i < said.Length; i++)
            if (said[i] != read[i] && !StrokeGlyphs.Alike(said[i], read[i])) lesson.Add((said[i], word.Glyphs[i].Glyph, cls));
        return lesson.Count == 0 ? null : lesson;
    }
}

/// <summary>What a word may say, for <see cref="StrokeTextReader"/>.</summary>
/// <param name="Parse">The word's one reading, or null when it says nothing the grammar knows.</param>
/// <param name="Allowed">The characters a glyph may be read as; null is every template character.</param>
/// <param name="AnySpacing">Any reading may hold a space. A schematic's may only when it is a value or a line set
/// (<c>0.5 pF</c>, <c>Z=50Ω, E=90°</c>); a plot's axis title is words (<c>Return Loss (dB)</c>).</param>
/// <param name="MaxWeight">The heaviest type read, stroke width over cap height; null is
/// <see cref="StrokeTextReader.MaxWeight"/>. A plot's marker names are set bold, and the marker grammar is narrow enough
/// to read them.</param>
/// <param name="Typography">A schematic's typography is read (brief-imf-7): serif feet trimmed, subscripts read as runs
/// of their own, a lone letter read as one glyph, a word the matcher confused snapped to the known word it is. A plot's
/// tick labels have none of it.</param>
public sealed record WordGrammar(Func<string, WordReading?> Parse, Func<char, bool>? Allowed = null, bool AnySpacing = false,
                                 double? MaxWeight = null, bool Typography = false)
{
    /// <summary>A schematic picture's words: <see cref="ValueGrammar.Parse"/>, its typography read.</summary>
    public static WordGrammar Schematic { get; } = new(ValueGrammar.Parse, Typography: true);
}

/// <summary>The in-house reader: AS-10's glyph matcher under the grammar (this file's header).</summary>
public sealed class StrokeTextReader : IImageTextReader
{
    /// <summary>A word whose glyphs lie farther than this from their templates on average, in cap heights, is not
    /// text the templates know — a scribble, a logo — and is unread. Typeset sans-serif glyphs read at 0.02 … 0.07 (a W
    /// at 0.13); a random scrawl's pieces at 0.07 … 0.2, each near SOME letter, so the mean is what tells them apart.</summary>
    public const double MaxWordDistance = 0.09;

    /// <summary>A glyph farther than this from every template makes its word unread.</summary>
    public const double MaxGlyphDistance = 0.2;

    /// <summary>A gap wider than this between glyphs, in cap heights, is a space. A skeleton stops short of its ink and
    /// a typeset digit is narrower than its advance, so letters of one word stand up to ~0.6 apart; a space is ~0.9.</summary>
    public const double SpaceGap = 0.75;

    /// <summary>A glyph reading worse than this, in cap heights, and at least <see cref="MinCutWidth"/> wide may be two
    /// glyphs whose ink touches, and is tried cut in two.</summary>
    public const double CutAbove = 0.1;

    /// <summary>The narrowest glyph tried cut, in cap heights.</summary>
    public const double MinCutWidth = 0.7;

    /// <summary>
    /// A word whose strokes are wider than this share of its cap height is not read: its counters close up — an
    /// <c>=</c> becomes a bar, an <c>N</c>'s diagonal collapses — and it reads WRONG rather than not at all (IBM Plex
    /// Sans Bold at a 14 px cap: <c>IN</c> as the value <c>1H</c>, <c>DNP</c> as <c>QmP</c>). Regular faces stand at
    /// 0.12 … 0.20, bold ones at 0.23 … 0.29 at 14 and 24 px caps alike.
    /// </summary>
    public const double MaxWeight = 0.22;

    /// <summary>
    /// A word heavier than <see cref="MaxWeight"/> is thinned to a regular weight and read (TextRegions, R-imf7-1) — up to
    /// this. Past it the type is too heavy for its size even thinned: at 9 px caps bold stands at 0.33, and IBM Plex Sans
    /// Bold there read 5 of 18 survey words right and 8 wrong (design note §23.1). The heaviest the datasheet fixtures
    /// set, bold at 9 and 18 px caps, stands at 0.23–0.28.
    /// </summary>
    public const double MaxBoldWeight = 0.30;

    /// <summary>A glyph offers every character within this of its best, in cap heights, however well the best fits: a
    /// clean glyph at 0.03 would otherwise offer only what lies within 0.0375, and a typeset 5 and S, 0 and O, θ and 8
    /// stand 0.01 … 0.02 apart.</summary>
    public const double MinReach = 0.02;

    /// <summary>A glyph offers at most this many characters to the grammar.</summary>
    private const int MaxAlternatives = 4;

    /// <summary>The combinations kept while a word's readings are built, cheapest first.</summary>
    private const int Beam = 512;

    private GlyphTemplates? _templates;
    private readonly WordGrammar _grammar;

    /// <param name="templates">The glyphs; null is the user's (built-in designator and value glyphs, plus taught).</param>
    /// <param name="grammar">What a word may say; null is a schematic's (<see cref="ValueGrammar"/>). A plot's tick
    /// labels and titles are read under their own (brief-img-14 R-im14-3).</param>
    public StrokeTextReader(GlyphTemplates? templates = null, WordGrammar? grammar = null)
    {
        _templates = templates;
        _grammar = grammar ?? WordGrammar.Schematic;
    }

    public IReadOnlyList<ImageWord> Read(TextRegion region, double strokeWidth)
    {
        ArgumentNullException.ThrowIfNull(region);
        _templates ??= GlyphTemplates.ForUserText();
        double pen = Math.Max(1, strokeWidth);
        double weight = Weight(region);
        if (region.Thinned == 0 && weight > (_grammar.MaxWeight ?? MaxWeight) || region.Thinned > 0 && weight > MaxBoldWeight)
            return [Unread(region.Box, region.Id, "", double.PositiveInfinity, []) with { TooHeavy = true }];

        var asDrawn = ReadPaths(region, [.. region.Strokes.Where(p => p.Count >= 2)], pen);
        // A serif face's feet, trimmed (R-imf7-6): read both ways, the better kept — a sans t's crossbar is a short
        // branch at a right angle too, and reads worse without it. A schematic's words only: a plot's are sans.
        if (!_grammar.Typography) return asDrawn;
        double cap = TextRegions.CapHeight(region);
        var trimmed = TrimSerifs([.. region.Strokes.Where(p => p.Count >= 2)], cap);
        if (trimmed is null) return asDrawn;
        var feetOff = ReadPaths(region, trimmed, pen);
        return Better(feetOff, asDrawn) ? feetOff : asDrawn;
    }

    /// <summary>Fewer words unread; or as few, more of them designators (then known words); or as many, nearer the
    /// templates.</summary>
    internal static bool Better(IReadOnlyList<ImageWord> a, IReadOnlyList<ImageWord> b)
    {
        int ua = a.Count(w => w.Reading is null), ub = b.Count(w => w.Reading is null);
        if (ua != ub) return ua < ub;
        int na = a.Sum(Named), nb = b.Sum(Named);
        return na != nb ? na > nb : Fit(a) < Fit(b) - 1e-9;

        // A designator counts twice a known word: R1 over the known word RL its glyphs also spell.
        static int Named(ImageWord w) => w.Reading?.Rank switch { 0 => 2, 1 => 1, _ => 0 };
    }

    private static double Fit(IReadOnlyList<ImageWord> ws) =>
        ws.SelectMany(w => w.Glyphs).Select(g => g.Best.Distance).DefaultIfEmpty(double.PositiveInfinity).Average();

    /// <summary>A serif's foot is a terminal branch of the skeleton no longer than this, in cap heights (R-imf7-6).</summary>
    public const double SerifSpur = 0.15;

    /// <summary>
    /// The skeleton without a serif face's feet (R-imf7-6), or null when it has none: every terminal branch — an edge
    /// from a junction to a free end — no longer than <see cref="SerifSpur"/> of the cap height that leaves the stroke it
    /// stands on at more than 45°. The stroke is the junction's longest other edge. A junction left with one edge is
    /// that stem's end; the stem is carried on to where its feet reached along it, closing the gap a bracketed serif
    /// leaves (its skeleton forks a little above the baseline).
    /// </summary>
    internal static List<PathD>? TrimSerifs(List<PathD> paths, double cap)
    {
        if (cap <= 0 || paths.Count < 2) return null;
        const double Same = 1.5;
        bool Near(PointD a, PointD b) => Math.Abs(a.x - b.x) <= Same && Math.Abs(a.y - b.y) <= Same;
        int Degree(PointD at) => paths.Sum(q => (Near(q[0], at) ? 1 : 0) + (Near(q[^1], at) ? 1 : 0));
        static double Length(PathD p) { double l = 0; for (int i = 1; i < p.Count; i++) l += Math.Sqrt(Sq(p[i].x - p[i - 1].x) + Sq(p[i].y - p[i - 1].y)); return l; }
        static double Sq(double v) => v * v;
        // The direction an edge leaves its end at, measured over the first stretch of it.
        static (double X, double Y) Leaving(PathD p, bool fromStart, double stretch)
        {
            var o = fromStart ? p[0] : p[^1];
            PointD far = o;
            double run = 0;
            for (int k = 1; k < p.Count; k++)
            {
                var q = fromStart ? p[k] : p[p.Count - 1 - k];
                var prev = fromStart ? p[k - 1] : p[p.Count - k];
                run += Math.Sqrt(Sq(q.x - prev.x) + Sq(q.y - prev.y));
                far = q;
                if (run >= stretch) break;
            }
            double dx = far.x - o.x, dy = far.y - o.y, l = Math.Sqrt(dx * dx + dy * dy);
            return l < 1e-9 ? (0, 0) : (dx / l, dy / l);
        }

        var spurs = new HashSet<int>();
        for (int i = 0; i < paths.Count; i++)
        {
            var p = paths[i];
            if (Length(p) > SerifSpur * cap) continue;
            bool startFree = Degree(p[0]) == 1, endFree = Degree(p[^1]) == 1;
            if (startFree == endFree) continue;   // an isolated piece, or a bridge between junctions
            var joint = startFree ? p[^1] : p[0];
            // The stroke it stands on: the longest other edge at its junction.
            int stem = -1;
            double longest = 0;
            for (int j = 0; j < paths.Count; j++)
            {
                if (j == i || !(Near(paths[j][0], joint) || Near(paths[j][^1], joint))) continue;
                double l = Length(paths[j]);
                if (l > longest) (stem, longest) = (j, l);
            }
            if (stem < 0 || longest <= SerifSpur * cap) continue;
            var b = Leaving(p, !startFree, SerifSpur * cap);
            var st = Leaving(paths[stem], Near(paths[stem][0], joint), SerifSpur * cap);
            double cos = Math.Abs(b.X * st.X + b.Y * st.Y);
            if (cos < Math.Cos(45 * Math.PI / 180)) spurs.Add(i);
        }
        if (spurs.Count == 0) return null;

        var kept = paths.Where((_, i) => !spurs.Contains(i)).Select(p => new PathD(p)).ToList();
        // A stem whose junction kept only it: carried on as far as its feet reached along it.
        foreach (var joint in spurs.Select(i => Degree(paths[i][0]) == 1 ? paths[i][^1] : paths[i][0]).Distinct())
        {
            var at = kept.Where(q => Near(q[0], joint) || Near(q[^1], joint)).ToList();
            if (at.Count != 1) continue;
            var q = at[0];
            bool start = Near(q[0], joint);
            var into = Leaving(q, start, SerifSpur * cap);   // pointing away from the joint, into the stem
            double reach = spurs.Where(i => Near(paths[i][0], joint) || Near(paths[i][^1], joint))
                                .SelectMany(i => paths[i]).Max(v => -((v.x - joint.x) * into.X + (v.y - joint.y) * into.Y));
            if (reach < 0.5) continue;
            var end = new PointD(joint.x - into.X * reach, joint.y - into.Y * reach);
            if (start) q.Insert(0, end); else q.Add(end);
        }
        return kept;
    }

    private IReadOnlyList<ImageWord> ReadPaths(TextRegion region, List<PathD> paths, double pen)
    {
        // The skeleton in y-up coordinates, as a pen's strokes are, each kink at a free end trimmed (an end another
        // stroke meets is a junction, and trimming there would cut the glyph apart); a piece too small to have a
        // skeleton — a full stop — is a dot at its centre.
        bool Free(int k, PointD end) => !paths.Where((_, j) => j != k).Any(q =>
            Math.Abs(q[0].x - end.x) + Math.Abs(q[0].y - end.y) <= 1.5 || Math.Abs(q[^1].x - end.x) + Math.Abs(q[^1].y - end.y) <= 1.5);
        var strokes = paths
            .Select((p, k) =>
            {
                var xy = new double[2 * p.Count];
                for (int i = 0; i < p.Count; i++) (xy[2 * i], xy[2 * i + 1]) = (p[i].x, -p[i].y);
                return new SilkStroke(TrimEndKinks(xy, pen, Free(k, p[0]), Free(k, p[^1])), pen);
            })
            .ToList();
        foreach (var g in region.Glyphs)
            if (!strokes.Any(st => Points(st.Xy).Any(q => q.X >= g.Left - 0.5 && q.X <= g.Right + 1.5 && -q.Y >= g.Top - 0.5 && -q.Y <= g.Bottom + 1.5)))
                strokes.Add(new SilkStroke([g.CentreX, -g.CentreY, g.CentreX, -g.CentreY], pen));
        var whole = ReadStrokes(region, strokes, pen);
        if (region.Vertical || !_grammar.Typography || Subscript(region.Glyphs) is not { } sub || ReadSubscripted(region, strokes, pen, sub) is not { } split)
            return whole;
        // What follows the subscript, read as the whole line read it where the line already had a space there (R₂ 10kΩ):
        // the better of that and the run read alone.
        if (split.Count > 1)
        {
            int left = split[1].Box.Left;
            var wholeAfter = whole.Where(w => w.Box.Left >= left - 2).ToList();
            if (wholeAfter.Count > 0 && whole.Where(w => w.Box.Left < left - 2).All(w => w.Box.Right < left)
                && Better(wholeAfter, [.. split.Skip(1)]))
                split = [split[0], .. wholeAfter];
        }
        // The geometry has said the word has a subscript: its runs read apart stand unless they leave more unread, or the
        // whole reads as a designator they do not (v_out is a name, never the value 0Uf its glyphs also spell whole).
        int unreadSplit = split.Count(w => w.Reading is null), unreadWhole = whole.Count(w => w.Reading is null);
        bool wholeNamed = whole.Any(w => w.Class == WordClass.Designator), splitNamed = split.Any(w => w.Class == WordClass.Designator);
        return unreadSplit < unreadWhole || unreadSplit == unreadWhole && (splitNamed || !wholeNamed) ? split : whole;
    }

    /// <summary>
    /// Where a subscript stands in a word (R-imf7-9), as IMF-4 joins one to the glyph before it: the first glyph after
    /// one to three base glyphs that is 0.4–0.85 of the glyph before it tall, its foot dropped 0.12–0.6 of that glyph's
    /// height below that glyph's and its top at least 0.3 of it below that glyph's — and the glyphs after it that stand
    /// in its band (a lower top than the base's, no lower a foot than its own): <c>R₁</c>, <c>v_out</c>, <c>L_{n−1}</c>.
    /// The glyphs from <c>To</c> on are what follows on the base line (R₃10kΩ, R-imf7-11). Null when there is none.
    /// </summary>
    internal static (int From, int To)? Subscript(IReadOnlyList<PixelBox> glyphs)
    {
        // The line's baseline: the median foot of its full-height glyphs — a Q's tail drops its own box's foot.
        int tallest = glyphs.Max(g => g.Height);
        var feet = glyphs.Where(g => g.Height >= 0.85 * tallest).Select(g => g.Bottom).Order().ToList();
        int baseline = feet[feet.Count / 2];
        for (int i = 1; i < glyphs.Count && i <= 3; i++)
        {
            var b = glyphs[i - 1];
            var g = glyphs[i];
            double h = b.Height;
            if (g.Height < 0.4 * h || g.Height > 0.85 * h) continue;
            double drop = g.Bottom - Math.Min(b.Bottom, baseline);
            if (drop < 0.12 * h || drop > 0.6 * h || g.Top - b.Top < 0.3 * h) continue;
            int to = i + 1;
            while (to < glyphs.Count && glyphs[to].Top - b.Top >= 0.25 * h && glyphs[to].Bottom <= g.Bottom + 0.15 * h
                   && glyphs[to].Left - glyphs[to - 1].Right <= 0.5 * h) to++;
            return (i, to);
        }
        return null;
    }

    /// <summary>
    /// A word with a subscript, read as its runs (R-imf7-9, R-imf7-11): the base and the subscript each normalised to its
    /// own height — a subscript read at its base's is a comma — and decoded together as one word (<c>R</c> + <c>1</c> is
    /// <c>R1</c>, <c>v</c> + <c>out</c> is <c>vout</c>, <c>L</c> + <c>n-1</c> is <c>Ln-1</c>); what follows on the base
    /// line read as words of its own (a value, or a device's part number). Null when the base and subscript say nothing.
    /// </summary>
    private IReadOnlyList<ImageWord>? ReadSubscripted(TextRegion region, List<SilkStroke> strokes, double pen, (int From, int To) sub)
    {
        var boxes = region.Glyphs;
        List<SilkStroke> In(int from, int to)
        {
            var bs = Enumerable.Range(from, to - from).Select(k => boxes[k]).ToList();
            return [.. strokes.Where(st => Points(st.Xy).Count(q => bs.Any(g => q.X >= g.Left - 1 && q.X <= g.Right + 1 && -q.Y >= g.Top - 1 && -q.Y <= g.Bottom + 1))
                                           * 2 > st.Xy.Length / 2)];
        }
        PixelBox Box(int from, int to) => Enumerable.Range(from, to - from).Select(k => boxes[k]).Aggregate((a, b) => a.Union(b));
        var baseStrokes = In(0, sub.From);
        var subStrokes = In(sub.From, sub.To);
        if (baseStrokes.Count == 0 || subStrokes.Count == 0) return null;
        if (StrokeGlyphs.ReadWord(baseStrokes, _templates!, false) is not { } b) return null;
        // The subscript read at its own height, and at 0.65 of its base's — a lower-case subscript (C_n) read at its own
        // height is a capital — the better decode kept.
        (IReadOnlyList<GlyphReading> Glyphs, (WordReading Reading, double Distance) Read)? best = null;
        foreach (double? height in new double?[] { null, 0.65 * b.Height })
        {
            if (StrokeGlyphs.ReadWord(subStrokes, _templates!, false, height) is not { } sr) continue;
            var gs = b.Glyphs.Concat(sr.Glyphs).ToList();
            if (Decode(gs, [(0, gs.Count)], _grammar) is not { } d || !char.IsLetter(d.Reading.Text[0])) continue;
            if (best is not { } x || d.Reading.Rank < x.Read.Reading.Rank || d.Reading.Rank == x.Read.Reading.Rank && d.Distance < x.Read.Distance)
                best = (gs, d);
        }
        if (best is not { } chosen) return null;
        var glyphs = chosen.Glyphs.ToList();
        var read = chosen.Read;
        var subGlyphs = glyphs.Skip(b.Glyphs.Count).ToList();
        if (read.Reading.Class != WordClass.Designator && SubscriptDesignator(b.Glyphs, subGlyphs) is { } designator) read = designator;
        // A letter subscript read as a digit — R_L as R1 — is the letter where the letter fits clearly better: a subscript
        // is as often a letter (R_L, R_s, C_n) as a number.
        string seen = new([.. glyphs.Select(g => g.Best.Char)]);
        if (read.Reading.Class == WordClass.Designator && read.Reading.Text != seen
            && subGlyphs.All(g => !char.IsAsciiDigit(g.Best.Char)
                                 && g.Ranked.FirstOrDefault(m => char.IsAsciiDigit(m.Char)).Distance > 1.25 * g.Best.Distance)
            && _grammar.Parse(seen) is { Designates.Count: > 0 } lettered)
            read = (lettered, glyphs.Average(g => g.Best.Distance));
        var head = new ImageWord(0, region.Id, Box(0, sub.To), read.Reading, new string([.. glyphs.Select(g => g.Best.Char)]), read.Distance, glyphs);
        if (sub.To >= boxes.Count) return [head];
        var rest = region with { Box = Box(sub.To, boxes.Count), Glyphs = [.. boxes.Skip(sub.To)] };
        var after = ReadStrokes(rest, In(sub.To, boxes.Count), pen);
        return [head, .. after];
    }

    /// <summary>
    /// A subscripted word read as a designator where its glyphs allow one (R-imf7-9): a known prefix whose letters the
    /// base's glyphs give as their best or as a character the matcher confuses with it (an italic R read B, KnownWords),
    /// and digits the subscript's glyphs give within their reach or as a confusion of their best (an italic 1 set upright
    /// reads l) — R₃ whose 3 a J fits a hair better is R3. A subscript
    /// is what a designator's number is set as, which is why the digit may be taken here and not in a word on its line.
    /// </summary>
    private static (WordReading Reading, double Distance)? SubscriptDesignator(IReadOnlyList<GlyphReading> head, IReadOnlyList<GlyphReading> sub)
    {
        if (head.Count is < 1 or > 3 || sub.Count is < 1 or > 4) return null;
        var letters = new List<List<GlyphMatch>>();
        foreach (var g in head)
            letters.Add([.. g.Ranked.Where(m => char.IsAsciiLetterUpper(m.Char) && m.Distance <= MaxGlyphDistance && KnownWords.Confused(g.Best.Char, m.Char))]);
        var digits = new List<GlyphMatch>();
        foreach (var g in sub)
        {
            double reach = Math.Max(g.Best.Distance / (1 - StrokeGlyphs.MinMargin), g.Best.Distance + MinReach) + 1e-9;
            if (g.Ranked.FirstOrDefault(m => char.IsAsciiDigit(m.Char)
                                             && (m.Distance <= reach || m.Distance <= MaxGlyphDistance && KnownWords.Confused(g.Best.Char, m.Char)))
                is not { Char: not '\0' } d) return null;
            digits.Add(d);
        }
        if (digits[0].Char == '0') return null;
        (string Text, double Score)? best = null;
        void Try(int i, string text, double score)
        {
            if (i == letters.Count)
            {
                if (!StrokeGlyphs.KnownPrefixes.Contains(text)) return;
                if (best is null || score < best.Value.Score) best = (text, score);
                return;
            }
            foreach (var m in letters[i]) Try(i + 1, text + m.Char, score + m.Distance);
        }
        Try(0, "", 0);
        if (best is not { } b) return null;
        string all = b.Text + new string([.. digits.Select(d => d.Char)]);
        return ValueGrammar.Parse(all) is { Class: WordClass.Designator } r
            ? (r, (b.Score + digits.Sum(d => d.Distance)) / (head.Count + sub.Count)) : null;
    }

    /// <summary>
    /// All of <paramref name="strokes"/> as ONE glyph, normalised to their whole skeleton as StrokeGlyphs normalises a
    /// glyph (cap height 1, baseline 0, centred), and matched: a lone letter (R-imf7-10) cut into pieces where it touched
    /// a symbol is still one letter, which the split along the line would make two. Null when there is nothing to read.
    /// </summary>
    internal static GlyphReading? OneGlyph(IReadOnlyList<SilkStroke> strokes, GlyphTemplates templates)
    {
        var xs = strokes.SelectMany(st => st.Xy.Where((_, i) => i % 2 == 0)).ToList();
        var ys = strokes.SelectMany(st => st.Xy.Where((_, i) => i % 2 == 1)).ToList();
        if (xs.Count == 0) return null;
        double height = ys.Max() - ys.Min(), cx = (xs.Min() + xs.Max()) / 2, foot = ys.Min();
        if (height <= 0) return null;
        var glyph = new Glyph([.. strokes.Select(st => st.Xy.Select((v, i) => i % 2 == 0 ? (v - cx) / height : (v - foot) / height).ToArray())]);
        return new GlyphReading(glyph, templates.Match(glyph));
    }

    /// <summary>The words of a region's strokes, read: whole, or cut where glyphs touch. A region of one glyph — a lone
    /// letter — is read as one (<see cref="OneGlyph"/>).</summary>
    private IReadOnlyList<ImageWord> ReadStrokes(TextRegion region, List<SilkStroke> strokes, double pen)
    {
        if (region.Glyphs.Count == 1 && !region.Vertical && _grammar.Typography && OneGlyph(strokes, _templates!) is { } one)
        {
            var d = Decode([one], [(0, 1)], _grammar);
            return [d is { } r ? new ImageWord(0, region.Id, region.Box, r.Reading, one.Best.Char.ToString(), r.Distance, [one])
                               : Unread(region.Box, region.Id, one.Best.Char.ToString(), one.Best.Distance, [one])];
        }
        if (strokes.Count == 0 || StrokeGlyphs.ReadWord(strokes, _templates!, region.Vertical) is not { } word)
            return [Unread(region.Box, region.Id, "", double.PositiveInfinity, [])];

        // Along-the-line coordinate, so an extent maps back to pixels and a cut can be placed.
        Func<double, double, double> along = word.Orientation.Rotation switch
        {
            0 => (x, y) => x, 180 => (x, y) => -x, 90 => (x, y) => y, _ => (x, y) => -y,
        };
        double start = strokes.SelectMany(s => Points(s.Xy)).Min(p => along(p.X, p.Y));
        var asSeen = Words(region, word, along, start, _grammar);
        var (cutStrokes, cutWord) = CutTouching(strokes, word, along, start, region.Vertical, pen);
        var result = asSeen;
        if (cutWord.Glyphs.Count != word.Glyphs.Count)
        {
            // A cut stands where it reads more of the line — a typeset W cut in two is a t and a V, which say nothing — or
            // as much of it, nearer the templates: "kΩ" uncut is an m, poorly, and "1m" is grammatical.
            var cut = Words(region, cutWord, along, cutStrokes.SelectMany(s => Points(s.Xy)).Min(p => along(p.X, p.Y)), _grammar);
            int unreadCut = cut.Count(w => w.Reading is null), unreadSeen = asSeen.Count(w => w.Reading is null);
            if (unreadCut < unreadSeen || (unreadCut == unreadSeen && Fit(cut) < Fit(asSeen))) result = cut;
        }
        // A known word whose letters touch (R-imf7-2): a 6 px RFIN inside an IC box is three glyphs, R, an F and I run
        // together, N. A word with no reading, one or two glyphs short of a word of the list, is cut at its widest
        // glyphs until it is as long, and taken when it then spells that word. Only one with no reading: a clean NB
        // cut through its N spelled RFB.
        if (_grammar.Typography && result is [{ Reading: null } only] && only.Glyphs.Count >= 2
            && KnownCut(strokes, word, along, start, region.Vertical, pen) is { } known)
            return [new ImageWord(0, region.Id, region.Box, known.Reading, known.Seen, known.Distance, known.Glyphs)];
        return result;
    }

    /// <summary>The known word <paramref name="word"/>'s strokes spell cut once or twice across its widest glyphs, or
    /// null (see <see cref="ReadStrokes"/>).</summary>
    private (WordReading Reading, string Seen, double Distance, IReadOnlyList<GlyphReading> Glyphs)? KnownCut(
        List<SilkStroke> strokes, (IReadOnlyList<GlyphReading> Glyphs, IReadOnlyList<(double Left, double Right)> Extents,
                                   TextOrientation Orientation, double Height) word,
        Func<double, double, double> along, double start, bool vertical, double pen, int depth = 0)
    {
        int n = word.Glyphs.Count;
        if (!KnownWords.All.Any(k => k.Length == n + 1 || k.Length == n + 2 - depth)) return null;
        (WordReading, string, double, IReadOnlyList<GlyphReading>)? best = null;
        double bestScore = double.MaxValue;
        foreach (int at in Enumerable.Range(0, n).Where(i => word.Extents[i].Right - word.Extents[i].Left >= 0.6)
                                                 .OrderByDescending(i => word.Extents[i].Right - word.Extents[i].Left).Take(2))
        {
            var (l, r) = word.Extents[at];
            for (double f = 0.3; f <= 0.7001; f += 0.1)
            {
                var cut = Cut(strokes, along, start + (l + f * (r - l)) * word.Height, pen);
                if (StrokeGlyphs.ReadWord(cut, _templates!, vertical) is not { } w || w.Glyphs.Count != n + 1) continue;
                if (KnownWords.Snap(w.Glyphs) is { } snap && _grammar.Parse(snap.Text) is { IsKnownWord: true } kr && snap.Distance < bestScore)
                {
                    bestScore = snap.Distance;
                    best = (kr, new string([.. w.Glyphs.Select(g => g.Best.Char)]), snap.Distance, w.Glyphs);
                }
                else if (depth == 0 && best is null
                         && KnownCut(cut, w, along, cut.SelectMany(st => Points(st.Xy)).Min(p => along(p.X, p.Y)), vertical, pen, 1) is { } deeper
                         && deeper.Distance < bestScore)
                {
                    bestScore = deeper.Distance;
                    best = deeper;
                }
            }
        }
        return best;
    }

    /// <summary>How much farther apart a heavy word's skeletons stand than a regular one's, cap heights: a skeleton stops
    /// half a stroke short of its ink on each side, so letters of a bold word thinned to a regular weight (R-imf7-1)
    /// stand its excess weight farther apart — Plex Bold's IN was the value "1 H" across the gap.</summary>
    private static double Heavier(TextRegion region) => region.Thinned > 0 ? Math.Max(0, Weight(region) - TextRegions.RegularWeight) : 0;

    /// <summary>The words of a read line: whole when it reads whole, else token by token.</summary>
    private static IReadOnlyList<ImageWord> Words(TextRegion region, (IReadOnlyList<GlyphReading> Glyphs,
        IReadOnlyList<(double Left, double Right)> Extents, TextOrientation Orientation, double Height) word,
        Func<double, double, double> along, double start, WordGrammar grammar)
    {
        // Tokens: runs of glyphs with no space between them.
        var tokens = new List<(int From, int To)>();
        int from = 0;
        for (int i = 1; i <= word.Glyphs.Count; i++)
            if (i == word.Glyphs.Count || word.Extents[i].Left - word.Extents[i - 1].Right > SpaceGap + Heavier(region))
            {
                tokens.Add((from, i));
                from = i;
            }

        // The whole line first, with its spaces; then word by word.
        if (Decode(word.Glyphs, tokens, grammar) is { } whole)
            return [Word(region, word, tokens[0].From, tokens[^1].To, whole.Reading, whole.Distance, along, start)];
        var result = new List<ImageWord>(tokens.Count);
        foreach (var t in tokens)
        {
            var one = Decode(word.Glyphs, [t], grammar);
            result.Add(Word(region, word, t.From, t.To, one?.Reading, one?.Distance ?? Mean(word.Glyphs, t), along, start));
        }
        return result;
    }

    /// <summary>The word's stroke width over its cap height — the median height of its glyphs at least half as tall as
    /// the tallest, so an <c>=</c>'s bars and a full stop do not count; 0 when the width was not measured.</summary>
    internal static double Weight(TextRegion region) =>
        region.InkWidth <= 0 || region.Glyphs.Count == 0 ? 0 : region.InkWidth / TextRegions.CapHeight(region);

    /// <summary>
    /// <paramref name="xy"/> without the kink thinning leaves at a square stroke end: a final run no longer than a pen,
    /// and no longer than 2 px, that turns more than 35° off the stroke's next four times that, which must be straight. Small as it is, it widens the glyph's box, and the
    /// glyph is centred on its box — a typeset I with a one-pixel kink at its foot stood 0.08 cap heights off centre and
    /// read as a t. A stroke shorter than four pens is left whole, and only a FREE end is trimmed.
    /// </summary>
    internal static double[] TrimEndKinks(double[] xy, double pen, bool firstFree = true, bool lastFree = true)
    {
        int n = xy.Length / 2;
        if (n < 3) return xy;
        double total = 0;
        for (int i = 1; i < n; i++) total += Dist(xy, i - 1, i);
        if (total < 4 * pen) return xy;

        // The index the end at `from` keeps, walking by `step`: its first point at least a pen in, when the run up to it
        // turns off the stroke beyond.
        double kink = Math.Min(pen, 2);   // thinning curls a pixel or two at a square end, whatever the pen
        int Keep(int from, int step)
        {
            int j = from;
            double run = 0;
            while (j + step >= 0 && j + step < n && run < kink) { run += Dist(xy, j, j + step); j += step; }
            if (run > 1.5 * kink) return from;   // a long first segment: nothing to trim
            int m = j;
            double on = 0;
            while (m + step >= 0 && m + step < n && on < 4 * kink) { on += Dist(xy, m, m + step); m += step; }
            if (on < 4 * kink - 1) return from;
            // The run beyond must be straight: a kink ends a stem, not a bar turning into a curve or a diagonal.
            double cx = xy[2 * m] - xy[2 * j], cy = xy[2 * m + 1] - xy[2 * j + 1], len = Math.Sqrt(cx * cx + cy * cy);
            for (int k = j; k != m; k += step)
                if (Math.Abs((xy[2 * k] - xy[2 * j]) * cy - (xy[2 * k + 1] - xy[2 * j + 1]) * cx) / len > 0.75) return from;
            double ax = xy[2 * j] - xy[2 * from], ay = xy[2 * j + 1] - xy[2 * from + 1];
            double bx = xy[2 * m] - xy[2 * j], by = xy[2 * m + 1] - xy[2 * j + 1];
            double cos = (ax * bx + ay * by) / Math.Max(1e-12, Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by)));
            return cos < Math.Cos(35 * Math.PI / 180) ? j : from;
        }
        int first = firstFree ? Keep(0, 1) : 0, last = lastFree ? Keep(n - 1, -1) : n - 1;
        if (first == 0 && last == n - 1) return xy;
        return xy[(2 * first)..(2 * last + 2)];
    }

    private static double Dist(double[] xy, int a, int b) =>
        Math.Sqrt((xy[2 * a] - xy[2 * b]) * (xy[2 * a] - xy[2 * b]) + (xy[2 * a + 1] - xy[2 * b + 1]) * (xy[2 * a + 1] - xy[2 * b + 1]));

    /// <summary>
    /// Two glyphs whose ink touches — a typeset <c>k</c> against an <c>Ω</c> — are one glyph to the matcher, and it
    /// reads as neither. Each glyph reading worse than <see cref="CutAbove"/> and at least <see cref="MinCutWidth"/> wide
    /// is cut across the line, at each twentieth from 30 % to 70 % of its width, its strokes stopped a pen short of the
    /// cut on each side; the cut whose two glyphs read best is kept when both read better than the whole did.
    /// </summary>
    private (List<SilkStroke>, (IReadOnlyList<GlyphReading> Glyphs, IReadOnlyList<(double Left, double Right)> Extents,
             TextOrientation Orientation, double Height)) CutTouching(
        List<SilkStroke> strokes, (IReadOnlyList<GlyphReading> Glyphs, IReadOnlyList<(double Left, double Right)> Extents,
                                   TextOrientation Orientation, double Height) word,
        Func<double, double, double> along, double start, bool vertical, double pen)
    {
        var tried = new HashSet<int>();
        for (int pass = 0; pass < 8; pass++)
        {
            int at = -1;
            for (int i = 0; i < word.Glyphs.Count; i++)
                if (!tried.Contains(i) && word.Glyphs[i].Best.Distance > CutAbove
                    && word.Extents[i].Right - word.Extents[i].Left >= MinCutWidth
                    && (at < 0 || word.Glyphs[i].Best.Distance > word.Glyphs[at].Best.Distance)) at = i;
            if (at < 0) break;
            tried.Add(at);

            var (l, r) = word.Extents[at];
            double whole = word.Glyphs[at].Best.Distance;
            (List<SilkStroke> Strokes, double Worse, double Sum, (IReadOnlyList<GlyphReading>, IReadOnlyList<(double, double)>, TextOrientation, double) Word)? best = null;
            for (double f = 0.3; f <= 0.7001; f += 0.05)
            {
                double c = start + (l + f * (r - l)) * word.Height;
                var cut = Cut(strokes, along, c, pen);
                if (StrokeGlyphs.ReadWord(cut, _templates!, vertical) is not { } w || w.Glyphs.Count != word.Glyphs.Count + 1) continue;
                double a = w.Glyphs[at].Best.Distance, b = w.Glyphs[at + 1].Best.Distance;
                if (best is null || a + b < best.Value.Sum) best = (cut, Math.Max(a, b), a + b, w);
            }
            if (best is not { } chosen || chosen.Worse >= whole) continue;
            strokes = chosen.Strokes;
            word = chosen.Word;
            tried = [.. tried.Select(i => i > at ? i + 1 : i)];
            start = strokes.SelectMany(s => Points(s.Xy)).Min(p => along(p.X, p.Y));
        }
        return (strokes, word);
    }

    /// <summary><paramref name="strokes"/> with every piece within <paramref name="gap"/> of the line where
    /// <paramref name="along"/> is <paramref name="c"/> removed, each stroke split there.</summary>
    private static List<SilkStroke> Cut(List<SilkStroke> strokes, Func<double, double, double> along, double c, double gap)
    {
        var result = new List<SilkStroke>(strokes.Count + 2);
        foreach (var st in strokes)
        {
            var xy = st.Xy;
            if (xy.Length < 4)
            {
                if (Math.Abs(along(xy[0], xy[1]) - c) > gap) result.Add(st);
                continue;
            }
            var piece = new List<double>();
            void Flush()
            {
                if (piece.Count >= 2) result.Add(new SilkStroke([.. piece], st.Width));
                piece = [];
            }
            int Side(double a) => a < c - gap ? -1 : a > c + gap ? 1 : 0;
            for (int i = 0; i + 3 < xy.Length; i += 2)
            {
                double x0 = xy[i], y0 = xy[i + 1], x1 = xy[i + 2], y1 = xy[i + 3];
                double a0 = along(x0, y0), a1 = along(x1, y1);
                // The segment clipped to each side, in order along it.
                var cuts = new List<double> { 0, 1 };
                foreach (double edge in new[] { c - gap, c + gap })
                    if ((a0 - edge) * (a1 - edge) < 0) cuts.Add((edge - a0) / (a1 - a0));
                cuts.Sort();
                for (int k = 0; k + 1 < cuts.Count; k++)
                {
                    double tm = 0.5 * (cuts[k] + cuts[k + 1]);
                    if (Side(a0 + tm * (a1 - a0)) == 0) { Flush(); continue; }
                    double xa = x0 + cuts[k] * (x1 - x0), ya = y0 + cuts[k] * (y1 - y0);
                    double xb = x0 + cuts[k + 1] * (x1 - x0), yb = y0 + cuts[k + 1] * (y1 - y0);
                    if (piece.Count == 0 || piece[^2] != xa || piece[^1] != ya) { Flush(); piece.AddRange([xa, ya]); }
                    piece.AddRange([xb, yb]);
                }
            }
            Flush();
        }
        return result;
    }

    private static IEnumerable<(double X, double Y)> Points(double[] xy)
    {
        for (int i = 0; i + 1 < xy.Length; i += 2) yield return (xy[i], xy[i + 1]);
    }

    private static double Mean(IReadOnlyList<GlyphReading> g, (int From, int To) t) =>
        Enumerable.Range(t.From, t.To - t.From).Average(i => g[i].Best.Distance);

    /// <summary>The word of glyphs <paramref name="from"/> … <paramref name="to"/>, its box in pixels.</summary>
    private static ImageWord Word(TextRegion region, (IReadOnlyList<GlyphReading> Glyphs, IReadOnlyList<(double Left, double Right)> Extents,
                                  TextOrientation Orientation, double Height) w, int from, int to, WordReading? reading,
                                  double distance, Func<double, double, double> along, double start)
    {
        var glyphs = w.Glyphs.Skip(from).Take(to - from).ToList();
        string seen = new([.. glyphs.Select(g => g.Best.Char)]);
        var box = region.Box;
        if (from > 0 || to < w.Glyphs.Count)
        {
            // The token's extent along the line, back in pixels (y-up coordinates are the pixels' with y negated).
            double a0 = start + w.Extents[from].Left * w.Height, a1 = start + w.Extents[to - 1].Right * w.Height;
            (double lo, double hi) = w.Orientation.Rotation switch
            {
                0 => (a0, a1), 180 => (-a1, -a0), 90 => (-a1, -a0), _ => (a0, a1),
            };
            int l = (int)Math.Floor(lo) - 1, h = (int)Math.Ceiling(hi) + 1;
            box = region.Vertical
                ? box with { Top = Math.Max(box.Top, l), Bottom = Math.Min(box.Bottom, h) }
                : box with { Left = Math.Max(box.Left, l), Right = Math.Min(box.Right, h) };
        }
        return reading is null
            ? Unread(box, region.Id, seen, distance, glyphs)
            : new ImageWord(0, region.Id, box, reading, seen, distance, glyphs);
    }

    private static ImageWord Unread(PixelBox box, int region, string seen, double distance, IReadOnlyList<GlyphReading> glyphs) =>
        new(0, region, box, null, seen, distance, glyphs);

    /// <summary>
    /// The best grammatical reading of the glyphs in <paramref name="tokens"/> (spaces between them), or null: each
    /// glyph offers the characters within AS-10's runner-up margin of its best; the combinations are parsed, and the
    /// best-ranked one wins, then the one nearest its templates.
    /// </summary>
    internal static (WordReading Reading, double Distance)? Decode(IReadOnlyList<GlyphReading> glyphs, IReadOnlyList<(int From, int To)> tokens,
                                                                  WordGrammar? grammar = null)
    {
        grammar ??= WordGrammar.Schematic;
        var all = tokens.SelectMany(t => Enumerable.Range(t.From, t.To - t.From)).ToList();
        if (all.Count == 0) return null;
        // Under a grammar with an alphabet, a glyph's best is its best IN the alphabet: a tick label's 6 that matched an
        // "a" a little better is still a 6, and the grammar has nothing else to offer it.
        var ranked = all.ToDictionary(i => i, i => grammar.Allowed is { } allowed
            ? (IReadOnlyList<GlyphMatch>)[.. glyphs[i].Ranked.Where(m => allowed(m.Char))]
            : glyphs[i].Ranked);
        if (all.Any(i => ranked[i].Count == 0 || ranked[i][0].Distance > MaxGlyphDistance)) return null;
        if (all.Average(i => ranked[i][0].Distance) > MaxWordDistance) return null;

        var spaceBefore = tokens.Skip(1).Select(t => t.From).ToHashSet();
        bool spaced = tokens.Count > 1;   // only a line set, or a value with its unit apart (0.5 pF), has a space
        var found = Search(false);
        // A word with no reading is searched once more with each glyph also offering the characters the matcher confuses
        // with its best (R-imf7-2) — a bold rating's slash read as an I, its 0 as an O: 1AI10OV is 1A/100V.
        if (found is null && grammar.Typography) found = Search(true);
        var best = found;
        // A known word whose glyphs the matcher confused (R-imf7-2): V1N is VIN, QNO is GND — over any reading that is
        // only a name, a part number or a comment, or none at all.
        if (grammar.Typography && (best is null || best.Value.Reading.Rank >= new WordReading("", WordClass.PartNumber).Rank)
            && KnownWords.Snap([.. all.Select(i => glyphs[i])], spaceBefore.Select(k => all.IndexOf(k)).ToHashSet(), read: best is not null) is { } snap
            && grammar.Parse(snap.Text) is { IsKnownWord: true } kr)
            return (kr, snap.Distance);
        return best is { } x ? (x.Reading, x.Score / all.Count) : null;

        (WordReading Reading, double Score, bool Known)? Search(bool confusions)
        {
        var beam = new List<(string Text, double Score, bool[] Coerced)> { ("", 0, []) };
        foreach (int i in all)
        {
            var g = ranked[i];
            double reach = Math.Max(g[0].Distance / (1 - StrokeGlyphs.MinMargin), g[0].Distance + MinReach) + 1e-9;
            var options = g.Where(m => m.Distance <= reach).Take(MaxAlternatives).ToList();
            // A stem with a dot standing clear above it is an i or a j, never a digit: the regulator's Ci read C1, a
            // designator's digit fitting the stem within the margin.
            if (Dotted(glyphs[i].Glyph) && options.Where(m => !char.IsDigit(m.Char)).ToList() is { Count: > 0 } letters) options = letters;
            if (confusions)
                options.AddRange(g.Where(m => m.Distance > reach && m.Distance <= Math.Min(MaxGlyphDistance, 3 * g[0].Distance + 0.03)
                                              && KnownWords.Confused(g[0].Char, m.Char)).Take(2));
            string sep = spaceBefore.Contains(i) ? " " : "";
            var next = new List<(string, double, bool[])>(beam.Count * options.Count);
            foreach (var (text, score, coerced) in beam)
                foreach (var m in options)
                    next.Add((text + sep + m.Char, score + m.Distance, [.. coerced, m.Distance > g[0].Distance + 1e-9]));
            beam = [.. next.OrderBy(b => b.Item2).Take(Beam)];
        }

        (WordReading Reading, double Score, bool Known)? chosen = null;
        foreach (var (text, score, coerced) in beam)
        {
            if (grammar.Parse(text) is not { } r || !DesignatorRule(r, coerced)) continue;
            if (spaced && !grammar.AnySpacing && r.Class is not (WordClass.LineParameters or WordClass.Value or WordClass.Comment)) continue;
            bool known = r.Class == WordClass.Designator && ValueGrammar.KnownPrefix(r.Text);
            if (chosen is not { } b || r.Rank < b.Reading.Rank
                || (r.Rank == b.Reading.Rank && (known && !b.Known || known == b.Known && score < b.Score)))
                chosen = (r, score, known);
        }
        return chosen;
        }
    }

    /// <summary>Whether a glyph is a stem with a dot clear above it, as an i or a j is drawn: two strokes or more, the
    /// highest no larger than a quarter of the glyph's height either way and wholly above the rest.</summary>
    internal static bool Dotted(Glyph glyph)
    {
        if (glyph.Strokes.Count < 2) return false;
        var boxes = glyph.Strokes.Where(s => s.Length >= 2).Select(s =>
        {
            double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
            for (int k = 0; k + 1 < s.Length; k += 2) { x0 = Math.Min(x0, s[k]); x1 = Math.Max(x1, s[k]); y0 = Math.Min(y0, s[k + 1]); y1 = Math.Max(y1, s[k + 1]); }
            return (X0: x0, X1: x1, Y0: y0, Y1: y1);
        }).ToList();
        if (boxes.Count < 2) return false;
        double top = boxes.Min(b => b.Y0), bottom = boxes.Max(b => b.Y1), height = bottom - top;
        // A normalised glyph's y runs up: the dot is the stroke reaching highest.
        var dot = boxes.MaxBy(b => b.Y1);
        var rest = boxes.Where(b => b != dot).ToList();
        return height > 0 && dot.X1 - dot.X0 <= 0.25 * height && dot.Y1 - dot.Y0 <= 0.25 * height
               && dot.Y0 > rest.Max(b => b.Y1) && rest.Max(b => b.Y1) - rest.Min(b => b.Y0) >= 0.5 * height;
    }

    /// <summary>AS-10's designator rule: no letter where a digit fits better, and a digit where a letter fits better
    /// only after a known prefix.</summary>
    private static bool DesignatorRule(WordReading r, bool[] coerced)
    {
        if (r.Class != WordClass.Designator) return true;
        int letters = r.Text.TakeWhile(char.IsAsciiLetter).Count();
        for (int i = 0; i < coerced.Length; i++)
            if (coerced[i] && (i < letters || !ValueGrammar.KnownPrefix(r.Text))) return false;
        return true;
    }
}
