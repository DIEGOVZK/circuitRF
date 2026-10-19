using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// The physical coupled pairs' artwork — MCLIN (edge-coupled microstrip), BCLIN (broadside stripline) and
/// MBCLIN (broadside microstrip): two strips W wide and L long, the gap or offset between them, a pin at
/// every strip end in the coupled-pair order the models read, and the strips on the layers the stackup
/// resolution chooses — the same call the electrical side makes.
/// </summary>
public class CoupledClinPCellTests
{
    private static long Dbu(double metres) => (long)Math.Round(metres * 1e9);
    private static readonly LayerKey Top = new(1, 0), L2 = new(2, 0), L3 = new(3, 0), Bot = new(4, 0);

    private static PCellResult Gen(string id, Dictionary<string, double> p, Technology? tech = null,
                                   Dictionary<string, string>? text = null)
    {
        Assert.True(PCellRegistry.TryGet(id, out var generate));
        var parameters = new Dictionary<string, PCellValue>(PCellParameters.FromReals(p));
        foreach (var (k, v) in text ?? []) parameters[k] = PCellValue.Text(v);
        return generate(parameters, tech, PCellLayerSelection.Default);
    }

    private static StackupLayer Cu(string name, double um, LayerKey key, bool ground = false) => new()
    {
        Kind = StackupKind.Conductor, Name = name, ThicknessDbu = Dbu(um * 1e-6), SigmaSm = 5.8e7,
        DrawingLayers = [key], IsGroundReference = ground,
    };

    private static StackupLayer Diel(string name, double um, double er) => new()
        { Kind = StackupKind.Dielectric, Name = name, ThicknessDbu = Dbu(um * 1e-6), Epsr = er, TanD = 0.002 };

    /// <summary>The reference stack: ground, 73 µm, 25 µm strip, 76 µm, 18 µm strip, 77 µm, ground.</summary>
    internal static Technology StriplineTech()
    {
        var t = new Technology { Name = "Stripline4" };
        t.Layers =
        [
            new LayerDef { Key = Top, Name = "GND1", ZOrder = 0, Color = new Rgba(200, 80, 40, 255) },
            new LayerDef { Key = L2, Name = "S2", ZOrder = 1, Color = new Rgba(40, 200, 40, 255) },
            new LayerDef { Key = L3, Name = "S3", ZOrder = 2, Color = new Rgba(40, 90, 200, 255) },
            new LayerDef { Key = Bot, Name = "GND4", ZOrder = 3, Color = new Rgba(90, 90, 90, 255) },
        ];
        t.Stackup.Layers =
        [
            Cu("GND1", 18, Top, ground: true), Diel("P1", 73, 3.0), Cu("S2", 25, L2), Diel("C1", 76, 3.0),
            Cu("S3", 18, L3), Diel("P2", 77, 3.0), Cu("GND4", 18, Bot, ground: true),
        ];
        return t;
    }

    /// <summary>A three-conductor board: TOP, an inner signal layer, and the ground below it.</summary>
    internal static Technology MicrostripTech()
    {
        var t = new Technology { Name = "Board3" };
        t.Layers =
        [
            new LayerDef { Key = Top, Name = "TOP", ZOrder = 0, Color = new Rgba(200, 80, 40, 255) },
            new LayerDef { Key = L2, Name = "IN2", ZOrder = 1, Color = new Rgba(40, 200, 40, 255) },
            new LayerDef { Key = Bot, Name = "GND", ZOrder = 2, Color = new Rgba(90, 90, 90, 255) },
        ];
        t.Stackup.Layers =
        [
            Cu("TOP", 35, Top), Diel("PP", 100, 4.0), Cu("IN2", 18, L2), Diel("CORE", 200, 4.0), Cu("GND", 35, Bot, ground: true),
        ];
        return t;
    }

    [Fact]
    public void Mclin_IsTwoStripsAGapApart_WithPinsAtEveryEndInPairOrder()
    {
        double w = 300e-6, s = 120e-6, l = 4e-3;
        var r = Gen("MCLIN", new() { ["W"] = w, ["S"] = s, ["L"] = l });
        Assert.Equal(2, r.Shapes.Count);
        var a = Assert.IsType<RectShape>(r.Shapes[0]);
        var b = Assert.IsType<RectShape>(r.Shapes[1]);
        Assert.Equal(Dbu(w), a.Y2 - a.Y1);
        Assert.Equal(Dbu(w), b.Y2 - b.Y1);
        Assert.Equal(Dbu(l), a.X2 - a.X1);
        Assert.Equal(Dbu(s), a.Y1 - b.Y2);                       // the gap, B below A
        Assert.Equal(["1", "2", "3", "4"], r.Pins.Select(p => p.Name));
        Assert.Equal((0L, 0L), (r.Pins[0].X, r.Pins[0].Y));      // R4: pin 1 at the origin
        Assert.Equal((Dbu(l), 0L), (r.Pins[1].X, r.Pins[1].Y));
        Assert.Equal((0L, -Dbu(w + s)), (r.Pins[2].X, r.Pins[2].Y));   // 3 beside 1
        Assert.Equal((Dbu(l), -Dbu(w + s)), (r.Pins[3].X, r.Pins[3].Y));
        Assert.All(r.Pins, p => Assert.Equal(Dbu(w), p.WidthDbu));
        Assert.Equal([180.0, 0.0, 180.0, 0.0], r.Pins.Select(p => p.OutwardDirectionDeg));

        // Every handle measures exactly its parameter where it sits.
        foreach (var h in r.Handles!)
        {
            double expect = h.Parameter switch { "W" => w, "S" => s, "L" => l, _ => double.NaN };
            Assert.Equal(Dbu(expect), h.ProjectedPosition, 0);
        }
        Assert.Equal(["L", "L", "W", "W", "S"], r.Handles!.Select(h => h.Parameter));
    }

