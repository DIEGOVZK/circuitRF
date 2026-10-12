// brief-iml-1-data-home-and-label-format.md R-iml1-5 — the training kit's validator reads a label's value words with a
// Python port of the value grammar, built on tools/schematic-ml/units.json. This holds that file to ValueGrammar itself,
// both ways where a table can be enumerated: every spelling it lists reads, and every letter it leaves out does not.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Vision;

public sealed class UnitsTableTests
{
    private static readonly JsonElement Units = Load();

    private static JsonElement Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ClassListTests.KitDir(), "units.json")));
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, string> Map(string property) =>
        Units.GetProperty(property).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

    private static double Scale(string prefix) =>
        prefix.Length == 0 ? 1 : Units.GetProperty("prefixScale").GetProperty(prefix).GetDouble();

    private static void Near(double want, double got, string what) =>
        Assert.True(Math.Abs(want - got) <= 1e-9 * Math.Abs(want) + 1e-30, $"{what}: want {want}, got {got}");

    /// <summary>A value as the kit's validator reads one: ValueGrammar.Parse's order without the line set.</summary>
    private static WordValue? Value(string text) => ValueGrammar.ReadValue(text) ?? ValueGrammar.ReadNamedValue(text);

    /// <summary>Every letter a prefix could be spelled with, and the two micro signs.</summary>
    private static IEnumerable<string> Letters() =>
        Enumerable.Range('a', 26).Concat(Enumerable.Range('A', 26)).Select(c => ((char)c).ToString()).Append("µ").Append("μ");

    [Fact]
    public void Examples_ReadAsStated()
    {
        foreach (var ex in Units.GetProperty("examples").EnumerateArray())
        {
            string text = ex.GetProperty("text").GetString()!;
            if (ex.TryGetProperty("line", out var line))
            {
                var ps = ValueGrammar.ReadLineParameters(text);
                Assert.True(ps is not null, $"{text} reads as line parameters");
                var want = line.EnumerateArray().ToList();
                Assert.Equal(want.Count, ps!.Count);
                for (int i = 0; i < want.Count; i++)
                {
                    Assert.Equal(want[i][0].GetString(), ps[i].Key);
                    Near(want[i][1].GetDouble(), ps[i].Si, text);
                    Assert.Equal(want[i][2].GetString(), ps[i].Dimension.ToString());
                }
                continue;
            }
            var v = Value(text);
            Assert.True(v is not null, $"{text} reads as a value");
            Near(ex.GetProperty("si").GetDouble(), v!.Si, text);
            Assert.Equal(ex.GetProperty("dims").EnumerateArray().Select(d => d.GetString()), v.Dimensions.Select(d => d.ToString()));
            Assert.Equal(ex.TryGetProperty("notFitted", out var nf) && nf.GetBoolean(), v.NotFitted);
        }
    }

    [Fact]
    public void Rejects_ReadAsNothing()
    {
        foreach (var r in Units.GetProperty("rejects").EnumerateArray())
        {
            string text = r.GetString()!;
            Assert.True(Value(text) is null && ValueGrammar.ReadLineParameters(text) is null, $"{text} reads as nothing");
        }
    }

    [Fact]
    public void FaradAndHenryPrefixes_AreExactlyTheTable()
    {
        var table = Map("faradHenryPrefixes");
        bool femtoOnlyFarads = Units.GetProperty("femtoOnlyFarads").GetBoolean();
        foreach (string unit in new[] { "F", "H" })
            foreach (string letter in Letters())
            {
                string text = "1" + letter + unit;
                var v = ValueGrammar.ReadValue(text);
                bool listed = table.ContainsKey(letter) && !(femtoOnlyFarads && letter == "f" && unit == "H");
                var dim = unit == "F" ? "Capacitance" : "Inductance";
                bool read = v is { NotFitted: false } && v.Dimensions.Count == 1 && v.Dimensions[0].ToString() == dim;
                Assert.True(listed == read, $"{text}: units.json says {(listed ? "a value" : "not one")}, ValueGrammar {(read ? "reads it" : "does not")}");
                if (listed) Near(Scale(table[letter]), v!.Si, text);
            }
    }

    [Fact]
    public void OhmWordsAndPrefixes_AreExactlyTheTable()
    {
        var table = Map("ohmPrefixes");
        foreach (var word in Units.GetProperty("ohmWords").EnumerateArray().Select(w => w.GetString()!).Append("Ω"))
        {
            foreach (string letter in Letters())
            {
                string text = "1" + letter + word;
                var v = ValueGrammar.ReadValue(text);
                bool listed = table.ContainsKey(letter);
                bool read = v is { NotFitted: false } && v.Dimensions.SequenceEqual([UnitDimension.Resistance]);
                Assert.True(listed == read, $"{text}: units.json says {(listed ? "a value" : "not one")}, ValueGrammar {(read ? "reads it" : "does not")}");
                if (listed) Near(Scale(table[letter]), v!.Si, text);
            }
            Near(1, ValueGrammar.ReadValue("1" + word)!.Si, "1" + word);
        }
    }

    [Fact]
    public void LettersAsDecimalPoints_AreExactlyTheTable()
    {
        var lap = Units.GetProperty("letterAsPoint");
        string letters = lap.GetProperty("letters").GetString()!;
        var resistor = lap.GetProperty("resistorLetters").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        string unitless = lap.GetProperty("unitlessResistorLetters").GetString()!;
        foreach (string letter in Letters())
        {
            string text = "4" + letter + "7";
            var v = ValueGrammar.ReadValue(text);
            Assert.True(letters.Contains(letter) == v is not null, $"{text}: listed {letters.Contains(letter)}, read {v is not null}");
            if (unitless.Contains(letter))
            {
                Assert.Equal([UnitDimension.Resistance], v!.Dimensions);
                Near(4.7 * Scale(resistor[letter]), v.Si, text);
            }
        }
    }

    [Fact]
    public void NotFittedWords_ReadAsNotFitted()
    {
        foreach (var w in Units.GetProperty("notFittedWords").EnumerateArray())
        {
            string word = w.GetString()!.ToUpperInvariant();
            Assert.True(ValueGrammar.ReadValue(word) is { NotFitted: true }, $"{word} is a not-fitted marker");
        }
    }

    [Fact]
    public void LineLengthsAndFrequencies_AreTheTable()
    {
        foreach (var (property, key) in new[] { ("lineLengths", "L"), ("lineFrequencies", "F") })
            foreach (var row in Units.GetProperty(property).EnumerateArray())
            {
                string unit = row[0].GetString()!, text = $"{key}=2{unit}";
                var ps = ValueGrammar.ReadLineParameters(text);
                Assert.True(ps is { Count: 1 }, $"{text} reads");
                Near(2 * row[1].GetDouble(), ps![0].Si, text);
            }
    }
}
