// brief-img-12-acceptance-docs-example.md R-im12-4 — the field pictures as a theory, one case per picture present under
// the git-ignored testdata/image-sources/ (testdata/image-sources/README.md holds the schema). On a fresh clone the theory
// skips with the reason.
//
// brief-imf-1-corpus-and-scorer.md R-imf1-5: a schematic picture is SCORED — FieldScore's line is printed, so the TRX
// carries the table (design note §26) — and HELD only on what overview §4 promises for it (brief-imf-10 R-imf10-2): a
// datasheet picture its passives by kind, its topology, its ports and its IC or amplifier as one part; a web picture its
// kinds and topology; every picture no region with more pins than its largest drawn part; the shaded illustration
// refused. Most pictures do not reach the promise (§26 names each miss's cause), so each is held where its reading
// stands — a count right no lower, a topology read stays read — and a reader change that loses ground fails on the
// machine that has the pictures, while a fresh clone skips. A layout picture keeps IM-12's counts. The committed style
// pictures' table is printed beside it.

using System;
using System.Collections.Generic;
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
            if (Holds.TryGetValue(picture, out var held)) Hold(picture, held, score);
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

    /// <summary>What a field picture is held to: at least this many right of its kinds, ports and blocks, its topology
    /// when it is read, no region over <see cref="Region"/> pins — or refused as no schematic.</summary>
    private sealed record Held(int Kinds = 0, bool Topology = false, int Ports = 0, int Blocks = 0, int Region = int.MaxValue,
                               bool Refused = false);

    /// <summary>
    /// R-imf10-2, measured 2026-10-11. Region is the largest drawn part's pin count, except on web a, d and h — where a
    /// two-pin part's pieces merge into a 4-pin block, a miss held where it stands. ds-g is the stress picture: scored,
    /// held only on its regions.
    /// </summary>
    private static readonly Dictionary<string, Held> Holds = new(StringComparer.Ordinal)
    {
        ["picture-ds-a1"] = new(Kinds: 3, Blocks: 1, Region: 8),
        ["picture-ds-a2"] = new(Kinds: 3, Blocks: 1, Region: 8),
        ["picture-ds-b1"] = new(Kinds: 2, Ports: 1, Blocks: 1, Region: 4),
        ["picture-ds-b2"] = new(Kinds: 2, Region: 4),
        ["picture-ds-c1"] = new(Kinds: 3, Ports: 2, Blocks: 1, Region: 4),
        ["picture-ds-c2"] = new(Kinds: 3, Ports: 2, Blocks: 1, Region: 4),
        ["picture-ds-d1"] = new(Kinds: 6, Ports: 2, Blocks: 1, Region: 3),
        ["picture-ds-d2"] = new(Kinds: 6, Ports: 1, Region: 3),
        ["picture-ds-e1"] = new(Region: 4),
        ["picture-ds-e2"] = new(Region: 4),
        ["picture-ds-f1"] = new(Kinds: 5, Ports: 1, Blocks: 1, Region: 8),
        ["picture-ds-f2"] = new(Kinds: 11, Blocks: 1, Region: 8),
        ["picture-ds-g1"] = new(Region: 16),
        ["picture-ds-g2"] = new(Region: 16),
        ["picture-ds-h1"] = new(Kinds: 2, Ports: 2, Region: 4),
        ["picture-ds-h2"] = new(Kinds: 2, Ports: 2, Region: 4),
        ["picture-a"] = new(Kinds: 3, Region: 4),
        ["picture-b"] = new(Kinds: 3, Topology: true, Region: 2),
        ["picture-c"] = new(Kinds: 7, Region: 3),
        ["picture-d"] = new(Region: 4),
        ["picture-e"] = new(Kinds: 4, Region: 2),
        ["picture-f"] = new(Kinds: 6, Region: 2),
        ["picture-g"] = new(Refused: true),
        ["picture-h"] = new(Kinds: 6, Region: 4),
        ["picture-i"] = new(Kinds: 1, Region: 2),
    };

    private static void Hold(string picture, Held held, FieldScore score)
    {
        string line = score.Line();
        if (held.Refused)
        {
            Assert.True(score.Refused, $"{picture}: no schematic, but read — {line}");
            Assert.StartsWith("This picture does not look like a schematic", score.Refusal);
            return;
        }
        Assert.False(score.Refused, $"{picture}: refused — {line}");
        Assert.True((score.Kinds?.Right ?? 0) >= held.Kinds, $"{picture}: kinds lost ground — {line}");
        Assert.True(!held.Topology || score.Topology == true, $"{picture}: topology lost — {line}");
        Assert.True((score.Ports?.Right ?? 0) >= held.Ports, $"{picture}: ports lost ground — {line}");
        Assert.True((score.Blocks?.Right ?? 0) >= held.Blocks, $"{picture}: its IC or amplifier lost — {line}");
        Assert.True(score.LargestRegion <= held.Region, $"{picture}: a region of {score.LargestRegion} pins — {line}");
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
