// What a trace is — brief-img-14-sparameter-plots-to-touchstone.md R-im14-6.
//
// An S-parameter ENTRY (i, j) and a QUANTITY, read from words: a legend entry, a label along the trace, the y-axis title
// or the plot's title, strongest first. The words come from the stroke reader, whose alphabet has no bar and no angle
// sign, so the grammar is forgiving where the reader cannot help it: |S21| reads as lS21l or 1S211, and both are S21.
// An unread title is unread, never guessed — a trace no evidence names is UNASSIGNED, and the Touchstone waits for it.

using System.Globalization;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Imaging.Plots;

/// <summary>What a trace's values are (R-im14-6).</summary>
public enum PlotQuantityKind
{
    Unassigned,
    /// <summary>20·log10|S|.</summary>
    Db,
    /// <summary>|S|.</summary>
    Magnitude,
    /// <summary>arg S, degrees.</summary>
    PhaseDeg,
    /// <summary>arg S, radians.</summary>
    PhaseRad,
    Real,
    Imag,
    /// <summary>The complex value itself: a Smith or polar trace.</summary>
    Complex,
    /// <summary>(1 + |Γ|) / (1 − |Γ|): gives |Γ| only.</summary>
    Vswr,
    /// <summary>−20·log10|Sii|, a positive number.</summary>
    ReturnLoss,
    /// <summary>−20·log10|Sij|, a positive number.</summary>
    InsertionLoss,
    /// <summary>Not a trace of the file: the user set it aside (a reference line, an annotation the pass took for a
    /// curve). Digitised into the CSV; never written to the Touchstone.</summary>
    Ignored,
}

/// <summary>A trace's assignment: which entry, which quantity, and what said so.</summary>
/// <param name="Evidence">Where it came from: <c>legend</c>, <c>label</c>, <c>axis title</c>, <c>plot title</c>,
/// <c>chart</c> (a Smith or polar trace is complex by its chart), or <c>user</c>.</param>
/// <param name="Unwrapped">A phase trace drawn unwrapped (it runs past ±180°).</param>
public sealed record PlotAssignment(int I, int J, PlotQuantityKind Quantity, string Evidence, bool Unwrapped = false)
{
    public static PlotAssignment None { get; } = new(0, 0, PlotQuantityKind.Unassigned, "");

    public bool Assigned => Quantity is not (PlotQuantityKind.Unassigned or PlotQuantityKind.Ignored) && I > 0 && J > 0;

    public bool Ignored => Quantity == PlotQuantityKind.Ignored;

    public bool IsMagnitude => Quantity is PlotQuantityKind.Db or PlotQuantityKind.Magnitude or PlotQuantityKind.Vswr
                               or PlotQuantityKind.ReturnLoss or PlotQuantityKind.InsertionLoss;

    public bool IsPhase => Quantity is PlotQuantityKind.PhaseDeg or PlotQuantityKind.PhaseRad;

    /// <summary>The entry, <c>S21</c>.</summary>
    public string Entry => I > 0 && J > 0 ? $"S{I}{J}" : "?";

    /// <summary>The wire spelling, <c>S21:db</c> — what <c>--trace</c> takes.</summary>
    public override string ToString() => Ignored ? "ignore" : Assigned ? $"{Entry}:{PlotQuantity.Name(Quantity)}" : "unassigned";
}

public static class PlotQuantity
{
    public static string Name(PlotQuantityKind k) => k switch
    {
        PlotQuantityKind.Db => "db",
        PlotQuantityKind.Magnitude => "mag",
        PlotQuantityKind.PhaseDeg => "phase",
        PlotQuantityKind.PhaseRad => "phase-rad",
        PlotQuantityKind.Real => "re",
        PlotQuantityKind.Imag => "im",
        PlotQuantityKind.Complex => "complex",
        PlotQuantityKind.Vswr => "vswr",
        PlotQuantityKind.ReturnLoss => "rl",
        PlotQuantityKind.InsertionLoss => "il",
        PlotQuantityKind.Ignored => "ignore",
        _ => "unassigned",
    };

    /// <summary>Parses a quantity's wire name (<c>db</c>, <c>mag</c>, <c>phase</c>, …).</summary>
    public static PlotQuantityKind? ParseName(string text) => text.Trim().ToLowerInvariant() switch
    {
        "db" or "db20" => PlotQuantityKind.Db,
        "mag" or "lin" or "linear" => PlotQuantityKind.Magnitude,
        "phase" or "deg" or "ang" or "phase-deg" => PlotQuantityKind.PhaseDeg,
        "phase-rad" or "rad" => PlotQuantityKind.PhaseRad,
        "re" or "real" => PlotQuantityKind.Real,
        "im" or "imag" => PlotQuantityKind.Imag,
        "complex" or "smith" or "polar" or "ri" => PlotQuantityKind.Complex,
        "vswr" => PlotQuantityKind.Vswr,
        "rl" or "return-loss" => PlotQuantityKind.ReturnLoss,
        "il" or "insertion-loss" => PlotQuantityKind.InsertionLoss,
        "ignore" or "ignored" => PlotQuantityKind.Ignored,
        _ => null,
    };

