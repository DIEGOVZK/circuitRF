// The characters a silkscreen glyph is matched against — brief-artsch-10-silkscreen-ocr.md R-as10-3, R-as10-5.
//
// Two kinds of template, both normalised as StrokeGlyphs normalises a glyph (cap height 1, baseline 0, centred):
//   - BUILT IN: the designator characters of the stroke font every layout label is drawn in (StrokeFont, its data and
//     licence in src/Design/resources/stroke-font/ — one copy, brief-silkscreen-stroke-font.md R-ssf-1), plus the few
//     variants of it CAD plotter fonts commonly draw instead, built from the font's own strokes
//     (src/Design/resources/silkscreen-glyphs/). CAD stroke fonts are close relatives of it.
//   - TAUGHT: glyphs a user corrected in the parts table and asked to learn, kept in the per-user state directory
//     (silkscreen-glyphs/taught.json) and used on every later run. Nothing is written to a workspace.
//
// And FACES (brief-imf-7-typography.md R-imf7-8): the serif and italic sets of the same distribution, offered as further
// variants of every character by Typeset alone — the words of a schematic picture's italic style. Text and the
// silkscreen sets never hold them, so neither a plot's tick labels, a board's designators nor an upright picture's words
// can read differently for their being there (offered to every word, they turned a sans F into an italic f and doubled
// the reading time).
//
// And two CLASSES (brief-img-9-text-and-values.md R-im9-2). DESIGNATOR glyphs are all the silkscreen reader ever
// consults — BuiltIn and ForUser hold nothing else, so AS-10's measured behaviour cannot move. VALUE glyphs — the
// font's lower case and . / = ° µ Ω ( ) , plus a θ built from its strokes — are added only by ForText, which reads a
// schematic picture's words, and a glyph taught from a picture's value is kept in that class.

using System.Text;
using System.Text.Json;
using CircuitRF.Design.Layout.Text;

namespace CircuitRF.Design.Layout.Recognition.Silkscreen;

/// <summary>Which reader a template serves (R-im9-2).</summary>
public enum GlyphClass
{
    /// <summary>A designator's characters: the silkscreen reader's whole set.</summary>
    Designator,

    /// <summary>The further characters a schematic's values and names are written in; only a picture's words read them.</summary>
    Value,
}

/// <summary>One template: a character and its strokes.</summary>
/// <param name="Source">Where it came from: the font's file name, or <see cref="GlyphTemplates.TaughtSource"/>.</param>
/// <param name="Class">Which reader it serves.</param>
public sealed record GlyphTemplate(char Char, Glyph Glyph, string Source, GlyphClass Class = GlyphClass.Designator);

/// <summary>A set of templates.</summary>
public sealed class GlyphTemplates
{
    /// <summary>The per-user folder taught glyphs are kept in (under <see cref="UserStateDirectory"/>).</summary>
    public const string TaughtFolder = "silkscreen-glyphs";

    /// <summary>The file in it.</summary>
    public const string TaughtFile = "taught.json";

    /// <summary>The <see cref="GlyphTemplate.Source"/> of a taught glyph.</summary>
    public const string TaughtSource = "taught";

    private const string FontSource = "hershey-roman-simplex";
    private const string VariantResource = "CircuitRF.Design.SilkscreenGlyphs.hershey-variants.txt";
    private const string VariantSource = "hershey-variants";
    private const string ValueVariantResource = "CircuitRF.Design.SilkscreenGlyphs.hershey-value-variants.txt";
    private const string ValueVariantSource = "hershey-value-variants";

    /// <summary>The characters a designator is written in — the font's glyphs that are matching templates. The rest
    /// of the font (lower case, punctuation) draws labels but is never matched: a designator never holds it, and
    /// offering it would only give a silkscreen glyph more ways to be read wrong.</summary>
    public const string DesignatorCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_+";

