// The companion files a Gerber output set ships beside its artwork
// (docs/sonnet-briefs/brief-gi4-companion-declaration-files.md).
//
// THE PROBLEM THIS FILE EXISTS FOR: a production output set routinely ships, in the same folder as
// the artwork, a plain-text parameter file stating the coordinate format, the units and the zero
// suppression the WHOLE JOB was written with — and a tool listing carrying the column a drill file
// most often omits, whether each tool is plated. Both were classified "no Gerber or drill content in
// its head" and skipped, and in the same run the import reported, in words, that it had INFERRED the
// digit format. An inference that is usually right is still an inference; a declaration sitting three
// lines above in the skipped list is the strongest evidence available short of the file speaking for
// itself.
//
// RECOGNISED BY KEYWORDS, NEVER BY A FILENAME CONVENTION (R-gi4-1). That is not a new doctrine — it
// is GerberFileClassifier's own, stated in its header and gated: renaming a file to a misleading
// extension must not change what it is classified as. It also keeps any toolchain's naming out of
// this repository (root CLAUDE.md, "Commercial Vendor References"). TWO recognised keywords are
// required before the kind is claimed, for the reason LooksLikeJobFile refuses to call arbitrary JSON
// a job file: one keyword is a coincidence in somebody's unrelated settings file.
//
// THE ONE TRAP, AND IT IS AN ERROR OF ORDERS OF MAGNITUDE (R-gi4-4): a declaration file's suppression
// flags name the zeros that are SUPPRESSED — the GERBER sense (%FSL = leading zeros omitted), NOT the
// Excellon sense (LZ = leading zeros KEPT). So a declaration maps onto GerberZeroOmission directly
// and must never go through the Excellon inversion. The conversion lives in ExcellonFormat beside the
// existing one, with the same warning attached; nothing here inverts anything.

using System.Globalization;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>Which files in the folder a declaration speaks for (R-gi4-6). An artwork parameter file
/// speaks for the artwork and a drill parameter file for the drill data; neither reaches across that
/// boundary, and neither reaches outside its own folder.</summary>
public enum GerberDeclarationScope
{
    /// <summary>The file states no data type. Applied to the drill data — the only kind in a Gerber
    /// set that does not state its own format — and SAID so, rather than assumed silently.</summary>
    Unstated,
    Artwork,
    Drill,
}

/// <summary>Which of the two companion forms a <see cref="GerberFileKind.Declaration"/> file is.
/// R-gi4-2: a tool listing is the same KIND, separately identified — it is read for one column and
/// nothing else.</summary>
public enum GerberDeclarationForm
{
    None,

    /// <summary>A <c>KEYWORD  VALUE</c> parameter file stating the job's coordinate format.</summary>
    Parameters,

    /// <summary>A table of tool numbers, diameters and a plating column.</summary>
    ToolListing,

    /// <summary>A printed aperture report: the %AD table an RS-274D artwork file does not carry. See
    /// <see cref="GerberLegacyCompanions"/>.</summary>
    ApertureTable,

    /// <summary>A printed drill size report: the tool table a drill file that never sizes its tools
    /// does not carry. See <see cref="GerberLegacyCompanions"/>.</summary>
    DrillSizeTable,
}

/// <summary>What one parameter file declared. Every field is null when the file did not state it, and
/// <see cref="Refusal"/> non-null means NOTHING here may be used — see R-gi4-5 on the scale.</summary>
public sealed record GerberDeclaration(
    string Path,
    GerberDeclarationScope Scope,
    GerberUnit? Unit,
    int? IntegerDigits,
    int? DecimalDigits,
    GerberZeroOmission? ZeroOmission,
    bool ZeroSuppressionNone,
    double? Scale,
    IReadOnlyList<string> Keywords,
    string? Refusal)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>True when the file settled at least one of the three unknowns a drill file leaves
    /// open. A recognised declaration that states none of them is reported and used for nothing.</summary>
    public bool StatesAnything =>
        Refusal is null &&
        (Unit is not null || (IntegerDigits is not null && DecimalDigits is not null) ||
         ZeroOmission is not null || ZeroSuppressionNone);

    /// <summary>R-gi4-8's sentence: what this file settled, in the words the import prints.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Unit is { } u) parts.Add(u == GerberUnit.Inches ? "inch" : "mm");
            if (IntegerDigits is { } i && DecimalDigits is { } d) parts.Add($"{i}:{d}");
            if (ZeroSuppressionNone) parts.Add("no zero suppression");
            else if (ZeroOmission is { } z)
                parts.Add(z == GerberZeroOmission.Leading ? "leading zeros suppressed" : "trailing zeros suppressed");
            return parts.Count == 0 ? "nothing about the coordinate format" : string.Join(", ", parts);
        }
    }
}

