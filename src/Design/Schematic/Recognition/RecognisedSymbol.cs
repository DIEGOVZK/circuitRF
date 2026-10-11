// Each symbol region named — brief-img-8-schematic-symbols.md R-im8-5, R-im8-7, R-im8-8 (overview D9).
//
// SchematicSymbols.Read asks the classifier (R-im8-6) what each region of a schematic picture's reading is, and applies
// D9 to the answer, the same whichever classifier gave it:
//  * three or more attachments — a transistor, an IC box, or anything not matched — is a MULTI-PIN device, which
//    IM-10 makes an S-parameter block with one port per wired pin and a grounded pin its reference. So is an amplifier
//    triangle, though it has two;
//  * two attachments and no match is kind `?` with its two pins (generated as a C, as the artwork series does);
//  * one attachment and no match is an UNREAD TERMINAL, reported and left open.
// Nothing here reads a value or a designator (IM-9) or makes a circuit (IM-10).

using System.Globalization;
using CircuitRF.Design.Layout.Recognition;

namespace CircuitRF.Design.Schematic.Recognition;

/// <summary>What becomes of a region (R-im8-5).</summary>
public enum SymbolDisposition
{
    /// <summary>A part of a known kind.</summary>
    Recognised,

    /// <summary>Two pins, nothing matched: kind <c>?</c>.</summary>
    Unknown,

    /// <summary>A multi-pin device: an S-parameter block, a port per wired pin (D9).</summary>
    CutOut,

    /// <summary>One pin, nothing matched: left open.</summary>
    UnreadTerminal,
}

/// <summary>One pin of a recognised symbol, in the template's pin order.</summary>
/// <param name="Name">The template pin's name; for a symbol no template named, its number from 1.</param>
/// <param name="X">Where its wire reaches the symbol, in the read picture's pixels.</param>
/// <param name="Node">The wire node (<see cref="WireGraph"/>) it connects to.</param>
public sealed record RecognisedPin(int Index, string Name, double X, double Y, int Node);

/// <summary>One symbol region, named (R-im8-7).</summary>
/// <param name="Template">The template that matched — and so the drawing convention — or null.</param>
/// <param name="Orientation">How the template lies on the picture; R0 when none matched.</param>
/// <param name="Score">The classifier's distance (the template matcher's in units of w); +∞ when nothing was tried.</param>
/// <param name="RunnerUp">The best reading of another kind.</param>
/// <param name="Confidence">The parts table's dot.</param>
/// <param name="Demotions">Better-scoring readings a structure check set aside.</param>
public sealed record RecognisedSymbol(
    SymbolRegion Region, ImageSymbolKind Kind, SymbolDisposition Disposition, SymbolTemplate? Template,
    SymbolOrientation Orientation, IReadOnlyList<RecognisedPin> Pins, double Score, SymbolCandidate? RunnerUp,
    PartConfidence Confidence, IReadOnlyList<SymbolDemotion> Demotions)
{
    /// <summary>The drawing convention the matched template follows, or null.</summary>
    public string? Convention => Template?.Convention;

    /// <summary>Which of its kind it is (IMF-5): a transistor's polarity — from its arrow where that settles it — a
    /// source's AC, DC, supply, battery or current; null when the kind has one.</summary>
    public string? Variant { get; init; }

    /// <summary>A resistor crossed by an arrow: a variable resistor, whose value is one setting (R-imf5-3).</summary>
    public bool Variable { get; init; }

    /// <summary>The kind as the overlay names it: <c>?</c> for <see cref="ImageSymbolKind.Unknown"/>; a transistor's or a
    /// source's variant after it (<c>Transistor NPN</c>, <c>Source AC</c>), a variable resistor so called.</summary>
    public string KindLabel => Kind switch
    {
        ImageSymbolKind.Unknown => "?",
        ImageSymbolKind.Transistor or ImageSymbolKind.Source when Variant is { } v => $"{Kind} {v.ToUpperInvariant()}",
        ImageSymbolKind.Resistor when Variable => "Resistor (variable)",
        _ => Kind.ToString(),
    };
}