    /// <summary>The further characters a schematic picture's values, line parameters and names are written in (R-im9-2)
    /// — the font's own glyphs, plus θ from <c>hershey-value-variants.txt</c>.</summary>
    public const string ValueCharacters = "abcdefghijklmnopqrstuvwxyz./=°µΩ(),";

    // The font in its own units: y down, the cap line at -12 and the baseline at 9.
    private const double FontBaseline = StrokeFont.BaselineUnits, FontCapHeight = StrokeFont.CapUnits;

    private GlyphTemplates(IReadOnlyList<GlyphTemplate> all) => All = all;

    /// <summary>Every template.</summary>
    public IReadOnlyList<GlyphTemplate> All { get; }

    private static readonly Lazy<GlyphTemplates> BuiltInSet = new(LoadBuiltIn);

    /// <summary>The built-in font's templates alone.</summary>
    public static GlyphTemplates BuiltIn => BuiltInSet.Value;

    /// <summary>The built-in templates and whatever this user has taught as designator glyphs, read from the per-user
    /// state directory — what a silkscreen recognition matches against, in the GUI, the CLI and the MCP tool alike. A
    /// glyph taught as a value glyph is not among them (R-im9-2).</summary>
    public static GlyphTemplates ForUser() =>
        BuiltIn.With(ReadTaught(UserStateDirectory.SubDir(TaughtFolder)).Where(t => t.Class == GlyphClass.Designator));

    private static readonly Lazy<GlyphTemplates> TextSet = new(() => BuiltIn.With(LoadValueGlyphs()));

    /// <summary>The built-in designator AND value glyphs, in the stroke font alone — what a plot's words are read against.</summary>
    public static GlyphTemplates Text => TextSet.Value;

    /// <summary><see cref="Text"/> and everything this user has taught, of both classes.</summary>
    public static GlyphTemplates ForUserText() => Text.With(ReadTaught(UserStateDirectory.SubDir(TaughtFolder)));

    private static readonly Lazy<GlyphTemplates> TypesetSet = new(() => Text.With(LoadFaces()));

    /// <summary><see cref="Text"/> and the serif and italic faces' glyphs as further variants of each character
    /// (brief-imf-7 R-imf7-8) — what a schematic picture's italic style is read against (ImageText). A glyph's match is
    /// its best over every face (<see cref="Match"/> keeps each character's nearest template), so a word may mix them.</summary>
    public static GlyphTemplates Typeset => TypesetSet.Value;

    /// <summary><see cref="Typeset"/> and everything this user has taught, of both classes.</summary>
    public static GlyphTemplates ForUserTypeset() => Typeset.With(ReadTaught(UserStateDirectory.SubDir(TaughtFolder)));

    /// <summary>This set with <paramref name="more"/> added.</summary>
    public GlyphTemplates With(IEnumerable<GlyphTemplate> more)
    {
        var extra = more.ToList();
        return extra.Count == 0 ? this : new GlyphTemplates([.. All, .. extra]);
    }

    /// <summary>
    /// A taught glyph matches only within this, in cap heights. It is the board's own font, drawn by the same plotter
    /// instructions every time, so the same character matches it to within rounding (0.000 on a field board, where the
    /// font's other characters stood 0.017 and more away). A looser match is another character in the same style —
    /// and taken, it would read every glyph of that font as the few characters taught.
    /// </summary>
    public const double TaughtReach = 0.01;

    /// <summary>Every template's distance from <paramref name="glyph"/>, nearest first, each character once (its
    /// nearest template). A taught template farther than <see cref="TaughtReach"/> takes no part.</summary>
    public IReadOnlyList<GlyphMatch> Match(Glyph glyph)
    {
        var best = new Dictionary<char, double>();
        foreach (var t in All)
        {
            double d = Glyph.Distance(glyph, t.Glyph);
            if (d > TaughtReach && t.Source == TaughtSource) continue;
            if (!best.TryGetValue(t.Char, out double was) || d < was) best[t.Char] = d;
        }
        return [.. best.Select(kv => new GlyphMatch(kv.Key, kv.Value)).OrderBy(m => m.Distance).ThenBy(m => m.Char)];
    }

