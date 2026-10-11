// The parts table as a document — brief-artsch-4-parts-and-parts-table.md R-as4-8; overview D10.
//
// UTF-8, comma-delimited, a fixed header in R-as4-7's order, one row per part sorted by designator in
// natural order. THE FORMAT IS THE CONTRACT: the GUI's grid, `--parts-out` and `--parts` all go through
// this file, so a user or an agent edits the table the same way.
//
// Reading back is an OVERLAY on a fresh recognition, never a replacement for one:
//   - a row whose designator the board has overrides the EDITABLE columns (Kind, Value, Variable, Model,
//     ModelFile); a value clears the variable;
//   - the MEASURED columns (Connection, Case, X, Y, Evidence, Confidence, PartNumber) are read off the
//     board again and the table's are ignored — with a note where the two differ, so an edit that did
//     nothing says so;
//   - a designator the board does not have is reported and ignored: the table never invents a part — except that
//     a row at the X and Y of a part whose designator was READ off the silkscreen or GENERATED renames that part
//     (AS-10 R-as10-5): those two designators are recognition's guesses, and correcting one is an edit like any
//     other. A designator from a placed footprint, a placement file or a BOM is the board's own and is never renamed;
//   - a column the table does not have is a REFUSAL naming it, because a misspelt "Vaule" would
//     otherwise do nothing, silently.
// Write then read is the identity.

using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Design.Layout.Recognition;

/// <summary>What reading a parts table back did.</summary>
/// <param name="Table">The recognised table with the edits applied, or null on a refusal.</param>
/// <param name="Notes">One sentence per edit not applied or measured column that differed.</param>
/// <param name="NotOnBoard">Designators the table names and the board does not have.</param>
/// <param name="Refusal">Why the table could not be read at all, or null.</param>
public sealed record PartsCsvReading(
    PartsTable? Table, IReadOnlyList<string> Notes, IReadOnlyList<string> NotOnBoard, string? Refusal);

