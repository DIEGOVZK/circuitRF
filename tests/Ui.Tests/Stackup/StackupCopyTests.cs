using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Views.Layout;
using SkiaSharp;
using Xunit;

// The namespace is NOT `CircuitRF.Ui.Tests.Stackup`, though the folder is — see StackupSceneTests.cs
// for the shadowing that rule exists to avoid.
namespace CircuitRF.Ui.Tests.StackupRender;

/// <summary>
/// brief-stackup-render-7-copy.md's gate.
///
/// <para>A clipboard WRITE is not assertable headlessly — there is no clipboard in this process and
/// <c>PlotExporter.SetClipboardDataAsync</c> is the shared path this brief deliberately does not
/// re-implement. So every test here asserts everything up TO the write, which is where all of this
/// brief's decisions actually are: which scene, at what size, in which colours, with what overlay.</para>
/// </summary>
// SkiaFontsTypefaceCollection: this class ASSERTS OVER RENDERED TEXT BYTES, which is the second
// half of that collection's stated membership rule and the half it says is easy to miss — a class
// like this one looks as though it touches no global at all. It does not set either typeface static;
// what it cannot survive is another class setting one WHILE it renders. Caught in a full-solution
// run (2026-09-13): two builds of one scene came back with `font-family="Helvetica"` on one side and
// IBM Plex on the other, and the same test passed alone.
[Collection(CircuitRF.Ui.Tests.SkiaFontsTypefaceCollection.Name)]
public class StackupCopyTests
{
    /// <summary>The width the scene is LAID OUT at — PlotExporter's own letter landscape, restated
    /// so a test that measures it does not read it from the subject. It is no longer the width of the
    /// page the picture is composed onto: see <see cref="StackupGraphicExport.PageFor"/>.</summary>
    private const float LayoutWidth = 792f;

    private static PagePlacement Page(Technology? tech)
        => StackupGraphicExport.PageFor(StackupGraphicExport.PageScene(tech));

    public static TheoryData<string> ShippedIds()
    {
        var data = new TheoryData<string>();
        foreach (var entry in ShippedTechnologies.All) data.Add(entry.Id);
        return data;
    }

    private static Technology Shipped(string id) => ShippedTechnologies.Load(id);

    // ── R-stk7-2 — the whole stackup, never the scrolled window ───────────────────────────────────

    /// <summary>
    /// <b>The crop test, and the one that matters.</b> A copy of what happens to be visible is a
    /// picture missing layers, and a picture missing layers is not obviously wrong to anyone reading
    /// it later — which is what makes the failure worth a test rather than an eye.
    ///
    /// <para>The fixture is taller than the pane it is drawn in by a factor of several, so at the
    /// control's own height most of these names are scrolled away; every one of them is still in the
    /// copied picture.</para>
    /// </summary>
    [Fact]
    public void EveryBandNameIsCopied_IncludingTheOnesThePaneScrollsAway()
    {
        var tech  = Tall(14);
        var scene = StackupGraphicExport.PageScene(tech);

        // Non-vacuity: this stack genuinely does not fit the drawing pane, so a screenshot of the
        // control would have cropped it.
        Assert.True(scene.Height > TechEditorMetrics.StackupDrawingMinHeight * 3,
            $"fixture is not tall enough to be a crop test: {scene.Height} px");

        string svg = StackupGraphicExport.BuildSvgString(tech, StackupRenderTheme.Light);

        foreach (var layer in tech.Stackup.Layers)
            Assert.Contains(layer.Name, svg, StringComparison.Ordinal);
    }

