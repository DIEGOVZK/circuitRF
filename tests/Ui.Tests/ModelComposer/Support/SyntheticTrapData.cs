using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using CircuitRF.Engine.Modeling;
using RfCore.Data;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// The drain-lag truth the brief-modelcomposer-10 gates measure against, written in closed form at the intrinsic plane —
/// independent of the composer's mapping code: <see cref="ComposedTestModels"/>' device with its threshold shifted by the
/// drain-lag state, <c>vgs → vgs − At·tanh((phi2 − vds)/Ct)</c>, and the same diode gate (which takes no trap input).
///
/// <list type="bullet">
/// <item>DC I–V sits at <c>phi2 = vds</c>, where the shift is zero.</item>
/// <item>Pulsed I–V from a quiescent drain voltage <c>Vq</c> sits at <c>phi2 = Vq</c> for a pulse no higher than it (only
/// emission acts, which is slow), and at <c>phi2 = vds</c> above it — the truth's capture finishes before the sample.</item>
/// <item>CW S-parameters at the DC points: the frozen-state small signal ([X10] §VI) — Gm and Gds the partial derivatives at
/// <c>phi2</c> held — with constant capacitances.</item>
/// </list>
/// </summary>
public static class SyntheticTrapData
{
    public const double At = 0.3, Ct = 5;

    private static readonly Dictionary<string, double> P =
        ComposedTestModels.IdParams.Concat(ComposedTestModels.IgParams).ToDictionary(x => x.Name, x => x.Value);

    /// <summary>The grid: below forward gate conduction, up to 10 V.</summary>
    public static readonly double[] Vgs = [-1.6, -1.2, -0.8, -0.4, 0.0, 0.2, 0.4];
    public static readonly double[] Vds = [0.5, 1, 2, 3, 4, 5, 6, 8, 10];

    /// <summary>The truth's channel current at a trap state.</summary>
    public static double Channel(double vgs, double vds, double phi2)
    {
        double g = vgs - At * Math.Tanh((phi2 - vds) / Ct);
        double f = Math.Log(1 + Math.Exp((g - P["Vto"]) / P["Vs"])) * P["Vs"];
        return P["beta"] * f * f * (1 + P["lambda"] * vds) * Math.Tanh(P["alpha"] * vds);
    }

    private static double IgGs(double vgs) => P["Isg"] * (Math.Exp(vgs / P["Vtg"]) - 1);
    private static double IgGd(double vgs, double vds) => P["Isg"] * (Math.Exp((vgs - vds) / P["Vtg"]) - 1);

    /// <summary>One measured point at a trap state: the gate terminal carries both branches, the drain the channel less the
    /// gate–drain branch.</summary>
    private static BiasPoint Point(double vgs, double vds, double phi2) =>
        new($"Vgs{R(vgs)}_Vds{R(vds)}", vgs, vds, IgGs(vgs) + IgGd(vgs, vds), Channel(vgs, vds, phi2) - IgGd(vgs, vds));

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static ImportSettings Csv => new()
    {
        Format = ImportSettings.Csv,
        Columns = [new("Vgs", "vgs", "V"), new("Vds", "vds", "V"), new("Ig", "ig", "A"), new("Id", "id", "A")],
    };

    /// <summary>DC I–V at the intrinsic plane.</summary>
    public static (CmcDataSet, NormalisedData) Dc(string name = "dc") =>
        (new CmcDataSet
        {
            Name = name, Kind = DataSetKind.DcIv, Sources = [name + ".csv"], Import = Csv,
            Conditions = new DataConditions { ReferencePlane = ReferencePlane.Intrinsic, AmbientC = 25 },
        },
         new NormalisedData { Points = [.. from g in Vgs from d in Vds select Point(g, d, d)] });

