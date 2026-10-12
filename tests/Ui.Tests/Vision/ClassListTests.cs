// brief-iml-1-data-home-and-label-format.md §3 — tools/schematic-ml/classes.json is the one class list the generator,
// the training kit and every exported model share. It may only name circuitRF kinds that exist, and a class whose pins
// are fixed must draw exactly the pins that kind has: IML-7 maps a detected part's pins onto the kind's by name.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Vision;

public sealed class ClassListTests
{
    internal static string KitDir()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "tools", "schematic-ml");
    }

    private static JsonElement Classes()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(KitDir(), "classes.json")));
        return doc.RootElement.GetProperty("classes").Clone();
    }

    private static SymbolKind Kind(string name, string cls)
    {
        Assert.True(Enum.TryParse<SymbolKind>(name, ignoreCase: false, out var kind) && Enum.IsDefined(kind),
                    $"{cls}: SymbolKind {name} does not exist");
        return kind;
    }

    private static string[] Names(JsonElement c, string property) =>
        c.TryGetProperty(property, out var a) ? [.. a.EnumerateArray().Select(e => e.GetString()!)] : [];

    [Fact]
    public void Names_AreUniqueKebabCase()
    {
        var names = Classes().EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToList();
        Assert.NotEmpty(names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, n => Assert.Matches(new Regex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$"), n));
    }

    [Fact]
    public void EveryMappedClass_NamesAKindThatExists_AndDrawsThatKindsPins()
    {
        foreach (var c in Classes().EnumerateArray())
        {
            string cls = c.GetProperty("name").GetString()!;
            var map = c.GetProperty("circuitrf");
            if (map.ValueKind == JsonValueKind.String)
            {
                Assert.Equal("ignore", map.GetString());
                continue;
            }
            var kind = Kind(map.GetProperty("kind").GetString()!, cls);
            if (map.TryGetProperty("coupled", out var coupled)) Kind(coupled.GetString()!, cls);

            var pins = c.GetProperty("pins");
            if (pins.ValueKind == JsonValueKind.String)
            {
                // A box whose pins run round its perimeter maps to a kind whose pin count is a parameter.
                Assert.Equal("perimeter", pins.GetString());
                Assert.True(ComponentTypeRegistry.PortCountParameter(kind) is not null,
                            $"{cls}: perimeter pins need a variadic kind, and {kind} is fixed");
                continue;
            }

            string[] drawn = [.. pins.EnumerateArray().Select(p => p.GetProperty("name").GetString()!)];
            string[] kindPins = [.. SymbolPortDefs.For(kind).Select(p => p.Name)];
            if (map.TryGetProperty("count", out var count))
            {
                // A transformer is N coils of one kind, coupled: N times the kind's pins.
                Assert.Equal(count.GetInt32() * kindPins.Length, drawn.Length);
                continue;
            }
            // The pins drawn, plus the ones the emit leaves open or ties to ground, are exactly the kind's pins.
            var all = drawn.Concat(Names(map, "open")).Concat(Names(map, "grounded")).ToList();
            Assert.True(all.Count == kindPins.Length && all.ToHashSet(StringComparer.Ordinal).SetEquals(kindPins),
                        $"{cls}: pins {string.Join(",", all)} against {kind}'s {string.Join(",", kindPins)}");
        }
    }
}
