// The pictures brief-img-14-sparameter-plots-to-touchstone.md §4 reads back, drawn by circuitRF's own plotter: the CLI's
// `sparam`, `plot` and `render` verbs run IN PROCESS (CliEntry.Run), so a gate costs a picture's drawing rather than a
// process start. A picture is drawn the first time a test asks for it and kept for the class.
//
// THE NETWORKS: ForeignPlots.Answer's series-LC band-pass (R = 2 Ω, L = 10 nH, C = 0.4 pF between 50 Ω ports),
// simulated by the sparam engine over 1–5 GHz (and 0.2–20 GHz for the log-frequency picture); the committed
// potentially_unstable_amp.s2p for a gain above 0 dB; and a uniform line, written here as a .s1p: Γ = −⅓·e^(∓jπf/1 GHz),
// a 25 Ω load a quarter wave long at 1 GHz, turning clockwise (the physical sense) or counter-clockwise.
//
// MARKERS on a Smith chart are put in the plot document `plot --write-cdd` writes and drawn by `render`: the Data
// Display places each at its stated Γ with its readout box to the right of the chart.
//
// TWO Y AXES (brief-pdg-1-two-y-axes.md §3) are the plotter's own: a trace with axis=right goes on a right-hand axis whose
// title the plotter colours like its trace. A RIGHT-ONLY plot is that picture with everything left of the frame painted
// out, because the plotter always labels its left axis.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Cli;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Imaging.Plots;
using Xunit;

namespace CircuitRF.Ui.Tests.PlotRecognition;

