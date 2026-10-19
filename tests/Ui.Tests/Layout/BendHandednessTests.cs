using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// Update Layout from Schematic places a bend so its copper turns the way its SYMBOL does. The symbol at
/// R0 turns clockwise on the layout's Y-up frame while MBEND/MMITER artwork at a positive Angle turns
/// counter-clockwise, and a rotation cannot change handedness: before the pin alignment could mirror,
/// every default bend (and every mirrored one at a negative Angle) was placed as the mirror image of the
/// drawing — pins in the right places, both pointing the wrong way, the chamfer on the inside of a
/// meander's turn and the next line a strip-width off.
/// </summary>
public sealed class BendHandednessTests : IDisposable
{
    private readonly string _root, _schematicDir, _layoutDir;

    public BendHandednessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-bendhand-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        string cellDir = CellFolder.CreateCellFolder(_root, "Bend");
        _schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        _layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static EditableComponent Bend(SymbolKind kind, double angleDeg, SymbolRotation rot, bool mirror)
    {
        var comp = new EditableComponent { InstanceName = "B1", Symbol = kind, Rotation = rot, MirrorX = mirror };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Name == "Angle" ? angleDeg.ToString(System.Globalization.CultureInfo.InvariantCulture) : dp.Expression,
                Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }

    private SchematicToLayoutGenerator.GenerationResult Forward(SchematicEditModel m, LayoutView l)
    {
        var r = SchematicToLayoutGenerator.Run(m, l, _schematicDir, _root, _layoutDir, null, null, null);
        r.Command?.Execute();
        return r;
    }

    /// <summary>Each port's bearing from the symbol's origin (its corner), in the layout's Y-up degrees.</summary>
    private static double[] SymbolPortBearings(EditableComponent c) =>
        [.. c.ToRenderComponent().Ports.Select(p =>
        {
            var (x, y) = SchematicGeometry.LocalToWorld(p.LocalX, p.LocalY, 0, 0, c.Rotation, c.MirrorX);
            return Math.Atan2(-y, x) * 180 / Math.PI;
        })];

    /// <summary>Each placed pin's outward direction, carried through the instance's own transform.</summary>
    private double[] LayoutPinBearings(LayoutInstance inst)
    {
        var pins = CellPins.Resolve(CellLayoutResolver.Resolve(inst.CellRef, _layoutDir).View!, null);
        return [.. new[] { "1", "2" }.Select(n =>
        {
            var p = pins.Single(q => q.Name == n);
            double a = p.OutwardDeg * Math.PI / 180;
            var at = LayoutInstanceTransform.TransformPoint(p.X, p.Y, inst, 0, 0);
            var ahead = LayoutInstanceTransform.TransformPoint(p.X + (long)Math.Round(1e5 * Math.Cos(a)),
                                                               p.Y + (long)Math.Round(1e5 * Math.Sin(a)), inst, 0, 0);
            return Math.Atan2(ahead.Y - at.Y, ahead.X - at.X) * 180 / Math.PI;
        })];
    }

    private static void AssertSameBearings(double[] expected, double[] actual)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            double d = Math.Abs(LayoutAngle.Normalize(expected[i] - actual[i]));
            Assert.True(Math.Min(d, 360 - d) < 1.0,
                $"pin {i + 1} points {actual[i]:F1}° on the board but {expected[i]:F1}° on the sheet");
        }
    }

    [Theory]
    [InlineData(SymbolKind.MBend, 90.0, SymbolRotation.R0, false)]
    [InlineData(SymbolKind.MBend, -90.0, SymbolRotation.R0, false)]
    [InlineData(SymbolKind.MBend, 90.0, SymbolRotation.R90, true)]
    [InlineData(SymbolKind.MBend, -90.0, SymbolRotation.R270, true)]
    [InlineData(SymbolKind.MMiter, 90.0, SymbolRotation.R0, false)]
    [InlineData(SymbolKind.MMiter, -90.0, SymbolRotation.R0, false)]
    [InlineData(SymbolKind.MMiter, 90.0, SymbolRotation.R180, true)]
    [InlineData(SymbolKind.MMiter, -90.0, SymbolRotation.R90, true)]
    [InlineData(SymbolKind.MCurve, 90.0, SymbolRotation.R0, false)]
    [InlineData(SymbolKind.MCurve, -90.0, SymbolRotation.R270, true)]
    public void ABend_TurnsTheWayItsSymbolDoes_WhateverTheSignOfItsAngle(
        SymbolKind kind, double angleDeg, SymbolRotation rot, bool mirror)
    {
        var bend = Bend(kind, angleDeg, rot, mirror);
        var schematic = new SchematicEditModel { SchematicDirectory = _schematicDir };
        schematic.Components.Add(bend);
        var layout = new LayoutView();
        Forward(schematic, layout);

        AssertSameBearings(SymbolPortBearings(bend), LayoutPinBearings(Assert.Single(layout.Instances)));
    }

    /// <summary>
    /// A straight part has no handedness to get wrong, so the alignment never mirrors it: a line's
    /// symbol and cell agree under a rotation alone, and a flip would only swap which side is "up".
    /// </summary>
    [Fact]
    public void AStraightLine_IsNeverMirroredByTheAlignment()
    {
        var mlin = Bend(SymbolKind.Mlin, 0, SymbolRotation.R90, false);
        var schematic = new SchematicEditModel { SchematicDirectory = _schematicDir };
        schematic.Components.Add(mlin);
        var layout = new LayoutView();
        Forward(schematic, layout);

        Assert.False(Assert.Single(layout.Instances).MirrorX);
    }

    /// <summary>
    /// Changing a placed bend's Angle from +90 to −90 regenerates a cell turning the other way. The
    /// placement turns with it — keeping the copper on the symbol's side — and says so.
    /// </summary>
    [Fact]
    public void ABendWhoseAngleChangesSign_IsRealigned_AndTheRunSaysSo()
    {
        var bend = Bend(SymbolKind.MMiter, 90, SymbolRotation.R0, false);
        var schematic = new SchematicEditModel { SchematicDirectory = _schematicDir };
        schematic.Components.Add(bend);
        var layout = new LayoutView();
        Forward(schematic, layout);

        bend.Parameters.Single(p => p.Name == "Angle").Expression = "-90";
        var second = Forward(schematic, layout);

        AssertSameBearings(SymbolPortBearings(bend), LayoutPinBearings(Assert.Single(layout.Instances)));
        Assert.Contains(second.Lines, l => l.Text.Contains("so its pins still face the way the symbol's do"));

        // …and a third run with nothing changed leaves it alone.
        var third = Forward(schematic, layout);
        Assert.Null(third.Command);
    }
}
