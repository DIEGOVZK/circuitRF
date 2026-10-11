// The words datasheets write, and the glyphs the matcher confuses — brief-imf-7-typography.md R-imf7-2.
//
// A word in the list (resources/schematic-words/known-words.txt, one per line) ranks above any other name
// (WordReading.Rank). And a known word wins over the reading the glyphs gave when it differs from it only where a glyph
// was read as a character the matcher confuses with the word's — I and 1, O and 0, S and 5, B and 8, G, Q, O and D, U
// and V, O and a typeset Ω — each of those glyphs still near the word's character, and the rest within the port
// vocabulary's margin of their best (PortWords): a datasheet's V1N is VIN, its QNO is GND, a bold ΩUT is OUT.
//
// Matching ignores case, spaces, hyphens and underscores; a word snapped keeps each glyph's own case where the glyph
// offered it (Cadj stays Cadj — the parts table keeps the word as read).

using System.Reflection;
using CircuitRF.Design.Layout.Recognition.Silkscreen;

namespace CircuitRF.Design.Schematic.Recognition;

public static class KnownWords
{
    private const string Resource = "CircuitRF.Design.SchematicWords.known-words.txt";

    private static readonly Lazy<HashSet<string>> List = new(Load);

    /// <summary>Every word the list holds, normalised (<see cref="PortWords.Normal"/>).</summary>
    public static IReadOnlySet<string> All => List.Value;

    /// <summary>Whether <paramref name="text"/> is a word of the list.</summary>
    public static bool IsKnown(string text) => text.Length >= 2 && List.Value.Contains(PortWords.Normal(text));

    private static HashSet<string> Load()
    {
        using var stream = typeof(KnownWords).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"the embedded word list '{Resource}' is missing from {typeof(KnownWords).Assembly.GetName().Name}");
        using var reader = new StreamReader(stream);
        var words = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            string t = line.Trim();
            if (t.Length == 0 || t[0] == '#') continue;
            words.Add(PortWords.Normal(t));
        }
        return words;
    }

    /// <summary>The characters the matcher confuses, a class each (upper case; a glyph's character is compared
    /// upper-cased). The brief's five, and three the datasheet pictures showed: D among the round ones (GND read
    /// QNO), U and V (VOUT read UΩUT), a bold slash and an I (a rating's 1A/100V), the typeset Ω, whose open foot an O's skeleton nearly has, and an italic serif R, whose
    /// leg curls into a B's lower bowl.</summary>
    private static readonly string[] Confusions = ["I1Ll/", "O0DQGΩo", "S5s", "B8R", "UVuv", "Z2", "aeso"];

    /// <summary>Whether a glyph read as <paramref name="seen"/> may be <paramref name="want"/>'s character by confusion. The
    /// lower-case bowls are a class of their own (a small bold a reads e or s): a class holds a letter in the case it is
    /// written in, and the list's upper-case word matches either.</summary>
    internal static bool Confused(char seen, char want)
    {
        if (char.ToUpperInvariant(seen) == char.ToUpperInvariant(want) || StrokeGlyphs.Alike(seen, want)) return true;
        static bool Has(string c, char x) => c.Contains(x) || c.Contains(char.ToLowerInvariant(x));
        return Confusions.Any(c => c.Contains(seen) && Has(c, want));
    }

    /// <summary>How far past a glyph's best match the word's character may be, plainly: PortWords' margin.</summary>
    private const double Margin = 0.4, Reach = 0.04;

    /// <summary>
    /// The known word <paramref name="glyphs"/> spell, or null: as many glyphs as the word has characters (spaces,
    /// hyphens and underscores aside), each offering the word's character — as its best, within the margin of its best,
    /// or, for at most half of them rounded up (QNO's two of three), as a character confused with its best (<see cref="Confused"/>) no farther than
    /// <see cref="StrokeTextReader.MaxGlyphDistance"/>. Over a word that has a reading (<paramref name="read"/>), only
    /// confusion stands in for a glyph's best: a clean RF is no NF. The nearest such word wins. A space is written where
    /// <paramref name="spaceBefore"/> puts one.
    /// </summary>
    public static (string Text, double Distance)? Snap(IReadOnlyList<GlyphReading> glyphs, IReadOnlySet<int>? spaceBefore = null,
                                                      bool read = false)
    {
        if (glyphs.Count < 2) return null;
        (string Text, double Score)? best = null;
        foreach (string word in List.Value)
        {
            if (word.Length != glyphs.Count) continue;
            var sb = new System.Text.StringBuilder();
            double score = 0;
            int confused = 0;
            bool ok = true;
            for (int i = 0; i < word.Length && ok; i++)
            {
                var g = glyphs[i];
                double reach = Math.Max(g.Best.Distance / (1 - Margin), g.Best.Distance + Reach);
                GlyphMatch? plain = null, near = null;
                foreach (var m in g.Ranked)
                {
                    if (m.Distance > StrokeTextReader.MaxGlyphDistance) break;
                    if (char.ToUpperInvariant(m.Char) != word[i]) continue;
                    if (m.Distance <= reach) { plain = m; break; }
                    near ??= m;
                }
                if (plain is { } q && read && !Confused(g.Best.Char, q.Char)) plain = null;
                if (plain is null && near is not null && Confused(g.Best.Char, word[i])) { plain = near; confused++; }
                if (plain is not { } p) { ok = false; break; }
                if (spaceBefore?.Contains(i) == true) sb.Append(' ');
                sb.Append(char.IsLetter(p.Char) ? p.Char : word[i]);
                score += p.Distance;
            }
            if (!ok || confused > (word.Length + 1) / 2) continue;
            if (best is null || score < best.Value.Score) best = (sb.ToString(), score);
        }
        return best is { } b ? (b.Text, b.Score / glyphs.Count) : null;
    }
}
