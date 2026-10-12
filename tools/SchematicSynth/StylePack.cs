using System.Globalization;
using System.Text.Json;

namespace CircuitRF.SchematicSynth;

/// <summary>A weighted choice: <c>{"a": 3, "b": 1}</c> in a pack, or one value.</summary>
public sealed class Choice<T>(IReadOnlyList<(T Value, double Weight)> options)
{
    public IReadOnlyList<(T Value, double Weight)> Options { get; } = options;

    public T Pick(Random r)
    {
        double total = Options.Sum(o => o.Weight), x = r.NextDouble() * total;
        foreach (var (v, w) in Options)
        {
            if (x < w) return v;
            x -= w;
        }
        return Options[^1].Value;
    }

    public static Choice<T> Of(T value) => new([(value, 1.0)]);
}

/// <summary>A closed range a pack states, <c>[lo, hi]</c>, drawn uniformly.</summary>
public readonly record struct Span(double Lo, double Hi)
{
    public double Pick(Random r) => Lo + r.NextDouble() * (Hi - Lo);
}

/// <summary>One colour scheme: paper, grid, wires, symbols, words — picked together, because real pictures combine them
/// as a set (pink wires and blue symbols on cream paper; green wires and dark-red symbols on ivory).</summary>
public sealed record Palette(
    string Paper, string Grid, string Wire, string Symbol, IReadOnlyList<string> Text, string Type, string Fill, string Accent);

/// <summary>A label stack: which lines a part's words make and in what order (brief-iml3 R-iml3-1).</summary>
/// <param name="Lines">From <c>type</c>, <c>designator</c>, <c>values</c>, <c>value</c> (the first only).</param>
/// <param name="Row">Designator and value side by side on one baseline — still separate texts.</param>
public sealed record LabelStack(IReadOnlyList<string> Lines, bool Row);

/// <summary>
/// A style pack (brief-iml3 R-iml3-1): every visual choice a picture in one drawing convention makes, as data —
/// <c>tools/SchematicSynth/StylePacks/&lt;name&gt;.json</c>. Each picture draws ONE choice from every list and range, so a
/// picture is consistent within itself the way an editor is, and the batch spans the pack. Glyphs are named from the
/// shared glyph library (<c>StylePacks/glyphs/*.json</c>, <see cref="GlyphLibrary"/>) or written inline.
/// docs/design/schematic-vision.md §5 says what each field means.
/// </summary>
public sealed class StylePack
{
    public required string Name { get; init; }
    public string About { get; init; } = "";
    /// <summary>The real families whose pictures motivated the pack — ids only (L2).</summary>
    public IReadOnlyList<string> Families { get; init; } = [];
    /// <summary><c>screen</c> (an editor's canvas: window chrome, crisp pixels), <c>document</c> (a figure in a PDF or a
    /// web page: captions, no chrome, crisp) or <c>paper</c> (a document printed and scanned: grain, light, a slight
    /// turn).</summary>
    public string Medium { get; init; } = "screen";
    public double Weight { get; init; } = 1;

    public required IReadOnlyList<Palette> Palettes { get; init; }
    public Choice<string> Grid { get; init; } = Choice<string>.Of("none");
    /// <summary>Grid pitch in grid steps (100 schematic units).</summary>
    public Span GridPitch { get; init; } = new(1, 1);
    public double PageFrame { get; init; }
    public double TitleBlock { get; init; }

    /// <summary>Pen widths as a share of a grid step, never under <see cref="MinPx"/> device pixels.</summary>
    public Span WirePen { get; init; } = new(0.015, 0.025);
    public Span SymbolPen { get; init; } = new(0.015, 0.025);
    public Span MinPx { get; init; } = new(1, 1.5);
    public Choice<string> Caps { get; init; } = Choice<string>.Of("butt");
    public double Antialias { get; init; } = 0.9;
    /// <summary>A glyph's drawing scaled about its body centre; the gap to the placement's pins is drawn as lead.</summary>
    public Span SymbolScale { get; init; } = new(0.85, 1.0);

