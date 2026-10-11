// Which symbol each word names, and which wire end — brief-img-9-text-and-values.md R-im9-4, R-im9-6, R-im9-7.
//
// THE ASSIGNMENT IS AS-10's (RefdesAssociation): the one-to-one matching of words to symbols that minimises the total
// distance from each word's centre to its symbol's box, per cluster of words and symbols that can reach one another,
// within three of the symbol's diagonals. It runs once per class, so a designator and a value can name one symbol:
//   designators → every symbol but a ground;
//   values      → resistors, inductors, capacitors and the unread two-pin kind; a value on the line under a part's
//                 designator to that part (a label block, IM-12); then a value stating its unit first among
//                 the symbols of its kind (IM-12), then every value left among the symbols left; the value's dimension
//                 is checked against the symbol's — a value in henries assigned to a capacitor is a MISMATCH, reported
//                 and left unattached, never applied;
//   line sets   → transmission lines and stubs; and a Z=… on a line under a port's designator, to that port.
// A designator that contradicts its symbol's kind (L3 on a capacitor's drawing) is kept, and the kind's confidence
// drops one step: the parts table shows both and the person decides. Net and port names need no assignment — IM-7
// already put each name word at the wire end it labels (NetLabel, SupplyMark).
//
// D10: a word that could not be read is not a wrong word. A symbol with no designator is given the next free one of
// its kind's prefix, and the report says so; a symbol with no value gets its variable in IM-10.

using System.Globalization;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Layout.Recognition.Silkscreen;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>A symbol a word may name.</summary>
/// <param name="Symbol">Its index among the reading's symbols.</param>
public sealed record LabelTarget(int Symbol, PixelBox Box, ImageSymbolKind Kind, PartConfidence Confidence);

/// <summary>What the words say about one symbol.</summary>
/// <param name="Designator">The designator read — or generated, when none was (R-im9-6); null for a ground.</param>
/// <param name="Generated">The designator was generated, not read.</param>
/// <param name="Contradicts">The designator's prefix names another kind than the symbol's drawing.</param>
/// <param name="Confidence">The kind's confidence, one step lower when the designator contradicts it.</param>
public sealed record SymbolLabels(int Symbol, ImageSymbolKind Kind, string? Designator, bool Generated,
                                  ImageWord? DesignatorWord, ImageWord? ValueWord, ImageWord? LineWord,
                                  bool Contradicts, PartConfidence Confidence)
{
    public WordValue? Value => ValueWord?.Reading?.Value;
    public IReadOnlyList<LineParameter>? LineParameters => LineWord?.Reading?.Parameters;

    /// <summary>The annotations nearest this part (R-imf7-5): a phrase, a parenthetical, a value list — for its notes.</summary>
    public IReadOnlyList<ImageWord> Comments { get; init; } = [];
}

/// <summary>A net or port name at a wire end (R-im9-4).</summary>
/// <param name="Node">The <see cref="WireGraph"/> node it names; −1 for a lead ending in paper (<see cref="Lead"/>).</param>
/// <param name="Port">The name is a port word, or labels a supply mark's net.</param>
public sealed record NodeName(int Node, string Name, ImageWord Word, bool Port)
{
    /// <summary>It names the terminal standing on <see cref="Node"/> (R-imf6-1).</summary>
    public bool AtTerminal { get; init; }

    /// <summary>It stands beyond an open end, up to 6 h from it (R-imf6-4): the end is a port.</summary>
    public bool Far { get; init; }

    /// <summary>The symbol and the pin index of the lead ending in paper it names, or null.</summary>
    public (int Symbol, int Pin)? Lead { get; init; }

    /// <summary>The name's further words on its line (<c>RF</c> <c>In</c>).</summary>
    public IReadOnlyList<ImageWord> More { get; init; } = [];
}

/// <summary>A value assigned to a symbol of another kind, left unattached.</summary>
public sealed record KindMismatch(ImageWord Word, int Symbol, ImageSymbolKind Kind);

