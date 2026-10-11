// brief-img-10-schematic-emit.md §4 — NetlistSchematic.Build's EXACT placement hints (R-im10-4): no hint, no change; each
// hinted instance at its snapped position and orientation; a hint that would land on another instance falls back for
// that instance alone.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class NetlistSchematicHintsTests
{
    private const string Netlist =
        "Port:P1 in 0 Num=1 Z=50 Ohm\nL:L1 in mid L=3.3 nH\nC:C1 mid 0 C=1.5 pF\nL:L2 mid out L=3.3 nH\n" +
        "Port:P2 out 0 Num=2 Z=50 Ohm\nanalysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz\n";

    /// <summary>SHA-256 of this netlist's drawing as Build made it before exact hints existed.</summary>
    private const string Before = "9ffdd1df73b8f729ccca7f0d8d192b35f27cbd511a27cb78766648280c7048c7";

    private static NetlistSchematicResult Draw(ExactPlacement? exact)
    {
        var (lib, tb) = new CnlReader().Read(Netlist);
        return NetlistSchematic.Build(lib, tb, exact: exact);
    }

    private static string Hash(SchematicEditModel m) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SchematicPersistence.Serialize(m))));

    [Fact]
    public void WithoutHints_TheDrawingIsByteIdenticalToBefore() =>
        Assert.Equal(Before, Hash(Assert.IsType<SchematicEditModel>(Draw(null).Schematic)));

    /// <summary>A ladder laid out by hand: the two series inductors across, the capacitor upright between them.</summary>
    private static Dictionary<string, ExactInstance> Ladder() => new()
    {
        ["P1"] = new(0, 0, SymbolRotation.R0, false),
        ["L1"] = new(1_010, -480, SymbolRotation.R90, false),
        ["C1"] = new(2_000, 520, SymbolRotation.R0, false),
        ["L2"] = new(3_000, -500, SymbolRotation.R270, true),
        ["P2"] = new(4_000, 0, SymbolRotation.R0, true),
    };

    [Fact]
    public void WithExactHints_EachInstanceIsAtItsSnappedPositionAndOrientation()
    {
        var r = Draw(new ExactPlacement { Instances = Ladder() });
        var m = Assert.IsType<SchematicEditModel>(r.Schematic);

        Assert.Empty(r.PlacementFallbacks);
        foreach (var (name, e) in Ladder())
        {
            var c = m.Components.Single(x => x.InstanceName == name);
            Assert.Equal((Math.Round(e.X / 100) * 100, Math.Round(e.Y / 100) * 100, e.Rotation, e.MirrorX),
                         (c.X, c.Y, c.Rotation, c.MirrorX));
        }
        // The drawing is the circuit the netlist states.
        var tb = NetExtractor.Extract(m).TestBench;
        Assert.Equal(["mid", "0"], tb.Instances.Single(i => i.InstanceName == "C1").NetBindings);
    }

    [Fact]
    public void AHintOnTopOfAnotherInstance_FallsBackForThatInstanceOnly()
    {
        var hints = Ladder();
        hints["L2"] = hints["L1"];   // drawn over L1: its pins would land on L1's, on other nets
        var r = Draw(new ExactPlacement { Instances = hints });
        var m = Assert.IsType<SchematicEditModel>(r.Schematic);

        Assert.Equal(["L2"], r.PlacementFallbacks);
        var l1 = m.Components.Single(x => x.InstanceName == "L1");
        Assert.Equal((1_000.0, -500.0, SymbolRotation.R90), (l1.X, l1.Y, l1.Rotation));
        var l2 = m.Components.Single(x => x.InstanceName == "L2");
        Assert.NotEqual((l1.X, l1.Y), (l2.X, l2.Y));
        Assert.Equal(["mid", "out"], NetExtractor.Extract(m).TestBench.Instances.Single(i => i.InstanceName == "L2").NetBindings
                                                                     .Order(StringComparer.Ordinal));
    }
}
