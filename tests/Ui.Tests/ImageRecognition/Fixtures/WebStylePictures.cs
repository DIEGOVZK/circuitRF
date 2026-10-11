// brief-imf-1-corpus-and-scorer.md R-imf1-2 — the committed web-style pictures: small circuits drawn by our own code the
// way textbooks and web pages draw them (overview §1.2), with their exact answers, and one picture that is no circuit
// at all. Written with the datasheet-style pictures by StylePicturesGenerator, into Fixtures/style-pictures.
//
// The serif faces are a test asset (Fixtures/fonts, overview O4); everything else is a face circuitRF ships.

using System;
using System.Collections.Generic;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.ImageAcceptance;

internal static class WebStylePictures
{
    public static IEnumerable<StylePicture> All()
    {
        yield return new StylePicture("heavy-iec", 1020, 760, 1, HeavyIecCnl, HeavyIec, HeavyIecDraw);
        yield return new StylePicture("textbook-zigzag", 800, 400, 1, LowPassCnl, Textbook, c => TextbookDraw(c, false, 22));
        yield return new StylePicture("ladder-dashed", 960, 360, 1, LadderCnl, Ladder, LadderDraw);
        yield return new StylePicture("typeset", 820, 400, 1, TypesetCnl, Typeset, TypesetDraw);
        yield return new StylePicture("loop-coil-names", 740, 300, 1, BiasTeeCnl, BiasTee, BiasTeeDraw);
        // textbook-zigzag again, 360 px wide: a 1.8 px pen, 7 px caps, its junctions drawn as diamonds.
        yield return new StylePicture("low-res", 800, 400, 360f / 800, LowPassCnl, LowRes, c => TextbookDraw(c, true, 16));
        yield return new StylePicture("illustration", 640, 420, 1, "", Illustration, IllustrationDraw);
    }

    // ── heavy-iec: a common-emitter stage, an 8 px pen ───────────────────────────────────────────────────────────

    private const string HeavyIecCnl = """
        P1Tone:V1 vs 0 Num=1 Z=50 Ohm
        C:C1 vs b C=10 uF
        R:R1 vcc b R=47 kOhm
        R:R2 b 0 R=10 kOhm
        R:R3 vcc c R=4.7 kOhm
        BJT_NPN:Q1 c b e
        R:R4 e 0 R=1 kOhm
        C:C3 e 0 C=100 uF
        C:C2 c out C=10 uF
        R:RL out 0 R=10 kOhm
        Port:P2 out 0 Num=2 Z=50 Ohm
        Vdc:V2 vcc 0 Vdc=12 V
        """;

    private static readonly Dictionary<string, object> HeavyIec = new()
    {
        ["source"] = "web",
        ["kinds"] = new Dictionary<string, int> { ["R"] = 5, ["C"] = 3 },
        ["transistors"] = 1, ["sources"] = 2, ["ports"] = 1, ["grounds"] = 6,
        ["designators"] = new[] { "R1", "R2", "R3", "R4", "RL", "C1", "C2", "C3", "Q1" },
        ["style"] = "w = 8 px, IEC resistor boxes, thick capacitor plates 19 w long, a BJT in an envelope circle, a sine source " +
                    "circle, a + supply circle, an arrowhead into a terminal dot, subscripted designators",
    };

    private static void HeavyIecDraw(SKCanvas c)
    {
        var d = new Drafting(c, 8, SKColors.Black);
        var sans = Drafting.Sans;
        const float cap = 28, thick = 16, gap = 24, plate = 152;
        void Earth(float x, float y) => d.Ground(x, y, 64, 14);

        d.Line(300, 60, 960, 60);
        d.Dot(580, 60, 10);
        d.Box(300, 60, 300, 360, 100, 40);
        d.Subscripted(sans, "R", "1", 220, 225, cap);
        d.Dot(300, 360, 10);
        d.Box(300, 360, 300, 680, 100, 40);
        Earth(300, 680);
        d.Subscripted(sans, "R", "2", 220, 535, cap);

        // The input: a sine source and the coupling capacitor.
        d.Capacitor(100, 360, 300, 360, gap, plate, thick);
        d.Subscripted(sans, "C", "1", 176, 270, cap);
        d.Line(100, 360, 100, 470);
        d.Circle(100, 520, 50);
        using (var sine = new SKPath())
        {
            for (int k = 0; k <= 40; k++)
            {
                float t = k / 40f, x = 70 + 60 * t, y = 520 - 15 * MathF.Sin(2 * MathF.PI * t);
                if (k == 0) sine.MoveTo(x, y); else sine.LineTo(x, y);
            }
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6 };
            c.DrawPath(sine, paint);
        }
        d.Line(100, 570, 100, 680);
        Earth(100, 680);
        d.Subscripted(sans, "v", "s", 160, 530, cap);