    public Choice<string> Fonts { get; init; } = Choice<string>.Of("sans");
    /// <summary>Text cap-ish height in grid steps.</summary>
    public Span TextSize { get; init; } = new(0.5, 0.7);
    public double Bold { get; init; }
    public required Choice<LabelStack> Stacks { get; init; }
    public Choice<string> Placement { get; init; } = Choice<string>.Of("side");
    public double Rotated { get; init; }
    public Choice<string> ValueForms { get; init; } = Choice<string>.Of("named");
    public double Spaced { get; init; }
    public Choice<string> Ohm { get; init; } = Choice<string>.Of("Ohm");
    public Choice<string> Micro { get; init; } = Choice<string>.Of("u");
    public double Subscripts { get; init; }
    public double Tags { get; init; }
    public Choice<string> NetLabels { get; init; } = Choice<string>.Of("text");

    public Choice<string> Dots { get; init; } = Choice<string>.Of("circle");
    /// <summary>Junction dot diameter in grid steps.</summary>
    public Span DotSize { get; init; } = new(0.08, 0.12);
    public Choice<string> PinMarkers { get; init; } = Choice<string>.Of("none");
    public double Hops { get; init; }

    /// <summary>Per class, the glyphs this pack draws it with.</summary>
    public required IReadOnlyDictionary<string, Choice<Glyph>> Glyphs { get; init; }
    /// <summary>How often a part is drawn as a class circuitRF never draws but means the same thing.</summary>
    public IReadOnlyDictionary<string, double> Substitute { get; init; } = new Dictionary<string, double>();
    /// <summary>How often each distractor appears in a picture (<see cref="Distractors"/>).</summary>
    public IReadOnlyDictionary<string, double> Distract { get; init; } = new Dictionary<string, double>();
    public IReadOnlyList<string> ControllerTitles { get; init; } = ["S-PARAMETERS", "DC", "AC", "TRANSIENT", "HARMONIC BALANCE"];

    public double Chance(string distractor) => Distract.GetValueOrDefault(distractor);

    /// <summary>The glyph for <paramref name="cls"/>, or null when this pack does not draw that class.</summary>
    public Glyph? GlyphFor(string cls, Random r) => Glyphs.TryGetValue(cls, out var c) ? c.Pick(r) : null;

    // ── loading ──────────────────────────────────────────────────────────────

    private static readonly Lazy<IReadOnlyList<StylePack>> Shipped = new(() => LoadEmbedded());

    /// <summary>Every pack compiled into this assembly (the <c>StylePacks/*.json</c> files), by name.</summary>
    public static IReadOnlyList<StylePack> All => Shipped.Value;

    public static StylePack Named(string name) =>
        All.FirstOrDefault(p => p.Name == name) ?? throw new ArgumentException($"No style pack named '{name}'. Packs: {string.Join(", ", All.Select(p => p.Name))}");