/// <summary>Every region of a reading, named, and the report.</summary>
public sealed record SchematicSymbolsRead(IReadOnlyList<RecognisedSymbol> Symbols, SymbolReport Report);

public static class SchematicSymbols
{
    /// <summary>The kinds that are an S-parameter block whatever their pin count (D9). A transistor is no longer one: it
    /// is circuitRF's own BJT or FET (IMF O1); nor is an amplifier, circuitRF's System Amp (IMF O5).</summary>
    public static bool IsDevice(ImageSymbolKind k) => k is ImageSymbolKind.Ic;

    /// <summary>The kinds read with their own pins whatever their count: a transistor's three, a transformer's four or
    /// five (IMF-5).</summary>
    public static bool IsMultiPinPart(ImageSymbolKind k) => k is ImageSymbolKind.Transistor or ImageSymbolKind.Transformer;

    /// <summary>Names every symbol region of <paramref name="read"/> with <paramref name="classifier"/> (the template
    /// matcher when null). Pure.</summary>
    public static SchematicSymbolsRead Read(SchematicImageRead read, IImageSymbolClassifier? classifier = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        classifier ??= new TemplateSymbolClassifier();
        double w = read.StrokeWidth, s0 = read.SymbolScale > 0 ? read.SymbolScale : TemplateSymbolClassifier.FallbackSymbolInW * w;
        var symbols = read.Symbols.Select(r =>
        {
            // What a datasheet draws round its part, read by how it is wired before any template (R-imf8-1 … R-imf8-3).
            if (DatasheetSymbols.IcBox(r, read.Outlines, w) is { } ic) return ic;
            if (r.Attachments.Count >= 2 && DatasheetSymbols.Triangle(r, w, s0) is { } triangle
                && DatasheetSymbols.Amplifier(r, triangle, w) is { } amp) return amp;
            if (DatasheetSymbols.Coax(r, w, s0))
                return new RecognisedSymbol(r, ImageSymbolKind.Port, SymbolDisposition.Recognised, null, default,
                                            [.. r.Attachments.Select((a, i) => Pin(i, (i + 1).ToString(CultureInfo.InvariantCulture), a))],
                                            0, null, PartConfidence.High, []) { Variant = DatasheetSymbols.CoaxVariant };
            var c = classifier.Classify(r, read.StrokeWidth, read.SymbolScale);
            // A lead ending in paper (R-imf5-4) stands only when it makes a part: else the region is read without it.
            if (c.Best is null && r.Attachments.Any(a => a.Node < 0))
            {
                r = r with { Attachments = [.. r.Attachments.Where(a => a.Node >= 0)] };
                c = classifier.Classify(r, read.StrokeWidth, read.SymbolScale);
            }
            var named = Name(r, c);
            // A filled dot on a wire's free end is a datasheet's terminal (R-imf6-1), whatever template it came nearest.
            return named.Pins.Count == 1 && named.Kind != ImageSymbolKind.Port && FilledDot(r, read.StrokeWidth)
                ? new RecognisedSymbol(r, ImageSymbolKind.Port, SymbolDisposition.Recognised, null, default, named.Pins, 0, null,
                                       PartConfidence.Medium, []) { Variant = DotVariant }
                : named;
        }).ToList();
        return new SchematicSymbolsRead(symbols, SymbolReport.Of(symbols));
    }

    /// <summary>The variant of a terminal drawn as a filled dot (R-imf6-1).</summary>
    public const string DotVariant = "dot";

