using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-16 R-mc16-2, on the sources alone (the owner runs DocGen): the Model Composer page is in the
/// navigation, and every link the series' pages make to a section — on the page itself or another — names a heading
/// that exists.
/// </summary>
public sealed class ModelComposerDocumentedPageTests
{
    private static readonly string Src = Path.Combine(ModelCliFixture.RepoRoot(), "docs", "user", "src", "reference");

    [Fact]
    public void ThePageIsInTheNavigation_AndEverySectionItLinksToExists()
    {
        string nav = File.ReadAllText(Path.Combine(Src, "..", "_nav.txt"));
        Assert.Contains("reference/model-composer.html", nav, StringComparison.Ordinal);

        foreach (string page in new[] { "model-composer.md", "ai-agents.md" })
        {
            string text = File.ReadAllText(Path.Combine(Src, page));
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(text, @"\]\(([a-z0-9-]+\.html)?#([a-z0-9-]+)\)|href=""#([a-z0-9-]+)"""))
            {
                string target = m.Groups[1].Success && m.Groups[1].Value.Length > 0
                    ? Path.ChangeExtension(m.Groups[1].Value, ".md") : page;
                string id = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                string targetText = File.ReadAllText(Path.Combine(Src, target));
                Assert.True(targetText.Contains("{#" + id + "}", StringComparison.Ordinal)
                            || targetText.Contains("id=\"" + id + "\"", StringComparison.Ordinal),
                            $"{page} links to {target}#{id}, which has no such section");
            }
        }
    }
}
