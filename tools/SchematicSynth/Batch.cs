using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace CircuitRF.SchematicSynth;

public sealed record BatchOptions(int Count, int Seed, string Out, int Preview, int Threads, string RepoRoot)
{
    /// <summary><c>crf</c> (IML-2) or <c>foreign</c> (IML-3).</summary>
    public string Generator { get; init; } = "crf";
    /// <summary>The style packs a foreign batch draws from.</summary>
    public IReadOnlyList<StylePack> Packs { get; init; } = [];
    /// <summary>The data home, for its fetched fonts and — with <c>--preview</c> — its real pictures.</summary>
    public string? DataHome { get; init; }
}

/// <summary>
/// A batch of generated circuitRF pictures (brief-iml-2 R-iml2-5): deterministic for a seed (picture <c>k</c> of seed
/// <c>s</c> is the same bytes on every run and every machine, whatever the thread count), parallel across cores,
/// resumable (a picture whose label exists is skipped — the label is written last), with a class histogram at the end.
/// </summary>
public static class Batch
{
    /// <summary>The picture's id: the generator, the seed and its index.</summary>
    public static string Id(int seed, int index, string generator = "crf") =>
        $"{(generator == "foreign" ? "fgn" : "crf")}-{seed}-{index.ToString("D6", CultureInfo.InvariantCulture)}";

    /// <summary>Picture <paramref name="index"/> of <paramref name="seed"/>'s own random stream.</summary>
    public static Random Stream(int seed, int index) => new(unchecked(seed * 1_000_003 + index * 7_919 + 17));

    /// <summary>
    /// One picture, or null with the reason. A circuit whose drawing does not extract to it is re-drawn Tidy, then a new
    /// circuit is drawn, a few times; the attempts consume the picture's own random stream, so the outcome is still a
    /// function of (seed, index) alone.
    /// </summary>
    public static PictureFiles? MakeOne(int seed, int index, IReadOnlyList<string> authored, Underlays underlays, out string? why)
    {
        why = null;
        var r = Stream(seed, index);
        double x = r.NextDouble();
        var d = Draw(r, x, authored);
        if (d is null) { why = "no drawing extracted to its circuit in five attempts"; return null; }

        var pic = PictureMaker.Make(Id(seed, index), d, r, underlays);
        var errors = LabelChecks.Check(pic.Label, pic.SymbolMask).Where(f => f.Error).ToList();
        if (errors.Count > 0) { why = string.Join("; ", errors.Take(3)); return null; }
        return pic;
    }

    /// <summary>A drawing laid out Tidy, by hand or from the repository's own schematics, as <paramref name="x"/> (uniform
    /// in [0, 1)) falls; a circuit whose drawing does not extract to it is tried again, five times at most.</summary>
    private static Drawing? Draw(Random r, double x, IReadOnlyList<string> authored)
    {
        var mode = authored.Count > 0 && x < 0.15 ? DrawMode.Authored : x < 0.57 ? DrawMode.Tidy : DrawMode.Hand;
        Drawing? d = null;
        for (int attempt = 0; attempt < 5 && d is null; attempt++)
        {
            if (mode == DrawMode.Authored) d = Drawings.Authored(authored[r.Next(authored.Count)], r);
            else
            {
                var c = CircuitGenerator.Next(r);
                d = mode == DrawMode.Hand ? Drawings.Hand(c, r) ?? Drawings.Tidy(c, r) : Drawings.Tidy(c, r);
            }
        }
        return d;
    }

    /// <summary>A well-mixed seed for picture <paramref name="index"/> of <paramref name="seed"/>. Seeded
    /// <see cref="Random"/>s from neighbouring integers begin with correlated draws, so a foreign picture's first rolls
    /// (its pack, two circuits or one) came out alike across a batch; a SplitMix64 finaliser decorrelates them.</summary>
    internal static int Mix(int seed, int index)
    {
        ulong z = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL + (ulong)(uint)index + 0x632BE59BD9B4E019UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return (int)(z ^ (z >> 31));
    }