    /// <summary>Parses <c>S21:db</c> (the <c>--trace</c> assignment's right-hand side).</summary>
    public static PlotAssignment? ParseAssignment(string text, string evidence = "user")
    {
        if (ParseName(text) == PlotQuantityKind.Ignored) return new PlotAssignment(0, 0, PlotQuantityKind.Ignored, evidence);
        var parts = text.Split(':', 2);
        if (Entry(parts[0]) is not { } e) return null;
        var q = parts.Length == 2 ? ParseName(parts[1]) : PlotQuantityKind.Db;
        return q is { } k ? new PlotAssignment(e.I, e.J, k, evidence) : null;
    }

    // S(2,1), S21, s21, S_21, |S21| read as lS21l / 1S211 / IS21I, ∠S21 read as anything before the S. The stroke
    // reader has no bracket it is sure of at a small size — S(2,1) comes back as Sf2 1) or S[2.1 — so a bracket is any
    // of the strokes it is read as, and the comma any separator; a digit is never taken for a bracket.
    private static readonly Regex EntryShape = new(
        @"(?:[Ss]\s*(?:[(\[{fFlIt|]\s*([1-4])\s*[,.;\s]\s*([1-4])|[(\[{fFlIt|]?\s*_?([1-4])([1-4]))|(?<![0-9.])5\s*[(\[{fFlIt|]\s*([1-4])\s*[,.;\s]\s*([1-4]))",
        RegexOptions.CultureInvariant);

    /// <summary>The S-parameter entry a word names, or null.</summary>
    public static (int I, int J)? Entry(string text)
    {
        // Inside an entry a Z is a 2 (the stroke reader's commonest confusion at a small size); outside it, it is
        // left alone — Z0 is a word of its own.
        var m = EntryShape.Match(text);
        if (!m.Success) m = EntryShape.Match(Regex.Replace(text, @"(?<=[Ss5][(\[{fFlIt|]?\s*_?[1-4,.\s]*)[Zz]", "2"));
        // …and a 1 as t, l, I or | straight after the entry's first digit (S2t is S21), or as that first digit itself
        // when the second is a digit too (a title's S11 at 18 px reads Stt).
        if (!m.Success)
        {
            string one = Regex.Replace(text, @"(?<=[Ss][(\[{]?\s*_?)[tlI|](?=[1-4tlI|](?![a-z]))", "1");
            m = EntryShape.Match(Regex.Replace(one, @"(?<=[Ss][(\[{]?\s*_?[1-4])[tlI|](?![a-z])", "1"));
        }
        if (!m.Success) return null;
        string a = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[5].Value;
        string b = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Success ? m.Groups[4].Value : m.Groups[6].Value;
        return (int.Parse(a, CultureInfo.InvariantCulture), int.Parse(b, CultureInfo.InvariantCulture));
    }

    /// <summary>The quantity a word names, or null — from its unit and its function words.</summary>
    public static PlotQuantityKind? Kind(string text)
    {
        // A typeset B at a small size reads as θ or 8: dθ and d8 are dB.
        string t = Regex.Replace(text.ToLowerInvariant(), @"\s+", "").Replace("dθ", "db").Replace("d8", "db");
        if (t.Contains("vswr") || t.Contains("swr")) return PlotQuantityKind.Vswr;
        if (t.Contains("returnloss") || Regex.IsMatch(t, @"(^|[^a-z])rl([^a-z]|$)")) return PlotQuantityKind.ReturnLoss;
        if (t.Contains("insertionloss") || Regex.IsMatch(t, @"(^|[^a-z])il([^a-z]|$)") || t.Contains("isolation")) return PlotQuantityKind.InsertionLoss;
        if (t.Contains("phase") || Regex.IsMatch(t, "ph[a-z]se") || t.Contains("ang") || t.Contains("deg") || t.Contains("°")) return t.Contains("rad") ? PlotQuantityKind.PhaseRad : PlotQuantityKind.PhaseDeg;
        if (t.Contains("rad")) return PlotQuantityKind.PhaseRad;
        if (Regex.IsMatch(t, @"(^|[^a-z])(re|real)\(") || t.StartsWith("real", StringComparison.Ordinal)) return PlotQuantityKind.Real;
        if (Regex.IsMatch(t, @"(^|[^a-z])(im|imag)\(") || t.StartsWith("imag", StringComparison.Ordinal)) return PlotQuantityKind.Imag;
        if (t.Contains("db") || t.Contains("gain")) return PlotQuantityKind.Db;
        if (t.Contains("mag") || t.StartsWith('|') || Regex.IsMatch(t, @"^[l1i]s")) return PlotQuantityKind.Magnitude;
        return null;
    }

