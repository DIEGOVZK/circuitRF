using System.Numerics;
using System.Text;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Modeling;
using CircuitRF.Engine;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-19: the threshold-shift coupling. The truth is <see cref="ComposedTestModels"/>' equation device with
/// both <c>CaptureEmission</c> states coupled through <c>ΔV = a_d·(phi2 − vds) + a_g·(vgs − phi1)</c>, simulated by circuitRF
/// itself as the intrinsic core: its DC is the trap-free device's exactly; the document built from its DC I–V and multi-bias S
/// alone — no pulsed data — fits both strengths back; and under drive the states walk the knee out, lowering the power at
/// compression. A block without <c>Coupling</c> is the block MC-10 wrote, byte for byte.
/// </summary>
public sealed class TrapShiftCouplingTests(ITestOutputHelper output)
{
    private const double Ad = 0.05, Ag = 0.1;

    private static ModelTraps Shift(double ad = Ad, double ag = Ag) => new()
    {
        Model = CaptureEmissionTraps.ModelName, Coupling = ModelTraps.Shift,
        States = [CaptureEmissionTraps.GateLag, CaptureEmissionTraps.DrainLag],
        Parameters = [new(CaptureEmissionTraps.ShiftDrain, ad), new(CaptureEmissionTraps.ShiftGate, ag)],
    };

    private static readonly string TruthPath = ComposedTestModels.WriteTemp(ComposedTestModels.Model() with { Traps = Shift() });
    private static readonly string FreePath = ComposedTestModels.WriteTemp(ComposedTestModels.Model());

    private static string Core(string path, string g, string d) => ValidationBench.Device(path, 4, intrinsic: true, g, d);

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    /// <summary>One DC point solved to 1e-14 A: the gate and drain terminal currents.</summary>
    private static (double Ig, double Id) Dc(string path, double vgs, double vds)
    {
        var r = NonlinearDcEngine.Run(Elaborate(ValidationBench.Dc("t", Core(path, "g", "d"), 25, vgs, vds)),
                                      new AnalysisSettings { NonlinearAbsTol = 1e-14 });
        Assert.True(r.Converged, $"DC at ({vgs}, {vds})");
        return (r.ProbeCurrents["IPG"], r.ProbeCurrents["IPD"]);
    }

    [Fact]
    public void Dc_IsTheTrapFreeCurrent_AtEveryPoint()
    {
        double worst = 0;
        foreach (double g in SyntheticTrapData.Vgs)
            foreach (double d in SyntheticTrapData.Vds)
            {
                double a = Dc(TruthPath, g, d).Id, b = Dc(FreePath, g, d).Id;
                worst = Math.Max(worst, Math.Abs(a - b) / Math.Abs(b));
            }
        output.WriteLine($"worst relative DC difference {worst:E2} over {SyntheticTrapData.Vgs.Length * SyntheticTrapData.Vds.Length} points");
        Assert.True(worst <= 1e-9, $"{worst:E2}");
    }

    // ── the strengths, from data circuitRF simulated ─────────────────────────

    private static readonly double[] Freqs = [1e9, 2e9, 3e9, 4e9, 5e9];

    private static ImportSettings Csv => new()
    {
        Format = ImportSettings.Csv,
        Columns = [new("Vgs", "vgs", "V"), new("Vds", "vds", "V"), new("Ig", "ig", "A"), new("Id", "id", "A")],
    };

    private static BiasPoint Point(double vgs, double vds)
    {
        var (ig, id) = Dc(TruthPath, vgs, vds);
        return new BiasPoint($"Vgs{vgs:R}_Vds{vds:R}", vgs, vds, ig, id);
    }

    private static (CmcDataSet, NormalisedData) DcSet() =>
        (new CmcDataSet
        {
            Name = "dc", Kind = DataSetKind.DcIv, Sources = ["dc.csv"], Import = Csv,
            Conditions = new DataConditions { ReferencePlane = ReferencePlane.Intrinsic, AmbientC = 25 },
        },
         new NormalisedData { Points = [.. from g in SyntheticTrapData.Vgs from d in SyntheticTrapData.Vds select Point(g, d)] });

