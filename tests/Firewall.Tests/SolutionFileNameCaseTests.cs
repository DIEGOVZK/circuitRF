using System.Text.RegularExpressions;

namespace CircuitRF.Firewall.Tests;

/// <summary>
/// Every <c>.slnx</c> name written as a literal in code must be spelled exactly as the file on disk.
///
/// <para>The tests, the doc fixtures and a few tools find the repository root by walking up until the
/// solution file appears. 212 of those walks named it in lower case while the file is
/// <c>circuitRF.slnx</c>: on macOS and Windows the filesystem is case-insensitive and the walk
/// succeeded, so nothing here ever noticed, while on a case-sensitive filesystem (an ordinary Linux
/// checkout) every one of those tests failed with "repo root not found". A walk run on this machine
/// cannot catch the next one either, which is why this compares the spelling ordinally instead.</para>
/// </summary>
public class SolutionFileNameCaseTests
{
    private static readonly Regex SlnxLiteral = new(@"[""']([A-Za-z0-9_.-]+\.slnx)[""']", RegexOptions.Compiled);

    // A symlinked directory is never followed: a local Wine prefix under tools/ links a drive to "/".
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible    = true,
        AttributesToSkip      = FileAttributes.ReparsePoint,
    };

    [Fact]
    public void EverySlnxLiteral_MatchesTheFileOnDiskExactly()
    {
        string root = FindRepoRoot();
        var onDisk = Directory.EnumerateFiles(root, "*.slnx", Walk)
                              .Select(Path.GetFileName)
                              .ToHashSet(StringComparer.Ordinal);

        var wrong = new List<string>();
        foreach (string top in new[] { "src", "tests", "tools" })
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, top), "*.*", Walk)
                                         .Where(f => f.EndsWith(".cs", StringComparison.Ordinal)
                                                  || f.EndsWith(".py", StringComparison.Ordinal))
                                         .Where(f => !IsBuildOutput(f)))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                foreach (Match m in SlnxLiteral.Matches(lines[i]))
                    if (!onDisk.Contains(m.Groups[1].Value))
                        wrong.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: \"{m.Groups[1].Value}\"");
        }

        Assert.True(wrong.Count == 0,
            $"{wrong.Count} .slnx name(s) do not match a solution file's exact spelling " +
            $"({string.Join(", ", onDisk.Order(StringComparer.Ordinal))}); they only resolve on a " +
            "case-insensitive filesystem:\n" + string.Join("\n", wrong));
    }

    private static bool IsBuildOutput(string path)
    {
        string sep = Path.DirectorySeparatorChar.ToString();
        return path.Contains(sep + "bin" + sep, StringComparison.Ordinal)
            || path.Contains(sep + "obj" + sep, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null,
            "Could not locate the repo root (the folder holding circuitRF.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