    /// <summary>A region that is a filled dot: 3–8 w across each way, as wide as tall within half; filled out across its
    /// rows and its columns it fills at least seven tenths of its box and is the same turned half a turn, and its ink —
    /// with the run the wire reading took out of it where the wire ends inside given back — is nearly all of that: a disc,
    /// not an arrowhead, a ground or a ring.</summary>
    internal static bool FilledDot(SymbolRegion r, double w)
    {
        int W = r.Box.Width, H = r.Box.Height;
        if (W < 3 * w - 1 || H < 3 * w - 1 || W > 8 * w + 1 || H > 8 * w + 1 || Math.Max(W, H) > 1.5 * Math.Min(W, H)) return false;
        var m = r.Mask;
        var fill = new bool[W * H];
        for (int y = 0; y < H; y++)
        {
            int a = -1, b = -1;
            for (int x = 0; x < W; x++) if (m[x, y]) { if (a < 0) a = x; b = x; }
            for (int x = a; x >= 0 && x <= b; x++) fill[y * W + x] = true;
        }
        for (int x = 0; x < W; x++)
        {
            int a = -1, b = -1;
            for (int y = 0; y < H; y++) if (m[x, y]) { if (a < 0) a = y; b = y; }
            for (int y = a; y >= 0 && y <= b; y++) fill[y * W + x] = true;
        }
        // Its own ink, with the run the wire took out of it — up to a pen past the wire's end — given back.
        var at = r.Attachments.First(t => t.Node >= 0);
        int ink = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double dx = r.Box.Left + x + 0.5 - at.X, dy = r.Box.Top + y + 0.5 - at.Y;
                bool run = dx * at.DirX + dy * at.DirY <= w && Math.Abs(dx * at.DirY - dy * at.DirX) <= 0.75 * w + 1;
                if (m[x, y] || run && fill[y * W + x]) ink++;
            }
        int filled = fill.Count(f => f), same = 0;
        if (filled < 0.7 * W * H || ink < 0.85 * filled) return false;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (fill[y * W + x] && fill[(H - 1 - y) * W + (W - 1 - x)]) same++;
        return same >= 0.85 * filled;
    }

    /// <summary>D9 applied to one classification.</summary>
    public static RecognisedSymbol Name(SymbolRegion region, SymbolClassification c)
    {
        int n = region.Attachments.Count;
        var best = c.Best;
        var kind = best?.Kind ?? ImageSymbolKind.Unknown;
        var disposition = n >= 3 && !IsMultiPinPart(kind) || IsDevice(kind) ? SymbolDisposition.CutOut
                        : best is not null ? SymbolDisposition.Recognised
                        : n == 2 ? SymbolDisposition.Unknown
                        : SymbolDisposition.UnreadTerminal;

        IReadOnlyList<RecognisedPin> pins = best is not null
            ? [.. best.PinAttachments.Select((a, i) => Pin(i, best.Template.Pins[i].Name, region.Attachments[a]))]
            : [.. region.Attachments.Select((a, i) => Pin(i, (i + 1).ToString(CultureInfo.InvariantCulture), a))];
        return new RecognisedSymbol(region, kind, disposition, best?.Template, best?.Orientation ?? default, pins,
                                    best?.Score ?? c.RunnerUp?.Score ?? double.PositiveInfinity, c.RunnerUp,
                                    best is null ? PartConfidence.Low : c.Confidence, c.Demotions)
        {
            Variant = best is null ? null : c.Variant ?? best.Template.Variant,
            Variable = best is not null && c.Crossed,
        };
    }

    private static RecognisedPin Pin(int i, string name, SymbolAttachment a) => new(i, name, a.X, a.Y, a.Node);
}

/// <summary>What naming the symbols found (R-im8-8).</summary>
public sealed record SymbolReport
{
    /// <summary>Recognised parts by kind.</summary>
    public IReadOnlyDictionary<ImageSymbolKind, int> ByKind { get; init; } = new Dictionary<ImageSymbolKind, int>();

    /// <summary>Two-pin regions nothing matched: kind <c>?</c>.</summary>
    public int Unknown { get; init; }

    /// <summary>Each multi-pin device: its kind (<see cref="ImageSymbolKind.Unknown"/> when unmatched) and pin count.</summary>
    public IReadOnlyList<(ImageSymbolKind Kind, int Pins)> CutOut { get; init; } = [];

