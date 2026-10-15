// ================================================================
//  MarkdownDocumentTests.cs — a workspace's README.md as a read-only document.
//
//  What is actually at risk here, in order:
//
//   1. THE README IS FOUND. It is located by a case-insensitive directory scan, not by probing the
//      name, because macOS and Windows disagree with Linux about whether `Readme.md` is `README.md`.
//      A test that only ever asks for the exact spelling would pass on two platforms and tell you
//      nothing about the third.
//
//   2. THE TABLES DO NOT LEAK. SmallMarkdown has no two-dimensional anything, so a GFM table is
//      flattened into lines. If that ever stops working the failure is silent and ugly rather than
//      loud: `|---|---|` simply appears on screen. Two of the six shipped examples have a table.
//
//   3. EVERY SHIPPED EXAMPLE STILL HAS ONE. The README is the thing the user is shown instead of the
//      Welcome tab when they copy an example out of File ▸ New ▸ Examples. An example that lost its README
//      would open on an empty document area, which looks like a bug in the copy.
// ================================================================

using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Markdown;

namespace CircuitRF.Ui.Tests.Markdown;

public sealed class MarkdownDocumentTests : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "crf-readme-" + Guid.NewGuid().ToString("N")[..12]);

    public MarkdownDocumentTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    private static string Flat(MarkdownLine line) => string.Concat(line.Runs.Select(r => r.Text));

    // ══ 1. Finding it ═══════════════════════════════════════════════════════

    [Fact]
    public void AWorkspaceWithNoReadmeHasNone()
        => Assert.Null(MarkdownDocument.FindWorkspaceReadme(_tmp));

    [Fact]
    public void AMissingDirectoryIsNotAnException()
        => Assert.Null(MarkdownDocument.FindWorkspaceReadme(Path.Combine(_tmp, "nope")));

    [Theory]
    [InlineData("README.md")]
    [InlineData("Readme.md")]
    [InlineData("readme.MD")]
    public void TheReadmeIsFoundWhateverItIsSpelled(string name)
    {
        File.WriteAllText(Path.Combine(_tmp, name), "# hello");
        Assert.Equal(name, Path.GetFileName(MarkdownDocument.FindWorkspaceReadme(_tmp)));
    }

    /// <summary>Another <c>.md</c> beside it is not the workspace's README.</summary>
    [Fact]
    public void ADifferentMarkdownFileIsNotTheReadme()
    {
        File.WriteAllText(Path.Combine(_tmp, "NOTES.md"), "# hello");
        Assert.Null(MarkdownDocument.FindWorkspaceReadme(_tmp));
    }

    [Theory]
    [InlineData("x.md",   true)]
    [InlineData("X.MD",   true)]
    [InlineData("x.csch", false)]
    [InlineData("x",      false)]
    public void MarkdownIsRecognizedByExtension(string name, bool expected)
        => Assert.Equal(expected, MarkdownDocument.IsMarkdown(name));

    // ══ 2. Loading it ═══════════════════════════════════════════════════════

    /// <summary>
    /// The document holds the PARSED lines and not the source text, which is the whole of why it is
    /// read-only: there is nothing an edit could be applied to.
    /// </summary>
    [Fact]
    public void LoadParsesAndTitlesItself()
    {
        string path = Path.Combine(_tmp, "README.md");
        File.WriteAllText(path, "# Title\n\nA line.");

        var doc = MarkdownDocument.Load(path);

        Assert.Equal("README.md", doc.Title);
        Assert.Equal(path, doc.FilePath);
        Assert.Equal("Title", Flat(doc.Lines[0]));
        Assert.Equal(1, doc.Lines[0].HeadingLevel);
        Assert.Contains(doc.Lines, l => Flat(l) == "A line.");
    }

    // ══ 3. Tables, flattened ════════════════════════════════════════════════

    [Fact]
    public void ATableRuleIsNotRendered()
    {
        var lines = SmallMarkdown.Parse("| Block | Shows |\n|---|---|\n| `A` | first |\n| `B` | second |");

        Assert.All(lines, l => Assert.DoesNotContain("---", Flat(l)));
        Assert.All(lines, l => Assert.DoesNotContain("|", Flat(l)));
    }

    [Fact]
    public void ATableHeaderIsBoldAndItsRowsAreBullets()
    {
        var lines = SmallMarkdown.Parse("| Block | Shows |\n|:--|--:|\n| A | first |\n| B | second |");

        Assert.Equal(3, lines.Count);

        Assert.Null(lines[0].Bullet);
        Assert.All(lines[0].Runs, r => Assert.True(r.Bold));
        Assert.Equal("Block – Shows", Flat(lines[0]));

        Assert.Equal(SmallMarkdown.Bullet, lines[1].Bullet);
        Assert.Equal("A – first", Flat(lines[1]));
        Assert.True(lines[1].Runs[0].Bold);          // the row's first cell leads
        Assert.False(lines[1].Runs[^1].Bold);
    }

    /// <summary>A table ends at the blank line, like a list.</summary>
    [Fact]
    public void ProseAfterATableIsProse()
    {
        var lines = SmallMarkdown.Parse("| a | b |\n|---|---|\n| c | d |\n\nAfter.");

        Assert.Equal("After.", Flat(lines[^1]));
        Assert.Null(lines[^1].Bullet);
    }

    /// <summary>
    /// A row of pipes with no rule under it is not a table, and the line above one is only a header
    /// BECAUSE of the rule — which is why it is rewritten when the rule is read rather than guessed at.
    /// </summary>
    [Fact]
    public void ASentenceIsNotATable()
    {
        var lines = SmallMarkdown.Parse("Use a | b in the shell.");

        MarkdownLine only = Assert.Single(lines);
        Assert.Equal("Use a | b in the shell.", Flat(only));
    }

    [Fact]
    public void AnEscapedPipeIsCellContent()
    {
        var lines = SmallMarkdown.Parse("| op | means |\n|---|---|\n| a \\| b | or |");

        Assert.Equal("a | b – or", Flat(lines[^1]));
    }

    // ══ 4. Fenced code, and the wraps that must not be joined ═══════════════

    /// <summary>
    /// A fence's contents are lines of their own, whatever they contain — this is the one that would
    /// have run a directory tree together into a single sentence had soft-wrap joining reached it.
    /// </summary>
    [Fact]
    public void AFencedBlockIsVerbatimLines()
    {
        var lines = SmallMarkdown.Parse(
            "Read it:\n\n```\npcell-kit/\n  kit.py    the generators\n\n  x\n```\n\nAfter.",
            joinSoftWraps: true);

        var code = lines.Where(l => l.IsCode).Select(Flat).ToArray();
        Assert.Equal(new[] { "pcell-kit/", "  kit.py    the generators", "", "  x" }, code);

        Assert.Equal("Read it:", Flat(lines[0]));
        Assert.Equal("After.",   Flat(lines[^1]));
        Assert.False(lines[^1].IsCode);
    }

    /// <summary>Nothing inside a fence is markup — a leading <c>#</c> is a comment, not a heading.</summary>
    [Fact]
    public void MarkupInsideAFenceIsText()
    {
        var lines = SmallMarkdown.Parse("```\n# gamma Z0=50 mag_ang\n- 0.20  30\n```");

        Assert.All(lines, l => Assert.True(l.IsCode));
        Assert.All(lines, l => Assert.Equal(0, l.HeadingLevel));
        Assert.All(lines, l => Assert.Null(l.Bullet));
        Assert.Equal("# gamma Z0=50 mag_ang", Flat(lines[0]));
    }

    /// <summary>
    /// The two callers want opposite answers about a hard-wrapped paragraph, so both are pinned: a
    /// release body keeps the author's line breaks, a README folds them back out.
    /// </summary>
    [Fact]
    public void SoftWrapsJoinOnlyWhenAsked()
    {
        const string source = "A sentence that the\nauthor wrapped.\n\n- an item that\n  wrapped too";

        var kept = SmallMarkdown.Parse(source);
        Assert.Equal("A sentence that the", Flat(kept[0]));
        Assert.Equal("author wrapped.",     Flat(kept[1]));

        var joined = SmallMarkdown.Parse(source, joinSoftWraps: true);
        Assert.Equal("A sentence that the author wrapped.", Flat(joined[0]));
        Assert.Equal("an item that wrapped too", Flat(joined[^1]));
        Assert.Equal(SmallMarkdown.Bullet, joined[^1].Bullet);
    }

    /// <summary>A heading is a line by itself: the paragraph under it is not part of it.</summary>
    [Fact]
    public void AParagraphNeverJoinsIntoTheHeadingAboveIt()
    {
        var lines = SmallMarkdown.Parse("## Amplifier\nA two-port file.", joinSoftWraps: true);

        Assert.Equal("Amplifier", Flat(lines[0]));
        Assert.Equal(2, lines[0].HeadingLevel);
        Assert.Equal("A two-port file.", Flat(lines[1]));
        Assert.Equal(0, lines[1].HeadingLevel);
    }

    // ══ 5. The shipped examples ═════════════════════════════════════════════

    /// <summary>
    /// Every example still ships the README the user is shown on open, it still parses to something,
    /// and none of it reaches the screen as raw table markup.
    /// </summary>
    [Fact]
    public void EveryShippedExampleHasAReadableReadme()
    {
        var examples = ExampleWorkspaces.All();
        Assert.NotEmpty(examples);

        foreach (var example in examples)
        {
            string? readme = MarkdownDocument.FindWorkspaceReadme(example.Directory);
            Assert.True(readme is not null, $"'{example.Title}' ships no README.md.");

            var doc = MarkdownDocument.Load(readme!);
            Assert.NotEmpty(doc.Lines);

            foreach (var line in doc.Lines)
            {
                Assert.DoesNotContain("|---", Flat(line));
                Assert.DoesNotContain("```",  Flat(line));
            }
        }
    }
}