/// <summary>One row of a tool listing. The diameter is kept as WRITTEN as well as parsed, because a
/// disagreement with the drill file is reported with both numbers in it (gate 8).</summary>
public sealed record DrillToolListingRow(int Number, string DiameterText, double Diameter, bool? Plated);

/// <summary>A tool listing, which is a PLATING SOURCE AND NOTHING ELSE (R-gi4-7). The drill file stays
/// authoritative about every hit, every coordinate and every diameter.</summary>
public sealed record DrillToolListing(string Path, GerberUnit? Unit, IReadOnlyList<DrillToolListingRow> Rows)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public int PlatingRows => Rows.Count(r => r.Plated is not null);
}

public static class GerberDeclarationFile
{
    // ── Recognition (R-gi4-1) ─────────────────────────────────────────────────

    /// <summary>R-gi4-1's two-keyword minimum, and R-gi4-2's tool listing, over text already in hand
    /// — the form <see cref="GerberFileClassifier.ClassifyContent"/> drives, so no fixture needs a
    /// temporary directory to assert what a byte stream is. Returns <see cref="GerberDeclarationForm.None"/>
    /// when the text is neither.</summary>
    public static GerberDeclarationForm Recognize(string head, out string why)
    {
        int keywords = CountKeywords(head);
        if (keywords >= MinimumKeywords)
        {
            why = $"a parameter file declaring the job's output format ({keywords} recognized keyword(s))";
            return GerberDeclarationForm.Parameters;
        }

        var (rows, plating) = CountListingRows(head);
        if (rows >= MinimumListingRows && plating > 0)
        {
            why = $"a drill tool listing ({rows} tool row(s), {plating} of them stating plating)";
            return GerberDeclarationForm.ToolListing;
        }

        if (GerberLegacyCompanions.RecognizeApertureTable(head, out why)) return GerberDeclarationForm.ApertureTable;
        if (GerberLegacyCompanions.RecognizeDrillSizeTable(head, out why)) return GerberDeclarationForm.DrillSizeTable;

        why = "";
        return GerberDeclarationForm.None;
    }

    /// <summary>ONE keyword is a coincidence in somebody's unrelated settings file — the same caution
    /// <see cref="GerberFileClassifier"/> already writes into its job-file test, which refuses to call
    /// arbitrary JSON a job file.</summary>
    public const int MinimumKeywords = 2;

    /// <summary>A single <c>T1 0.3 PLATED</c> line can appear in prose; two of them in a row is a
    /// table.</summary>
    public const int MinimumListingRows = 2;

