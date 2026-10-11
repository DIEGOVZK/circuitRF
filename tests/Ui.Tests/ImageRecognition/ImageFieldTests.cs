// brief-img-12-acceptance-docs-example.md R-im12-4 — the field pictures as a theory, one case per picture present under
// the git-ignored testdata/image-sources/ (testdata/image-sources/README.md holds the schema). On a fresh clone the theory
// skips with the reason.
//
// brief-imf-1-corpus-and-scorer.md R-imf1-5: a schematic picture is SCORED, not held — FieldScore's line is printed,
// so the TRX carries the table, and the picture fails only on what IMF-10 promises (overview §4); until then, only on
// an exception. A `refused` that does not match is marked `!` in the line: the illustration is read today, Paste
// forcing the schematic kind, and is refused only once IMF-9 can say it is not a schematic. A layout picture keeps
// IM-12's counts. The committed style pictures' table is printed beside it.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition.Image;
using CircuitRF.Design.Schematic.Recognition;
using CircuitRF.Ui.Tests.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

public sealed class ImageFieldTests(ITestOutputHelper output)
{
    private const string Folder = "testdata/image-sources";
    private const string Gate = Folder + "/*/expected.json";
    private const string Reason = "the field pictures are other people's and are kept outside the repository (testdata/image-sources/README.md)";

    public static TheoryData<string> Pictures()
    {
        var data = new TheoryData<string>();
        if (FixturePaths.Find(Folder) is { } root)
            foreach (string dir in Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "expected.json"))).Order(StringComparer.Ordinal))
                data.Add(Path.GetFileName(dir));
        return data;
    }

    [FixtureTheory(Gate, Reason)]
    [MemberData(nameof(Pictures))]
    public void TheFieldPicture_IsScored(string picture)
    {
        string dir = Path.Combine(FixturePaths.Require(Folder), picture);
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")));
        var root = expected.RootElement;

        if (root.GetProperty("kind").GetString() != "layout")
        {
            var score = FieldScore.Read(dir, picture);
            output.WriteLine(FieldScore.Header);
            output.WriteLine(score.Line());
            return;
        }

        var source = ImageSource.FromFile(Path.Combine(dir, root.GetProperty("picture").GetString()!));
        Assert.True(source.Ok, $"{picture}: {source.Refusal}");
        Assert.True(root.TryGetProperty("umPerPx", out var scale), $"{picture}: a layout picture's expected.json states umPerPx");
        string? tech = root.TryGetProperty("technology", out var t) ? t.GetString() : null;
        string? techPath = tech is not null && tech.EndsWith(".ctech", StringComparison.OrdinalIgnoreCase) ? Path.Combine(dir, tech) : null;
        var technology = techPath is not null ? TechPersistence.LoadFromFile(techPath) : tech is not null ? ShippedTechnologies.Load(tech) : TracePictures.Tech();
        var kind = ImageKind.Classify(source.Source!.Raster);
        var read = ImageRecognition.Circuit(new ImageRecognitionInput
        {
            Trace = new ImageTraceInput
            {
                Source = source.Source!, Technology = technology, TechnologyPath = techPath,
                Scale = ImageScaleStatement.Stated(scale.GetDouble() * 1e-6),
                Kind = kind.Kind == DrawingKind.Layout ? kind : kind.Force(DrawingKind.Layout),
            },
        });
        Assert.True(read.Circuit is not null, $"{picture}: {read.Refusal}");
        var tb = read.Circuit!.TestBench;

        if (root.TryGetProperty("parts", out var parts))
            Assert.Equal(parts.GetInt32(), tb.Instances.Count(i => i.Reference is "R" or "L" or "C"));
        if (root.TryGetProperty("nets", out var nets))
            Assert.Equal(nets.GetInt32(), tb.Instances.SelectMany(i => i.NetBindings).Where(n => n != "0").Distinct().Count());
        if (root.TryGetProperty("kinds", out var kinds))
            foreach (var k in kinds.EnumerateObject())
                Assert.True(k.Value.GetInt32() == tb.Instances.Count(i => i.Reference == k.Name),
                            $"{picture}: {k.Name} × {tb.Instances.Count(i => i.Reference == k.Name)}, expected {k.Value.GetInt32()}");
    }

    /// <summary>The committed style pictures' scores, one line each — the same table, for the pictures every clone has.</summary>
    [Fact]
    public void TheCommittedStylePictures_AreScored()
    {
        string root = FixturePaths.Require(StylePictures.Folder);
        output.WriteLine(FieldScore.Header);
        foreach (string dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
            output.WriteLine(FieldScore.Read(dir, Path.GetFileName(dir)).Line());
    }
}