    /// <summary>Multi-bias S of the truth, through ideal tees: the states sit at their DC values far above 1/τ_emit.</summary>
    private static (CmcDataSet, NormalisedData) SSet()
    {
        var points = new List<BiasPoint>();
        var s = new List<Complex>();
        foreach (double g in SyntheticTrapData.Vgs)
            foreach (double d in SyntheticTrapData.Vds)
            {
                points.Add(Point(g, d));
                var cube = SParameterEngine.Run(Elaborate(ValidationBench.SParams("t", Core(TruthPath, "g", "d"), 25, g, d, Freqs, 50)),
                                                Freqs, new AnalysisSettings { NonlinearAbsTol = 1e-14 })["S"];
                for (int k = 0; k < Freqs.Length; k++)
                    s.AddRange([(Complex)cube[k, 0, 0], (Complex)cube[k, 0, 1], (Complex)cube[k, 1, 0], (Complex)cube[k, 1, 1]]);
            }
        return (new CmcDataSet
        {
            Name = "bias", Kind = DataSetKind.SParams, Sources = ["bias.s2p"],
            Import = new ImportSettings { Format = ImportSettings.Touchstone, Pattern = "{vgs}_{vds}" },
            Conditions = new DataConditions { ReferencePlane = ReferencePlane.Intrinsic, AmbientC = 25 },
        }, new NormalisedData { Points = points, Ports = 2, FrequenciesHz = Freqs, S = [.. s], Z0 = 50 });
    }

    private static TrapFitSettings ShiftSettings(IReadOnlyDictionary<string, double>? typed = null) => new()
    {
        Model = CaptureEmissionTraps.ModelName, Coupling = ModelTraps.Shift,
        States = [CaptureEmissionTraps.GateLag, CaptureEmissionTraps.DrainLag],
        Parameters = typed ?? new Dictionary<string, double>(StringComparer.Ordinal),
    };

    private static CurrentFitSettings Fit => new()
    {
        Id = SyntheticFetData.ThermalIdEquation with
        {
            Expression = ComposedTestModels.IdEq,
            Parameters = SyntheticFetData.ThermalIdEquation.Parameters.Where(p => p.Key != "kT").ToDictionary(),
        },
        Ig = SyntheticFetData.ThermalIgEquation,
        Restarts = 1,
    };

    [Fact]
    public void FromDcAndMultiBiasS_TheStrengthsAreRecovered_WithNoPulsedData()
    {
        var dc = DcSet();
        var (doc, data, intrinsic) = SyntheticTrapData.Document(ShiftSettings(), dc, SSet());
        Assert.Null(TrapFit.Unavailable(doc));
        var current = CurrentFit.Run(doc, data, intrinsic, Fit);
        Assert.Null(current.Record.Traps);   // Id₀ is trap-free

        var traps = TrapFit.Run(doc, data, intrinsic, TrapFit.Settings(doc), current: current);
        foreach (string line in traps.Record.Report) output.WriteLine(line);
        var a = traps.Record.Strengths;
        double ed = Math.Abs(a[CaptureEmissionTraps.ShiftDrain].Value - Ad) / Ad, eg = Math.Abs(a[CaptureEmissionTraps.ShiftGate].Value - Ag) / Ag;
        output.WriteLine($"a_d error {100 * ed:F3} %, a_g error {100 * eg:F3} %");
        Assert.True(ed < 0.02, $"a_d {a[CaptureEmissionTraps.ShiftDrain].Value}");
        Assert.True(eg < 0.02, $"a_g {a[CaptureEmissionTraps.ShiftGate].Value}");
        Assert.All(a.Values, s => Assert.NotNull(s.Error));

        // A typed strength is not fitted, and the other's relation does not depend on it.
        var typed = TrapFit.Run(doc, data, intrinsic, ShiftSettings(new Dictionary<string, double> { [CaptureEmissionTraps.ShiftGate] = 0.2 }),
                                current: current).Record.Strengths;
        Assert.Equal(new ShiftStrength(0.2, null), typed[CaptureEmissionTraps.ShiftGate]);
        Assert.Equal(a[CaptureEmissionTraps.ShiftDrain], typed[CaptureEmissionTraps.ShiftDrain]);

        // With no S an untyped strength is refused, naming both ways out.
        var (dcDoc, dcData, dcIntrinsic) = SyntheticTrapData.Document(ShiftSettings(), dc);
        var refused = Assert.Throws<ModelingException>(() => TrapFit.Run(dcDoc, dcData, dcIntrinsic, TrapFit.Settings(dcDoc), current: current));
        Assert.Contains("type it, or add multi-bias S-parameters", refused.Message);
    }

    // ── the knee walkout under drive ─────────────────────────────────────────

