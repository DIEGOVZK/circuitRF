using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using CircuitRF.Design.Layout;
using CircuitRF.Render;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using Xunit;

// The namespace is NOT `CircuitRF.Ui.Tests.Stackup`, though the folder is — see the note at the top
// of StackupSceneTests.cs: a namespace segment named `Stackup` shadows the `Stackup` TYPE for every
// file under `CircuitRF.Ui.Tests`.
namespace CircuitRF.Ui.Tests.StackupRender;

/// <summary>
/// brief-stackup-render-4-inline-edit.md's gate — double-click a value ON the drawing, type, Return.
///
/// <h3>The box under test is the REAL one</h3>
/// <para><see cref="SchematicInlineEditBox"/> constructs, opens, measures its text against Skia and
/// pre-selects the value with no application host — verified before this file was written, because
/// this project's .csproj says tests must not call Avalonia runtime APIs and a <c>TextBox</c> is the
/// first thing here that could have needed one. So nothing is stubbed: the gate drives the same
/// control the schematic editor drives.</para>
///
/// <para>What does need a host is <c>Focus()</c> and an arrange pass — which is exactly why the
/// editor RAISES <c>Opened</c> rather than focusing itself, and why the hosting half (the panel, the
/// three key handlers, the Esc precedence) is a source scan, the same fallback
/// <see cref="TechEditorStackupTabLayoutTests"/> already uses for this view.</para>
/// </summary>
public class StackupInlineEditTests
{
    private const double PaneWidth = 900;

    // ── A technology carrying all seven editable fields ───────────────────────────────────────────