    /// <summary>Picture <paramref name="index"/> of a foreign batch (brief-iml3): its own random stream picks a pack by
    /// weight, one drawing (two, as often as the pack says) and draws them in that pack.</summary>
    public static PictureFiles? MakeForeign(int seed, int index, IReadOnlyList<StylePack> packs, IReadOnlyList<string> authored,
                                            FontBook fonts, out string? why)
    {
        why = null;
        var r = new Random(Mix(seed, index));
        double total = packs.Sum(p => p.Weight), pick = r.NextDouble() * total;
        var pack = packs[^1];
        foreach (var p in packs)
        {
            if (pick < p.Weight) { pack = p; break; }
            pick -= p.Weight;
        }
        var drawings = new List<Drawing>();
        int wanted = r.NextDouble() < pack.Chance("twoCircuits") ? 2 : 1;
        for (int k = 0; k < wanted; k++)
            if (Draw(r, r.NextDouble(), authored) is { } d) drawings.Add(d);
        if (drawings.Count == 0) { why = "no drawing extracted to its circuit in five attempts"; return null; }

        var pic = ForeignPicture.Make(Id(seed, index, "foreign"), drawings, pack, fonts, r);
        var errors = LabelChecks.Check(pic.Label, pic.SymbolMask).Where(f => f.Error).ToList();
        if (errors.Count > 0) { why = pack.Name + ": " + string.Join("; ", errors.Take(3)); return null; }
        return pic;
    }

    public static void Write(PictureFiles pic, string dir)
    {
        File.WriteAllBytes(Path.Combine(dir, pic.ImageName), pic.Image);
        foreach (var (name, bytes) in pic.Masks) File.WriteAllBytes(Path.Combine(dir, name), bytes);
        File.WriteAllText(Path.Combine(dir, pic.Id + ".cnl"), pic.Netlist);
        // The label last, through a temporary name: a label on disk means the picture is complete.
        string label = Path.Combine(dir, pic.Id + ".mllabel.json");
        File.WriteAllText(label + ".tmp", pic.Label.ToJson());
        File.Move(label + ".tmp", label, overwrite: true);
    }

