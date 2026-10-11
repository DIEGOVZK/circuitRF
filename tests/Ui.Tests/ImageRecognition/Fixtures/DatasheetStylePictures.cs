// brief-imf-1-corpus-and-scorer.md R-imf1-1 — the committed datasheet-style pictures: small circuits drawn by our own
// code in the conventions application circuits in datasheets use (overview §1.1), each at 1× and 2× — the two
// resolutions a pasted screenshot comes at — with its exact answer beside it. Run once
// (CRF_IMF1_FIXTURES=1 dotnet test --filter StylePicturesGenerator) and the folders under Fixtures/style-pictures are
// rewritten; the gates read the committed bytes, never a fresh drawing (D17), as ForeignPictures' do.
//
// Each folder holds picture.png, answer.cnl (the circuit as drawn: an IC box an SnP on its non-ground pins, an
// amplifier triangle an Amp — overview O5, O6) and expected.json in testdata/image-sources/README.md's schema.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Design.Imaging;
using CircuitRF.Ui.Tests.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

/// <summary>One committed style picture: drawn at <paramref name="Scale"/> into a <paramref name="Width"/> ×
/// <paramref name="Height"/> (1× pixels) sheet, with its answer (empty: no circuit) and the expected.json keys it states.</summary>
internal sealed record StylePicture(string Name, int Width, int Height, float Scale, string Answer,
                                    IReadOnlyDictionary<string, object> Expected, Action<SKCanvas> Draw)
{
    public RasterImage Render() => Pictures.Draw((int)MathF.Ceiling(Width * Scale), (int)MathF.Ceiling(Height * Scale), c =>
    {
        c.Scale(Scale);
        Draw(c);
    });
}

internal static class StylePictures
{
    public const string Folder = "tests/Ui.Tests/ImageRecognition/Fixtures/style-pictures";

    public static IEnumerable<StylePicture> All() => DatasheetStylePictures.All().Concat(WebStylePictures.All());

    /// <summary>Writes every picture's folder under <paramref name="dir"/>.</summary>
    public static void Write(string dir)
    {
        foreach (var p in All())
        {
            string folder = Path.Combine(dir, p.Name);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "picture.png"), Pictures.Png(p.Render()));
            var expected = new Dictionary<string, object> { ["picture"] = "picture.png", ["kind"] = "schematic" };
            if (p.Answer.Length > 0)
            {
                File.WriteAllText(Path.Combine(folder, "answer.cnl"), p.Answer.Replace("\r\n", "\n") + "\n");
                expected["answer"] = "answer.cnl";
            }
            foreach (var (k, v) in p.Expected) expected[k] = v;
            File.WriteAllText(Path.Combine(folder, "expected.json"),
                              JsonSerializer.Serialize(expected, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
    }
}

internal static class DatasheetStylePictures
{
    // A datasheet's hairline pen and small type, at 1×.
    private const float Hairline = 1.1f, Cap = 8, Small = 6;
    private static readonly SKColor Blue = new(0x1f, 0x4e, 0x9c);

    public static IEnumerable<StylePicture> All()
    {
        foreach (float scale in new[] { 1f, 2f })
        {
            string at = scale == 1 ? "1x" : "2x";
            yield return new StylePicture($"ds-rf-amp-{at}", 650, 300, scale, RfAmpCnl, Expected(RfAmp, at), RfAmpDraw);
            yield return new StylePicture($"ds-gain-block-{at}", 560, 230, scale, GainBlockCnl, Expected(GainBlock, at), GainBlockDraw);
            yield return new StylePicture($"ds-regulator-{at}", 740, 320, scale, RegulatorCnl, Expected(Regulator, at), RegulatorDraw);
            yield return new StylePicture($"ds-ldo-{at}", 520, 240, scale, LdoCnl, Expected(Ldo, at), LdoDraw);
            yield return new StylePicture($"ds-test-circuit-{at}", 600, 260, scale, TestCircuitCnl, Expected(TestCircuit, at), TestCircuitDraw);
        }
    }

    private static Dictionary<string, object> Expected(Dictionary<string, object> keys, string at)
    {
        var e = new Dictionary<string, object> { ["source"] = "datasheet" };
        foreach (var (k, v) in keys) e[k] = k == "style" ? $"{v}; {(at == "1x" ? "screen 1x" : "retina 2x")}" : v;
        return e;
    }