        // The transistor in its envelope.
        d.Circle(560, 360, 75);
        d.Line(300, 360, 520, 360);
        d.Line(520, 322, 520, 398, 10);
        d.Line(520, 340, 580, 305);
        d.Line(580, 305, 580, 200);
        d.Arrow(520, 380, 580, 415, 22);
        d.Line(580, 415, 580, 500);
        d.Subscripted(sans, "Q", "1", 650, 330, cap);

        d.Box(580, 60, 580, 200, 80, 40);
        d.Subscripted(sans, "R", "3", 610, 145, cap);
        d.Dot(580, 200, 10);
        d.Dot(580, 500, 10);
        d.Box(580, 500, 580, 680, 90, 40);
        Earth(580, 680);
        d.Subscripted(sans, "R", "4", 610, 610, cap);
        d.Line(580, 500, 740, 500);
        d.Capacitor(740, 500, 740, 680, gap, plate, thick);
        Earth(740, 680);
        d.Subscripted(sans, "C", "3", 830, 600, cap);

        // The output: the coupling capacitor, the load, an arrowhead into the terminal dot.
        d.Capacitor(580, 200, 800, 200, gap, plate, thick);
        d.Subscripted(sans, "C", "2", 662, 112, cap);
        d.Dot(800, 200, 10);
        d.Box(800, 200, 800, 440, 100, 40);
        Earth(800, 440);
        d.Subscripted(sans, "R", "L", 830, 330, cap);
        d.Arrow(800, 200, 886, 200, 26);
        d.Dot(900, 200, 12);
        d.Subscripted(sans, "v", "o", 880, 170, cap);

