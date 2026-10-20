// ================================================================
//  EmSetupForLayoutLookupTests.cs — owner report, 2026-09-15, on the shipped Patch Antenna example
//
//  "The Patch Antenna example includes a .cem file, but it's not configured properly — compute the
//  radiation pattern is turned off."
//
//  THE EXAMPLE WAS NEVER WRONG. `patch/em/patch-5p8GHz.cem` asks for the pattern, the sheet mesh and
//  the resonance search, and AntennaExampleTests gates all three. What was wrong is the door: the
//  Layout Editor's EM button names a setup by CONVENTION — `<workspace>/em/<layout stem>.cem` — so on
//  the example's own layout it found nothing, CREATED a fresh default setup (1-20 GHz, Auto kernel,
//  no radiation pattern, no sheet mesh) and opened that instead. Two setups, the plausible one on
//  screen, and nothing said the other existed.
//
//  Both shipped EM examples keep their .cem in the CELL's own `em/` folder, named after the frequency
//  rather than after the layout, which is the arrangement the antenna documentation tells a reader to
//  open. So the conventional name is not a rule the format carries and cannot be the only question
//  asked. EmSetupResolver.FindSetupsForLayout asks the question the RUN asks instead.
// ================================================================

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public sealed class EmSetupForLayoutLookupTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "crf-emlookup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void WriteCem(string path, string layoutRef)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        EmSetupPersistence.SaveToFile(path, new EmSetup
        {
            Name      = Path.GetFileNameWithoutExtension(path),
            LayoutRef = layoutRef,
        });
    }

    private static string MakeWorkspace()
    {
        string root = TempDir();
        File.WriteAllText(Path.Combine(root, ".cws"), "{\"FormatVersion\":2}");
        Directory.CreateDirectory(Path.Combine(root, "cell", "layout"));
        LayoutPersistence.SaveToFile(
            Path.Combine(root, "cell", "layout", "patch.clay"),
            new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron });
        return root;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  The report itself — the shipped example
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The regression gate for the report.</b> The shipped Patch Antenna example's layout has
    /// exactly ONE EM setup in its workspace, it is the one the documentation names, and it is not at
    /// the conventional path — so a lookup that only knows the conventional path finds nothing here,
    /// which is exactly what happened.
    /// </summary>
    [Fact]
    public void ThePatchAntennaExamplesLayout_ResolvesToTheExamplesOwnSetup()
    {
        string ws   = Path.Combine(RepoRoot(), "examples", "Patch Antenna");
        string cws  = Path.Combine(ws, ".cws");
        string clay = Path.Combine(ws, "patch", "layout", "patch.clay");
        Assert.True(File.Exists(cws) && File.Exists(clay), "the Patch Antenna example is not in the tree");

        var found = EmSetupResolver.FindSetupsForLayout(cws, clay);

        Assert.Equal(
            [Path.Combine(ws, "patch", "em", "patch-5p8GHz.cem")],
            found);

        // …and it is NOT where the EM button would have written one, which is the whole point.
        Assert.False(File.Exists(Path.Combine(ws, "em", "patch.cem")));

        // The setup it found is the configured one, so opening it lands a user on the documented run
        // rather than on a default. (AntennaExampleTests owns the full assertion; this is the half
        // that ties THIS lookup to THAT file.)
        Assert.True(EmSetupPersistence.LoadFromFile(found[0]).RadiationPattern);
    }

    /// <summary>The same for the other shipped EM example, which is in the same shape.</summary>
    [Fact]
    public void TheTaperExamplesLayout_ResolvesToTheExamplesOwnSetup()
    {
        string ws   = Path.Combine(RepoRoot(), "examples", "Klopfenstein Taper");
        string cws  = Path.Combine(ws, ".cws");
        string clay = Path.Combine(ws, "Taper", "layout", "Taper.clay");
        Assert.True(File.Exists(cws) && File.Exists(clay), "the Klopfenstein Taper example is not in the tree");

        Assert.Equal(
            [Path.Combine(ws, "Taper", "em", "Taper-MoM.cem")],
            EmSetupResolver.FindSetupsForLayout(cws, clay));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  The rule
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A <c>.cem</c> anywhere in the workspace counts, wherever it sits and whatever it is
    /// called — which is what the reference rule already says and the lookup now asks.</summary>
    [Fact]
    public void ASetupInACellFolder_IsFound_ThoughItsNameMatchesNothing()
    {
        string root = MakeWorkspace();
        string cem  = Path.Combine(root, "cell", "em", "sweep-at-6GHz.cem");
        WriteCem(cem, "cell/layout/patch.clay");

        Assert.Equal(
            [cem],
            EmSetupResolver.FindSetupsForLayout(
                Path.Combine(root, ".cws"), Path.Combine(root, "cell", "layout", "patch.clay")));
    }

    /// <summary>
    /// <b>The conventional path comes first when it is among them</b>, so a workspace that has both
    /// keeps opening the one the application itself wrote — the behaviour before this change, which
    /// must not move.
    /// </summary>
    [Fact]
    public void TheConventionalPathIsPreferred_WhenBothExist()
    {
        string root = MakeWorkspace();
        string conventional = Path.Combine(root, "em", "patch.cem");
        string inCell       = Path.Combine(root, "cell", "em", "another.cem");
        WriteCem(inCell, "cell/layout/patch.clay");
        WriteCem(conventional, "cell/layout/patch.clay");

        var found = EmSetupResolver.FindSetupsForLayout(
            Path.Combine(root, ".cws"), Path.Combine(root, "cell", "layout", "patch.clay"));

        Assert.Equal([conventional, inCell], found);
    }

    /// <summary>A setup pointing at a DIFFERENT layout is not this layout's setup — asked through the
    /// reference rule, not by name, which is why a same-named layout elsewhere does not match.</summary>
    [Fact]
    public void ASetupForAnotherLayout_IsNotReturned()
    {
        string root = MakeWorkspace();
        Directory.CreateDirectory(Path.Combine(root, "other", "layout"));
        LayoutPersistence.SaveToFile(
            Path.Combine(root, "other", "layout", "patch.clay"),
            new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron });
        WriteCem(Path.Combine(root, "em", "patch.cem"), "other/layout/patch.clay");

        Assert.Empty(EmSetupResolver.FindSetupsForLayout(
            Path.Combine(root, ".cws"), Path.Combine(root, "cell", "layout", "patch.clay")));
    }

    /// <summary>An absolute reference resolves too — same rule, and the case a hand-edited or
    /// out-of-workspace setup arrives in.</summary>
    [Fact]
    public void AnAbsoluteReference_Resolves()
    {
        string root = MakeWorkspace();
        string clay = Path.Combine(root, "cell", "layout", "patch.clay");
        string cem  = Path.Combine(root, "em", "abs.cem");
        WriteCem(cem, clay);

        Assert.Equal([cem], EmSetupResolver.FindSetupsForLayout(Path.Combine(root, ".cws"), clay));
    }

    /// <summary>An unreadable <c>.cem</c> is skipped, never thrown on: this lookup sits in the middle
    /// of a user's own gesture, and the file it could not read is not the one they asked about.</summary>
    [Fact]
    public void AnUnreadableSetup_IsSkipped_NotThrownOn()
    {
        string root = MakeWorkspace();
        Directory.CreateDirectory(Path.Combine(root, "em"));
        File.WriteAllText(Path.Combine(root, "em", "broken.cem"), "{ this is not json");
        string good = Path.Combine(root, "em", "good.cem");
        WriteCem(good, "cell/layout/patch.clay");

        Assert.Equal(
            [good],
            EmSetupResolver.FindSetupsForLayout(
                Path.Combine(root, ".cws"), Path.Combine(root, "cell", "layout", "patch.clay")));
    }

    /// <summary>Dot-folders are not walked — a workspace's own bookkeeping, a <c>.git</c>, a build
    /// tree someone parked here. A <c>.cem</c> under one is not a document of this workspace.</summary>
    [Fact]
    public void DotFolders_AreNotWalked()
    {
        string root = MakeWorkspace();
        WriteCem(Path.Combine(root, ".crf-scratch", "hidden.cem"), "cell/layout/patch.clay");

        Assert.Empty(EmSetupResolver.FindSetupsForLayout(
            Path.Combine(root, ".cws"), Path.Combine(root, "cell", "layout", "patch.clay")));
    }
}
