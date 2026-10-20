using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Design.Revision;
using CircuitRF.Ui.Archive;
using Xunit;

namespace CircuitRF.Ui.Tests.Revision;

/// <summary>
/// RC-8's gates — <c>docs/sonnet-briefs/brief-revision-control-8-archive-with-history.md</c> §5,
/// <c>docs/design/revision-control.md</c> §9A.
///
/// <para><b>Every fixture path here is the SHAPE of a path, never a real one.</b> The whole point of
/// this brief is a file nobody meant to send; a fixture named after a real customer's part would be
/// that failure committed into this repository on purpose.</para>
///
/// <para>In <see cref="AppDataRootCollection"/> with RC-3's own gates: the git-environment isolation
/// these tests install is process-wide.</para>
/// </summary>
[Collection(AppDataRootCollection.Name)]
public class ArchiveWithHistoryTests
{
    // ── Gate 1: the default is off ────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc8-1, R-rc8-2. <b>Off, on a workspace that has a history, with nothing remembered that could
    /// flip it.</b>
    ///
    /// <para>Every other failure the architecture guards against is a LOSS and the design survives it.
    /// This one is disclosure and it cannot be undone by anyone at any later time — so when a default
    /// can be wrong in two directions, it points away from the irreversible one.</para>
    /// </summary>
    [GitFact]
    public void HistoryIsOfferedAndIsOffByDefault()
    {
        using var ws = ArmedWorkspace();
        TakeSavePoint(ws, "a version");

        var plan = WorkspaceArchiveScanner.Scan(ws.Root);

        Assert.True(plan.HistoryAvailable);           // it is offered…
        Assert.False(plan.IncludeHistory);            // …and it starts off
        Assert.Null(plan.History);                    // nothing computed until it is ticked

        // A second scan of the same workspace answers the same way: nothing remembers the choice.
        Assert.False(WorkspaceArchiveScanner.Scan(ws.Root).IncludeHistory);
    }

    /// <summary>A workspace circuitRF has never recorded offers nothing at all.</summary>
    [Fact]
    public void AWorkspaceWithNoHistoryOffersNothing()
    {
        using var ws = new GitWorkspace();
        ws.Write("cells/a/thing.csch", "{}");

        var plan = WorkspaceArchiveScanner.Scan(ws.Root);
        Assert.False(plan.HistoryAvailable);
        Assert.False(plan.IncludeHistory);
    }

    // ── Gate 2: excluded produces exactly today's archive ─────────────────────────────────────────

    /// <summary>
    /// <b>The strongest available statement that the default changed nothing</b>: the archive of a
    /// workspace that has a history, with the box left alone, is entry for entry and byte for byte the
    /// archive of the same workspace before it had one.
    ///
    /// <para>Compared as entries and contents rather than as two files, because a zip stamps each
    /// entry with the moment it was written and two runs a millisecond apart are never the same
    /// bytes.</para>
    /// </summary>
    [GitFact]
    public void WithTheBoxLeftAloneTheArchiveIsExactlyTodaysArchive()
    {
        using var ws = new GitWorkspace();
        ws.Write("cells/amp/amp.csch", "{\"Components\":[]}");
        ws.Write("cells/amp/amp.csym", "{}");
        ws.Write("results/run.s2p", "! touchstone\n");

        // The archive as it was BEFORE this workspace had any history at all.
        string before = Path.Combine(ws.Root + "-out", "before.zip");
        WorkspaceArchiveWriter.Write(WorkspaceArchiveScanner.Scan(ws.Root), before);

        // Now give it one — several restore points and a version.
        Arm(ws);
        for (int i = 0; i < 3; i++)
        {
            ws.Write("cells/amp/amp.csch", $"{{\"Components\":[],\"rev\":{i}}}");
            TakeSavePoint(ws, $"edit {i}");
        }
        Assert.True(WorkspaceCommit.Commit(ws.Git(), "the one I am sending").Ok);

        // Put the file back exactly as the first archive saw it, so the comparison is of the ARCHIVE
        // and not of the edits made to create a history.
        ws.Write("cells/amp/amp.csch", "{\"Components\":[]}");

        string after = Path.Combine(ws.Root + "-out", "after.zip");
        var plan = WorkspaceArchiveScanner.Scan(ws.Root);
        Assert.True(plan.HistoryAvailable);
        Assert.False(plan.IncludeHistory);
        WorkspaceArchiveWriter.Write(plan, after);

        // Entry for entry and byte for byte — EXCEPT the two policy files RC-3 wrote into the
        // workspace when it armed it. Those are ordinary, visible files in the folder the scanner
        // walks, they were there before the dialog opened, and they are not the history; excluding
        // them from the comparison is what makes it a statement about THIS brief's default rather
        // than about RC-3's.
        var policy = new[] { WorkspacePolicyFiles.GitIgnoreName, WorkspacePolicyFiles.GitAttributesName };
        Assert.Equal(EntryContents(before, policy), EntryContents(after, policy));

        // And nothing of the repository is in there under any name.
        Assert.DoesNotContain(EntryNames(after), n => n.Contains("/.git/", StringComparison.Ordinal));
    }