    private static IReadOnlyList<StylePack> LoadEmbedded()
    {
        var asm = typeof(StylePack).Assembly;
        var lib = GlyphLibrary.Embedded;
        var packs = new List<StylePack>();
        foreach (string res in asm.GetManifestResourceNames().Where(n => n.StartsWith("StylePacks.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var reader = new StreamReader(s);
            packs.Add(Parse(reader.ReadToEnd(), lib, res));
        }
        return packs;
    }

    /// <summary>A pack from its JSON. Every glyph it names must be in <paramref name="lib"/>, every class in
    /// classes.json — a misspelt name is an error here, not a class that silently never draws.</summary>
    public static StylePack Parse(string json, GlyphLibrary lib, string source = "pack")
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        string name = root.GetProperty("name").GetString()!;
        string where = $"{source} ({name})";

        var glyphs = new Dictionary<string, Choice<Glyph>>(StringComparer.Ordinal);
        if (root.TryGetProperty("glyphs", out var g))
            foreach (var cls in g.EnumerateObject())
            {
                if (!ClassList.Classes.ContainsKey(cls.Name)) throw new FormatException($"{where}: glyphs names class '{cls.Name}', which classes.json does not have");
                var options = new List<(Glyph, double)>();
                foreach (var e in cls.Value.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Object)
                    {
                        options.Add((Glyph.Parse(cls.Name, cls.Name + ".inline" + options.Count, e), 1));
                        continue;
                    }
                    var (gname, weight) = Weighted(e.GetString()!);
                    if (!lib.Glyphs.TryGetValue(gname, out var glyph)) throw new FormatException($"{where}: glyph '{gname}' is not in the glyph library");
                    if (glyph.Class != cls.Name) throw new FormatException($"{where}: glyph '{gname}' draws {glyph.Class}, listed under {cls.Name}");
                    options.Add((glyph, weight));
                }
                if (options.Count > 0) glyphs[cls.Name] = new Choice<Glyph>(options);
            }

        var text = root.TryGetProperty("text", out var t) ? t : default;
        var pens = root.TryGetProperty("pens", out var p) ? p : default;
        var junctions = root.TryGetProperty("junctions", out var j) ? j : default;
        var canvas = root.TryGetProperty("canvas", out var c) ? c : default;

        var stacks = new List<(LabelStack, double)>();
        if (text.ValueKind == JsonValueKind.Object && text.TryGetProperty("stacks", out var st))
            foreach (var s in st.EnumerateArray())
            {
                var lines = s.GetProperty("lines").EnumerateArray().Select(x => x.GetString()!).ToList();
                foreach (var l in lines)
                    if (l is not ("type" or "designator" or "values" or "value"))
                        throw new FormatException($"{where}: a stack line is type, designator, values or value, not '{l}'");
                stacks.Add((new LabelStack(lines, s.TryGetProperty("row", out var row) && row.GetBoolean()),
                            s.TryGetProperty("weight", out var w) ? w.GetDouble() : 1));
            }
        if (stacks.Count == 0) stacks.Add((new LabelStack(["designator", "values"], false), 1));

        var palettes = root.GetProperty("palettes").EnumerateArray().Select(x => new Palette(
            x.GetProperty("paper").GetString()!, Str(x, "grid") ?? "#c0c0c0", x.GetProperty("wire").GetString()!,
            x.GetProperty("symbol").GetString()!,
            x.TryGetProperty("text", out var tx) ? (tx.ValueKind == JsonValueKind.Array ? [.. tx.EnumerateArray().Select(e => e.GetString()!)] : [tx.GetString()!]) : ["#000000"],
            Str(x, "type") ?? Str(x, "symbol")!, Str(x, "fill") ?? "#d8d8d8", Str(x, "accent") ?? "#d00000")).ToList();
        foreach (var pal in palettes)
            foreach (string col in new[] { pal.Paper, pal.Grid, pal.Wire, pal.Symbol, pal.Type, pal.Fill, pal.Accent }.Concat(pal.Text))
                if (!SkiaSharp.SKColor.TryParse(col, out _)) throw new FormatException($"{where}: '{col}' is not a colour");

        return new StylePack
        {
            Name = name,
            About = Str(root, "about") ?? "",
            Families = root.TryGetProperty("families", out var f) ? [.. f.EnumerateArray().Select(x => x.GetString()!)] : [],
            Medium = Str(root, "medium") ?? "screen",
            Weight = Num(root, "weight") ?? 1,
            Palettes = palettes,
            Grid = Pick(canvas, "grid", "none"),
            GridPitch = Range(canvas, "gridPitch", new Span(1, 1)),
            PageFrame = Num(canvas, "frame") ?? 0,
            TitleBlock = Num(canvas, "titleBlock") ?? 0,
            WirePen = Range(pens, "wire", new Span(0.015, 0.025)),
            SymbolPen = Range(pens, "symbol", new Span(0.015, 0.025)),
            MinPx = Range(pens, "minPx", new Span(1, 1.5)),
            Caps = Pick(pens, "caps", "butt"),
            Antialias = Num(pens, "antialias") ?? 0.9,
            SymbolScale = Range(pens, "symbolScale", new Span(0.85, 1.0)),
            Fonts = Pick(text, "fonts", "sans"),
            TextSize = Range(text, "size", new Span(0.5, 0.7)),
            Bold = Num(text, "bold") ?? 0,
            Stacks = new Choice<LabelStack>(stacks),
            Placement = Pick(text, "placement", "side"),
            Rotated = Num(text, "rotated") ?? 0,
            ValueForms = Pick(text, "valueForms", "named"),
            Spaced = Num(text, "spaced") ?? 0,
            Ohm = Pick(text, "ohm", "Ohm"),
            Micro = Pick(text, "micro", "u"),
            Subscripts = Num(text, "subscripts") ?? 0,
            Tags = Num(text, "tags") ?? 0,
            NetLabels = Pick(text, "netLabels", "text"),
            Dots = Pick(junctions, "dots", "circle"),
            DotSize = Range(junctions, "size", new Span(0.08, 0.12)),
            PinMarkers = Pick(junctions, "pinMarkers", "none"),
            Hops = Num(junctions, "hops") ?? 0,
            Glyphs = glyphs,
            Substitute = Map(root, "substitute"),
            Distract = Map(root, "distractors"),
            ControllerTitles = root.TryGetProperty("controllerTitles", out var ct) ? [.. ct.EnumerateArray().Select(x => x.GetString()!)] : ["S-PARAMETERS", "DC", "AC", "TRANSIENT", "HARMONIC BALANCE"],
        };
    }