    /// <summary>
    /// <paramref name="text"/> drawn in the built-in font as centre-line strokes: reading along +x from x = 0, the
    /// baseline on y = 0, cap height <paramref name="capHeight"/> — <see cref="StrokeFont.Layout"/>, the layout every
    /// stroke label is drawn with. What a CAD tool's plotter font writes, for tests and for anyone checking a reading
    /// by eye.
    /// </summary>
    public IReadOnlyList<double[]> Draw(string text, double capHeight) =>
        StrokeFont.Layout(text, LabelFontStyle.Regular, capHeight).Strokes;

    // ── the built-in font ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The font, and the variants of it CAD plotter fonts commonly draw instead (a flagless 1, a serifed 1, a
    /// round-topped 3) — templates only: <see cref="Draw"/> writes the font itself.</summary>
    private static GlyphTemplates LoadBuiltIn()
    {
        var templates = new List<GlyphTemplate>();
        foreach (char ch in DesignatorCharacters)
            templates.Add(new GlyphTemplate(ch, Normalised(StrokeFont.Glyphs[ch].Strokes), FontSource));
        foreach (var g in StrokeFont.ReadGlyphs(VariantResource))
            templates.Add(new GlyphTemplate((char)g.CodePoint, Normalised(g.Strokes), VariantSource));
        return new GlyphTemplates(templates);
    }

    /// <summary>Font-unit strokes as a glyph: cap height 1, baseline 0, y up, centred on the glyph's own box.</summary>
    private static Glyph Normalised(IReadOnlyList<double[]> strokes)
    {
        double minX = strokes.SelectMany(Xs).Min(), maxX = strokes.SelectMany(Xs).Max(), cx = (minX + maxX) / 2;
        return new Glyph([.. strokes.Select(st =>
        {
            var n = new double[st.Length];
            for (int i = 0; i + 1 < st.Length; i += 2)
            {
                n[i] = (st[i] - cx) / FontCapHeight;
                n[i + 1] = (FontBaseline - st[i + 1]) / FontCapHeight;
            }
            return n;
        })]);
    }

    /// <summary>The value class: the font's glyphs of <see cref="ValueCharacters"/>, and the variants file's.</summary>
    private static IEnumerable<GlyphTemplate> LoadValueGlyphs()
    {
        var templates = new List<GlyphTemplate>();
        foreach (char ch in ValueCharacters)
            templates.Add(new GlyphTemplate(ch, Normalised(StrokeFont.Glyphs[ch].Strokes), FontSource, GlyphClass.Value));
        foreach (var g in StrokeFont.ReadGlyphs(ValueVariantResource))
            templates.Add(new GlyphTemplate((char)g.CodePoint, Normalised(g.Strokes), ValueVariantSource, GlyphClass.Value));
        return templates;
    }

    /// <summary>The faces (R-imf7-8): each character a designator glyph where a designator writes it and a value glyph
    /// otherwise, as the stroke font's own are classed. Every face is drawn in the stroke font's frame — cap line −12,
    /// baseline 9 — so a glyph is normalised the same whichever face drew it.</summary>
    private static IEnumerable<GlyphTemplate> LoadFaces()
    {
        foreach (var (resource, source, upright) in Faces)
            foreach (var g in StrokeFont.ReadGlyphs(resource))
            {
                char ch = (char)g.CodePoint;
                // λ and π are drawn in the file but no grammar reads either, and offered they took a bold = (Z=50Ω read
                // Zπ50Ω): a character that can only be a misreading is not a template.
                if (NotOffered.Contains(ch)) continue;
                var cls = DesignatorCharacters.Contains(ch) ? GlyphClass.Designator : GlyphClass.Value;
                var strokes = CentreLines(g.Strokes);
                yield return new GlyphTemplate(ch, Normalised(strokes), source, cls);
                if (upright) yield return new GlyphTemplate(ch, Normalised(Upright(strokes)), source + "-upright", cls);
            }
    }

