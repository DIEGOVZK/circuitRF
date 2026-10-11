// Terminals and the names that name them — brief-imf-6-ports-terminals-names.md R-imf6-1, R-imf6-4, R-imf6-5.
//
// A datasheet draws a port as a NAMED TERMINAL: an open circle (`terminal-circle`) or a filled dot at the free end of a
// wire, with `VIN`, `RF In` beside it. A filled dot is a one-pin region read as a disc (SchematicSymbols.FilledDot), or
// — drawn small, as a 1× screen crop draws it — the junction blob the wire reading found on a free end
// (WireNode.Terminal) with no region reaching it.
//
// THE PORT VOCABULARY IS A GRAMMAR. A word beside a terminal, or in line with an open end, is read once more against the
// names a port is called by (IN, OUT, RF In, VIN, RF+DC …), each glyph allowed any character within a wider margin of
// its best than ImageText's own decode allows: the type is the same type, but the place says what the word is. A sans
// "RF" whose R matched K a little better, an "Out" whose O matched Ω — read beside a terminal, they are the port's name.
// The words of one line, a word space apart, are one name ("RF" "In" → "RF In").

using CircuitRF.Design.Layout.Recognition.Silkscreen;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>A terminal of the picture (R-imf6-1).</summary>
/// <param name="Symbol">Its symbol's index among the reading's symbols; −1 for a dot the wire reading found on an end.</param>
/// <param name="Node">The wire end it stands on.</param>
/// <param name="Box">Where it is drawn, read pixels.</param>
/// <param name="Filled">A filled dot, not an open circle.</param>
public sealed record PictureTerminal(int Symbol, int Node, PixelBox Box, bool Filled)
{
    public double X => Box.CentreX;
    public double Y => Box.CentreY;
}

