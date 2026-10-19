// Designer feedback round 21: MLIN states its length electrically (E at F) as TLIN and MKLOPF do; the switch
// between the two forms is a round trip; Swap Line Type takes a TLIN back to the MLIN it came from; and a Smith
// Chart paste turns an MLIN into the TLIN it is on the technology it was copied with, a DC supply into a short,
// and refuses — naming both parts — an arm of two parts in series to ground. One test per claim.

using System.Globalization;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Smith;
using CircuitRF.Ui.Tests.Schematic;

namespace CircuitRF.Ui.Tests;

public sealed class DesignerRound21LineConversionTests
{
    private const double F = 2e9;

    private static EditableComponent Line(SymbolKind kind, string name, double x, double y,
        params (string Name, string Expr, string Unit)[] values)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = kind, X = x, Y = y, Rotation = SymbolRotation.R0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 2))
            c.Parameters.Add(new EditableParameter
                { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension });
        foreach (var (n, expr, unit) in values)
        {
            var row = c.Parameters.FirstOrDefault(p => p.Name == n);
            if (row is null) c.Parameters.Add(row = new EditableParameter { Name = n });
            row.Expression = expr;
            row.Unit = unit;
        }
        return c;
    }

    private static EditableComponent Part(SymbolKind kind, string name, double x, double y, string p = "", string v = "", string unit = "")
    {
        var c = new EditableComponent { InstanceName = name, Symbol = kind, X = x, Y = y, Rotation = SymbolRotation.R0 };
        if (p.Length > 0) c.Parameters.Add(new EditableParameter { Name = p, Expression = v, Unit = unit });
        return c;
    }

    private static EditableComponent Port(string name, int num, double x)
    {
        var c = Part(SymbolKind.TermG, name, x, 200, "Num", num.ToString(CultureInfo.InvariantCulture));
        c.Parameters.Add(new EditableParameter { Name = "Z", Expression = "50", Unit = "Ω" });
        return c;
    }

    private static EditableWire Wire(double x0, double y0, double x1, double y1)
    {
        var w = new EditableWire();
        w.Points.Add((x0, y0));
        w.Points.Add((x1, y1));
        return w;
    }

    private static double Num(EditableComponent c, string name)
        => double.Parse(c.Parameters.Single(p => p.Name == name).Expression, CultureInfo.InvariantCulture);

    /// <summary>
    /// A port, a series MLIN, a shunt inductor to a DC supply with a bypass capacitor on the supply, and a port —
    /// the bias network of a line between two ports. Every pin sits on the R0 pin offsets of its symbol.
    /// </summary>
    private static (List<EditableComponent> C, List<EditableWire> W) BiasedLine()
    {
        var comps = new List<EditableComponent>
        {
            Port("P1", 1, -400),
            Line(SymbolKind.Mlin, "ML1", 0, 0, ("W", "0.4", "mm"), ("L", "10", "mm"), ("SignalLayer", "Top", "")),
            Part(SymbolKind.Inductor, "L1", 400, 200, "L", "39", "nH"),        // pins (400,0)–(400,400)
            Part(SymbolKind.Vdc, "V1", 400, 600, "Vdc", "3.3", "V"),            // pins (400,400)–(400,800)
            Part(SymbolKind.Ground, "G1", 400, 800),
            Part(SymbolKind.Capacitor, "C8", 800, 600, "C", "0.1", "uF"),      // pins (800,400)–(800,800)
            Part(SymbolKind.Ground, "G2", 800, 800),
            Port("P2", 2, 600),
        };
        var wires = new List<EditableWire>
        {
            Wire(-400, 0, -200, 0), Wire(200, 0, 400, 0), Wire(400, 0, 600, 0), Wire(400, 400, 800, 400),
        };
        return (comps, wires);
    }

    [Fact]
    public void AnElectricalMlin_IsTheLengthItSwitchesTo_AndSwitchesBack()
    {
        var tech = PlanarLineInjectionTests.FourLayer();
        var mlin = Line(SymbolKind.Mlin, "ML1", 0, 0, ("W", "0.4", "mm"), ("L", "10", "mm"), ("SignalLayer", "Top", ""));

        var (electrical, _) = LineEntryConversion.Switch(mlin, tech, F);
        Assert.NotNull(electrical);
        Assert.DoesNotContain(electrical!, p => p.Name == "L");
        mlin.Parameters.Clear();
        mlin.Parameters.AddRange(electrical);
        Assert.True(LineEntryConversion.IsElectrical(mlin.Parameters));

        // What a writer arriving with the physical length puts in E is the E the switch wrote.
        Assert.Equal(Num(mlin, "E"), double.Parse(LineEntryConversion.AngleDegreesFor(mlin, tech, 10e-3)!, CultureInfo.InvariantCulture), 1e-5);

        var (physical, _) = LineEntryConversion.Switch(mlin, tech, F);
        Assert.Equal(10.0, double.Parse(physical!.Single(p => p.Name == "L").Expression, CultureInfo.InvariantCulture), 1e-4);
        Assert.DoesNotContain(physical!, p => p.Name is "E" or "F");
    }

    [Fact]
    public void MlinToTlinAndBack_IsTheSameLine()
    {
        var tech = PlanarLineInjectionTests.FourLayer();
        var mlin = Line(SymbolKind.Mlin, "ML1", 0, 0, ("W", "0.4", "mm"), ("L", "10", "mm"), ("SignalLayer", "Top", ""));

        var tlin = LineTypeSwap.Swap(mlin, SymbolKind.Tline, new LineTypeSwapContext(tech, F)).Component!;
        var back = LineTypeSwap.Swap(tlin, SymbolKind.Mlin, new LineTypeSwapContext(tech, F)).Component!;

        Assert.Equal(0.4, Num(back, "W"), 1e-6);
        Assert.Equal(10.0, Num(back, "L"), 1e-4);
    }

    [Fact]
    public void SmithPaste_TurnsTheMlinIntoItsTlin_AndTheSupplyIntoAShort()
    {
        var tech = PlanarLineInjectionTests.FourLayer();
        var (comps, wires) = BiasedLine();

        var r = SmithPasteRecognizer.Recognize(comps, wires, mirrored: false, F, tech);

        Assert.Null(r.Refusal);
        var line = r.Elements!.Single(e => e.Kind == SmithElementKind.Tline);
        var swapped = LineTypeSwap.Swap(comps.Single(c => c.InstanceName == "ML1"), SymbolKind.Tline,
            new LineTypeSwapContext(tech, F, AngleFrequencyHz: F)).Component!;
        Assert.Equal(Num(swapped, "Z"), line.Values.Z0Ohm, 1e-4);
        Assert.Equal(Num(swapped, "E"), line.Values.ElectricalLengthDeg, 1e-4);
        // The coil ends on the shorted supply, so it is a shunt inductor; the bypass capacitor is left out by name.
        Assert.Contains(r.Elements!, e => e.Kind == SmithElementKind.L && e.Placement == SmithPlacement.Shunt);
        Assert.Equal(2, r.Elements!.Count);
        Assert.Contains("V1", r.Note!, StringComparison.Ordinal);
        Assert.Contains("C8", r.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void SmithPaste_RefusesAnArmOfTwoPartsToTheShortedSupply_NamingBoth()
    {
        var (comps, wires) = BiasedLine();
        // The coil reaches the supply through a line: L1 (400,0)–(400,400), TL6 (400,400)–(800,400), V1 below it.
        comps.RemoveAll(c => c.InstanceName is "V1" or "G1" or "C8" or "G2");
        comps.Add(Line(SymbolKind.Tline, "TL6", 600, 400, ("Z", "50", "Ω"), ("E", "17", "deg"), ("F", "1", "GHz")));
        comps.Add(Part(SymbolKind.Vdc, "V1", 800, 600, "Vdc", "3.3", "V"));
        comps.Add(Part(SymbolKind.Ground, "G1", 800, 800));
        wires.RemoveAll(w => w.Points[0] == (400, 400));

        var r = SmithPasteRecognizer.Recognize(comps, wires, mirrored: false, F, PlanarLineInjectionTests.FourLayer());

        Assert.NotNull(r.Refusal);
        Assert.Contains("L1", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("TL6", r.Refusal!, StringComparison.Ordinal);
    }
}
