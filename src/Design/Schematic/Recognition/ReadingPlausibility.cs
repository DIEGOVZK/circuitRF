// How believable a reading of a schematic picture is — brief-imf-9-auto-settings-and-paste.md R-imf9-1 (overview F12).
//
// The reader can be wrong in ways it cannot see from inside one stage: a runaway region that swallows its neighbours
// reads as one twenty-pin part, a picture's shading reads as a tangle of short wires, a dimension arrow shorts two
// nets. Each leaves a mark a real schematic never has, and the score is those marks counted — five terms in [0, 1]:
//
//   ink explained — the share of the ink read as a wire, a symbol, a word, a supply mark or an annotation
//   parts named   — symbol regions that are a part; a multi-pin region is one only when it is a box or a triangle
//                   (IMF-8), so a runaway region of no known kind counts for nothing
//   pins honest   — every two-pin part has two wired pins, and no region has more leads than its part has
//   words read    — words read over word regions, and designators found over the parts that carry one
//   graph sane    — no two-pin part with both pins on one net, and every net but a named or a port's reaching two pins
//
// The score is their weighted sum. The weights were fitted once — least squares on the IMF-1 fixtures' readings and
// their candidate readings against how right each was (FieldScore), then clipped at zero and normalised — and are
// constants here, the fit's table in the design note §25; they are never tuned per picture. The candidates IMF-9 reads
// are compared by this score, and a picture whose best reading falls under the floor is no schematic.

using System.Globalization;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>The terms of one reading's plausibility (R-imf9-1), each in [0, 1], with the counts behind them.</summary>
public sealed record ReadingPlausibility
{
    // ── The fit (design note §25) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The weights of ink explained, parts named, pins honest, words read and graph sane — fitted on the IMF-1
    /// fixtures, summing to 1. Pins honest fitted to nothing: on these readings it says nothing parts named does not
    /// (a runaway region is a region of no known kind first); it is kept as a term, and reported.</summary>
    public static IReadOnlyList<double> Weights { get; } = [0.10, 0.62, 0.00, 0.09, 0.19];

    /// <summary>A reading scoring under this is no schematic's (R-imf9-3): every committed schematic fixture's reading
    /// scores above it (the least, 0.72), and the illustration's, read as a schematic anyway, below (0.19). It sits low in
    /// that gap on purpose: two downloaded web schematics read poorly score 0.41 and 0.43, and a poor reading of a real
    /// schematic is the dialog's to correct, not a refusal.</summary>
    public const double Floor = 0.30;

    /// <summary>A multi-pin region of no known kind with more leads than this is a runaway, not a part.</summary>
    public const int MaxUnnamedPins = 4;

    // ── The terms ───────────────────────────────────────────────────────────────────────────────────────────────

    public double InkExplained { get; init; }
    public double PartsNamed { get; init; }
    public double PinsHonest { get; init; }
    public double WordsRead { get; init; }
    public double GraphSane { get; init; }

    /// <summary>The terms in the order of <see cref="Weights"/>.</summary>
    public IReadOnlyList<double> Terms => [InkExplained, PartsNamed, PinsHonest, WordsRead, GraphSane];

    /// <summary>The weighted sum of the terms, in [0, 1].</summary>
    public double Score => Combine(Terms, Weights);

    // ── The counts behind them, for the sentence a refusal says ─────────────────────────────────────────────────

    public int Regions { get; init; }
    public int Named { get; init; }
    public int Dishonest { get; init; }
    public int WordRegions { get; init; }
    public int WordsReadCount { get; init; }
    public int Nets { get; init; }
    public int LonelyNets { get; init; }
    public int ShortedParts { get; init; }

    public static double Combine(IReadOnlyList<double> terms, IReadOnlyList<double> weights)
    {
        double s = 0;
        for (int i = 0; i < terms.Count; i++) s += terms[i] * weights[i];
        return Math.Clamp(s, 0, 1);
    }

    /// <summary>The terms of <paramref name="r"/>; zero throughout when it read nothing at all.</summary>
    public static ReadingPlausibility Of(SchematicImageResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Read is not { } read) return new ReadingPlausibility();
        var symbols = r.Symbols?.Symbols ?? [];
        int n = symbols.Count;

        double ink = read.InkPixels > 0 ? Math.Clamp((double)read.ExplainedInk / read.InkPixels, 0, 1) : 0;
        int named = symbols.Count(IsPart);
        int dishonest = symbols.Count(s => !Honest(s));

        // Words: the regions any reading came from, and the designators found over the parts that carry one.
        int wordRegions = read.Words.Count;
        int readRegions = r.Words.Where(w => w.Reading is not null).Select(w => w.Region).Distinct().Count();
        double wordShare = wordRegions == 0 ? 0.5 : Math.Min(1, (double)readRegions / wordRegions);
        int designated = symbols.Count(s => s.Disposition is SymbolDisposition.Recognised or SymbolDisposition.CutOut
                                            && s.Kind is not (ImageSymbolKind.Ground or ImageSymbolKind.Port));
        int found = r.Labels?.Symbols.Count(l => !l.Generated && l.Designator is not null) ?? 0;
        double designators = designated == 0 ? wordShare : Math.Min(1, (double)found / designated);

