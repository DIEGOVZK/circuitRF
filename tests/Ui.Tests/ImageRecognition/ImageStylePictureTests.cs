// brief-imf-1-corpus-and-scorer.md R-imf1-5 — the committed style pictures (Fixtures/style-pictures, drawn by
// DatasheetStylePictures and WebStylePictures), one fact per picture: each reads its circuit exactly — kinds, nets,
// ports, grounds, its IC or amplifier as one part, every designator (overview §4). Each is skipped with the phase that
// owns what it still lacks, and that phase turns it on: the skip reasons are the series' to-do list. The illustration
// is no circuit, and until IMF-9 it is only read without throwing.

using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

internal static class StyleScore
{
    public static FieldScore Read(ITestOutputHelper output, string picture)
    {
        var score = FieldScore.Read(Path.Combine(FixturePaths.Require(StylePictures.Folder), picture), picture);
        output.WriteLine(FieldScore.Header);
        output.WriteLine(score.Line());
        return score;
    }

    public static void ReadsExactly(ITestOutputHelper output, string picture)
    {
        var score = Read(output, picture);
        Assert.True(score.Exact, score.Line());
    }
}

public sealed class ImageDatasheetStyleTests(ITestOutputHelper output)
{
    private const string RfAmp = "IMF-8 reads the 8-pin IC box (pin-number boxes, names inside) and the filled-dot RF connectors; " +
                                 "it needs IMF-2's hairline scale and fill, IMF-3's hairline plates, IMF-4's region stopping at the box " +
                                 "and IMF-7's annotation phrases first";
    private const string GainBlock = "IMF-8 reads the filled triangle as an Amp and the coax connectors as ports; it needs IMF-2's fill, " +
                                     "IMF-4's region, IMF-6's terminals and IMF-7's word designators (Cblock, Rbias, RFC) first";
    private const string Regulator = "IMF-8 reads the IC box with its pin names outside; it needs IMF-3's curved plates and dimension arrow, " +
                                     "IMF-5's filled diodes and variable resistor and IMF-7's bold labels first";
    private const string Ldo = "IMF-8 reads the IC box with its pin names inside; it needs IMF-4's region stopping at the box and " +
                               "IMF-6's named open-circle terminals first";
    private const string TestCircuit = "IMF-8 reads the triangle as an Amp and the coax connectors as ports; it needs IMF-3's dashed " +
                                       "fixture box and IMF-4's solid block box first";

    [Fact(Skip = RfAmp)] public void RfAmp_1x() => StyleScore.ReadsExactly(output, "ds-rf-amp-1x");
    [Fact(Skip = RfAmp)] public void RfAmp_2x() => StyleScore.ReadsExactly(output, "ds-rf-amp-2x");
    [Fact(Skip = GainBlock)] public void GainBlock_1x() => StyleScore.ReadsExactly(output, "ds-gain-block-1x");
    [Fact(Skip = GainBlock)] public void GainBlock_2x() => StyleScore.ReadsExactly(output, "ds-gain-block-2x");
    [Fact(Skip = Regulator)] public void Regulator_1x() => StyleScore.ReadsExactly(output, "ds-regulator-1x");
    [Fact(Skip = Regulator)] public void Regulator_2x() => StyleScore.ReadsExactly(output, "ds-regulator-2x");
    [Fact(Skip = Ldo)] public void Ldo_1x() => StyleScore.ReadsExactly(output, "ds-ldo-1x");
    [Fact(Skip = Ldo)] public void Ldo_2x() => StyleScore.ReadsExactly(output, "ds-ldo-2x");
    [Fact(Skip = TestCircuit)] public void TestCircuit_1x() => StyleScore.ReadsExactly(output, "ds-test-circuit-1x");
    [Fact(Skip = TestCircuit)] public void TestCircuit_2x() => StyleScore.ReadsExactly(output, "ds-test-circuit-2x");
}

public sealed class ImageWebStyleTests(ITestOutputHelper output)
{
    private const string HeavyIec = "IMF-5 reads the transistor in its circle and the two source circles; it needs IMF-2's heavy-pen " +
                                    "scale, IMF-3's thick plates and box sides, IMF-4's region, IMF-6's terminal dot and IMF-7's subscripts first";
    private const string Textbook = "IMF-6 makes the terminal pairs ports and the bottom rail ground; it needs IMF-3's zig-zag " +
                                    "(no curved-lines refusal) and voltage arrows and IMF-7's serif italic first";
    private const string Ladder = "IMF-6 makes the terminal pairs ports and the bottom rail ground; it needs IMF-3's dashed " +
                                  "continuation and thick plates and IMF-4's labels touching their symbols first";
    private const string Typeset = "IMF-7 reads the serif italic designator-then-value runs; it needs IMF-5's ± source circle first";
    private const string BiasTee = "IMF-6 makes the names at a distance ports; it needs IMF-5's looped coil first";
    private const string LowRes = "IMF-6 makes the terminal pairs ports; it needs IMF-2's resampling of a 1-2 px pen and 7 px caps " +
                                  "and IMF-3's diamond junctions first";

    [Fact(Skip = HeavyIec)] public void HeavyIecStage() => StyleScore.ReadsExactly(output, "heavy-iec");
    [Fact(Skip = Textbook)] public void TextbookZigzag() => StyleScore.ReadsExactly(output, "textbook-zigzag");
    [Fact(Skip = Ladder)] public void LadderDashed() => StyleScore.ReadsExactly(output, "ladder-dashed");
    [Fact(Skip = Typeset)] public void TypesetFigure() => StyleScore.ReadsExactly(output, "typeset");
    [Fact(Skip = BiasTee)] public void LoopCoilNames() => StyleScore.ReadsExactly(output, "loop-coil-names");
    [Fact(Skip = LowRes)] public void LowResolution() => StyleScore.ReadsExactly(output, "low-res");

    /// <summary>IMF-9 refuses it in one sentence saying it is not a schematic; until then it is read and scored.</summary>
    [Fact]
    public void Illustration_ReadsWithoutThrowing() => StyleScore.Read(output, "illustration");
}
