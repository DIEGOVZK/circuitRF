// brief-img-8-schematic-symbols.md R-im8-3 — each convention read with its kind and its orientation, and a diode's pins
// in the order its drawing gives them.

using System;
using System.Linq;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Schematic.Recognition;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class SymbolMatchTests
{
    private static Action<SKCanvas, SKPaint> Drawing(string name) => name switch
    {
        "us-resistor" => SymbolPictures.UsResistor,
        "iec-resistor" => SymbolPictures.IecResistor,
        "coil" => SymbolPictures.Coil,
        "iec-inductor" => SymbolPictures.IecInductor,
        "flat-capacitor" => SymbolPictures.FlatCapacitor,
        "curved-capacitor" => SymbolPictures.CurvedCapacitor,
        "ground" => SymbolPictures.Ground,
        "terminal" => SymbolPictures.Terminal,
        "box-line" => SymbolPictures.BoxLine,
        "diode" => SymbolPictures.Diode,
        "wide-capacitor" => SymbolPictures.WideCapacitor,
        "hairline-curved-capacitor" => SymbolPictures.HairlineCurvedCapacitor,
        "hairline-diode" => SymbolPictures.HairlineDiode,
        "hairline-zigzag" => SymbolPictures.HairlineZigZag,
        "variable-resistor" => SymbolPictures.VariableResistor,
        "heavy-zigzag" => SymbolPictures.HeavyZigZag,
        "hatched-zigzag" => SymbolPictures.HatchedZigZag,
        "looped-coil" => SymbolPictures.LoopedCoil,
        "transformer" => SymbolPictures.Transformer,
        "npn-in-circle" => SymbolPictures.NpnInCircle,
        "pnp-in-circle" => SymbolPictures.PnpInCircle,
        "ac-source" => SymbolPictures.AcSource,
        "dc-source" => SymbolPictures.DcSource,
        "supply" => SymbolPictures.Supply,
        "battery" => SymbolPictures.Battery,
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [InlineData("us-resistor", ImageSymbolKind.Resistor)]
    [InlineData("iec-resistor", ImageSymbolKind.Resistor)]
    [InlineData("coil", ImageSymbolKind.Inductor)]
    [InlineData("iec-inductor", ImageSymbolKind.Inductor)]
    [InlineData("flat-capacitor", ImageSymbolKind.Capacitor)]
    [InlineData("curved-capacitor", ImageSymbolKind.Capacitor)]
    [InlineData("ground", ImageSymbolKind.Ground)]
    [InlineData("terminal", ImageSymbolKind.Port)]
    [InlineData("box-line", ImageSymbolKind.TransmissionLine)]
    [InlineData("diode", ImageSymbolKind.Diode)]
    public void EachConvention_ReadsItsKindAndOrientation_At0And90Degrees(string drawing, ImageSymbolKind kind)
    {
        foreach (var (deg, rotation) in new[] { (0f, SymbolRotation.R0), (90f, SymbolRotation.R90) })
        {
            var r = SymbolPictures.Read(SymbolPictures.Picture(Drawing(drawing), deg));

            var s = Assert.Single(r.Symbols);
            Assert.Equal(SymbolDisposition.Recognised, s.Disposition);
            Assert.Equal(kind, s.Kind);
            Assert.Equal(new SymbolOrientation(rotation, false), s.Orientation);
        }
    }

    [Fact]
    public void AMirroredDiode_ReadsItsPinsInTheMirroredOrder()
    {
        // At 90° the anode is on the right; mirrored left to right, on the left.
        var plain = Assert.Single(SymbolPictures.Read(SymbolPictures.Picture(SymbolPictures.Diode, 90)).Symbols);
        var mirrored = Assert.Single(SymbolPictures.Read(SymbolPictures.Picture(SymbolPictures.Diode, 90, mirror: true)).Symbols);

        Assert.Equal(ImageSymbolKind.Diode, mirrored.Kind);
        Assert.True(plain.Pins[0].X > plain.Pins[1].X);
        Assert.True(mirrored.Pins[0].X < mirrored.Pins[1].X);
        Assert.Equal(plain.Pins[0].Name, mirrored.Pins[0].Name);
    }

    /// <summary>brief-imf-5 §4 — the datasheet and textbook drawings each read as their kind and, where the kind has
    /// several, their variant, at 0° and 90°; the variable resistor is a resistor crossed by its arrow.</summary>
    [Theory]
    [InlineData("wide-capacitor", ImageSymbolKind.Capacitor, null)]
    [InlineData("hairline-curved-capacitor", ImageSymbolKind.Capacitor, null)]
    [InlineData("hairline-diode", ImageSymbolKind.Diode, null)]
    [InlineData("hairline-zigzag", ImageSymbolKind.Resistor, null)]
    [InlineData("variable-resistor", ImageSymbolKind.Resistor, null)]
    [InlineData("heavy-zigzag", ImageSymbolKind.Resistor, null)]
    [InlineData("hatched-zigzag", ImageSymbolKind.Resistor, null)]
    [InlineData("looped-coil", ImageSymbolKind.Inductor, "looped")]
    [InlineData("transformer", ImageSymbolKind.Transformer, null)]
    [InlineData("npn-in-circle", ImageSymbolKind.Transistor, "npn")]
    [InlineData("pnp-in-circle", ImageSymbolKind.Transistor, "pnp")]
    [InlineData("ac-source", ImageSymbolKind.Source, "ac")]
    [InlineData("dc-source", ImageSymbolKind.Source, "dc")]
    [InlineData("supply", ImageSymbolKind.Source, "supply")]
    [InlineData("battery", ImageSymbolKind.Source, "battery")]
    public void EachDatasheetAndTextbookDrawing_ReadsItsKind_At0And90Degrees(string drawing, ImageSymbolKind kind, string? variant)
    {
        foreach (var deg in new[] { 0f, 90f })
        {
            var s = Assert.Single(SymbolPictures.Read(SymbolPictures.Picture(Drawing(drawing), deg)).Symbols);
            Assert.Equal(SymbolDisposition.Recognised, s.Disposition);
            Assert.Equal(kind, s.Kind);
            Assert.Equal(variant, s.Variant);
            Assert.Equal(drawing == "variable-resistor", s.Variable);
        }
    }

    /// <summary>R-imf5-6 — a transistor's pins by role: the base from the side, the collector up, the emitter (the
    /// arrow's) down, at 0°.</summary>
    [Theory]
    [InlineData("npn-in-circle")]
    [InlineData("pnp-in-circle")]
    public void ATransistorsPins_AreItsBaseCollectorAndEmitter(string drawing)
    {
        var s = Assert.Single(SymbolPictures.Read(SymbolPictures.Picture(Drawing(drawing))).Symbols);
        var pin = s.Pins.ToDictionary(p => p.Name);
        Assert.Equal(["c", "b", "e"], s.Pins.Select(p => p.Name));
        Assert.True(pin["b"].X < pin["c"].X && pin["b"].X < pin["e"].X, "the base is not the side lead");
        Assert.True(pin["c"].Y < pin["e"].Y, "the collector is not above the emitter");
    }
}
