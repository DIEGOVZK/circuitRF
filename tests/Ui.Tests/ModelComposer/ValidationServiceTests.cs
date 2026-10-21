using System.Globalization;
using CircuitRF.Cli;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-11 §4 (R-mc11-4): validation against data of the truth the model was composed from passes — S at
/// the measurement plane, a power sweep against the truth's own HB and a loadpull file against the truth's own loadpull — a
/// model with a perturbed Lg fails the S item, and the generated testbenches run unchanged through <c>circuitrf hb</c> and
/// <c>circuitrf lp</c>.
/// </summary>
[Collection(CircuitRF.Ui.Tests.Lvs.LvsCliConsoleCollection.Name)]
public sealed class ValidationServiceTests(ITestOutputHelper output)
{
    [Fact]
    public void SParams_PassForTheModel_AndFailWithAPerturbedLg()
    {
        string cmc = ExportFixture.Document();
        var good = ModelValidation.RunFile(cmc);
        foreach (string line in good.Lines) output.WriteLine(line);
        var item = Assert.Single(good.Items);
        Assert.Equal(ValidationKind.SParams, item.Kind);
        Assert.True(item.Passed, item.Line);

        // The same model with Lg three times as large.
        var model = CmodelFile.Read(good.ModelPath);
        string netlist = model.Network.Netlist.Replace("Lg=3.95E-11", "Lg=1.185E-10");
        Assert.NotEqual(model.Network.Netlist, netlist);
        string bad = Path.Combine(Path.GetDirectoryName(cmc)!, "lg.cmodel");
        CmodelFile.Write(model with { Network = model.Network with { Netlist = netlist } }, bad);
        var perturbed = ModelValidation.RunFile(cmc, bad);
        output.WriteLine(perturbed.Lines[0]);
        Assert.False(perturbed.Items[0].Passed);
        Assert.False(perturbed.Passed);
    }

    [Fact]
    public void PowerSweep_MatchesTheTruthsHb_AndItsTestbenchRunsThroughTheHbVerb()
    {
        string cmc = ExportFixture.Document();
        string dir = Path.GetDirectoryName(cmc)!;
        var cond = new LargeSignalConditions
        {
            Measurement = LargeSignalMeasurement.PowerSweep, FrequencyHz = 2e9, Vgs = -0.6, Vds = 6,
            SourceZ = 25, LoadZ = new System.Numerics.Complex(40, 10),
        };
        double[] pin = [-15, -10, -5, 0];

        // The "measurement": the truth through the testbench validation will generate for the model.
        var truth = CircuitEvaluation.Evaluate(
            PreparedCircuit.FromText(ValidationBench.PowerSweep("pwr", ExportFixture.Truth("gx", "dx"), 25, cond, pin, 5), dir, dir),
            new CircuitEvaluationRequest { Measurements = false });
        var (pout, pinDel, _, _, pdc) = ModelValidation.Powers(truth.Results.First(r => r.Data.Contains("V")).Data, 1);
        string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        File.WriteAllLines(Path.Combine(dir, "pwr.csv"),
            ["Pin,Pout,PAE", .. pin.Select((p, k) => $"{R(p)},{R(ModelValidation.Dbm(pout[k]))},{R(100 * (pout[k] - pinDel[k]) / pdc[k])}")]);
        var doc = CmcFile.Read(cmc);
        CmcFile.Write(cmc, doc with
        {
            DataSets = [.. doc.DataSets, new CmcDataSet
            {
                Name = "pwr", Kind = DataSetKind.LargeSignal, Sources = ["pwr.csv"],
                Conditions = new DataConditions { AmbientC = 25, LargeSignal = cond },
                Import = new ImportSettings
                {
                    Format = ImportSettings.Csv,
                    Columns = [new("Pin", "pin", "dBm"), new("Pout", "pout", "dBm"), new("PAE", "pae", "%")],
                },
            }],
        });

        var result = ModelValidation.RunFile(cmc, settings: ValidationSettings.Default with { DataSets = ["pwr"] });
        var item = Assert.Single(result.Items);
        output.WriteLine(item.Line);
        Assert.Equal(ValidationKind.PowerSweep, item.Kind);
        Assert.True(item.Passed, item.Line);
        var model = item.Data["Pout.model"].RealValues;
        Assert.True(model[^1] - model[0] > 5, "the sweep drives the device");

        string bench = ModelValidation.WriteTestbenches(cmc, result).Single();
        JsonRun.Reset();
        Assert.Equal(0, CliEntry.Run(["hb", bench]));
    }

