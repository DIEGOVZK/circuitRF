// brief-imf-1-corpus-and-scorer.md R-imf1-3, R-imf1-4 — how well a schematic picture reads, as numbers: one line per
// picture, the same columns for every picture, so a phase of the IMF series reports its effect against the baseline
// (design note §17) rather than as a pass or a fail.
//
// A picture's answer is its expected.json (testdata/image-sources/README.md holds the schema), and, when it names one,
// an answer netlist: the topology is the reading's part–net graph against the answer's (NetlistShape.SameTopology —
// kinds and connections, ground and ports included; names, values and port numbers left out). Every other column is
// checked only when expected.json states it, and prints `-` when it does not. A count is right/drawn with an over-count
// costing as much as an under-count; a list (designators, names) is right/drawn by the entries read.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Schematic.Recognition;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

/// <summary>A picture's expected.json, read. Every key but <see cref="Picture"/> and <see cref="Kind"/> is optional.</summary>
internal sealed record FieldExpected
{
    public required string Picture { get; init; }
    public required string Kind { get; init; }
    public string? Source { get; init; }
    public bool? Refused { get; init; }
    public int? Parts { get; init; }
    public int? Nets { get; init; }
    public IReadOnlyDictionary<string, int>? Kinds { get; init; }
    public int? Grounds { get; init; }
    public int? Terminals { get; init; }
    public int? Ports { get; init; }
    public int? Transistors { get; init; }
    public int? Sources { get; init; }
    public int? Amplifiers { get; init; }
    public int? Ics { get; init; }
    public int? Transformers { get; init; }
    public IReadOnlyList<string>? Designators { get; init; }
    public IReadOnlyDictionary<string, string>? Values { get; init; }
    public IReadOnlyList<string>? Names { get; init; }
    public string? Style { get; init; }

    /// <summary>The circuit the picture draws, from the <c>answer</c> netlist beside it.</summary>
    public TestBench? Answer { get; init; }

    public static FieldExpected Load(string folder)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "expected.json")));
        var r = doc.RootElement;
        int? Int(string key) => r.TryGetProperty(key, out var v) ? v.GetInt32() : null;
        string? Str(string key) => r.TryGetProperty(key, out var v) ? v.GetString() : null;
        return new FieldExpected
        {
            Picture = r.GetProperty("picture").GetString()!,
            Kind = r.GetProperty("kind").GetString()!,
            Source = Str("source"),
            Refused = r.TryGetProperty("refused", out var refused) ? refused.GetBoolean() : null,
            Parts = Int("parts"), Nets = Int("nets"), Grounds = Int("grounds"), Terminals = Int("terminals"), Ports = Int("ports"),
            Transistors = Int("transistors"), Sources = Int("sources"), Amplifiers = Int("amplifiers"), Ics = Int("ics"),
            Transformers = Int("transformers"),
            Kinds = r.TryGetProperty("kinds", out var kinds) ? kinds.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()) : null,
            Designators = r.TryGetProperty("designators", out var des) ? [.. des.EnumerateArray().Select(d => d.GetString()!)] : null,
            Values = r.TryGetProperty("values", out var values) ? values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!) : null,
            Names = r.TryGetProperty("names", out var names) ? [.. names.EnumerateArray().Select(d => d.GetString()!)] : null,
            Style = Str("style"),
            Answer = Str("answer") is { } answer ? new CnlReader().Read(File.ReadAllText(Path.Combine(folder, answer))).Item2 : null,
        };
    }
}

/// <summary><paramref name="Right"/> of <paramref name="Of"/>.</summary>
internal readonly record struct Tally(int Right, int Of)
{
    public bool Full => Right == Of;
    public override string ToString() => $"{Right}/{Of}";
}

/// <summary>One picture's score. A null tally was not stated in its expected.json.</summary>
internal sealed record FieldScore
{
    public required string Picture { get; init; }
    public bool Refused { get; init; }
    public string? Refusal { get; init; }
    public bool? RefusedExpected { get; init; }

    /// <summary>The kind the picture classifies as on its own; the reading itself is Paste's, which reads it as a schematic
    /// unless it is no drawing at all.</summary>
    public DrawingKind Auto { get; init; }
    public string ExpectedKind { get; init; } = "schematic";
    public double Seconds { get; init; }