        // The graph: the nets of the wire nodes every pin reaches.
        var g = read.Wires;
        int Net(int node)
        {
            var seg = g.Segments.FirstOrDefault(s => s.From == node || s.To == node);
            return seg is null ? -1 : g.Net(seg.Id);
        }
        var pinsOn = new Dictionary<int, int>();
        var spoken = new HashSet<int>();
        int twoPin = 0, shorted = 0;
        foreach (var s in symbols)
        {
            var nets = s.Pins.Where(p => p.Node >= 0).Select(p => Net(p.Node)).Where(k => k >= 0).ToList();
            foreach (int k in nets) pinsOn[k] = pinsOn.GetValueOrDefault(k) + 1;
            if (s.Kind is ImageSymbolKind.Port) spoken.UnionWith(nets);
            if (TwoPin(s) && nets.Count == 2)
            {
                twoPin++;
                if (nets[0] == nets[1]) shorted++;
            }
        }
        foreach (var l in read.NetLabels) if (Net(l.Node) is var k && k >= 0) spoken.Add(k);
        foreach (var m in read.SupplyMarks) if (Net(m.Node) is var k && k >= 0) spoken.Add(k);
        foreach (var node in g.Nodes.Where(x => x.Terminal)) if (Net(node.Id) is var k && k >= 0) spoken.Add(k);
        int lonely = 0;
        for (int k = 0; k < g.NetCount; k++)
            if (pinsOn.GetValueOrDefault(k) < 2 && !spoken.Contains(k)) lonely++;
        double sane = twoPin + g.NetCount == 0 ? 0 : 1 - (double)(shorted + lonely) / (twoPin + g.NetCount);

        return new ReadingPlausibility
        {
            InkExplained = ink,
            PartsNamed = n == 0 ? 0 : (double)named / n,
            PinsHonest = n == 0 ? 0 : 1 - (double)dishonest / n,
            WordsRead = (wordShare + designators) / 2,
            GraphSane = Math.Clamp(sane, 0, 1),
            Regions = n, Named = named, Dishonest = dishonest, WordRegions = wordRegions, WordsReadCount = readRegions,
            Nets = g.NetCount, LonelyNets = lonely, ShortedParts = shorted,
        };
    }

    /// <summary>A region that is a part: recognised, or a multi-pin part of a known kind — a box, a triangle, a block.</summary>
    private static bool IsPart(RecognisedSymbol s) =>
        s.Disposition == SymbolDisposition.Recognised || s.Disposition == SymbolDisposition.CutOut && s.Kind != ImageSymbolKind.Unknown;

    private static bool TwoPin(RecognisedSymbol s) =>
        s.Kind is ImageSymbolKind.Resistor or ImageSymbolKind.Inductor or ImageSymbolKind.Capacitor or ImageSymbolKind.Diode
        || s.Disposition == SymbolDisposition.Unknown;

    /// <summary>A two-pin part with its two pins wired, and no region with more leads than its part has.</summary>
    private static bool Honest(RecognisedSymbol s)
    {
        int leads = s.Region.Attachments.Count(a => a.Node >= 0);
        if (s.Disposition == SymbolDisposition.CutOut && s.Kind == ImageSymbolKind.Unknown) return leads <= MaxUnnamedPins;
        if (s.Template is { } t && leads > t.Pins.Count) return false;
        return !TwoPin(s) || s.Pins.Count(p => p.Node >= 0) == 2;
    }

    /// <summary>The evidence a refusal names (R-imf9-3): the two weakest terms, in words.</summary>
    public string Evidence()
    {
        static string Pct(double v) => (100 * v).ToString("0", CultureInfo.InvariantCulture) + " %";
        var said = new (double Term, string Words)[]
        {
            (InkExplained, $"only {Pct(InkExplained)} of its ink reads as wires, symbols or words"),
            (PartsNamed, Regions == 0 ? "no part was found in it" : $"{Named} of its {Regions} symbol regions {(Named == 1 ? "is" : "are")} a part"),
            (PinsHonest, $"{Dishonest} of its symbol regions {(Dishonest == 1 ? "has" : "have")} more leads than a part has"),
            (WordsRead, WordRegions == 0 ? "it has no words" : $"{WordsReadCount} of its {WordRegions} words {(WordsReadCount == 1 ? "reads" : "read")}"),
            (GraphSane, $"{LonelyNets + ShortedParts} of its {Nets} nets go nowhere or are shorted by a part"),
        };
        return string.Join(", and ", said.OrderBy(s => s.Term).Take(2).Select(s => s.Words));
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Score:0.00} (ink {InkExplained:0.00}, parts {PartsNamed:0.00}, pins {PinsHonest:0.00}, words {WordsRead:0.00}, nets {GraphSane:0.00})");
}