/// <summary>Everything the words say, attached.</summary>
public sealed record ImageTextLabels(
    IReadOnlyList<SymbolLabels> Symbols, IReadOnlyList<NodeName> Names, IReadOnlyList<KindMismatch> Mismatches,
    IReadOnlyList<ImageWord> Unattached, ImageTextReport Report);

public static class LabelAssociation
{
    // Pixel coordinates are carried to the shared assignment in sixteenths, so a box's edge is not rounded to a pixel.
    private const long Sub = 16;

    /// <summary>Attaches <paramref name="words"/> to the named symbols of <paramref name="symbols"/> and to the wire
    /// ends of <paramref name="read"/>. Pure.</summary>
    public static ImageTextLabels Associate(SchematicImageRead read, SchematicSymbolsRead symbols, IReadOnlyList<ImageWord> words)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(symbols);
        var targets = symbols.Symbols.Select((s, i) => new LabelTarget(i, s.Region.Box, s.Kind, s.Confidence)).ToList();
        // The words at wire ends first (IM-7's), then the names of terminals and of open ends at a distance (IMF-6).
        var taken = words.Where(w => read.NetLabels.Any(l => l.Text == w.Region) || read.SupplyMarks.Any(m => m.Text == w.Region))
                         .Select(w => w.Id).ToHashSet();
        var far = PictureTerminals.Names(read, symbols, words, taken);
        return Associate(targets, words, read.NetLabels, read.SupplyMarks, far, PictureTerminals.TextHeight(read));
    }

    /// <summary>
    /// The symbols with a plain box's kind taken from the designator read beside it: a box read as a transmission line
    /// whose designator says R (R₂, R3) is a resistor drawn the IEC way — the two drawings are the same box, and the
    /// designator is the only thing that tells them apart (brief-imf-7, typeset). Null when no symbol changes.
    /// </summary>
    public static SchematicSymbolsRead? KindsByDesignator(SchematicSymbolsRead symbols, ImageTextLabels labels)
    {
        var list = symbols.Symbols.ToList();
        bool changed = false;
        foreach (var l in labels.Symbols)
        {
            var s = list[l.Symbol];
            if (s.Kind != ImageSymbolKind.TransmissionLine || s.Pins.Count != 2 || l.Generated || l.Designator is not { } d) continue;
            if (KindOfDesignator(d) != ImageSymbolKind.Resistor) continue;
            list[l.Symbol] = s with { Kind = ImageSymbolKind.Resistor };
            changed = true;
        }
        return changed ? symbols with { Symbols = list } : null;
    }

    /// <summary>A word reaches a part within this many text heights of its box (R-imf7-12): a label three caps from a
    /// heavy pen's symbol is as near as one three caps from a hairline's. Ten: circuitRF's own drawing sets a part's
    /// label block up to six text heights under it, and the assignment, not the reach, decides between neighbours.</summary>
    public const double ReachInTextHeights = 10;

    /// <summary>A word designator (R-imf7-3, R-imf7-10) names a part only this near, in text heights: it is a part's
    /// only beside it.</summary>
    public const double WordDesignatorReach = 3;

    /// <summary>Attaches <paramref name="words"/> to <paramref name="targets"/> and, through IM-7's net labels and
    /// supply marks, to wire ends; <paramref name="named"/> are names already placed — a terminal's, an open end's at a
    /// distance (IMF-6) — whose words nothing else takes.</summary>
    /// <param name="h">The picture's text height, pixels: every reach is in it (R-imf7-12). 0 keeps AS-10's reach of
    /// three of a symbol's diagonals.</param>
    public static ImageTextLabels Associate(IReadOnlyList<LabelTarget> targets, IReadOnlyList<ImageWord> words,
                                            IReadOnlyList<NetLabel>? labels = null, IReadOnlyList<SupplyMark>? supplies = null,
                                            IReadOnlyList<NodeName>? named = null, double h = 0)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(words);
        var used = new HashSet<int>();

        // Names first: a word IM-7 put at a wire end names that net, whatever else it could be (P1 is a port there). A
        // port's name in the vocabulary is taken as the vocabulary spells it (R-imf6-1): V1N beside a wire is VIN.
        var names = new List<NodeName>();
        void Name(int node, int region, bool supply)
        {
            foreach (var w in words.Where(w => w.Region == region))
            {
                string? snapped = PortWords.Snap([w]);
                if (snapped is null && (w.Reading is not { Class: WordClass.Name or WordClass.Designator } || w.Text.Length < 2)) continue;
                string text = snapped ?? w.Reading!.Text;
                names.Add(new NodeName(node, text, w, supply || w.Reading?.IsPortWord == true
                                                      || snapped is not null && !PortWords.IsSupply(snapped)));
                used.Add(w.Id);
            }
        }
        foreach (var l in labels ?? []) Name(l.Node, l.Text, false);
        foreach (var s in supplies ?? []) if (s.Text is { } t) Name(s.Node, t, true);
        foreach (var n in named ?? [])
        {
            names.Add(n);
            used.Add(n.Word.Id);
            foreach (var m in n.More) used.Add(m.Id);
        }

        var designator = new ImageWord?[targets.Count];
        var value = new ImageWord?[targets.Count];
        var line = new ImageWord?[targets.Count];
        var mismatches = new List<KindMismatch>();

        Assign(WordClass.Designator, t => targets[t].Kind != ImageSymbolKind.Ground, (w, t) => designator[t] = Respelled(w, targets[t].Kind));
        // Word designators (R-imf7-3, R-imf7-10): Cblock, RFC, a lone R — each only by a part of its kind beside it.
        foreach (var kind in targets.Select(t => t.Kind).Distinct().ToList())
            Assign(WordClass.Name, t => designator[t] is null && targets[t].Kind == kind, (w, t) => designator[t] = w,
                   w => w.Reading!.Designates.Contains(kind), WordDesignatorReach);
        // A value written with its part's designator (CIN = 1µF, R-imf7-4) is that part's, wherever it stands.
        foreach (var vw in words.Where(w => w.For is not null && w.Class == WordClass.Value && !used.Contains(w.Id)).ToList())
            for (int t = 0; t < targets.Count; t++)
                if (designator[t] is { } dw && value[t] is null && string.Equals(PortWords.Normal(dw.Text), PortWords.Normal(vw.For!), StringComparison.Ordinal))
                {
                    value[t] = vw;
                    used.Add(vw.Id);
                    break;
                }
        void ApplyValue(ImageWord w, int t)
        {
            // A capacitance or an inductance of nothing is no value a part is drawn with: its glyphs spelled something
            // else (a textbook's v_out reads 0Uf). A 0 Ω resistor is a jumper, and stays.
            if (w.Reading!.Value is { NotFitted: false, Si: 0 } && targets[t].Kind is ImageSymbolKind.Capacitor or ImageSymbolKind.Inductor) return;
            if (w.Reading!.Value!.Fits(Dimension(targets[t].Kind)) || targets[t].Kind == ImageSymbolKind.Unknown) value[t] = w;
            else mismatches.Add(new KindMismatch(w, targets[t].Symbol, targets[t].Kind));   // reported as that, not as unattached
        }
        // A value on the line under a designator, left-aligned or centred with it, is that part's — a label block, as
        // circuitRF, most tools and datasheets print one (IM-12, R-imf7-4) — and so is one right after it on its line (R₃10kΩ,
        // R3 10kΩ: R-imf7-11). Its kind must fit; the block may stand nearer another part's drawing than its own.
        foreach (var vw in words.Where(w => w.Class == WordClass.Value && w.For is null && !used.Contains(w.Id)).ToList())
            for (int t = 0; t < targets.Count; t++)
            {
                if (designator[t] is not { } dw || value[t] is not null || !TakesValue(targets[t].Kind)) continue;
                if (!(Under(vw, dw) || After(vw, dw)) || !(vw.Reading!.Value!.Fits(Dimension(targets[t].Kind)) || targets[t].Kind == ImageSymbolKind.Unknown)) continue;
                value[t] = vw;
                used.Add(vw.Id);
                break;
            }
        // A part number under a diode's, a transistor's or an IC's designator, or after it on its line — D1 over 1N4002
        // (R-imf5-2), Q₁2N2222 (R-imf7-11): its value word, which the parts table takes as the part's number. A source's
        // the same way (V₁12V): the emit reads its voltage from it.
        foreach (var pw in words.Where(w => w.Class is not (WordClass.Designator or WordClass.Comment) && !used.Contains(w.Id)).ToList())
            for (int t = 0; t < targets.Count; t++)
            {
                if (designator[t] is not { } dw || value[t] is not null
                    || targets[t].Kind is not (ImageSymbolKind.Diode or ImageSymbolKind.Transistor or ImageSymbolKind.Ic or ImageSymbolKind.Source)
                    || !(Under(pw, dw) || After(pw, dw))) continue;
                value[t] = pw;
                used.Add(pw.Id);
                break;
            }
        // A value that states its unit goes first to the symbols it can be the value of: a tool that prints a part's
        // label block beside the NEXT part's drawing (circuitRF's own, IM-12) puts C=1pF nearer a resistor than its
        // capacitor. Then the rest, by distance alone — so a value with no part of its kind near is still a mismatch.
        foreach (var dim in new[] { UnitDimension.Resistance, UnitDimension.Inductance, UnitDimension.Capacitance })
            Assign(WordClass.Value,
                   t => value[t] is null && (Dimension(targets[t].Kind) == dim || targets[t].Kind == ImageSymbolKind.Unknown),
                   ApplyValue, w => w.Reading!.Value!.Dimensions is [var d] && d == dim);
        Assign(WordClass.Value, t => value[t] is null && TakesValue(targets[t].Kind), ApplyValue);
        Assign(WordClass.LineParameters, t => IsLine(targets[t].Kind), (w, t) => line[t] = w);
        // A port's impedance, Z=…, on the line under its designator (a label block, IM-12).
        foreach (var lw in words.Where(w => w.Class == WordClass.LineParameters && !used.Contains(w.Id)
                                            && w.Reading!.Parameters!.All(p => p.Key is "Z" or "Z0")).ToList())
            for (int t = 0; t < targets.Count; t++)
            {
                if (targets[t].Kind != ImageSymbolKind.Port || line[t] is not null || designator[t] is not { } dw) continue;
                if (!Under(lw, dw)) continue;
                line[t] = lw;
                used.Add(lw.Id);
                break;
            }

        // Annotations (R-imf7-5): each to the nearest part within reach, for its notes.
        var comments = new List<ImageWord>[targets.Count];
        for (int t = 0; t < targets.Count; t++) comments[t] = [];
        // A value written for a designator no part has (Vref = 1.25 V) is an annotation too.
        var annotations = words.Where(w => w.Class == WordClass.Comment && !used.Contains(w.Id))
            .Concat(words.Where(w => w.For is not null && !used.Contains(w.Id))
                         .Select(w => w with { Reading = new WordReading($"{w.For} = {w.Text}", WordClass.Comment) }))
            .ToList();
        foreach (var cw in annotations)
        {
            int nearest = -1;
            double best = h > 0 ? ReachInTextHeights * h : double.MaxValue;
            for (int t = 0; t < targets.Count; t++)
            {
                if (targets[t].Kind == ImageSymbolKind.Ground) continue;
                double d = targets[t].Box.Distance(cw.Box);
                if (d < best) (nearest, best) = (t, d);
            }
            if (nearest < 0) continue;
            comments[nearest].Add(cw);
            used.Add(cw.Id);
        }

        // Designators for the symbols without one (D10).
        var taken = designator.Where(d => d is not null).Select(d => d!.Reading!.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<SymbolLabels>(targets.Count);
        for (int t = 0; t < targets.Count; t++)
        {
            var target = targets[t];
            string? name = designator[t]?.Reading!.Text;
            bool generated = false;
            if (name is null && Prefix(target.Kind) is { } prefix)
            {
                int n = 1;
                while (taken.Contains(prefix + n.ToString(CultureInfo.InvariantCulture))) n++;
                name = prefix + n.ToString(CultureInfo.InvariantCulture);
                taken.Add(name);
                generated = true;
            }
            bool contradicts = !generated && name is not null && KindOfDesignator(name) is { } k && Contradicts(k, target.Kind);
            var confidence = contradicts && target.Confidence > PartConfidence.Low ? target.Confidence - 1 : target.Confidence;
            result.Add(new SymbolLabels(target.Symbol, target.Kind, name, generated, designator[t], value[t], line[t],
                                        contradicts, confidence) { Comments = comments[t] });
        }

        var unattached = words.Where(w => w.Class is not (WordClass.Unread or WordClass.Comment) && !used.Contains(w.Id)).ToList();
        return new ImageTextLabels(result, names, mismatches, unattached, ImageTextReport.Of(words, result, names, mismatches, unattached));

        // One class's words to the targets that take them, by AS-10's assignment; within the reach in text heights when
        // the picture's h is known (R-imf7-12), else within AS-10's three diagonals.
        void Assign(WordClass cls, Func<int, bool> takes, Action<ImageWord, int> apply, Func<ImageWord, bool>? only = null,
                    double reach = ReachInTextHeights)
        {
            var ws = words.Where(w => w.Class == cls && w.For is null && !used.Contains(w.Id) && (only is null || only(w))).ToList();
            var ts = Enumerable.Range(0, targets.Count).Where(takes).ToList();
            if (ws.Count == 0 || ts.Count == 0) return;
            long reachSub = h > 0 ? (long)Math.Round(reach * h * Sub) : 0;
            var claims = ws.Select(w => new PartClaim(PartEvidenceSource.Silkscreen,
                (long)Math.Round(w.Box.CentreX * Sub), (long)Math.Round(w.Box.CentreY * Sub), w.Text, reachSub)).ToList();
            var candidates = ts.Select(t => new RefdesCandidate(Box(targets[t].Box))).ToList();
            var assigned = RefdesAssociation.Assign(claims, candidates, bodyReach: h <= 0);
            for (int i = 0; i < ws.Count; i++)
            {
                if (assigned[i] < 0) continue;
                used.Add(ws[i].Id);
                apply(ws[i], ts[assigned[i]]);
            }
        }
    }

    /// <summary>Word <paramref name="w"/> stands on one of the next two lines under <paramref name="d"/>, left-aligned
    /// with it — or on the next line, 0.3–1.2 of its height under it, centred on it (a datasheet's block, R-imf7-4).</summary>
    private static bool Under(ImageWord w, ImageWord d)
    {
        double h = d.Box.Height;
        if (w.Box.Top <= d.Box.Bottom) return false;
        double gap = w.Box.Top - d.Box.Bottom;
        return Math.Abs(w.Box.Left - d.Box.Left) <= 0.5 * h && gap <= 3.5 * h
               || Math.Abs(w.Box.CentreX - d.Box.CentreX) <= 0.5 * h && gap <= 1.2 * h;
    }

    /// <summary>Word <paramref name="w"/> follows <paramref name="d"/> on its line — the run R₃10kΩ cut at its subscript,
    /// or R3 10kΩ a space apart (R-imf7-11): its left within a text height of the designator's right, the two lines
    /// overlapping by half the shorter.</summary>
    private static bool After(ImageWord w, ImageWord d)
    {
        double h = d.Box.Height;
        double gap = w.Box.Left - d.Box.Right;
        double overlap = Math.Min(w.Box.Bottom, d.Box.Bottom) - Math.Max(w.Box.Top, d.Box.Top);
        return gap >= -1 && gap <= h && overlap >= 0.5 * Math.Min(w.Box.Height, d.Box.Height);
    }

    /// <summary>
    /// A designator assigned to a part whose kind its prefix contradicts, or names no kind, read once more as the part's
    /// prefix where its first glyph was read as a character the matcher confuses with it (R-imf7-2): a bold D1 beside a
    /// diode read as Q1 or O1, a serif italic R₂ beside a resistor read as B2. The word as read when no such reading is
    /// near.
    /// </summary>
    private static ImageWord Respelled(ImageWord w, ImageSymbolKind kind)
    {
        // A plain box read as a line is drawn the IEC resistor's way too, so its designator may be an R's.
        string? prefix = kind == ImageSymbolKind.TransmissionLine ? "R" : Prefix(kind);
        if (prefix is not { Length: 1 } || w.Text.Length == 0 || w.Text[0] == prefix[0]) return w;
        if (KindOfDesignator(w.Text) is { } named && !Contradicts(named, kind)) return w;
        if (w.Glyphs.Count != w.Text.Length || w.Text.TakeWhile(char.IsAsciiLetter).Count() != 1) return w;
        var g = w.Glyphs[0];
        if (!KnownWords.Confused(g.Best.Char, prefix[0])
            || !g.Ranked.Any(m => m.Char == prefix[0] && m.Distance <= StrokeTextReader.MaxGlyphDistance)) return w;
        string text = prefix + w.Text[1..];
        return ValueGrammar.Parse(text) is { Class: WordClass.Designator } r ? w with { Reading = r } : w;
    }

    private static Bbox Box(PixelBox b) => new(b.Left * Sub, b.Top * Sub, (b.Right + 1) * Sub, (b.Bottom + 1) * Sub);

    private static bool TakesValue(ImageSymbolKind k) =>
        k is ImageSymbolKind.Resistor or ImageSymbolKind.Inductor or ImageSymbolKind.Capacitor or ImageSymbolKind.Unknown;

    private static bool IsLine(ImageSymbolKind k) =>
        k is ImageSymbolKind.TransmissionLine or ImageSymbolKind.ShortStub or ImageSymbolKind.OpenStub;

    private static UnitDimension Dimension(ImageSymbolKind k) => k switch
    {
        ImageSymbolKind.Resistor => UnitDimension.Resistance,
        ImageSymbolKind.Inductor => UnitDimension.Inductance,
        ImageSymbolKind.Capacitor => UnitDimension.Capacitance,
        _ => UnitDimension.None,
    };

    /// <summary>The prefix a generated designator takes; null for a ground. The unread two-pin kind is generated as a
    /// capacitor, as the artwork series generates it.</summary>
    public static string? Prefix(ImageSymbolKind k) => k switch
    {
        ImageSymbolKind.Resistor => "R",
        ImageSymbolKind.Inductor => "L",
        ImageSymbolKind.Capacitor or ImageSymbolKind.Unknown => "C",
        ImageSymbolKind.TransmissionLine or ImageSymbolKind.ShortStub or ImageSymbolKind.OpenStub => "TL",
        ImageSymbolKind.Diode => "D",
        ImageSymbolKind.Transistor => "Q",
        ImageSymbolKind.Amplifier or ImageSymbolKind.Ic => "U",
        ImageSymbolKind.Port => "P",
        ImageSymbolKind.Source => "V",
        ImageSymbolKind.Transformer => "T",
        ImageSymbolKind.Bead => "FB",
        _ => null,
    };

    /// <summary>The kind a designator's prefix names, where it names one plainly.</summary>
    private static ImageSymbolKind? KindOfDesignator(string designator) =>
        new string([.. designator.TakeWhile(char.IsAsciiLetter)]).ToUpperInvariant() switch
        {
            "R" => ImageSymbolKind.Resistor,
            "L" or "FB" => ImageSymbolKind.Inductor,
            "C" => ImageSymbolKind.Capacitor,
            "D" => ImageSymbolKind.Diode,
            "Q" => ImageSymbolKind.Transistor,
            _ => null,
        };

    /// <summary>A designator's kind contradicts a drawing's when both are among the kinds a prefix names plainly and
    /// they differ. An unread drawing contradicts nothing.</summary>
    private static bool Contradicts(ImageSymbolKind byName, ImageSymbolKind drawn) =>
        drawn is ImageSymbolKind.Resistor or ImageSymbolKind.Inductor or ImageSymbolKind.Capacitor
            or ImageSymbolKind.Diode or ImageSymbolKind.Transistor
        && byName != drawn;
}

