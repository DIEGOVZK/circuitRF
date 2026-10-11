// brief-img-14-sparameter-plots-to-touchstone.md §4 — assembling the file (R-im14-9): the default fills for the entries
// nobody plotted, each recorded in the header, and the refusal of a trace no evidence names.

using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Imaging.Plots;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.PlotRecognition;

[Collection(SkiaFontsTypefaceCollection.Name)]
public sealed class TouchstoneAssemblyTests(PlotPictureSet pictures, ITestOutputHelper output) : IClassFixture<PlotPictureSet>
{
    private TouchstoneAssemblyResult Build(params string[] pngs)
    {
        var s = PlotPictureSet.Read(null, pngs);
        foreach (var l in s.Lines()) output.WriteLine(l);
        var built = TouchstoneAssembly.Build(s);
        foreach (var l in built.Header) output.WriteLine("! " + l);
        return built;
    }

    [Fact]
    public void S11AndAPassiveS21_FillS12AsS21_AndS22AsS11_InTheHeader()
    {
        var built = Build(pictures.Picture("s21db"), pictures.Picture("s11db"));
        Assert.True(built.Ok, built.Refusal?.Sentence);
        Assert.Contains(built.Header, l => l.StartsWith("S12: = S21 (reciprocal"));
        Assert.Contains(built.Header, l => l.StartsWith("S22: = S11 (symmetric"));
        for (int k = 0; k < built.Snp!.FrequencyCount; k++)
        {
            Assert.Equal(built.Snp[k][1, 0], built.Snp[k][0, 1]);
            Assert.Equal(built.Snp[k][0, 0], built.Snp[k][1, 1]);
        }
    }

    [Fact]
    public void AnS21AboveZeroDb_FillsS12WithZero()
    {
        var built = Build(pictures.Picture("amp21"));
        Assert.True(built.Ok, built.Refusal?.Sentence);
        Assert.Contains(built.Header, l => l.StartsWith("S12: zero") && l.Contains("rises above 0 dB"));
        Assert.All(Enumerable.Range(0, built.Snp!.FrequencyCount), k => Assert.Equal(Complex.Zero, built.Snp[k][0, 1]));
    }

    [Fact]
    public void AnUnassignedTrace_Refuses_NamingIt()
    {
        var built = Build(FixturePaths.Require(Path.Combine(ForeignPlots.Folder, "plot-two-black.png")));
        Assert.False(built.Ok);
        Assert.Equal("unassigned", built.Refusal!.Kind);
        Assert.StartsWith("T1 (plot 1, #", built.Refusal.Sentence);
        Assert.Equal("--trace \"T1=S21:db\"", built.Refusal.Flag);
    }
}
