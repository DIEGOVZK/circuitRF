// brief-imf-9-auto-settings-and-paste.md R-imf9-1 … R-imf9-3 — a reading's plausibility, the candidates kept by it, and
// the floor under which a picture is no schematic. Read as Paste reads: the kind classified, never forced.

using System.IO;
using System.Linq;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Schematic.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

public sealed class ReadingPlausibilityTests
{
    internal static ImageSource Picture(string name) =>
        ImageSource.FromFile(Path.Combine(FixturePaths.Require(StylePictures.Folder), name, "picture.png")).Source!;

    internal static SchematicImageResult Read(string name) => SchematicImageRecognition.Circuit(new SchematicImageInput { Source = Picture(name) });

    /// <summary>The least plausible committed schematic (ds-gain-block-2x, 0.72), the one whose words carry least
    /// (loop-coil-names), and an exact one.</summary>
    [Theory]
    [InlineData("ds-ldo-2x")]
    [InlineData("ds-gain-block-2x")]
    [InlineData("loop-coil-names")]
    public void ACommittedSchematic_ScoresAboveTheFloor(string picture)
    {
        var r = Read(picture);
        Assert.True(r.Ok, r.Refusal);
        Assert.True(r.Plausibility!.Score > ReadingPlausibility.Floor, r.Plausibility.ToString());
        Assert.Equal(r.ReadingName, r.Candidates[0].Name);   // the kept reading is listed first
    }

    /// <summary>A runaway region — the pre-IMF-4 reader's failure: the parts of the drawing merged into one region of no
    /// known kind with twenty leads — is a part no more, and its leads are more than a part has. Built from the real
    /// reading rather than recorded: the plausibility reads only what the result holds.</summary>
    [Fact]
    public void ARunawayRegion_ScoresBelowTheReadingItReplaces()
    {
        var r = Read("ds-ldo-2x");
        var symbols = r.Symbols!.Symbols;
        var merged = symbols.Where(s => s.Kind is not (ImageSymbolKind.Ground or ImageSymbolKind.Port)).ToList();
        Assert.True(merged.Count >= 3);
        var leads = merged.SelectMany(s => s.Region.Attachments).ToList();
        for (int i = 0; leads.Count < 20; i++) leads.Add(leads[i]);
        var box = merged.Select(s => s.Region.Box).Aggregate((a, b) => a.Union(b));
        var region = new SymbolRegion(0, box, merged.Sum(s => s.Region.Area), [.. leads.Take(20)], new BinaryImage(box.Width, box.Height));
        var runaway = new RecognisedSymbol(region, ImageSymbolKind.Unknown, SymbolDisposition.CutOut, null, default,
                                           [.. merged.SelectMany(s => s.Pins).Select((p, i) => p with { Index = i })],
                                           double.PositiveInfinity, null, PartConfidence.Low, []);
        var bad = r with { Symbols = new SchematicSymbolsRead([runaway, .. symbols.Except(merged)], r.Symbols.Report) };

        var p = ReadingPlausibility.Of(bad);
        Assert.True(p.Score < r.Plausibility!.Score, $"{p} against {r.Plausibility}");   // a candidate reading it would lose
        Assert.True(p.PinsHonest < 1 && p.PartsNamed < 1, p.ToString());
    }
}
