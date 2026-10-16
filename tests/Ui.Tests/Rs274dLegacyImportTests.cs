// Gate for GerberLegacyCompanions: an RS-274D output set — artwork with no %FS/%MO/%AD, a drill file
// that selects tools it never sizes, and the printed aperture and drill size reports beside them.
// Read as RS-274X, every layer imported with zero shapes and the run still reported success.
//
// Fixtures are hand-authored and name no tool, product or toolchain, as GI4's are.

using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

public class Rs274dLegacyImportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rs274d-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const int Dbu = 1000;

    /// <summary>Three 40 mil pads and a line, in mils, plotted at a film offset of (5000, 4000).</summary>
    private const string Artwork =
        "D10*\nX5100Y4100D03*\nX5300Y4100D03*\nX5200Y4300D03*\nD11*\nX5000Y4000D02*\nX5400Y4000D01*\nM02*\n";

    private const string ApertureReport =
        "Photo-Plotter Apertures Report\n" +
        "Position  Width  Hgt/ID  Shape    Qty\n" +
        "   10        40       0    RND      3\n" +
        "   11        10       0    LINE\n";

    /// <summary>The same three holes at BOARD coordinates, leading zeros kept, tools never sized.</summary>
    private const string Drill = "%\nT1F2S5\nX0010Y0010\nX0030Y0010\nX0020Y0030\nM30\n";

    private const string DrillSizeReport =
        "Drill Sizes Report\n" +
        "Tool   Size   Pltd   Feed   Speed   Qty\n" +
        "   1     20    x      200    500      3\n" +
        "   2     37    x      200    500      0\n";

    private GerberImport.ImportResult Import(params (string Name, string Text)[] files)
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        foreach (var (name, text) in files) File.WriteAllText(Path.Combine(dir, name), text);
        return GerberImport.Import(
            [.. Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal)], _root, "board", null, Dbu);
    }

    [Fact]
    public void TheReportsSupplyApertures_AndTheHolesAreShiftedOntoTheirPads()
    {
        var r = Import(("TOP.PHO", Artwork), ("TOP.REP", ApertureReport),
                       ("DRL.DRL", Drill), ("DRL.REP", DrillSizeReport));

        Assert.NotNull(r.CellDir);
        Assert.Contains(r.Messages, m => m.Contains("TOP.PHO: RS-274D artwork — 2 aperture(s) taken from TOP.REP"));
        Assert.Contains(r.Messages, m => m.Contains("so its size was INFERRED"));
        // 5000 mil, 4000 mil — the film offset, exactly, so every hole pairs with its pad.
        Assert.Contains(r.Messages, m => m.Contains("shifted by (127 mm, 101.6 mm), which puts 3 of 3 on a pad"));
        Assert.Contains(r.Messages, m => m.Contains("3 hit(s) → 3 via(s)"));
        // The drill format came from a header line the import wrote, never from the file.
        Assert.Contains(r.Messages, m => m.Contains("DRL.DRL: Units: inch — INFERRED"));
        Assert.DoesNotContain(r.Messages, m => m.StartsWith("DRL.DRL") && m.Contains("declared by the file"));
    }

    [Fact]
    public void WithoutAnApertureReport_TheFileIsRefused_NotImportedEmpty()
    {
        var r = Import(("TOP.PHO", Artwork));

        Assert.Null(r.CellDir);
        Assert.Contains(r.Messages, m => m.Contains("TOP.PHO: this is RS-274D artwork") &&
                                         m.Contains("no aperture report"));
    }
}