public static class PictureTerminals
{
    /// <summary>The terminals of a reading, left to right, top to bottom: every one-pin port symbol drawn as an open
    /// circle or a filled dot, and every filled dot on a free wire end that no symbol reaches.</summary>
    public static IReadOnlyList<PictureTerminal> Find(SchematicImageRead read, SchematicSymbolsRead symbols)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(symbols);
        var result = new List<PictureTerminal>();
        var syms = symbols.Symbols;
        for (int i = 0; i < syms.Count; i++)
        {
            var s = syms[i];
            if (IsTerminal(s) && s.Pins[0].Node >= 0)
                result.Add(new PictureTerminal(i, s.Pins[0].Node, s.Region.Box, s.Variant == SchematicSymbols.DotVariant));
        }
        var reached = syms.SelectMany(s => s.Pins).Select(p => p.Node).ToHashSet();
        int r = (int)Math.Ceiling(1.5 * read.StrokeWidth);
        foreach (var n in read.Wires.Ends.Where(n => n.Terminal && !reached.Contains(n.Id)))
        {
            int x = (int)Math.Round(n.X), y = (int)Math.Round(n.Y);
            result.Add(new PictureTerminal(-1, n.Id, new PixelBox(x - r, y - r, x + r, y + r), true));
        }
        return [.. result.OrderBy(t => t.X).ThenBy(t => t.Y)];
    }

    /// <summary>A one-pin port symbol drawn as a terminal — an open circle or a filled dot — rather than a Term box or a
    /// pin flag.</summary>
    public static bool IsTerminal(RecognisedSymbol s) =>
        s.Disposition == SymbolDisposition.Recognised && s.Kind == ImageSymbolKind.Port && s.Pins.Count == 1
        && (s.Template?.Name == "terminal-circle" || s.Variant == SchematicSymbols.DotVariant);

    /// <summary>The picture's text height h in read pixels; six pens when the profile has none.</summary>
    internal static double TextHeight(SchematicImageRead read) =>
        read.Profile is { } p && p.Text.Px > 0 ? p.Text.Px * read.Scale : 6 * read.StrokeWidth;

    // ── the names ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The names of terminals (R-imf6-1) and of open ends at a distance (R-imf6-4), each word claimed once. A terminal
    /// takes the nearest name within 3 h of it, any side, that stands no nearer another part. An open wire end, or a lead ending in paper, takes a name in
    /// line with it (its line within 1 h of the word) beyond its end within 6 h, when no symbol and no other wire is
    /// nearer the word. Only a word that reads as a port's name or as a name at all is taken — a designator or a value
    /// beside a terminal is its part's.
    /// </summary>
    internal static List<NodeName> Names(SchematicImageRead read, SchematicSymbolsRead symbols, IReadOnlyList<ImageWord> words,
                                         ISet<int> taken)
    {
        var result = new List<NodeName>();
        if (words.Count == 0) return result;
        double h = TextHeight(read);
        var graph = read.Wires;
        var syms = symbols.Symbols;

        // A word and the words beside it on its line, a word space apart, in reading order.
        List<ImageWord> Line(ImageWord word)
        {
            var line = new List<ImageWord> { word };
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var o in words)
                {
                    if (line.Contains(o) || taken.Contains(o.Id)) continue;
                    if (Math.Abs(o.Box.CentreY - word.Box.CentreY) > 0.4 * h) continue;
                    if (line.Any(l => o.Box.Distance(l.Box) <= h)) { line.Add(o); grew = true; }
                }
            }
            return [.. line.OrderBy(l => l.Box.Left)];
        }
        (string Name, List<ImageWord> Line)? Read(ImageWord word)
        {
            var line = Line(word);
            if (line.Count > 1 && PortWords.Snap(line) is { } whole) return (whole, line);
            if (PortWords.Snap([word]) is { } one) return (one, [word]);
            // A part's word designator (Cblock, a lone R) is no port's name (R-imf7-3, R-imf7-10).
            return word.Reading is { Class: WordClass.Name, Designates.Count: 0 } r && r.Text.Length >= 2 ? (r.Text, [word]) : null;
        }

        var claims = new List<(double Gap, NodeName Name)>();

        foreach (var t in Find(read, symbols))
            foreach (var word in words)
            {
                if (taken.Contains(word.Id)) continue;
                double gap = t.Box.Distance(word.Box);
                if (gap > 3 * h) continue;
                // A word nearer another part than the terminal is that part's: V_CC beside its supply circle.
                if (syms.Where((s, k) => k != t.Symbol && !IsTerminal(s)).Any(s => s.Region.Box.Distance(word.Box) < gap)) continue;
                if (Read(word) is not { } name) continue;
                claims.Add((gap, new NodeName(t.Node, name.Name, word, !PortWords.IsSupply(name.Name))
                {
                    AtTerminal = true, More = [.. name.Line.Where(l => l != word)],
                }));
            }

        // The open ends: wire ends nothing reaches, and leads ending in paper.
        var ends = new List<(int Node, (int Symbol, int Pin)? Lead, double X, double Y, double Ux, double Uy, int Segment)>();
        var reached = syms.SelectMany(s => s.Pins).Select(p => p.Node).Where(n => n >= 0).ToHashSet();
        foreach (var l in read.NetLabels) reached.Add(l.Node);
        foreach (var m in read.SupplyMarks) reached.Add(m.Node);
        foreach (var e in graph.Ends)
        {
            if (reached.Contains(e.Id) || e.Terminal) continue;
            var (ux, uy) = graph.Incoming(e.Id);
            ends.Add((e.Id, null, e.X, e.Y, ux, uy, graph.SegmentAt(e.Id).Id));
        }
        for (int i = 0; i < syms.Count; i++)
            foreach (var p in syms[i].Pins.Where(p => p.Node < 0))
                if (syms[i].Region.Attachments.FirstOrDefault(a => a.Node < 0 && a.X == p.X && a.Y == p.Y) is { } a)
                    ends.Add((-1, (i, p.Index), p.X, p.Y, -a.DirX, -a.DirY, -1));
        foreach (var (node, lead, x, y, ux, uy, segment) in ends)
        {
            if (ux == 0 && uy == 0) continue;
            foreach (var word in words)
            {
                if (taken.Contains(word.Id)) continue;
                var b = word.Box;
                // Beyond the end along the wire's line, within 6 h; the line within 1 h of the word across it.
                double near = Math.Abs(ux) >= Math.Abs(uy)
                    ? (ux > 0 ? b.Left - x : x - (b.Right + 1))
                    : (uy > 0 ? b.Top - y : y - (b.Bottom + 1));
                double across = Math.Abs(ux) >= Math.Abs(uy)
                    ? Math.Max(0, Math.Max(b.Top - y, y - (b.Bottom + 1)))
                    : Math.Max(0, Math.Max(b.Left - x, x - (b.Right + 1)));
                if (near < -0.5 * h || near > 6 * h || across > h) continue;
                double gap = b.Distance(x, y);
                // No symbol and no other wire nearer the word than the end.
                if (syms.Where((s, k) => lead is not { } ld || ld.Symbol != k).Any(s => s.Region.Box.Distance(b) < gap)) continue;
                if (graph.Segments.Any(s => s.Id != segment && SegmentToBox(s, b) < gap)) continue;
                if (Read(word) is not { } name) continue;
                claims.Add((gap, new NodeName(node, name.Name, word, !PortWords.IsSupply(name.Name))
                {
                    Far = true, Lead = lead, More = [.. name.Line.Where(l => l != word)],
                }));
            }
        }

        // Nearest first, each word, terminal and end once.
        var placed = new HashSet<(int, int, int)>();
        foreach (var (_, n) in claims.OrderBy(c => c.Gap))
        {
            var key = (n.Node, n.Lead?.Symbol ?? -1, n.Lead?.Pin ?? -1);
            if (placed.Contains(key) || taken.Contains(n.Word.Id) || n.More.Any(m => taken.Contains(m.Id))) continue;
            placed.Add(key);
            taken.Add(n.Word.Id);
            foreach (var m in n.More) taken.Add(m.Id);
            result.Add(n);
        }
        return result;
    }

    private static double SegmentToBox(WireSegment s, PixelBox b)
    {
        double best = double.MaxValue;
        for (int k = 0; k <= 16; k++)
        {
            double t = k / 16.0;
            best = Math.Min(best, b.Distance(s.A.x + t * (s.B.x - s.A.x), s.A.y + t * (s.B.y - s.A.y)));
        }
        return best;
    }
}

