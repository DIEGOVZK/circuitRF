using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Clipboard;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Render;

using CircuitRF.Ui.Tests;

/// <summary>
/// RND-1's gates (docs/sonnet-briefs/brief-render-1-render-layer-below-the-firewall.md §5), except
/// gate 1 — the firewall row itself, which is an assembly-reference check and lives in
/// <c>tests/Firewall.Tests/UiFirewallTests.cs</c> with every other one.
///
/// <para><b>What this process is, and why that is the whole point.</b> A headless xunit run has NO
/// live Avalonia app host: <c>Avalonia.Platform.AssetLoader</c> throws
/// <c>InvalidOperationException: Unable to locate 'Avalonia.Platform.IAssetLoader'</c> here. That is
/// exactly the condition <c>circuitrf render</c> will run in, and it is the condition under which
/// the two silent fallbacks R-rnd1-4 and R-rnd1-5 fixed used to fire — a rendered picture in
/// <see cref="SKTypeface.Default"/> instead of the embedded faces, and
/// <c>ColorTheme.BuiltIn</c> instead of the shipped <c>.ccolor</c>, both reported as a success. So
/// the assertions below are not "does the reader work"; they are "does this process, with no
/// framework under it, get the SAME font and the SAME theme the application gets".</para>
///
/// <para><b>What gate 2 can and cannot be at RND-1, measured rather than assumed.</b> The brief asks
/// for byte identity between a render driven from a project with no Avalonia reference and the GUI's
/// own clipboard export. Two things make the literal cross-process form unbuildable until RND-2 adds
/// the <c>render</c> verb, and both were measured here rather than reasoned about:
/// <list type="number">
/// <item>There is no second process to run it in. <c>src/Cli</c> references
/// <c>CircuitRF.Render</c> from this brief onward but has no verb that draws anything yet.</item>
/// <item><b>Two SVG renders in ONE process are never byte-identical anyway.</b> Skia's SVG device
/// assigns globally-incrementing element ids across calls — already recorded in
/// <c>LayoutClipboardExportTests</c>, and re-measured by
/// <see cref="SkiaSvgIdsIncrementAcrossCalls_SoInProcessSvgByteIdentityIsNotAvailable"/> below so
/// the claim is not taken on trust.</item>
/// </list>
/// And a same-process comparison would prove nothing regardless: both sides call the SAME
/// <c>SchematicRenderer.Draw</c> in <c>CircuitRF.Render</c> with the same <c>SkiaFonts</c>, so a
/// typeface difference — the one failure mode gate 2 names — cannot appear between them.
///
/// <para>So gate 2's teeth are here in the constructible form: for each of the three document kinds,
/// each of the two formats, the export is rendered twice — once as the application renders it, once
/// with the typeface substituted — and asserted to DIFFER. That is the vacuity guard the gate needs
/// (a picture whose text is a different face IS a different file, so byte identity is a meaningful
/// thing to demand of it), and gates 3 and 4 then pin that this process takes the real face and the
/// real theme. RND-2 closes the loop by running the same fixture through the verb in a second
/// process and comparing the bytes.</para>
/// </summary>
// This class renders a LabelShape, so it reads LayoutTextOutline.TestOverrideTypeface — a shared
// static several other classes set. See the collection's own note for why that now selects a
// different FACE rather than merely a loadable one.
[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public class RenderLayerBelowFirewallTests(ITestOutputHelper output)
{
    // ── Gate 3: the font is the real font ─────────────────────────────────────

    /// <summary>
    /// The vacuity guard for every font assertion below: the OLD loader really does fail in this
    /// process. Without this, "the family name is DejaVu Sans" could be true because Avalonia
    /// happened to work here, and the fix would be untested.
    /// </summary>
    [Fact]
    public void AvaloniaAssetLoader_StillHasNoHostHere_SoTheOldPathWouldStillHaveFallenBack()
    {
        var ex = Record.Exception(() =>
            Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://CircuitRF.Ui/Assets/Fonts/DejaVuSans.ttf")));

        Assert.NotNull(ex);
        output.WriteLine($"{ex!.GetType().Name}: {ex.Message}");
        Assert.IsType<InvalidOperationException>(ex);
    }

    /// <summary>
    /// R-rnd1-4 / gate 3. Every face <c>SkiaFonts</c> exposes resolves to the real embedded typeface
    /// with no platform at all — NOT to <see cref="SKTypeface.Default"/>, which is what the previous
    /// loader returned here and what would have made every headless picture a different picture.
    /// </summary>
    [Fact]
    public void SkiaFonts_ResolveTheRealEmbeddedFaces_WithNoAvaloniaPlatform()
    {
        string fallback = SKTypeface.Default.FamilyName;
        output.WriteLine($"SKTypeface.Default is '{fallback}'");

        // Read through SkiaFonts.RealFace, NOT through the public properties: those honour
        // TestOverrideTypeface, which ~10 other test classes set to SKTypeface.Default for the
        // duration of their own tests while xunit runs classes in parallel. Reading the property
        // here reported the suite's scheduling — measured, and recorded on RealFace itself.
        (string Path, string Family)[] faces =
        [
            ("Assets/Fonts/DejaVuSans.ttf",             "DejaVu Sans"),
            ("Assets/Fonts/DejaVuSans-Bold.ttf",        "DejaVu Sans"),
            ("Assets/Fonts/DejaVuSans-Oblique.ttf",     "DejaVu Sans"),
            ("Assets/Fonts/DejaVuSans-BoldOblique.ttf", "DejaVu Sans"),
            ("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Regular.ttf",  "IBM Plex Sans"),
            ("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Bold.ttf",     "IBM Plex Sans"),
            ("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-SemiBold.ttf", "IBM Plex Sans"),
            ("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Italic.ttf",   "IBM Plex Sans"),
            ("Assets/Fonts/IBM_Plex_Sans/static/IBMPlexSans-Light.ttf",    "IBM Plex Sans"),
        ];

        foreach (var (path, family) in faces)
        {
            using var face = SkiaFonts.RealFace(path);
            output.WriteLine($"{path} -> '{face.FamilyName}'  bold={face.IsBold} italic={face.IsItalic}");
            Assert.Equal(family, face.FamilyName);
            Assert.NotEqual(fallback, face.FamilyName);
        }
    }

    /// <summary>
    /// The nine faces are actually IN the assembly. A resource that is named in <c>SkiaFonts</c> but
    /// not embedded resolves to <see cref="SKTypeface.Default"/> silently — the exact failure R-aut3-4
    /// records for <c>ShippedTechnologies</c> (a class moved without its <c>EmbeddedResource</c> items
    /// compiles, enumerates nothing, and reports nothing).
    /// </summary>
    [Fact]
    public void TheNineFacesSkiaFontsLoads_AreEmbeddedInCircuitRfRender()
    {
        var asm = typeof(global::CircuitRF.Render.LayoutRenderer).Assembly;
        var names = asm.GetManifestResourceNames();
        foreach (string n in names) output.WriteLine(n);

        string[] expected =
        [
            "CircuitRF.Render.Assets.Fonts.DejaVuSans.ttf",
            "CircuitRF.Render.Assets.Fonts.DejaVuSans-Bold.ttf",
            "CircuitRF.Render.Assets.Fonts.DejaVuSans-Oblique.ttf",
            "CircuitRF.Render.Assets.Fonts.DejaVuSans-BoldOblique.ttf",
            "CircuitRF.Render.Assets.Fonts.IBM_Plex_Sans.static.IBMPlexSans-Regular.ttf",
            "CircuitRF.Render.Assets.Fonts.IBM_Plex_Sans.static.IBMPlexSans-Bold.ttf",
            "CircuitRF.Render.Assets.Fonts.IBM_Plex_Sans.static.IBMPlexSans-SemiBold.ttf",
            "CircuitRF.Render.Assets.Fonts.IBM_Plex_Sans.static.IBMPlexSans-Italic.ttf",
            "CircuitRF.Render.Assets.Fonts.IBM_Plex_Sans.static.IBMPlexSans-Light.ttf",
        ];
        foreach (string e in expected) Assert.Contains(e, names);
    }

    /// <summary>
    /// The linked-back <c>AvaloniaResource</c> in <c>src/Ui</c> is the OTHER half of R-rnd1-4, and
    /// it is a build-file change of exactly the kind that works in <c>dotnet run</c> and fails in a
    /// bundle. The source of truth is checked here: the files the URIs name exist at the ONE place
    /// they now live, under <c>src/Render</c>, and no second copy was left in <c>src/Ui</c>.
    /// (Gate 6 — that a real <c>dotnet publish</c> still carries them — is a packaging check.)
    /// </summary>
    [Fact]
    public void TheFontsLiveInSrcRenderOnly_AndSrcUiKeepsNoSecondCopy()
    {
        string root = RepoRoot();
        Assert.True(Directory.Exists(Path.Combine(root, "src", "Render", "Assets", "Fonts")));
        Assert.False(Directory.Exists(Path.Combine(root, "src", "Ui", "Assets", "Fonts")),
            "src/Ui has its own copy of the fonts again. Two copies of a font file is two files that "
          + "will be updated on one side only, with nothing to report the drift — link them instead.");
        Assert.False(Directory.Exists(Path.Combine(root, "src", "Ui", "Assets", "Color")),
            "src/Ui has its own copy of the shipped .ccolor again — same reason.");

        string ui = File.ReadAllText(Path.Combine(root, "src", "Ui", "CircuitRF.Ui.csproj"));
        Assert.Contains(@"..\Render\Assets\Fonts\**", ui, StringComparison.Ordinal);
        Assert.Contains(@"..\Render\Assets\Color\**", ui, StringComparison.Ordinal);
    }

    // ── Gate 4: the theme is the real theme ───────────────────────────────────

    /// <summary>
    /// R-rnd1-5 / gate 4. <c>"Default"</c> resolves through <c>ThemeResolver</c>'s chain to the
    /// SHIPPED <c>.ccolor</c> in a process with no app host — not to <c>ColorTheme.BuiltIn</c>, which
    /// is where it silently landed before, because step 3 of the chain was installed only by
    /// <c>App.axaml.cs</c>.
    ///
    /// <para>The tell is <see cref="global::CircuitRF.Render.ColorTheme.Name"/>: the file names
    /// itself, and <c>BuiltIn</c> is a different object with a different name. Then the two are
    /// asserted to AGREE role by role, which is a standing claim <c>src/Ui/CLAUDE.md</c> already
    /// makes and nothing checked headlessly.</para>
    /// </summary>
    [Fact]
    public void ThemeResolver_ResolvesTheShippedCcolor_WithNoAppHost_AndItAgreesWithBuiltIn()
    {
        var resolved = global::CircuitRF.Render.ThemeResolver.Resolve(
            global::CircuitRF.Render.ThemeResolver.DefaultThemeName);
        var builtIn = global::CircuitRF.Render.ColorTheme.BuiltIn;

        output.WriteLine($"resolved '{resolved.Name}' vs BuiltIn '{builtIn.Name}'");
        Assert.False(ReferenceEquals(resolved, builtIn),
            "ThemeResolver fell through to ColorTheme.BuiltIn — the built-in .ccolor provider is not "
          + "installed in a process with no Avalonia host, which is exactly the silent difference "
          + "R-rnd1-5 exists to remove.");

        // The shipped file is the source the Settings editor shows; BuiltIn is the per-role fallback.
        // They are two copies of one palette and must agree — a standing claim src/Ui/CLAUDE.md
        // makes and nothing checked from a process with no app host.
        var (light, dark) = builtIn.GetRoleMaps();
        Assert.NotEmpty(light);
        foreach (string role in light.Keys)
        {
            Assert.Equal(builtIn.Resolve(role, global::CircuitRF.Render.ColorVariant.Light),
                         resolved.Resolve(role, global::CircuitRF.Render.ColorVariant.Light));
        }
        foreach (string role in dark.Keys)
        {
            Assert.Equal(builtIn.Resolve(role, global::CircuitRF.Render.ColorVariant.Dark),
                         resolved.Resolve(role, global::CircuitRF.Render.ColorVariant.Dark));
        }
    }

    /// <summary>The shipped theme is embedded in <c>CircuitRF.Render</c> — the resource-name half of
    /// the assertion above, so a rename shows up as itself rather than as a mysterious fallback.</summary>
    [Fact]
    public void TheShippedCcolor_IsEmbeddedInCircuitRfRender()
    {
        var asm = typeof(global::CircuitRF.Render.ThemeResolver).Assembly;
        Assert.Contains("CircuitRF.Render.Assets.Color.Default.ccolor", asm.GetManifestResourceNames());
    }

    // ── Gate 2, in its constructible form ─────────────────────────────────────

    /// <summary>
    /// <b>The measurement this file's header rests on, taken rather than quoted.</b> Two identical
    /// renders in one process, for every document kind and both formats. Whatever this reports is
    /// what in-process byte identity actually IS here — <c>LayoutClipboardExportTests</c> records
    /// that Skia's SVG device numbers elements from a counter it does not reset per canvas, so this
    /// exists to say whether that still bites and for which content.
    /// </summary>
    [Theory]
    [InlineData("schematic")]
    [InlineData("symbol")]
    [InlineData("layout")]
    public void TwoIdenticalRendersInOneProcess_AgreeExceptWhereTheFormatCarriesAStamp(string kind)
    {
        string svgA = Svg(kind), svgB = Svg(kind);
        output.WriteLine($"{kind} svg: {(svgA == svgB ? "byte-identical" : FirstDifference(svgA, svgB))}");

        // MEASURED, 2026-09-07: the schematic and the symbol come back byte-identical. The LAYOUT
        // does not, and the difference is exactly one thing — Skia's clipPath id (`cl_3` vs `cl_4`,
        // and `cl_a` vs `cl_b` past nine — the counter is not decimal),
        // from a counter its SVG device does not reset per canvas. It shows up here and not in the
        // other two because only the layout renderer emits a clip. So the id counter is a real
        // constraint on in-process SVG comparison, but a NARROW one, and it is normalised only where
        // the raw bytes actually differ — an exclusion that stops being needed stops being applied.
        if (svgA != svgB) Assert.Equal(StripSkiaIds(svgA), StripSkiaIds(svgB));

        byte[] pdfA = Pdf(kind), pdfB = Pdf(kind);
        string hexA = Convert.ToHexString(pdfA), hexB = Convert.ToHexString(pdfB);
        output.WriteLine($"{kind} pdf: {pdfA.Length} vs {pdfB.Length} bytes, "
                       + $"{(hexA == hexB ? "byte-identical" : "differ")}");

        // A PDF carries a creation date (SKDocumentPdfMetadata). If two renders one millisecond
        // apart differ, that is where — and it is the ONE exclusion RND-1 §5.2 sanctions by name,
        // on the `em` verb's provenance-timestamp precedent. Anything else is a finding.
        if (hexA != hexB)
        {
            output.WriteLine(FirstDifference(hexA, hexB) ?? "(lengths only)");
            Assert.Equal(StripPdfDates(pdfA), StripPdfDates(pdfB));
        }
    }

    /// <summary>
    /// Gate 2's teeth, and they do not touch a shared static to get them.
    ///
    /// <para>Skia's SVG device writes the typeface's family name into every text run, so the
    /// exported file SAYS which face drew it — and this asserts the application's own clipboard
    /// export draws its text with the face <c>SkiaFonts</c> hands out, rather than with a family the
    /// host happened to have. Combined with
    /// <see cref="SkiaFonts_ResolveTheRealEmbeddedFaces_WithNoAvaloniaPlatform"/>, which reads the
    /// loader with no platform under it, that is the end-to-end claim: the picture a headless
    /// process exports carries the real embedded typeface. Before R-rnd1-4 it did not — the old
    /// loader fell through to <see cref="SKTypeface.Default"/> here.</para>
    ///
    /// <para><b>Asserted against what SkiaFonts is CURRENTLY handing out, not against the literal
    /// family name</b>, and that is deliberate. <c>SkiaFonts.TestOverrideTypeface</c> is a shared
    /// mutable static, roughly ten test classes set it for the duration of their own tests, and
    /// xunit runs classes in PARALLEL — the race <c>SkiaFonts.Load</c>'s own comment records, and
    /// which was measured again while writing this file: the literal form passed alone and read
    /// <c>Helvetica</c> in the full-solution run. A gate that reads ambient global state reports the
    /// suite's scheduling. The "is it the REAL face" half belongs to gate 3, which bypasses the
    /// override entirely.</para>
    /// </summary>
    [Theory]
    [InlineData("schematic")]
    [InlineData("symbol")]
    [InlineData("layout")]
    public void TheExportedSvg_DrawsItsTextWithTheFaceSkiaFontsHandsOut(string kind)
    {
        string svg = Svg(kind);
        var families = new SortedSet<string>(
            Regex.Matches(svg, @"font-family=""([^""]+)""").Select(m => m.Groups[1].Value));
        output.WriteLine($"{kind}: {string.Join(", ", families)}");

        Assert.NotEmpty(families);
        Assert.Contains(SkiaFonts.PlexRegular.FamilyName, families);
    }

    /// <summary>
    /// The renderer that produced those bytes carries no UI framework on the path — the same claim
    /// <c>tests/Firewall.Tests</c> makes about the assembly, asserted here about the type that
    /// actually did the drawing, so the two gates cannot drift apart.
    /// </summary>
    [Fact]
    public void EveryRendererThatDrewAbove_LivesInCircuitRfRender()
    {
        foreach (var t in new[]
                 {
                     typeof(global::CircuitRF.Render.SchematicRenderer),
                     typeof(global::CircuitRF.Render.SymbolEditorRenderer),
                     typeof(global::CircuitRF.Render.LayoutRenderer),
                     typeof(global::CircuitRF.Render.WBondRenderer),
                     typeof(SkiaFonts),
                 })
        {
            Assert.Equal("CircuitRF.Render", t.Assembly.GetName().Name);
            Assert.DoesNotContain(t.Assembly.GetReferencedAssemblies(),
                a => a.Name!.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    // Every fixture carries TEXT on purpose. A picture with no glyphs in it cannot show a typeface
    // substitution, so a gate built on one would pass whatever the font loader did.

    private static List<EditableComponent> SchematicComponents() =>
    [
        new EditableComponent
        {
            Symbol = SymbolKind.Resistor, X = 0, Y = 0, InstanceName = "R1",
            Parameters = { new EditableParameter { Name = "R", Expression = "50" } },
        },
        new EditableComponent
        {
            Symbol = SymbolKind.Capacitor, X = 300, Y = 0, InstanceName = "C1",
            Parameters = { new EditableParameter { Name = "C", Expression = "1p" } },
        },
    ];

    private static List<SymbolPrimitive> SymbolPrimitives() =>
    [
        new LinePrimitive { X1 = 20, Y1 = 20, X2 = 180, Y2 = 20 },
        new LinePrimitive { X1 = 180, Y1 = 20, X2 = 180, Y2 = 100 },
        new LinePrimitive { X1 = 180, Y1 = 100, X2 = 20, Y2 = 100 },
        new LinePrimitive { X1 = 20, Y1 = 100, X2 = 20, Y2 = 20 },
        new TextPrimitive { Content = "DUT", AnchorX = 100, AnchorY = 60, FontSize = 14 },
    ];

    private static List<SymbolPin> SymbolPins() =>
    [
        new SymbolPin(20, 60, 0, "in"),
        new SymbolPin(180, 60, 1, "out"),
    ];

    private static readonly LayerKey FixtureLayer = new(1, 0);

    private static Technology LayoutTech() => new()
    {
        Name = "RND-1 fixture",
        Layers =
        [
            new LayerDef
            {
                Key = FixtureLayer, Name = "M1", Color = new Rgba(200, 30, 30),
                FillOpacity = 0.5, Visible = true,
            },
        ],
    };

    private static List<LayoutShape> LayoutShapes() =>
    [
        new RectShape { Layer = FixtureLayer, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 50_000 },
        // Sans, because a Stroke label (the default) is drawn as pen strokes and carries no font at all.
        new LabelShape { Layer = FixtureLayer, X = 10_000, Y = 20_000, Text = "PAD 1", Height = 12_000, Font = LabelFont.Sans },
    ];

    private static string Svg(string kind) => kind switch
    {
        "schematic" => SchematicClipboard.TryRenderToSvg(
                           SchematicComponents(), [], [],
                           global::CircuitRF.Render.SchematicRenderTheme.Light,
                           useTransparentBackground: false, excludeGrid: true)!.Value.Svg,
        "symbol"    => SymbolSvg(),
        _           => LayoutClipboard.TryRenderToSvg(
                           LayoutShapes(), LayoutTech(),
                           global::CircuitRF.Render.LayoutRenderTheme.Light, transparent: false)!.Value.Svg,
    };

    private static byte[] Pdf(string kind) => kind switch
    {
        "schematic" => SchematicClipboard.TryRenderToPdf(
                           SchematicComponents(), [], [],
                           global::CircuitRF.Render.SchematicRenderTheme.Light,
                           useTransparentBackground: false, excludeGrid: true)!,
        "symbol"    => SymbolClipboard.TryRenderToPdf(
                           SymbolPrimitives(), SymbolPins(), 0, 0, 200, 120,
                           global::CircuitRF.Render.SchematicRenderTheme.Light,
                           useTransparentBackground: false)!,
        _           => LayoutClipboard.TryRenderToPdf(
                           LayoutShapes(), LayoutTech(),
                           global::CircuitRF.Render.LayoutRenderTheme.Light, transparent: false)!,
    };

    private static string SymbolSvg() =>
        SymbolClipboard.TryRenderToSvg(
            SymbolPrimitives(), SymbolPins(), 0, 0, 200, 120,
            global::CircuitRF.Render.SchematicRenderTheme.Light,
            useTransparentBackground: false)!;

    // ── Normalisation, named and justified ────────────────────────────────────

    /// <summary>
    /// The PDF creation/modification date, the one field a PDF carries by construction
    /// (<c>SKDocumentPdfMetadata</c>). Excluded BY NAME, on the precedent RND-1 §5.2 cites — the
    /// `em` verb's provenance write-timestamp — and only where the bytes actually differ, so an
    /// exclusion that stops being needed stops being applied.
    /// </summary>
    private static string StripPdfDates(byte[] pdf) =>
        Regex.Replace(Encoding.Latin1.GetString(pdf), @"D:\d{14}[^)]*", "D:<stamp>");

    /// <summary>
    /// Skia's own SVG element ids — <c>cl_3</c>, <c>cl_a</c>, … — which come from a counter its SVG
    /// device does not reset per canvas. Not a property of the picture and not of either code path;
    /// see the measurement in
    /// <see cref="TwoIdenticalRendersInOneProcess_AgreeExceptWhereTheFormatCarriesAStamp"/>.
    /// </summary>
    private static string StripSkiaIds(string svg) =>
        Regex.Replace(svg, @"\b(cl|img|gr|fp)_[0-9a-z]+\b", "$1_N");

    private static string? FirstDifference(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i])
                return $"first difference at {i}: …{a.Substring(Math.Max(0, i - 40), Math.Min(80, a.Length - Math.Max(0, i - 40)))}… "
                     + $"vs …{b.Substring(Math.Max(0, i - 40), Math.Min(80, b.Length - Math.Max(0, i - 40)))}…";
        return a.Length == b.Length ? null : $"lengths differ: {a.Length} vs {b.Length}";
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitRF.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