    // ── ds-rf-amp: an RF amplifier IC's application circuit ──────────────────────────────────────────────────────

    private const string RfAmpCnl = """
        Port:P1 rfin 0 Num=1 Z=50 Ohm
        C:C1 rfin in C=100 pF
        SnP:U1 in out vcc 0 NumPorts=3
        L:L1 vcc out L=22 nH
        C:C2 out rfout C=100 pF
        Port:P2 rfout 0 Num=2 Z=50 Ohm
        C:C3 vcc 0 C=1 nF
        C:C4 vcc 0 C=10 nF
        """;

    private static readonly Dictionary<string, object> RfAmp = new()
    {
        ["kinds"] = new Dictionary<string, int> { ["C"] = 4, ["L"] = 1 },
        ["ics"] = 1, ["ports"] = 2, ["grounds"] = 4,
        ["names"] = new[] { "VCC" },
        ["designators"] = new[] { "C1", "C2", "C3", "C4", "L1" },
        ["values"] = new Dictionary<string, string> { ["C1"] = "100pF", ["C2"] = "100pF", ["C3"] = "1nF", ["C4"] = "10nF", ["L1"] = "22nH" },
        ["style"] = "hairline pen, 8-pin IC box with pin-number boxes, pin names inside and a gradient fill, hairline plates " +
                    "with a wide gap, a coil, filled-dot RF connectors, a VCC supply arrow, two-line annotation phrases, a hop",
    };

    private static void RfAmpDraw(SKCanvas c)
    {
        var d = new Drafting(c, Hairline, SKColors.Black);
        var sans = Drafting.Sans;

        // The IC: an 8-pin box, pins numbered in small boxes, named inside, filled light grey fading to white.
        var box = new SKRect(250, 95, 350, 225);
        d.GradientRect(box, new SKColor(0xd8, 0xd8, 0xd8), SKColors.White);
        float[] left = [115, 145, 175, 205], right = [205, 175, 145, 115];
        string[] names = ["RFIN", "GND", "GND", "NC", "GND", "GND", "RFOUT", "VCC"];
        for (int k = 0; k < 4; k++)
        {
            d.Rect(new SKRect(236, left[k] - 5, 250, left[k] + 5));
            d.TextCentred(sans, (k + 1).ToString(), 243, left[k] + Small / 2, Small);
            d.Text(sans, names[k], 254, left[k] + Small / 2, Small);
            d.Rect(new SKRect(350, right[k] - 5, 364, right[k] + 5));
            d.TextCentred(sans, (k + 5).ToString(), 357, right[k] + Small / 2, Small);
            d.TextRight(sans, names[k + 4], 346, right[k] + Small / 2, Small);
        }
        d.Text(sans, "U1", 292, 238, Cap);

        // RF in: a filled dot, a series blocking capacitor, pin 1.
        d.Dot(44, 115, 3);
        d.Text(sans, "RF In", 30, 134, Cap);
        d.Line(44, 115, 120, 115);
        d.Capacitor(120, 115, 190, 115, 8, 20);
        d.Line(190, 115, 236, 115);
        d.Text(sans, "C1", 148, 101, Cap);
        d.Text(sans, "100pF", 138, 136, Cap);
        d.Text(sans, "DC Blocking", 125, 150, Cap);
        d.Text(sans, "Capacitor", 130, 161, Cap);

        // Pins 2 and 3 to ground; pin 4 not connected.
        d.Wire(236, 145, 215, 145, 215, 245);
        d.Line(236, 175, 215, 175);
        d.Dot(215, 175, 2.2f);
        d.Ground(215, 245, 18, 4);
        d.Line(236, 205, 228, 205);

        // Pins 5 and 6 to ground.
        d.Wire(364, 175, 385, 175, 385, 245);
        d.Line(364, 205, 385, 205);
        d.Dot(385, 205, 2.2f);
        d.Ground(385, 245, 18, 4);

        // Pin 7, RF out: the bias choke's node, a series blocking capacitor, a filled dot.
        d.Line(364, 145, 460, 145);
        d.Dot(420, 145, 2.2f);
        d.Capacitor(460, 145, 520, 145, 8, 20);
        d.Line(520, 145, 610, 145);
        d.Dot(610, 145, 3);
        d.Text(sans, "RF Out", 592, 164, Cap);
        d.Text(sans, "C2", 484, 131, Cap);
        d.Text(sans, "100pF", 474, 162, Cap);
        d.Text(sans, "DC Blocking", 465, 176, Cap);
        d.Text(sans, "Capacitor", 470, 187, Cap);

        // L1 from the supply rail down to pin 7's node; pin 8's wire hops over its lead on the way to the rail.
        d.Coil(420, 50, 420, 110, 40, 4);
        d.Line(420, 110, 420, 145);
        d.Text(sans, "L1", 428, 75, Cap);
        d.Text(sans, "22nH", 428, 87, Cap);
        d.Hop(364, 470, 115, 420, 6);
        d.Line(470, 115, 470, 50);

        // The rail: VCC's supply arrow and two decoupling capacitors to ground.
        d.Line(420, 50, 560, 50);
        d.Dot(470, 50, 2.2f);
        d.Dot(500, 50, 2.2f);
        d.Arrow(500, 50, 500, 22, 7);
        d.Text(sans, "VCC", 507, 32, Cap);
        d.Dot(530, 50, 2.2f);
        d.Capacitor(530, 50, 530, 100, 6, 18);
        d.Ground(530, 100, 18, 4);
        d.TextRight(sans, "C3", 522, 72, Cap);
        d.TextRight(sans, "1nF", 522, 84, Cap);
        d.Capacitor(560, 50, 560, 100, 6, 18);
        d.Ground(560, 100, 18, 4);
        d.Text(sans, "C4", 570, 72, Cap);
        d.Text(sans, "10nF", 570, 84, Cap);
    }

