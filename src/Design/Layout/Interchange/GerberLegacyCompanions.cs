// The RS-274D output set: artwork with no format and no apertures of its own, a drill file with no
// tool diameters, and both answered only by the plain-text reports printed beside them.
//
// THE PROBLEM THIS FILE EXISTS FOR: before RS-274X, a Gerber file was a bare stream of X/Y/D-code
// blocks. The coordinate format and the aperture shapes were set up on the photoplotter by hand, from
// a printed aperture report shipped with the set — so the artwork itself has no %FS, no %MO and no
// %AD at all, and its drill file selects tools (T1F2S5) that it never sizes. Read as RS-274X, every
// D-code is "used before it was defined", every draw has "no aperture selected", and the import
// finished with zero shapes on every layer and nothing to say about why.
//
// WHAT IS DONE ABOUT IT: the reports are read as companions, exactly as GerberDeclarationFile reads a
// parameter file, and each becomes the HEADER its file would have carried — %FS/%MO/%AD for the
// artwork, an M48 tool table for the drill file. The readers themselves are untouched: they read the
// file as if it had been written with the header in it, which is precisely what the plotter operator
// did with the printed report.
//
// WHAT IS INFERRED, AND SAID SO: neither report states a unit or a coordinate format. The artwork is
// read as inch with three decimals (one unit = one mil, the overwhelmingly usual RS-274D setting) and
// the reports' sizes as mils. That is a guess, and the import says it is — a board that comes out the
// wrong size is rescaled by hand, which is better than a board that does not come out at all.
//
// THE THIRD THING THESE SETS DO: the artwork is plotted at a film position while the drill data sits at
// board coordinates, so every hole lands inches away from its pad. RegisterDrill finds the one shift
// that puts the holes on the pads, and applies it only when most of them land on one.
//
// Recognised by COLUMN HEADINGS, never by a file name (GerberFileClassifier's doctrine, and the reason
// no toolchain is named anywhere in here).

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>One row of an aperture report: the D-code, its size (in the report's own unit, mils) and
/// the shape word the report prints.</summary>
public sealed record LegacyApertureRow(int Code, int Width, int Height, string Shape);

/// <summary>A printed aperture report — the %AD table an RS-274D file does not carry.</summary>
public sealed record LegacyApertureTable(string Path, IReadOnlyList<LegacyApertureRow> Rows)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>One row of a drill size report: a tool number, its size (mils) and the plating column when
/// the report has one.</summary>
public sealed record LegacyDrillSizeRow(int Tool, int Size, bool? Plated);

/// <summary>A printed drill size report — the tool table a drill file that selects but never sizes
/// its tools does not carry.</summary>
public sealed record LegacyDrillSizeTable(string Path, IReadOnlyList<LegacyDrillSizeRow> Rows)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>What <see cref="GerberLegacyCompanions.SynthesizeArtworkHeader"/> made of one RS-274D file:
/// <see cref="AperturesDefined"/> counts the apertures the file USES, and <see cref="Sources"/> names
/// the reports they came from.</summary>
public sealed record LegacyArtworkHeader(
    string Header, int IntegerDigits, int DecimalDigits, GerberZeroOmission ZeroOmission,
    int AperturesDefined, IReadOnlyList<string> Sources, IReadOnlyList<string> Notes);

public static class GerberLegacyCompanions
{
    /// <summary>The report's sizes are mils: one thousandth of the inch the coordinates are read in.</summary>
    private const double MilsPerInch = 1000.0;

    /// <summary>A heading and one row is a coincidence; a heading and two rows is a table.</summary>
    public const int MinimumRows = 2;

    // ── Recognition ───────────────────────────────────────────────────────────

    /// <summary>An aperture report: a heading naming a position (the D-code), a width and a shape, over
    /// rows of <c>code width height SHAPE</c>.</summary>
    public static bool RecognizeApertureTable(string head, out string why)
    {
        int rows = ReadApertureRows(head).Count;
        why = rows >= MinimumRows ? $"an aperture report ({rows} aperture row(s))" : "";
        return rows >= MinimumRows;
    }

    /// <summary>A drill size report: a heading naming a tool and a size, over rows of integers.</summary>
    public static bool RecognizeDrillSizeTable(string head, out string why)
    {
        int rows = ReadDrillSizeRows(head).Count;
        why = rows >= MinimumRows ? $"a drill size report ({rows} tool row(s))" : "";
        return rows >= MinimumRows;
    }

    public static LegacyApertureTable? ReadApertureTable(string path)
    {
        string? text = ReadText(path);
        if (text is null) return null;
        var rows = ReadApertureRows(text);
        return rows.Count >= MinimumRows ? new LegacyApertureTable(System.IO.Path.GetFullPath(path), rows) : null;
    }

    public static LegacyDrillSizeTable? ReadDrillSizeTable(string path)
    {
        string? text = ReadText(path);
        if (text is null) return null;
        var rows = ReadDrillSizeRows(text);
        return rows.Count >= MinimumRows ? new LegacyDrillSizeTable(System.IO.Path.GetFullPath(path), rows) : null;
    }

    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static List<LegacyApertureRow> ReadApertureRows(string text)
    {
        var rows = new List<LegacyApertureRow>();
        bool inTable = false;
        foreach (string[] fields in Lines(text))
        {
            if (!inTable)
            {
                inTable = HasWords(fields, "POSITION", "WIDTH", "SHAPE") || HasWords(fields, "APERTURE", "WIDTH", "SHAPE");
                continue;
            }
            // code width height SHAPE [qty]: three integers then a word.
            if (fields.Length < 4 || Code(fields[0]) is not { } code || Int(fields[1]) is not { } width ||
                Int(fields[2]) is not { } height || !fields[3].All(char.IsAsciiLetter)) continue;
            if (rows.All(r => r.Code != code)) rows.Add(new LegacyApertureRow(code, width, height, fields[3].ToUpperInvariant()));
        }
        return rows;
    }

    private static List<LegacyDrillSizeRow> ReadDrillSizeRows(string text)
    {
        var rows = new List<LegacyDrillSizeRow>();
        string[]? heading = null;
        int platingColumn = -1;
        foreach (string[] fields in Lines(text))
        {
            if (heading is null)
            {
                if (fields.Length >= 2 && Up(fields[0]) == "TOOL" && Up(fields[1]) == "SIZE")
                {
                    heading = fields;
                    platingColumn = Array.FindIndex(fields, f => Up(f) is "PLTD" or "PLATED" or "PLT");
                }
                continue;
            }
            if (fields.Length < 2 || Int(fields[0]) is not { } tool || Int(fields[1]) is not { } size || size <= 0)
                continue;

            // The plating column prints a mark or nothing, so a row one field short of the heading is
            // a row with that column blank.
            bool? plated = null;
            if (platingColumn > 0)
            {
                if (fields.Length == heading.Length) plated = Up(fields[platingColumn]) is "X" or "Y" or "YES";
                else if (fields.Length == heading.Length - 1) plated = false;
            }
            if (rows.All(r => r.Tool != tool)) rows.Add(new LegacyDrillSizeRow(tool, size, plated));
        }
        return rows;
    }

    private static IEnumerable<string[]> Lines(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            var fields = raw.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 0) yield return fields;
        }
    }

    private static bool HasWords(string[] fields, params string[] words)
    {
        var upper = fields.Select(f => Up(f).Split('/')[0]).ToHashSet(StringComparer.Ordinal);
        return words.All(upper.Contains);
    }

    private static string Up(string s) => s.ToUpperInvariant();

    private static int? Int(string s) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;

    private static int? Code(string s) => Int(s.StartsWith('D') || s.StartsWith('d') ? s[1..] : s);

    // ── The artwork ───────────────────────────────────────────────────────────

    private static readonly Regex DCode = new(@"D(\d+)\*", RegexOptions.CultureInvariant);
    private static readonly Regex Coordinate = new(@"[XY]([+-]?)(\d+)", RegexOptions.CultureInvariant);

    /// <summary>An RS-274D file: no format, no aperture definitions, and at least one aperture
    /// selected anyway. An RS-274X file always carries the first two.</summary>
    public static bool IsRs274D(string text) =>
        !text.Contains("%FS", StringComparison.Ordinal) &&
        !text.Contains("%AD", StringComparison.Ordinal) &&
        DCode.Matches(text).Any(m => int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out int d) && d >= 10);

    /// <summary>
    /// The header the file would have carried: an inferred %FS, inch, and an %AD for every aperture
    /// the tables define. <paramref name="tables"/> are in priority order — the first table to define
    /// a code wins, and a later one that disagrees is reported.
    /// </summary>
    public static LegacyArtworkHeader SynthesizeArtworkHeader(string text, IReadOnlyList<LegacyApertureTable> tables)
    {
        var notes = new List<string>();

        // THE COORDINATE FORMAT. Numbers of varying length that never start with a zero are leading-
        // zero-omitted; varying lengths that DO keep their leading zeros are trailing-omitted. The
        // decimals are three (mils) unless the numbers are too long for that to be a board.
        var words = Coordinate.Matches(text).Select(m => m.Groups[2].Value).ToList();
        int longest = words.Count == 0 ? 5 : words.Max(w => w.Length);
        bool varying = words.Select(w => w.Length).Distinct().Skip(1).Any();
        bool zerosKept = words.Any(w => w.Length > 1 && w[0] == '0');
        var omission = varying && zerosKept ? GerberZeroOmission.Trailing : GerberZeroOmission.Leading;
        int integers = 2;
        int decimals = Math.Max(3, longest - integers);
        if (decimals > 6) { integers = longest - 6; decimals = 6; }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"%FS{(omission == GerberZeroOmission.Leading ? 'L' : 'T')}AX{integers}{decimals}Y{integers}{decimals}*%\n");
        sb.Append("%MOIN*%\n");

        var used = DCode.Matches(text)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Where(d => d >= 10).ToHashSet();

        var defined = new Dictionary<int, (LegacyApertureRow Row, LegacyApertureTable From)>();
        var conflicts = new SortedSet<int>();
        foreach (var table in tables)
            foreach (var row in table.Rows)
            {
                if (!defined.TryGetValue(row.Code, out var first)) defined[row.Code] = (row, table);
                else if (first.Row.Width != row.Width || first.Row.Height != row.Height ||
                         first.Row.Shape != row.Shape) conflicts.Add(row.Code);
            }

        int count = 0;
        var unsupported = new List<string>();
        var sources = new List<string>();
        foreach (var (code, (row, from)) in defined.OrderBy(kv => kv.Key))
        {
            if (Aperture(row) is { } template)
            {
                sb.Append(CultureInfo.InvariantCulture, $"%ADD{code}{template}*%\n");
                if (!used.Contains(code)) continue;
                count++;
                if (!sources.Contains(from.FileName)) sources.Add(from.FileName);
            }
            else if (used.Contains(code)) unsupported.Add($"D{code} ({row.Shape})");
        }

        if (unsupported.Count > 0)
            notes.Add($"{unsupported.Count} aperture(s) this file uses have a shape with no equivalent here " +
                      $"and were left undefined: {string.Join(", ", unsupported)}.");
        var usedConflicts = conflicts.Where(used.Contains).ToList();
        if (usedConflicts.Count > 0)
            notes.Add($"The aperture reports disagree about {string.Join(", ", usedConflicts.Select(c => $"D{c}"))}; " +
                      "the one beside this file was used.");
        var missing = used.Where(d => !defined.ContainsKey(d)).Order().ToList();
        if (missing.Count > 0)
            notes.Add($"{missing.Count} aperture(s) this file uses are in no aperture report and were not drawn: " +
                      $"{string.Join(", ", missing.Select(c => $"D{c}"))}.");

        return new LegacyArtworkHeader(sb.ToString(), integers, decimals, omission, count, sources, notes);
    }

    /// <summary>The %AD template for one report row, in inch. A plotted line and a round flash are both
    /// a circle; a square's report gives one side; null for a shape with no equivalent.</summary>
    private static string? Aperture(LegacyApertureRow row)
    {
        if (row.Width <= 0) return null;
        string w = Inch(row.Width);
        string h = Inch(row.Height > 0 ? row.Height : row.Width);
        return row.Shape switch
        {
            "LINE" or "RND" or "ROUND" or "CIRCLE" or "CIRC" or "RD" => $"C,{w}",
            "SQR" or "SQRL" or "SQUARE" or "SQ" => $"R,{w}X{w}",
            "RECT" or "RECTANGLE" or "RCT" => $"R,{w}X{h}",
            "OVAL" or "OBLONG" or "OBL" => $"O,{w}X{h}",
            _ => null,
        };
    }

    private static string Inch(int mils) =>
        (mils / MilsPerInch).ToString("0.######", CultureInfo.InvariantCulture);

    // ── The drill file ────────────────────────────────────────────────────────

    private static readonly Regex ToolSelect = new(@"(?m)^\s*T(\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex ToolDefinition = new(@"(?m)^\s*T\d+[^\n]*C\d*\.?\d", RegexOptions.CultureInvariant);

    /// <summary>A drill file that selects tools and sizes none of them.</summary>
    public static bool NeedsToolSizes(string text) =>
        ToolSelect.IsMatch(text) && !ToolDefinition.IsMatch(text);

    private static readonly Regex UnitWord = new(@"(?m)^\s*(INCH|METRIC)\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// The drill file with the report's tool table in front of it as an M48 header, sizes in inch.
    /// The file's own body is unchanged; its first <c>%</c> closes the header.
    ///
    /// <para>The report's sizes are mils, so the drill file is inch; when it does not say so itself,
    /// the header says it, with the zeros and digits its own coordinates show — the same inference
    /// <see cref="SynthesizeArtworkHeader"/> makes for the artwork. <paramref name="inferredFormat"/>
    /// is that line, or null when the file states its own unit.</para>
    /// </summary>
    public static string WithToolTable(string text, LegacyDrillSizeTable table, out string? inferredFormat)
    {
        var sb = new StringBuilder("M48\n");
        inferredFormat = null;
        if (!UnitWord.IsMatch(text))
        {
            var words = Coordinate.Matches(text).Select(m => m.Groups[2].Value).ToList();
            int longest = words.Count == 0 ? 5 : words.Max(w => w.Length);
            bool zerosKept = words.Any(w => w.Length > 1 && w[0] == '0');
            // LZ is the Excellon sense: leading zeros KEPT, i.e. trailing ones suppressed.
            string zeros = zerosKept ? "LZ" : "TZ";
            int decimals = Math.Max(3, longest - 2);
            inferredFormat = $"INCH,{zeros},00.{new string('0', decimals)}";
            sb.Append(inferredFormat).Append('\n');
        }
        if (table.Rows.All(r => r.Plated == true)) sb.Append(";TYPE=PLATED\n");
        else if (table.Rows.All(r => r.Plated == false)) sb.Append(";TYPE=NON_PLATED\n");
        foreach (var row in table.Rows)
            sb.Append(CultureInfo.InvariantCulture, $"T{row.Tool}C{Inch(row.Size)}\n");
        if (!text.TrimStart().StartsWith('%')) sb.Append("%\n");
        return sb.Append(text).ToString();
    }

    /// <summary>
    /// The evidence for a drill file whose format came from the line <see cref="WithToolTable"/>
    /// wrote, restated as what it is. The reader credits that line to the file — "declared by the
    /// file (its INCH/METRIC line)" — and the file said nothing of the kind. Only the parts that line
    /// settled are restated; a caller's override keeps its own sentence.
    /// </summary>
    public static ExcellonReadResult AsInferred(ExcellonReadResult read)
    {
        if (read.Refusal is not null) return read;
        var f = read.Format;
        const DrillFormatEvidence fromLine = DrillFormatEvidence.UnitsKeyword;
        string unit = f.Unit == GerberUnit.Inches ? "inch" : "millimetre";
        string zeros = f.ZeroOmission == GerberZeroOmission.Leading ? "leading" : "trailing";

        string Restate(string line) =>
            line.StartsWith("Units:", StringComparison.Ordinal) && f.UnitEvidence == fromLine
                ? $"Units: {unit} — INFERRED: the file does not say, and the drill size report beside it is in mils."
            : line.StartsWith("Digit format:", StringComparison.Ordinal) && f.DigitsEvidence == fromLine
                ? $"Digit format: {f.IntegerDigits}:{f.DecimalDigits} — INFERRED from the file's own coordinates; the file declares no format."
            : line.StartsWith("Zero suppression:", StringComparison.Ordinal) && f.ZeroOmissionEvidence == fromLine
                ? $"Zero suppression: {zeros} zeros suppressed — INFERRED: the file carries no LZ/TZ word, and its " +
                  (f.ZeroOmission == GerberZeroOmission.Trailing
                      ? "coordinates keep their leading zeros."
                      : "coordinates are written without leading zeros.")
            : line;

        var evidence = f.Evidence.Select(Restate).ToList();
        var format = new DrillFormatInference
        {
            Unit = f.Unit, UnitEvidence = f.UnitEvidence,
            IntegerDigits = f.IntegerDigits, DecimalDigits = f.DecimalDigits, DigitsEvidence = f.DigitsEvidence,
            ZeroOmission = f.ZeroOmission, ZeroOmissionEvidence = f.ZeroOmissionEvidence,
            DecimalCoordinates = f.DecimalCoordinates, ZeroSuppressionApplies = f.ZeroSuppressionApplies,
            Evidence = evidence,
        };
        // The reader copies the same sentences into its diagnostics; restate them there too.
        var original = f.Evidence.ToHashSet(StringComparer.Ordinal);
        var diagnostics = read.Diagnostics.Select(d => original.Contains(d) ? Restate(d) : d).ToList();
        return read with { Format = format, Diagnostics = diagnostics };
    }

    // ── Registration ──────────────────────────────────────────────────────────

    /// <summary>
    /// The one shift that puts this drill file's holes on the artwork's pads, or null when there is
    /// none worth taking: at least three holes, and at least half of them, must land on a pad centre
    /// after the shift, and more of them than land on one without it.
    /// </summary>
    public static (long Dx, long Dy, int Matched, int Total)? FindDrillOffset(
        ExcellonReadResult drill, IEnumerable<GerberImportedShape> artwork, long toleranceDbu)
    {
        if (drill.Hits.Count < 3 || toleranceDbu <= 0) return null;

        var centres = new List<(long X, long Y)>();
        foreach (var imported in artwork)
            switch (imported.Shape)
            {
                case CircleShape c: centres.Add((c.Cx, c.Cy)); break;
                case RectShape r: centres.Add(((r.X1 + r.X2) / 2, (r.Y1 + r.Y2) / 2)); break;
                case RoundedRectShape rr: centres.Add(((rr.X1 + rr.X2) / 2, (rr.Y1 + rr.Y2) / 2)); break;
            }
        if (centres.Count < 3) return null;

        var cells = centres.Select(c => Cell(c.X, c.Y, toleranceDbu)).ToHashSet();

        // A sample of holes votes for every offset that would put it on a pad; the offset most of the
        // sample agrees on is the candidate, and every hole then checks it.
        int stride = Math.Max(1, drill.Hits.Count / 48);
        var votes = new Dictionary<(long, long), int>();
        for (int i = 0; i < drill.Hits.Count; i += stride)
        {
            var hit = drill.Hits[i];
            foreach (var c in centres)
            {
                var key = Cell(c.X - hit.X, c.Y - hit.Y, toleranceDbu);
                votes[key] = votes.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }
        if (votes.Count == 0) return null;
        var best = votes.MaxBy(kv => kv.Value).Key;

        // The cell is only the tolerance; the shift itself is the EXACT difference most of the sample
        // shares inside it, because via pairing matches a hole to its pad by coordinate.
        var exact = new Dictionary<(long, long), int>();
        for (int i = 0; i < drill.Hits.Count; i += stride)
        {
            var hit = drill.Hits[i];
            foreach (var c in centres)
            {
                var d = (c.X - hit.X, c.Y - hit.Y);
                if (Cell(d.Item1, d.Item2, toleranceDbu) == best) exact[d] = exact.TryGetValue(d, out int n) ? n + 1 : 1;
            }
        }
        var (dx, dy) = exact.MaxBy(kv => kv.Value).Key;

        int matched = Matched(drill, cells, dx, dy, toleranceDbu);
        int unshifted = Matched(drill, cells, 0, 0, toleranceDbu);
        int total = drill.Hits.Count;
        if (matched < 3 || matched * 2 < total || matched <= unshifted || (dx == 0 && dy == 0)) return null;
        return (dx, dy, matched, total);
    }

    private static int Matched(ExcellonReadResult drill, HashSet<(long, long)> cells, long dx, long dy, long tol)
    {
        int matched = 0;
        foreach (var hit in drill.Hits)
        {
            var (cx, cy) = Cell(hit.X + dx, hit.Y + dy, tol);
            bool on = false;
            for (long i = -1; i <= 1 && !on; i++)
                for (long j = -1; j <= 1 && !on; j++)
                    on = cells.Contains((cx + i, cy + j));
            if (on) matched++;
        }
        return matched;
    }

    private static (long, long) Cell(long x, long y, long tol) =>
        ((long)Math.Round((double)x / tol), (long)Math.Round((double)y / tol));

    /// <summary>The drill file with every hit and slot moved by (dx, dy).</summary>
    public static ExcellonReadResult Shift(ExcellonReadResult drill, long dx, long dy)
    {
        var extents = DrillExtents.Empty;
        var hits = drill.Hits.Select(h => h with { X = h.X + dx, Y = h.Y + dy }).ToList();
        foreach (var h in hits) extents = extents.Include(h.X, h.Y);
        var slots = drill.Slots.Select(s =>
        {
            var xy = new long[s.Xy.Length];
            for (int i = 0; i + 1 < xy.Length; i += 2) { xy[i] = s.Xy[i] + dx; xy[i + 1] = s.Xy[i + 1] + dy; }
            return s with { Xy = xy };
        }).ToList();
        foreach (var s in slots)
            for (int i = 0; i + 1 < s.Xy.Length; i += 2) extents = extents.Include(s.Xy[i], s.Xy[i + 1]);
        return drill with { Hits = hits, Slots = slots, Extents = extents };
    }
}
