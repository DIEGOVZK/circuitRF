using System.Globalization;

namespace CircuitRF.SchematicSynth;

/// <summary>
/// Generated training pictures for the schematic picture reader (docs/design/schematic-vision.md §4,
/// tools/schematic-ml/README.md).
///
/// <code>
/// dotnet run --project tools/SchematicSynth -- crf --count 30000 --seed 1 --out $CRF_ML_DATA/synth/crf/batch-001
/// dotnet run --project tools/SchematicSynth -- crf --count 0 --preview 24 --seed 1 --out …   (a sheet of what is there)
/// dotnet run --project tools/SchematicSynth -- foreign --packs all --count 60000 --seed 1 --out $CRF_ML_DATA/synth/foreign/batch-001
/// </code>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("""
                usage: SchematicSynth crf     --count N --seed S --out DIR [--preview N] [--threads N]
                       SchematicSynth foreign --packs all|a,b --count N --seed S --out DIR [--data DIR] [--preview N] [--threads N]
                       SchematicSynth packs   (list the style packs)

                  crf         pictures of circuitRF's own schematics, drawn by its own renderer (IML-2)
                  foreign     the same circuits drawn in other editors' conventions, one style pack a picture (IML-3)
                  --packs     the style packs to draw from: all, or names separated by commas
                  --data      the data home, for its fetched fonts and real pictures (default $CRF_ML_DATA, else ~/circuitRF-ml-data)
                  --count     how many pictures (index 0 … N-1 of the seed's stream)
                  --seed      the batch seed; the same seed gives the same bytes
                  --out       the batch folder, OUTSIDE the repository ($CRF_ML_DATA/synth/crf/batch-001)
                  --preview   also write DIR/preview.png: the first N pictures with their labels drawn over them
                  --threads   parallel pictures (default: every core)
                """);
            return args.Length == 0 ? 2 : 0;
        }
        if (args[0] == "packs")
        {
            foreach (var p in StylePack.All)
                Console.WriteLine($"{p.Name,-26} {p.Medium,-6} {string.Join(",", p.Families),-22} {p.Glyphs.Count,3} classes  {p.About}");
            return 0;
        }
        if (args[0] is not ("crf" or "foreign"))
        {
            Console.Error.WriteLine($"Unknown generator '{args[0]}'. There are two: crf and foreign.");
            return 2;
        }

        int count = 0, seed = 1, preview = 0, threads = Environment.ProcessorCount;
        string? output = null, packs = null, data = null;
        for (int i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--count": count = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--out": output = Next(); break;
                case "--preview": preview = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--threads": threads = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--packs" when args[0] == "foreign": packs = Next(); break;
                case "--data": data = Next(); break;
                default: Console.Error.WriteLine($"Unknown option {args[i]}."); return 2;
            }
        }
        if (output is null)
        {
            Console.Error.WriteLine("--out is required: the batch folder, e.g. $CRF_ML_DATA/synth/crf/batch-001.");
            return 2;
        }

        string? repo = RepoRoot();
        if (repo is null)
        {
            Console.Error.WriteLine("Run this from inside the circuitRF repository: it reads the repository's own schematics.");
            return 2;
        }
        string outFull = Path.GetFullPath(output);
        string repoFull = Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if ((outFull + Path.DirectorySeparatorChar).StartsWith(repoFull, StringComparison.Ordinal))
        {
            // L1: generated pictures are data, and data never enters the repository.
            Console.Error.WriteLine($"Refused: {outFull} is inside the repository. Generated pictures go in the data home "
                                  + "($CRF_ML_DATA, default ~/circuitRF-ml-data), never in the working tree.");
            return 2;
        }

        IReadOnlyList<StylePack> chosen = [];
        if (args[0] == "foreign")
        {
            if (packs is null or "all") chosen = StylePack.All;
            else
            {
                try { chosen = [.. packs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(StylePack.Named)]; }
                catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
            }
        }
        data ??= Environment.GetEnvironmentVariable("CRF_ML_DATA") is { Length: > 0 } env ? env
               : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "circuitRF-ml-data");
        string? dataHome = Directory.Exists(data) ? Path.GetFullPath(data) : null;

        return Batch.Run(new BatchOptions(count, seed, outFull, preview, threads, repo) { Generator = args[0], Packs = chosen, DataHome = dataHome }, Console.Out);
    }

    /// <summary>The repository root: the nearest folder above the working directory, or above this program, holding
    /// <c>circuitRF.slnx</c>.</summary>
    public static string? RepoRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) return dir.FullName;
        }
        return null;
    }
}