    /// <summary>
    /// Two conductors around a MAGNETIC core, plus a plated via with a stated wall.
    ///
    /// <para>Built rather than shipped because two of the seven labels are CONDITIONAL: µr is drawn
    /// only when it is not 1 (the engineering convention — a non-magnetic dielectric has no µr on the
    /// drawing and is edited from the card), and a wall thickness is drawn only on a plated-FILL via,
    /// which is the only state in which it means anything. A shipped board has neither.</para>
    /// </summary>
    private static Technology SevenFieldTech()
    {
        var tech = new Technology { Name = "InlineEditFixture", DefaultDisplayUnit = LayoutUnit.Um };
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "TopMetal",
            ThicknessDbu = 35_000, SigmaSm = 5.8e7,
        });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Dielectric, Name = "Core",
            ThicknessDbu = 1_600_000, Epsr = 4.4, TanD = 0.02, Mur = 2.5,
        });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "BotMetal",
            ThicknessDbu = 35_000, SigmaSm = 5.8e7, IsGroundReference = true,
        });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Via, Name = "Through",
            SpanFromLayer = "TopMetal", SpanToLayer = "BotMetal",
            Fill = ViaFillKind.Plated, Plated = true, WallThicknessDbu = 25_000,
        });
        return tech;
    }

    /// <summary>The seven fields, and the entry each is drawn on.</summary>
    public static TheoryData<StackupField, string> EveryField => new()
    {
        { StackupField.Name,          "TopMetal" },
        { StackupField.Thickness,     "TopMetal" },
        { StackupField.Sigma,         "TopMetal" },
        { StackupField.Epsr,          "Core"     },
        { StackupField.TanD,          "Core"     },
        { StackupField.Mur,           "Core"     },
        { StackupField.WallThickness, "Through"  },
    };

    private static TechEditorViewModel Editor([System.Runtime.CompilerServices.CallerMemberName] string who = "") =>
        new(Path.Combine(Path.GetTempPath(), $"stackup-inline-edit-{who}.ctech"), SevenFieldTech());

    private sealed record Surface(
        TechEditorViewModel Vm, StackupCanvas Canvas, StackupInlineEditor Editor, SchematicInlineEditBox Box);

    private static Surface Open([System.Runtime.CompilerServices.CallerMemberName] string who = "")
    {
        var vm = Editor(who);
        var canvas = new StackupCanvas { ViewModel = vm };
        canvas.MeasureForWidth(PaneWidth);   // the scene the double-click is hit-tested against

        var box = new SchematicInlineEditBox();
        var editor = new StackupInlineEditor(box);
        canvas.InlineEditor = editor;
        return new Surface(vm, canvas, editor, box);
    }

    /// <summary>The centre of the label carrying one field on one entry — where a double-click
    /// lands.</summary>
    private static Point PointOn(StackupCanvas canvas, string layer, StackupField field)
    {
        var label = LabelOn(canvas, layer, field);
        return new Point((label.Rect.Left + label.Rect.Right) / 2.0,
                         (label.Rect.Top + label.Rect.Bottom) / 2.0);
    }

    private static StackupLabel LabelOn(StackupCanvas canvas, string layer, StackupField field)
    {
        // Measured first, every time: a committed edit, an undo or a redo invalidates the scene
        // cache, and a real canvas would be re-measured by the layout pass that the invalidation
        // requested. The cache keys on the technology reference and the width, so this is free when
        // nothing changed.
        canvas.MeasureForWidth(PaneWidth);

        var scene = canvas.SceneCache.Current;
        Assert.NotNull(scene);
        var label = scene!.Labels.FirstOrDefault(
            l => l.Field == field && string.Equals(l.LayerName, layer, StringComparison.Ordinal));
        Assert.True(label is not null, $"the drawing must carry a {field} label on {layer}");
        return label!;
    }

    private static StackupLayerRowViewModel Row(TechEditorViewModel vm, string name) =>
        vm.StackupLayers.First(r => r.Layer.Name == name);

    private static string Staged(StackupLayerRowViewModel row, StackupField field) => field switch
    {
        StackupField.Name          => row.StagedName,
        StackupField.Thickness     => row.StagedThicknessText,
        StackupField.Sigma         => row.StagedSigmaSm,
        StackupField.Epsr          => row.StagedEpsr,
        StackupField.TanD          => row.StagedTanD,
        StackupField.Mur           => row.StagedMur,
        StackupField.WallThickness => row.StagedWallThickness,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    /// <summary>A value that is valid for the field and differs from the fixture's.</summary>
    private static string NewValueFor(StackupField field) => field switch
    {
        StackupField.Name          => "Renamed",
        StackupField.Thickness     => "17",
        StackupField.Sigma         => "3.5e+07",
        StackupField.Epsr          => "9.8",
        StackupField.TanD          => "0.0031",
        StackupField.Mur           => "1.7",
        StackupField.WallThickness => "12",
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    // ── R-stk4-3 — the gesture ───────────────────────────────────────────────────────────────────

    /// <summary>A single click that opened an editor would be an editor the user only meant to click
    /// past — <c>InlineEditText</c>'s own doc comment records that finding.</summary>
    [Fact]
    public void ASingleClickSelectsAndOpensNothing_ADoubleClickOpens()
    {
        var s = Open();
        var p = PointOn(s.Canvas, "TopMetal", StackupField.Thickness);

        Assert.True(s.Canvas.PressAt(p));
        Assert.False(s.Editor.IsOpen);
        Assert.Equal("TopMetal", s.Vm.SelectedStackupLayerName);

        Assert.True(s.Canvas.DoubleClickAt(p));
        Assert.True(s.Editor.IsOpen);
        Assert.Equal(StackupField.Thickness, s.Editor.OpenField);
        Assert.Equal("TopMetal", s.Editor.OpenLayerName);
    }

    /// <summary>A static word, a unit, a boundary note: all <see cref="StackupField.None"/>. A
    /// double-click on one has already selected, and does nothing extra.</summary>
    [Fact]
    public void ADoubleClickOnANoneLabelOpensNothing()
    {
        var s = Open();
        var scene = s.Canvas.SceneCache.Current!;

        var inert = scene!.Labels.Where(l => l.Field == StackupField.None).ToList();
        Assert.NotEmpty(inert);

        foreach (var label in inert)
        {
            var p = new Point((label.Rect.Left + label.Rect.Right) / 2.0,
                              (label.Rect.Top + label.Rect.Bottom) / 2.0);
            Assert.False(s.Canvas.DoubleClickAt(p), $"\"{label.Text}\" must not open an editor");
            Assert.False(s.Editor.IsOpen);
        }
    }

    /// <summary>
    /// §4. A span is a PAIR of conductor names, chosen from the card's combos and dragged in brief 5.
    /// Its label carries the field for selection and reporting; this editor declines it.
    /// </summary>
    [Fact]
    public void ADoubleClickOnASpanOpensNothing()
    {
        var s = Open();
        var p = PointOn(s.Canvas, "Through", StackupField.Span);

        Assert.True(s.Canvas.PressAt(p));            // the first press of the double-click
        Assert.False(s.Canvas.DoubleClickAt(p));
        Assert.False(s.Editor.IsOpen);
        Assert.Equal("Through", s.Vm.SelectedStackupLayerName);   // …but it still selected
    }

    // ── R-stk4-4 — what the box opens holding, and where ─────────────────────────────────────────

    /// <summary>
    /// <b>Character for character, from the row VM's staged string</b> — never from a freshly
    /// formatted number. Two formattings in two places is how the drawing and the card come to
    /// disagree about a trailing zero, and then an edit that touched nothing writes a changed file.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryField))]
    public void TheBoxOpensSeededWithTheRowVmsStagedString(StackupField field, string layer)
    {
        var s = Open();
        Assert.True(s.Canvas.DoubleClickAt(PointOn(s.Canvas, layer, field)));

        Assert.Equal(Staged(Row(s.Vm, layer), field), s.Box.Text);
    }

    /// <summary>The box lands on the label's own text origin — <c>TextX</c> and the Skia
    /// <c>Baseline</c> the renderer drew it at, at the size the scene measured it with. Not the hit
    /// rect's corner, which is the padded rect and is a pixel and a half to the left.</summary>
    [Theory]
    [MemberData(nameof(EveryField))]
    public void TheBoxIsPlacedOnTheLabelsOwnTextOriginAndSize(StackupField field, string layer)
    {
        var s = Open();
        var label = LabelOn(s.Canvas, layer, field);
        Assert.True(s.Canvas.DoubleClickAt(PointOn(s.Canvas, layer, field)));

        double size = StackupScene.FontSizeFor(label.Style);
        Assert.Equal(size, s.Box.FontSize, 6);
        Assert.Equal(SchematicInlineEditBox.MarginFor(label.TextX, label.Baseline, size), s.Box.Margin);
        Assert.Equal(SchematicInlineEditBox.WidthFor(s.Box.Text, size), s.Box.Width, 6);
    }

    /// <summary>
    /// <b>The unit is left standing.</b>
    ///
    /// <para>On THIS surface every staged string turns out to carry only the number — a thickness
    /// stages "1.6" and the drawing draws the "µm" as its own separate label piece — so the rule
    /// resolves to selecting the whole string, which is the same outcome: type, and you replace the
    /// value. The assertion is therefore against the shared rule rather than against a spelling, plus
    /// a direct check that the rule itself still leaves a unit alone when there is one, so a later
    /// change to how a value is staged cannot silently turn "type over the number" into "type over
    /// the number and its unit".</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryField))]
    public void TheValueIsPreselectedAndAnyUnitIsLeftStanding(StackupField field, string layer)
    {
        var s = Open();
        Assert.True(s.Canvas.DoubleClickAt(PointOn(s.Canvas, layer, field)));

        string text = s.Box.Text ?? "";
        Assert.Equal(0, s.Box.SelectionStart);
        Assert.Equal(InlineEdit.ValueSelectionLength(text), s.Box.SelectionEnd);
        Assert.DoesNotContain(' ', text);   // the staged strings carry no unit of their own

        // The rule, on a string that does carry one.
        Assert.Equal(3, InlineEdit.ValueSelectionLength("1.6 mm"));
    }

    // ── R-stk4-5 — THE BYTE GATE ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The test that holds the whole series' rule.</b> Editing a value through the canvas and
    /// editing it through the card's own text box must produce the same file, byte for byte, and one
    /// undo entry each — because the canvas path does exactly two things, and they are the two the
    /// card's own LostFocus handler does.
    ///
    /// <para>"One undo entry" is measured as: the stack can be undone once, the undo restores the
    /// technology the editor opened on, and then it cannot be undone again.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryField))]
    public void EditingOnTheDrawingAndEditingOnTheCardWriteTheSameBytes(StackupField field, string layer)
    {
        string value = NewValueFor(field);

        // The drawing.
        var drawn = Open($"drawn-{field}");
        string before = TechPersistence.Serialize(drawn.Vm.Working);
        Assert.True(drawn.Canvas.DoubleClickAt(PointOn(drawn.Canvas, layer, field)));
        drawn.Box.Text = value;
        drawn.Editor.Commit();

        // The card.
        var carded = Editor($"carded-{field}");
        Assert.Equal(before, TechPersistence.Serialize(carded.Working));
        StackupInlineEditor.Apply(Row(carded, layer), field, value);

        Assert.Equal(TechPersistence.Serialize(carded.Working),
                     TechPersistence.Serialize(drawn.Vm.Working));
        Assert.NotEqual(before, TechPersistence.Serialize(drawn.Vm.Working));

        foreach (var vm in new[] { drawn.Vm, carded })
        {
            Assert.True(vm.UndoRedo.CanUndo);
            vm.UndoRedo.Undo();
            Assert.Equal(before, TechPersistence.Serialize(vm.Working));
            Assert.False(vm.UndoRedo.CanUndo);
        }
    }

    /// <summary>
    /// A bad value behaves on the drawing exactly as it does on the card: the model is untouched, no
    /// undo entry is pushed, and the row's EXISTING error property is set. There is deliberately no
    /// second refusal message on the canvas — one source of truth for the rule means one source of
    /// truth for what it says when the rule is broken.
    /// </summary>
    [Fact]
    public void AnUnparseableValueChangesNothing_AndSetsTheRowsOwnError()
    {
        var s = Open();
        string before = TechPersistence.Serialize(s.Vm.Working);

        Assert.True(s.Canvas.DoubleClickAt(PointOn(s.Canvas, "TopMetal", StackupField.Thickness)));
        s.Box.Text = "not a length";
        s.Editor.Commit();

        Assert.Equal(before, TechPersistence.Serialize(s.Vm.Working));
        Assert.False(s.Vm.UndoRedo.CanUndo);

        var row = Row(s.Vm, "TopMetal");
        Assert.True(row.HasThicknessError);

        // …the same three outcomes the card produces for the same input.
        var card = Editor("card-refusal");
        StackupInlineEditor.Apply(Row(card, "TopMetal"), StackupField.Thickness, "not a length");
        Assert.Equal(before, TechPersistence.Serialize(card.Working));
        Assert.False(card.UndoRedo.CanUndo);
        Assert.Equal(Row(card, "TopMetal").ThicknessError, row.ThicknessError);
    }

    /// <summary><c>CommitEdit</c> no-ops when nothing changed, so opening an editor and pressing
    /// Return without typing costs no undo entry — for free, and only because this path goes through
    /// it.</summary>
    [Theory]
    [MemberData(nameof(EveryField))]
    public void OpeningAndCommittingWithoutTypingCostsNoUndoEntry(StackupField field, string layer)
    {
        var s = Open($"noop-{field}");
        string before = TechPersistence.Serialize(s.Vm.Working);

        Assert.True(s.Canvas.DoubleClickAt(PointOn(s.Canvas, layer, field)));
        s.Editor.Commit();

        Assert.Equal(before, TechPersistence.Serialize(s.Vm.Working));
        Assert.False(s.Vm.UndoRedo.CanUndo);
    }

    // ── R-stk4-6 — the three keys ────────────────────────────────────────────────────────────────

    /// <summary>Escape closes the box and writes nothing — not even the staged string, so the card
    /// below is untouched as well as the model.</summary>
    [Fact]
    public void EscapeRevertsAndLeavesTheModelAndTheCardUntouched()
    {
        var s = Open();
        string before = TechPersistence.Serialize(s.Vm.Working);
        string staged = Row(s.Vm, "TopMetal").StagedThicknessText;

        Assert.True(s.Canvas.DoubleClickAt(PointOn(s.Canvas, "TopMetal", StackupField.Thickness)));
        s.Box.Text = "999";
        s.Editor.Revert();

        Assert.False(s.Editor.IsOpen);
        Assert.Equal(before, TechPersistence.Serialize(s.Vm.Working));
        Assert.Equal(staged, Row(s.Vm, "TopMetal").StagedThicknessText);
        Assert.False(s.Vm.UndoRedo.CanUndo);
    }

    /// <summary>Hiding a focused box raises <c>LostFocus</c>, which is itself a commit path. One
    /// Return must still cost one undo entry.</summary>
    [Fact]
    public void ASecondCommitOnAClosedBoxDoesNothing()
    {
        var s = Open();
        string before = TechPersistence.Serialize(s.Vm.Working);

        Assert.True(s.Canvas.DoubleClickAt(PointOn(s.Canvas, "TopMetal", StackupField.Thickness)));
        s.Box.Text = "17";
        s.Editor.Commit();
        s.Editor.Commit();   // what LostFocus would deliver on the way out

        Assert.True(s.Vm.UndoRedo.CanUndo);
        s.Vm.UndoRedo.Undo();
        Assert.Equal(before, TechPersistence.Serialize(s.Vm.Working));
        Assert.False(s.Vm.UndoRedo.CanUndo);
    }

    /// <summary>
    /// <b>Closing the box raises <c>Closed</c>, which is what puts keyboard focus back in the tab.</b>
    ///
    /// <para>Owner, 2026-09-13: a second Esc did not clear the band selection. The first reverted
    /// correctly and then nothing in the editor held focus — Avalonia drops it when the focused
    /// control is hidden — so the second keystroke routed nowhere near the view whose handler clears
    /// the selection. The same gap silenced Page Up/Down after any committed edit.</para>
    ///
    /// <para>Focus itself needs an application host, so what is asserted here is the EVENT and its
    /// conditions; that the host focuses the view on it is the scan below.</para>
    /// </summary>
    [Fact]
    public void ClosingTheBoxRaisesClosed_SoTheHostCanTakeFocusBack()
    {
        var s = Open();
        var p = PointOn(s.Canvas, "TopMetal", StackupField.Thickness);
        int closed = 0;
        s.Editor.Closed += () => closed++;

        // A Close with nothing open must NOT fire: the scene rebuild after every committed edit, undo
        // and redo calls it (R-stk4-8), and focus must not be yanked out of a card the user is typing
        // in on the strength of that.
        s.Editor.Close();
        Assert.Equal(0, closed);

        // Escape.
        Assert.True(s.Canvas.DoubleClickAt(p));
        s.Editor.Revert();
        Assert.Equal(1, closed);
        Assert.False(s.Canvas.InlineEditIsOpen);

        // Return, which closes before it writes (R-stk4-7) — the same need for focus back.
        Assert.True(s.Canvas.DoubleClickAt(p));
        s.Editor.Commit();
        Assert.Equal(2, closed);

        // …and it is raised with the editor already shut, so a handler cannot take focus out of a box
        // that is halfway through closing.
        bool openWhenRaised = true;
        s.Editor.Closed += () => openWhenRaised = s.Editor.IsOpen;
        Assert.True(s.Canvas.DoubleClickAt(p));
        s.Editor.Revert();
        Assert.False(openWhenRaised);
    }

    /// <summary>The host's half: the view takes focus back on <c>Closed</c>, to the same target
    /// <c>FocusForScrollingDeferred</c> already uses.</summary>
    [Fact]
    public void TheViewTakesFocusBackWhenTheBoxCloses()
    {
        var code = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml.cs"));
        Assert.Contains("StackupInlineEditor.Closed += () => Focus();", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Esc's two jobs, and the precedence with brief 3. While the box is open the keystroke belongs to
    /// the box and the selection stands; a second Esc, box closed, clears it.
    ///
    /// <para>The composition lives in <c>TechEditorView.OnEscapeKeyDown</c>, which needs an
    /// application host to raise a key into — so what is asserted here is the two states it switches
    /// on, and the scan below asserts that it switches on them.</para>
    /// </summary>
    [Fact]
    public void WithTheBoxOpenEscapeLeavesTheSelectionStanding_ASecondOneClearsIt()
    {
        var s = Open();
        var p = PointOn(s.Canvas, "TopMetal", StackupField.Thickness);

        Assert.True(s.Canvas.PressAt(p));
        Assert.True(s.Canvas.DoubleClickAt(p));
        Assert.True(s.Canvas.InlineEditIsOpen);

        s.Editor.Revert();
        Assert.False(s.Canvas.InlineEditIsOpen);
        Assert.Equal("TopMetal", s.Vm.SelectedStackupLayerName);

        s.Vm.ClearStackupSelection();
        Assert.Null(s.Vm.SelectedStackupLayerName);
    }

    // ── R-stk4-7 / R-stk4-8 — the box closes ─────────────────────────────────────────────────────

    /// <summary>Before the commit, not after: the commit rebuilds every row VM and the scene, so a box
    /// left open is pointing at a rect that no longer exists.</summary>
    [Fact]
    public void ACommitClosesTheBox()
    {
        var s = Open();
        Assert.True(s.Canvas.DoubleClickAt(PointOn(s.Canvas, "TopMetal", StackupField.Thickness)));
        s.Box.Text = "17";
        s.Editor.Commit();

        Assert.False(s.Editor.IsOpen);
        Assert.False(s.Box.IsVisible);
    }

    [Fact]
    public void AnUndoClosesTheBox()
    {
        var s = Open();

        // Something to undo, from the card below.
        var row = Row(s.Vm, "Core");
        row.StagedEpsr = "3.66";
        row.CommitEpsr();

        Assert.True(s.Canvas.DoubleClickAt(PointOn(s.Canvas, "TopMetal", StackupField.Thickness)));
        Assert.True(s.Editor.IsOpen);

        s.Vm.UndoRedo.Undo();
        Assert.False(s.Editor.IsOpen);
    }

    [Fact]
    public void ARedoClosesTheBox()
    {
        var s = Open();
        var row = Row(s.Vm, "Core");
        row.StagedEpsr = "3.66";
        row.CommitEpsr();
        s.Vm.UndoRedo.Undo();

        Assert.True(s.Canvas.DoubleClickAt(PointOn(s.Canvas, "TopMetal", StackupField.Thickness)));
        Assert.True(s.Editor.IsOpen);

        s.Vm.UndoRedo.Redo();
        Assert.False(s.Editor.IsOpen);
    }

    [Fact]
    public void AnEditCommittedFromTheCardClosesTheBox()
    {
        var s = Open();
        Assert.True(s.Canvas.DoubleClickAt(PointOn(s.Canvas, "TopMetal", StackupField.Thickness)));
        Assert.True(s.Editor.IsOpen);

        var row = Row(s.Vm, "Core");
        row.StagedEpsr = "3.66";
        row.CommitEpsr();

        Assert.False(s.Editor.IsOpen);
    }

    /// <summary>Adding an entry rebuilds the scene too — brief 6's Add Via arrives through the same
    /// event, and so does every other command on the header row.</summary>
    [Fact]
    public void AddingAStackupEntryClosesTheBox()
    {
        var s = Open();
        Assert.True(s.Canvas.DoubleClickAt(PointOn(s.Canvas, "TopMetal", StackupField.Thickness)));
        Assert.True(s.Editor.IsOpen);

        s.Vm.AddViaLayerCommand.Execute(null);
        Assert.False(s.Editor.IsOpen);
    }

    // ── R-stk4-1 / R-stk4-2 / R-stk4-6 — the hosting, by source scan ─────────────────────────────

    /// <summary>
    /// R-stk4-1. <c>SchematicInlineEditBox.WidthFor</c> measures with <c>SkiaFonts.PlexRegular</c>, so
    /// the box's own text is drawn in that face and its width is right for it — which is what makes
    /// the box line up with what it replaces. The scene draws the six SPEC values in the same face;
    /// a band NAME is <c>PlexSemiBold</c>, and is the one label whose glyphs differ from the box's.
    /// The box's background is opaque, so it covers the label rather than sitting beside it, and the
    /// measurement is of what the box actually draws either way — recorded here so the difference is
    /// a known one rather than something rediscovered as a misalignment.
    /// </summary>
    [Fact]
    public void EverySpecValueIsDrawnAtTheSizeTheBoxOpensAt()
    {
        var s = Open();
        var scene = s.Canvas.SceneCache.Current!;

        foreach (var label in scene.Labels.Where(l => l.Field is not (StackupField.None or StackupField.Span)))
        {
            double size = StackupScene.FontSizeFor(label.Style);
            Assert.True(size > 0);
            Assert.Equal(label.Style is StackupLabelStyle.BandName or StackupLabelStyle.ColumnName
                             ? StackupScene.BandNameSize
                             : StackupScene.SpecSize,
                         size, 6);
        }
    }

    /// <summary>R-stk4-2. One box, a sibling of the drawing in one Panel, hidden at rest. Never one
    /// per label, and never a hand-rolled box.</summary>
    [Fact]
    public void TheViewHostsExactlyOneBoxAsASiblingOfTheDrawing()
    {
        var tab = StackupTab();

        int panel  = Require(tab, "<Panel>");
        int canvas = Require(tab, "<ctrl:StackupCanvas x:Name=\"StackupDrawing\"");
        int box    = Require(tab, "<ctrl:SchematicInlineEditBox x:Name=\"StackupInlineEdit\"");

        Assert.True(panel < canvas && canvas < box);
        Assert.True(box < tab.IndexOf("</Panel>", StringComparison.Ordinal));

        // Exactly one.
        Assert.Equal(box, tab.LastIndexOf("<ctrl:SchematicInlineEditBox", StringComparison.Ordinal));

        var element = tab[box..tab.IndexOf("/>", box, StringComparison.Ordinal)];
        Assert.Contains("IsVisible=\"False\"", element);
    }

    /// <summary>
    /// R-stk4-2, <b>measured rather than assumed</b> — the brief asks for exactly that, and the
    /// fallback if it were false (close the editor on scroll) is a different design.
    ///
    /// <para>A <c>ScrollContentPresenter</c> arranges its content at a NEGATIVE offset and leaves
    /// everything inside it alone, so a child's <c>Margin</c> is in CONTENT coordinates: the box's
    /// position within the panel does not move when the pane scrolls, and the box therefore travels
    /// with the drawing it is sitting on. The presenter is used directly because a whole
    /// <c>ScrollViewer</c> is a templated control and would find no theme in this project — which is
    /// the very failure <c>SchematicInlineEditBox.StyleKeyOverride</c> documents.</para>
    /// </summary>
    [Fact]
    public void AMarginInsideTheDrawingsScrollerIsInContentCoordinates()
    {
        var box  = new Border { Width = 20, Height = 10, Margin = new Thickness(5, 40, 0, 0),
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                                VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Top };
        var tall = new Border { Width = 100, Height = 300 };   // stands in for a tall stackup
        var panel = new Panel();
        panel.Children.Add(tall);
        panel.Children.Add(box);

        var presenter = new ScrollContentPresenter
        {
            Content = panel, CanVerticallyScroll = true, CanHorizontallyScroll = false,
        };
        presenter.UpdateChild();
        presenter.Measure(new Size(100, 100));
        presenter.Arrange(new Rect(0, 0, 100, 100));

        Assert.Equal(new Size(100, 300), presenter.Extent);
        Assert.Equal(new Point(5, 40), box.TranslatePoint(default, presenter));

        presenter.Offset = new Vector(0, 30);
        presenter.Measure(new Size(100, 100));
        presenter.Arrange(new Rect(0, 0, 100, 100));

        // The box has not moved within the panel …
        Assert.Equal(new Rect(5, 40, 20, 10), box.Bounds);
        // … and the panel is what scrolled, carrying it.
        Assert.Equal(new Point(5, 10), box.TranslatePoint(default, presenter));
    }

    /// <summary>R-stk4-6. The contract is the host's to wire, because the control raises nothing and
    /// handles no key itself.</summary>
    [Fact]
    public void TheThreeKeysAreWiredToTheEditor()
    {
        var tab  = StackupTab();
        var code = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml.cs"));

        Assert.Contains("KeyDown=\"OnStackupInlineEditKeyDown\"", tab);
        Assert.Contains("LostFocus=\"OnStackupInlineEditLostFocus\"", tab);

        int keys = Require(code, "private void OnStackupInlineEditKeyDown");
        var body = code[keys..code.IndexOf("private void OnStackupInlineEditLostFocus", keys, StringComparison.Ordinal)];
        Assert.Contains("Key.Return or Key.Enter", body);
        Assert.Contains("StackupInlineEditor.Commit()", body);
        Assert.Contains("Key.Escape", body);
        Assert.Contains("StackupInlineEditor.Revert()", body);

        // LostFocus commits. This is the one that costs the user an edit if it is missed.
        int lost = Require(code, "private void OnStackupInlineEditLostFocus");
        Assert.Contains("StackupInlineEditor.Commit()", code[lost..(lost + 200)]);
    }

    /// <summary>
    /// R-stk4-6's collision with brief 3, and <b>the view's tunnelling handler does the revert itself
    /// — registered <c>handledEventsToo: true</c>, without which it never runs at all</b>.
    ///
    /// <para>Owner-reported twice (2026-09-13). A docked document sits inside <c>WorkspaceWindow</c>,
    /// which carries <c>&lt;KeyBinding Gesture="Escape" …/&gt;</c>, and a Window's KeyBindings are
    /// evaluated BEFORE visual-tree routing begins — so Escape arrives at this view already marked
    /// Handled and an ordinary handler is skipped. It is the third instance in this application:
    /// <c>SchematicView.OnViewKeyDownTunnel</c> and <c>ReadoutStripView.OnStripKeyDownTunnel</c> each
    /// hit it for their own inline editor and each names the mechanism in a comment.</para>
    ///
    /// <para>The flag is asserted ALONGSIDE the window's binding that makes it necessary, so this
    /// test says why rather than merely that — and fails loudly if that binding ever goes away and
    /// somebody wonders whether the flag is still earning its place.</para>
    /// </summary>
    [Fact]
    public void TheViewsEscapeHandlerRevertsTheOpenBoxItself_BeforeClearingTheSelection()
    {
        var code = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml.cs"));

        Assert.Contains(
            "AddHandler(KeyDownEvent, OnEscapeKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);",
            code, StringComparison.Ordinal);

        // The reason it is needed: the window marks Escape handled before routing starts.
        Assert.Contains("<KeyBinding Gesture=\"Escape\"",
            RepoFile(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml")), StringComparison.Ordinal);

        int esc = Require(code, "private void OnEscapeKeyDown");
        var body = code[esc..code.IndexOf("private void OnActivationFocusRequested", esc, StringComparison.Ordinal)];

        int guard  = body.IndexOf("InlineEditIsOpen", StringComparison.Ordinal);
        int revert = body.IndexOf("StackupInlineEditor.Revert()", StringComparison.Ordinal);
        int clear  = body.IndexOf("ClearStackupSelection", StringComparison.Ordinal);

        Assert.True(guard >= 0, "the handler must check for an open editor");
        Assert.True(revert > guard, "…and REVERT it rather than leaving the keystroke to the box");
        Assert.True(revert < clear, "…before it clears the selection");
    }

    /// <summary>
    /// §4, and the reason it is stated: the schematic page's own inline editing has already been taken
    /// out once, silently, by a one-line change to a shared control. Nothing in this brief's own
    /// files may reach into those three.
    /// </summary>
    [Fact]
    public void TheSharedInlineEditControlsAreUsedAndNotReimplemented()
    {
        var editor = RepoFile(Path.Combine("src", "Ui", "Controls", "StackupInlineEditor.cs"));

        Assert.Contains("_box.Open(", editor);
        Assert.Contains("_box.SelectValueOnly()", editor);

        // No second placement arithmetic and no second width rule: the two constants a hand-rolled
        // host would re-derive are never named here.
        Assert.DoesNotContain("AscenderRatio", editor);
        Assert.DoesNotContain("MarginFor", editor);
        Assert.DoesNotContain("WidthFromMeasuredText", editor);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static string StackupTab()
    {
        var axaml = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml"));
        int start = axaml.IndexOf("<TabItem Header=\"{Binding ViewModel.StackupTabHeader}\">",
                                  StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = axaml.IndexOf("<TabItem Header=\"{Binding ViewModel.DrcTabHeader}\">", start,
                                StringComparison.Ordinal);
        Assert.True(end > start);
        return axaml[start..end];
    }

    private static int Require(string haystack, string needle)
    {
        int i = haystack.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, $"expected to find: {needle}");
        return i;
    }

    private static string RepoFile(string rel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, rel));
    }
}
