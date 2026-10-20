// ================================================================
//  LayoutToolButtonFocusTests.cs — a tool button hands the keyboard back to the canvas
//
//  Owner report, 2026-09-09: with the Port tool armed from the toolbar, Escape did not disarm it,
//  and neither `s`/F3 (geometry snap) nor F9 (snap distance) did anything.
//
//  None of those keys were broken. Clicking a Button gives it keyboard focus, and every one of
//  those shortcuts is read either by LayoutCanvas.OnKeyDown (which needs the canvas focused) or by
//  LayoutEditorView.OnViewKeyDownTunnel, whose first line is `IsKeyboardFocusWithin`. So after
//  arming a tool the keyboard belonged to the toolbar.
//
//  It shows up on Port and not on Rectangle because a DRAG tool's first press on the canvas focuses
//  it and the gesture is still in progress, so Escape works from then on. Port and Via commit on a
//  SINGLE click — the only gesture that would move focus into the canvas is the one that places the
//  port — so between arming and placing there was no way to reach the keyboard at all.
//
//  The Schematic Editor's own tool buttons have always called SchematicCanvasCtrl.Focus(); these
//  drive a Command binding instead of a click handler and were never given the same half. This test
//  is the gate on that: a tool button added later without the handler fails here rather than being
//  found by arming it and pressing Escape.
// ================================================================

using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class LayoutToolButtonFocusTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    private const string Xaml = "src/Ui/Views/Layout/LayoutEditorView.axaml";
    private const string Code = "src/Ui/Views/Layout/LayoutEditorView.axaml.cs";

    [Fact]
    public void EveryLayoutToolButton_ReturnsFocusToTheCanvas()
    {
        string xaml = Read(Xaml);

        // One <Button …> element per tool, found by the command it drives.
        var buttons = Regex.Matches(xaml, @"<Button\b[^>]*?SetActiveToolCommand[^>]*?>",
                                    RegexOptions.Singleline);

        Assert.True(buttons.Count >= 10,
            $"Expected the layout toolbar's tool buttons to be found; matched {buttons.Count}.");

        foreach (System.Text.RegularExpressions.Match b in buttons)
        {
            Assert.True(
                b.Value.Contains("Click=\"OnToolButtonClick\"", StringComparison.Ordinal),
                "A layout tool button arms a MODE whose keyboard shortcuts (Escape, s/F3, F9) all " +
                "live on the canvas, so it must hand the keyboard back with " +
                "Click=\"OnToolButtonClick\". Without it a single-click tool (Port, Via) is " +
                "unreachable by keyboard at all. Offending button: " +
                Regex.Replace(b.Value, @"\s+", " "));
        }
    }

    [Fact]
    public void TheHandlerActuallyFocusesTheCanvas()
    {
        string code = Read(Code);

        var handler = Regex.Match(
            code, @"private void OnToolButtonClick\([^)]*\)\s*(=>[^;]*;|\{[^}]*\})",
            RegexOptions.Singleline);

        Assert.True(handler.Success, $"{Code} declares no OnToolButtonClick handler.");
        Assert.Contains("LayoutCanvasCtrl.Focus()", handler.Value, StringComparison.Ordinal);
    }
}