    private const string NotOffered = "λπ";

    /// <summary>The italic face's lean, degrees: its I's stem runs 6 units right over the 21 of its cap height.</summary>
    private const double ItalicLean = 15.9;

    /// <summary>
    /// An italic glyph set upright — sheared back by the face's lean about the baseline. A schematic picture's italic style
    /// is sheared upright before it is read (ImageText, R-imf7-7); its letterforms stay italic (a flagged 1, a curled t),
    /// and these are what they are matched against.
    /// </summary>
    private static IReadOnlyList<double[]> Upright(IReadOnlyList<double[]> strokes)
    {
        double k = Math.Tan(ItalicLean * Math.PI / 180);
        return [.. strokes.Select(st =>
        {
            var n = (double[])st.Clone();
            for (int i = 0; i + 1 < n.Length; i += 2) n[i] -= (FontBaseline - n[i + 1]) * k;
            return n;
        })];
    }

    /// <summary>
    /// A face glyph as a picture's skeleton draws it. The complex faces draw a thick stem as two or three strokes a unit
    /// apart and a serif as a short bar across a stem's end; a typeset word's skeleton has one centre line, and its
    /// serif spurs are trimmed before it is matched (ImageText, R-imf7-6). Kept as drawn, both cost a template more
    /// than its letterform gains it — measured on DejaVu Serif, the faces fitted worse than the simplex font did. So
    /// a stroke lying within 1.5 font units of a longer one is dropped, and so is a level bar no longer than 9.5 units
    /// (0.45 of the cap height) on the cap line, the x-height or the baseline that meets another stroke — a foot.
    /// </summary>
    private static IReadOnlyList<double[]> CentreLines(IReadOnlyList<double[]> strokes)
    {
        if (strokes.Count < 2) return strokes;
        var order = Enumerable.Range(0, strokes.Count).OrderByDescending(i => Length(strokes[i])).ToList();
        var kept = new List<double[]>();
        foreach (int i in order)
        {
            var st = strokes[i];
            if (kept.Count > 0 && Along(st, 1).All(p => kept.Any(k => Near(k, p.X, p.Y, 1.5)))) continue;
            kept.Add(st);
        }
        bool Foot(double[] st)
        {
            if (st.Length != 4 || Math.Abs(st[1] - st[3]) > 0.5 || Math.Abs(st[0] - st[2]) > 9.5) return false;
            if (!new[] { -12.0, -5.0, 9.0 }.Any(y => Math.Abs(st[1] - y) <= 0.5)) return false;
            return kept.Any(k => !ReferenceEquals(k, st) && Along(st, 1).Any(p => Near(k, p.X, p.Y, 1.5)));
        }
        var result = kept.Where(st => !Foot(st)).ToList();
        return result.Count > 0 ? [.. strokes.Where(result.Contains)] : strokes;
    }

    private static double Length(double[] st)
    {
        double l = 0;
        for (int i = 2; i + 1 < st.Length; i += 2) l += Math.Sqrt(Math.Pow(st[i] - st[i - 2], 2) + Math.Pow(st[i + 1] - st[i - 1], 2));
        return l;
    }

    /// <summary>Points every <paramref name="step"/> units along a stroke, its ends included.</summary>
    private static IEnumerable<(double X, double Y)> Along(double[] st, double step)
    {
        if (st.Length < 4) { if (st.Length >= 2) yield return (st[0], st[1]); yield break; }
        for (int i = 0; i + 3 < st.Length; i += 2)
        {
            double dx = st[i + 2] - st[i], dy = st[i + 3] - st[i + 1], len = Math.Sqrt(dx * dx + dy * dy);
            int n = Math.Max(1, (int)Math.Ceiling(len / step));
            for (int k = 0; k < n; k++) yield return (st[i] + dx * k / n, st[i + 1] + dy * k / n);
        }
        yield return (st[^2], st[^1]);
    }