/// <summary>Writes and reads the parts table's CSV.</summary>
public static class PartsTableCsv
{
    /// <summary>The header, in order.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "Refdes", "Kind", "Connection", "Case", "Value", "Variable", "Model", "ModelFile",
        "PartNumber", "X", "Y", "Evidence", "Confidence", "Notes",
    ];

    private static readonly HashSet<string> Measured =
        new(["Connection", "Case", "X", "Y", "Evidence", "Confidence", "PartNumber"], StringComparer.OrdinalIgnoreCase);

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    // ── writing ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The table's CSV text — LF line ends; the caller writes it as UTF-8.</summary>
    public static string Write(PartsTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Columns.Select(c => Header(table, c)))).Append('\n');
        foreach (var row in table.Rows.OrderBy(r => r.Refdes, PartsTable.NaturalOrder))
            sb.Append(string.Join(",", Columns.Select(c => Escape(Cell(table, row, c))))).Append('\n');
        return sb.ToString();
    }

    /// <summary>Writes the table to <paramref name="path"/>, UTF-8 without a byte order mark.</summary>
    public static void WriteFile(string path, PartsTable table) =>
        File.WriteAllText(path, Write(table), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>The unit a picture's coordinate columns state in their header (R-im10-7): <c>X (px)</c>. A layout's
    /// write none, so every artwork table is the bytes it always was.</summary>
    public const string PixelUnit = " (px)";

    /// <summary>A column's header as <paramref name="table"/> writes it.</summary>
    public static string Header(PartsTable table, string column) =>
        table.Pixels && column is "X" or "Y" ? column + PixelUnit : column;

    /// <summary>One cell as the table writes it.</summary>
    public static string Cell(PartsTable table, PartRow row, string column) => column switch
    {
        "Refdes" => row.Refdes,
        "Kind" => PartsTable.KindText(row),
        "Connection" => PartsTable.ConnectionText(row.Connection),
        "Case" => row.Case?.Code ?? "",
        "Value" => row.Value is { } v ? PartsTable.ValueText(v, row.GeneratedKind) : "",
        "Variable" => row.Variable ?? "",
        "Model" => row.Model.ToString(),
        "ModelFile" => row.ModelFile ?? "",
        "PartNumber" => row.PartNumber ?? "",
        "X" => table.Pixels ? row.X.ToString(System.Globalization.CultureInfo.InvariantCulture) : table.Format.Length(row.X),
        "Y" => table.Pixels ? row.Y.ToString(System.Globalization.CultureInfo.InvariantCulture) : table.Format.Length(row.Y),
        "Evidence" => PartEvidenceText.Format(row.Evidence),
        "Confidence" => PartsTable.ConfidenceText(row.Confidence),
        "Notes" => string.Join("; ", row.Notes),
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "not a parts-table column"),
    };

    private static string Escape(string field) =>
        field.IndexOfAny([',', '"', '\n', '\r']) >= 0 || (field.Length > 0 && (char.IsWhiteSpace(field[0]) || char.IsWhiteSpace(field[^1])))
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;

    // ── reading ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads a parts table from disk and lays it over <paramref name="recognised"/>.</summary>
    public static PartsCsvReading ReadFile(string path, PartsTable recognised)
    {
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PartsCsvReading(null, [], [], $"The parts table '{Path.GetFileName(path)}' could not be read: {ex.Message}");
        }
        return Read(text, recognised);
    }

    /// <summary>Lays the table in <paramref name="text"/> over <paramref name="recognised"/>.</summary>
    public static PartsCsvReading Read(string text, PartsTable recognised)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(recognised);
        var parsed = DelimitedTables.Parse(text, ',');
        if (parsed.Rows.Count == 0)
            return new PartsCsvReading(null, [], [], "The parts table is empty: it has no header row.");

        // ── the header: every column known, none twice, Refdes present ────────────────────────────
        var header = parsed.Rows[0].Fields.Select(f => f.Trim()).ToList();
        var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool? pixels = null;
        for (int i = 0; i < header.Count; i++)
        {
            if (header[i].Length == 0 && i == header.Count - 1) continue;   // a trailing separator
            // A coordinate column states its unit only when it is a picture's (R-im10-7).
            string name = header[i];
            if (name.EndsWith(PixelUnit, StringComparison.OrdinalIgnoreCase) && name[..^PixelUnit.Length] is "X" or "Y" or "x" or "y")
            {
                name = name[..^PixelUnit.Length];
                pixels = true;
            }
            else if (name is "X" or "Y" or "x" or "y") pixels ??= false;
            string? known = Columns.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
            if (known is null)
                return new PartsCsvReading(null, [], [],
                    $"The parts table has a column '{header[i]}' that is not one of its columns ({string.Join(", ", Columns)}). " +
                    "Rename or remove it — a misspelt column would otherwise change nothing, silently.");
            if (!at.TryAdd(known, i))
                return new PartsCsvReading(null, [], [], $"The parts table has the column '{known}' twice.");
        }
        if (!at.ContainsKey("Refdes"))
            return new PartsCsvReading(null, [], [], "The parts table has no Refdes column, so no row can be matched to a part.");
        if (pixels is { } px && px != recognised.Pixels)
            return new PartsCsvReading(null, [], [], px
                ? "The parts table's X and Y are a picture's pixels, so it is a schematic picture's table and cannot be laid over artwork."
                : "The parts table's X and Y are lengths on a layout, so it is an artwork's table and cannot be laid over a picture.");

        var notes = new List<string>();
        var notOnBoard = new List<string>();
        var rows = recognised.Rows.ToDictionary(r => r.Refdes, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in parsed.Rows.Skip(1))
        {
            if (line.Populated == 0) continue;
            string? Field(string column) => at.TryGetValue(column, out int i) ? (line.Field(i) ?? "").Trim() : null;

            string refdes = Field("Refdes")!;
            if (refdes.Length == 0) { notes.Add($"Line {line.Line} names no part and was ignored."); continue; }
            Func<string, string?> field = Field;
            if (!rows.TryGetValue(refdes, out var row))
            {
                if (Renamed(recognised, rows.Values, seen, Field("X"), Field("Y")) is not { } was) { notOnBoard.Add(refdes); continue; }
                rows.Remove(was.Refdes);
                seen.Add(was.Refdes);
                row = Rename(was, refdes, notes);
                // The kind and the variable a renamed part takes from its new designator stand unless the row changes
                // them itself: the kind it states as the board had it and the old default variable are no edit.
                string wasKind = Cell(recognised, was, "Kind"), wasVariable = was.DefaultVariable;
                field = c => (c == "Kind" && string.Equals(Field(c), wasKind, StringComparison.OrdinalIgnoreCase))
                             || (c == "Variable" && string.Equals(Field(c), wasVariable, StringComparison.Ordinal)) ? null : Field(c);
            }
            if (!seen.Add(refdes)) { notes.Add($"{refdes} is in the table twice; line {line.Line} was ignored."); continue; }

            // Measured: the board's answer stands; say so where the table's differs.
            foreach (string column in Columns.Where(Measured.Contains))
                if (Field(column) is { } stated && !string.Equals(stated, Cell(recognised, row, column), StringComparison.OrdinalIgnoreCase))
                    notes.Add($"{refdes}: {column} '{stated}' is measured on the board as '{Cell(recognised, row, column)}'; the board's is kept.");

            rows[row.Refdes] = Edit(recognised, row, field, notes);
        }

        var table = recognised with { Rows = [.. rows.Values.OrderBy(r => r.Refdes, PartsTable.NaturalOrder)] };
        return new PartsCsvReading(table, notes, notOnBoard, null);
    }

    /// <summary>The part a row at (<paramref name="x"/>, <paramref name="y"/>) renames: one whose designator was read
    /// off the silkscreen or generated, written at exactly that X and Y, and not already in the table.</summary>
    private static PartRow? Renamed(PartsTable table, IEnumerable<PartRow> rows, HashSet<string> seen, string? x, string? y)
    {
        if (x is not { Length: > 0 } || y is not { Length: > 0 }) return null;
        return rows.FirstOrDefault(r =>
            r.Evidence.TryGetValue(PartField.Refdes, out var src) && src is PartEvidenceSource.Silkscreen or PartEvidenceSource.Picture or PartEvidenceSource.Generated
            && !seen.Contains(r.Refdes)
            && string.Equals(Cell(table, r, "X"), x, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Cell(table, r, "Y"), y, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A part renamed: the user's designator, and the kind its prefix gives where the kind came from the old
    /// designator or was unknown. A default variable follows the new name.</summary>
    private static PartRow Rename(PartRow was, string refdes, List<string> notes)
    {
        var evidence = new Dictionary<PartField, PartEvidenceSource>(was.Evidence) { [PartField.Refdes] = PartEvidenceSource.User };
        var row = was with { Refdes = refdes, RenamedFrom = was.Refdes, Variable = was.Variable == was.DefaultVariable ? null : was.Variable };
        bool kindFromName = was.Kind == PartKind.Unknown
                            || (was.Evidence.TryGetValue(PartField.Kind, out var ks) && ks == PartEvidenceSource.Refdes);
        if (kindFromName && was.PadCount <= 2)
        {
            var said = new List<string>();
            var kind = PartReading.KindFromRefdes(refdes, was.PadCount, said) ?? PartKind.Unknown;
            if (kind != was.Kind)
            {
                bool sameDimension = was.TakesValue && (was with { Kind = kind }).TakesValue && was.GeneratedKind == (was with { Kind = kind }).GeneratedKind;
                row = row with { Kind = kind, Value = sameDimension ? was.Value : null };
                if (!sameDimension) evidence.Remove(PartField.Value);
            }
            if (kind == PartKind.Unknown) evidence.Remove(PartField.Kind);
            else evidence[PartField.Kind] = PartEvidenceSource.Refdes;
            notes.AddRange(said.Select(n => $"{refdes}: {n}."));
        }
        notes.Add($"{was.Refdes} was renamed {refdes}.");
        return row with { Evidence = evidence };
    }

    /// <summary>The editable columns of one row applied.</summary>
    private static PartRow Edit(PartsTable table, PartRow row, Func<string, string?> field, List<string> notes)
    {
        var evidence = new Dictionary<PartField, PartEvidenceSource>(row.Evidence);
        string oldDefault = row.DefaultVariable;
        var edited = row;

        // ── kind ──────────────────────────────────────────────────────────────────────────────────
        if (field("Kind") is { Length: > 0 } kindText)
        {
            if (PartsTable.ParseKindAndVariant(kindText) is not { } parsed)
                notes.Add($"{row.Refdes}: '{kindText}' is not a kind ({string.Join(", ", Enum.GetValues<PartKind>().Select(KindSpelling))}); the kind was kept.");
            else if (parsed.Kind != row.Kind || parsed.Variant != row.Variant)
            {
                var kind = parsed.Kind;
                bool multiPad = row.PadCount > 2;
                // A picture's own kinds (R-imf5-8): a transistor stays a transistor and a coupling a coupling — only the
                // variant changes — and none of them is an artwork part's.
                // An IC, an amplifier and a block-diagram symbol (R-imf8-9) are read by how they are wired: none changes kind.
                static bool Device(PartKind k) => k is PartKind.Q or PartKind.K or PartKind.IC or PartKind.Amp or PartKind.Block;
                bool device = Device(row.Kind), toDevice = Device(kind);
                bool pictureKind = kind is PartKind.D or PartKind.Q or PartKind.V or PartKind.I or PartKind.K or PartKind.Bead
                                   or PartKind.IC or PartKind.Amp or PartKind.Block;
                if (pictureKind && !table.Pixels)
                    notes.Add($"{row.Refdes}: {PartsTable.KindText(kind)} is a schematic picture's kind, not an artwork part's; the kind was kept.");
                else if ((device || toDevice) && kind != row.Kind)
                    notes.Add(row.Kind switch
                    {
                        PartKind.Q or PartKind.K => $"{row.Refdes} is {PartsTable.KindText(row)} and can only change its {(row.Kind == PartKind.Q ? "polarity" : "value")}; the kind was kept.",
                        PartKind.Block => $"{row.Refdes} is {PartsTable.KindText(row)} and can only change which block it is; the kind was kept.",
                        PartKind.IC or PartKind.Amp => $"{row.Refdes} is {PartsTable.KindText(row)}, read by how it is wired; the kind was kept.",
                        _ => kind is PartKind.Q or PartKind.K
                            ? $"{row.Refdes} is {PartsTable.KindText(row)} and can only change its value; the kind was kept."
                            : $"{row.Refdes} cannot become {PartsTable.KindText(kind)}: that kind is read by how the picture draws it; the kind was kept.",
                    });
                else if (multiPad && kind is not (PartKind.MultiPin or PartKind.Connector or PartKind.Ignore or PartKind.Q))
                    notes.Add($"{row.Refdes} has {row.PadCount} pads and cannot be a two-terminal {PartsTable.KindText(kind)}; the kind was kept.");
                else if (!multiPad && kind == PartKind.MultiPin)
                    notes.Add($"{row.Refdes} has {row.PadCount} pads and cannot be a multi-pin part; the kind was kept.");
                else
                {
                    var next = edited with { Kind = kind, Variant = parsed.Variant };
                    bool sameDimension = edited.TakesValue && next.TakesValue && edited.GeneratedKind == next.GeneratedKind;
                    edited = next with { Value = sameDimension ? edited.Value : null };
                    evidence[PartField.Kind] = PartEvidenceSource.User;
                    if (!sameDimension) evidence.Remove(PartField.Value);
                }
            }
        }

        // ── value: a value clears the variable ────────────────────────────────────────────────────
        if (field("Value") is { } valueText)
        {
            if (valueText.Length == 0)
            {
                if (edited.Value is not null) { edited = edited with { Value = null }; evidence.Remove(PartField.Value); }
            }
            else if (!edited.TakesValue)
                notes.Add($"{row.Refdes} is {PartsTable.KindText(edited.Kind)} and takes no value; '{valueText}' was ignored.");
            else if (!TryReadValue(valueText, edited.GeneratedKind, out double si, out string? why))
                notes.Add($"{row.Refdes}: {why}; the value was kept.");
            else if (edited.Value is not { } old || Math.Abs(old - si) > 1e-9 * Math.Max(Math.Abs(old), Math.Abs(si)))
            {
                edited = edited with { Value = si };
                evidence[PartField.Value] = PartEvidenceSource.User;
            }
        }

        // ── model ─────────────────────────────────────────────────────────────────────────────────
        string? modelText = field("Model");
        string? fileText = field("ModelFile");
        PartModelKind? model = modelText is { Length: > 0 }
            ? Enum.TryParse<PartModelKind>(modelText, ignoreCase: true, out var m) && Enum.IsDefined(m) ? m : null
            : fileText is { Length: > 0 } ? PartModelKind.SnP : null;
        if (modelText is { Length: > 0 } && model is null)
            notes.Add($"{row.Refdes}: '{modelText}' is not a model (Ideal or SnP); the model was kept.");
        else if (model == PartModelKind.Ideal && edited.Model != PartModelKind.Ideal)
        {
            edited = edited with { Model = PartModelKind.Ideal, ModelFile = null };
            evidence[PartField.Model] = PartEvidenceSource.User;
        }
        else if (model == PartModelKind.SnP)
        {
            string? file = fileText is { Length: > 0 } ? fileText : edited.ModelFile;
            if (file is null) notes.Add($"{row.Refdes}: an SnP model needs a ModelFile; the model was kept.");
            else if (!string.Equals(file, edited.ModelFile, StringComparison.Ordinal) || edited.Model != PartModelKind.SnP)
            {
                string full = table.ResolveModelFile(edited with { ModelFile = file })!;
                var why = new List<string>();
                if (PartModelResolver.TwoPort(full, why))
                {
                    edited = edited with { Model = PartModelKind.SnP, ModelFile = table.StoredPath(full) };
                    evidence[PartField.Model] = PartEvidenceSource.User;
                }
                else notes.Add($"{row.Refdes}: {string.Join("; ", why)}.");
            }
        }

        // ── the variable: a value or a measured model clears it; else the user's name or the default ──
        bool wantsVariable = edited.IsModelled && edited.TakesValue && edited.Value is null && edited.Model == PartModelKind.Ideal;
        string? variable = wantsVariable ? edited.Variable is { } kept && kept != oldDefault ? kept : edited.DefaultVariable : null;
        if (wantsVariable && field("Variable") is { Length: > 0 } named && named != variable)
        {
            if (Identifier.IsMatch(named)) variable = named;
            else notes.Add($"{row.Refdes}: '{named}' is not a variable name (a letter or underscore, then letters, digits and underscores); it was kept as {variable}.");
        }

        return edited with { Variable = variable, Evidence = evidence };
    }

    /// <summary>
    /// A Value cell as the table reads it, for a part generated as <paramref name="kind"/>: a value with its unit in
    /// the part's own dimension, in base SI. The rule an edited CSV is read with, and the one the dialog's Value cell
    /// borders red on (brief-artsch-8 R-as8-3) — so the two cannot disagree about what "10nH" on a capacitor is.
    /// </summary>
    public static bool TryReadValue(string text, PartKind kind, out double si, out string? why)
    {
        why = null;
        if (kind is PartKind.V or PartKind.I or PartKind.K) return TryReadSourceValue(text, kind, out si, out why);
        var want = PartReading.DimensionOf(kind);
        if (!Schematic.BomTablePaste.TryReadValue(text, PartReading.ToSymbol(kind), description: false, out si, out var dim))
        {
            why = $"'{text}' is not a value with a unit";
            return false;
        }
        if (dim == want) return true;
        why = $"'{text}' is not a {want.ToString().ToLowerInvariant()}";
        return false;
    }

    private static readonly Regex SourceValue = new(@"^([+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*([kmuµn]?)\s*([VA]?)$", RegexOptions.CultureInvariant);

    /// <summary>A source's volts or amps — <c>12 V</c>, <c>-5V</c>, <c>10 mA</c> — or a coupling's plain number.</summary>
    private static bool TryReadSourceValue(string text, PartKind kind, out double si, out string? why)
    {
        si = 0;
        why = null;
        var m = SourceValue.Match(text.Trim());
        string unit = kind switch { PartKind.V => "V", PartKind.I => "A", _ => "" };
        if (!m.Success || (m.Groups[3].Value.Length > 0 && m.Groups[3].Value != unit) || (kind == PartKind.K && m.Groups[2].Value.Length > 0)
            || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n))
        {
            why = kind == PartKind.K ? $"'{text}' is not a coupling coefficient" : $"'{text}' is not a {(kind == PartKind.V ? "voltage" : "current")}";
            return false;
        }
        si = n * m.Groups[2].Value switch { "k" => 1e3, "m" => 1e-3, "u" or "µ" => 1e-6, "n" => 1e-9, _ => 1 };
        return true;
    }

    /// <summary>A kind as the refusal lists it: with its variants where it has them.</summary>
    private static string KindSpelling(PartKind k) =>
        PartsTable.Variants(k) is { Count: > 0 } v ? string.Join(", ", v.Select(x => $"{PartsTable.KindText(k)} {x}")) : PartsTable.KindText(k);
}