    private static int CountKeywords(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in Lines(text))
            if (SplitKeyword(line) is { } kv && Canonical(kv.Key) is { } canonical)
                seen.Add(canonical);
        return seen.Count;
    }

    private static (int Rows, int WithPlating) CountListingRows(string text)
    {
        int rows = 0, plating = 0;
        foreach (string line in Lines(text))
            if (ParseListingRow(line) is { } row)
            {
                rows++;
                if (row.Plated is not null) plating++;
            }
        return (rows, plating);
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>Reads a parameter file. Returns null when the text is not one.</summary>
    public static GerberDeclaration? ReadParameters(string path, string text)
    {
        GerberUnit? unit = null;
        int? integers = null, decimals = null;
        bool? leadingSuppressed = null, trailingSuppressed = null;
        GerberZeroOmission? explicitOmission = null;
        bool explicitNone = false;
        double? scale = null;
        var scope = GerberDeclarationScope.Unstated;
        var keywords = new List<string>();

        foreach (string line in Lines(text))
        {
            if (SplitKeyword(line) is not { } kv) continue;
            if (Canonical(kv.Key) is not { } key) continue;
            if (!keywords.Contains(key, StringComparer.Ordinal)) keywords.Add(key);
            string value = kv.Value;

            switch (key)
            {
                case KeyIntegerDigits when Count(value) is { } n: integers = n; break;
                case KeyDecimalDigits when Count(value) is { } n: decimals = n; break;
                case KeyFormat when Split(value) is { } split: integers ??= split.Integers; decimals ??= split.Decimals; break;
                case KeyUnits when Units(value) is { } u: unit = u; break;
                case KeySuppressLeading when Flag(value) is { } b: leadingSuppressed = b; break;
                case KeySuppressTrailing when Flag(value) is { } b: trailingSuppressed = b; break;
                case KeyZeroSuppression:
                    if (Contains(value, "NONE") || Contains(value, "NO-SUPPRESSION")) explicitNone = true;
                    else if (Contains(value, "LEAD")) explicitOmission = GerberZeroOmission.Leading;
                    else if (Contains(value, "TRAIL")) explicitOmission = GerberZeroOmission.Trailing;
                    break;
                case KeyScale when Number(value) is { } s: scale = s; break;
                case KeyDataType when DataType(value) is { } t: scope = t; break;
                default: break;
            }
        }

        if (keywords.Count < MinimumKeywords) return null;

        // R-gi4-4. THE FLAGS NAME THE ZEROS THAT ARE SUPPRESSED, which is the GERBER sense, so the
        // conversion below is a direct mapping and NOT the Excellon inversion. It lives in
        // ExcellonFormat beside that inversion so the two can be read together.
        var (omission, none) = ExcellonFormat.ZeroOmissionFromSuppressionFlags(leadingSuppressed, trailingSuppressed);
        if (explicitOmission is { } eo) { omission = eo; none = false; }
        else if (explicitNone) { omission = null; none = true; }

        // R-gi4-5: a scale factor is never silently ignored. circuitRF does not scale coordinates on
        // the way in — every reader here maps an output unit onto DBU exactly — so a declaration that
        // states anything but unity is REFUSED rather than half-applied. Dropping it multiplies the
        // whole board.
        string? refusal = scale is { } s2 && Math.Abs(s2 - 1.0) > 1e-9
            ? $"states a coordinate scale of {s2.ToString("0.######", CultureInfo.InvariantCulture)}, " +
              "which circuitRF does not apply — so nothing in it was used. Re-export the set at a " +
              "scale of 1, or state the format with the import's own controls."
            : null;

        return new GerberDeclaration(
            System.IO.Path.GetFullPath(path), scope, unit, integers, decimals, omission, none, scale,
            keywords, refusal);
    }

    /// <summary>Reads a tool listing. Returns null when the text is not one.</summary>
    public static DrillToolListing? ReadToolListing(string path, string text)
    {
        GerberUnit? unit = null;
        var rows = new List<DrillToolListingRow>();
        foreach (string line in Lines(text))
        {
            if (SplitKeyword(line) is { } kv && Canonical(kv.Key) == KeyUnits && Units(kv.Value) is { } u)
                unit = u;
            if (ParseListingRow(line) is { } row && rows.All(r => r.Number != row.Number)) rows.Add(row);
        }

        return rows.Count >= MinimumListingRows && rows.Any(r => r.Plated is not null)
            ? new DrillToolListing(System.IO.Path.GetFullPath(path), unit, rows)
            : null;
    }

    /// <summary>Reads whichever of the two forms <paramref name="path"/> holds, from disk. Returns
    /// (null, null) for a file that is neither, and for one that cannot be read — an unreadable
    /// companion is not a reason to fail an import of the artwork beside it.</summary>
    public static (GerberDeclaration? Parameters, DrillToolListing? Listing) ReadFile(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException) { return (null, null); }
        catch (UnauthorizedAccessException) { return (null, null); }

        return (ReadParameters(path, text), ReadToolListing(path, text));
    }

    // ── Line shapes ───────────────────────────────────────────────────────────

    private static IEnumerable<string> Lines(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length > 0) yield return line;
        }
    }

    /// <summary>A <c>KEYWORD  VALUE</c> line, in the three separators these files use: whitespace, an
    /// <c>=</c> or a <c>:</c>. A line with no value at all is not a declaration of anything.</summary>
    private static (string Key, string Value)? SplitKeyword(string line)
    {
        int cut = -1;
        for (int i = 0; i < line.Length; i++)
            if (line[i] is '=' or ':' || char.IsWhiteSpace(line[i])) { cut = i; break; }
        if (cut <= 0) return null;

        string key = line[..cut];
        string value = line[cut..].TrimStart('=', ':', ' ', '\t').Trim();
        return value.Length == 0 ? null : (key, value);
    }

    /// <summary>Uppercase, and every run of non-alphanumeric characters collapsed to one hyphen — the
    /// declarations in circulation are uppercase and hyphenated, and the same word appears
    /// underscored, spaced and dotted in different sets. Matching the SHAPE rather than the exact
    /// spelling is what keeps any one toolchain's spelling out of the table below.</summary>
    private static string Normalize(string key)
    {
        var sb = new System.Text.StringBuilder(key.Length);
        bool pendingSeparator = false;
        foreach (char c in key)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingSeparator && sb.Length > 0) sb.Append('-');
                pendingSeparator = false;
                sb.Append(char.ToUpperInvariant(c));
            }
            else pendingSeparator = true;
        }
        return sb.ToString();
    }

    private const string KeyIntegerDigits = "INTEGER-DIGITS";
    private const string KeyDecimalDigits = "DECIMAL-DIGITS";
    private const string KeyFormat = "FORMAT";
    private const string KeyUnits = "UNITS";
    private const string KeySuppressLeading = "SUPPRESS-LEADING-ZEROS";
    private const string KeySuppressTrailing = "SUPPRESS-TRAILING-ZEROS";
    private const string KeyZeroSuppression = "ZERO-SUPPRESSION";
    private const string KeyScale = "SCALE";
    private const string KeyDataType = "DATA-TYPE";

    /// <summary>
    /// The recognised vocabulary, as SHAPES rather than as one product's spellings. Each entry is a
    /// normalized alias; the value is the canonical key the reader switches on.
    ///
    /// <para>Kept deliberately small. Every alias here is a phrase that describes the thing it names
    /// in ordinary words — "integer places", "suppress leading zeros" — which is why the same handful
    /// covers every set on hand, and why nothing in it identifies who wrote the file.</para>
    /// </summary>
    private static readonly Dictionary<string, string> Vocabulary = new(StringComparer.Ordinal)
    {
        ["INTEGER-PLACES"] = KeyIntegerDigits,
        ["INTEGER-DIGITS"] = KeyIntegerDigits,
        ["INTEGER-PART"] = KeyIntegerDigits,
        ["LEADING-DIGITS"] = KeyIntegerDigits,
        ["WHOLE-DIGITS"] = KeyIntegerDigits,

        ["DECIMAL-PLACES"] = KeyDecimalDigits,
        ["DECIMAL-DIGITS"] = KeyDecimalDigits,
        ["DECIMAL-PART"] = KeyDecimalDigits,
        ["TRAILING-DIGITS"] = KeyDecimalDigits,
        ["FRACTION-DIGITS"] = KeyDecimalDigits,

        ["FORMAT"] = KeyFormat,
        ["COORDINATE-FORMAT"] = KeyFormat,
        ["DATA-FORMAT"] = KeyFormat,
        ["NUMBER-FORMAT"] = KeyFormat,
        ["DIGIT-FORMAT"] = KeyFormat,

        ["UNITS"] = KeyUnits,
        ["UNIT"] = KeyUnits,
        ["OUTPUT-UNITS"] = KeyUnits,
        ["DATA-UNITS"] = KeyUnits,
        ["COORDINATE-UNITS"] = KeyUnits,
        ["MEASUREMENT-UNITS"] = KeyUnits,

        // BOTH the full word and the clipped one. These tables are written to a fixed column width,
        // so "LEADING" is routinely clipped to "LEAD" and "TRAILING" to "TRAIL" — and the cost of
        // missing the alias is not a missing keyword, it is the WHOLE FILE going quiet about the one
        // field this rung exists for. A parameter file that states the digits and whose suppression
        // line is not recognised is strictly WORSE than no declaration at all: it supplies the digit
        // count, which displaces the file's own coordinate-width inference, and then leaves the
        // suppression DEFAULTED — so a set that settled itself before is raised as a prompt, or
        // headless refused outright.
        ["SUPPRESS-LEADING-ZEROS"] = KeySuppressLeading,
        ["SUPPRESS-LEADING-ZEROES"] = KeySuppressLeading,
        ["SUPPRESS-LEAD-ZEROS"] = KeySuppressLeading,
        ["SUPPRESS-LEAD-ZEROES"] = KeySuppressLeading,
        ["OMIT-LEADING-ZEROS"] = KeySuppressLeading,
        ["OMIT-LEAD-ZEROS"] = KeySuppressLeading,
        ["LEADING-ZERO-SUPPRESSION"] = KeySuppressLeading,
        ["LEAD-ZERO-SUPPRESSION"] = KeySuppressLeading,

        ["SUPPRESS-TRAILING-ZEROS"] = KeySuppressTrailing,
        ["SUPPRESS-TRAILING-ZEROES"] = KeySuppressTrailing,
        ["SUPPRESS-TRAIL-ZEROS"] = KeySuppressTrailing,
        ["SUPPRESS-TRAIL-ZEROES"] = KeySuppressTrailing,
        ["OMIT-TRAILING-ZEROS"] = KeySuppressTrailing,
        ["OMIT-TRAIL-ZEROS"] = KeySuppressTrailing,
        ["TRAILING-ZERO-SUPPRESSION"] = KeySuppressTrailing,
        ["TRAIL-ZERO-SUPPRESSION"] = KeySuppressTrailing,

        ["ZERO-SUPPRESSION"] = KeyZeroSuppression,
        ["ZEROS"] = KeyZeroSuppression,
        ["ZERO-HANDLING"] = KeyZeroSuppression,

        ["SCALE"] = KeyScale,
        ["SCALE-FACTOR"] = KeyScale,
        ["COORDINATE-SCALE"] = KeyScale,
        ["OUTPUT-SCALE"] = KeyScale,

        // DEVICE-TYPE names the OUTPUT DEVICE the job was written for, and that is the only place an
        // artwork parameter file usually says which data it describes. Without it such a file is
        // Unstated, so it lands in the same folder bucket as the drill parameter file beside it, the
        // two are compared, and a folder whose companions describe DIFFERENT KINDS of data is
        // reported as one whose companions contradict each other — with both discarded. The value
        // guard below is what keeps this from claiming a plotter model number: it yields a scope only
        // when the value names a data kind.
        ["DATA-TYPE"] = KeyDataType,
        ["FILE-TYPE"] = KeyDataType,
        ["OUTPUT-TYPE"] = KeyDataType,
        ["CONTENT-TYPE"] = KeyDataType,
        ["DEVICE-TYPE"] = KeyDataType,
        ["OUTPUT-DEVICE"] = KeyDataType,
    };

    private static string? Canonical(string key) =>
        Vocabulary.TryGetValue(Normalize(key), out string? canonical) ? canonical : null;

    // ── Values ────────────────────────────────────────────────────────────────

    private static int? Count(string value)
    {
        string t = value.Trim();
        int end = 0;
        while (end < t.Length && char.IsAsciiDigit(t[end])) end++;
        return end > 0 && int.TryParse(t[..end], NumberStyles.None, CultureInfo.InvariantCulture, out int n)
               && n is > 0 and <= 12 ? n : null;
    }

    /// <summary>A <c>3:4</c> or <c>3.4</c> digit split. Both spellings are in circulation and both mean
    /// the same thing here — three integer digits, four decimal.</summary>
    private static (int Integers, int Decimals)? Split(string value)
    {
        string t = value.Trim();
        int sep = t.IndexOfAny([':', '.', ',']);
        if (sep <= 0) return null;
        return Count(t[..sep]) is { } i && Count(t[(sep + 1)..]) is { } d ? (i, d) : null;
    }

    private static GerberUnit? Units(string value)
    {
        string t = Normalize(value);
        if (t.StartsWith("METRIC", StringComparison.Ordinal) || t.StartsWith("MM", StringComparison.Ordinal) ||
            t.StartsWith("MILLIM", StringComparison.Ordinal)) return GerberUnit.Millimetres;
        if (t.StartsWith("INCH", StringComparison.Ordinal) || t.Equals("IN", StringComparison.Ordinal) ||
            t.StartsWith("IMPERIAL", StringComparison.Ordinal)) return GerberUnit.Inches;
        return null;
    }

    private static bool? Flag(string value)
    {
        string t = Normalize(value);
        if (t is "YES" or "Y" or "ON" or "TRUE" or "1" or "SUPPRESSED") return true;
        if (t is "NO" or "N" or "OFF" or "FALSE" or "0" or "NONE" or "KEPT") return false;
        return null;
    }

    private static double? Number(string value)
    {
        string t = value.Trim();
        int end = 0;
        while (end < t.Length && (char.IsAsciiDigit(t[end]) || t[end] is '.' or '+' or '-' or 'e' or 'E')) end++;
        return double.TryParse(t[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : null;
    }

    private static GerberDeclarationScope? DataType(string value)
    {
        string t = Normalize(value);

        // "NC" is a WORD here and never a substring, which is the one asymmetry in this method and it
        // is deliberate: two letters test true inside dozens of ordinary ones — "INCH" and "ENCODED"
        // among them — and this test runs first, so a substring match would classify an artwork
        // declaration as drill data and scope the whole file at the wrong half of the set. Every
        // other marker below is long enough that a substring cannot collide by accident.
        if (t.Contains("DRILL", StringComparison.Ordinal) || t.Contains("ROUT", StringComparison.Ordinal) ||
            t.Contains("EXCELLON", StringComparison.Ordinal) || HasWord(t, "NC"))
            return GerberDeclarationScope.Drill;
        if (t.Contains("GERBER", StringComparison.Ordinal) || t.Contains("ARTWORK", StringComparison.Ordinal) ||
            t.Contains("IMAGE", StringComparison.Ordinal) || t.Contains("PLOT", StringComparison.Ordinal))
            return GerberDeclarationScope.Artwork;
        return null;
    }

    /// <summary>Whether <paramref name="word"/> appears in <paramref name="normalized"/> as a whole
    /// hyphen-separated word. <see cref="Normalize"/> has already collapsed every separator to a
    /// hyphen, so a split on it is the whole of the test.</summary>
    private static bool HasWord(string normalized, string word)
    {
        foreach (string part in normalized.Split('-'))
            if (part.Equals(word, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool Contains(string value, string what) =>
        Normalize(value).Contains(what, StringComparison.Ordinal);

    // ── Tool listing rows ─────────────────────────────────────────────────────

    /// <summary>
    /// One row of a tool listing: a tool number, a diameter and — the whole reason to read the file —
    /// a plating word. Deliberately tolerant of the column order after the tool number, because these
    /// tables carry extra columns (a hit count, a depth, a feed) in no fixed place.
    ///
    /// <para>A row with no recognised plating word still counts as a row, so a table can be
    /// identified by shape; the file is only accepted as a listing when at least one row states
    /// plating, since R-gi4-7 reads it for that column and nothing else.</para>
    /// </summary>
    private static DrillToolListingRow? ParseListingRow(string line)
    {
        var fields = line.Split([' ', '\t', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2) return null;

        if (ToolNumber(fields[0]) is not { } number) return null;

        double diameter = 0;
        string diameterText = "";
        for (int i = 1; i < fields.Length; i++)
            if (Diameter(fields[i]) is { } d && d.Value > 0)
            {
                diameter = d.Value;
                diameterText = d.Text;
                break;
            }

        // THE PLATING WORD IS READ OFF THE WHOLE ROW, not field by field. "NON PLATED" is written
        // with a space in some of these tables, and a per-field test sees "NON" (which means nothing)
        // followed by "PLATED" (which means the opposite of what the row says). Normalize collapses
        // the separator, so the two spellings become one.
        bool? plated = Plating(string.Join(' ', fields.Skip(1)));

        // A tool number and nothing measurable is a heading or a sentence, not a row.
        return diameterText.Length == 0 ? null : new DrillToolListingRow(number, diameterText, diameter, plated);
    }

    /// <summary>A leading <c>T</c> is conventional and optional; what is required is that the field is
    /// a bare tool number, so a prose line beginning with a word is not a row.</summary>
    private static int? ToolNumber(string field)
    {
        string t = field.StartsWith('T') || field.StartsWith('t') ? field[1..] : field;
        return t.Length > 0 && t.All(char.IsAsciiDigit) &&
               int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    /// <summary>A decimal number, optionally written as the drill file writes it (<c>C0.300</c>). A
    /// bare integer is NOT a diameter — a hit count sits in these tables and would otherwise be read
    /// as one.</summary>
    private static (double Value, string Text)? Diameter(string field)
    {
        string t = field.StartsWith('C') || field.StartsWith('c') ? field[1..] : field;
        if (!t.Contains('.', StringComparison.Ordinal)) return null;
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? (v, t) : null;
    }

    /// <summary>The plating column. Non-plated spellings are tested first and in full, exactly as
    /// <see cref="ExcellonReader.PlatingFromFileName"/> does, because "NPTH" contains "PTH".</summary>
    private static bool? Plating(string field)
    {
        string t = Normalize(field);
        foreach (string marker in NonPlated)
            if (t.Contains(marker, StringComparison.Ordinal)) return false;
        foreach (string marker in Plated)
            if (t.Contains(marker, StringComparison.Ordinal)) return true;
        return null;
    }

    private static readonly string[] NonPlated =
        ["NPTH", "NON-PLATED", "NONPLATED", "UNPLATED", "NOT-PLATED", "NO-PLATE"];

    private static readonly string[] Plated = ["PTH", "PLATED", "PLTD"];
}

/// <summary>
/// The companion files of one import, scoped the way R-gi4-6 requires: <b>to the files of their own
/// kind, in their own folder</b>. Nothing here reaches outside the folder a declaration sits in —
/// R-L4g-3's rule that an import must never quietly consume something the user did not point at
/// applies unchanged, and a declaration read from a neighbouring folder would be exactly that.
/// </summary>
public sealed class GerberCompanionFiles
{
    private readonly Dictionary<string, GerberDeclaration> _drillDeclarations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DrillToolListing> _listings = [];
    private readonly List<LegacyApertureTable> _apertureTables = [];
    private readonly List<LegacyDrillSizeTable> _drillSizeTables = [];
    private readonly List<GerberDeclaration> _all = [];
    private readonly List<string> _messages = [];

    /// <summary>What could not be used, and why — R-gi4-8's second half. Reported by the import
    /// alongside everything a declaration DID settle.</summary>
    public IReadOnlyList<string> Messages => _messages;

    /// <summary>Every parameter file that was recognised, used or not.</summary>
    public IReadOnlyList<GerberDeclaration> Declarations => _all;

    public IReadOnlyList<DrillToolListing> Listings => _listings;

    /// <summary>Every printed aperture report, for RS-274D artwork (<see cref="GerberLegacyCompanions"/>).</summary>
    public IReadOnlyList<LegacyApertureTable> ApertureTables => _apertureTables;

    public static GerberCompanionFiles Read(IEnumerable<GerberFileClass> declarationFiles)
    {
        var set = new GerberCompanionFiles();
        var byFolder = new Dictionary<string, List<GerberDeclaration>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in declarationFiles.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (file.Form == GerberDeclarationForm.ApertureTable)
            {
                if (GerberLegacyCompanions.ReadApertureTable(file.Path) is { } apertures) set._apertureTables.Add(apertures);
                continue;
            }
            if (file.Form == GerberDeclarationForm.DrillSizeTable)
            {
                if (GerberLegacyCompanions.ReadDrillSizeTable(file.Path) is { } sizes) set._drillSizeTables.Add(sizes);
                continue;
            }

            var (parameters, listing) = GerberDeclarationFile.ReadFile(file.Path);
            if (listing is not null) set._listings.Add(listing);
            if (parameters is null) continue;

            set._all.Add(parameters);

            if (parameters.Refusal is { } refusal)
            {
                // R-gi4-5: a scale factor is never silently ignored. The whole file is refused, said
                // by name, rather than half-applied.
                set._messages.Add($"{parameters.FileName} {refusal}");
                continue;
            }

            if (parameters.Scope == GerberDeclarationScope.Artwork)
            {
                // R-gi4-6's boundary, and the one case where honouring it costs nothing: every Gerber
                // artwork file carries a mandatory %FS/%MO pair, so an artwork declaration is always
                // outranked by the file it describes (R-gi4-3).
                set._messages.Add(
                    $"{parameters.FileName} declares {parameters.Summary} for this set's ARTWORK. It was " +
                    "not used: every Gerber file states its own format and unit, and a file is " +
                    "authoritative about itself.");
                continue;
            }

            if (!parameters.StatesAnything)
            {
                set._messages.Add(
                    $"{parameters.FileName} is a parameter file, but it states nothing about the " +
                    "coordinate format, so nothing was taken from it.");
                continue;
            }

            string folder = Folder(parameters.Path);
            if (!byFolder.TryGetValue(folder, out var list)) byFolder[folder] = list = [];
            list.Add(parameters);
        }

        foreach (var (folder, list) in byFolder)
        {
            if (list.Count == 1) { set._drillDeclarations[folder] = list[0]; Announce(set, list[0]); continue; }

            // Two parameter files in one folder that state the same thing are a copy; two that
            // disagree are a folder nobody can read a job format out of, and picking one of them
            // would be exactly the silent guess this phase exists to remove.
            var first = list[0];
            bool agree = list.All(d =>
                (d.Unit ?? first.Unit) == first.Unit &&
                (d.IntegerDigits ?? first.IntegerDigits) == first.IntegerDigits &&
                (d.DecimalDigits ?? first.DecimalDigits) == first.DecimalDigits &&
                (d.ZeroOmission ?? first.ZeroOmission) == first.ZeroOmission &&
                d.ZeroSuppressionNone == first.ZeroSuppressionNone);
            if (agree) { set._drillDeclarations[folder] = first; Announce(set, first); }
            else
                set._messages.Add(
                    $"{list.Count} parameter files in this folder ({string.Join(", ", list.Select(d => d.FileName))}) " +
                    "declare DIFFERENT coordinate formats, so none of them was used. Each drill file " +
                    "was read from what it says about itself.");
        }

        return set;
    }

    /// <summary>R-gi4-6, the case where the file does not draw the boundary for us. A parameter file
    /// that states no data type is applied to the DRILL data — the only kind in a Gerber set that does
    /// not state its own format, so it is the only kind a declaration can settle anything for — and
    /// that decision is SAID rather than made silently, which is the whole doctrine of this phase.</summary>
    private static void Announce(GerberCompanionFiles set, GerberDeclaration declaration)
    {
        if (declaration.Scope != GerberDeclarationScope.Unstated) return;
        set._messages.Add(
            $"{declaration.FileName} declares {declaration.Summary} but does not say which data it " +
            "describes, so it was applied to this folder's DRILL data — the only kind here that does " +
            "not state its own format. Every Gerber artwork file was read from its own %FS/%MO pair.");
    }

    /// <summary>The parameter file that speaks for <paramref name="drillFilePath"/>: one in its own
    /// folder, scoped to the drill data or scoped to nothing at all.</summary>
    public GerberDeclaration? DrillDeclarationFor(string drillFilePath) =>
        _drillDeclarations.TryGetValue(Folder(drillFilePath), out var d) ? d : null;

    /// <summary>The tool listing that goes with <paramref name="drillFilePath"/>: one in its own
    /// folder, preferring a stem match — a set with a plated and a non-plated drill file has one
    /// listing each, and giving both files the same listing would state one file's plating over the
    /// other's.</summary>
    public DrillToolListing? ListingFor(string drillFilePath)
    {
        string folder = Folder(drillFilePath);
        string stem = Path.GetFileNameWithoutExtension(drillFilePath);
        var here = _listings.Where(l => string.Equals(Folder(l.Path), folder, StringComparison.OrdinalIgnoreCase))
                            .ToList();
        if (here.Count == 0) return null;

        var matched = here.Where(l => GerberFileClassifier.StemsMatch(
                              stem, Path.GetFileNameWithoutExtension(l.Path))).ToList();
        if (matched.Count == 1) return matched[0];
        if (matched.Count > 1) return null;
        return here.Count == 1 ? here[0] : null;
    }

    /// <summary>The aperture reports that speak for an RS-274D <paramref name="artworkPath"/>: those in
    /// its own folder, the one whose stem matches first — a per-layer report beside a whole-job table,
    /// where the two agree, is the same table twice.</summary>
    public IReadOnlyList<LegacyApertureTable> ApertureTablesFor(string artworkPath)
    {
        string folder = Folder(artworkPath);
        string stem = Path.GetFileNameWithoutExtension(artworkPath);
        return [.. _apertureTables
            .Where(t => string.Equals(Folder(t.Path), folder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => GerberFileClassifier.StemsMatch(stem, Path.GetFileNameWithoutExtension(t.Path)) ? 0 : 1)
            .ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The drill size report for a drill file that sizes none of its tools: one in its own
    /// folder, preferring a stem match, as <see cref="ListingFor"/> chooses.</summary>
    public LegacyDrillSizeTable? DrillSizesFor(string drillFilePath)
    {
        string folder = Folder(drillFilePath);
        string stem = Path.GetFileNameWithoutExtension(drillFilePath);
        var here = _drillSizeTables
            .Where(t => string.Equals(Folder(t.Path), folder, StringComparison.OrdinalIgnoreCase)).ToList();
        var matched = here.Where(t => GerberFileClassifier.StemsMatch(
                              stem, Path.GetFileNameWithoutExtension(t.Path))).ToList();
        if (matched.Count == 1) return matched[0];
        if (matched.Count > 1) return null;
        return here.Count == 1 ? here[0] : null;
    }

    private static string Folder(string path) =>
        Path.GetDirectoryName(Path.GetFullPath(path)) ?? Path.GetFullPath(path);
}
