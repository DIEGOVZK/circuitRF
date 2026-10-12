// brief-iml-3 §3 — style packs (tools/SchematicSynth/StylePacks). Fast and data-free: no data home, no fetched fonts.
// Every committed pack and glyph parses; every glyph's pins lie on its strokes and its body is a real box; every class
// in classes.json is drawn by three packs at least; and three seeds per pack generate pictures that pass the same
// checks IML-2's do — the structural label checks with each pin on its symbol's ink, bodies inside the picture or cut,
// and each drawing extracting to the netlist written beside it.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Schematic;
using CircuitRF.SchematicSynth;
using Xunit;

namespace CircuitRF.Ui.Tests.Vision;

public sealed class StylePackTests
{
    [Fact]
    public void EveryCommittedPack_Parses_AndNamesFamiliesByIdOnly()
    {
        var packs = StylePack.All;
        Assert.True(packs.Count >= 12, $"{packs.Count} packs; the brief asks for 12 at least");
        Assert.Equal(packs.Count, packs.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var p in packs)
        {
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", p.Name);
            Assert.NotEmpty(p.Palettes);
            Assert.Contains(p.Medium, new[] { "screen", "document", "paper" });
            // L2: a pack says which real family motivated it by id, never by a source's name.
            Assert.NotEmpty(p.Families);
            foreach (string f in p.Families) Assert.Matches("^(family-[a-z]|corpus-[a-z]+)$", f);
        }
    }

    [Fact]
    public void EveryGlyph_HasItsPinsOnItsStrokes_AndARealBody()
    {
        var lib = GlyphLibrary.Embedded.Glyphs;
        foreach (var cls in ClassList.Classes.Keys)
            Assert.True(lib.ContainsKey(cls + ".plain"), $"the glyph library has no {cls}.plain, the fallback for a pack that does not draw {cls}");

        foreach (var (name, g) in lib)
        {
            Assert.True(g.Body.Width > 0 && g.Body.Height > 0, $"{name}: body {g.Body} is not a box");
            if (g.BoxFrame)
            {
                Assert.Empty(g.Pins);
                continue;
            }
            // The pins are the class's, by name.
            var cls = ClassList.Classes[g.Class];
            Assert.NotNull(cls.Pins);
            Assert.Equal(cls.Pins!.Order(StringComparer.Ordinal), g.Pins.Keys.Order(StringComparer.Ordinal));
            if (g.Variant is { } v) Assert.Contains(v, cls.Variants);

            foreach (var (pin, at) in g.Pins)
            {
                double best = g.Strokes.Min(s => Distance(at, s));
                Assert.True(best <= 1.0, $"{name}: pin {pin} at ({at.X}, {at.Y}) is {best:0.#} units from its nearest stroke");
            }
        }
    }

    private static double Distance((double X, double Y) p, Stroke s)
    {
        double best = double.MaxValue;
        int n = s.Points.Count;
        for (int i = 0; i + 1 < n + (s.Closed ? 1 : 0); i++)
        {
            var (a, b) = (s.Points[i], s.Points[(i + 1) % n]);
            double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
            double t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
            best = Math.Min(best, Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2)));
        }
        return best;
    }

    [Fact]
    public void EveryClass_IsDrawnByThreePacksAtLeast()
    {
        foreach (var cls in ClassList.Classes.Keys)
        {
            int n = StylePack.All.Count(p => p.Glyphs.ContainsKey(cls));
            Assert.True(n >= 3, $"{cls} has a glyph in {n} pack(s); the brief asks for 3 at least");
        }
    }

    [Fact]
    public void AMissingFont_FallsBackToAShippedFace_AndSaysSo()
    {
        var fonts = FontBook.Create(fontsDir: null);
        Assert.Equal(0, fonts.Fetched);
        var face = fonts.Get("serif-italic", new Random(1));
        Assert.NotNull(face);
        Assert.True(fonts.Fallbacks.TryGetValue("serif-italic", out var drawnIn));
        Assert.StartsWith("shipped:", drawnIn);
        // A style circuitRF ships is no fallback.
        fonts.Get("sans", new Random(1));
        Assert.False(fonts.Fallbacks.ContainsKey("sans"));
    }

    [Fact]
    public void ThreeSeedsPerPack_GenerateAndValidate()
    {
        var fonts = FontBook.Create(fontsDir: null);
        var cases = StylePack.All.SelectMany(p => new[] { 1, 2, 3 }.Select(seed => (Pack: p, Seed: seed))).ToList();
        var problems = new ConcurrentBag<string>();
        Parallel.ForEach(cases, c =>
        {
            var pic = Batch.MakeForeign(c.Seed, 0, [c.Pack], authored: [], fonts, out string? why);
            string at = $"{c.Pack.Name} seed {c.Seed}";
            if (pic is null) { problems.Add($"{at}: {why}"); return; }
            var label = pic.Label;
            if (label.Style != c.Pack.Name) problems.Add($"{at}: style {label.Style}");
            foreach (var e in LabelChecks.Check(label, pic.SymbolMask).Where(f => f.Error)) problems.Add($"{at}: {e}");
            if (label.Parts.Count == 0) problems.Add($"{at}: no parts");
            foreach (var p in label.Parts)
            {
                bool inside = p.Body[0] >= 0 && p.Body[1] >= 0 && p.Body[2] <= label.Width && p.Body[3] <= label.Height;
                if (!inside && !p.Cut) problems.Add($"{at}: {p.Id} body leaves the picture and is not marked cut");
            }
            // Every drawing in the picture is its circuit: a substitution redraws, it never rewires.
            foreach (var d in pic.Drawings)
                if (!Netlists.Same(d.Truth, NetExtractor.Extract(d.Model).TestBench, values: true, out string w))
                    problems.Add($"{at}: the drawing does not extract to its netlist: {w}");
        });
        Assert.True(problems.IsEmpty, string.Join("\n", problems.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void ASeed_GivesTheSameBytes()
    {
        var fonts = FontBook.Create(fontsDir: null);
        var pack = StylePack.Named("textbook-serif");
        var a = Batch.MakeForeign(4, 2, [pack], authored: [], fonts, out _)!;
        var b = Batch.MakeForeign(4, 2, [pack], authored: [], fonts, out _)!;
        Assert.Equal(a.Image, b.Image);
        Assert.Equal(a.Label.ToJson(), b.Label.ToJson());
        Assert.Equal(a.Netlist, b.Netlist);
    }

    [Fact]
    public void ATuningTag_ReadsAsTheValueItFollows()
    {
        Assert.True(ValueText.Reads("L=0.56 nH {o}"));
        Assert.True(ValueText.Reads("C = 1 pF {t}"));
        Assert.False(ValueText.Reads("{t}"));
    }
}