    private static bool Near(double[] st, double x, double y, double reach)
    {
        if (st.Length < 4) return st.Length >= 2 && Math.Pow(st[0] - x, 2) + Math.Pow(st[1] - y, 2) <= reach * reach;
        for (int i = 0; i + 3 < st.Length; i += 2)
            if (Glyph.SegmentDistance2(x, y, st[i], st[i + 1], st[i + 2], st[i + 3]) <= reach * reach) return true;
        return false;
    }

    private static readonly (string Resource, string Source, bool Upright)[] Faces =
    [
        ("CircuitRF.Design.StrokeFont.hershey-roman-complex.txt", "hershey-roman-complex", false),
        ("CircuitRF.Design.StrokeFont.hershey-italic-complex.txt", "hershey-italic-complex", false),
    ];

    private static IEnumerable<double> Xs(double[] s) { for (int i = 0; i < s.Length; i += 2) yield return s[i]; }

    // ── taught glyphs ───────────────────────────────────────────────────────────────────────────────────

    private sealed record TaughtFileModel(int Format, List<TaughtGlyph> Glyphs);
    private sealed record TaughtGlyph(string Char, List<double[]> Strokes, string? Class = null);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The glyphs taught in <paramref name="directory"/>; none when it has no file or the file cannot be
    /// read (a damaged file costs the taught glyphs, never a recognition).</summary>
    public static IReadOnlyList<GlyphTemplate> ReadTaught(string? directory)
    {
        if (directory is null) return [];
        string path = Path.Combine(directory, TaughtFile);
        if (!File.Exists(path)) return [];
        try
        {
            var model = JsonSerializer.Deserialize<TaughtFileModel>(File.ReadAllText(path), Json);
            return model?.Glyphs is not { } glyphs ? [] :
                [.. glyphs.Where(g => g.Char is { Length: 1 } && g.Strokes is { Count: > 0 })
                          .Select(g => new GlyphTemplate(g.Char[0], new Glyph(g.Strokes), TaughtSource,
                                                         g.Class == ValueClassName ? GlyphClass.Value : GlyphClass.Designator))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    // The file's spelling of the value class; a designator glyph is written with no class, as AS-10 wrote it.
    private const string ValueClassName = "value";

    /// <summary>
    /// Adds <paramref name="glyphs"/> to the taught set in <paramref name="directory"/> (R-as10-5), creating it; a
    /// glyph already taught as the same character with the same strokes is not added twice. Returns how many were
    /// added. They are designator glyphs, which the silkscreen reader consults.
    /// </summary>
    public static int Learn(string directory, IEnumerable<(char Char, Glyph Glyph)> glyphs) =>
        Learn(directory, glyphs.Select(g => (g.Char, g.Glyph, GlyphClass.Designator)));

    /// <summary>As <see cref="Learn(string, IEnumerable{ValueTuple{char, Glyph}})"/>, each glyph in its own class — a
    /// picture's value teaches value glyphs (R-im9-5), which the silkscreen reader never consults.</summary>
    public static int Learn(string directory, IEnumerable<(char Char, Glyph Glyph, GlyphClass Class)> glyphs)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var known = ReadTaught(directory).ToList();
        var list = known.Select(t => new TaughtGlyph(t.Char.ToString(), [.. t.Glyph.Strokes], ClassName(t.Class))).ToList();
        int added = 0;
        foreach (var (ch, glyph, cls) in glyphs)
        {
            if (known.Any(t => t.Char == ch && t.Class == cls && Glyph.Distance(t.Glyph, glyph) < 1e-9)) continue;
            list.Add(new TaughtGlyph(ch.ToString(), [.. glyph.Strokes.Select(s => s.Select(v => Math.Round(v, 5)).ToArray())], ClassName(cls)));
            known.Add(new GlyphTemplate(ch, glyph, TaughtSource, cls));
            added++;
        }
        if (added == 0) return 0;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, TaughtFile);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new TaughtFileModel(1, list), Json), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return added;
    }

    private static string? ClassName(GlyphClass c) => c == GlyphClass.Value ? ValueClassName : null;
}