    // ── ds-gain-block: a gain block's recommended circuit, in blue type ──────────────────────────────────────────

    private const string GainBlockCnl = """
        Port:P1 rfin 0 Num=1 Z=50 Ohm
        C:Cblock1 rfin in C=1 nF
        Amp:U1 in 0 out 0
        C:Cblock2 out rfout C=1 nF
        Port:P2 rfout 0 Num=2 Z=50 Ohm
        L:RFC out bias L=220 nH
        C:Cbypass bias 0 C=100 nF
        R:Rbias bias vcc R=68 Ohm
        """;

    private static readonly Dictionary<string, object> GainBlock = new()
    {
        ["kinds"] = new Dictionary<string, int> { ["C"] = 3, ["L"] = 1, ["R"] = 1 },
        ["amplifiers"] = 1, ["ports"] = 2, ["grounds"] = 4,
        ["names"] = new[] { "Vcc" },
        ["designators"] = new[] { "Cblock", "Cblock", "Rbias", "RFC", "Cbypass" },
        ["values"] = new Dictionary<string, string> { ["Rbias"] = "68Ω", ["RFC"] = "220nH" },
        ["style"] = "blue text, a grey-filled amplifier triangle with pin numbers 3, 1 and 2,4, coax connectors with a centre " +
                    "dot and a ground, open-circle pin markers on the leads, a current arrow beside a wire, word designators, " +
                    "a parenthetical note",
    };

