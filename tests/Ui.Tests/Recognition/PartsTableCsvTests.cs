// brief-artsch-4-parts-and-parts-table.md §4 — the parts table's CSV: write then read is the identity;
// an edited Value clears the Variable; an edited measured column is ignored with a note; an unknown
// column is the refusal; a designator the board does not have is reported.

using System;
using System.Linq;
using System.Text;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class PartsTableCsvTests
{
    private static PartsTable Recognised()
    {
        var (view, placement, bom) = PartsBoard();
        return RecognizeParts(view, placement, bom).Parts;
    }

    /// <summary>The table's CSV with one cell changed, every field quoted, and any extra lines and
    /// columns appended.</summary>
    private static string Csv(PartsTable table, Func<string, string, string?>? edit = null,
                              string extraColumn = "", string extraLine = "")
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder(string.Join(",", PartsTableCsv.Columns.Select(Q)) + (extraColumn.Length > 0 ? "," + Q(extraColumn) : "") + "\n");
        foreach (var row in table.Rows)
            sb.Append(string.Join(",", PartsTableCsv.Columns.Select(c => Q(edit?.Invoke(row.Refdes, c) ?? PartsTableCsv.Cell(table, row, c)))))
              .Append(extraColumn.Length > 0 ? ",\"\"" : "").Append('\n');
        return sb.Append(extraLine).ToString();
    }

    [Fact]
    public void WriteThenReadIsTheIdentity()
    {
        var table = Recognised();
        string text = PartsTableCsv.Write(table);

        var read = PartsTableCsv.Read(text, table);

        Assert.Null(read.Refusal);
        Assert.Empty(read.Notes);
        Assert.Equal(text, PartsTableCsv.Write(read.Table!));
    }

    /// <summary>brief-img-10 R-im10-7: a picture's table states its pixels on the coordinate columns and round-trips; it
    /// is refused over artwork and an artwork's over a picture; an artwork table writes the header it always did.</summary>
    [Fact]
    public void APixelTableRoundTrips_IsRefusedOverArtwork_AndAnArtworkTableIsUnchanged()
    {
        var artwork = Recognised();
        var picture = artwork with { Pixels = true };
        string pixels = PartsTableCsv.Write(picture), lengths = PartsTableCsv.Write(artwork);

        Assert.StartsWith(string.Join(",", PartsTableCsv.Columns) + "\n", lengths);
        Assert.Contains(",X (px),Y (px),", pixels.Split('\n')[0]);
        Assert.Equal(pixels, PartsTableCsv.Write(PartsTableCsv.Read(pixels, picture).Table!));
        Assert.NotNull(PartsTableCsv.Read(pixels, artwork).Refusal);
        Assert.NotNull(PartsTableCsv.Read(lengths, picture).Refusal);
    }

    /// <summary>AS-10 R-as10-5: a row at a generated part's X and Y renames it, and the kind and the variable follow the
    /// new designator; a designator the board itself states (here the placement file's) is never renamed.</summary>
    [Fact]
    public void ARowAtAGuessedPartsPlaceRenamesItButNeverABoardDesignator()
    {
        var (view, placement, bom) = PartsBoard();
        var guessed = RecognizeParts(view, null, null).Parts;
        var series = guessed.Rows.Single(r => r.Connection == PartConnection.Series);

        var read = PartsTableCsv.Read(Csv(guessed, (r, c) => r == series.Refdes && c == "Refdes" ? "L4" : null), guessed);

        var l4 = read.Table!.Row("L4")!;
        Assert.Equal((PartKind.L, series.Refdes, PartEvidenceSource.User, "L4_L"),
                     (l4.Kind, l4.RenamedFrom, l4.Evidence[PartField.Refdes], l4.Variable));
        Assert.Null(read.Table.Row(series.Refdes));

        var named = Recognised();
        var r1 = PartsTableCsv.Read(Csv(named, (r, c) => r == "R1" && c == "Refdes" ? "R9" : null), named);
        Assert.Equal(["R9"], r1.NotOnBoard);
        Assert.NotNull(r1.Table!.Row("R1"));
    }

    [Fact]
    public void AnEditedValueClearsTheVariable()
    {
        var table = Recognised();
        Assert.Equal("C2_C", table.Row("C2")!.Variable);

        var read = PartsTableCsv.Read(Csv(table, (r, c) => r == "C2" && c == "Value" ? "2.2 pF" : null), table);

        var c2 = read.Table!.Row("C2")!;
        Assert.Equal(2.2, c2.Value!.Value * 1e12, 9);
        Assert.Null(c2.Variable);
        Assert.Equal(PartEvidenceSource.User, c2.Evidence[PartField.Value]);
    }

    [Fact]
    public void AnEditedConnectionIsIgnoredWithANote()
    {
        var table = Recognised();

        var read = PartsTableCsv.Read(Csv(table, (r, c) => r == "R1" && c == "Connection" ? "shunt" : null), table);

        Assert.Equal(PartConnection.Series, read.Table!.Row("R1")!.Connection);
        Assert.Contains(read.Notes, n => n.StartsWith("R1: Connection 'shunt'", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownColumnIsTheRefusal()
    {
        var read = PartsTableCsv.Read(Csv(Recognised(), extraColumn: "Vaule"), Recognised());

        Assert.Null(read.Table);
        Assert.Contains("'Vaule'", read.Refusal);
    }

    [Fact]
    public void ADesignatorNotOnTheBoardIsReportedAndIgnored()
    {
        var table = Recognised();

        var read = PartsTableCsv.Read(Csv(table, extraLine: "C99,C,series,,1 pF\n"), table);

        Assert.Equal(["C99"], read.NotOnBoard);
        Assert.Null(read.Table!.Row("C99"));
        Assert.Equal(table.Rows.Count, read.Table.Rows.Count);
    }

    /// <summary>brief-imf-5 R-imf5-8: a picture's own kinds — a transistor with its polarity, a source AC or DC with its
    /// volts, a current source, a coupling with its k, a bead, a diode with its part number — round-trip; a transistor's
    /// polarity is an edit, its kind is not, and none of them is an artwork part's.</summary>
    [Fact]
    public void APicturesDeviceKindsRoundTrip_AndATransistorChangesOnlyItsPolarity()
    {
        PartRow Row(string refdes, PartKind kind, int pads, PartConnection connection, string? variant = null, double? value = null) => new()
        {
            Refdes = refdes, Kind = kind, Variant = variant, Connection = connection, PadCount = pads, Value = value,
            Variable = value is null && kind is PartKind.V or PartKind.I or PartKind.K && variant != "AC" ? $"{refdes}_{kind}" : null,
        };
        var picture = new PartsTable(
        [
            Row("D1", PartKind.D, 2, PartConnection.Series) with { PartNumber = "1N4002" },
            Row("FB1", PartKind.Bead, 2, PartConnection.Series),
            Row("I1", PartKind.I, 2, PartConnection.Shunt),
            Row("Q1", PartKind.Q, 3, PartConnection.MultiPin, "NPN"),
            Row("T1", PartKind.K, 0, PartConnection.Series),
            Row("V1", PartKind.V, 2, PartConnection.Shunt, "AC"),
            Row("V2", PartKind.V, 2, PartConnection.Shunt, "DC", 12),
        ], CircuitRF.Design.RailRf.RailLengthFormat.Dbu, null) { Pixels = true };
        string text = PartsTableCsv.Write(picture);
        Assert.Contains("Q1,Q NPN,", text);
        Assert.Contains("V2,V DC,shunt,,12 V,", text);

        var read = PartsTableCsv.Read(text, picture);
        Assert.Empty(read.Notes);
        Assert.Equal(text, PartsTableCsv.Write(read.Table!));

        var edited = PartsTableCsv.Read(text.Replace("Q1,Q NPN,", "Q1,Q PNP,").Replace("T1,K,series,,,", "T1,K,series,,0.95,")
                                            .Replace("V2,V DC,shunt,,12 V,", "V2,V DC,shunt,,5V,"), picture).Table!;
        Assert.Equal((PartKind.Q, "PNP"), (edited.Row("Q1")!.Kind, edited.Row("Q1")!.Variant));
        Assert.Equal(0.95, edited.Row("T1")!.Value);
        Assert.Equal(5, edited.Row("V2")!.Value);
        var kept = PartsTableCsv.Read(text.Replace("Q1,Q NPN,", "Q1,R,"), picture);
        Assert.Equal(PartKind.Q, kept.Table!.Row("Q1")!.Kind);
        Assert.Contains(kept.Notes, n => n.StartsWith("Q1 is Q NPN", StringComparison.Ordinal));

        var artwork = Recognised();
        var refused = PartsTableCsv.Read(Csv(artwork, (r, c) => r == "R1" && c == "Kind" ? "Bead" : null), artwork);
        Assert.Equal(PartKind.R, refused.Table!.Row("R1")!.Kind);
    }
}