    // ── Gate 3: included round-trips, restore points and all ─────────────────────────────────────

    /// <summary>
    /// R-rc8-5, R-rc8-5a. <b>Archive, extract, and every restore point and every version the sender
    /// had is present and restorable.</b>
    ///
    /// <para>The restore points are what distinguishes this journey from every other way a workspace
    /// leaves a machine: the archive copies the repository DIRECTORY, so it takes circuitRF's own
    /// per-checkpoint references with it. A copy carries no history at all, and a fetch over a network
    /// would take branches and tags and nothing under a private namespace.</para>
    /// </summary>
    [GitFact]
    public void IncludedRoundTripsWithEveryRestorePointAndEveryVersion()
    {
        using var ws = ArmedWorkspace();

        for (int i = 0; i < 4; i++)
        {
            ws.Write("cells/amp/amp.csch", $"rev {i}");
            TakeSavePoint(ws, $"edit {i}");
        }
        Assert.True(WorkspaceCommit.Commit(ws.Git(), "what I am sending").Ok);

        var sentPoints   = RestorePoints.List(ws.Git()).Select(p => p.CommitId).OrderBy(s => s).ToList();
        var sentVersions = HistoryBrowser.Versions(ws.Git()).Select(v => v.CommitId).OrderBy(s => s).ToList();
        Assert.Equal(4, sentPoints.Count);
        Assert.NotEmpty(sentVersions);

        var extracted = ArchiveAndExtract(ws, includeHistory: true);

        // The recipient's workspace IS a repository at its own root — §9A.5's first row of §12 Q4's
        // table, adopted without ceremony.
        var theirs = GitCommand.For(extracted)!;
        Assert.True(theirs.IsRepositoryRoot());
        Assert.True(GitRepository.IsManagedByCircuitRf(theirs));

        Assert.Equal(sentPoints,   RestorePoints.List(theirs).Select(p => p.CommitId).OrderBy(s => s));
        Assert.Equal(sentVersions, HistoryBrowser.Versions(theirs).Select(v => v.CommitId).OrderBy(s => s));

        // Restorable, not merely listed: the oldest restore point writes its own state back.
        var oldest = RestorePoints.List(theirs).OrderBy(p => p.Sequence).First();
        var restored = WorkspaceRestore.Restore(theirs, oldest);
        Assert.True(restored.Ok);
        Assert.Equal("rev 0", File.ReadAllText(Path.Combine(extracted, "cells", "amp", "amp.csch")));
    }