    public static int Run(BatchOptions o, TextWriter log)
    {
        Directory.CreateDirectory(o.Out);
        var authored = Drawings.AuthoredSources(o.RepoRoot);
        bool foreign = o.Generator == "foreign";
        var underlays = foreign ? null : Underlays.Create(Path.Combine(o.Out, ".underlay"), o.Seed);
        var fonts = FontBook.Create(o.DataHome is null ? null : Path.Combine(o.DataHome, "fonts"));
        log.WriteLine($"[synth] {o.Count} picture(s), seed {o.Seed}, {o.Threads} thread(s), {authored.Count} authored schematic(s) in the mix");
        if (foreign)
            log.WriteLine($"[synth] {o.Packs.Count} style pack(s): {string.Join(", ", o.Packs.Select(p => p.Name))}; {fonts.Fetched} fetched face(s)"
                          + (fonts.Fetched == 0 ? " — run `python -m crfml.fetch_fonts` for more than circuitRF's own faces" : ""));
        var styles = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        var presence = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var parts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var failures = new ConcurrentBag<string>();
        int made = 0, skipped = 0, done = 0;
        var started = DateTime.UtcNow;

        void Count(IEnumerable<string> classes)
        {
            foreach (var g in classes.GroupBy(c => c, StringComparer.Ordinal))
            {
                presence.AddOrUpdate(g.Key, 1, (_, v) => v + 1);
                parts.AddOrUpdate(g.Key, g.Count(), (_, v) => v + g.Count());
            }
        }

        Parallel.For(0, o.Count, new ParallelOptions { MaxDegreeOfParallelism = o.Threads }, i =>
        {
            string id = Id(o.Seed, i, o.Generator);
            string labelPath = Path.Combine(o.Out, id + ".mllabel.json");
            if (File.Exists(labelPath))
            {
                Count(ClassesIn(labelPath));
                if (foreign) styles.AddOrUpdate(StyleIn(labelPath), 1, (_, v) => v + 1);
                Interlocked.Increment(ref skipped);
            }
            else
            {
                PictureFiles? pic;
                string? why;
                try { pic = foreign ? MakeForeign(o.Seed, i, o.Packs, authored, fonts, out why) : MakeOne(o.Seed, i, authored, underlays!, out why); }
                catch (Exception ex) { pic = null; why = ex.GetType().Name + ": " + ex.Message; }
                if (pic is null) failures.Add($"{id}: {why}");
                else
                {
                    Write(pic, o.Out);
                    Count(pic.Classes);
                    if (foreign) styles.AddOrUpdate(pic.Label.Style, 1, (_, v) => v + 1);
                    Interlocked.Increment(ref made);
                }
            }
            int n = Interlocked.Increment(ref done);
            if (n % 500 == 0) log.WriteLine($"[synth] {n}/{o.Count} ({(DateTime.UtcNow - started).TotalSeconds:0} s)");
        });

        int total = made + skipped;
        log.WriteLine($"[synth] made {made}, already there {skipped}, failed {failures.Count} — {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
        foreach (var f in failures.Order(StringComparer.Ordinal).Take(20)) log.WriteLine("  failed " + f);
        log.WriteLine();
        log.WriteLine("class                pictures   share    parts");
        foreach (var cls in ClassList.Classes.Keys)
        {
            int p = presence.GetValueOrDefault(cls), n = parts.GetValueOrDefault(cls);
            string mark = ClassList.OfKind.Values.Contains(cls) && total > 0 && p * 100 < total ? "  < 1 %" : "";
            if (p == 0 && !foreign && !ClassList.OfKind.Values.Contains(cls) && cls != "stub-open") mark = "  (not drawn by circuitRF)";
            if (p == 0 && foreign) mark = "  (none drawn)";
            log.WriteLine($"{cls,-20} {p,8} {(total > 0 ? 100.0 * p / total : 0),6:0.0} % {n,8}{mark}");
        }

        if (foreign)
        {
            log.WriteLine();
            log.WriteLine("style pack                 pictures   share");
            foreach (var pk in o.Packs)
            {
                int c = styles.GetValueOrDefault(pk.Name);
                log.WriteLine($"{pk.Name,-26} {c,8} {(total > 0 ? 100.0 * c / total : 0),6:0.0} %");
            }
            foreach (var (style, face) in fonts.Fallbacks.OrderBy(f => f.Key, StringComparer.Ordinal))
                log.WriteLine($"[synth] font style '{style}' is not in the data home's fonts — drawn in {face}");
        }

        if (o.Preview > 0 && foreign)
        {
            // Each pack's first pictures, so one sheet shows every convention, and the real pictures they were written from.
            var byPack = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string f in Directory.EnumerateFiles(o.Out, "*.mllabel.json").Order(StringComparer.Ordinal))
            {
                string st = StyleIn(f);
                var l = byPack.TryGetValue(st, out var x) ? x : byPack[st] = [];
                if (l.Count < Math.Max(1, o.Preview / Math.Max(1, o.Packs.Count))) l.Add(Path.GetFileName(f)[..^".mllabel.json".Length]);
            }
            var ids = o.Packs.SelectMany(p => byPack.GetValueOrDefault(p.Name) ?? []).ToList();
            string sheet = Path.Combine(o.Out, "preview.png");
            ContactSheet.Write(o.Out, ids, sheet);
            log.WriteLine($"[synth] preview of {ids.Count} picture(s) across {byPack.Count} pack(s): {sheet}");
            if (o.DataHome is not null)
            {
                var families = o.Packs.SelectMany(p => p.Families).Distinct(StringComparer.Ordinal).ToList();
                string real = Path.Combine(o.Out, "preview-real.png");
                int n = ContactSheet.WriteReal(o.DataHome, families, Math.Max(4, o.Preview / 2), real);
                if (n > 0) log.WriteLine($"[synth] {n} real picture(s) from {string.Join(", ", families)}, beside it: {real}");
            }
        }
        else if (o.Preview > 0)
        {
            // The batch's first pictures; with --count 0, the first labelled pictures already in the folder.
            var ids = (o.Count > 0
                    ? Enumerable.Range(0, o.Count).Select(i => Id(o.Seed, i, o.Generator))
                    : Directory.EnumerateFiles(o.Out, "*.mllabel.json").Select(f => Path.GetFileName(f)[..^".mllabel.json".Length]).Order(StringComparer.Ordinal))
                .Where(id => File.Exists(Path.Combine(o.Out, id + ".mllabel.json"))).Take(o.Preview).ToList();
            string sheet = Path.Combine(o.Out, "preview.png");
            ContactSheet.Write(o.Out, ids, sheet);
            log.WriteLine($"[synth] preview of {ids.Count} picture(s): {sheet}");
        }
        return failures.Count == 0 ? 0 : 1;
    }

    private static string StyleIn(string labelPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(labelPath));
        return doc.RootElement.TryGetProperty("style", out var s) ? s.GetString() ?? "" : "";
    }

    private static IEnumerable<string> ClassesIn(string labelPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(labelPath));
        return [.. doc.RootElement.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("class").GetString()!)];
    }
}
