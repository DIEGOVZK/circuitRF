using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>A drag-and-drop placement lands unrotated</b> (owner, 2026-09-17).
///
/// <para>Owner-reported: the same Library tile placed at a different angle depending on how it was
/// placed. Arming the place tool always starts at R0 — <c>PlacementService</c> arms every entry that
/// way — but the drop handlers read <c>SchematicViewModel.CurrentPlacementRotation</c>, the sticky
/// angle left behind by whatever was last rotated while armed. Nothing on screen explained the
/// difference, and a drag cannot be rotated mid-gesture (the keyboard does not route during a drag),
/// so the carried-over angle could only ever be a surprise from an earlier edit.</para>
///
/// <para><b>Why a source scan.</b> A <c>UserControl</c> cannot be constructed headlessly in this
/// project, so no real <c>DragDrop.DropEvent</c> can be raised here — the same constraint
/// <see cref="DropTakesKeyboardFocusTests"/> works around. What can be held shut is the rule: the
/// canvas's drag-and-drop path asks for no placement rotation at all. Comments and string literals
/// are stripped first, or the doc comment above <c>DropRotation</c> — which names the property it
/// replaced — would satisfy the scan by talking about it.</para>
/// </summary>
public class DropPlacesUnrotatedTests
{
    [Fact]
    public void SchematicCanvas_DragAndDrop_NeverReadsTheStickyPlacementRotation()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        var code = File.ReadAllText(Path.Combine(dir!, "src", "Ui", "Controls", "SchematicCanvas.cs"));
        code = Regex.Replace(code, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        code = Regex.Replace(code, @"//[^\n]*", " ");
        code = Regex.Replace(code, "\"(?:[^\"\\\\\n]|\\\\.)*\"", "\"\"");

        Assert.DoesNotContain("CurrentPlacementRotation", code);
        Assert.Contains("const SymbolRotation DropRotation = SymbolRotation.R0", code);
    }
}
