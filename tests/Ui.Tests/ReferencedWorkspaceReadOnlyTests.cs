using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.ProjectTree;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  RC-2 — referenced workspaces are read-only by default
//  (docs/sonnet-briefs/brief-revision-control-2-read-only-references.md §5,
//   docs/design/revision-control.md §7A.2/§7A.3/§7A.5).
//
//  What is being pinned is a DEFECT FIX, not a revision-control feature. The chain
//  §7A.2 describes — a library cell edited through a reference, the fix reaching one
//  designer and nobody else, the librarian's next publish overwriting it — is true in
//  the shipped application on a machine with no git anywhere, which is why gate 8
//  exists and why nothing in this file or in the code it drives goes near one.
//
//  The refusal is asserted as DATA (WorkspaceViewModel.RefusalForEditOf) rather than
//  through the posted message list. MessagesTool marshals through Dispatcher.UIThread,
//  which is a direct call in an isolated run and a queued one nobody pumps once another
//  test in the process has bound the dispatcher — a trap ReadOnlyWorkspaceTests already
//  records. Posting is one line either side of the decision and is scanned, not run.
// ──────────────────────────────────────────────────────────────────────────────

[Collection(CellStatGlobalsCollection.Name)]
public sealed class ReferencedWorkspaceReadOnlyTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _mine;     // the designer's own workspace — references the library
    private readonly string _library;  // the librarian's workspace — owns the cells

    public ReferencedWorkspaceReadOnlyTests()
    {
        _tmp     = Path.Combine(Path.GetTempPath(), "crf_rc2_" + Guid.NewGuid().ToString("N")[..8]);
        _mine    = Path.Combine(_tmp, "myproject");
        _library = Path.Combine(_tmp, "stdlib");
        Directory.CreateDirectory(_mine);
        Directory.CreateDirectory(_library);
        WorkspacePersistence.SaveToFile(Path.Combine(_library, ".cws"), new CwsFile());
        WorkspaceRootFinder.InvalidateCache();
    }

    public void Dispose()
    {
        WorkspaceRootFinder.InvalidateCache();
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes <c>myproject/.cws</c> BY HAND, in the exact shape a build before
    /// <see cref="CwsWorkspaceRef.Editable"/> existed produced: an alias, a path, and no editability
    /// field of any kind. Gate 1 turns on this being the real thing rather than a round trip through
    /// today's writer, which would have had every opportunity to fill the field in.
    /// </summary>
    private void WriteLegacyCws(string alias = "stdlib")
        => File.WriteAllText(Path.Combine(_mine, ".cws"),
            "{\n" +
            "  \"FormatVersion\": " + WorkspacePersistence.CurrentFormatVersion + ",\n" +
            "  \"ReferencedWorkspaces\": [\n" +
            "    { \"Alias\": \"" + alias + "\", \"Path\": \"../stdlib/.cws\" }\n" +
            "  ]\n" +
            "}\n");

    private void WriteCws(bool editable, string alias = "stdlib")
    {
        WorkspacePersistence.SaveToFile(Path.Combine(_mine, ".cws"), new CwsFile
        {
            ReferencedWorkspaces =
            [
                new CwsWorkspaceRef { Alias = alias, Path = "../stdlib/.cws", Editable = editable },
            ],
        });
        WorkspaceRootFinder.InvalidateCache();
    }

    /// <summary>A cell in the LIBRARY, and the <c>.ccell</c> path a parameter editor opens on.</summary>
    private string LibraryCell(string name = "Amp")
    {
        string dir = CellFolder.CreateCellFolder(_library, name);
        WorkspaceRootFinder.InvalidateCache();
        return Path.Combine(dir, CellFolder.CcellFileName);
    }

    private static CellParameterEditorDocument DocFor(string ccellPath)
    {
        var file  = CellPersistence.LoadFromFile(ccellPath);
        var model = new CellParameterEditModel(ccellPath, file);
        return new CellParameterEditorDocument(
            Path.GetFileName(Path.GetDirectoryName(ccellPath))!,
            new CellParameterEditorViewModel("Amp", model));
    }

    private WorkspaceViewModel OpenOnMine()
        => new() { CurrentWorkspacePath = Path.Combine(_mine, ".cws") };

    /// <summary>A command that records whether it was ever allowed to run.</summary>
    private sealed class SpyCommand : IUiCommand
    {
        public int Runs;
        public string Description => "Set NumPorts";
        public void Execute() => Runs++;
        public void Undo()    => Runs--;
    }

    private static Dictionary<string, byte[]> SnapshotTree(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .ToDictionary(p => p, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void AssertUnchanged(string root, Dictionary<string, byte[]> before)
    {
        var after = SnapshotTree(root);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (path, bytes) in before)
            Assert.True(bytes.SequenceEqual(after[path]), $"{path} was written");
    }

    // ── Gate 1: the default, and the inversion ───────────────────────────────

    /// <summary>
    /// R-rc2-4. A <c>.cws</c> written before the field existed — byte for byte, no editability field
    /// anywhere in it — reads as READ-ONLY. This deliberately inverts the house rule the sibling
    /// <see cref="CwsWorkspaceRef.CellsOnly"/> states three lines away in the same class, because the
    /// old behaviour there is not a preference anyone set: it is the hazard.
    /// </summary>
    [Fact]
    public void AReferenceWrittenBeforeTheFieldExisted_IsReadOnly()
    {
        WriteLegacyCws();
        WorkspaceRootFinder.InvalidateCache();

        // The fixture really is field-free — otherwise this gate would be testing today's writer.
        string raw = File.ReadAllText(Path.Combine(_mine, ".cws"));
        Assert.DoesNotContain("Editable", raw, StringComparison.OrdinalIgnoreCase);

        string ccell = LibraryCell();
        Assert.Equal(ReferenceAccess.ReadOnly,
                     ReferencedWorkspacePolicy.AccessFor(_mine, ccell, out string? owner, out string? alias));
        Assert.Equal(WorkspaceRootFinder.Normalize(_library), owner);
        Assert.Equal("stdlib", alias);
    }

    /// <summary>A reference someone explicitly made editable reads as editable, and the default a
    /// NEW reference is created with is read-only — File ▸ Reference Workspace… writes no flag.</summary>
    [Fact]
    public void EditableIsExplicit_AndANewReferenceIsReadOnly()
    {
        File.WriteAllText(Path.Combine(_mine, ".cws"), "{}");
        Assert.True(WorkspaceViewModel.AddReferencedWorkspace(
            _mine, "stdlib", Path.Combine(_library, ".cws"), out string? error), error);

        string ccell = LibraryCell();
        Assert.Equal(ReferenceAccess.ReadOnly, ReferencedWorkspacePolicy.AccessFor(_mine, ccell));

        Assert.True(ReferencedWorkspacePolicy.SetReferenceEditable(_mine, "stdlib", true, out error), error);
        Assert.Equal(ReferenceAccess.Editable, ReferencedWorkspacePolicy.AccessFor(_mine, ccell));

        Assert.True(ReferencedWorkspacePolicy.SetReferenceEditable(_mine, "stdlib", false, out error), error);
        Assert.Equal(ReferenceAccess.ReadOnly, ReferencedWorkspacePolicy.AccessFor(_mine, ccell));
    }

    /// <summary>
    /// The policy says nothing about content this workspace owns, and nothing about a file outside
    /// every reference. A read-only default that leaked into either would make the whole workspace
    /// un-editable, which is the failure mode worth naming.
    /// </summary>
    [Fact]
    public void OwnContentAndUnreferencedContentAreUntouched()
    {
        WriteLegacyCws();
        string mine  = Path.Combine(CellFolder.CreateCellFolder(_mine, "Local"), CellFolder.CcellFileName);
        string loose = Path.Combine(_tmp, "loose.clay");
        File.WriteAllText(loose, "{}");
        WorkspaceRootFinder.InvalidateCache();

        Assert.Equal(ReferenceAccess.NotReferenced, ReferencedWorkspacePolicy.AccessFor(_mine, mine));
        Assert.Equal(ReferenceAccess.NotReferenced, ReferencedWorkspacePolicy.AccessFor(_mine, loose));

        // And a sibling whose name merely STARTS with the referenced root's is not inside it.
        string decoy = Path.Combine(_tmp, "stdlib-old", "x.clay");
        Directory.CreateDirectory(Path.GetDirectoryName(decoy)!);
        File.WriteAllText(decoy, "{}");
        Assert.Equal(ReferenceAccess.NotReferenced, ReferencedWorkspacePolicy.AccessFor(_mine, decoy));
    }

    // ── Gate 2: the refusal fires on the EDIT, not on the save ───────────────

    /// <summary>
    /// The whole value of RC-2 is the moment the refusal arrives. Asserted on the point of
    /// MODIFICATION: the command never runs and nothing is pushed onto the undo stack, so the
    /// designer is stopped while they still have the context to do something sensible rather than
    /// after the work.
    /// </summary>
    [Fact]
    public void TheEditItselfIsRefused_TheCommandNeverRuns()
    {
        WriteLegacyCws();
        var vm  = OpenOnMine();
        var doc = DocFor(LibraryCell());
        doc.UndoRedo.EditGuard = () => vm.AllowEditOfDocument(doc);

        var cmd = new SpyCommand();
        doc.UndoRedo.Execute(cmd);

        Assert.Equal(0, cmd.Runs);              // the modification did not happen
        Assert.False(doc.UndoRedo.CanUndo);     // and left no history behind
        Assert.False(doc.UndoRedo.IsModified);  // so the document is not dirty either
    }

    /// <summary>The same document IS editable once the reference is editable — the refusal is a
    /// policy on the reference, not a property of the file.</summary>
    [Fact]
    public void AnEditableReferenceLetsTheEditThrough()
    {
        WriteCws(editable: true);
        var vm  = OpenOnMine();
        var doc = DocFor(LibraryCell());
        doc.UndoRedo.EditGuard = () => vm.AllowEditOfDocument(doc);

        var cmd = new SpyCommand();
        doc.UndoRedo.Execute(cmd);

        Assert.Equal(1, cmd.Runs);
        Assert.True(doc.UndoRedo.CanUndo);
    }

    /// <summary>
    /// R-rc2-1: SL2's disabled-Save path took the policy without modification. Save is disabled on
    /// the document for the SECOND reason now, and the reason states the remedy that fits it — open
    /// the owning workspace — rather than SL2's "save a copy", which answers a question the designer
    /// editing a shared library did not ask.
    /// </summary>
    [Fact]
    public void SaveIsDisabledThroughTheSameSl2Path_WithItsOwnReason()
    {
        WriteLegacyCws();
        var vm  = OpenOnMine();
        var doc = DocFor(LibraryCell());

        Assert.True(vm.IsDocumentReadOnly(doc));
        string reason = Assert.IsType<string>(vm.ReadOnlyDocumentReason(doc));
        Assert.Contains("stdlib", reason);
        Assert.Contains("references", reason);
        Assert.DoesNotContain("Save a copy", reason);
    }

    // ── Gates 3 and 4: the action, and the routing ───────────────────────────

    /// <summary>
    /// R-rc2-6. The refusal carries an INVOKABLE action, not advice — asserted on the action itself
    /// rather than on the text mentioning one — and invoking it opens the owning workspace, landing
    /// on the very document the designer tried to edit.
    /// </summary>
    [Fact]
    public async Task TheRefusalCarriesAnInvokableAction_ThatOpensTheOwningWorkspace()
    {
        WriteLegacyCws();
        string ccell = LibraryCell();

        (string Cws, string Doc)? opened = null;
        var vm = OpenOnMine();
        vm.OwningWindowLookup      = _ => null;                       // nobody has the library open
        vm.OpenWorkspaceForEditHook = (cws, doc) => opened = (cws, doc);

        var refusal = Assert.IsType<WorkspaceViewModel.ReferenceEditRefusal>(
            vm.RefusalForEditOf(DocFor(ccell)));

        Assert.NotNull(refusal.ActionLabel);
        Assert.NotNull(refusal.Invoke);
        Assert.Contains("stdlib", refusal.ActionLabel!);

        await refusal.Invoke!();

        Assert.Equal(Path.Combine(_library, ".cws"), opened!.Value.Cws);
        Assert.Equal(ccell, opened!.Value.Doc);
    }

    /// <summary>
    /// R-rc2-7. With the owning workspace already open, the edit is not refused — it is MOVED. That
    /// window comes forward, is asked for the document, and the refusal carries no action, because
    /// there is nothing left to offer.
    /// </summary>
    [Fact]
    public void WithBothWorkspacesOpen_TheEditIsRoutedToTheOwningWindow()
    {
        WriteLegacyCws();
        string ccell = LibraryCell();

        int    activated = 0;
        string? asked    = null;
        string? askedFor = null;

        var vm = OpenOnMine();
        vm.OwningWindowLookup = root =>
        {
            asked = root;
            return new WorkspaceViewModel.EditRouteTarget(() => activated++, p => askedFor = p);
        };

        var refusal = Assert.IsType<WorkspaceViewModel.ReferenceEditRefusal>(
            vm.RefusalForEditOf(DocFor(ccell)));

        Assert.Equal(WorkspaceRootFinder.Normalize(_library), WorkspaceRootFinder.Normalize(asked));
        Assert.Equal(1, activated);
        Assert.Equal(ccell, askedFor);
        Assert.Null(refusal.ActionLabel);
        Assert.Null(refusal.Invoke);
    }

    // ── Gate 5: the referencing workspace writes nothing ─────────────────────

    /// <summary>
    /// The point of routing the edit to the owner is that the owner is the one that records it. This
    /// asserts the negative half, which is the half that would fail silently: refusing and routing an
    /// edit writes NOTHING at all into the referencing workspace — not the <c>.cws</c>, not a session
    /// file, nothing.
    /// </summary>
    [Fact]
    public void RefusingAndRoutingWritesNothingIntoTheReferencingWorkspace()
    {
        WriteLegacyCws();
        string ccell = LibraryCell();
        var before   = SnapshotTree(_mine);

        var vm = OpenOnMine();
        vm.OwningWindowLookup = _ => new WorkspaceViewModel.EditRouteTarget(() => { }, _ => { });

        var doc = DocFor(ccell);
        doc.UndoRedo.EditGuard = () => vm.AllowEditOfDocument(doc);
        doc.UndoRedo.Execute(new SpyCommand());

        AssertUnchanged(_mine, before);
    }

    // ── Gate 6: the editable reference is marked, and the mark is stable ─────

    /// <summary>
    /// R-rc2-5/-9. An editably-referenced workspace is marked in the Project panel and its documents
    /// are marked in their tabs — and the mark describes this workspace's RELATIONSHIP to the other
    /// one, so it does not change with which windows happen to be open. A mark that flickered with
    /// window state would produce behaviour nobody can reason about.
    /// </summary>
    [Fact]
    public void AnEditableReferenceIsMarkedInTheTreeAndInTheTab_WhicheverWindowsAreOpen()
    {
        WriteCws(editable: true);
        string ccell = LibraryCell();
        var vm = OpenOnMine();

        var tree = WorkspaceScanner.Scan(_mine);
        var row  = tree.Children.Single(c => c.Kind == NodeKind.ReferencedWorkspace);
        Assert.True(row.IsEditableReference);

        var rowVm = new ProjectTreeNodeViewModel(row, new ProjectTreeFilterState(), actions: vm);
        Assert.True(rowVm.IsEditableReference);
        Assert.Equal("Make Reference Read-Only", rowVm.ToggleReferenceEditableHeader);

        Assert.True(vm.IsThroughEditableReference(ccell));

        // The other workspace being open, or not, changes nothing: the mark is read out of THIS
        // workspace's own .cws, which is the only place the relationship is recorded.
        vm.OwningWindowLookup = _ => new WorkspaceViewModel.EditRouteTarget(() => { }, _ => { });
        Assert.True(vm.IsThroughEditableReference(ccell));
        vm.OwningWindowLookup = _ => null;
        Assert.True(vm.IsThroughEditableReference(ccell));

        // …and a READ-ONLY reference — the ordinary case — carries no mark at all.
        WriteCws(editable: false);
        var plain = WorkspaceScanner.Scan(_mine).Children.Single(c => c.Kind == NodeKind.ReferencedWorkspace);
        Assert.False(plain.IsEditableReference);
        Assert.Equal("Allow Editing Through This Reference…",
                     new ProjectTreeNodeViewModel(plain, new ProjectTreeFilterState(), actions: vm)
                         .ToggleReferenceEditableHeader);
    }

    /// <summary>The tab mark is a real property on the document types a referenced sub-tree can open,
    /// set from the one sweep — the tab template binds it by name, exactly as it binds
    /// <c>IsForeign</c>.</summary>
    [Fact]
    public void TheTabMarkIsSetOnTheDocument()
    {
        WriteCws(editable: true);
        var doc = DocFor(LibraryCell());
        Assert.False(doc.IsEditableReference);

        ((IReferenceMarkedDocument)doc).IsEditableReference = OpenOnMine()
            .IsThroughEditableReference(doc.FilePath);

        Assert.True(doc.IsEditableReference);
    }

    // ── Gate 7: the two wordings ─────────────────────────────────────────────

    /// <summary>
    /// R-rc2-8. Arriving at another window's copy of a file has two different reasons and they read
    /// differently: a file the designer had open elsewhere and forgot is about DUPLICATION, and a
    /// referenced cell they never opened is about OWNERSHIP. Pinned on the source, because the branch
    /// is one expression inside the routing message and the assertion has to be that BOTH sentences
    /// exist and that the ownership one is chosen by the reference policy.
    /// </summary>
    [Fact]
    public void TheDuplicationWordingAndTheOwnershipWordingAreBothThere()
    {
        string body = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        Assert.Contains("shown there rather than opened twice.", body, StringComparison.Ordinal);
        Assert.Contains("ReadOnlyReferenceOwnerOf(wanted) is { } ownerRoot", body, StringComparison.Ordinal);
        Assert.Contains("which this workspace only references — it is ", body, StringComparison.Ordinal);
    }

    /// <summary>The refusal's own two shapes differ in the same way — routed says where the cell is
    /// edited, un-routed says how to get there.</summary>
    [Fact]
    public void TheRoutedAndUnroutedRefusalsSayDifferentThings()
    {
        WriteLegacyCws();
        string ccell = LibraryCell();

        var vm = OpenOnMine();
        vm.OwningWindowLookup = _ => new WorkspaceViewModel.EditRouteTarget(() => { }, _ => { });
        string routed = vm.RefusalForEditOf(DocFor(ccell))!.Text;

        vm.OwningWindowLookup = _ => null;
        string unrouted = vm.RefusalForEditOf(DocFor(ccell))!.Text;

        Assert.NotEqual(routed, unrouted);
        Assert.Contains("edited there", routed, StringComparison.Ordinal);
        Assert.Contains("Open 'stdlib' as a workspace", unrouted, StringComparison.Ordinal);
    }

    // ── The wiring the behavioural gates above stand on ─────────────────────

    /// <summary>
    /// The gates above install <see cref="UndoRedoStack.EditGuard"/> themselves, which proves the
    /// guard REFUSES but not that the application ever puts one on. That wiring is a sweep over the
    /// open documents rather than a line at each of the twenty-seven places one is registered — a
    /// rule twenty-seven openers have to remember is true in twenty-six of them — and it rides the
    /// two menu fan-outs, which run on every activation and therefore before a freshly-opened
    /// document can be typed into.
    ///
    /// <para>Scanned rather than driven: the sweep reads a private document registry that a headless
    /// test cannot populate without opening real editors, and what would go wrong is the wiring being
    /// dropped, not the sweep's own body being wrong.</para>
    /// </summary>
    [Fact]
    public void TheGuardIsInstalledBySweepingTheOpenDocuments()
    {
        string sweep = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.ReferenceReadOnly.cs"));
        Assert.Contains("stack.EditGuard ??= () => AllowEditOfDocument(dockable);", sweep, StringComparison.Ordinal);
        Assert.Contains("marked.IsEditableReference = IsThroughEditableReference(", sweep, StringComparison.Ordinal);

        string readOnly = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.ReadOnly.cs"));
        Assert.Contains("ApplyReferencePolicyToOpenDocuments();", readOnly, StringComparison.Ordinal);

        // …and the fan-outs that carry it are the two that already refresh the read-only menu state.
        string body = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        Assert.Equal(2, CountOccurrences(body, "RefreshReadOnlyMenuState();"));

        // Every editor's mutations meet at UndoRedoStack.Execute, which is what makes one guard
        // enough. A second unguarded entry point into the stack would silently reopen the hole.
        string stack = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "Commands", "UndoRedoStack.cs"));
        Assert.Contains("if (EditGuard is { } guard && !guard()) return;", stack, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    // ── Gate 8: no git anywhere ──────────────────────────────────────────────

    /// <summary>
    /// The gate that keeps the brief honest. §7A.2 is a defect in the existing workspace model that
    /// the revision-control investigation happened to find; if it shipped as part of a git feature,
    /// the users who never got the fix would be exactly the majority §4.3 identifies — RF designers
    /// with no git — and they are the ones with no history to fall back on when it bites. Every test
    /// above runs on a machine with no git installed, and the implementation reaches for none.
    /// </summary>
    [Fact]
    public void NothingInTheImplementationReachesForGit()
    {
        foreach (string rel in new[]
        {
            Path.Combine("src", "Design", "Workspace", "ReferencedWorkspacePolicy.cs"),
            Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.ReferenceReadOnly.cs"),
        })
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), rel));
            // "git" appears only where the code says it does NOT need one.
            foreach (string line in text.Split('\n'))
            {
                if (line.IndexOf("git", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Assert.True(line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                            || line.TrimStart().StartsWith("///", StringComparison.Ordinal),
                            $"{rel}: git named outside a comment — {line.Trim()}");
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