/// <summary>What reading a schematic picture's words found (R-im9-7).</summary>
public sealed record ImageTextReport
{
    public int Words { get; init; }
    public int Designators { get; init; }
    public int Values { get; init; }
    public int LineSets { get; init; }
    public int Names { get; init; }

    /// <summary>Words with no grammatical reading, each with its box.</summary>
    public IReadOnlyList<PixelBox> Unread { get; init; } = [];

    /// <summary>Of <see cref="Unread"/>, those not read because their type is too heavy for its size.</summary>
    public int TooHeavy { get; init; }

    /// <summary>Values assigned to a symbol of another kind, left unattached.</summary>
    public IReadOnlyList<KindMismatch> KindMismatches { get; init; } = [];

    /// <summary>Designators whose prefix names another kind than the symbol's drawing.</summary>
    public IReadOnlyList<string> Contradictions { get; init; } = [];

    /// <summary>Words read that name nothing: no symbol in reach, no wire end — a title, a note.</summary>
    public int Unattached { get; init; }

    /// <summary>Designators generated for symbols none was read for.</summary>
    public IReadOnlyList<string> Generated { get; init; } = [];

    /// <summary>Annotations read — phrases, parentheticals, value lists (R-imf7-5) — and of them, those attached to a
    /// part as a note.</summary>
    public int Comments { get; init; }
    public int CommentsAttached { get; init; }

