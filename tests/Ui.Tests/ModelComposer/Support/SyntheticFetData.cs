using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;
using CircuitRF.Engine.Optimization;
using Xunit;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// The synthetic ground truth the Model Composer gates measure against (overview §4): devices written as SDD —
/// independent of the composer's code — simulated by circuitRF to produce the "measurements". MC-4 needs two of them:
///
/// <list type="bullet">
/// <item>a LINEAR core, whose intrinsic Y is known in closed form, inside the GaN two-shell network or the classic
/// shell, written by hand as netlists rather than read from the stock schematics the de-embedding uses;</item>
/// <item>the nonlinear device of <see cref="ComposedTestModels"/> inside the GaN network, solved at DC, whose internal
/// node voltages are the intrinsic truth.</item>
/// </list>
///
/// MC-5 adds multi-bias S: at each bias a core written by hand — a linear SDD for an active point, plain R and C for
/// the cold-FET states of [P1] §5.5.2 — inside a network written by hand, with deterministic noise on S.
/// </summary>
public static class SyntheticFetData
{
    // ── The linear core ───────────────────────────────────────────────────────

    public const double Gg = 2e-4, Gm = 0.12, Gds = 6e-3;
    public const double Cgs = 0.35e-12, Cgd = 0.04e-12, Cds = 0.09e-12, Cm = 0.03e-12;