    internal static (string Name, double Weight) Weighted(string s)
    {
        int star = s.LastIndexOf('*');
        return star < 0 ? (s, 1) : (s[..star], double.Parse(s[(star + 1)..], CultureInfo.InvariantCulture));
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static Span Range(JsonElement e, string key, Span fallback)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number) return new Span(v.GetDouble(), v.GetDouble());
        return new Span(v[0].GetDouble(), v[1].GetDouble());
    }

    /// <summary>A choice written as one string, a list (equal weights) or an object of weights.</summary>
    private static Choice<string> Pick(JsonElement e, string key, string fallback)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return Choice<string>.Of(fallback);
        return v.ValueKind switch
        {
            JsonValueKind.String => Choice<string>.Of(v.GetString()!),
            JsonValueKind.Array => new Choice<string>([.. v.EnumerateArray().Select(x => (x.GetString()!, 1.0))]),
            _ => new Choice<string>([.. v.EnumerateObject().Select(x => (x.Name, x.Value.GetDouble()))]),
        };
    }

    private static Dictionary<string, double> Map(JsonElement e, string key)
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        if (e.TryGetProperty(key, out var v))
            foreach (var x in v.EnumerateObject()) d[x.Name] = x.Value.GetDouble();
        return d;
    }
}

/// <summary>
/// The shared glyph library: <c>StylePacks/glyphs/*.json</c>, each an object of <c>"&lt;class&gt;.&lt;style&gt;": glyph</c>.
/// A pack names the glyphs it draws; a new convention for a class is one more entry here and its name in a pack.
/// </summary>
public sealed class GlyphLibrary
{
    public required IReadOnlyDictionary<string, Glyph> Glyphs { get; init; }

    private static readonly Lazy<GlyphLibrary> Shipped = new(() =>
    {
        var asm = typeof(GlyphLibrary).Assembly;
        var glyphs = new Dictionary<string, Glyph>(StringComparer.Ordinal);
        foreach (string res in asm.GetManifestResourceNames().Where(n => n.StartsWith("Glyphs.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var reader = new StreamReader(s);
            foreach (var (k, v) in Parse(reader.ReadToEnd(), res).Glyphs)
                if (!glyphs.TryAdd(k, v)) throw new FormatException($"{res}: glyph '{k}' is defined twice");
        }
        return new GlyphLibrary { Glyphs = glyphs };
    });

    public static GlyphLibrary Embedded => Shipped.Value;

    public static GlyphLibrary Parse(string json, string source = "glyphs")
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var glyphs = new Dictionary<string, Glyph>(StringComparer.Ordinal);
        foreach (var e in doc.RootElement.EnumerateObject())
        {
            if (e.Name.StartsWith('$')) continue;
            int dot = e.Name.IndexOf('.');
            string cls = dot > 0 ? e.Name[..dot] : throw new FormatException($"{source}: glyph '{e.Name}' is not named <class>.<style>");
            if (!ClassList.Classes.ContainsKey(cls)) throw new FormatException($"{source}: glyph '{e.Name}' draws class '{cls}', which classes.json does not have");
            glyphs[e.Name] = Glyph.Parse(cls, e.Name, e.Value);
        }
        return new GlyphLibrary { Glyphs = glyphs };
    }
}