    /// <summary>Pulsed I–V from the quiescent bias (−3 V, <paramref name="vq"/>), which dissipates nothing.</summary>
    public static (CmcDataSet, NormalisedData) Pulsed(string name, double vq, TrapStateMode? mode = null, PrePulse? prePulse = null,
                                                      double[]? vgsList = null, double[]? vdsList = null) =>
        (new CmcDataSet
        {
            Name = name, Kind = DataSetKind.PulsedIv, Sources = [name + ".csv"], Import = Csv,
            Conditions = new DataConditions
            {
                ReferencePlane = ReferencePlane.Intrinsic, AmbientC = 25, Quiescent = new QuiescentBias(-3, vq),
                PulseWidth = 1e-6, Period = 1e-3, SampleDelay = 0.8e-6, TrapState = mode, PrePulse = prePulse,
            },
        },
         new NormalisedData
         {
             Points = [.. from g in vgsList ?? Vgs from d in vdsList ?? Vds select Point(g, d, Math.Max(prePulse?.Vds ?? vq, d))],
         });

    public static readonly double[] Freqs = [0.5e9, 1e9, 2e9, 3e9, 5e9];
    private const double Cgs = 0.35e-12, Cgd = 0.04e-12, Cds = 0.09e-12;

    /// <summary>The truth's frozen-state Gm and Gds at a DC point, by central differences at <c>phi2 = vds</c> held.</summary>
    public static (double Gm, double Gds) Frozen(double vgs, double vds)
    {
        const double h = 1e-5;
        return ((Channel(vgs + h, vds, vds) - Channel(vgs - h, vds, vds)) / (2 * h),
                (Channel(vgs, vds + h, vds) - Channel(vgs, vds - h, vds)) / (2 * h));
    }

    /// <summary>CW S-parameters at the DC grid, 50 Ω, from the frozen-state intrinsic admittance.</summary>
    public static (CmcDataSet, NormalisedData) S(string name = "bias")
    {
        var points = new List<BiasPoint>();
        var s = new List<Complex>();
        foreach (double g in Vgs)
            foreach (double d in Vds)
            {
                points.Add(Point(g, d, d));
                var (gm, gds) = Frozen(g, d);
                foreach (double f in Freqs)
                {
                    var jw = new Complex(0, 2 * Math.PI * f);
                    var y = new C2(jw * (Cgs + Cgd), jw * -Cgd, gm + jw * -Cgd, gds + jw * (Cds + Cgd));
                    var sp = NetworkAlgebra.YToS(y, 50);
                    s.AddRange([sp.A, sp.B, sp.C, sp.D]);
                }
            }
        return (new CmcDataSet
        {
            Name = name, Kind = DataSetKind.SParams, Sources = [name + ".s2p"],
            Import = new ImportSettings { Format = ImportSettings.Touchstone, Pattern = "{vgs}_{vds}" },
            Conditions = new DataConditions { ReferencePlane = ReferencePlane.Intrinsic, AmbientC = 25 },
        }, new NormalisedData { Points = points, Ports = 2, FrequenciesHz = Freqs, S = [.. s], Z0 = 50 });
    }

    /// <summary>A document holding these sets, its intrinsic data, and traps as <paramref name="traps"/> sets them.</summary>
    public static (CmcDocument Doc, Dictionary<string, NormalisedData> Data, IntrinsicResult Intrinsic) Document(
        TrapFitSettings? traps, params (CmcDataSet Set, NormalisedData Data)[] sets)
    {
        string dir = Directory.CreateTempSubdirectory("crf-mc10-").FullName;
        var doc = new CmcDocument { Kind = CmcKind.Active, DataSets = [.. sets.Select(s => s.Set)] };
        if (traps is not null) doc = TrapFit.WithSettings(doc, traps);
        var data = new Dictionary<string, NormalisedData>(StringComparer.Ordinal);
        var intrinsic = new Dictionary<string, DataSet>(StringComparer.Ordinal);
        foreach (var (set, d) in sets)
        {
            data[set.Name] = d;
            intrinsic[set.Name] = IntrinsicExtraction.Compute(set, d, PadDeembedding.Load(PadsSettings.None, dir), null, null, null, []);
        }
        return (doc, data, new IntrinsicResult(intrinsic, []));
    }

    /// <summary>The drain-lag CaptureEmission setting at its defaults.</summary>
    public static TrapFitSettings DrainLag(DcIvUse dc = DcIvUse.Target) => new() { Model = "CaptureEmission", DcIv = dc };
}
