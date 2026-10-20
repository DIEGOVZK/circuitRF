using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Designer round 22 (2026-10-20): rotating or dragging parts on a schematic made by Create Schematic
/// from Artwork came off their nets, and a net label with a space in it failed elaboration.
///
/// <para>The fixtures rebuild the shapes that recognition draws: a shunt part with its ground drawn
/// straight onto its pin (no wire), and a line tapped onto the corner or body of another part's
/// wire. The oracle is the netlist partition, as in <see cref="RotateMirrorConnectivityTests"/>.</para>
/// </summary>
[Trait("Suite", "SchematicConnectivity")]
public class DesignerRound22Tests
{
    private static string Connectivity(SchematicEditModel m)
    {
        var byNet = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);
        foreach (var inst in NetExtractor.Extract(m).TestBench.Instances)
            for (int k = 0; k < inst.NetBindings.Count; k++)
            {
                if (!byNet.TryGetValue(inst.NetBindings[k], out var members))
                    byNet[inst.NetBindings[k]] = members = [];
                members.Add($"{inst.InstanceName}.{k}");
            }
        return string.Join("\n", byNet.Values
            .Select(ms => string.Join(" ", ms.OrderBy(s => s, System.StringComparer.Ordinal)))
            .OrderBy(s => s, System.StringComparer.Ordinal));
    }

    private static EditableComponent Comp(SchematicEditModel m, SymbolKind kind, string name, double x, double y,
                                          SymbolRotation rot = SymbolRotation.R0)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = kind, X = x, Y = y, Rotation = rot };
        m.Components.Add(c);
        return c;
    }

    private static void Wire(SchematicEditModel m, params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        m.Wires.Add(w);
    }

    /// <summary>
    /// R1 feeds shunt C1 through one wire, a line taps that wire mid-span, and C1's ground sits on its
    /// pin. Rotating C1 used to re-draw the R1–C1 wire from R1 (dropping the tap) and leave the
    /// ground behind.
    /// </summary>
    private static (SchematicEditModel M, EditableComponent C1, EditableComponent G1) ShuntWithTapAndGround()
    {
        var m  = new SchematicEditModel();
        Comp(m, SymbolKind.Resistor, "R1", 0, 0);                               // pins (0,-200) (0,200)
        var c1 = Comp(m, SymbolKind.Capacitor, "C1", 0, 600);                   // pins (0,400) (0,800)
        var g1 = Comp(m, SymbolKind.Ground, "G1", 0, 800);
        Comp(m, SymbolKind.Resistor, "R2", -800, 300, SymbolRotation.R90);      // pins (-600,300) (-1000,300)
        Wire(m, (0, 200), (0, 400));
        Wire(m, (0, 300), (-600, 300));
        return (m, c1, g1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RotatingAShuntPart_KeepsTheTapOnItsWireAndItsGround(bool clockwise)
    {
        var (m, c1, g1) = ShuntWithTapAndGround();
        var before = Connectivity(m);

        new RotateCommand(m, [c1.Id], clockwise).Execute();

        Assert.Equal(before, Connectivity(m));
        Assert.Equal(SymbolRotation.R0, g1.Rotation);   // carried to the pin, still drawn upright
    }

    [Fact]
    public void RotatingAShuntPart_UndoesCleanly()
    {
        var (m, c1, _) = ShuntWithTapAndGround();
        var wires = m.Wires.Select(w => string.Join(" ", w.Points)).ToList();
        var cmd = new RotateCommand(m, [c1.Id], clockwise: true);

        cmd.Execute();
        cmd.Undo();

        Assert.Equal(wires, m.Wires.Select(w => string.Join(" ", w.Points)).ToList());
    }

    /// <summary>
    /// A wire runs along a row, turns down to C1's pin, and another wire starts at that corner.
    /// Dragging C1 up and left puts its pin back on the row; simplifying the doubled-back leg used to
    /// drop the corner, and the wire starting there came off.
    /// </summary>
    [Fact]
    public void DraggingAPartBackOntoItsOwnRow_KeepsTheWireStartingAtTheCorner()
    {
        var m  = new SchematicEditModel();
        Comp(m, SymbolKind.Resistor, "R3", -1200, 0, SymbolRotation.R90);       // pins (-1000,0) (-1400,0)
        var c1 = Comp(m, SymbolKind.Capacitor, "C1", 0, 400);                   // pins (0,200) (0,600)
        Comp(m, SymbolKind.Ground, "G1", 0, 600);
        Comp(m, SymbolKind.Resistor, "R1", 500, 0, SymbolRotation.R90);         // pins (700,0) (300,0)
        Wire(m, (-1000, 0), (0, 0), (0, 200));
        Wire(m, (0, 0), (300, 0));
        var before = Connectivity(m);

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(c1.Id);
        vm.SimulateDragCommit(-400, -200);

        Assert.Equal(before, Connectivity(m));
    }

    /// <summary>
    /// A two-port line with a tapped wire on each side, dragged on a diagonal. Every re-shape of a
    /// moved end either crosses the line's other pin or drops a tap, so the follow used to settle for
    /// the shape that crossed the pin, and the line came out shorted.
    /// </summary>
    [Fact]
    public void DraggingALineDiagonally_DoesNotShortItThroughItsTappedWires()
    {
        var m  = new SchematicEditModel();
        var t1 = Comp(m, SymbolKind.Tline, "T1", 14700, 5600);                  // pins (14500,5600) (14900,5600)
        Comp(m, SymbolKind.Capacitor, "C2", 16700, 5800);                       // pins (16700,5600) (16700,6000)
        Comp(m, SymbolKind.Resistor, "R3", 15100, 6000);                        // pins (15100,5800) (15100,6200)
        Comp(m, SymbolKind.Capacitor, "C1", 11400, 6000);                       // pins (11400,5800) (11400,6200)
        Comp(m, SymbolKind.Resistor, "R4", 13900, 6000);                        // pins (13900,5800) (13900,6200)
        Wire(m, (16700, 5600), (14900, 5600));
        Wire(m, (15100, 5600), (15100, 5800));
        Wire(m, (14500, 5600), (11400, 5600), (11400, 5800));
        Wire(m, (13900, 5600), (13900, 5800));
        Wire(m, (14300, 5600), (14300, 5800));                                  // a tap right beside the pin
        Comp(m, SymbolKind.Resistor, "R5", 14300, 6000);                        // pins (14300,5800) (14300,6200)
        var before = Connectivity(m);

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(t1.Id);
        vm.SimulateDragCommit(-400, -200);

        Assert.Equal(before, Connectivity(m));
    }

    /// <summary>
    /// A ground dragged off a capacitor along the capacitor's own axis: the wire grown back to the
    /// capacitor's pin used to be a bare L laid straight across its other pin.
    /// </summary>
    [Fact]
    public void DraggingAGroundOffAPart_DoesNotShortThePart()
    {
        var m  = new SchematicEditModel();
        Comp(m, SymbolKind.Capacitor, "C1", 1900, -500, SymbolRotation.R270);   // horizontal
        var c1 = m.Components[0];
        var right = m.PortDefsOf(c1).Select(d => m.PortWorldOf(c1, d)).OrderBy(p => p.X).Last();
        var g = Comp(m, SymbolKind.Ground, "G1", right.X, right.Y);
        Comp(m, SymbolKind.Resistor, "R1", 1000, -500, SymbolRotation.R90);
        var left = m.PortDefsOf(c1).Select(d => m.PortWorldOf(c1, d)).OrderBy(p => p.X).First();
        var r1 = m.Components[^1];
        var r1Right = m.PortDefsOf(r1).Select(d => m.PortWorldOf(r1, d)).OrderBy(p => p.X).Last();
        Wire(m, r1Right, left);

        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(g.Id);
        vm.SimulateDragCommit(-400, -200);

        var inst = NetExtractor.Extract(m).TestBench.Instances.Single(i => i.InstanceName == "C1");
        Assert.NotEqual(inst.NetBindings[0], inst.NetBindings[1]);
    }

    /// <summary>Text tool: a click elsewhere (or another tool) places what was typed; only Escape
    /// abandons it. The click used to start a new text and the typed one vanished.</summary>
    [Fact]
    public void TextTool_ClickElsewherePlacesTheTypedText_EscapeAbandonsIt()
    {
        var vm = new SchematicViewModel(new SchematicEditModel());
        vm.SetTextTool();
        vm.OnPointerPressed(0, 0, default);
        vm.OnTextInput("kept");
        vm.OnPointerPressed(1000, 1000, default);
        vm.OnTextInput("dropped");
        vm.OnKeyDown(Avalonia.Input.Key.Escape, default);

        Assert.Equal(["kept"], vm.EditModel.CanvasObjects.OfType<EditableText>().Select(t => t.Text));
    }

    /// <summary>A net label typed with a space ("to pin1") made the .cnl instance line read as one
    /// net too many, and elaboration refused the part. The name is normalised as it is set.</summary>
    [Theory]
    [InlineData("to pin1", "to_pin1")]
    [InlineData("  a  b ", "a_b")]
    [InlineData("x=1;y\"", "x_1_y_")]
    [InlineData("VDD_2", "VDD_2")]
    public void NetLabelName_IsNormalisedToOneCnlToken(string typed, string stored)
    {
        Assert.Equal(stored, new EditableNetLabel { Name = typed }.Name);
    }
}

/// <summary>
/// Designer round 22: Update Layout from Schematic drew an MLIN stated as E at F 57× too long — the generator's
/// resolver hands a "deg" angle back in degrees and the E → L conversion read it as radians. The drawn length must
/// be the length the schematic simulates.
/// </summary>
public sealed class DesignerRound22LayoutLengthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-r22-len-" + Guid.NewGuid().ToString("N")[..8]);

    public DesignerRound22LayoutLengthTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void AnMlinStatedInDegrees_IsDrawnAtTheLengthItSimulates()
    {
        var tech = CircuitRF.Design.Layout.ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        var line = new EditableComponent { InstanceName = "ML3", Symbol = SymbolKind.Mlin };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 2))
            line.Parameters.Add(new EditableParameter
                { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension });
        line.Parameters.RemoveAll(p => p.Name == "L");
        line.Parameters.Single(p => p.Name == "W").Expression = "42";
        line.Parameters.Single(p => p.Name == "W").Unit = "mil";
        line.Parameters.Add(new EditableParameter { Name = "E", Expression = "90", Unit = "deg" });
        line.Parameters.Add(new EditableParameter { Name = "F", Expression = "10", Unit = "GHz" });

        string cellDir = CellFolder.CreateCellFolder(_root, "Stub");
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(line);
        var target = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        var result = CircuitRF.Ui.Layout.SchematicToLayoutGenerator.Run(
            model, target, schematicDir, _root, CellFolder.SubFolderPath(cellDir, ViewType.Layout), tech, "tech", null);
        result.Command!.Execute();

        double drawn = target.PCellSnapshots.Values.Single().Parameters["L"].AsReal();
        var si = new Dictionary<string, double> { ["W"] = 42 * 25.4e-6, ["E"] = Math.PI / 2, ["F"] = 10e9 };
        double simulated = LineEntryConversion.LengthMeters(SymbolKind.Mlin, si, null, null, tech)!.Value;
        Assert.Equal(simulated, drawn, 9);
        Assert.InRange(drawn, 3e-3, 6e-3);   // a quarter wave at 10 GHz on 20 mil RO4350B, about 4.5 mm
    }

    /// <summary>A new F on an MLIN stated as E at F moves E, not the copper: switching back to L gives the length
    /// it had before. With E left as it was, the line silently changed length by the ratio of the two frequencies.</summary>
    [Fact]
    public void ChangingFOnAnElectricalMlin_KeepsItsPhysicalLength()
    {
        var tech = CircuitRF.Design.Layout.ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
        var line = new EditableComponent { InstanceName = "ML2", Symbol = SymbolKind.Mlin };
        line.Parameters.Add(new EditableParameter { Name = "W", Expression = "42", Unit = "mil" });
        line.Parameters.Add(new EditableParameter { Name = "E", Expression = "45", Unit = "deg" });
        line.Parameters.Add(new EditableParameter { Name = "F", Expression = "10", Unit = "GHz" });
        double LengthOf(EditableComponent c)
        {
            var l = LineEntryConversion.Switch(c, tech, 0).Parameters!.Single(p => p.Name == "L");
            return double.Parse(l.Expression, System.Globalization.CultureInfo.InvariantCulture);
        }
        double before = LengthOf(line);

        var retuned = LineEntryConversion.WithFrequency(line, tech, "1", "GHz")!;
        var after = new EditableComponent { InstanceName = "ML2", Symbol = SymbolKind.Mlin };
        after.Parameters.AddRange(retuned);

        Assert.Equal(before, LengthOf(after), 3);
        Assert.InRange(double.Parse(retuned.Single(p => p.Name == "E").Expression, System.Globalization.CultureInfo.InvariantCulture), 4.0, 5.0);
    }
}

