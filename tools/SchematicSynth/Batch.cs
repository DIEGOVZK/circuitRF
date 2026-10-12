using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace CircuitRF.SchematicSynth;

public sealed record BatchOptions(int Count, int Seed, string Out, int Preview, int Threads, string RepoRoot);

/// <summary>
/// A batch of generated circuitRF pictures (brief-iml-2 R-iml2-5): deterministic for a seed (picture <c>k</c> of seed
/// <c>s</c> is the same bytes on every run and every machine, whatever the thread count), parallel across cores,
/// resumable (a picture whose label exists is skipped — the label is written last), with a class histogram at the end.
/// </summary>
public static class Batch
{
    /// <summary>The picture's id: the generator, the seed and its index.</summary>
    public static string Id(int seed, int index) => $"crf-{seed}-{index.ToString("D6", CultureInfo.InvariantCulture)}";

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
        if (d is null) { why = "no drawing extracted to its circuit in five attempts"; return null; }

        var pic = PictureMaker.Make(Id(seed, index), d, r, underlays);
        var errors = LabelChecks.Check(pic.Label, pic.SymbolMask).Where(f => f.Error).ToList();
        if (errors.Count > 0) { why = string.Join("; ", errors.Take(3)); return null; }
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
        var underlays = Underlays.Create(Path.Combine(o.Out, ".underlay"), o.Seed);
        log.WriteLine($"[synth] {o.Count} picture(s), seed {o.Seed}, {o.Threads} thread(s), {authored.Count} authored schematic(s) in the mix");

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
            string id = Id(o.Seed, i);
            string labelPath = Path.Combine(o.Out, id + ".mllabel.json");
            if (File.Exists(labelPath))
            {
                Count(ClassesIn(labelPath));
                Interlocked.Increment(ref skipped);
            }
            else
            {
                PictureFiles? pic;
                string? why;
                try { pic = MakeOne(o.Seed, i, authored, underlays, out why); }
                catch (Exception ex) { pic = null; why = ex.GetType().Name + ": " + ex.Message; }
                if (pic is null) failures.Add($"{id}: {why}");
                else
                {
                    Write(pic, o.Out);
                    Count(pic.Classes);
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
            if (p == 0 && !ClassList.OfKind.Values.Contains(cls) && cls != "stub-open") mark = "  (not drawn by circuitRF)";
            log.WriteLine($"{cls,-20} {p,8} {(total > 0 ? 100.0 * p / total : 0),6:0.0} % {n,8}{mark}");
        }

        if (o.Preview > 0)
        {
            // The batch's first pictures; with --count 0, the first labelled pictures already in the folder.
            var ids = (o.Count > 0
                    ? Enumerable.Range(0, o.Count).Select(i => Id(o.Seed, i))
                    : Directory.EnumerateFiles(o.Out, "*.mllabel.json").Select(f => Path.GetFileName(f)[..^".mllabel.json".Length]).Order(StringComparer.Ordinal))
                .Where(id => File.Exists(Path.Combine(o.Out, id + ".mllabel.json"))).Take(o.Preview).ToList();
            string sheet = Path.Combine(o.Out, "preview.png");
            ContactSheet.Write(o.Out, ids, sheet);
            log.WriteLine($"[synth] preview of {ids.Count} picture(s): {sheet}");
        }
        return failures.Count == 0 ? 0 : 1;
    }

    private static IEnumerable<string> ClassesIn(string labelPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(labelPath));
        return [.. doc.RootElement.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("class").GetString()!)];
    }
}