public sealed class PlotPictureSet : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "crf-im14-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly Dictionary<string, string> _made = [];
    private readonly object _gate = new();

    public PlotPictureSet() => Directory.CreateDirectory(Dir);

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
    }

    public const string LcCnl = """
        Port:P1 in 0 Num=1 Z=50 Ohm
        Port:P2 out 0 Num=2 Z=50 Ohm
        R:R1 in a R=2 Ohm
        L:L1 a b L=10 nH
        C:C1 b out C=0.4 pF
        analysis SP1 type=sparam start=1 stop=5 npts=201 Unit=GHz
        """;

    /// <summary>The uniform line's reflection; <paramref name="clockwise"/> turns it the physical way.</summary>
    public static Complex Line(double f, bool clockwise = true) =>
        -1.0 / 3 * Complex.Exp(new Complex(0, (clockwise ? -1 : 1) * Math.PI * f / 1e9));

    /// <summary>The picture called <paramref name="name"/>, drawn on first use.</summary>
    public string Picture(string name)
    {
        lock (_gate)
        {
            if (_made.TryGetValue(name, out var done)) return done;
            string png = Path.Combine(Dir, name + ".png");
            switch (name)
            {
                case "s21db": Plot(Lc(), png, "cube=S,i=2,j=1,y=db20"); break;
                case "s21ph": Plot(Lc(), png, "cube=S,i=2,j=1,y=phase"); break;
                case "s11db": Plot(Lc(), png, "cube=S,i=1,j=1,y=db20"); break;
                case "s11log":
                {
                    string cdd = Path.Combine(Dir, "s11log.cdd");
                    Plot(Lc(wide: true), Path.Combine(Dir, "s11log-linear.png"), "cube=S,i=1,j=1,y=db20",
                         "--x", "0.5:10", "--y", "-40:0", "--write-cdd", cdd);
                    var doc = JsonNode.Parse(File.ReadAllText(cdd))!;
                    doc["Tabs"]![0]!["Plots"]![0]!["Axes"]!["XScale"] = "Log";
                    File.WriteAllText(cdd, doc.ToJsonString());
                    Run("render", cdd, "-o", png);
                    break;
                }
                case "s11smith": Smith(Lc(), png, [1.5e9, 3e9, 4e9], ForeignPlots.Answer.S11); break;
                case "amp21":
                    Plot(FixturePaths.Require("testdata/ndf/potentially_unstable_amp.s2p"), png, "cube=S,i=2,j=1,y=db20");
                    break;
                case "line-cw": Smith(LineS1p(clockwise: true), png, [], f => Line(f)); break;
                case "line-cw-markers": Smith(LineS1p(clockwise: true), png, [0.6e9, 1.4e9], f => Line(f)); break;
                case "line-ccw-markers": Smith(LineS1p(clockwise: false), png, [0.6e9, 1.4e9], f => Line(f, clockwise: false)); break;
                case "two-y":
                    Plot(Lc(), png, "cube=S,i=2,j=1,y=db20", "--trace", "cube=S,i=2,j=1,y=phase,axis=right", "--y2", "-180:180");
                    break;
                case "two-y-db":
                    Plot(Lc(), png, "cube=S,i=2,j=1,y=db20", "--trace", "cube=S,i=1,j=1,y=db20,axis=right");
                    break;
                case "right-only":
                {
                    string drawn = Path.Combine(Dir, "right-only-drawn.png");
                    Plot(Lc(), drawn, "cube=S,i=2,j=1,y=phase,axis=right", "--y2", "-180:180");
                    PaintLeftOfFrame(drawn, png);
                    break;
                }
                default: throw new ArgumentException(name);
            }
            Assert.True(File.Exists(png), png);
            return _made[name] = png;
        }
    }

    /// <summary>
    /// What a pixel column of trace <paramref name="t"/> at <paramref name="px"/> cannot tell apart: the range of
    /// <paramref name="truth"/> over the column and half the pen either side — a stroke <c>w</c> wide spreads a steep
    /// wall over <c>w</c> columns, so a notch's tip is drawn in the same columns as the walls beside it.
    /// </summary>
    public static double Spread(Func<double, double> truth, AxisCalibration x, double px, TraceCurve t)
    {
        double reach = 0.5 + t.Width / 2;
        var v = Enumerable.Range(0, 9).Select(k => truth(x.ValueAt(px - reach + k * reach / 4))).ToList();
        return v.Max() - v.Min();
    }

    /// <summary>The pictures read into one session, as every caller reads them.</summary>
    public static PlotSession Read(PlotReadingOptions? options, params string[] pngs) =>
        PlotReading.Read([.. pngs.Select(Input)], options);

    public static PlotPictureInput Input(string png) => new(ImageSource.FromFile(png).Source!, Path.GetFileName(png));

    // ── drawing ──────────────────────────────────────────────────────────────────────────────────────────────

    private string Lc(bool wide = false)
    {
        string s2p = Path.Combine(Dir, wide ? "lcw.s2p" : "lc.s2p");
        if (File.Exists(s2p)) return s2p;
        string cnl = Path.ChangeExtension(s2p, ".cnl");
        File.WriteAllText(cnl, wide ? LcCnl.Replace("start=1 stop=5 npts=201", "start=0.2 stop=20 npts=401") : LcCnl);
        Run("sparam", cnl, "-o", s2p);
        return s2p;
    }

    private string LineS1p(bool clockwise)
    {
        string s1p = Path.Combine(Dir, clockwise ? "line-cw.s1p" : "line-ccw.s1p");
        var text = new StringBuilder("# GHz S RI R 50\n");
        for (int k = 0; k <= 200; k++)
        {
            double f = 0.5e9 + k * 5e6;
            var g = Line(f, clockwise);
            text.Append(string.Create(CultureInfo.InvariantCulture, $"{f / 1e9:R} {g.Real:R} {g.Imaginary:R}\n"));
        }
        File.WriteAllText(s1p, text.ToString());
        return s1p;
    }

    /// <summary><paramref name="from"/> with everything left of its plot's frame, down to the x axis, painted in the
    /// background: no left y labels and no left title.</summary>
    private static void PaintLeftOfFrame(string from, string to)
    {
        var source = ImageSource.FromFile(from).Source!;
        var frame = PlotReading.FindPlots(source).Single();
        var r = source.Raster;
        byte[] rgba = (byte[])r.Rgba.Clone();
        int bg = r.Rgb(1, 1);
        for (int y = 0; y <= (int)frame.Bottom + 4; y++)
            for (int x = 0; x <= (int)frame.Left - 3; x++)
            {
                int i = 4 * (y * r.Width + x);
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)(bg >> 16), (byte)(bg >> 8), (byte)bg, 255);
            }
        File.WriteAllBytes(to, new RasterImage(r.Width, r.Height, rgba).EncodePng());
    }

    private static void Plot(string source, string png, string trace, params string[] more) =>
        Run(["plot", source, "-o", png, "--trace", trace, .. more]);

    /// <summary>S11 of <paramref name="source"/> on a Smith chart, with a marker at each of <paramref name="markers"/>
    /// placed at <paramref name="gamma"/> there.</summary>
    private void Smith(string source, string png, double[] markers, Func<double, Complex> gamma)
    {
        string cdd = Path.ChangeExtension(png, ".cdd");
        Plot(source, Path.ChangeExtension(png, ".plain.png"), "cube=S,i=1,j=1", "--type", "smith", "--write-cdd", cdd);
        var doc = JsonNode.Parse(File.ReadAllText(cdd))!;
        var list = new JsonArray();
        for (int k = 0; k < markers.Length; k++)
        {
            var g = gamma(markers[k]);
            list.Add(new JsonObject
            {
                ["Name"] = $"m{k + 1}", ["Index"] = k + 1, ["Freq"] = markers[k], ["FreqUnits"] = "GHz",
                ["InfoBoxX"] = 440, ["InfoBoxY"] = 40 + 110 * k, ["PositionStaticX"] = g.Real, ["PositionStaticY"] = g.Imaginary,
            });
        }
        doc["Tabs"]![0]!["Plots"]![0]!["Traces"]![0]!["Markers"] = list;
        File.WriteAllText(cdd, doc.ToJsonString());
        Run("render", cdd, "-o", png, "--size", "1100x612");
    }

    private static void Run(params string[] args)
    {
        JsonRun.Reset();
        Assert.Equal(0, CliEntry.Run(args));
    }
}