    /// <summary>The core's admittance at <paramref name="f"/>: <c>G + jωC</c>, with <c>C11 = Cgs + Cgd</c>,
    /// <c>C12 = −Cgd</c>, <c>C21 = Cm − Cgd</c>, <c>C22 = Cds + Cgd</c> ([P1] eq. 6.24 read backwards).</summary>
    public static C2 CoreY(double f)
    {
        var jw = new Complex(0, 2 * Math.PI * f);
        return new C2(Gg + jw * (Cgs + Cgd), jw * -Cgd, Gm + jw * (Cm - Cgd), Gds + jw * (Cds + Cgd));
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The core as an SDD cell on (g d s): linear currents and linear, non-reciprocal charges.</summary>
    public static string CoreCell =>
        "define core (g d s)\n" +
        $"SDD:q g s d s I[1,0]={R(Gg)}*_v1 I[2,0]={R(Gm)}*_v1+{R(Gds)}*_v2 " +
        $"I[1,1]={R(Cgs + Cgd)}*_v1-{R(Cgd)}*_v2 I[2,1]={R(Cm - Cgd)}*_v1+{R(Cds + Cgd)}*_v2\n" +
        "end\n";

    /// <summary>The classic single shell with the stock network's starting values, written by hand.</summary>
    public const string ClassicNetwork =
        "define classic (P1 P2 P3 P4 P5 P6)\n" +
        "C:C1 P1 P3 C=20e-15\nC:C2 P2 P3 C=30e-15\nC:C3 P1 P2 C=2e-15\n" +
        "L:L1 P1 n1 L=40e-12\nL:L2 P2 n2 L=45e-12\nL:L3 P3 n3 L=5e-12\n" +
        "R:R1 n1 P4 R=5\nR:R2 n2 P5 R=9\nR:R3 n3 P6 R=6\n" +
        "end\n";

    /// <summary>
    /// S (50 Ω, common source) of the linear core inside <paramref name="networkDefine"/> — a six-pin define block
    /// named <paramref name="cell"/> — or of the bare core when null.
    /// </summary>
    public static NormalisedData CoreS(string? networkDefine, string? cell, double[] freqs) =>
        TwoPortS(networkDefine is null ? "core:XC a b 0\n" : networkDefine + $"{cell}:XN a b 0 gi di si\ncore:XC gi di si\n", freqs);

    /// <summary>A 2-port Touchstone file holding <paramref name="s"/>'s first point, written to round-trip exactly.</summary>
    public static void WriteS2p(string path, NormalisedData s)
    {
        var sb = new StringBuilder("# Hz S RI R 50\n");
        for (int k = 0; k < s.FrequenciesHz!.Length; k++)
        {
            sb.Append(R(s.FrequenciesHz[k]));
            // Touchstone 2-port order: S11 S21 S12 S22.
            foreach (var (i, j) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
            {
                var v = s.SAt(0, k, i, j);
                sb.Append(' ').Append(R(v.Real)).Append(' ').Append(R(v.Imaginary));
            }
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>S of an arbitrary two-port <c>.cnl</c> body between nets <c>a</c> and <c>b</c>, ports to ground.</summary>
    public static NormalisedData TwoPortS(string body, double[] freqs)
    {
        string cnl = CoreCell + body + "Port:P1 a 0 Num=1 Z=50 Ohm\nPort:P2 b 0 Num=2 Z=50 Ohm\n";
        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var s = SParameterEngine.Run(nl, freqs)["S"];
        var flat = new Complex[freqs.Length * 4];
        for (int k = 0; k < freqs.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    flat[k * 4 + i * 2 + j] = (Complex)s[k, i, j];
        return new NormalisedData { Points = [new BiasPoint("p1", 0, 0, 0, 0)], Ports = 2, FrequenciesHz = freqs, S = flat, Z0 = 50 };
    }

    // ── The nonlinear device at DC ────────────────────────────────────────────

    /// <summary>
    /// <see cref="ComposedTestModels"/>' SDD device inside the GaN network, solved at DC at each extrinsic bias: the
    /// terminal currents from probes, and the intrinsic truth from its internal nodes. The intrinsic voltages are put on
    /// the points as a simulated table would carry them.
    /// </summary>
    public static IReadOnlyList<BiasPoint> DcPoints()
    {
        var points = new List<BiasPoint>();
        foreach (double vgs in (double[])[-1.5, -0.6, 0.6])
            foreach (double vds in (double[])[1, 5, 10])
            {
                string cnl = ComposedTestModels.SddGlobals + ComposedTestModels.ReferenceCell("ref") +
                             $"Vdc:VG vg 0 V={R(vgs)}\nIProbe:IPG vg g\nref:X1 g d 0\nIProbe:IPD vd d\nVdc:VD vd 0 V={R(vds)}\n";
                var (lib, tb) = new CnlReader().Read(cnl);
                using var nl = new Elaborator(lib).Elaborate(tb);
                var dc = NonlinearDcEngine.Run(nl);
                Assert.True(dc.Converged);
                var (rows, names) = nl.Nodes.ResultRows(excludeInternal: false);
                var v = new Dictionary<string, double>(StringComparer.Ordinal);
                for (int k = 0; k < rows.Length; k++) v[names[k]] = dc.NodeVoltages[rows[k] - 1];
                points.Add(new BiasPoint($"Vgs{R(vgs)}_Vds{R(vds)}", vgs, vds, dc.ProbeCurrents["IPG"], dc.ProbeCurrents["IPD"])
                {
                    VgsIntrinsic = v["X1.gi"] - v["X1.si"],
                    VdsIntrinsic = v["X1.di"] - v["X1.si"],
                });
            }
        return points;
    }

    /// <summary>A stock network copied into a fresh folder, loaded.</summary>
    public static SixPinNetwork Stock(string id) =>
        SixPinNetwork.Load(StockNetworks.CopyInto(Directory.CreateTempSubdirectory("crf-mc4-net-").FullName, id));

    // ── MC-5: multi-bias S, per-bias cores ─────────────────────────────────────

    /// <summary>A linear active core as an SDD cell <c>dut (g d s)</c>, the shape of <see cref="CoreCell"/>.</summary>
    public static string ActiveCore(double gg, double gm, double gds, double cgs, double cgd, double cds, double cm) =>
        "define dut (g d s)\n" +
        $"SDD:q g s d s I[1,0]={R(gg)}*_v1 I[2,0]={R(gm)}*_v1+{R(gds)}*_v2 " +
        $"I[1,1]={R(cgs + cgd)}*_v1-{R(cgd)}*_v2 I[2,1]={R(cm - cgd)}*_v1+{R(cds + cgd)}*_v2\n" +
        "end\n";

    /// <summary>The pinch-off cold core of [P1] §5.5.2.2: junction capacitances only, intrinsic C_DS zero.</summary>
    public static string PinchOffCore(double cgs, double cgd) =>
        $"define dut (g d s)\nC:CGS g s C={R(cgs)}\nC:CGD g d C={R(cgd)}\nend\n";

    /// <summary>The strongly forward-biased core of [P1] §5.5.2.4.1: three small resistances to one node.</summary>
    public static string ExtremeForwardCore(double r) =>
        $"define dut (g d s)\nR:RG g x R={R(r)}\nR:RD d x R={R(r)}\nR:RS s x R={R(r)}\nend\n";

    /// <summary>The moderately forward-biased core of [P1] Fig. 5.17: R1 ∥ C1, −Rch/6 at the gate, Rch/2 to drain and source.</summary>
    public static string ModerateForwardCore(double r1, double c1, double rch) =>
        $"define dut (g d s)\nR:R1 g a R={R(r1)}\nC:C1 g a C={R(c1)}\nR:RN a x R={R(-rch / 6)}\n" +
        $"R:RD d x R={R(rch / 2)}\nR:RS s x R={R(rch / 2)}\nend\n";

    /// <summary>
    /// One data set of S (50 Ω, common source): at each bias, its <c>dut</c> core inside <paramref name="networkDefine"/>
    /// (a six-pin define block named <paramref name="cell"/>), with Gaussian noise of standard deviation
    /// <paramref name="noise"/> on each real and imaginary part, seeded so the data is the same every run.
    /// </summary>
    public static NormalisedData Biased(string networkDefine, string cell, double[] freqs, double noise,
                                        params (BiasPoint Bias, string Core)[] points)
    {
        var flat = new Complex[points.Length * freqs.Length * 4];
        var rng = new SplitMix64(20261019);
        for (int p = 0; p < points.Length; p++)
        {
            string cnl = networkDefine + points[p].Core + $"{cell}:XN a b 0 gi di si\ndut:XC gi di si\n" +
                         "Port:P1 a 0 Num=1 Z=50 Ohm\nPort:P2 b 0 Num=2 Z=50 Ohm\n";
            var (lib, tb) = new CnlReader().Read(cnl);
            using var nl = new Elaborator(lib).Elaborate(tb);
            var s = SParameterEngine.Run(nl, freqs)["S"];
            for (int k = 0; k < freqs.Length; k++)
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++)
                        flat[((p * freqs.Length + k) * 2 + i) * 2 + j] =
                            (Complex)s[k, i, j] + new Complex(noise * rng.NextGaussian(), noise * rng.NextGaussian());
        }
        return new NormalisedData { Points = [.. points.Select(x => x.Bias)], Ports = 2, FrequenciesHz = freqs, S = flat, Z0 = 50 };
    }

    /// <summary>The cold points every MC-5 gate shares: pinched off at Vgs = −4 V, and the gate forward-biased.</summary>
    public static (BiasPoint, string) PinchOffPoint => (new BiasPoint("pinch", -4, 0, -1e-9, 0), PinchOffCore(0.12e-12, 0.10e-12));

    public static (BiasPoint, string) ForwardPoint(ForwardVariant variant) =>
        (new BiasPoint("forward", 1.5, 0, 0.02, -0.01),
         variant == ForwardVariant.Extreme ? ExtremeForwardCore(0.02) : ModerateForwardCore(3, 0.5e-12, 1.5));

    /// <summary>Four active points — off, on, the knee and saturation — each its own linear core, with drain currents
    /// shaped so the automatic choice picks all four.</summary>
    public static (BiasPoint, string)[] ActivePoints =>
    [
        (new BiasPoint("off", -3, 10, -1e-9, 0), ActiveCore(0, 0, 2e-4, 0.10e-12, 0.03e-12, 0.08e-12, 0)),
        (new BiasPoint("on", 0, 1, 1e-6, 0.05), ActiveCore(1e-4, 0.06, 0.04, 0.40e-12, 0.12e-12, 0.10e-12, 0.01e-12)),
        (new BiasPoint("knee", 0, 4, 1e-6, 0.095), ActiveCore(1e-4, 0.11, 0.008, 0.37e-12, 0.06e-12, 0.09e-12, 0.025e-12)),
        (new BiasPoint("sat", 0, 10, 1e-6, 0.1), ActiveCore(Gg, Gm, Gds, Cgs, Cgd, Cds, Cm)),
    ];

    /// <summary>A <c>.cmc</c> in a fresh folder with one Device-plane data set <c>bias</c> and stock network
    /// <paramref name="stock"/>; each variable starts at <paramref name="start"/>'s value when it names one, and is
    /// locked at the schematic's (the truth) otherwise. <paramref name="tie"/> ties variables, which are not locked.</summary>
    public static (string Cmc, CmcDocument Doc) Document(string stock, NetworkFitSettings fit,
                                                          IReadOnlyDictionary<string, double> start,
                                                          IReadOnlyDictionary<string, VariableTie>? tie = null)
    {
        string dir = Directory.CreateTempSubdirectory("crf-mc5-").FullName;
        string cmc = Path.Combine(dir, "fet.cmc");
        var doc = StockNetworks.Choose(new CmcDocument { Kind = CmcKind.Active, DataSets = [Set("bias", ReferencePlane.Device)] }, cmc, stock);
        var n = CmcExtrinsic.Network(doc);
        var vars = n.Variables.ToDictionary(
            kv => kv.Key,
            kv => start.TryGetValue(kv.Key, out var v) ? kv.Value with { Value = v }
                : tie?.GetValueOrDefault(kv.Key) is { } t ? kv.Value with { Tie = t }
                : kv.Value with { Lock = true },
            StringComparer.Ordinal);
        return (cmc, CmcExtrinsic.With(doc, n with { Variables = vars, Fit = fit }));
    }

    public static CmcDataSet Set(string name, ReferencePlane plane, BiasPathOhms? path = null) => new()
    {
        Name = name, Kind = DataSetKind.SParams, Sources = ["x.s2p"],
        Conditions = new DataConditions { ReferencePlane = plane, BiasPathOhms = path },
        Import = new ImportSettings { Format = ImportSettings.Touchstone, Pattern = "{vgs}_{vds}", PatternSource = "name" },
    };
}
