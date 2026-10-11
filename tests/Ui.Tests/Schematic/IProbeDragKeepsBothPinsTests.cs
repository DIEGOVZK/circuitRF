using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// An IProbe sitting in series between two wires stays in series when it is dragged along them.
/// The field report: probe pins 100 apart, a wire up into the left pin and a wire away from the right
/// pin along the same row; dragging the probe one step LEFT put its right pin onto the point the left
/// wire's end had held, so that end stayed behind on the right pin — the probe shorted, its left pin
/// open.
/// </summary>
[Trait("Suite", "SchematicConnectivity")]
public class IProbeDragKeepsBothPinsTests
{
    private static EditableComponent Comp(SymbolKind kind, double x, double y, string name)
        => new() { Symbol = kind, X = x, Y = y, InstanceName = name };

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    /// <summary>IP1 np=(0,0) nm=(100,0); a wire down from np to R1's top pin at (0,200); a wire
    /// along the row from nm to R2's top pin at (400,0).</summary>
    private static (SchematicEditModel M, EditableComponent P) Series(bool oneLWire = false)
    {
        var m = new SchematicEditModel();
        var p = Comp(SymbolKind.IProbe, 0, -100, "IP1");
        m.Components.Add(Comp(SymbolKind.Resistor, 0, 400, "R1"));     // pins (0,200)/(0,600)
        m.Components.Add(Comp(SymbolKind.Resistor, 400, 200, "R2"));   // pins (400,0)/(400,400)
        m.Components.Add(p);
        m.Wires.Add(Wire((0, 200), (0, 0)));
        m.Wires.Add(Wire((100, 0), (400, 0)));
        return (m, p);
    }

    private static Dictionary<string, string> Nets(SchematicEditModel m)
    {
        var tb = NetExtractor.Extract(m).TestBench;
        var nets = new Dictionary<string, string>();
        foreach (var inst in tb.Instances)
            for (int i = 0; i < inst.NetBindings.Count; i++)
                nets[$"{inst.InstanceName}.{i}"] = inst.NetBindings[i];
        return nets;
    }

    [Theory]
    [InlineData(-100, 0)]    // the report: the right pin lands where the left wire's end was
    [InlineData(-200, 0)]
    [InlineData(-100, -100)]
    [InlineData(100, 0)]
    public void DraggingTheProbe_KeepsItInSeries(double dx, double dy)
    {
        var (m, p) = Series();
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(p.Id);
        vm.SimulateDragCommit(dx, dy);

        var n = Nets(m);
        string wires = string.Join(" | ", m.Wires.Select(w => string.Join(" ", w.Points.Select(q => $"({q.X},{q.Y})"))));
        Assert.True(n["IP1.0"] == n["R1.0"], $"np left R1's net; wires {wires}");
        Assert.True(n["IP1.1"] == n["R2.0"], $"nm left R2's net; wires {wires}");
        Assert.True(n["IP1.0"] != n["IP1.1"], $"the probe is shorted; wires {wires}");
    }

    [Fact]
    public void DraggingTheProbeWithThePointer_KeepsItInSeries()
    {
        var (m, p) = Series();
        var vm = new SchematicViewModel(m);
        vm.OnPointerPressed(50, -130, Avalonia.Input.KeyModifiers.None, 0, 0);
        for (int x = 40; x >= -50; x -= 10)
            vm.OnPointerMoved(x, -130, true, 0, 0);
        vm.OnPointerReleased(-50, -130);

        var n = Nets(m);
        string wires = string.Join(" | ", m.Wires.Select(w => string.Join(" ", w.Points.Select(q => $"({q.X},{q.Y})"))));
        Assert.True(p.X == -100, $"probe at {p.X}; wires {wires}");
        Assert.True(n["IP1.0"] == n["R1.0"], $"np left R1's net; wires {wires}");
        Assert.True(n["IP1.1"] == n["R2.0"], $"nm left R2's net; wires {wires}");
        Assert.True(n["IP1.0"] != n["IP1.1"], $"the probe is shorted; wires {wires}");
    }

    [Fact]
    public void APinOnAWiresCorner_GrowsAStubWhenDraggedOff()
    {
        // One L-shaped wire up from R1's pin and along to R2's, with IP1's np sitting on the CORNER
        // and nm on the row. A corner is neither an endpoint nor a segment's interior, so the pin
        // that left it used to get neither a follow nor a stub — the screenshot's open left pin.
        var m = new SchematicEditModel();
        var p = Comp(SymbolKind.IProbe, 0, -100, "IP1");
        m.Components.Add(Comp(SymbolKind.Resistor, 0, 400, "R1"));
        m.Components.Add(p);
        m.Wires.Add(Wire((0, 200), (0, 0), (400, 0)));
        var vm = new SchematicViewModel(m);
        vm.Selection.SelectOne(p.Id);
        vm.SimulateDragCommit(-100, 0);

        var n = Nets(m);
        string wires = string.Join(" | ", m.Wires.Select(w => string.Join(" ", w.Points.Select(q => $"({q.X},{q.Y})"))));
        Assert.True(n["IP1.0"] == n["R1.0"], $"np left the wire; wires {wires}");
    }
}
