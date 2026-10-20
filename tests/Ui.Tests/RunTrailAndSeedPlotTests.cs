// ================================================================
//  RunTrailAndSeedPlotTests.cs
//
//  Two things a user report exposed that are not the same bug, and are both about a trail or a
//  brace saying something the code does not do.
//
//   1. A run wrote its netlist and then went silent: no plan line, no engine line, and the same
//      design ran normally a minute and a half later. That gap is Prepare returning a non-Success
//      plan — a branch that reported only to the Messages panel, which a crash report does not
//      carry. Every early exit between "netlist written" and "left the engine" now leaves a note,
//      because the trail is what gets read when there is no stack, and a run that ends with no
//      note reads as a run that vanished.
//
//   2. The trail showed two "addPlot … (now 2)" notes six seconds apart — the same count before
//      both, and no removePlot between them, which reads as an add that silently failed. An
//      untraced Ctrl+Z explains it exactly and nothing could say so, because undo and redo rewrite
//      the state every other breadcrumb describes and left none of their own.
//
//  Plus the brace that went with it: DataDisplayViewModel's constructor deselect sat at the
//  indentation of the addEmptyPlot branch WITHOUT braces, so it ran either way.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class RunTrailAndSeedPlotTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>One source file with its comments stripped — this is about what the code does, and
    /// the comments here name the very thing being scanned for.</summary>
    private static string Src(string relative)
    {
        string raw = File.ReadAllText(Path.Combine(RepoRoot(), relative));
        raw = Regex.Replace(raw, @"/\*.*?\*/", "", RegexOptions.Singleline);
        raw = Regex.Replace(raw, @"//[^\n]*", "");
        return raw;
    }

    // ---- 1. No run may end without saying so --------------------------------

    /// <summary>
    /// Derived, not listed: every <c>return</c> in the window between the netlist note and the
    /// engine note must be preceded by a breadcrumb. A future fourth refusal added to that stretch
    /// is forced to make the same decision rather than inheriting the silence.
    /// </summary>
    [Fact]
    public void EveryEarlyExitOfARun_LeavesABreadcrumb()
    {
        string src = Src(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        int start = src.IndexOf("netlist written to", StringComparison.Ordinal);
        int end   = src.IndexOf("left the engine",    StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "the run method's own landmarks must still be there");

        string window = src[start..end];
        var lines = window.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "return;") continue;

            // "Preceded" is deliberately generous — the note may sit a couple of statements up,
            // above a Messages call. What is forbidden is a return with nothing at all above it.
            string before = string.Join('\n', lines[Math.Max(0, i - 6)..i]);
            Assert.True(
                before.Contains("CrashReporter.Note", StringComparison.Ordinal),
                $"a run can exit at line {i} of the netlist-to-engine window with no trail note:\n{before}\n    return;");
        }
    }

    /// <summary>The one that was actually silent: a plan that comes back non-Success.</summary>
    [Fact]
    public void ARunThatIsNeverPlanned_SaysWhyInTheTrail()
    {
        string src = Src(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        Assert.Contains("NOT run - {plan.Status}", src, StringComparison.Ordinal);
    }

    /// <summary>And how it ended, once it has run — "left the engine" says only that it returned.</summary>
    [Fact]
    public void ARunsOutcome_ReachesTheTrail()
    {
        string src = Src(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        Assert.Contains("outcome {result.Status}", src, StringComparison.Ordinal);
    }

    // ---- 2. Undo and redo are gestures too ----------------------------------

    [Fact]
    public void UndoAndRedo_LeaveBreadcrumbs()
    {
        string src = Src(Path.Combine("src", "Ui", "DataDisplay", "Models", "UndoRedo.cs"));
        Assert.Contains("Gesture.Note(\"undo\"", src, StringComparison.Ordinal);
        Assert.Contains("Gesture.Note(\"redo\"", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// The note has to name the stack depths, because the ambiguity it was added to settle is a
    /// COUNT: "addPlot (now 2)" twice with no removePlot between reads as an add that failed, and
    /// only the undo's own before/after can say otherwise.
    /// </summary>
    [Fact]
    public void AnUndoOfAnAddedPlot_IsVisibleAsAStackMovement()
    {
        var lib = new DataSourceLibraryViewModel();
        var dd  = new DataDisplayViewModel(lib);

        Assert.Single(dd.Plots);          // the constructor's own seeded plot
        dd.AddPlot(PlotType.Table);
        Assert.Equal(2, dd.Plots.Count);

        dd.UndoRedo.Undo();
        Assert.Single(dd.Plots);          // ...and the count is back where the next addPlot reads it

        dd.UndoRedo.Redo();
        Assert.Equal(2, dd.Plots.Count);
    }

    // ---- The brace -----------------------------------------------------------

    /// <summary>
    /// <c>selectEmptyPlot</c> qualifies the plot the constructor SEEDED. With no plot seeded there
    /// is nothing for it to say anything about, and reaching the deselect anyway was only harmless
    /// by luck. The four combinations are cheap; the indentation was not evidence of any of them.
    /// </summary>
    [Theory]
    [InlineData(true,  true)]
    [InlineData(true,  false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void TheSeededPlotIsCreatedOnlyWhenAsked(bool addEmptyPlot, bool selectEmptyPlot)
    {
        var lib = new DataSourceLibraryViewModel();
        var dd  = new DataDisplayViewModel(lib, addEmptyPlot, selectEmptyPlot);

        Assert.Equal(addEmptyPlot ? 1 : 0, dd.Plots.Count);
        if (addEmptyPlot)
            Assert.Equal(selectEmptyPlot, dd.Plots[0].IsSelected);
        else
            Assert.False(dd.HasAnySelection);
    }
}
