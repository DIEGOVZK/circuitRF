using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Workspace;

/// <summary>One entry in the shipped examples index — a real workspace folder and how to name it.</summary>
/// <param name="Folder">The folder under the examples root. A leaf name, never a path.</param>
/// <param name="Title">What the menu calls it.</param>
/// <param name="Summary">One sentence, for the menu's tooltip.</param>
/// <param name="Directory">Where that folder actually is on this machine, resolved.</param>
public sealed record ExampleWorkspace(string Folder, string Title, string Summary, string Directory)
{
    /// <summary>The workspace manifest inside it.</summary>
    public string CwsPath => Path.Combine(Directory, WorkspaceFileName);

    internal const string WorkspaceFileName = ".cws";
}

/// <summary>
/// The example workspaces circuitRF ships — <b>ordinary workspace FOLDERS, copied out on request</b>
/// (File ▸ New ▸ Examples).
///
/// <h3>Why they are files and not an embedded resource</h3>
/// <para>They are real workspaces in the repository at <c>examples/</c>, openable in place, and the
/// owner edits them there (2026-09-15). An embedded resource would have to be unpacked before it
/// could be opened at all, which means the copy the application ships and the copy anyone edits are
/// two different artifacts with nothing keeping them in step. The shipped tree is the SAME tree —
/// <c>src/Ui/CircuitRF.Ui.csproj</c> copies <c>examples/**</c> into the app output the same way it
/// copies the user documentation and the PCell Python package.</para>
///
/// <h3>The index is data beside them, not a list in here</h3>
/// <para><c>examples/examples.json</c> gives the order, the menu titles and the one-line summaries.
/// Discovery-by-directory-scan was the alternative and it cannot express either of the first two;
/// a list compiled into circuitRF cannot be edited alongside the thing it describes. A folder named
/// in the index that is not on disk is SKIPPED rather than shown as a menu item that fails when
/// pressed — the two can disagree on a partial install, and the menu is the wrong place to find
/// out.</para>
///
/// <para>Framework-free and read-only. Nothing here copies anything; see
/// <c>ExampleWorkspaceInstall</c> for that half, which lives in <c>src/Ui</c> because it is built on
/// <c>WorkspaceCopy</c>.</para>
/// </summary>
public static class ExampleWorkspaces
{
    /// <summary>The folder the examples live in, beside the executable and in the source tree.</summary>
    public const string RootFolderName = "examples";

    /// <summary>The index file inside it.</summary>
    public const string IndexFileName = "examples.json";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Every shipped example, in index order, filtered to the ones actually present.
    ///
    /// <para>Empty when the examples were not installed, which is a supported state and not an
    /// error: the menu hides itself rather than offering rows that cannot work.</para>
    /// </summary>
    /// <param name="searchFrom">Where to start looking. Null uses the running application's own
    /// directory — the only right answer in an installed build, and one a test cannot supply.</param>
    public static IReadOnlyList<ExampleWorkspace> All(string? searchFrom = null)
    {
        if (ResolveRoot(searchFrom) is not { } root) return [];

        IndexFile? index;
        try
        {
            index = JsonSerializer.Deserialize<IndexFile>(
                File.ReadAllText(Path.Combine(root, IndexFileName)), _json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
        if (index?.Examples is not { Count: > 0 } entries) return [];

        var found = new List<ExampleWorkspace>(entries.Count);
        foreach (var e in entries)
        {
            if (e.Folder is not { Length: > 0 } folder) continue;

            // A leaf name, never a path: the index is data, and data that can name "../.." is data
            // that can reach out of the examples tree.
            if (folder.Contains('/') || folder.Contains('\\') || folder is "." or "..") continue;

            string dir = Path.Combine(root, folder);
            if (!File.Exists(Path.Combine(dir, ExampleWorkspace.WorkspaceFileName))) continue;

            found.Add(new ExampleWorkspace(
                folder,
                e.Title   is { Length: > 0 } t ? t : folder,
                e.Summary ?? "",
                dir));
        }
        return found;
    }

    /// <summary>
    /// The examples root, or null when there is none.
    ///
    /// <para>Beside the executable first — that is where a published build puts it — then walking up
    /// for the source tree, which is what makes <c>dotnet run</c> and a test find the same files.
    /// The same two-step <c>DocLauncher</c> uses for the bundled documentation, and for the same
    /// reason: a build-file change that works in one and fails in the other is how
    /// <c>tools/pcell-python</c> shipped broken once.</para>
    /// </summary>
    public static string? ResolveRoot(string? searchFrom = null)
    {
        string baseDir = searchFrom ?? AppContext.BaseDirectory;

        string bundled = Path.Combine(baseDir, RootFolderName);
        if (File.Exists(Path.Combine(bundled, IndexFileName))) return Path.GetFullPath(bundled);

        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, RootFolderName);
            if (File.Exists(Path.Combine(candidate, IndexFileName))) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private sealed class IndexFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<Entry>? Examples { get; set; }
    }

    private sealed class Entry
    {
        public string? Folder  { get; set; }
        public string? Title   { get; set; }
        public string? Summary { get; set; }
    }
}