    /// <summary>
    /// <b>The empty directories have to travel, and the pack R-rc8-9 requires is what creates
    /// them.</b>
    ///
    /// <para>git decides a folder is a repository by finding <c>objects</c> and <c>refs</c> inside it.
    /// <c>git gc</c> packs every loose reference into <c>packed-refs</c> and leaves <c>refs/heads</c>
    /// and <c>refs/tags</c> empty behind it — and a zip stores files, not folders. Verified rather than
    /// assumed: a repository whose <c>refs</c> directory is missing answers <i>fatal: not a git
    /// repository</i>, with nothing else having gone wrong.</para>
    /// </summary>
    [GitFact]
    public void TheRepositorysEmptyDirectoriesTravelAsDirectoryEntries()
    {
        using var ws = ArmedWorkspace();
        ws.Write("cells/amp/amp.csch", "one");
        TakeSavePoint(ws, "one");

        var plan = Prepared(ws);
        string zip = Path.Combine(ws.Root + "-out", "with.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        // The pack emptied them, so they exist only as directory entries.
        using (var archive = ZipFile.OpenRead(zip))
        {
            var dirs = archive.Entries.Select(e => e.FullName).Where(n => n.EndsWith('/')).ToList();
            Assert.Contains(dirs, n => n.EndsWith("/.git/refs/heads/", StringComparison.Ordinal));
        }

        string extracted = Extract(zip, ws.Root + "-in");
        Assert.True(Directory.Exists(Path.Combine(extracted, ".git", "refs")));
        Assert.Equal(0, RawIn(extracted, "rev-parse", "--show-prefix").Code);
    }

    /// <summary>
    /// A lock file is the momentary state of a write happening right now. One copied into an archive
    /// arrives as a repository the recipient's git refuses to write to, with no cause they could find.
    /// </summary>
    [GitFact]
    public void ALockFileLeftBehindDoesNotTravel()
    {
        using var ws = ArmedWorkspace();
        ws.Write("cells/amp/amp.csch", "one");
        TakeSavePoint(ws, "one");

        File.WriteAllText(Path.Combine(ws.Root, ".git", "index.lock"), "");

        Assert.DoesNotContain(HistoryArchive.RepositoryFiles(ws.Root),
                              f => Path.GetFileName(f) == "index.lock");
    }

    // ── Gate 4: a foreign-owned extraction reports circuitRF's sentence ───────────────────────────

    /// <summary>
    /// R-rc8-5b. <b>The most likely real-world first impression of an archive with history</b>: the
    /// recipient extracts it and the tree is owned by an account git does not consider theirs, which is
    /// the ordinary state of a network share and of an archive somebody else unpacked.
    ///
    /// <para>Two halves, because the answer has two halves. <b>circuitRF names the workspace root as an
    /// exception on every invocation</b> (§4.7), so on a git at or above the floor the failure never
    /// arises at all and the history simply works — which is the outcome, and the first assertion.
    /// <b>Where it does arise</b>, on a git that predates the option, git's own words are replaced by
    /// circuitRF's, which is the second.</para>
    ///
    /// <para>Foreign ownership is simulated through git's own test switch rather than by creating a
    /// second account, which a test cannot do. The switch makes git take exactly the branch it takes
    /// for a genuinely foreign tree.</para>
    /// </summary>
    [GitFact]
    public void AnExtractionGitConsidersForeignOwnedStillWorksAndOtherwiseSpeaksCircuitRfs()
    {
        using var ws = ArmedWorkspace();
        ws.Write("cells/amp/amp.csch", "one");
        TakeSavePoint(ws, "one");

        string extracted = ArchiveAndExtract(ws, includeHistory: true);

        string? previous = Environment.GetEnvironmentVariable("GIT_TEST_ASSUME_DIFFERENT_OWNER");
        Environment.SetEnvironmentVariable("GIT_TEST_ASSUME_DIFFERENT_OWNER", "1");
        try
        {
            var theirs = GitCommand.For(extracted)!;

            // The exception circuitRF names is what makes this a non-event.
            Assert.True(theirs.IsRepositoryRoot());
            Assert.Single(RestorePoints.List(theirs));

            // Without it — a git old enough to lack the option — the refusal is translated. An empty
            // value resets git's own list, which is the closest a test can get to that git.
            var refused = theirs.Run(["-c", "safe.directory=", "rev-parse", "--show-prefix"], null);
            Assert.False(refused.Ok);

            var diagnostic = GitFailures.Translate(refused, "opening this workspace", extracted);
            Assert.Equal("revision.ownership.foreign", diagnostic.Id);
            Assert.DoesNotContain("safe.directory", diagnostic.Render(), StringComparison.Ordinal);
            Assert.DoesNotContain("dubious", diagnostic.Render(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("different account", diagnostic.Render(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_TEST_ASSUME_DIFFERENT_OWNER", previous);
        }
    }

    // ── Gate 5: the deleted-file list is real ────────────────────────────────────────────────────

    /// <summary>
    /// R-rc8-7, R-rc8-8. <b>A file added, kept as a version, deleted, kept again — and the archive
    /// dialog names it.</b>
    ///
    /// <para>This is the single most effective warning available, and it is the reason the whole
    /// computed block exists: it converts an abstract worry into a filename the designer recognises.
    /// Nobody recognises "the repository contains historical objects".</para>
    /// </summary>
    [GitFact]
    public void AFileDeletedFromTheWorkspaceIsNamedInTheDialogsText()
    {
        using var ws = ArmedWorkspace();
        ws.Write("cells/amp/amp.csch", "one");
        ws.Write("imports/other-party-outline.gds", "artwork that came from somewhere else");
        Assert.True(WorkspaceCommit.Commit(ws.Git(), "with the import").Ok);

        File.Delete(ws.File_("imports/other-party-outline.gds"));
        Assert.True(WorkspaceCommit.Commit(ws.Git(), "finished with it").Ok);

        var summary = Summarise(ws);

        Assert.Contains("imports/other-party-outline.gds", summary.DeletedPaths);
        Assert.Contains(summary.Describe(),
                        line => line.Contains("other-party-outline.gds", StringComparison.Ordinal));

        // A file that is still there is not in the list, and neither is the FOLDER a deleted file left
        // behind — a tree named as though it were the customer's artwork would be a false alarm in
        // exactly the place a false alarm is most expensive.
        Assert.DoesNotContain("cells/amp/amp.csch", summary.DeletedPaths);
        Assert.DoesNotContain("imports", summary.DeletedPaths);
    }

    /// <summary>
    /// R-rc8-7a, gate 5a. <b>A file that only ever existed inside a restore point is named too — and a
    /// branch-only enumeration passes the gate above and fails this one.</b>
    ///
    /// <para>The archive copies the repository directory, so it carries circuitRF's per-checkpoint
    /// references; and a restore point captures every file the workspace holds, including ones the
    /// designer never deliberately kept and may never have opened. <b>That is exactly the population
    /// this warning exists for</b> — a deliberately kept file is one the designer already knows about.
    /// Walking the branch alone produces a warning that is nearly true, and a near-truth is what people
    /// act on.</para>
    /// </summary>
    [GitFact]
    public void AFileThatOnlyEverExistedInsideARestorePointIsNamed()
    {
        using var ws = ArmedWorkspace();
        ws.Write("cells/amp/amp.csch", "one");
        Assert.True(WorkspaceCommit.Commit(ws.Git(), "the design").Ok);

        // Never committed — only ever inside restore points.
        ws.Write("imports/only-in-a-restore-point.gds", "artwork nobody kept on purpose");
        TakeSavePoint(ws, "before I try something");

        File.Delete(ws.File_("imports/only-in-a-restore-point.gds"));
        TakeSavePoint(ws, "after");

        var summary = Summarise(ws);
        Assert.Contains("imports/only-in-a-restore-point.gds", summary.DeletedPaths);

        // The branch alone does not know about it, which is what makes this gate separate.
        Assert.DoesNotContain(BranchOnlyPaths(ws), p => p.Contains("only-in-a-restore-point"));
    }

    /// <summary>
    /// R-rc8-9, gate 5b. <b>A file that only ever existed inside a restore point retention has since
    /// THINNED is named too.</b>
    ///
    /// <para>Thinning drops a reference and frees nothing: packing never reclaims, and nothing reclaims
    /// unless a person asks. So the objects are still in the directory the archive copies — and a
    /// live-references-only enumeration would miss exactly them, which is the same nearly-true warning
    /// by a different route.</para>
    /// </summary>
    [GitFact]
    public void AFileInsideAThinnedRestorePointIsNamedToo()
    {
        using var ws = ArmedWorkspace();
        ws.Write("cells/amp/amp.csch", "one");
        Assert.True(WorkspaceCommit.Commit(ws.Git(), "the design").Ok);

        // Recorded at a CLOSE, not as a save-point: a save-point is a judgement the designer made and
        // retention may never thin one (R-rc5-1f), so a fixture built out of save-points cannot reach
        // the state this gate is about.
        ws.SetClock(DateTimeOffset.UtcNow - TimeSpan.FromDays(400));
        ws.Write("imports/only-in-a-thinned-point.gds", "artwork nobody kept on purpose");
        Take(ws, CheckpointOrigin.WorkspaceClosed, "before I try something");
        File.Delete(ws.File_("imports/only-in-a-thinned-point.gds"));
        Take(ws, CheckpointOrigin.WorkspaceClosed, "after");

        ws.SetClock(DateTimeOffset.UtcNow);
        for (int i = 0; i < 8; i++)
        {
            ws.Write("cells/amp/amp.csch", $"rev {i}");
            Take(ws, CheckpointOrigin.WorkspaceClosed, $"edit {i}");
        }

        // Repeated, because one pass is bounded to a fraction of the list by design.
        for (int pass = 0; pass < 5; pass++) RetentionSweep.Run(ws.Git(), new RetentionPolicy(30, 2));

        var live = RestorePoints.List(ws.Git()).Select(p => p.CommitId).ToList();
        var thinned = ThinningJournal.Read(ws.Root);
        Assert.NotEmpty(thinned);
        Assert.DoesNotContain(thinned[0].CommitId, live);

        var summary = Summarise(ws);
        Assert.Contains("imports/only-in-a-thinned-point.gds", summary.DeletedPaths);
    }

    /// <summary>
    /// R-rc8-8. <b>A long list shows the first few AND the count — never a number alone.</b> The names
    /// are the whole mechanism; a bare count is the sentence nobody recognises.
    /// </summary>
    [GitFact]
    public void ALongListIsNamedThenCountedAndNeverJustCounted()
    {
        using var ws = ArmedWorkspace();
        for (int i = 0; i < 12; i++) ws.Write($"imports/outline-{i:00}.gds", $"artwork {i}");
        Assert.True(WorkspaceCommit.Commit(ws.Git(), "with the imports").Ok);

        Directory.Delete(ws.File_("imports"), recursive: true);
        ws.Write("cells/amp/amp.csch", "still here");
        Assert.True(WorkspaceCommit.Commit(ws.Git(), "finished with them").Ok);

        var summary = Summarise(ws);
        Assert.Equal(12, summary.DeletedPaths.Count);

        string line = Assert.Single(summary.Describe(),
                                    l => l.Contains("not in the workspace now", StringComparison.Ordinal));
        Assert.Contains("outline-00.gds", line, StringComparison.Ordinal);
        Assert.Contains("12 files", line, StringComparison.Ordinal);
        Assert.Contains("more", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// R-rc8-8, R-rc8-11. <b>Two deleted files whose CONTENT is identical are both named.</b>
    ///
    /// <para>This is the bug the enumeration was first written with, and it is the exact shape §9A.4
    /// rejects: an object walk prints each object once, so seventeen copies of one imported outline
    /// were reported as one filename. The sender reads the one name, recognises it, decides it is
    /// fine — and sixteen they never saw go out with it. <b>A warning that names some of the files is
    /// worse than none, because the near-truth is what people act on.</b></para>
    /// </summary>
    [GitFact]
    public void TwoDeletedFilesWithIdenticalContentAreBothNamed()
    {
        using var ws = ArmedWorkspace();
        ws.Write("cells/amp/amp.csch", "one");
        ws.Write("imports/outline-a.gds", "the same bytes in both");
        ws.Write("imports/outline-b.gds", "the same bytes in both");
        Assert.True(WorkspaceCommit.Commit(ws.Git(), "with the imports").Ok);

        Directory.Delete(ws.File_("imports"), recursive: true);
        Assert.True(WorkspaceCommit.Commit(ws.Git(), "finished with them").Ok);

        var summary = Summarise(ws);
        Assert.Contains("imports/outline-a.gds", summary.DeletedPaths);
        Assert.Contains("imports/outline-b.gds", summary.DeletedPaths);
    }

    /// <summary>
    /// R-rc8-3, R-rc8-5a. <b>What the box says must not read as a warning against its own feature</b> —
    /// including the history is the better handover, and the one line about restore points is what a
    /// designer comparing this with a copy would otherwise have to guess.
    /// </summary>
    [GitFact]
    public void TheComputedBlockStatesTheGainAndNamesTheRestorePoints()
    {
        using var ws = ArmedWorkspace();
        ws.Write("cells/amp/amp.csch", "one");
        TakeSavePoint(ws, "one");
        TakeSavePointAfterEdit(ws, "two");
        Assert.True(WorkspaceCommit.Commit(ws.Git(), "the one I am sending").Ok);

        var lines = Summarise(ws).Describe();

        Assert.Contains(lines, l => l.Contains("2 restore points", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("1 version", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Restore points travel only this way", StringComparison.Ordinal));

        // No git vocabulary reaches a designer who did not ask for it (R-rc0-6).
        foreach (string word in (string[])["git", "commit", "branch", "HEAD", "checkout", "repository"])
            Assert.DoesNotContain(word, string.Join(' ', lines), StringComparison.OrdinalIgnoreCase);
    }

    // ── Gate 6: the size figure is post-pack, and it is what the archive spends ───────────────────

    /// <summary>
    /// R-rc8-9. <b>Packing runs before the figure is computed, and the figure matches the archive
    /// actually written.</b>
    ///
    /// <para>An unpacked repository can be ten times its real size — that is RC-3's measured overhang —
    /// and an archive is exactly where that surprises someone. A figure taken before the pack describes
    /// an archive nobody is going to get.</para>
    /// </summary>
    [GitFact]
    public void TheSizeFigureIsTakenAfterPackingAndIsWhatTheArchiveSpends()
    {
        using var ws = ArmedWorkspace();

        for (int i = 0; i < 12; i++)
        {
            ws.Write("cells/amp/amp.clay", new string((char)('a' + i % 26), 200_000));
            TakeSavePoint(ws, $"edit {i}");
        }

        long loose = Directory.Exists(Path.Combine(ws.Root, ".git", "objects"))
            ? Directory.EnumerateDirectories(Path.Combine(ws.Root, ".git", "objects"))
                       .Count(d => Path.GetFileName(d)!.Length == 2)
            : 0;
        Assert.True(loose > 0, "the fixture is meant to leave loose objects for the pack to fold up");

        var plan = Prepared(ws);

        // The pack ran: the loose objects are gone, and a pack file is there instead.
        Assert.DoesNotContain(Directory.EnumerateDirectories(Path.Combine(ws.Root, ".git", "objects")),
                              d => Path.GetFileName(d)!.Length == 2);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(ws.Root, ".git", "objects", "pack"), "*.pack"));

        string zip = Path.Combine(ws.Root + "-out", "with.zip");
        var result = WorkspaceArchiveWriter.Write(plan, zip);

        Assert.True(result.HistoryIncluded);
        Assert.Equal(plan.History!.Bytes, result.HistoryBytes);
        Assert.True(result.HistoryBytes > 0);

        // And the dialog's own running total is the one the archive spends.
        Assert.Equal(plan.AlwaysIncludedBytes + plan.History.Bytes, plan.SelectedBytes);
    }

    // ── Gate 7: no .cwsuser, in either state ─────────────────────────────────────────────────────

    /// <summary>
    /// R-rc8-4. <b>The exclusion is RC-1's and this is a regression check on it</b>, not the thing that
    /// builds it — it is one person's panel arrangement, and the recipient opens on the default.
    /// </summary>
    [GitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePerUserFileIsNeverArchivedInEitherState(bool includeHistory)
    {
        using var ws = ArmedWorkspace();
        ws.Write("cells/amp/amp.csch", "one");
        ws.Write(".cwsuser", "{\"DockLayout\":\"…\"}");
        TakeSavePoint(ws, "one");

        var plan = includeHistory ? Prepared(ws) : WorkspaceArchiveScanner.Scan(ws.Root);
        string zip = Path.Combine(ws.Root + "-out", $"{includeHistory}.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        Assert.DoesNotContain(EntryNames(zip),
                              n => n.EndsWith("/.cwsuser", StringComparison.OrdinalIgnoreCase));
    }

    // ── Gate 8: there is no third option ─────────────────────────────────────────────────────────

    /// <summary>
    /// R-rc8-11. <b>"Include only the last N versions" and "history from this date forward" are not
    /// built, and a source scan is what keeps them from being added quietly.</b>
    ///
    /// <para>They sound like the best of both. They are history rewriting — the identities stop
    /// matching the sender's — and worse, they invite exactly the wrong belief: a user who chose "the
    /// last ten" to avoid sending something will assume the something is gone. A filter that silently
    /// kept one referenced object, or missed a path renamed before it was deleted, would be a leak the
    /// user had explicitly tried to prevent and been told was handled. <b>A guarantee that is nearly
    /// true is worse than no guarantee.</b></para>
    ///
    /// <para>Comments stripped first, because this file and the ones it scans say all of the above in
    /// prose and a scan that read the prose would fail on its own explanation.</para>
    /// </summary>
    [Fact]
    public void NoVersionCountOrDateFilterExistsOnTheArchivePath()
    {
        string[] files =
        [
            "src/Ui/Archive/WorkspaceArchivePlan.cs",
            "src/Ui/Archive/WorkspaceArchiveScanner.cs",
            "src/Ui/Archive/WorkspaceArchiveWriter.cs",
            "src/Ui/Archive/ArchiveHistoryPreparation.cs",
            "src/Design/Revision/HistoryArchive.cs",
            "src/Ui/Views/Dialogs/ArchiveWorkspaceDialog.axaml.cs",
        ];

        // A count or a date reaching git's own history selection is the shape of the third option —
        // there is no honest use for any of them on this path.
        var forbidden = new Regex(
            @"--max-count|--since|--until|--after|--before|--shallow|--depth|MaxVersions|VersionLimit|HistorySince|HistoryFrom",
            RegexOptions.IgnoreCase);

        foreach (string relative in files)
        {
            string text = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), relative)));
            var hit = forbidden.Match(text);
            Assert.False(hit.Success, $"{relative} names '{hit.Value}' on the archive path — RC-8 R-rc8-11 excludes the third option");
        }
    }

    // ── Gate 9: a referenced workspace's repository never enters the archive ─────────────────────

    /// <summary>
    /// R-rc8-10. <b>Referenced cells brought in by the archive scan carry no history</b>, even when the
    /// reference is editable. They come from another workspace with its own repository, and the
    /// one-workspace-one-history rule holds in this direction too: <b>an archive carries files, never
    /// somebody else's repository.</b>
    /// </summary>
    [GitFact]
    public void AReferencedWorkspacesOwnRepositoryNeverTravels()
    {
        using var other = new GitWorkspace();
        other.Write("cells/shared/shared.csch", "{\"Components\":[]}");
        other.Write("cells/shared/shared.csym", "{}");
        Directory.CreateDirectory(Path.Combine(other.Root, ".git", "objects"));
        File.WriteAllText(Path.Combine(other.Root, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(other.Root, ".git", "config"), "[core]\n\trepositoryformatversion = 0\n");

        using var ws = ArmedWorkspace();
        ws.Write(".cws", "{\"ReferencedWorkspaces\":[{\"Alias\":\"lib\",\"Path\":\"" +
                          other.Root.Replace("\\", "\\\\") + "\",\"ReadOnly\":true}]}");
        ws.Write("cells/amp/amp.csch", "{\"Components\":[{\"CellRef\":\"lib://shared\"}]}");
        TakeSavePoint(ws, "one");

        var plan = Prepared(ws);
        string zip = Path.Combine(ws.Root + "-out", "with.zip");
        WorkspaceArchiveWriter.Write(plan, zip);

        var names = EntryNames(zip);

        // Ours travels…
        Assert.Contains(names, n => n.EndsWith("/.git/HEAD", StringComparison.Ordinal));
        // …and there is exactly one repository in the whole archive: ours, at the root.
        var repositories = names.Where(n => n.Contains("/.git/", StringComparison.Ordinal))
                                .Select(n => n[..(n.IndexOf("/.git/", StringComparison.Ordinal))])
                                .Distinct()
                                .ToList();
        Assert.Single(repositories);
        Assert.DoesNotContain("refs/", repositories[0], StringComparison.Ordinal);
    }

    // ── The exclusion RC-5 built is the one that still enforces the default ──────────────────────

    /// <summary>
    /// <b>One exclusion serving both states is the correct shape; two would be the bug.</b> RC-5's
    /// <c>.git</c> skip is untouched by this brief — including is an ADDITION on the writer, reached
    /// only through the plan's own flag, so the default is still enforced in one place and
    /// <c>Save Workspace As</c> is untouched by this feature existing.
    /// </summary>
    [Fact]
    public void TheOneExclusionStillRefusesTheRepositoryForBothConsumers()
    {
        Assert.True(WorkspaceArchiveScanner.IsSkipped(".git/HEAD"));
        Assert.True(WorkspaceArchiveScanner.IsSkipped(".git/objects/pack/pack-abc.pack"));
        Assert.True(WorkspaceArchiveScanner.IsSkippedFromArchive(".git/HEAD"));
        Assert.Equal(".git", WorkspaceArchiveScanner.RepositoryFolderName);
        Assert.Equal(WorkspaceArchiveScanner.RepositoryFolderName, HistoryArchive.RepositoryFolderName);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    private static GitWorkspace ArmedWorkspace()
    {
        var ws = new GitWorkspace();
        Arm(ws);
        return ws;
    }

    private static void Arm(GitWorkspace ws)
    {
        File.WriteAllText(ws.GlobalConfig,
            "[user]\n\tname = A Designer\n\temail = designer@example.invalid\n");
        Assert.True(WorkspaceArming.Arm(ws.Root, CheckpointOrigin.SavePoint, true, null, true).Armed);
    }

    private static void Take(GitWorkspace ws, CheckpointOrigin origin, string label)
        => Assert.True(WorkspaceCheckpoints.Take(ws.Git(), origin, label, attended: true).Recorded);

    private static void TakeSavePoint(GitWorkspace ws, string label, bool attended = true)
        => Assert.True(WorkspaceCheckpoints.Take(ws.Git(), CheckpointOrigin.SavePoint, label, attended).Recorded);

    private static void TakeSavePointAfterEdit(GitWorkspace ws, string label)
    {
        ws.Write("cells/amp/amp.csch", label);
        TakeSavePoint(ws, label);
    }

    /// <summary>A scanned plan with the box ticked and the figures computed — what the dialog holds
    /// once the user has ticked it and the preparation has finished.</summary>
    private static WorkspaceArchivePlan Prepared(GitWorkspace ws)
    {
        var plan = WorkspaceArchiveScanner.Scan(ws.Root);
        plan.IncludeHistory = true;
        Assert.NotNull(ArchiveHistoryPreparation.Prepare(plan));
        return plan;
    }

    private static HistoryArchiveSummary Summarise(GitWorkspace ws) => Prepared(ws).History!;

    /// <summary>What a BRANCH-ONLY enumeration would have found — the thing gates 5a and 5b exist to
    /// separate this feature from.</summary>
    private static IReadOnlyList<string> BranchOnlyPaths(GitWorkspace ws)
    {
        var listed = ws.Raw("rev-list", "--objects", "HEAD");
        return [.. listed.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => l.IndexOf(' ') is var i && i > 0 ? l[(i + 1)..] : "")
                            .Where(p => p.Length > 0)];
    }

    private static string ArchiveAndExtract(GitWorkspace ws, bool includeHistory)
    {
        var plan = includeHistory ? Prepared(ws) : WorkspaceArchiveScanner.Scan(ws.Root);
        string zip = Path.Combine(ws.Root + "-out", "archive.zip");
        WorkspaceArchiveWriter.Write(plan, zip);
        return Extract(zip, ws.Root + "-in");
    }

    private static string Extract(string zip, string into)
    {
        Directory.CreateDirectory(into);
        var result = WorkspaceArchiveExtractor.Extract(zip, into);
        Assert.Empty(result.Rejected);
        return result.WorkspaceDir;
    }

    private static IReadOnlyList<string> EntryNames(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return [.. zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>Entry name → its bytes, as a comparable string. Not the zip's own bytes: a zip stamps
    /// each entry with the moment it was written, so two identical archives are never the same
    /// file.</summary>
    private static string EntryContents(string zipPath, IReadOnlyList<string>? ignoreNames = null)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return string.Join('\n',
            zip.Entries.Where(e => ignoreNames is null || !ignoreNames.Contains(Path.GetFileName(e.FullName)))
               .OrderBy(e => e.FullName, StringComparer.Ordinal)
               .Select(e =>
               {
                   using var stream = e.Open();
                   using var memory = new MemoryStream();
                   stream.CopyTo(memory);
                   return e.FullName + " " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(memory.ToArray()));
               }));
    }

    private static (int Code, string Out, string Err) RawIn(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory       = dir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";

        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        string e = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, o, e);
    }

    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(source, @"^[ \t]*//.*$", "", RegexOptions.Multiline);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