    /// <summary>Pout (dBm) at each drive of a class-AB power sweep at 1 GHz into 150 Ω.</summary>
    private static double[] Sweep(string path, double[] pin)
    {
        var c = new LargeSignalConditions { Measurement = LargeSignalMeasurement.PowerSweep, FrequencyHz = 1e9, Vgs = -1.0, Vds = 10, LoadZ = 150 };
        string work = Directory.CreateTempSubdirectory("crf-mc19-").FullName;
        var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(ValidationBench.PowerSweep("t", Core(path, "gx", "dx"), 25, c, pin, 5), work, work),
                                           new CircuitEvaluationRequest { Measurements = false });
        Assert.True(r.Status == RunStatus.Success, r.StatusMessage);
        var hb = r.Results.First(x => x.Data.Contains("V")).Data;
        return [.. ModelValidation.Powers(hb, 1).Pout.Select(p => 10 * Math.Log10(p / 1e-3))];
    }

    /// <summary>The output power where the gain has fallen 3 dB below its small-signal value, interpolated.</summary>
    private static double P3dB(double[] pin, double[] pout)
    {
        double g0 = pout[0] - pin[0];
        for (int k = 1; k < pin.Length; k++)
        {
            double c0 = g0 - (pout[k - 1] - pin[k - 1]), c1 = g0 - (pout[k] - pin[k]);
            if (c1 < 3) continue;
            double w = (3 - c0) / (c1 - c0);
            return pout[k - 1] + w * (pout[k] - pout[k - 1]);
        }
        throw new InvalidOperationException("the sweep does not reach 3 dB compression");
    }

    [Fact]
    public void UnderDrive_TheStatesLowerThePowerAtCompression()
    {
        double[] pin = [.. Enumerable.Range(0, 31).Select(k => -10.0 + k)];
        double[] trapped = Sweep(TruthPath, pin), free = Sweep(FreePath, pin);
        // The small-signal gain differs too — gm·(1 − a_g) and gds + a_d·gm at RF are the DC/RF dispersion itself — so each
        // model's compression is read against its own small-signal gain.
        double pt = P3dB(pin, trapped), pf = P3dB(pin, free);
        output.WriteLine($"small-signal Pout {trapped[0]:F3} (shift) vs {free[0]:F3} dBm; P3dB {pt:F3} (shift) vs {pf:F3} (trap-free) dBm, " +
                         $"{pf - pt:F3} dB lower; at {pin[^1]} dBm in, {trapped[^1]:F3} vs {free[^1]:F3} dBm out");
        Assert.True(pf - pt > 0.5, $"the knee walkout lowers P3dB by {pf - pt:F3} dB");
    }

    // ── the format ───────────────────────────────────────────────────────────

    private static byte[] Bytes(ComposedModel m)
    {
        using var s = new MemoryStream();
        CmodelFile.Write(m, s);
        return s.ToArray();
    }

    [Fact]
    public void Format_NoCouplingRoundTripsByteIdentically_AndEachRefusalNamesItsField()
    {
        var trained = ComposedTestModels.Model() with
        {
            Traps = new ModelTraps { Model = CaptureEmissionTraps.ModelName, States = [CaptureEmissionTraps.DrainLag] },
            Core = ComposedTestModels.Model().Core with
            {
                Id = Constituent.Equation(ComposedTestModels.IdEq.Replace("vgs-Vto", "vgs-0.1*(phi2-vds)-Vto"), ["vgs", "vds", "phi2"], ComposedTestModels.IdParams),
            },
        };
        byte[] once = Bytes(trained);
        Assert.DoesNotContain("Coupling", Encoding.UTF8.GetString(CmodelFile.ModelJson(trained)));
        Assert.Equal(once, Bytes(CmodelFile.Read(new MemoryStream(once))));

        var shifted = ComposedTestModels.Model() with { Traps = Shift() };
        var back = CmodelFile.Read(new MemoryStream(Bytes(shifted)));
        Assert.Equal(ModelTraps.Shift, back.Traps!.Coupling);

        string Refused(ComposedModel m) => Assert.Throws<CmodelFormatException>(() => CmodelFile.Validate(m)).Field;
        Assert.Equal("Core.Constituents.Id.Inputs", Refused(trained with { Traps = trained.Traps! with { Coupling = ModelTraps.Shift, Parameters = [new(CaptureEmissionTraps.ShiftDrain, Ad)] } }));
        Assert.Equal("Traps.Coupling", Refused(shifted with { Traps = new ModelTraps
        {
            Model = CustomTraps.ModelName, Coupling = ModelTraps.Shift, States = ["phi2"],
            Expressions = [new("phi2.Dynamics", "(vds-phi2)/1e-3"), new("phi2.Dc", "vds")],
        } }));
        Assert.Equal($"Traps.Parameters.{CaptureEmissionTraps.ShiftDrain}", Refused(ComposedTestModels.Model() with { Traps = Shift(ad: double.NaN) }));
    }
}
