// A circuit as a graph with its names taken away — what R-im12's pictures are compared by (brief-img-12 §2): a
// picture's net names and designators are its own, so two circuits agree when the same parts, with the same values,
// are connected alike.
//
// SameTopology is brief-imf-1-corpus-and-scorer.md R-imf1-3's topology: the bipartite part–net graph, each part by
// kind and each net by whether it is ground, compared up to isomorphism — values, names, pin order and port numbers
// left out. Of above sorts each part's neighbourhood, which two different circuits can share; this cannot be fooled.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Schematic.Recognition;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

internal static class NetlistShape
{
    /// <summary>The value that makes a part what it is, in base SI: R, L, C, or a port's number and impedance. Empty
    /// when <paramref name="values"/> is false.</summary>
    public static string Value(Instance i, bool values = true)
    {
        if (!values) return i.Reference == "Port" ? "#" + i.Overrides.Single(o => o.Name == "Num").Expression : "";
        string? Si(string name) => i.Overrides.FirstOrDefault(o => o.Name == name) is { } o
            ? ValueGrammar.ReadValue(o.Expression + (o.Unit ?? ""))?.Si.ToString("G6", CultureInfo.InvariantCulture) ?? o.Expression
            : null;
        return i.Reference switch
        {
            "Port" => $"#{i.Overrides.Single(o => o.Name == "Num").Expression} Z={Si("Z")}",
            "R" or "L" or "C" => Si(i.Reference) ?? "",
            _ => "",
        };
    }

    /// <summary>Each part by kind and value with, for each of its pins, the parts on that pin's net (ground marked),
    /// sorted. Ports are left out when <paramref name="ports"/> is false, values when <paramref name="values"/> is.</summary>
    public static IReadOnlyList<string> Of(TestBench tb, bool ports = true, bool values = true)
    {
        var parts = tb.Instances.Where(i => ports || i.Reference != "Port").ToList();
        string Label(Instance i) => $"{i.Reference}[{Value(i, values)}]";
        string Net(string net) => net == "0" ? "GND" :
            string.Join(",", parts.Where(i => i.NetBindings.Contains(net)).Select(Label).Order(StringComparer.Ordinal));
        return [.. parts.Select(i => $"{Label(i)}: {string.Join(" | ", i.NetBindings.Select(Net).Order(StringComparer.Ordinal))}")
                        .Order(StringComparer.Ordinal)];
    }

    /// <summary>The nets a part's pins are on, its reference among them: an SnP's is ground when none is named.</summary>
    public static IReadOnlyList<string> Pins(Instance i) =>
        i.RefNetBinding is { } r ? [.. i.NetBindings, r] : i.Reference == "SnP" ? [.. i.NetBindings, "0"] : i.NetBindings;

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are the same parts by kind, connected alike, ground
    /// included: their part–net graphs are isomorphic.</summary>
    public static bool SameTopology(TestBench a, TestBench b)
    {
        var ga = new Graph(a);
        var gb = new Graph(b);
        if (ga.Parts.Count != gb.Parts.Count || ga.Ground.Count != gb.Ground.Count) return false;

        // Colour refinement over both graphs at once, so a colour means the same thing in each: a part by its kind and
        // its nets' colours, a net by groundness and its parts' colours, until the number of colours stops growing.
        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        int Code(string signature) => codes.TryGetValue(signature, out int c) ? c : codes[signature] = codes.Count;
        foreach (var g in new[] { ga, gb })
        {
            g.PartColour = [.. g.Parts.Select(p => Code("p:" + p.Kind))];
            g.NetColour = [.. g.Ground.Select(z => Code(z ? "n:0" : "n:"))];
        }
        for (int distinct = -1; ;)
        {
            foreach (var g in new[] { ga, gb })
            {
                var parts = g.Parts.Select((p, k) => Code($"{g.PartColour[k]}|{string.Join(',', p.Nets.Select(n => g.NetColour[n]).Order())}")).ToArray();
                var nets = g.NetColour.Select((c, n) => Code($"{c}|{string.Join(',', g.Incident[n].Select(k => g.PartColour[k]).Order())}")).ToArray();
                g.PartColour = parts;
                g.NetColour = nets;
            }
            int now = ga.PartColour.Concat(ga.NetColour).Concat(gb.PartColour).Concat(gb.NetColour).Distinct().Count();
            if (now == distinct) break;
            distinct = now;
        }
        if (!ga.PartColour.Order().SequenceEqual(gb.PartColour.Order()) || !ga.NetColour.Order().SequenceEqual(gb.NetColour.Order()))
            return false;

        // A search for the bijection itself, part by part, the rarest colour first; each pin's net mapped as it goes.
        var order = Enumerable.Range(0, ga.Parts.Count).OrderBy(k => ga.PartColour.Count(c => c == ga.PartColour[k])).ToArray();
        var used = new bool[gb.Parts.Count];
        var netMap = Enumerable.Repeat(-1, ga.Ground.Count).ToArray();
        var netBack = Enumerable.Repeat(-1, gb.Ground.Count).ToArray();

        bool Part(int step)
        {
            if (step == order.Length) return true;
            int pa = order[step];
            for (int pb = 0; pb < gb.Parts.Count; pb++)
            {
                if (used[pb] || gb.PartColour[pb] != ga.PartColour[pa]) continue;
                used[pb] = true;
                if (Pin(pa, pb, 0, new bool[gb.Parts[pb].Nets.Count], () => Part(step + 1))) return true;
                used[pb] = false;
            }
            return false;
        }

        bool Pin(int pa, int pb, int pin, bool[] taken, Func<bool> next)
        {
            var netsA = ga.Parts[pa].Nets;
            if (pin == netsA.Count) return next();
            int na = netsA[pin];
            var netsB = gb.Parts[pb].Nets;
            for (int q = 0; q < netsB.Count; q++)
            {
                int nb = netsB[q];
                if (taken[q] || ga.NetColour[na] != gb.NetColour[nb]) continue;
                bool fresh = netMap[na] < 0;
                if (fresh ? netBack[nb] >= 0 : netMap[na] != nb) continue;
                netMap[na] = nb;
                netBack[nb] = na;
                taken[q] = true;
                if (Pin(pa, pb, pin + 1, taken, next)) return true;
                taken[q] = false;
                if (fresh) netMap[na] = netBack[nb] = -1;
            }
            return false;
        }

        return Part(0);
    }

    /// <summary>A netlist as parts (kind, the index of each pin's net) and nets (ground or not, the parts on each).</summary>
    private sealed class Graph
    {
        public readonly List<(string Kind, List<int> Nets)> Parts = [];
        public readonly List<bool> Ground = [];
        public readonly List<List<int>> Incident = [];
        public int[] PartColour = [];
        public int[] NetColour = [];

        public Graph(TestBench tb)
        {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var i in tb.Instances)
            {
                var nets = new List<int>();
                foreach (string net in Pins(i))
                {
                    if (!index.TryGetValue(net, out int n))
                    {
                        index[net] = n = Ground.Count;
                        Ground.Add(net == "0");
                        Incident.Add([]);
                    }
                    nets.Add(n);
                    Incident[n].Add(Parts.Count);
                }
                Parts.Add((i.Reference, nets));
            }
        }
    }
}