    [Fact]
    public void Loadpull_MatchesTheTruthsLoadpull_AndItsTestbenchRunsThroughTheLpVerb()
    {
        string cmc = ExportFixture.Document();
        string dir = Path.GetDirectoryName(cmc)!;
        string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        System.Numerics.Complex[] loads = [new(40, 10), new(70, -15), new(25, 5), new(55, 25), new(30, -10), new(50, 0), new(35, 20)];
        string gam = Path.Combine(dir, "truth.gam");
        File.WriteAllLines(gam, ["# impedance Z0=50 re_im", .. loads.Select(z => $"{R(z.Real)} {R(z.Imaginary)}")]);

        // The "measurement": the truth load-pulled through the testbench validation generates for the model.
        var truth = CircuitEvaluation.Evaluate(
            PreparedCircuit.FromText(ValidationBench.Loadpull("lp", ExportFixture.Truth(), 25, 2e9, -0.6, 6, 50, [], [], gam, -15, 1, 30, 5, 3), dir, dir),
            new CircuitEvaluationRequest { Measurements = false });
        Assert.True(truth.Status == RunStatus.Success, truth.StatusMessage);
        RfCore.Loadpull.SplWriter.WriteSpl(truth.Results.First(r => r.Data.Contains("StopCode")).Data, Path.Combine(dir, "truth.spl"));
        var doc = CmcFile.Read(cmc);
        CmcFile.Write(cmc, doc with
        {
            DataSets = [.. doc.DataSets, new CmcDataSet
            {
                Name = "lp", Kind = DataSetKind.LargeSignal, Sources = ["truth.spl"],
                Conditions = new DataConditions
                {
                    AmbientC = 25,
                    LargeSignal = new LargeSignalConditions { Measurement = LargeSignalMeasurement.Loadpull, Vgs = -0.6, Vds = 6, SourceZ = 50 },
                },
                Import = new ImportSettings { Format = ImportSettings.Loadpull },
            }],
        });

        var result = ModelValidation.RunFile(cmc, settings: ValidationSettings.Default with { DataSets = ["lp"] });
        var item = Assert.Single(result.Items);
        output.WriteLine(item.Line);
        Assert.Equal(ValidationKind.Loadpull, item.Kind);
        Assert.True(item.Passed, item.Line);
        // Compared at compression (review 4), load by load, and at each side's MXP and MXE: the model is the truth.
        Assert.Contains("at P-3dB", item.Line);
        Assert.Contains("over 7 load(s)", item.Line);
        Assert.Contains("MXP ZL VSWR", item.Line);
        var (meas, model) = (item.Data["Pout_dBm@compression.meas"].RealValues, item.Data["Pout_dBm@compression.model"].RealValues);
        Assert.All(meas.Zip(model), m => Assert.True(Math.Abs(m.First - m.Second) < 0.05, $"{m.First} vs {m.Second} dBm"));
        Assert.All(item.Metrics.Where(m => m.Name.Contains("VSWR")), m => Assert.True(m.Value < 1.05, $"{m.Name} {m.Value}"));

        string bench = ModelValidation.WriteTestbenches(cmc, result).Single();
        JsonRun.Reset();
        Assert.Equal(0, CliEntry.Run(["lp", bench]));
    }
}
