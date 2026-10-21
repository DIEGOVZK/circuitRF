using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// A loadpull of a placed <c>ComposedFET</c> runs — its terminals sit behind its own extrinsic network, so no nonlinear
/// device touches the tuners' nets, and the loadpull engine reads Pout from the load tuner's branch currents. Gate: at each
/// load, Pout over the drive ladder equals an HB power sweep of the same model into the same load; a loadpull pursuit
/// finds both optima.
/// </summary>
public sealed class ComposedFetLoadpullTests(ITestOutputHelper output)
{
    /// <summary>
    /// Review 4: the shipped example's GaN at 2 GHz, behind the conjugately matched source the report's Class B uses. The
    /// engine's first rung, 40 dB above its tickle, is a leap the Newton does not make, and every load stopped there; the
    /// rung now continues from the tickle and the class load drives up to compression.
    /// </summary>
    [Fact]
    public void AFirstRungTheTickleCannotReach_IsContinuedFromIt_AndDrivesToCompression()
    {
        string model = Path.Combine(ModelCliFixture.RepoRoot(), "examples", "Model Composer", "synthetic-gan", "gan_4x125.cmodel");
        string dir = Directory.CreateTempSubdirectory("crf-lp-tickle-").FullName;
        File.WriteAllLines(Path.Combine(dir, "lp.gam"), ["# impedance Z0=50 re_im", "95.652 26.074"]);
        string cnl = "Vgs = -0.99481\nVds = 10\n" + ValidationBench.Device(model, 4, intrinsic: false, "g", "d") +
                     "Tuner:SRC g 0 Z[1]=16.605+j*179.27 Zdefault=1e-6 BiasTee=on Vbias=Vgs\n" +
                     "Tuner:LOAD d 0 Z[1]=50 Z[2]=1e-6-j*1.5675 Z[3]=1e-6-j*2.3465 Z[4]=1e-6-j*3.1201 Z[5]=1e-6-j*3.8871 Zdefault=1e-6 " +
                     "BiasTee=on Vbias=Vds\n" +
                     "analysis LP1 type=loadpull Tone=2e9 MaxHarm=5 LoadTuner=LOAD SourceTuner=SRC Sweep=Load TuneHarm=1 " +
                     "Grid=\"lp.gam\" Compression=3 GainType=Gt PinStart=-10 PinStep=1 PinMax=40 Tol=1e-6 MaxIter=100\n";
        var lp = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, dir, dir), new CircuitEvaluationRequest { Measurements = false });
        Assert.True(lp.Status == RunStatus.Success, lp.StatusMessage);
        var ds = lp.Results.First(r => r.Data.Contains("StopCode")).Data;
        Assert.Equal(1.0, ds["StopCode"].RealValues[0]);   // Compression
        double pout = ds["Pout_dBm"].RealValues.Where(double.IsFinite).Max();
        output.WriteLine($"Pout at compression {pout:F2} dBm");
        Assert.InRange(pout, 22, 24);
    }

    [Fact]
    public void Loadpull_OfAComposedFet_MatchesAnHbPowerSweepAtEachLoad()
    {
        string cmc = ExportFixture.Document();
        string dir = Path.GetDirectoryName(cmc)!;
        string model = Path.Combine(dir, "fet.cmodel");
        CmodelExport.Export(cmc, model, created: "2026-10-19T00:00:00Z");
        string device = ValidationBench.Device(model, 4, intrinsic: false, "g", "d");
        Complex[] loads = [new(40, 10), new(80, -20)];
        double[] pin = [-15, -10, -5];
        string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        File.WriteAllLines(Path.Combine(dir, "lp.gam"),
            ["# impedance Z0=50 re_im", .. loads.Select(z => $"{R(z.Real)} {R(z.Imaginary)}")]);
        string cnl = "Vgs = -0.6\nVds = 6\n" + device +
                     "Tuner:SRC g 0 Z[1]=50 Zdefault=1e-6 BiasTee=on Vbias=Vgs\n" +
                     "Tuner:LOAD d 0 Z[1]=50 Zdefault=1e-6 BiasTee=on Vbias=Vds\n" +
                     "analysis LP1 type=loadpull Tone=2e9 MaxHarm=5 LoadTuner=LOAD SourceTuner=SRC Sweep=Load TuneHarm=1 " +
                     $"Grid=\"lp.gam\" Compression=30 GainType=Gt PinStart={R(pin[0])} PinStep=5 PinMax={R(pin[^1])} Tol=1e-9\n";
        var lp = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, dir, dir), new CircuitEvaluationRequest { Measurements = false });
        Assert.True(lp.Status == RunStatus.Success, lp.StatusMessage);
        var ds = lp.Results.First(r => r.Data.Contains("StopCode")).Data;
        var pout = ds["Pout_W"].RealValues;
        var conv = ds["Converged"].RealValues;
        var pavl = ds["PavlDbm"].RealValues;
        int nP = pout.Length / loads.Length;

        for (int g = 0; g < loads.Length; g++)
        {
            var cond = new LargeSignalConditions
            {
                Measurement = LargeSignalMeasurement.PowerSweep, FrequencyHz = 2e9, Vgs = -0.6, Vds = 6, SourceZ = 50, LoadZ = loads[g],
            };
            var hb = CircuitEvaluation.Evaluate(
                PreparedCircuit.FromText(ValidationBench.PowerSweep("lp", ValidationBench.Device(model, 4, false, "gx", "dx"), 25, cond, pin, 5), dir, dir),
                new CircuitEvaluationRequest { Measurements = false });
            Assert.True(hb.Status == RunStatus.Success, hb.StatusMessage);
            var (want, _, _, _, _) = ModelValidation.Powers(hb.Results.First(r => r.Data.Contains("V")).Data, 1);
            for (int k = 0; k < pin.Length; k++)
            {
                // The ladder opens with the engine's tickle step; each drive is found by its own Pavl.
                int i = Enumerable.Range(g * nP, nP).Single(j => Math.Abs(pavl[j] - pin[k]) < 1e-9);
                Assert.Equal(1.0, conv[i]);
                output.WriteLine($"Z {loads[g]} Pin {pin[k]}: lp {ModelValidation.Dbm(pout[i]):F4} dBm, hb {ModelValidation.Dbm(want[k]):F4} dBm");
                Assert.True(Math.Abs(ModelValidation.Dbm(pout[i]) - ModelValidation.Dbm(want[k])) < 0.01, $"load {g}, Pin {pin[k]}");
            }
        }
    }

    [Fact]
    public void LoadpullPursuit_OfAComposedFet_FindsItsOptima()
    {
        string cmc = ExportFixture.Document();
        string dir = Path.GetDirectoryName(cmc)!;
        string model = Path.Combine(dir, "fet.cmodel");
        CmodelExport.Export(cmc, model, created: "2026-10-19T00:00:00Z");
        string cnl = "Vgs = -0.6\nVds = 6\n" + ValidationBench.Device(model, 4, intrinsic: false, "g", "d") +
                     "Tuner:SRC g 0 Z[1]=50 Zdefault=1e-6 BiasTee=on Vbias=Vgs\n" +
                     "Tuner:LOAD d 0 Z[1]=50 Zdefault=1e-6 BiasTee=on Vbias=Vds\n" +
                     "analysis LPP1 type=loadpull_pursuit Tone=2e9 MaxHarm=3 LoadTuner=LOAD SourceTuner=SRC Sweep=Load TuneHarm=1 " +
                     "Compression=1 GainType=Gt PinStart=-10 PinStep=2 PinMax=20 Tol=1e-9 CreateLoadpullResult=false\n";
        var lpp = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, dir, dir), new CircuitEvaluationRequest { Measurements = false });
        Assert.True(lpp.Status == RunStatus.Success, lpp.StatusMessage);
        var ds = lpp.Results.Single(r => r.Name == "LPP1").Data;
        Assert.Equal(1.0, ds["MXP_Converged"].RealValues[0]);
        Assert.Equal(1.0, ds["MXE_Converged"].RealValues[0]);
        Assert.Equal(0.0, ds["UnscorableCount"].RealValues[0]);
    }
}
