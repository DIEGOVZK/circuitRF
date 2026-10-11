// brief-imf-2-picture-scale-profile.md §4 — the picture's own scales (R-imf2-1), the reference profile the defaults keep
// (R-imf2-2), the enlarged reading of a Low picture (R-imf2-3) and a stated scale (R-imf2-5).
//
// The expected scales are the drawings' own, from the code that drew them (Fixtures/*StylePictures.cs): the pen; the cap
// height the type was set at; and s, the median drawn length of the plates and bars long enough to be strokes at all.

using System.IO;
using System.Linq;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Schematic.Recognition;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

public sealed class PictureProfileTests(ITestOutputHelper output)
{
    private static ImageSource Style(string name) =>
        ImageSource.FromFile(Path.Combine(FixturePaths.Require(StylePictures.Folder), name, "picture.png")).Source!;

    private SchematicImageRead Read(ImageSource source, SchematicImageOptions? options = null)
    {
        var read = SchematicImageReading.Read(source, options);
        foreach (var line in read.Profile!.Lines()) output.WriteLine(line);
        return read;
    }

    /// <summary>ds-rf-amp: a 1.1 px hairline, 8 px caps, plates 20 and 18 long and ground bars 18, 12.6 and 7.2 (median
    /// 18), drawn at 1× and 2×. heavy-iec: an 8 px pen, 28 px caps, plates 152 long and ground bars 64 and 44.8 (the
    /// 25.6 px bottom bars are shorter than a wire's shortest run; median 64). low-res: a 4 px pen, 16 px serif italic
    /// capitals and 90 px plates, all at 0.45 ×.</summary>
    [Theory]
    [InlineData("ds-rf-amp-1x", 1.1, 8.0, 18.0)]
    [InlineData("ds-rf-amp-2x", 2.2, 16.0, 36.0)]
    [InlineData("heavy-iec", 8.0, 28.0, 64.0)]
    [InlineData("low-res", 1.8, 7.2, 40.5)]
    public void ThePen_TheText_AndTheSymbolScale_AreMeasuredWithin15Percent(string picture, double pen, double text, double symbol)
    {
        var p = Read(Style(picture)).Profile!;
        Assert.Equal(ProfileBasis.Measured, p.Pen.Basis);
        Assert.Equal(ProfileBasis.Measured, p.Text.Basis);
        Assert.Equal(ProfileBasis.Measured, p.Symbol.Basis);
        Assert.InRange(p.Pen.Px, 0.85 * pen, 1.15 * pen);
        Assert.InRange(p.Text.Px, 0.85 * text, 1.15 * text);
        Assert.InRange(p.Symbol.Px, 0.85 * symbol, 1.15 * symbol);
    }

    /// <summary>A datasheet hairline at 1× and the 360 px textbook picture are Low: read enlarged, and everything they
    /// return is mapped back to the file's pixels.</summary>
    [Theory]
    [InlineData("ds-rf-amp-1x")]
    [InlineData("low-res")]
    public void ALowPicture_IsReadEnlarged_AndMappedBack(string picture)
    {
        var source = Style(picture);
        var read = Read(source);
        Assert.Equal(PictureResolution.Low, read.Profile!.Resolution);
        Assert.True(read.Scale > 1, $"read at ×{read.Scale}");
        Assert.Equal(read.Scale, read.Profile.Resample);
        Assert.Equal(source.Raster.Width * read.Scale, read.Width);
        Assert.Contains(read.Report.Lines(), l => l.StartsWith("Low resolution: read enlarged", System.StringComparison.Ordinal));
        var (x, y) = read.ToSource(read.Width, read.Height);
        Assert.Equal(source.Raster.Width, x, 6);
        Assert.Equal(source.Raster.Height, y, 6);
        Assert.All(read.Wires.Nodes, n =>
        {
            var (sx, sy) = read.ToSource(n.X, n.Y);
            Assert.InRange(sx, 0, source.Raster.Width);
            Assert.InRange(sy, 0, source.Raster.Height);
        });
    }

    /// <summary>circuitRF's own editor screenshot — the picture the defaults were measured on — profiles inside the
    /// reference band: every tolerance keeps its stated number.</summary>
    [Fact]
    public void TheEditorScreenshot_ProfilesToTheReference()
    {
        var read = Read(ImageSource.FromFile(Path.Combine(FixturePaths.Require(ForeignPictures.Folder), "term-s2p-screenshot.png")).Source!);
        var p = read.Profile!;
        Assert.Equal(PictureResolution.Normal, p.Resolution);
        Assert.Equal(1, read.Scale);
        Assert.Equal(1.0, p.TextFactor);
        Assert.Equal(1.0, p.SymbolFactor);
        Assert.InRange(p.Symbol.Px / read.StrokeWidth, PictureProfile.ReferenceSymbolLow, PictureProfile.ReferenceSymbolHigh);
        var o = new SchematicImageOptions();
        Assert.Same(o, p.Apply(o));
    }

    /// <summary>A stated scale replaces only itself (R-imf2-5).</summary>
    [Fact]
    public void AStatedScale_ReplacesOnlyItself()
    {
        var measured = Read(Style("heavy-iec")).Profile!;
        var stated = Read(Style("heavy-iec"), new SchematicImageOptions { Profile = new PictureProfileOptions { SymbolScale = 100 } }).Profile!;
        Assert.Equal(ProfileScale.Stated(100), stated.Symbol);
        Assert.Equal(measured.Pen, stated.Pen);
        Assert.Equal(measured.Text, stated.Text);
    }
}