    private static void GainBlockDraw(SKCanvas c)
    {
        var d = new Drafting(c, Hairline, SKColors.Black) { TextInk = Blue };
        var sans = Drafting.Sans;

        void Coax(float x, float y)
        {
            d.Circle(x, y, 8);
            d.Dot(x, y, 2);
            d.Line(x, y + 8, x, y + 30);
            d.Ground(x, y + 30, 18, 4);
        }

        Coax(40, 120);
        d.Text(sans, "RF In", 25, 172, Cap);
        d.Line(48, 120, 95, 120);
        d.Capacitor(95, 120, 135, 120, 8, 20);
        d.Line(135, 120, 197, 120);
        d.Text(sans, "Cblock", 92, 104, Cap);
        d.Text(sans, "1000pF", 91, 142, Cap);

        // The amplifier: a grey triangle, an open-circle marker on each lead, the pin numbers beside them.
        d.Circle(200, 120, 3);
        d.Line(203, 120, 210, 120);
        d.Polygon(new SKColor(0xc9, 0xce, 0xd6), 210, 85, 210, 155, 280, 120);
        d.Text(sans, "3", 196, 112, Cap);
        d.Line(240, 140, 240, 178);
        d.Ground(240, 178, 18, 4);
        d.Text(sans, "2,4", 246, 165, Cap);
        d.Line(280, 120, 289, 120);
        d.Circle(292, 120, 3);
        d.Text(sans, "1", 286, 112, Cap);

        d.Line(295, 120, 400, 120);
        d.Dot(350, 120, 2.2f);
        d.Capacitor(400, 120, 440, 120, 8, 20);
        d.Line(440, 120, 502, 120);
        d.Text(sans, "Cblock", 397, 104, Cap);
        d.Text(sans, "1000pF", 396, 142, Cap);
        Coax(510, 120);
        d.Text(sans, "RF Out", 492, 172, Cap);

        // The bias: Vcc through Rbias and the RF choke, bypassed to ground.
        d.Coil(350, 40, 350, 120, 36, 4);
        d.Text(sans, "RFC", 358, 85, Cap);
        d.Text(sans, "220nH", 358, 97, Cap);
        d.Line(310, 40, 350, 40);
        d.Dot(350, 40, 2.2f);
        d.Resistor(350, 40, 440, 40, 30, 5);
        d.Text(sans, "Rbias", 378, 60, Cap);
        d.Text(sans, "68Ω", 380, 72, Cap);
        d.Circle(444, 40, 4);
        d.Text(sans, "Vcc", 452, 44, Cap);
        d.Arrow(410, 28, 436, 28, 6);
        d.Text(sans, "Icc", 412, 20, Cap);
        d.Capacitor(310, 40, 310, 85, 6, 18);
        d.Ground(310, 85, 18, 4);
        d.Text(sans, "Cbypass", 240, 60, Cap);
        d.Text(sans, "(optional)", 228, 74, Cap);
    }

    // ── ds-regulator: an adjustable regulator's typical application, bold labels ──────────────────────────────────

    private const string RegulatorCnl = """
        Port:P1 vin 0 Num=1 Z=50 Ohm
        C:Ci vin 0 C=0.1 uF
        SnP:U1 vin vout adj NumPorts=3
        Diode:D1 vout vin
        R:R1 vout adj R=240 Ohm
        Diode:D2 adj vout
        R:R2 adj 0 R=5 kOhm
        C:Cadj adj 0 C=10 uF
        C:Co vout 0 C=1 uF
        Port:P2 vout 0 Num=2 Z=50 Ohm
        """;

    private static readonly Dictionary<string, object> Regulator = new()
    {
        ["kinds"] = new Dictionary<string, int> { ["R"] = 2, ["C"] = 3, ["Diode"] = 2 },
        ["ics"] = 1, ["ports"] = 2, ["grounds"] = 4,
        ["names"] = new[] { "VIN", "VOUT" },
        ["designators"] = new[] { "Ci", "Co", "Cadj", "R1", "R2", "D1", "D2" },
        ["values"] = new Dictionary<string, string>
        {
            ["R1"] = "240", ["R2"] = "5k", ["Ci"] = "0.1u", ["Cadj"] = "10u", ["Co"] = "1u", ["D1"] = "1A/100V", ["D2"] = "1A/100V",
        },
        ["style"] = "bold sans labels, curved-plate capacitors, filled diodes, a variable resistor, an IC box with pin names " +
                    "outside, a double-headed dimension arrow labelled Vref = 1.25 V, designator-over-value blocks",
    };

