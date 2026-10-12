using System.Collections.Concurrent;
using System.Text.Json;
using CircuitRF.Render;
using SkiaSharp;

namespace CircuitRF.SchematicSynth;

/// <summary>
/// The faces a style pack's words are drawn in (brief-iml3 R-iml3-5). Two sources:
/// <list type="bullet">
/// <item>circuitRF's own shipped faces — always present, read from <c>CircuitRF.Render</c>'s embedded resources;</item>
/// <item>the open-licensed families <c>python -m crfml.fetch_fonts</c> puts in <c>$CRF_ML_DATA/fonts</c>, described by its
/// <c>index.json</c> (file → styles). Nothing fetched is ever committed (L4).</item>
/// </list>
/// A pack asks for a STYLE (<c>sans</c>, <c>sans-bold</c>, <c>serif-italic</c>, <c>mono</c>, <c>condensed</c>, <c>ui</c>…),
/// never a family by name. A style no face carries falls back — dropping its modifiers, then to the shipped sans — and
/// <see cref="Fallbacks"/> says so, once per style, so a batch drawn without the fetched fonts reports it rather than
/// passing for one that had them.
/// </summary>
public sealed class FontBook
{
    private sealed record Face(string Source, IReadOnlyList<string> Styles, Func<SKTypeface> Load);

    private readonly List<Face> _faces = [];
    private readonly ConcurrentDictionary<string, SKTypeface> _loaded = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _fallbacks = new(StringComparer.Ordinal);

    /// <summary>Styles asked for that no face carries, and the face drawn instead.</summary>
    public IReadOnlyDictionary<string, string> Fallbacks => _fallbacks;

    /// <summary>How many fetched faces this book holds (0 without <c>$CRF_ML_DATA/fonts</c>).</summary>
    public int Fetched { get; private set; }

    /// <param name="fontsDir">The data home's <c>fonts</c> folder, or null for the shipped faces alone.</param>
    public static FontBook Create(string? fontsDir)
    {
        var book = new FontBook();
        var asm = typeof(ThemeResolver).Assembly;
        foreach (string res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            string file = res[(res.LastIndexOf('.', res.Length - 5) + 1)..];
            book._faces.Add(new Face("shipped:" + file, ShippedStyles(file), () =>
            {
                using var s = asm.GetManifestResourceStream(res)!;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return SKTypeface.FromData(SKData.CreateCopy(ms.ToArray()));
            }));
        }

        string? index = fontsDir is null ? null : Path.Combine(fontsDir, "index.json");
        if (index is not null && File.Exists(index))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(index));
            foreach (var f in doc.RootElement.GetProperty("faces").EnumerateArray())
            {
                string path = Path.Combine(fontsDir!, f.GetProperty("file").GetString()!);
                if (!File.Exists(path)) continue;
                var styles = f.GetProperty("styles").EnumerateArray().Select(x => x.GetString()!).ToList();
                book._faces.Add(new Face("fetched:" + Path.GetFileName(path), styles, () => SKTypeface.FromFile(path)));
                book.Fetched++;
            }
        }
        return book;
    }

    /// <summary>The styles a shipped face answers to, from its file name.</summary>
    private static List<string> ShippedStyles(string file)
    {
        string f = file.ToLowerInvariant();
        bool bold = f.Contains("bold"), oblique = f.Contains("oblique") || f.Contains("italic");
        string family = f.StartsWith("dejavu", StringComparison.Ordinal) ? (f.Contains("condensed") ? "condensed" : "sans") : "ui";
        if (f.Contains("light") || f.Contains("semibold")) return [family + "-light"];
        var styles = new List<string> { family + (bold ? "-bold" : "") + (oblique ? "-oblique" : "") };
        if (oblique) styles.Add(family + (bold ? "-bold" : "") + "-italic");
        return styles;
    }

    /// <summary>The face for <paramref name="style"/>: one of the faces carrying it, chosen by <paramref name="r"/>.</summary>
    public SKTypeface Get(string style, Random r)
    {
        foreach (string s in Candidates(style))
        {
            var faces = _faces.Where(f => f.Styles.Contains(s)).ToList();
            if (faces.Count == 0) continue;
            var face = faces[r.Next(faces.Count)];
            if (s != style) _fallbacks.TryAdd(style, face.Source);
            return _loaded.GetOrAdd(face.Source, _ => face.Load() ?? SKTypeface.Default);
        }
        var any = _faces[0];
        _fallbacks.TryAdd(style, any.Source);
        return _loaded.GetOrAdd(any.Source, _ => any.Load() ?? SKTypeface.Default);
    }

    /// <summary><c>serif-bold-italic</c> → itself, then with fewer modifiers, then the shipped sans of the same weight.</summary>
    private static IEnumerable<string> Candidates(string style)
    {
        yield return style;
        var parts = style.Split('-');
        bool bold = parts.Contains("bold");
        if (parts.Length > 2) yield return parts[0] + (bold ? "-bold" : "");
        if (parts.Length > 1) yield return parts[0];
        if (parts[0] is "ui") yield return "sans" + (bold ? "-bold" : "");
        yield return bold ? "sans-bold" : "sans";
    }
}