    public Tally? Kinds { get; init; }
    /// <summary>Symbols of no known kind.</summary>
    public int Unknown { get; init; }
    /// <summary>The part–net graph isomorphic to the answer's; null with no answer.</summary>
    public bool? Topology { get; init; }
    public Tally? Ports { get; init; }
    public Tally? Grounds { get; init; }
    public Tally? Terminals { get; init; }
    /// <summary>Transistors, sources, amplifiers, ICs and transformers together.</summary>
    public Tally? Devices { get; init; }
    /// <summary>The answer's amplifiers and ICs read as one part each, on as many nets.</summary>
    public Tally? Blocks { get; init; }
    public Tally? Designators { get; init; }
    public Tally? Values { get; init; }
    public Tally? Names { get; init; }
    /// <summary>The most pins any one symbol region has.</summary>
    public int LargestRegion { get; init; }
    /// <summary>The reading the reader kept (IMF-9) and its plausibility; null when no reading scored one.</summary>
    public string? Reading { get; init; }
    public double? Plausibility { get; init; }

    /// <summary>Everything the committed fixtures promise (overview §4): the circuit exactly, every designator.</summary>
    public bool Exact => !Refused && Topology == true && Kinds is not { Full: false } && Ports is not { Full: false }
                         && Grounds is not { Full: false } && Blocks is not { Full: false } && Designators is not { Full: false };

    public const string Header =
        "picture                  auto       read     kinds  ?   topo ports gnd   term  dev   block desig values names region     ms  reading    plaus";

