// brief-iml-4-real-picture-labels.md §3 — crfml.label_to_cnl turns a picture's label into its netlist truth. The
// routine gate runs no Python (L9), so the converter itself is held by crfml.selftest (each fixture's .cnl is exactly
// what its label gives) and this file holds what the C# side owns: that those committed netlists read in circuitRF and
// are the circuits drawn — a crossing without a dot does not connect, a T-join does — that the kit's table of element
// types is circuitRF's own, and, where the data home is present, that every corpus label's derived netlist is
// topologically its imported truth.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Tests.ImageAcceptance;
using Xunit;

namespace CircuitRF.Ui.Tests.Vision;

public sealed class LabelToCnlTests
{
    private static string Fixture(string name) => Path.Combine(ClassListTests.KitDir(), "tests", "fixtures", name);

    private static TestBench Read(string text) => new CnlReader().Read(text).TestBench;

    [Fact]
    public void TheKitsElementTypes_AreCircuitRFsOwn()
    {
        using var types = JsonDocument.Parse(File.ReadAllText(Path.Combine(ClassListTests.KitDir(), "cnl-types.json")));
        var table = types.RootElement.GetProperty("types").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        foreach (var (kind, reference) in table)
        {
            Assert.True(Enum.TryParse<SymbolKind>(kind, out var k), $"cnl-types.json: no SymbolKind {kind}");
            Assert.Equal(ComponentTypeRegistry.EngineReference(k), reference);
        }
        foreach (var p in types.RootElement.GetProperty("paired").EnumerateArray())
            Assert.Contains(p.GetString()!, table.Keys);

        // every kind a class emits is in the table, so the converter never meets a kind it cannot write
        using var classes = JsonDocument.Parse(File.ReadAllText(Path.Combine(ClassListTests.KitDir(), "classes.json")));
        foreach (var c in classes.RootElement.GetProperty("classes").EnumerateArray())
            if (c.GetProperty("circuitrf") is { ValueKind: JsonValueKind.Object } map)
                Assert.Contains(map.GetProperty("kind").GetString()!, table.Keys);
    }

    [Theory]
    [InlineData("divider", "Port:P1 a 0 Num=1 Z=50 Ohm\nPort:P2 b 0 Num=2 Z=50 Ohm\nR:R1 a b R=1000\nR:R2 b 0 R=2200\n")]
    [InlineData("rc-port", "Port:P1 a 0 Num=1 Z=50 Ohm\nC:C1 a b C=1e-11\nL:L1 b 0 L=4.7e-09\n")]
    [InlineData("crossing", "Vdc:V1 x 0 Vdc=1\nDiode:D1 y 0\n")]
    public void AFixtureLabel_GivesTheCircuitDrawn(string name, string expected)
    {
        var got = Read(File.ReadAllText(Fixture(name + ".cnl")));
        Assert.Empty(got.ReadWarnings);
        Assert.True(NetlistShape.SameTopology(got, Read(expected)),
                    $"{name}.cnl is not the circuit drawn:\n{string.Join("\n", NetlistShape.Of(got))}");
    }

    [Fact]
    public void ACrossingWithoutADot_DoesNotConnect_AndATJoinDoes()
    {
        // crossing.png: the battery's plus runs right across the diode's anode wire with no dot, and the battery's minus
        // runs into the middle of the diode's ground wire, also with no dot
        var tb = Read(File.ReadAllText(Fixture("crossing.cnl")));
        var battery = tb.Instances.Single(i => i.Reference == "Vdc");
        var diode = tb.Instances.Single(i => i.Reference == "Diode");
        Assert.NotEqual(battery.NetBindings[0], diode.NetBindings[0]);
        Assert.Equal("0", battery.NetBindings[1]);
        Assert.Equal("0", diode.NetBindings[1]);
    }

    // ── the real labels, when the data home is here ────────────────────────────────────────────────────────────────

    internal static string DataHome() =>
        Environment.GetEnvironmentVariable("CRF_ML_DATA") is { Length: > 0 } d
            ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "circuitRF-ml-data");

    /// <summary>Each corpus picture whose label has been converted: its derived netlist and the truth it came with.</summary>
    internal static IReadOnlyList<(string Id, string Derived, string Truth)> DerivedCorpus()
    {
        string real = Path.Combine(DataHome(), "real");
        if (!Directory.Exists(real)) return [];
        return [.. Directory.GetDirectories(real, "corpus-*")
            .SelectMany(d => Directory.GetFiles(d, "*.derived.cnl"))
            .Select(f => (Id: Path.GetFileName(f)[..^".derived.cnl".Length], Derived: f))
            .Select(t => (t.Id, t.Derived, Truth: Path.Combine(Path.GetDirectoryName(t.Derived)!, t.Id + ".cnl")))
            .Where(t => File.Exists(t.Truth))
            .OrderBy(t => t.Id, StringComparer.Ordinal)];
    }

    [DataHomeFact]
    public void EveryCorpusLabel_IsTopologicallyItsTruth()
    {
        var wrong = DerivedCorpus()
            .Where(t => !NetlistShape.SameTopology(Read(File.ReadAllText(t.Derived)), Read(File.ReadAllText(t.Truth))))
            .Select(t => t.Id).ToList();
        Assert.True(wrong.Count == 0, "labels whose netlist is not their picture's truth: " + string.Join(", ", wrong));
    }
}

/// <summary>A fact that needs the labelled real pictures, which live outside the repository (L1): skipped, with the
/// reason, when the data home holds no converted corpus label.</summary>
public sealed class DataHomeFactAttribute : FactAttribute
{
    public DataHomeFactAttribute()
    {
        if (LabelToCnlTests.DerivedCorpus().Count == 0)
            Skip = "no labelled corpus pictures in the data home ($CRF_ML_DATA, else ~/circuitRF-ml-data): the real pictures " +
                   "and their labels are kept outside the repository (tools/schematic-ml/README.md)";
    }
}
