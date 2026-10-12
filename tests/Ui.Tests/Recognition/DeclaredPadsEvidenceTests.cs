// brief-artsch-12-declared-pads-netlist.md §4 — the parts and nets a Gerber set's pad attributes declare: parts
// with their terminals in declared pin order, a connector's port named from its pin name, the land-pattern reading
// when the attributes are stripped, and the declared nets checked against the copper.

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition;

public sealed class DeclaredPadsEvidenceTests
{
    private static readonly Dictionary<string, string> AgreeingNets = new()
    {
        ["J1,1,In"] = "IN", ["C1,2"] = "IN", ["C1,1"] = "N1", ["L1,1"] = "N1", ["L1,2"] = "OUT",
    };

    /// <summary>
    /// A Bottom plane 40 × 10 mm; on Top a connector pad J1 and a line to C1, a line from C1 to L1 and one from L1 to the
    /// right edge — every land an 0402 with its mask openings. C1's pin 1 is its RIGHT pad, so a terminal order taken
    /// from the geometry would be backwards.
    /// </summary>
    private static LayoutView Board(bool declared, IReadOnlyDictionary<string, string>? nets = null)
    {
        var view = new LayoutView();
        view.Shapes.Add(Rect(Bottom, 0, 0, 40_000, 10_000));
        var c1 = Land("0402", DensityLevel.Nominal, 12_000, 5_000);
        var l1 = Land("0402", DensityLevel.Nominal, 24_000, 5_000);
        var (c1l, c1r) = Ends(c1);
        var (l1l, l1r) = Ends(l1);
        view.Shapes.AddRange(c1);
        view.Shapes.AddRange(l1);
        var j1 = Rect(Top, 3_000, 4_500, 4_000, 5_500);
        view.Shapes.Add(j1);
        view.Shapes.Add(Rect(Top, 3_500, 4_800, c1l, 5_200));
        view.Shapes.Add(Rect(Top, c1r, 4_800, l1l, 5_200));
        view.Shapes.Add(Rect(Top, l1r, 4_800, 40_000, 5_200));
        if (!declared) return view;

        void Declare(LayoutShape s, string pin)
        {
            s.Pin = pin;
            s.Net = nets?.GetValueOrDefault(pin);
        }
        Declare(j1, "J1,1,In");
        var c1Copper = c1.Where(s => s.Layer == Top).OrderBy(s => s.X1).ToList();
        var l1Copper = l1.Where(s => s.Layer == Top).OrderBy(s => s.X1).ToList();
        Declare(c1Copper[0], "C1,2");
        Declare(c1Copper[1], "C1,1");
        Declare(l1Copper[0], "L1,1");
        Declare(l1Copper[1], "L1,2");
        return view;
    }

    [Fact]
    public void DeclaredPadsPlaceThePartsInPinOrderAndNameTheConnectorsPort()
    {
        var result = RecognizeParts(Board(declared: true), null, null);

        Assert.True(result.Ok, result.Refusal);
        var c1 = result.Parts.Row("C1")!;
        Assert.Equal((PartKind.C, PartConnection.Series, PartEvidenceSource.DeclaredPads, "0402"),
                     (c1.Kind, c1.Connection, c1.Evidence[PartField.Refdes], c1.Case!.Code));
        Assert.True(c1.Terminals[0].X > c1.Terminals[1].X, "terminal 1 is the pad declaring pin 1, the right one");
        var l1 = result.Parts.Row("L1")!;
        Assert.Equal((PartKind.L, PartConnection.Series), (l1.Kind, l1.Connection));
        Assert.True(l1.Terminals[0].X < l1.Terminals[1].X);
        Assert.DoesNotContain(result.Parts.Rows, r => r.Evidence[PartField.Refdes] == PartEvidenceSource.Generated);

        var port = Assert.Single(result.Board!.Ports, p => p.Source == PortSource.Connector);
        Assert.Equal(("In", "J1"), (port.Name, port.Refdes));
        Assert.Equal(3, result.Report.Count(RecognitionFindingClass.PartsFromDeclaredPads));
    }

    [Fact]
    public void StrippedOfItsAttributesTheBoardIsReadFromLandPatterns()
    {
        var result = RecognizeParts(Board(declared: false), null, null);

        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(2, result.Parts.Rows.Count);
        Assert.All(result.Parts.Rows, r => Assert.Equal(PartEvidenceSource.Generated, r.Evidence[PartField.Refdes]));
        Assert.DoesNotContain(result.Board!.Ports, p => p.Source == PortSource.Connector);
        Assert.Equal(0, result.Report.Count(RecognitionFindingClass.PartsFromDeclaredPads));
    }

    [Fact]
    public void DeclaredNetsAreCheckedAgainstTheCopper()
    {
        RecognitionReport Check(string pin, string? net)
        {
            var nets = new Dictionary<string, string>(AgreeingNets);
            if (net is not null) nets[pin] = net;
            return RecognizeParts(Board(declared: true, nets), null, null).Report;
        }

        var agree = Check("", null);
        Assert.Equal((3, 0, 0), (agree.Count(RecognitionFindingClass.DeclaredNetsAgree),
                                 agree.Count(RecognitionFindingClass.DeclaredNetsShorted), agree.Count(RecognitionFindingClass.DeclaredNetsOpen)));

        var shorted = Check("C1,2", "DC");   // on J1's island, which declares IN
        Assert.Equal((0, 1, 0), (shorted.Count(RecognitionFindingClass.DeclaredNetsAgree),
                                 shorted.Count(RecognitionFindingClass.DeclaredNetsShorted), shorted.Count(RecognitionFindingClass.DeclaredNetsOpen)));
        Assert.Contains("DC and IN", shorted.Of(RecognitionFindingClass.DeclaredNetsShorted)!.Sentence);

        var open = Check("L1,2", "IN");      // on the output line, a different island from J1's
        Assert.Equal((0, 0, 1), (open.Count(RecognitionFindingClass.DeclaredNetsAgree),
                                 open.Count(RecognitionFindingClass.DeclaredNetsShorted), open.Count(RecognitionFindingClass.DeclaredNetsOpen)));
        Assert.Contains("IN is on 2 separate pieces", open.Of(RecognitionFindingClass.DeclaredNetsOpen)!.Sentence);
    }
}