    public string Line()
    {
        static string T(Tally? t) => t?.ToString() ?? "-";
        string auto = ImageKind.Name(Auto) + (string.Equals(ImageKind.Name(Auto), ExpectedKind, StringComparison.OrdinalIgnoreCase) ? "" : "!");
        string read = (Refused ? "refused" : "read") + (RefusedExpected is { } r && r != Refused ? "!" : "");
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{Picture,-24} {auto,-10} {read,-8} {T(Kinds),-6} {Unknown,-3} {(Topology is { } t ? t ? "yes" : "no" : "-"),-4} " +
            $"{T(Ports),-5} {T(Grounds),-5} {T(Terminals),-5} {T(Devices),-5} {T(Blocks),-5} {T(Designators),-5} " +
            $"{T(Values),-6} {T(Names),-5} {LargestRegion,-6} {Seconds * 1000,6:F0}  {Reading ?? "-",-10} {(Plausibility is { } p ? p.ToString("0.00", CultureInfo.InvariantCulture) : "-")}");
        return Refusal is null ? line : $"{line}  — {Refusal}";
    }

    /// <summary>Reads the picture in <paramref name="folder"/> as Paste does — every option at its default, the kind
    /// left to the reader, which reads a layout-looking picture as a schematic and refuses one that is no drawing
    /// (IMF-9) — and scores it against its expected.json.</summary>
    public static FieldScore Read(string folder, string name)
    {
        var expected = FieldExpected.Load(folder);
        var source = ImageSource.FromFile(Path.Combine(folder, expected.Picture));
        if (!source.Ok)
            return new FieldScore { Picture = name, Refused = true, Refusal = source.Refusal, RefusedExpected = expected.Refused, ExpectedKind = expected.Kind };
        var kind = ImageKind.Classify(source.Source!.Raster);
        var clock = Stopwatch.StartNew();
        var read = SchematicImageRecognition.Circuit(new SchematicImageInput { Source = source.Source! });
        clock.Stop();
        return Of(read, expected) with { Picture = name, Auto = kind.Kind, Seconds = clock.Elapsed.TotalSeconds };
    }

    /// <summary>R-imf1-3 — <paramref name="read"/> scored against <paramref name="expected"/>.</summary>
    public static FieldScore Of(SchematicImageResult read, FieldExpected expected)
    {
        var symbols = read.Symbols?.Symbols ?? [];
        var tb = read.Circuit?.TestBench;
        var instances = tb?.Instances ?? [];
        // A symbol of no known kind is emitted as a stand-in (a C today): it is not a part of that kind read.
        var symbolOf = read.Circuit?.SymbolOf ?? new Dictionary<string, int>();
        bool Unread(Instance i) => symbolOf.TryGetValue(i.InstanceName, out int k) && symbols[k].Kind == ImageSymbolKind.Unknown;
        int Count(string reference) => instances.Count(i => i.Reference == reference && !Unread(i));
        int Symbols(ImageSymbolKind k) => symbols.Count(s => s.Kind == k);
        static Tally? Of(int? want, int got) => want is { } w ? new Tally(Right(w, got), w) : null;

        Tally? kinds = expected.Kinds is { } ks ? new Tally(ks.Sum(k => Right(k.Value, Count(k.Key))), ks.Values.Sum()) : null;

        var devices = new (int? Want, int Got)[]
        {
            (expected.Transistors, Symbols(ImageSymbolKind.Transistor)),
            (expected.Sources, instances.Count(i => i.Reference is "P1Tone" or "Vdc")),
            (expected.Amplifiers, Symbols(ImageSymbolKind.Amplifier)),
            (expected.Ics, Symbols(ImageSymbolKind.Ic)),
            (expected.Transformers, Count("Mutual")),
        }.Where(d => d.Want is not null).ToList();

        // An amplifier or IC is right when one part of the reading is on as many nets as the answer's.
        Tally? blocks = null;
        if (expected.Answer is { } answer)
        {
            static bool IsBlock(Instance i) => i.Reference is "SnP" or "Amp";
            static int Nets(Instance i) => NetlistShape.Pins(i).Distinct().Count();
            var want = answer.Instances.Where(IsBlock).Select(Nets).ToList();
            if (want.Count > 0)
            {
                var got = instances.Where(IsBlock).Select(Nets).ToList();
                int right = 0;
                foreach (int n in want)
                    if (got.Remove(n)) right++;
                blocks = new Tally(right, want.Count);
            }
        }

        var labels = read.Labels?.Symbols.Where(s => !s.Generated && s.Designator is not null).ToList() ?? [];
        Tally? designators = expected.Designators is { } ds
            ? new Tally(Matched(ds, labels.Select(l => l.Designator!)), ds.Count) : null;
        Tally? values = expected.Values is { } vs
            ? new Tally(vs.Count(v => labels.Any(l => Same(l.Designator!, v.Key) && l.ValueWord is { } w && SameValue(v.Value, w))), vs.Count)
            : null;
        Tally? names = expected.Names is { } ns
            ? new Tally(Matched(ns, read.Labels?.Names.Select(n => n.Name) ?? []), ns.Count) : null;

        return new FieldScore
        {
            Picture = expected.Picture,
            Refused = !read.Ok,
            Refusal = read.Refusal,
            RefusedExpected = expected.Refused,
            ExpectedKind = expected.Kind,
            Kinds = kinds,
            Unknown = Symbols(ImageSymbolKind.Unknown),
            Topology = expected.Answer is { } a ? tb is not null && NetlistShape.SameTopology(a, tb) : null,
            Ports = Of(expected.Ports, Count("Port")),
            Grounds = Of(expected.Grounds, read.Circuit?.Grounds.Count ?? 0),
            Terminals = Of(expected.Terminals, Symbols(ImageSymbolKind.Port)),
            Devices = devices.Count > 0 ? new Tally(devices.Sum(d => Right(d.Want!.Value, d.Got)), devices.Sum(d => d.Want!.Value)) : null,
            Blocks = blocks,
            Designators = designators,
            Values = values,
            Names = names,
            LargestRegion = symbols.Count > 0 ? symbols.Max(s => s.Pins.Count) : 0,
            Reading = read.Plausibility is not null ? read.ReadingName : null,
            Plausibility = read.Plausibility?.Score,
        };
    }

    /// <summary>A count is right by how near it is: four ports read where two are drawn is none right, not two.</summary>
    private static int Right(int want, int got) => Math.Max(0, want - Math.Abs(got - want));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>How many of <paramref name="want"/> are among <paramref name="got"/>, each read once.</summary>
    private static int Matched(IEnumerable<string> want, IEnumerable<string> got)
    {
        var left = got.ToList();
        int right = 0;
        foreach (string w in want)
            if (left.FindIndex(g => Same(g, w)) is var k and >= 0) { left.RemoveAt(k); right++; }
        return right;
    }

    /// <summary>A value is right when it reads as the same number, or — a part number — as the same text.</summary>
    private static bool SameValue(string want, ImageWord word)
    {
        // A bare number (a resistor's 240) is a value in its part's base unit, which the grammar reads only with one.
        double? si = ValueGrammar.ReadValue(want)?.Si
                     ?? (double.TryParse(want, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) ? n : null);
        if (si is { } w && word.Reading?.Value is { } g)
            return Math.Abs(w - g.Si) <= 1e-6 * Math.Abs(w);
        return string.Equals(want.Replace(" ", ""), word.Text.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
    }
}