    [Fact]
    public void Bclin_DrawsEachStripOnItsOwnResolvedLayer_OffsetByO()
    {
        double w = 400e-6, o = 150e-6, l = 5e-3;
        var r = Gen("BCLIN", new() { ["W"] = w, ["O"] = o, ["L"] = l }, StriplineTech());
        var a = Assert.IsType<RectShape>(r.Shapes[0]);
        var b = Assert.IsType<RectShape>(r.Shapes[1]);
        Assert.Equal(L2, a.Layer);                               // S2 — the first signal layer with S3 beneath it
        Assert.Equal(L3, b.Layer);
        Assert.Equal(Dbu(o), (a.Y1 + a.Y2) / 2 - (b.Y1 + b.Y2) / 2);
        Assert.Equal([L2, L2, L3, L3], r.Pins.Select(p => p.Layer));
        Assert.Equal((0L, -Dbu(o)), (r.Pins[2].X, r.Pins[2].Y));
        Assert.Null(r.Diagnostics);
        var oh = Assert.Single(r.Handles!, h => h.Parameter == "O");
        Assert.Equal(Dbu(o), oh.ProjectedPosition, 0);

        // Named layers win; with no technology the fallback layers are said, not silent.
        var fb = Gen("BCLIN", new() { ["W"] = w, ["O"] = 0, ["L"] = l });
        Assert.Contains(fb.Diagnostics!, d => d.Contains("no technology", StringComparison.Ordinal));
    }

    [Fact]
    public void Mbclin_TakesTheTopLayerAndTheOneBelowIt_OverTheGround()
    {
        var r = Gen("MBCLIN", new() { ["W"] = 300e-6, ["O"] = 0, ["L"] = 2e-3 }, MicrostripTech());
        Assert.Equal(Top, r.Shapes[0].Layer);
        Assert.Equal(L2, r.Shapes[1].Layer);
        Assert.Null(r.Diagnostics);
    }

    [Fact]
    public void BroadsideResolution_ReadsTheCrossSectionOffTheStack_CopperAddingHeight()
    {
        var (pair, failure, _) = SubstrateResolver.ResolveBroadsidePair(StriplineTech(), null, null, stripline: true);
        Assert.Null(failure);
        Assert.Equal("S2", pair!.A.Name);
        Assert.Equal("S3", pair.B.Name);
        Assert.Equal(76e-6, pair.Hs, 12);
        Assert.Equal(73e-6, pair.Ha, 12);
        Assert.Equal(77e-6, pair.Hb, 12);
        Assert.Equal(25e-6, pair.Ta, 12);
        Assert.Equal(18e-6, pair.Tb, 12);
        Assert.Equal(3.0, pair.EpsR, 12);

        var (mp, mf, _) = SubstrateResolver.ResolveBroadsidePair(MicrostripTech(), null, null, stripline: false);
        Assert.Null(mf);
        Assert.Equal(("TOP", "IN2"), (mp!.A.Name, mp.B.Name));
        Assert.Equal(100e-6, mp.Hs, 12);
        Assert.Equal(200e-6, mp.Hb, 12);
        Assert.Equal(200e-6 + 18e-6 + 100e-6, mp.Hd, 12);        // strip A on the surface

        // A board with one ground has no stripline pair, and says which part it is instead.
        var (_, sf, _) = SubstrateResolver.ResolveBroadsidePair(MicrostripTech(), "TOP", null, stripline: true);
        Assert.Contains("MBCLIN", sf!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCoupledPair_IsRegistered_ForLvs_AndTheMicrostripOneForTheGroundPour()
    {
        foreach (var (id, kind) in new[] { ("MCLIN", SymbolKind.MCLine), ("BCLIN", SymbolKind.BCLine), ("MBCLIN", SymbolKind.MBCLine) })
        {
            Assert.True(DeviceTypes.TryGetSymbolKind(id, out var k));
            Assert.Equal(kind, k);
            Assert.Equal(id, ComponentTypeRegistry.EngineReference(kind));
        }
        Assert.True(MicrostripSubstrateInjection.IsMicrostripKind(SymbolKind.MCLine));
        Assert.Contains(CoupledLinePCells.McLinId, GroundPourPlanner.MicrostripGenerators);
        Assert.True(BroadsideSubstrateInjection.IsBroadsideKind(SymbolKind.BCLine));
        Assert.True(BroadsideSubstrateInjection.IsBroadsideKind(SymbolKind.MBCLine));
    }
}
