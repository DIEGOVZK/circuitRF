// brief-img-14-sparameter-plots-to-touchstone.md §4 — `circuitrf recognize` on plot pictures, run as a PROCESS: several
// pictures make one Touchstone, byte for byte the in-process assembly's but for the time line TouchstoneIO writes (as
// `em`'s gate allows); a Smith trace with no frequency exits 1 naming --freq; and src/Cli names nothing of the plot
// reader but its entry points, their options and their results (R-im14-15, AS-7's source-scan rule).

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Imaging.Plots;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PlotRecognition;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class PlotCliVerbTests(PlotPictureSet pictures, ITestOutputHelper output) : IClassFixture<PlotPictureSet>
{
    private (int Exit, string StdOut, string StdErr) Cli(params string[] args)
    {
        var result = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], args);
        output.WriteLine($"$ {string.Join(' ', args)} -> {result.ExitCode}\n{result.StdOut}\n{result.StdErr}");
        return result;
    }

    private static string Undated(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.StartsWith("! Created", StringComparison.Ordinal)));

    [Fact]
    public void TwoPictures_WriteTheInProcessAssembly_ByteForByte()
    {
        string s21 = pictures.Picture("s21db"), s11 = pictures.Picture("s11db");
        string target = Path.Combine(pictures.Dir, "cli", "part.s2p");
        var (exit, _, stderr) = Cli("recognize", s21, s11, "-o", target);
        Assert.True(exit == 0, stderr);

        var built = TouchstoneAssembly.Build(PlotPictureSet.Read(null, s21, s11));
        Assert.Equal(TouchstoneAssembly.Text(built, dated: false), Undated(File.ReadAllText(target)));
        // The sources are kept beside it (D4).
        Assert.True(File.Exists(Path.Combine(pictures.Dir, "cli", "part.source.png")));
    }

    [Fact]
    public void ASmithTraceWithoutFrequency_Exits1_NamingFreq()
    {
        string target = Path.Combine(pictures.Dir, "cli", "nofreq.s1p");
        var (exit, _, stderr) = Cli("recognize", FixturePaths.Require(Path.Combine(ForeignPlots.Folder, "smith-z.png")), "-o", target);
        Assert.Equal(1, exit);
        Assert.Contains("--freq T1=", stderr);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void SrcCli_NamesNoPlotReadingStep()
    {
        string repo = PalaceBackendTests.RepoRoot();
        // The entry points, their options and results, and the wire grammar of an assignment (PlotQuantity).
        string[] allowed =
        [
            "PlotReading", "PlotReadingOptions", "PlotPictureInput", "PlotAxisOverride", "PlotTraceOverride", "PlotFrequencyOverride",
            "PlotSession", "PlotPictureRead", "PlotRead", "PlotTraceRead", "PlotSample", "PlotFinding", "PlotMarker", "TraceFrequency",
            "TouchstoneAssembly", "TouchstoneAssemblyOptions", "TouchstoneAssemblyResult", "AssemblyRefusal", "AssemblyGrid",
            "MatrixCell", "PhaseRule", "MissingPhase", "FillRule", "PlotCsv", "PlotKind", "PlotType", "PlotQuantity", "PlotQuantityKind",
            "PlotAssignment", "AxisClicks", "AxisScale", "AxisCalibration", "AxisLabel", "RectCalibration", "SmithCalibration",
            "PolarCalibration", "LegendEntry", "TraceCurve", "TraceStyle", "TraceHints", "PlotFrame",
        ];
        var declared = Directory.GetFiles(Path.Combine(repo, "src", "Design", "Imaging", "Plots"), "*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f),
                @"^(?:public|internal)\s+(?:(?:static|sealed|readonly|abstract|partial)\s+)*(?:record\s+struct|record|class|enum|struct|interface)\s+(\w+)",
                RegexOptions.Multiline).Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.Contains("PlotTraces", declared);   // not vacuous
        Assert.Contains("MinimumPhase", declared);
        var forbidden = declared.Except(allowed).ToList();

        var hits = Directory.GetFiles(Path.Combine(repo, "src", "Cli"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f =>
            {
                string code = Regex.Replace(Regex.Replace(Regex.Replace(File.ReadAllText(f), @"/\*.*?\*/", "", RegexOptions.Singleline),
                                                          @"//[^\n]*", ""), "\"(?:[^\"\\\\\\n]|\\\\.)*\"", "\"\"");
                // A member access (MissingPhase.MinimumPhase) is not a type named; a qualified type (Plots.Foo) is.
                return forbidden.Where(t => Regex.IsMatch(code, $@"(?<![\w.]){t}\b|Plots\.{t}\b")).Select(t => $"{Path.GetFileName(f)}: {t}");
            }).ToList();
        Assert.Empty(hits);
    }
}