/// <summary>The names a port is called by (R-imf6-1, R-imf6-5), and the supply names that are not one.</summary>
public static class PortWords
{
    private static readonly string[] Inputs = ["IN", "INPUT", "RFIN", "RFINPUT", "VIN", "VI"];
    private static readonly string[] Outputs = ["OUT", "OUTPUT", "RFOUT", "RFOUTPUT", "VOUT", "VO"];
    private static readonly string[] Others = ["RF", "DC", "RF+DC", "LO", "IF", "ANT"];
    private static readonly string[] Supplies = ["VCC", "VDD", "VEE", "VSS", "VS", "+VS", "V+", "V-"];

    /// <summary>Every spelling the vocabulary reads, upper case, without spaces, hyphens or underscores.</summary>
    private static readonly string[] Vocabulary = [.. Inputs, .. Outputs, .. Others, .. Supplies];

    /// <summary>A name with its spaces, hyphens and underscores taken out, upper case: <c>RF-In</c> → <c>RFIN</c>.</summary>
    internal static string Normal(string name)
    {
        string t = name.Trim().ToUpperInvariant().Replace(" ", "").Replace("_", "");
        return t.Length > 2 ? t.Replace("-", "") : t;   // V- keeps its sign
    }

    /// <summary>A name that reads as a circuit's input: <c>IN</c>, <c>RF In</c>, <c>VIN</c>, <c>RF-IN</c>.</summary>
    public static bool IsInput(string name) => Inputs.Contains(Normal(name));

    /// <summary>A name that reads as a circuit's output: <c>OUT</c>, <c>RFOUT</c>, <c>VOUT</c>, <c>Vo</c>.</summary>
    public static bool IsOutput(string name) => Outputs.Contains(Normal(name));

    /// <summary>A name that names a supply rather than a port: a <c>V…</c> rail or a voltage — <c>VCC</c>, <c>+Vs</c>,
    /// <c>V+</c>, <c>+5V</c> — that is no input or output.</summary>
    public static bool IsSupply(string name)
    {
        if (IsInput(name) || IsOutput(name)) return false;
        string t = name.Trim().TrimStart('+', '-', '−');
        return t.Length > 0 && (char.ToUpperInvariant(t[0]) == 'V' || char.IsAsciiDigit(t[0]))
            && !t.Contains("GND", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How far past a glyph's best match a vocabulary character may be: a fraction of the best and a little
    /// more — wider than ImageText's own decode, because the place has already said what the word is.</summary>
    private const double Margin = 0.4, Reach = 0.04;

    /// <summary>
    /// The port name <paramref name="line"/> spells — its words in order, a space between them — or null. Each glyph
    /// must offer the vocabulary's character within the margin of its best (in its own case where both cases are
    /// offered); a word read by a reader with no glyphs is compared as text.
    /// </summary>
    public static string? Snap(IReadOnlyList<ImageWord> line)
    {
        if (line.Count == 0) return null;
        if (line.Any(w => w.Glyphs.Count == 0))
        {
            string text = string.Join(" ", line.Select(w => w.Text));
            return Vocabulary.Contains(Normal(text)) ? text : null;
        }
        var glyphs = line.SelectMany(w => w.Glyphs).ToList();
        var breaks = new HashSet<int>();
        int at = 0;
        foreach (var w in line.Take(line.Count - 1)) breaks.Add(at += w.Glyphs.Count);

        string? best = null;
        double bestScore = double.MaxValue;
        foreach (string v in Vocabulary)
        {
            if (v.Length != glyphs.Count) continue;
            var sb = new System.Text.StringBuilder();
            double score = 0;
            bool ok = true;
            for (int i = 0; i < v.Length && ok; i++)
            {
                var g = glyphs[i];
                double reach = Math.Max(g.Best.Distance / (1 - Margin), g.Best.Distance + Reach);
                var match = g.Ranked.Where(m => m.Distance <= reach && m.Distance <= StrokeTextReader.MaxGlyphDistance
                                                && char.ToUpperInvariant(m.Char) == v[i])
                                    .Cast<GlyphMatch?>().FirstOrDefault();
                if (match is not { } m) { ok = false; break; }
                if (breaks.Contains(i)) sb.Append(' ');
                sb.Append(char.IsLetter(m.Char) ? m.Char : v[i]);
                score += m.Distance;
            }
            if (ok && score < bestScore) { bestScore = score; best = sb.ToString(); }
        }
        return best;
    }

    /// <summary>The order a port takes by its name (R-imf6-5): an input 0, an output 1, any other 2.</summary>
    internal static int Rank(string? name) => name is null ? 2 : IsInput(name) ? 0 : IsOutput(name) ? 1 : 2;
}