    private static void RegulatorDraw(SKCanvas c)
    {
        var d = new Drafting(c, Hairline, SKColors.Black);
        var bold = Drafting.SansBold;
        const float cap = 9;

        void Block(string designator, string value, float left, float top)
        {
            d.Text(bold, designator, left, top, cap);
            d.Text(bold, value, left, top + 13, cap);
        }

        // In: the input capacitor, the protection diode's return over the top.
        d.Text(bold, "VIN", 14, 109, cap);
        d.Line(50, 105, 250, 105);
        d.Dot(110, 105, 2.2f);
        d.Capacitor(110, 105, 110, 185, 6, 20, curved: true);
        d.Ground(110, 185, 18, 4);
        Block("Ci", "0.1µF", 122, 140);
        d.Dot(160, 105, 2.2f);
        d.Wire(160, 105, 160, 40, 280, 40);
        d.Diode(320, 40, 280, 40, 14);
        d.Wire(320, 40, 440, 40, 440, 105);
        Block("D1", "1A/100V", 282, 18);

        // The regulator: pin names outside the box.
        d.Rect(new SKRect(250, 80, 350, 130));
        d.Text(bold, "U1", 290, 110, cap);
        d.Text(bold, "IN", 230, 98, cap);
        d.Text(bold, "OUT", 356, 98, cap);
        d.Text(bold, "ADJ", 306, 146, cap);

        // Out: R1 to the adjust node with D2 across it, the output capacitor.
        d.Line(350, 105, 690, 105);
        d.Text(bold, "VOUT", 694, 109, cap);
        foreach (float x in new float[] { 440, 480, 560, 640 }) d.Dot(x, 105, 2.2f);
        d.Resistor(480, 105, 480, 210, 36, 5);
        Block("R1", "240Ω", 490, 150);
        d.Diode(560, 210, 560, 105, 14);
        Block("D2", "1A/100V", 570, 150);
        d.Capacitor(640, 105, 640, 185, 6, 20, curved: true);
        d.Ground(640, 185, 18, 4);
        Block("Co", "1µF", 652, 140);

        // The adjust node: the variable R2 and Cadj to ground.
        d.Wire(300, 130, 300, 210, 560, 210);
        foreach (float x in new float[] { 380, 420, 480 }) d.Dot(x, 210, 2.2f);
        d.VariableResistor(380, 210, 380, 295, 36, 5);
        d.Ground(380, 295, 18, 4);
        Block("R2", "5k", 344, 248);
        d.Capacitor(420, 210, 420, 280, 6, 20, curved: true);
        d.Ground(420, 280, 18, 4);
        Block("Cadj", "10µF", 430, 240);

        // Vref across R1: a dimension, not a wire.
        d.Arrow(455, 112, 455, 203, 7, both: true);
        d.TextRight(bold, "Vref = 1.25 V", 449, 160, cap);
    }

    // ── ds-ldo: a low-dropout regulator, pin names inside the box, open-circle terminals ──────────────────────────

    private const string LdoCnl = """
        Port:P1 vin 0 Num=1 Z=50 Ohm
        C:CIN vin 0 C=1 uF
        SnP:U1 vin vin vout NumPorts=3
        C:COUT vout 0 C=1 uF
        Port:P2 vout 0 Num=2 Z=50 Ohm
        """;

    private static readonly Dictionary<string, object> Ldo = new()
    {
        ["kinds"] = new Dictionary<string, int> { ["C"] = 2 },
        ["ics"] = 1, ["ports"] = 2, ["grounds"] = 3, ["terminals"] = 2,
        ["names"] = new[] { "VIN", "VOUT" },
        ["designators"] = new[] { "CIN", "COUT" },
        ["values"] = new Dictionary<string, string> { ["CIN"] = "1u", ["COUT"] = "1u" },
        ["style"] = "hairline IC box with pin names inside, open-circle terminals VIN and VOUT, two capacitors beside the box, " +
                    "a value list beside the output",
    };

    private static void LdoDraw(SKCanvas c)
    {
        var d = new Drafting(c, Hairline, SKColors.Black);
        var sans = Drafting.Sans;

        d.Rect(new SKRect(200, 70, 320, 150));
        d.Text(sans, "VIN", 205, 99, 7);
        d.Text(sans, "EN", 205, 129, 7);
        d.TextRight(sans, "VOUT", 315, 99, 7);
        d.TextCentred(sans, "GND", 260, 145, 7);

        d.Circle(40, 95, 4);
        d.Text(sans, "VIN", 30, 84, Cap);
        d.Line(44, 95, 200, 95);
        d.Dot(120, 95, 2.2f);
        d.Capacitor(120, 95, 120, 170, 6, 20);
        d.Ground(120, 170, 18, 4);
        d.TextRight(sans, "CIN", 104, 136, Cap);
        d.Dot(160, 95, 2.2f);
        d.Wire(200, 125, 160, 125, 160, 95);

        d.Line(260, 150, 260, 190);
        d.Ground(260, 190, 18, 4);

        d.Line(320, 95, 476, 95);
        d.Dot(400, 95, 2.2f);
        d.Capacitor(400, 95, 400, 170, 6, 20);
        d.Ground(400, 170, 18, 4);
        d.Text(sans, "COUT", 416, 136, Cap);
        d.Circle(480, 95, 4);
        d.Text(sans, "VOUT", 466, 84, Cap);
        d.Text(sans, "CIN = 1µF", 430, 186, Cap);
        d.Text(sans, "COUT = 1µF", 430, 199, Cap);
    }