    /// <summary>The same, on every technology circuitRF ships — so a new one joins this gate the day
    /// it lands rather than the day someone remembers to add it.</summary>
    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void EveryBandNameIsCopied_OnEveryShippedTechnology(string id)
    {
        var tech = Shipped(id);
        string svg = StackupGraphicExport.BuildSvgString(tech, StackupRenderTheme.Light);

        foreach (var layer in tech.Stackup.Layers)
            Assert.Contains(layer.Name, svg, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The picture FILLS its page, edge to edge, with only the padding around it</b> — owner,
    /// 2026-09-13: pasted into a presentation the cross-section arrived inside a much larger empty
    /// box, which is what a tall narrow drawing centred on letter landscape is.
    ///
    /// <para>Measured off the composed raster rather than off the arithmetic that produced it: the
    /// question is where the ink actually landed, and a test that re-ran <c>FitScale</c> and compared
    /// it with itself would pass however the canvas was transformed.</para>
    ///
    /// <para>Every shipped technology AND the tall fixture, because the bug is a function of the
    /// drawing's aspect ratio and the shipped stackups differ in it by a factor of several.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void ThePictureFillsThePage_OnEveryShippedTechnology(string id) => AssertFillsPage(Shipped(id));

    [Fact]
    public void ThePictureFillsThePage_OnAStackTallerThanALetterPage()
    {
        var tech = Tall(14);
        // Non-vacuity: this is the shape that was worst before — far taller than the page it used to
        // be scaled down onto.
        Assert.True(StackupGraphicExport.PageScene(tech).Height > 612f,
            "fixture is not the tall case this is about");
        AssertFillsPage(tech);
    }

    private static void AssertFillsPage(Technology tech)
    {
        var page = Page(tech);
        var (minX, minY, maxX, maxY) = PaintedBounds(tech, StackupRenderTheme.Light);

        float pad = StackupGraphicExport.PagePad;

        // The ink reaches the padding on all four sides — nothing is cropped, and nothing beyond the
        // padding is blank. One pixel of slack, because the bounds are read off a raster and the page
        // is a fractional number of points.
        Near(pad, minX, "left");
        Near(pad, minY, "top");
        Near(page.Width  - pad, maxX, "right");
        Near(page.Height - pad, maxY, "bottom");

        static void Near(float expected, float actual, string edge)
            => Assert.True(Math.Abs(expected - actual) <= 1.5f,
                           $"{edge} edge at {actual}, expected {expected}");

        // …which is only worth anything if the page is the drawing rather than a sheet: the blank
        // fraction of the page is the padding and nothing else.
        float drawn = StackupGraphicExport.PageScene(tech).Width;
        Assert.True(page.Width < 2 * pad + drawn + 1, $"page is wider than the drawing: {page.Width}");
    }

    /// <summary>
    /// The page composition is a pure function of the TECHNOLOGY — never of the pane's width, which
    /// is the one number a screenshot or a reused on-screen scene would have carried into it.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void ThePictureIsLaidOutFromTheTechnologyAlone(string id)
    {
        var tech  = Shipped(id);
        float w = StackupGraphicExport.PageScene(tech).Width;

        // At least the letter-landscape width it starts from, and the same answer every time — the
        // pane it happens to be drawn in on screen reaches none of this.
        Assert.True(w >= LayoutWidth, $"laid out narrower than the page: {w}");
        Assert.Equal(w, StackupGraphicExport.PageScene(Shipped(id)).Width, 3);
    }

    /// <summary>
    /// <b>No spec in a copied picture is wrapped onto a second line</b> (owner, 2026-09-13).
    ///
    /// <para>A pane has a width the user chose and the drawing makes the best of it; a copied picture
    /// has no such constraint — it is vector, it is going into a document, and a page is only as wide
    /// as it is asked to be. So the page grows until the label column holds its widest group on one
    /// line.</para>
    ///
    /// <para><b>Non-vacuous</b>: the second half shows that some shipped technology genuinely DOES
    /// wrap at the letter width, so the widening is doing something rather than describing a page that
    /// already fitted.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void TheCopiedPictureNeverWrapsASpec(string id)
        => Assert.False(StackupGraphicExport.PageScene(Shipped(id)).LabelsWrap);

    [Fact]
    public void AtTheLetterWidthSomeShippedTechnologyWouldHaveWrapped()
    {
        Assert.Contains(ShippedTechnologies.All,
            e => StackupScene.Build(Shipped(e.Id), LayoutWidth).LabelsWrap);
    }

    /// <summary>A technology that already fits is NOT widened — the copy stays letter-landscape
    /// wherever it can, which is the page every other picture in this application composes onto.</summary>
    [Fact]
    public void ATechnologyThatAlreadyFitsIsNotWidened()
    {
        var fits = ShippedTechnologies.All
            .Select(e => Shipped(e.Id))
            .First(t => !StackupScene.Build(t, LayoutWidth).LabelsWrap);

        Assert.Equal(LayoutWidth, StackupGraphicExport.PageScene(fits).Width, 3);
    }

    /// <summary>The widening is bounded. A layer name long enough to demand an unopenable page gets
    /// the widest page the export will lay out, and wraps — which is what every copy did before.</summary>
    [Fact]
    public void TheWideningIsCappedRatherThanUnbounded()
    {
        var tech = Shipped("pcb-2layer_RO4350B_20mil_1oz");
        var band = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Dielectric);
        band.Name = new string('W', 4000);

        float w = StackupGraphicExport.PageScene(tech).Width;
        Assert.InRange(w, LayoutWidth, StackupGraphicExport.MaxPageWidth);
    }

    // ── R-stk7-3 — an export is artwork, not an editing surface ───────────────────────────────────

    /// <summary>
    /// <b>A selection changes nothing about the copied picture.</b>
    ///
    /// <para>Byte-identical, and NOT vacuously so: the second half of this test draws the same scene
    /// on the same page with a selection overlay and shows that it would have been visible. Without
    /// that half, a renderer that had stopped drawing selections at all would pass.</para>
    /// </summary>
    [Fact]
    public void ASelectionIsNotPartOfTheCopiedPicture()
    {
        var tech  = Shipped(ShippedTechnologies.All.First().Id);
        var scene = StackupGraphicExport.PageScene(tech);
        var theme = StackupRenderTheme.Light;

        var vm = new TechEditorViewModel(
            Path.Combine(Path.GetTempPath(), "stackup-copy.ctech"), tech);
        vm.SelectedStackupLayerName = scene.Bands[0].Name;
        string selected = StackupGraphicExport.BuildSvgString(vm.Working, theme);

        vm.ClearStackupSelection();
        string cleared = StackupGraphicExport.BuildSvgString(vm.Working, theme);

        Assert.Equal(cleared, selected, StringComparer.Ordinal);

        // The overlay the export refuses to draw is one that WOULD have shown.
        string withOverlay = PlotDocumentWriter.BuildSvgString(
            canvas => RenderWithOverlay(canvas, scene, theme,
                new StackupOverlay { SelectedLayer = scene.Bands[0].Name }),
            PagePlacement.Letter);

        Assert.NotEqual(cleared, withOverlay);
    }

    /// <summary>Hover, grippers and a drag's chrome are the same answer as the selection's — none of
    /// them is a parameter of this export at all, which is what makes them unreachable.</summary>
    [Fact]
    public void NoOverlayReachesTheCopiedPicture()
    {
        var tech  = Shipped(ShippedTechnologies.All.First().Id);
        var scene = StackupGraphicExport.PageScene(tech);
        var theme = StackupRenderTheme.Light;

        string plain = StackupGraphicExport.BuildSvgString(tech, theme);

        foreach (var overlay in new[]
        {
            new StackupOverlay { HoverLayer    = scene.Bands[0].Name },
            new StackupOverlay { DragInsertY   = scene.Bands[0].Rect.Bottom },
            new StackupOverlay { DragGhost     = (scene.BandColumn.Left, 10f, scene.BandColumn.Right, 40f) },
        })
        {
            string chromed = PlotDocumentWriter.BuildSvgString(
                canvas => RenderWithOverlay(canvas, scene, theme, overlay), PagePlacement.Letter);

            Assert.NotEqual(plain, chromed);
        }
    }

    // ── Both writers, everywhere ──────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(ShippedIds))]
    public void BothWritersProduceADocument_ForEveryShippedTechnology(string id)
    {
        var tech = Shipped(id);

        Assert.NotEmpty(StackupGraphicExport.BuildPdfBytes(tech, StackupRenderTheme.Light));
        Assert.Contains("<svg", StackupGraphicExport.BuildSvgString(tech, StackupRenderTheme.Light),
            StringComparison.Ordinal);
    }

    /// <summary>An empty stackup copies a blank cross-section rather than throwing or dividing by
    /// zero — and so does a null technology, which is what a canvas with no document bound has.</summary>
    [Fact]
    public void BothWritersProduceADocument_ForAnEmptyStackupAndForNothingAtAll()
    {
        foreach (var tech in new Technology?[] { new Technology(), null })
        {
            Assert.NotEmpty(StackupGraphicExport.BuildPdfBytes(tech, StackupRenderTheme.Light));
            Assert.Contains("<svg", StackupGraphicExport.BuildSvgString(tech, StackupRenderTheme.Light),
                StringComparison.Ordinal);
        }
    }

    // ── R-stk7-4 — the keystroke ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Ctrl</c>/<c>Cmd</c>+<c>C</c> on the Stackup tab copies the picture. Both spellings, because
    /// one of the two is the only one a Mac user ever types.
    /// </summary>
    [Theory]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Meta)]
    public void TheCopyKeystrokeIsTakenOnTheStackupTab(KeyModifiers modifiers)
        => Assert.True(TechEditorView.CopyKeystrokeTakes(Key.C, modifiers, StackupTab, source: null));

    /// <summary>
    /// <b>The other three tabs keep their plain text copy.</b> This editor has four of them and only
    /// one has a picture to copy; claiming the keystroke on the Layers, DRC or Interchange tab would
    /// break Ctrl+C there and give nothing back.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(-1)]
    public void TheCopyKeystrokeIsNotTakenOnAnyOtherTab(int tabIndex)
        => Assert.False(TechEditorView.CopyKeystrokeTakes(Key.C, KeyModifiers.Control, tabIndex, source: null));

    /// <summary>
    /// <b>It never steals the keystroke from a text field.</b> Avalonia's text box only marks Ctrl+C
    /// handled when it actually copied something, so a Ctrl+C typed into a field with nothing selected
    /// reaches this handler — and putting a picture on the clipboard because somebody's text selection
    /// was empty is not what they asked for. The inline value editor over the drawing is a
    /// <c>TextBox</c>, so this covers R-stk4's box as well as the cards' fields.
    /// </summary>
    [Fact]
    public void TheCopyKeystrokeIsNotTakenFromATextInput()
    {
        Assert.False(TechEditorView.CopyKeystrokeTakes(
            Key.C, KeyModifiers.Control, StackupTab, new Avalonia.Controls.TextBox()));
        Assert.False(TechEditorView.CopyKeystrokeTakes(
            Key.C, KeyModifiers.Control, StackupTab, new Avalonia.Controls.SelectableTextBlock()));
    }

    /// <summary>The gesture is the plain one. Ctrl+Shift+C and Ctrl+Alt+C mean other things in other
    /// applications and are not claimed here; an unmodified C is a letter.</summary>
    [Theory]
    [InlineData(Key.C, KeyModifiers.None)]
    [InlineData(Key.C, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(Key.C, KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData(Key.C, KeyModifiers.Meta    | KeyModifiers.Shift)]
    [InlineData(Key.X, KeyModifiers.Control)]
    [InlineData(Key.V, KeyModifiers.Control)]
    public void NoOtherGestureIsClaimed(Key key, KeyModifiers modifiers)
        => Assert.False(TechEditorView.CopyKeystrokeTakes(key, modifiers, StackupTab, source: null));

    /// <summary>
    /// <b>The handler must BUBBLE.</b> R-stk2-10 keeps the drawing non-focusable, so "while the
    /// drawing has focus" cannot be the discriminator the brief names — and a tunnelling handler here
    /// would take Ctrl+C away from every text box in the tab before the box ever saw it, which is the
    /// exact thing R-stk7-4 says not to do. Bubbling is what makes "only a keystroke nothing else
    /// claimed" true, and it is invisible from the handler's own body — hence a scan.
    /// </summary>
    [Fact]
    public void TheCopyHandlerIsRegisteredBubbling_UnlikeTheOtherTwo()
    {
        string code = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml.cs"));

        Assert.Contains("AddHandler(KeyDownEvent, OnCopyKeyDown);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHandler(KeyDownEvent, OnCopyKeyDown, RoutingStrategies",
            code, StringComparison.Ordinal);

        // The other two tunnel on purpose, and this scan is only meaningful while that is still
        // true. Escape additionally takes handledEventsToo — WorkspaceWindow's own KeyBinding marks
        // it handled before routing starts — which Ctrl+C must NOT copy: claiming an already-handled
        // keystroke is the opposite of "only one nothing else wanted".
        Assert.Contains("AddHandler(KeyDownEvent, OnEscapeKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);",
            code, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHandler(KeyDownEvent, OnCopyKeyDown, RoutingStrategies.Tunnel",
            code, StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Stackup tab's index in the editor's <c>SectionTabs</c>.</summary>
    private const int StackupTab = 1;

    /// <summary>
    /// The composed SVG with Skia's own generated resource ids renumbered from zero.
    ///
    /// <para><b>Skia's SVG device numbers <c>clipPath</c> ids from a PROCESS-GLOBAL counter</b>, in
    /// hex — so two SVGs of the same picture, composed one after the other, carry <c>cl_9</c> and
    /// <c>cl_f</c> and are not byte-equal however deterministic the drawing is. It appears on the
    /// transparent-background path only, which is the only one that clips (one id per document; the
    /// opaque path generates none at all).</para>
    ///
    /// <para>Renumbering is what keeps a byte comparison meaningful rather than abandoning it: every
    /// coordinate, colour, order and string is still compared exactly, so the failure this gate
    /// exists for — a layout pass that read a dictionary in hash order — still fails it.</para>
    /// </summary>
    internal static string CanonicalSvg(string svg)
    {
        var seen = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);

        return System.Text.RegularExpressions.Regex.Replace(
            svg, @"(?<=id=""|url\(#)([A-Za-z]+_[0-9a-f]+)(?="")?", m =>
            {
                if (!seen.TryGetValue(m.Value, out var canonical))
                    seen[m.Value] = canonical = $"id{seen.Count}";
                return canonical;
            });
    }

    private static string RepoFile(string relativePath)
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return File.ReadAllText(Path.Combine(dir, relativePath));
    }


    /// <summary>The export's own page composition with an overlay put back — the counterfactual the
    /// R-stk7-3 tests measure against, and the only place in this file that draws one.</summary>
    private static void RenderWithOverlay(
        SKCanvas canvas, StackupScene scene, StackupRenderTheme theme, StackupOverlay overlay)
    {
        var   page  = StackupGraphicExport.PageFor(scene);
        float scale = StackupGraphicExport.FitScale(scene, page);
        canvas.Translate((page.Width  - scene.Width  * scale) * 0.5f,
                         (page.Height - scene.Height * scale) * 0.5f);
        canvas.Scale(scale);
        StackupRenderer.Draw(canvas, scene, theme, overlay);
    }

    /// <summary>The bounding box of everything the composed page actually painted, in page points.
    /// The page starts transparent and the drawing carries its own ground, so "painted" is exactly
    /// "alpha > 0".</summary>
    internal static (float MinX, float MinY, float MaxX, float MaxY) PaintedBounds(
        Technology? tech, StackupRenderTheme theme)
    {
        using var bitmap = Raster(tech, theme);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }

        Assert.True(minX != int.MaxValue, "the composed page painted nothing at all");
        return (minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>The composed page as a 1x raster, so a test can ask where the ink is.</summary>
    internal static SKBitmap Raster(
        Technology? tech, StackupRenderTheme theme, bool transparentBackground = false)
    {
        var scene  = StackupGraphicExport.PageScene(tech);
        var page   = StackupGraphicExport.PageFor(scene);
        var bitmap = new SKBitmap((int)page.Width, (int)page.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            StackupGraphicExport.Render(canvas, scene, theme, page, transparentBackground);
        }
        return bitmap;
    }

    /// <summary>A stackup with <paramref name="pairs"/> conductor/dielectric pairs — taller than the
    /// page, which is the case the whole of R-stk7-2 is about.</summary>
    internal static Technology Tall(int pairs)
    {
        var tech = new Technology { Name = "Tall", DefaultDisplayUnit = LayoutUnit.Um };
        for (int i = 0; i < pairs; i++)
        {
            tech.Stackup.Layers.Add(new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = $"Metal {i + 1}",
                ThicknessDbu = 3_000, SigmaSm = 5.8e7,
            });
            tech.Stackup.Layers.Add(new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = $"Dielectric {i + 1}",
                ThicknessDbu = 20_000, Epsr = 4.4, TanD = 0.02,
            });
        }
        return tech;
    }
}
