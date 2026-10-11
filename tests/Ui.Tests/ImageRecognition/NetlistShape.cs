// A circuit as a graph with its names taken away — what R-im12's pictures are compared by (brief-img-12 §2): a
// picture's net names and designators are its own, so two circuits agree when the same parts, with the same values,
// are connected alike.

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
}