    // ── ds-test-circuit: a characterisation set-up, a dashed fixture box and a solid block box ────────────────────

    private const string TestCircuitCnl = """
        Port:P1 rfin 0 Num=1 Z=50 Ohm
        C:C1 rfin n1 C=10 nF
        L:L1 n1 vdd L=1 uH
        Amp:U1 n1 0 n2 0
        C:C2 n2 rfout C=10 nF
        Port:P2 rfout 0 Num=2 Z=50 Ohm
        """;

    private static readonly Dictionary<string, object> TestCircuit = new()
    {
        ["kinds"] = new Dictionary<string, int> { ["C"] = 2, ["L"] = 1 },
        ["amplifiers"] = 1, ["ports"] = 2, ["grounds"] = 3,
        ["names"] = new[] { "+5V" },
        ["designators"] = new[] { "C1", "C2", "L1" },
        ["values"] = new Dictionary<string, string> { ["C1"] = "10nF", ["C2"] = "10nF", ["L1"] = "1µH" },
        ["style"] = "a dashed box around a fixture, a solid box around the device, coax connectors, an amplifier triangle " +
                    "with pin numbers, a supply arrow",
    };

    private static void TestCircuitDraw(SKCanvas c)
    {
        var d = new Drafting(c, Hairline, SKColors.Black);
        var sans = Drafting.Sans;

        void Coax(float x, float y)
        {
            d.Circle(x, y, 8);
            d.Dot(x, y, 2);
            d.Line(x, y + 8, x, y + 30);
            d.Ground(x, y + 30, 18, 4);
        }

        Coax(40, 150);
        d.Text(sans, "RF In", 25, 200, Cap);
        d.DashedRect(new SKRect(90, 60, 260, 215), 5, 4);
        d.Text(sans, "Test Fixture", 96, 74, Cap);
        d.Line(48, 150, 120, 150);
        d.Capacitor(120, 150, 170, 150, 6, 20);
        d.Text(sans, "C1", 138, 135, Cap);
        d.Text(sans, "10nF", 133, 172, Cap);
        d.Line(170, 150, 320, 150);
        d.Dot(200, 150, 2.2f);
        d.Coil(200, 150, 200, 40, 36, 4);
        d.Text(sans, "L1", 208, 92, Cap);
        d.Text(sans, "1µH", 208, 104, Cap);
        d.Arrow(200, 40, 200, 18, 7);
        d.Text(sans, "+5V", 207, 28, Cap);

        d.Rect(new SKRect(290, 90, 420, 215));
        d.Text(sans, "DUT", 296, 104, Cap);
        d.Polygon(null, 320, 120, 320, 180, 380, 150);
        d.Text(sans, "1", 310, 143, Cap);
        d.Line(350, 165, 350, 195);
        d.Ground(350, 195, 18, 4);
        d.Text(sans, "2", 356, 182, Cap);
        d.Text(sans, "3", 384, 143, Cap);

        d.Line(380, 150, 470, 150);
        d.Capacitor(470, 150, 520, 150, 6, 20);
        d.Text(sans, "C2", 488, 135, Cap);
        d.Text(sans, "10nF", 483, 172, Cap);
        d.Line(520, 150, 562, 150);
        Coax(570, 150);
        d.Text(sans, "RF Out", 550, 200, Cap);
    }
}

/// <summary>Runs only when CRF_IMF1_FIXTURES is set; otherwise skipped, saying how.</summary>
public sealed class StylePicturesGenerator
{
    [RegenerateFact("CRF_IMF1_FIXTURES", "the committed datasheet- and web-style pictures")]
    public void WriteTheCommittedPictures()
    {
        string dir = Path.Combine(FixturePaths.Require("tests/Ui.Tests/ImageRecognition/Fixtures"), "style-pictures");
        StylePictures.Write(dir);
    }
}