    /// <summary>Readings set aside by a structure check, by the check.</summary>
    public IReadOnlyDictionary<StructureCheck, int> Demotions { get; init; } = new Dictionary<StructureCheck, int>();

    public int UnreadTerminals { get; init; }

    /// <summary>Resistors crossed by an arrow (R-imf5-3).</summary>
    public int Variable { get; init; }

    /// <summary>Transistors and sources by their variant (R-imf5-6, R-imf5-7): <c>npn</c>, <c>ac</c> ….</summary>
    public IReadOnlyDictionary<string, int> Variants { get; init; } = new Dictionary<string, int>();

    public static SymbolReport Of(IReadOnlyList<RecognisedSymbol> symbols) => new()
    {
        Variable = symbols.Count(s => s.Variable),
        Variants = symbols.Where(s => s.Disposition == SymbolDisposition.Recognised && s.Kind is ImageSymbolKind.Transistor or ImageSymbolKind.Source
                                      && s.Variant is not null)
                          .GroupBy(s => s.Variant!).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
        ByKind = symbols.Where(s => s.Disposition == SymbolDisposition.Recognised).GroupBy(s => s.Kind)
                        .OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
        Unknown = symbols.Count(s => s.Disposition == SymbolDisposition.Unknown),
        CutOut = [.. symbols.Where(s => s.Disposition == SymbolDisposition.CutOut).Select(s => (s.Kind, s.Pins.Count))],
        Demotions = symbols.SelectMany(s => s.Demotions).GroupBy(d => d.Failed).OrderBy(g => g.Key)
                           .ToDictionary(g => g.Key, g => g.Count()),
        UnreadTerminals = symbols.Count(s => s.Disposition == SymbolDisposition.UnreadTerminal),
    };

    /// <summary>The report in words, one finding per line; a count of nothing is not a line.</summary>
    public IReadOnlyList<string> Lines()
    {
        var r = new List<string>();
        if (ByKind.Count > 0)
            r.Add("Symbols: " + string.Join(", ", ByKind.Select(kv => $"{kv.Value} {Noun(kv.Key)}")) + ".");
        if (Variants.Count > 0)
            r.Add("Transistors and sources: " + string.Join(", ", Variants.Select(kv => $"{kv.Value} {VariantNoun(kv.Key)}")) + ".");
        if (Variable > 0) r.Add($"{Count(Variable, "resistor")} crossed by an arrow: variable, {(Variable == 1 ? "its value" : "each value")} one setting.");
        if (Unknown > 0) r.Add($"{Count(Unknown, "two-pin symbol")} matching no drawing, read as ?.");
        foreach (var (kind, pins) in CutOut)
            r.Add($"{(kind == ImageSymbolKind.Unknown ? "Multi-pin symbol" : Noun(kind))} with {Count(pins, "pin")}: an S-parameter block.");
        foreach (var (check, n) in Demotions)
            r.Add($"{Count(n, "reading")} set aside by the {check.ToString().ToLowerInvariant()} structure check.");
        if (UnreadTerminals > 0) r.Add($"{Count(UnreadTerminals, "unread terminal")} left open.");
        return r;
    }

    private static string Noun(ImageSymbolKind k) => k switch
    {
        ImageSymbolKind.TransmissionLine => "transmission line",
        ImageSymbolKind.ShortStub => "short stub",
        ImageSymbolKind.OpenStub => "open stub",
        ImageSymbolKind.Ic => "IC",
        ImageSymbolKind.Bead => "ferrite bead",
        _ => k.ToString().ToLowerInvariant(),
    };

    private static string VariantNoun(string v) => v switch
    {
        "npn" => "n-p-n", "pnp" => "p-n-p", "nfet" => "n-channel FET", "pfet" => "p-channel FET",
        "ac" => "AC", "dc" => "DC", "supply" => "supply", "battery" => "battery", "current" => "current",
        _ => v,
    };

    private static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
}
