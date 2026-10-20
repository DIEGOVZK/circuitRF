using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Archive;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  RC-1 — the `.cwsuser` split
//  (brief-revision-control-1-workspace-file-split.md; docs/design/revision-control.md §3.1/§3.1a)
//
//  Per-user session state — the panel arrangement, the tree's expanded categories, the open tabs,
//  the active tab and the colour scheme — moved out of the `.cws` and into a sibling `.cwsuser`.
//  Measured on this repo's own demo workspace, those five fields were 97.8% of the file, so the
//  `.cws` changed on every session close for reasons that had nothing to do with the design.
//
//  The property these tests exist to hold is that ABSENCE IS THE NORMAL STATE. Every clone, every
//  archive and every workspace handed to a colleague arrives with no `.cwsuser`; a versioned
//  document that cannot be opened without an unversioned one is not split, it is broken. So the
//  gates below spend most of their weight on the file NOT being there, on it being wrong, and on it
//  being written by exactly one place.
//
//  Headless, over real temp directories, in the shape WorkspaceCopyTests already uses: the feature
//  IS a rule about files on disk, and an in-memory double would agree with itself about the thing
//  under test.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class WorkspaceUserSidecarTests : IDisposable
{
    private readonly string _root;
    private readonly string _ws;
    private readonly string _cws;
    private readonly string _sidecar;

    public WorkspaceUserSidecarTests()
    {
        _root    = Path.Combine(Path.GetTempPath(), "crf_cwsuser_" + Guid.NewGuid().ToString("N")[..8]);
        _ws      = Path.Combine(_root, "workspaceA");
        Directory.CreateDirectory(_ws);
        _cws     = Path.Combine(_ws, ".cws");
        _sidecar = Path.Combine(_ws, ".cwsuser");
    }

    public void Dispose()
    {
        WorkspaceWritability.WritabilityProbe = null;   // also drops the memo
        WorkspaceRootFinder.InvalidateCache();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>A workspace configuration with every field RC-1 touches populated, plus the project
    /// fields that must stay behind, so a test asserting "moved" and one asserting "stayed" run on
    /// the same object.</summary>
    private static CwsFile Everything() => new()
    {
        // The project half — none of this may leave the `.cws`.
        LibraryRefs        = ["../lib"],
        KnownFiles         = ["Amp/Amp.ccell"],
        DefaultTechRef     = "tech/pcb.ctech",
        DefaultAssemblyRef = "rules/house.wasm",
        PythonInterpreter  = "py.exe -3",

        // The per-user half — all five of these belong in the sidecar.
        DockLayout         = JsonNode.Parse("""{"Root":{"Proportion":0.25},"ActiveDocument":"a.csch"}"""),
        TreeViewState      = new CwsTreeViewState { Cells = true, Libraries = false, TechFiles = false },
        OpenDocuments      =
        [
            new CwsOpenDocument { Path = "tech/pcb.ctech",         Kind = "tech",      TabOrder = 0 },
            new CwsOpenDocument { Path = "Amp/schematic/Amp.csch", Kind = "schematic", TabOrder = 1 },
        ],
        ActiveDocumentPath = "tech/pcb.ctech",
        ColorSchemeName    = "dusk",
    };

    private static void AssertSessionStateMatches(CwsFile expected, CwsFile actual)
    {
        Assert.Equal(expected.DockLayout?.ToJsonString(), actual.DockLayout?.ToJsonString());
        Assert.Equal(expected.ActiveDocumentPath, actual.ActiveDocumentPath);
        Assert.Equal(expected.ColorSchemeName, actual.ColorSchemeName);

        Assert.Equal(expected.TreeViewState!.Cells,     actual.TreeViewState!.Cells);
        Assert.Equal(expected.TreeViewState!.Libraries, actual.TreeViewState!.Libraries);
        Assert.Equal(expected.TreeViewState!.TechFiles, actual.TreeViewState!.TechFiles);

        Assert.Equal(expected.OpenDocuments!.Count, actual.OpenDocuments!.Count);
        foreach (var (want, got) in expected.OpenDocuments!.Zip(actual.OpenDocuments!))
        {
            Assert.Equal(want.Path,     got.Path);
            Assert.Equal(want.Kind,     got.Kind);
            Assert.Equal(want.TabOrder, got.TabOrder);
        }
    }

    // ── Gate 1: the round trip is unchanged ───────────────────────────────────

    /// <summary>
    /// <b>Asserted against the PRE-SPLIT behaviour, not against a transcription of the new writer.</b>
    /// The pre-split behaviour is available exactly: a `.cws` that carries all five fields inline and
    /// has no sidecar beside it is what every workspace on disk looked like before RC-1, and RC-1's
    /// migration rule (R-rc1-12) says it must still load identically. So the two shapes are loaded
    /// and compared to each other rather than to a literal — a mistake in the new writer that the new
    /// reader happens to undo cannot pass this.
    /// </summary>
    [Fact]
    public void ASplitWorkspace_RestoresExactlyWhatAPreSplitOneDid()
    {
        var written = Everything();

        // Post-split: two files, written by the choke point.
        Assert.True(WorkspacePersistence.SaveToFileAtomic(_cws, written));
        var afterSplit = WorkspacePersistence.LoadFromFile(_cws);

        // Pre-split: one file, every field inline, no sidecar — a workspace saved by any build
        // before RC-1.
        string legacyDir = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllText(Path.Combine(legacyDir, ".cws"),
                          JsonSerializer.Serialize(written, new JsonSerializerOptions { WriteIndented = true }));
        var preSplit = WorkspacePersistence.LoadFromFile(Path.Combine(legacyDir, ".cws"));

        AssertSessionStateMatches(preSplit, afterSplit);
        AssertSessionStateMatches(written, afterSplit);
    }

    [Fact]
    public void TheProjectHalfStaysInTheCws_AndTheSessionHalfIsNotInIt()
    {
        Assert.True(WorkspacePersistence.SaveToFileAtomic(_cws, Everything()));

        var cwsJson = JsonNode.Parse(File.ReadAllText(_cws))!.AsObject();

        // What stays. `PythonInterpreter` is here on purpose: it is per-MACHINE rather than
        // per-user, but the `.cwsuser` is not a per-machine file either, and moving it would cost a
        // kit-using workspace a process-launch storm on every fresh clone for no gain.
        foreach (var stays in new[]
                 { "FormatVersion", "LibraryRefs", "KnownFiles", "DefaultTechRef",
                   "DefaultAssemblyRef", "PythonInterpreter" })
            Assert.True(cwsJson.ContainsKey(stays), $"{stays} must stay in the .cws");

        // What moves — and the colour scheme is one of them by the owner's decision of 2026-09-06,
        // which is the one field §3.1's table had assigned rather than measured.
        foreach (var moved in new[]
                 { "DockLayout", "TreeViewState", "OpenDocuments", "ActiveDocumentPath", "ColorSchemeName" })
            Assert.False(cwsJson.ContainsKey(moved), $"{moved} must not be written into the .cws");

        var userJson = JsonNode.Parse(File.ReadAllText(_sidecar))!.AsObject();
        foreach (var moved in new[]
                 { "DockLayout", "TreeViewState", "OpenDocuments", "ActiveDocumentPath", "ColorSchemeName" })
            Assert.True(userJson.ContainsKey(moved), $"{moved} must be written into the .cwsuser");
    }

    // ── Gate 2: an absent sidecar is normal, and never reported ───────────────

    [Fact]
    public void DeletingTheSidecar_LeavesAWorkspaceThatOpensOnDefaults()
    {
        Assert.True(WorkspacePersistence.SaveToFileAtomic(_cws, Everything()));
        File.Delete(_sidecar);

        var read = WorkspacePersistence.LoadFromFile(_cws);

        // Defaults, exactly as a freshly-created workspace has them...
        Assert.Null(read.DockLayout);
        Assert.Null(read.TreeViewState);
        Assert.Null(read.OpenDocuments);
        Assert.Null(read.ActiveDocumentPath);
        Assert.Null(read.ColorSchemeName);

        // ...and the design configuration is untouched, which is the whole point of the split.
        Assert.Equal("tech/pcb.ctech", read.DefaultTechRef);
        Assert.Equal(["../lib"], read.LibraryRefs);
    }

    /// <summary>
    /// <b>Absence is never reported, and this asserts the structural reason rather than an empty
    /// message sink</b> (R-rc1-5). `WorkspaceViewModel` cannot be constructed headlessly (see
    /// `src/Ui/CLAUDE.md`), so a live sink is out of reach — but the stronger statement is available
    /// and is the one that actually holds: the whole read path is in `src/Design`, which has no
    /// message sink to post to. A missing sidecar CANNOT be reported, at any level, because nothing
    /// on that path can report anything.
    ///
    /// <para>The scan is comment-stripped: this repo has been caught by a source scan satisfied by
    /// its own documentation.</para>
    /// </summary>
    [Fact]
    public void NothingOnTheSidecarReadPath_CanPostAMessageAtAll()
    {
        foreach (var file in new[] { "WorkspaceUserPersistence.cs", "WorkspacePersistence.cs" })
        {
            string code = StripComments(RepoFile(Path.Combine("src", "Design", "Workspace", file)));

            foreach (var reporter in new[] { "Messages.", "IMessageSink", "Warning(", "PostDiagnostic" })
                Assert.False(code.Contains(reporter, StringComparison.Ordinal),
                    $"{file} must not be able to report a missing or malformed .cwsuser ({reporter})");
        }
    }

    // ── Gate 3: a malformed sidecar is an absent one ──────────────────────────

    [Theory]
    [InlineData("{\"DockLayout\": {\"Root\":")]           // truncated JSON
    [InlineData("[1, 2, 3]")]                              // valid JSON, wrong shape
    [InlineData("{\"TreeViewState\": 42}")]                // valid JSON, wrong shape inside
    [InlineData("")]                                       // empty file
    [InlineData("not json at all")]
    public void AMalformedSidecar_IsTreatedAsAnAbsentOne(string content)
    {
        Assert.True(WorkspacePersistence.SaveToFileAtomic(_cws, Everything()));
        File.WriteAllText(_sidecar, content);

        var read = WorkspacePersistence.LoadFromFile(_cws);

        Assert.Null(read.DockLayout);
        Assert.Null(read.OpenDocuments);
        Assert.Null(read.ColorSchemeName);
        Assert.Equal("tech/pcb.ctech", read.DefaultTechRef);
    }

    // ── Gate 4: a sidecar with no `.cws` beside it is not a workspace ─────────

    [Fact]
    public void AnOrphanSidecar_ResolvesToNoWorkspace()
    {
        string orphanDir = Path.Combine(_root, "just-a-folder");
        Directory.CreateDirectory(orphanDir);
        string orphan = Path.Combine(orphanDir, ".cwsuser");
        File.WriteAllText(orphan, "{}");

        Assert.Null(WorkspaceUserPersistence.ResolveWorkspace(orphan));

        // ...and with its sibling present it resolves to exactly that file.
        File.WriteAllText(Path.Combine(orphanDir, ".cws"), "{\"FormatVersion\":2}");
        Assert.Equal(Path.Combine(orphanDir, ".cws"), WorkspaceUserPersistence.ResolveWorkspace(orphan));
    }

    /// <summary>
    /// The other half of R-rc1-8, which only the dispatcher can express: an orphan gets a SENTENCE
    /// and no window. Source-scanned because `App.OpenFiles` runs against a live application
    /// lifetime — the same fallback the three registration-parity tests in `WBondStandaloneTests`
    /// already use on this file.
    /// </summary>
    [Fact]
    public void TheDispatcher_AnswersAnOrphanSidecarWithASentenceAndNoWindow()
    {
        string app = StripComments(RepoFile(Path.Combine("src", "Ui", "App.axaml.cs")));

        int caseAt = app.IndexOf("case \".cwsuser\":", StringComparison.Ordinal);
        Assert.True(caseAt >= 0, "App.OpenFiles must have a case for .cwsuser");

        string block = app[caseAt..Math.Min(app.Length, caseAt + 900)];

        // It resolves through the folder rather than opening the sidecar itself...
        Assert.Contains("WorkspaceUserPersistence.ResolveWorkspace", block);
        // ...and the failing branch says something instead of falling through to an empty window.
        Assert.Contains("Messages.Warning", block);

        // No git vocabulary reaches a designer who did not ask for it (R-rc0-6).
        foreach (var word in new[] { "commit", "branch", "repository", "checkout", "HEAD" })
            Assert.DoesNotContain(word, block, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>A dotfile with no stem DOES have an extension, as far as <c>Path.GetExtension</c> is
    /// concerned</b> — and the whole dispatch of both halves of a workspace rests on it, since
    /// <c>App.OpenFiles</c> switches on exactly that value. Pinned rather than assumed because
    /// <c>MoveRefRegistry</c> carried a comment asserting the opposite, and a wrong belief here would
    /// make double-clicking either half open nothing at all.
    /// </summary>
    [Fact]
    public void ADotfileWithNoStem_StillHasAnExtensionForTheDispatcherToSwitchOn()
    {
        Assert.Equal(".cws",     Path.GetExtension(".cws"));
        Assert.Equal(".cwsuser", Path.GetExtension(".cwsuser"));
        Assert.Equal(".cwsuser", Path.GetExtension(Path.Combine("some", "workspace", ".cwsuser")));
    }

    // ── Gate 5: migration is a read-side default, dropped on the first save ───

    [Fact]
    public void AnOlderCws_IsHonouredOnRead_AndItsStaleCopiesAreDroppedByTheFirstSave()
    {
        var original = Everything();

        // A `.cws` as every build before RC-1 wrote one: all five fields inline, no sidecar.
        File.WriteAllText(_cws,
            JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true }));
        Assert.False(File.Exists(_sidecar));

        // Honoured on read, unchanged, with no rewrite pass and nothing asked of the user.
        var loaded = WorkspacePersistence.LoadFromFile(_cws);
        AssertSessionStateMatches(original, loaded);
        Assert.False(File.Exists(_sidecar));
        Assert.Equal(WorkspacePersistence.CurrentFormatVersion, loaded.FormatVersion);

        // One save is the whole of the migration.
        Assert.True(WorkspacePersistence.SaveToFileAtomic(_cws, loaded));

        var cwsJson = JsonNode.Parse(File.ReadAllText(_cws))!.AsObject();
        foreach (var moved in new[]
                 { "DockLayout", "TreeViewState", "OpenDocuments", "ActiveDocumentPath", "ColorSchemeName" })
            Assert.False(cwsJson.ContainsKey(moved), $"{moved} must be dropped from the .cws by the first save");

        AssertSessionStateMatches(original, WorkspacePersistence.LoadFromFile(_cws));
    }

    // ── Gate 6: a read-only workspace writes NEITHER file ─────────────────────

    [Fact]
    public void AReadOnlyWorkspace_WritesNeitherHalf()
    {
        // A workspace that already has both halves on disk, written while it was writable.
        Assert.True(WorkspacePersistence.SaveToFileAtomic(_cws, Everything()));
        string cwsBefore     = File.ReadAllText(_cws);
        string sidecarBefore = File.ReadAllText(_sidecar);

        // SL2's own seam, which is what makes this test run identically on all three platforms.
        WorkspaceWritability.WritabilityProbe = _ => false;

        var changed = Everything();
        changed.ColorSchemeName    = "something-different";
        changed.ActiveDocumentPath = "elsewhere.csch";
        changed.DefaultTechRef     = "tech/other.ctech";

        Assert.False(WorkspacePersistence.SaveToFileAtomic(_cws, changed));

        Assert.Equal(cwsBefore,     File.ReadAllText(_cws));
        Assert.Equal(sidecarBefore, File.ReadAllText(_sidecar));
    }

    [Fact]
    public void AReadOnlyWorkspace_DoesNotGainASidecarItNeverHad()
    {
        File.WriteAllText(_cws, "{\"FormatVersion\":2}");
        WorkspaceWritability.WritabilityProbe = _ => false;

        Assert.False(WorkspacePersistence.SaveToFileAtomic(_cws, Everything()));
        Assert.False(File.Exists(_sidecar));
    }

    // ── Gate 7: the choke point is the only writer ────────────────────────────

    /// <summary>
    /// <b>A `.cwsuser` write anywhere but the choke point is the failure this gate exists for.</b>
    /// SL2 made `SaveToFileAtomic` the single `.cws` writer because a rule enforced by eighteen
    /// callers agreeing is a rule that is true in seventeen places and found by a user in the
    /// eighteenth; the split inherits that only for as long as nothing else writes the second file.
    ///
    /// <para>Comment-stripped, because a source scan satisfied by its own documentation has caught
    /// this repo before.</para>
    /// </summary>
    [Fact]
    public void NothingOutsideThePersistenceChokePoint_WritesTheSidecar()
    {
        string root = RepoRoot();

        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            string code = StripComments(File.ReadAllText(file));

            if (name != "WorkspacePersistence.cs")
                Assert.False(code.Contains("WorkspaceUserPersistence.Save(", StringComparison.Ordinal),
                    $"{name} writes the .cwsuser; only WorkspacePersistence may.");

            // A hard-coded literal beside a write call is how a second writer would actually arrive —
            // not by calling the one function that is already named after the thing.
            if (name != "WorkspaceUserPersistence.cs" &&
                code.Contains(".cwsuser", StringComparison.OrdinalIgnoreCase))
                Assert.False(code.Contains("WriteAllText", StringComparison.Ordinal),
                    $"{name} names .cwsuser and writes a file; route it through WorkspacePersistence.");
        }
    }

    [Fact]
    public void TheSidecarWriter_IsNotReachableFromAnotherAssembly()
    {
        var save = typeof(WorkspaceUserPersistence)
            .GetMethod("Save", System.Reflection.BindingFlags.Static |
                               System.Reflection.BindingFlags.NonPublic |
                               System.Reflection.BindingFlags.Public);

        Assert.NotNull(save);
        Assert.False(save!.IsPublic, "WorkspaceUserPersistence.Save must stay internal to CircuitRF.Design");
    }

    // ── Gate 8: the type is registered to all three operating systems ─────────

    /// <summary>
    /// The three parity tests in <c>WBondStandaloneTests</c> already assert the general rule — every
    /// type declared to an operating system has a case in <c>OpenFiles</c> and vice versa. This one
    /// names <c>.cwsuser</c> specifically, so RC-1's own requirement fails loudly rather than as a
    /// mismatch between three lists that all happen to be missing it.
    /// </summary>
    [Fact]
    public void TheSidecarIsRegisteredOnAllThreePlatforms_UnderTheWorkspaceTypeItAlreadyHas()
    {
        string root = RepoRoot();

        // macOS: the same UTI, not a second one — double-clicking either half means one thing.
        string plist = File.ReadAllText(Path.Combine(root, "src", "Ui", "Assets", "macOS", "Info.plist"));
        Assert.Contains("<string>cwsuser</string>", plist);

        // Windows: the same ProgId, so Explorer runs the one Open verb the workspace already has.
        var wxs = XDocument.Parse(File.ReadAllText(Path.Combine(root, "packaging", "windows", "circuitRF.wxs")));
        var ext = wxs.Descendants().Single(e => e.Name.LocalName == "Extension" &&
                                                e.Attribute("Id")?.Value == "cwsuser");
        Assert.Equal("circuitRF.Workspace", ext.Parent!.Attribute("Id")!.Value);

        // Linux: a third GLOB on the existing mime-type. A <mime-type> is a container of globs, so
        // this needs no second type and therefore no second MimeType= entry in the .desktop file —
        // which is what keeps TheDesktopEntryAndTheMimeFileClaimExactlyTheSameTypes green.
        var mime = XDocument.Parse(File.ReadAllText(Path.Combine(root, "packaging", "linux", "circuitrf-mime.xml")));
        var glob = mime.Descendants().Single(e => e.Name.LocalName == "glob" &&
                                                  e.Attribute("pattern")?.Value == "*.cwsuser");
        Assert.Equal("application/x-circuitrf-workspace", glob.Parent!.Attribute("type")!.Value);
    }

    // ── Gate 9: archived by neither, copied by Save Workspace As ──────────────

    /// <summary>
    /// <b>Both halves are asserted, because the ASYMMETRY is the requirement.</b> A test for only the
    /// archive would pass on a shared skip list — and a shared skip list is exactly what breaks the
    /// copy. This is the reverse of `.git`, where both consumers want the same answer, so the next
    /// person to tidy the two predicates into one has a red test rather than a silent change to
    /// whichever half they were not thinking about.
    /// </summary>
    [Fact]
    public void TheSidecarIsExcludedFromAnArchive_AndKeptInASaveWorkspaceAsCopy()
    {
        Assert.True(WorkspacePersistence.SaveToFileAtomic(_cws, Everything()));
        Assert.True(File.Exists(_sidecar));

        // The archive leaves it out, whatever the user ticks.
        var plan = WorkspaceArchiveScanner.Scan(_ws);
        Assert.DoesNotContain(plan.AlwaysIncluded, p => p.EndsWith(".cwsuser", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Options,        o => o.ArchivePath.EndsWith(".cwsuser", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.SkippedPaths,         p => p.EndsWith(".cwsuser", StringComparison.OrdinalIgnoreCase));

        // The copy keeps it: the same person's own workspace on the same machine, which the window
        // then switches to.
        string dest = Path.Combine(_root, "workspaceB");
        WorkspaceCopy.Run(_ws, dest);
        Assert.True(File.Exists(Path.Combine(dest, ".cwsuser")),
            "Save Workspace As must carry the panel arrangement — nothing is bought by dropping it.");
        Assert.True(File.Exists(Path.Combine(dest, ".cws")));
    }

    [Fact]
    public void TheTwoSkipPredicates_DisagreeAboutTheSidecarAndAgreeAboutEverythingElse()
    {
        Assert.False(WorkspaceArchiveScanner.IsSkipped(".cwsuser"));
        Assert.True(WorkspaceArchiveScanner.IsSkippedFromArchive(".cwsuser"));

        foreach (var shared in new[]
                 { ".DS_Store", ".crf-open.json", "Amp/schematic/Amp.csch.tmp", "Amp/schematic/Amp.csch" })
            Assert.Equal(WorkspaceArchiveScanner.IsSkipped(shared),
                         WorkspaceArchiveScanner.IsSkippedFromArchive(shared));
    }

    // ── The delete-on-empty rule that keeps the split behaviour-preserving ────

    /// <summary>
    /// Saving a configuration with no session state in it clears the sidecar, exactly as it used to
    /// clear those fields out of the `.cws`. Without this the two halves would disagree about
    /// whether a layout exists, and a "close every tab, then save" would reopen the closed tabs.
    /// </summary>
    [Fact]
    public void SavingWithNoSessionState_RemovesTheSidecarRatherThanLeavingAStaleOne()
    {
        Assert.True(WorkspacePersistence.SaveToFileAtomic(_cws, Everything()));
        Assert.True(File.Exists(_sidecar));

        Assert.True(WorkspacePersistence.SaveToFileAtomic(_cws, new CwsFile { DefaultTechRef = "tech/pcb.ctech" }));

        Assert.False(File.Exists(_sidecar));
        Assert.Null(WorkspacePersistence.LoadFromFile(_cws).DockLayout);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string RepoFile(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));
}
