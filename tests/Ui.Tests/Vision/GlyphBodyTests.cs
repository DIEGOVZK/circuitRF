// brief-iml-2 R-iml2-4 / brief-iml-8 R-iml8-1 — GlyphBody.Of is a glyph's drawing without the straight leads ending
// on its pins: what the training labels' body box is and what IML-8 measures a picture's part size with. Each case
// here states the kind's leads itself, from the glyph's definition, renders the glyph through the schematic renderer,
// rubs the leads out of the render, and holds the ink that is left to the body box GlyphBody computed.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Render;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Vision;

public sealed class GlyphBodyTests
{
    /// <summary>Each kind's leads, as (x1, y1, x2, y2) in symbol units, read off BuiltInSymbols.</summary>
    public static TheoryData<SymbolKind, double[]> Kinds => new()
    {
        { SymbolKind.Resistor, [0, -200, 0, -90, 0, 90, 0, 200] },
        { SymbolKind.Inductor, [0, -200, 0, -100, 0, 100, 0, 200] },
        { SymbolKind.Capacitor, [0, -200, 0, -12, 0, 12, 0, 200] },
        { SymbolKind.Ground, [0, 0, 0, 40] },
        { SymbolKind.Term, [0, -200, 0, -110, 0, 120, 0, 200] },
        { SymbolKind.P1Tone, [0, -200, 0, -110, 0, 120, 0, 200] },
        { SymbolKind.BjtNpn, [-200, 0, -80, 0, 0, -110, 0, -200, 0, 110, 0, 200] },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Body_ExcludesTheLeads_AndMatchesTheDrawnGlyph(SymbolKind kind, double[] leads)
    {
        int ports = SymbolPortDefs.For(kind).Length;
        var body = GlyphBody.Of(kind, ports);
        var symbol = BuiltInSymbols.Primitives(kind, ports);

        // No pin is inside the body: what is left out is everything from the pin to the drawing.
        foreach (var pin in symbol.Pins)
            Assert.False(pin.LocalX > body.MinX + 0.5 && pin.LocalX < body.MaxX - 0.5 && pin.LocalY > body.MinY + 0.5 && pin.LocalY < body.MaxY - 0.5,
                         $"{kind}: pin {pin.Name} at ({pin.LocalX}, {pin.LocalY}) is inside the body {body}");

        // The glyph as the renderer draws it, one pixel a unit, its words left out (a polarity mark sits beside it).
        const int size = 600, centre = 300;
        using var bmp = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            var prims = symbol.Primitives.Where(p => p is not TextPrimitive).ToList();
            SchematicRenderer.DrawSymbol(canvas, prims, centre, centre, SymbolRotation.R0, false, 0, 0, 1.0, SchematicRenderTheme.Light);
        }

        // Rub out the leads, a pen's width round each, and bound what is left.
        const double pen = 3.0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (Math.Max(255 - c.Red, Math.Max(255 - c.Green, 255 - c.Blue)) < 96) continue;
                double lx = x + 0.5 - centre, ly = y + 0.5 - centre;
                bool onLead = false;
                for (int k = 0; k < leads.Length; k += 4)
                    onLead |= Distance(lx, ly, leads[k], leads[k + 1], leads[k + 2], leads[k + 3]) <= pen;
                if (onLead) continue;
                minX = Math.Min(minX, lx); maxX = Math.Max(maxX, lx);
                minY = Math.Min(minY, ly); maxY = Math.Max(maxY, ly);
            }

        // The ink is the body grown by half a pen (and anti-aliasing), give or take where a lead was rubbed out.
        const double tol = 4.0;
        Assert.True(Math.Abs(minX - body.MinX) <= tol && Math.Abs(maxX - body.MaxX) <= tol
                    && Math.Abs(minY - body.MinY) <= tol && Math.Abs(maxY - body.MaxY) <= tol,
                    $"{kind}: drawn body ({minX:0.#}, {minY:0.#}, {maxX:0.#}, {maxY:0.#}) against GlyphBody {body}");
        double drawnLong = Math.Max(maxX - minX, maxY - minY), bodyLong = Math.Max(body.Width, body.Height);
        Assert.InRange(drawnLong - bodyLong, -tol, tol);
    }

    private static double Distance(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, len = dx * dx + dy * dy;
        double t = len == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len, 0, 1);
        double x = ax + t * dx - px, y = ay + t * dy - py;
        return Math.Sqrt(x * x + y * y);
    }
}