    /// <summary>A word that is evidence of a trace's entry or quantity, or a frequency axis's title.</summary>
    public static bool IsTitleWord(string text) =>
        Entry(text) is not null || Kind(text) is not null || FrequencyUnit(text) is not null || text.Contains("Z0", StringComparison.Ordinal);

    /// <summary>The unit an axis title gives frequency in — <c>Frequency (GHz)</c>, <c>freq [MHz]</c> — as its SI
    /// multiplier, or null. Read as the stroke reader reads it at a small size: a G comes back as C, O, Q or 6 (circuitRF's
    /// own <c>freq (GHz)</c> reads <c>freq fCHz1</c>), a z as Z or 2.</summary>
    public static double? FrequencyUnit(string text)
    {
        var m = HzShape.Match(text.Replace(" ", ""));
        if (!m.Success) return null;
        return m.Groups[1].Value switch
        {
            "T" => 1e12,
            "G" or "C" or "O" or "Q" or "6" => 1e9,
            "M" => 1e6,
            "k" or "K" => 1e3,
            _ => 1,
        };
    }

    private static readonly Regex HzShape = new(@"([TGCOQ6MkK]?)H[zZ2](?![a-z])", RegexOptions.CultureInvariant);

    /// <summary>Whether an axis title says the axis is frequency.</summary>
    public static bool IsFrequencyTitle(string text) =>
        FrequencyUnit(text) is not null || text.Contains("freq", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A trace's assignment from its evidence, strongest first: each source may give the entry, the quantity, or
    /// both; the entry is taken from the strongest source naming one and the quantity likewise. A trace on a chart is
    /// complex by its chart, whatever the words say.
    /// </summary>
    /// <param name="sources">(evidence name, word) pairs, strongest first.</param>
    public static PlotAssignment Assign(IEnumerable<(string Evidence, string Text)> sources, PlotType type)
    {
        (int I, int J)? entry = null;
        PlotQuantityKind? kind = type == PlotType.Rectangular ? null : PlotQuantityKind.Complex;
        string? entryFrom = null, kindFrom = type == PlotType.Rectangular ? null : "chart";
        foreach (var (evidence, text) in sources)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (entry is null && Entry(text) is { } e) { entry = e; entryFrom = evidence; }
            if (kind is null && Kind(text) is { } k) { kind = k; kindFrom = evidence; }
            // "Gain" names S21 when nothing else names an entry; "Return Loss" names S11; "Isolation" S12.
            if (entry is null)
            {
                string t = text.ToLowerInvariant();
                if (t.Contains("gain") || t.Contains("insertion")) { entry = (2, 1); entryFrom = evidence; }
                else if (t.Contains("isolation")) { entry = (1, 2); entryFrom = evidence; }
            }
        }
        if (entry is not { } en || kind is not { } kd) return PlotAssignment.None with { Evidence = entryFrom ?? kindFrom ?? "" };
        string from = entryFrom == kindFrom || kindFrom is null ? entryFrom! : $"{entryFrom} + {kindFrom}";
        return new PlotAssignment(en.I, en.J, kd, from);
    }

    /// <summary>A value of quantity <paramref name="kind"/> as |S| (a magnitude quantity) — VSWR, return and insertion
    /// loss carry their sign conventions here (R-im14-6).</summary>
    public static double Magnitude(PlotQuantityKind kind, double v) => kind switch
    {
        PlotQuantityKind.Db => Math.Pow(10, v / 20),
        PlotQuantityKind.Magnitude => Math.Abs(v),
        PlotQuantityKind.Vswr => v <= 1 ? 0 : (v - 1) / (v + 1),
        PlotQuantityKind.ReturnLoss or PlotQuantityKind.InsertionLoss => Math.Pow(10, -Math.Abs(v) / 20),
        _ => double.NaN,
    };

    /// <summary>A phase quantity's value in radians.</summary>
    public static double Radians(PlotQuantityKind kind, double v) => kind == PlotQuantityKind.PhaseRad ? v : v * Math.PI / 180;
}