        // The supply: a + circle from the rail to ground.
        d.Line(960, 60, 960, 205);
        d.Circle(960, 250, 45);
        d.Line(945, 250, 975, 250, 6);
        d.Line(960, 235, 960, 265, 6);
        d.Line(960, 295, 960, 680);
        Earth(960, 680);
        d.Subscripted(sans, "V", "CC", 838, 262, cap);
    }

    // ── textbook-zigzag (and low-res): an RC low-pass between two terminal pairs ─────────────────────────────────

    private const string LowPassCnl = """
        Port:P1 in 0 Num=1 Z=50 Ohm
        R:R1 in out R=1 kOhm
        C:C1 out 0 C=1 uF
        Port:P2 out 0 Num=2 Z=50 Ohm
        """;

    private static readonly Dictionary<string, object> Textbook = new()
    {
        ["source"] = "web",
        ["kinds"] = new Dictionary<string, int> { ["R"] = 1, ["C"] = 1 },
        ["ports"] = 2, ["terminals"] = 4,
        ["designators"] = new[] { "R", "C" },
        ["style"] = "a thick zig-zag, thick plates, terminal-circle pairs joined by a bottom rail, voltage arrows between them, " +
                    "serif italic labels",
    };

    private static readonly Dictionary<string, object> LowRes = new(Textbook)
    {
        ["style"] = "textbook-zigzag at 360 px wide: a 1-2 px pen, 7 px caps, diamond junctions",
    };

    private static void TextbookDraw(SKCanvas c, bool diamonds, float cap)
    {
        var d = new Drafting(c, 4, SKColors.Black);
        var italic = Drafting.SerifItalic;
        void Junction(float x, float y)
        {
            if (diamonds) d.Diamond(x, y, 9); else d.Dot(x, y, 8);
        }

        foreach (var (x, y) in new (float, float)[] { (60, 100), (60, 320), (740, 100), (740, 320) }) d.Circle(x, y, 9);
        d.Line(69, 100, 250, 100);
        d.Resistor(250, 100, 450, 100, 120, 18, 6, 7);
        d.Line(450, 100, 731, 100);
        Junction(560, 100);
        d.Capacitor(560, 100, 560, 320, 24, 90, 7);
        Junction(560, 320);
        d.Line(69, 320, 731, 320);
        d.Arrow(60, 290, 60, 132, 16);
        d.Arrow(740, 290, 740, 132, 16);

        d.Text(italic, "R", 340, 68, cap);
        d.Text(italic, "C", 600, 220, cap);
        d.Subscripted(italic, "v", "in", 76, 222, cap);
        d.Subscripted(italic, "v", "out", 668, 222, cap);
    }

    // ── ladder-dashed: an LC ladder, designators touching their symbols, sections left out ───────────────────────

    private const string LadderCnl = """
        Port:P1 n0 0 Num=1 Z=50 Ohm
        L:L1 n0 n1 L=1 nH
        C:C1 n1 0 C=1 pF
        L:L2 n1 n2 L=1 nH
        C:C2 n2 0 C=1 pF
        L:L3 n2 n3 L=1 nH
        C:C3 n3 0 C=1 pF
        Port:P2 n3 0 Num=2 Z=50 Ohm
        """;

    private static readonly Dictionary<string, object> Ladder = new()
    {
        ["source"] = "web",
        ["kinds"] = new Dictionary<string, int> { ["L"] = 3, ["C"] = 3 },
        ["ports"] = 2, ["terminals"] = 4,
        ["designators"] = new[] { "L1", "L2", "L3", "C1", "C2", "C3" },
        ["style"] = "coils, thick plates, designators touching their symbols, a dashed continuation in both rails, " +
                    "terminal-circle pairs, serif italic designators with subscripts",
    };

    private static void LadderDraw(SKCanvas c)
    {
        var d = new Drafting(c, 4, SKColors.Black);
        var italic = Drafting.SerifItalic;
        const float cap = 22;

        foreach (var (x, y) in new (float, float)[] { (50, 90), (50, 290), (910, 90), (910, 290) }) d.Circle(x, y, 8);
        d.Line(58, 90, 100, 90);
        d.Coil(100, 90, 220, 90, 100, 4);
        d.Line(220, 90, 300, 90);
        d.Coil(300, 90, 420, 90, 100, 4);
        d.Line(420, 90, 480, 90);
        d.Dashed(480, 90, 600, 90, 10, 10);
        d.Line(600, 90, 640, 90);
        d.Coil(640, 90, 760, 90, 100, 4);
        d.Line(760, 90, 902, 90);
        d.Line(58, 290, 480, 290);
        d.Dashed(480, 290, 600, 290, 10, 10);
        d.Line(600, 290, 902, 290);
        foreach (float x in new float[] { 260, 460, 800 })
        {
            d.Dot(x, 90, 7);
            d.Capacitor(x, 90, x, 290, 16, 70, 8);
            d.Dot(x, 290, 7);
        }

        // The words touch what they name: a coil's humps from above, a capacitor's plates from the right.
        float[] coils = [145, 345, 685], caps = [260, 460, 800];
        for (int k = 0; k < 3; k++)
        {
            d.Subscripted(italic, "L", (k + 1).ToString(), coils[k], 76, cap);
            d.Subscripted(italic, "C", (k + 1).ToString(), caps[k] + 34, 200, cap);
        }
    }

    // ── typeset: a book-style figure, designator-then-value runs ─────────────────────────────────────────────────

    private const string TypesetCnl = """
        Vdc:V1 vs 0 Vdc=12 V
        R:R1 vs a R=2.2 kOhm
        R:R2 a 0 R=10 kOhm
        L:L1 a b L=150 nH
        C:C1 b 0 C=100 pF
        R:R3 b 0 R=10 kOhm
        """;

    private static readonly Dictionary<string, object> Typeset = new()
    {
        ["source"] = "web",
        ["kinds"] = new Dictionary<string, int> { ["R"] = 3, ["L"] = 1, ["C"] = 1 },
        ["sources"] = 1, ["grounds"] = 1,
        ["designators"] = new[] { "R1", "R2", "R3", "L1", "C1", "V1" },
        ["values"] = new Dictionary<string, string> { ["R1"] = "2.2k", ["R2"] = "10k", ["R3"] = "10k", ["L1"] = "150n", ["C1"] = "100p" },
        ["style"] = "a thin pen, IEC boxes, a coil, designator-then-value runs in a serif italic face, a ± source circle",
    };

    private static void TypesetDraw(SKCanvas c)
    {
        var d = new Drafting(c, 1.5f, SKColors.Black);
        var italic = Drafting.SerifItalic;
        const float cap = 14;

        d.Circle(80, 220, 28);
        d.Line(74, 205, 86, 205);
        d.Line(80, 199, 80, 211);
        d.Line(74, 236, 86, 236);
        d.Subscripted(italic, "V", "1", 116, 225, cap, "12V");
        d.Line(80, 192, 80, 90);
        d.Box(80, 90, 260, 90, 60, 20);
        d.Subscripted(italic, "R", "1", 130, 72, cap, "2.2kΩ");
        d.Line(260, 90, 300, 90);
        d.Dot(300, 90, 3);
        d.Box(300, 90, 300, 330, 60, 20);
        d.Subscripted(italic, "R", "2", 312, 215, cap, "10kΩ");
        d.Coil(300, 90, 500, 90, 80, 4);
        d.Subscripted(italic, "L", "1", 350, 62, cap, "150nH");
        d.Line(500, 90, 640, 90);
        d.Dot(540, 90, 3);
        d.Capacitor(540, 90, 540, 330, 8, 36);
        d.Subscripted(italic, "C", "1", 448, 215, cap, "100pF");
        d.Box(640, 90, 640, 330, 60, 20);
        d.Subscripted(italic, "R", "3", 652, 215, cap, "10kΩ");

        d.Line(80, 248, 80, 330);
        d.Line(80, 330, 640, 330);
        foreach (float x in new float[] { 300, 400, 540 }) d.Dot(x, 330, 3);
        d.Line(400, 330, 400, 360);
        d.Ground(400, 360, 24, 5);
    }

    // ── loop-coil-names: a bias tee, its ends named from a distance ──────────────────────────────────────────────

    private const string BiasTeeCnl = """
        Port:P1 rfdc 0 Num=1 Z=50 Ohm
        C:C1 rfdc rf C=100 pF
        L:L1 rfdc dc L=100 nH
        Port:P2 rf 0 Num=2 Z=50 Ohm
        Port:P3 dc 0 Num=3 Z=50 Ohm
        """;

    private static readonly Dictionary<string, object> BiasTee = new()
    {
        ["source"] = "web",
        ["kinds"] = new Dictionary<string, int> { ["L"] = 1, ["C"] = 1 },
        ["ports"] = 3,
        ["names"] = new[] { "RF+DC", "DC", "RF" },
        ["style"] = "a looped coil, a flat capacitor, sans names set 4-6 text heights beyond the open wire ends they name",
    };

    private static void BiasTeeDraw(SKCanvas c)
    {
        var d = new Drafting(c, 3, SKColors.Black);
        var sans = Drafting.Sans;
        const float cap = 18;

        d.Line(180, 230, 420, 230);
        d.Dot(340, 230, 6);
        d.Capacitor(420, 230, 500, 230, 14, 60);
        d.Line(500, 230, 600, 230);
        d.Coil(340, 230, 340, 130, 70, 4, looped: true);
        d.TextRight(sans, "RF+DC", 100, 236, cap);
        d.Text(sans, "RF", 680, 236, cap);
        d.TextCentred(sans, "DC", 340, 52, cap);
    }

    // ── illustration: shaded cylinders, no circuit ──────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, object> Illustration = new()
    {
        ["source"] = "web",
        ["refused"] = true,
        ["style"] = "shaded gradients and slanted cylinders: no circuit",
    };

    private static void IllustrationDraw(SKCanvas c)
    {
        using (var shader = SKShader.CreateRadialGradient(new SKPoint(320, 210), 380, [new SKColor(0xee, 0xf2, 0xf8), new SKColor(0x9a, 0xa4, 0xb0)],
                                                          SKShaderTileMode.Clamp))
        using (var ground = new SKPaint { Shader = shader })
            c.DrawRect(0, 0, 640, 420, ground);

        foreach (var (cx, cy, length, r, colour) in new (float, float, float, float, SKColor)[]
                 {
                     (210, 150, 300, 34, new SKColor(0x2a, 0x5d, 0x9f)),
                     (360, 250, 360, 42, new SKColor(0x7a, 0x7f, 0x86)),
                     (470, 330, 240, 26, new SKColor(0xb0, 0x5a, 0x22)),
                 })
        {
            c.Save();
            c.Translate(cx, cy);
            c.RotateDegrees(-25);
            var dark = new SKColor((byte)(colour.Red / 2), (byte)(colour.Green / 2), (byte)(colour.Blue / 2));
            using (var body = SKShader.CreateLinearGradient(new SKPoint(0, -r), new SKPoint(0, r), [dark, colour, SKColors.White, colour, dark],
                                                            [0, 0.3f, 0.45f, 0.6f, 1], SKShaderTileMode.Clamp))
            using (var paint = new SKPaint { Shader = body, IsAntialias = true })
                c.DrawRect(-length / 2, -r, length, 2 * r, paint);
            using (var end = new SKPaint { Color = colour.WithAlpha(255), IsAntialias = true })
                c.DrawOval(new SKRect(length / 2 - r * 0.35f, -r, length / 2 + r * 0.35f, r), end);
            using (var dielectric = new SKPaint { Color = new SKColor(0xf4, 0xf4, 0xf0), IsAntialias = true })
                c.DrawOval(new SKRect(length / 2 - r * 0.22f, -r * 0.62f, length / 2 + r * 0.22f, r * 0.62f), dielectric);
            using (var core = new SKPaint { Color = new SKColor(0xc8, 0x8a, 0x3c), IsAntialias = true })
                c.DrawOval(new SKRect(length / 2 - r * 0.08f, -r * 0.2f, length / 2 + r * 0.08f, r * 0.2f), core);
            c.Restore();
        }
    }
}