    public static ImageTextReport Of(IReadOnlyList<ImageWord> words, IReadOnlyList<SymbolLabels> symbols,
                                     IReadOnlyList<NodeName> names, IReadOnlyList<KindMismatch> mismatches,
                                     IReadOnlyList<ImageWord> unattached) => new()
    {
        Words = words.Count,
        Designators = words.Count(w => w.Class == WordClass.Designator),
        Values = words.Count(w => w.Class == WordClass.Value),
        LineSets = words.Count(w => w.Class == WordClass.LineParameters),
        Names = words.Count(w => w.Class == WordClass.Name),
        Unread = [.. words.Where(w => w.Class == WordClass.Unread).Select(w => w.Box)],
        TooHeavy = words.Count(w => w.TooHeavy),
        KindMismatches = mismatches,
        Contradictions = [.. symbols.Where(s => s.Contradicts).Select(s => s.Designator!)],
        Unattached = unattached.Count,
        Generated = [.. symbols.Where(s => s.Generated).Select(s => s.Designator!)],
        Comments = words.Count(w => w.Class == WordClass.Comment),
        CommentsAttached = symbols.Sum(s => s.Comments.Count),
    };

    /// <summary>The report in words, one finding per line; a count of nothing is not a line.</summary>
    public IReadOnlyList<string> Lines()
    {
        var r = new List<string>
        {
            $"{Count(Words, "word")} read: {Designators} designator{S(Designators)}, {Values} value{S(Values)}, " +
            $"{LineSets} line parameter set{S(LineSets)}, {Names} name{S(Names)}.",
        };
        if (Unread.Count > 0) r.Add($"{Count(Unread.Count, "word")} could not be read; {(Unread.Count == 1 ? "its box is" : "their boxes are")} kept.");
        if (TooHeavy > 0)
            r.Add($"{Count(TooHeavy, "word")} {(TooHeavy == 1 ? "is" : "are")} in type too heavy for its size to read reliably " +
                  "(bold, or small); they were not read.");
        foreach (var m in KindMismatches)
            r.Add($"'{m.Word.Text}' is not a {Noun(m.Kind)}'s value; it was left unattached.");
        foreach (var c in Contradictions)
            r.Add($"{c} names a different kind than its symbol is drawn as; both are shown.");
        if (Comments > 0)
            r.Add($"{Count(Comments, "annotation")} read; {CommentsAttached} attached to the nearest part as a note, never a net name.");
        if (Unattached > 0) r.Add($"{Count(Unattached, "word")} named no part and no wire.");
        if (Generated.Count > 0)
            r.Add($"No designator was read for {Count(Generated.Count, "part")}: named {string.Join(", ", Generated.Take(12))}" +
                  (Generated.Count > 12 ? ", …" : "") + ".");
        return r;
    }

    private static string Noun(ImageSymbolKind k) => k.ToString().ToLowerInvariant();
    private static string S(int n) => n == 1 ? "" : "s";
    private static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + S(n);
}