/// <summary>Designer round 22: a standalone Match Designer's design lives only in its window, so the workspace
/// archive asks about one that was edited since it opened or was last flattened.</summary>
public sealed class DesignerRound22MatchDesignerTests
{
    [Fact]
    public void AStandaloneDesign_IsUnkeptOnceEdited_AndKeptAgainAfterRevert()
    {
        var d = new CircuitRF.Ui.Matching.MatchDesignerViewModel();
        d.SetStandalone(null);
        Assert.False(d.HasUnkeptDesign);

        d.SetTermination(2, CircuitRF.Core.Matching.Termination.Resistive(30), fromProbe: false);
        Assert.True(d.HasUnkeptDesign);

        d.Revert();
        Assert.False(d.HasUnkeptDesign);
        d.Dispose();
    }
}

/// <summary>Designer round 22: Create Schematic from Artwork follows a selection made while it is open.</summary>
public sealed class DesignerRound22ArtworkScopeTests
{
    [Fact]
    public void ASelectionMadeWhileTheDialogIsOpen_BecomesTheScope_AndClearingItGoesBackToWhole()
    {
        string clay = Path.Combine(Path.GetTempPath(), "crf-r22-scope-" + Guid.NewGuid().ToString("N")[..8] + ".clay");
        var view = new LayoutView { DbuPerMicron = 1000 };
        var input = new CircuitRF.Design.Layout.Recognition.RecognitionInput
            { View = view, Shapes = view.Shapes, Technology = CircuitRF.Design.Layout.ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz"), ClayPath = clay };
        var vm = new CircuitRF.Ui.Recognition.CreateSchematicFromArtworkViewModel(input, [], debounce: TimeSpan.FromHours(1));
        Assert.False(vm.SelectionAvailable);
        Assert.True(vm.ScopeWhole);

        vm.UpdateSelection([[0, 0, 1000, 0, 1000, 1000, 0, 1000]]);
        Assert.True(vm.SelectionAvailable);
        Assert.True(vm.ScopeSelection);

        vm.UpdateSelection([]);
        Assert.False(vm.SelectionAvailable);
        Assert.True(vm.ScopeWhole);
    }
}
