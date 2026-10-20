using System.Globalization;
using System.Numerics;
using CircuitRF.Cli;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Modeling;
using CircuitRF.Engine;
using CircuitRF.Ui.Tests.Lvs;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-18: a model from DC I–V alone. Fit all on a document with one DC set and no S holds the network
/// with its line and writes the typed charges; the export passes <c>circuitrf check</c>, its DC matches the data, and its
/// intrinsic Y carries exactly the typed capacitances. Zero charges are said at export, and typed values beside S data
/// are refused.
/// </summary>
[Collection(LvsCliConsoleCollection.Name)]
public sealed class DcOnlyModelTests(ITestOutputHelper output)
{
    private const double Cgs = 0.3e-12, Cgd = 0.05e-12, Cds = 0.08e-12, Cm = -0.04e-12;
    private const string Typed = "Cgs 300 fF, Cgd 50 fF, Cds 80 fF, Cm -40 fF";

    [Fact]
    public void DcAlone_FitsAndExportsAModel_WhoseChargesAreTheTypedCapacitances()
    {
        string dir = Directory.CreateTempSubdirectory("crf-mc18-").FullName;
        try
        {
            ModelCliFixture.WriteRaw(dir);
            string cmc = Path.Combine(dir, "fet.cmc");
            ModelCommands.New(cmc, CmcKind.Active, "gan-two-shell", new BasePeriphery(4, "125 um"));
            ModelCommands.Import(cmc, ModelCliFixture.DcSet);
            var doc = CmcFile.Read(cmc);
            var c = CurrentFit.Settings(doc);
            doc = CurrentFit.WithSettings(doc, c with
            {
                Restarts = 1,
                Id = c.Id with { Hidden = [8], MaxIterations = 3000 },
                Ig = c.Ig with { Hidden = [4], MaxIterations = 500 },
            });
            doc = ChargeFit.WithSettings(doc, ChargeFit.Settings(doc) with { Typed = new TypedCharges(Cgs, Cgd, Cds, Cm) });
            CmcFile.Write(cmc, doc);

            // Fit all holds the network with its line; naming the network still refuses.
            Assert.Equal(["network", "thermal", "current", "charge"], ModelCommands.AllSteps(doc));
            var fit = ModelCommands.Fit(cmc, "all").Select(l => l.Text).ToList();
            foreach (string l in fit) output.WriteLine(l);
            Assert.Contains("network: held at its values — no S data to fit to", fit);
            Assert.Contains($"no S data: the charges are typed — {Typed}.", fit);
            Assert.Throws<ModelingException>(() => ModelCommands.Fit(cmc, "network"));

            string model = Path.Combine(dir, "fet.cmodel");
            CmodelExport.Export(cmc, model, created: "2026-10-20T00:00:00Z");
            Assert.Equal(0, Check(model));
            Assert.Equal($"Qg, Qd: typed ({Typed})", CmodelFile.Read(model).Provenance.Charges);
            Assert.Contains(ModelCommands.Explain(cmc), l => l.Text == $"Qg, Qd: typed ({Typed}).");

            // DC within 1 % of range; no S items.
            var v = ModelValidation.RunFile(cmc, model);
            Assert.DoesNotContain(v.Items, i => i.Kind == ValidationKind.SParams);
            var dc = v.Items.Single(i => i.Kind == ValidationKind.DcIv).Data;
            double[] meas = dc["Id.meas"].RealValues, mod = dc["Id.model"].RealValues;
            double rms = Math.Sqrt(meas.Select((m, k) => (m - mod[k]) * (m - mod[k])).Average()) / (meas.Max() - meas.Min());
            output.WriteLine($"DC I–V RMS {100 * rms:F3} % of range");
            Assert.True(rms <= 0.01, $"DC I–V RMS {100 * rms:F2} % of range");

            // (a) not run, (b) zero, (c) run.
            var check = ConservationCheck.RunFile(cmc, new LargeSignalSettings());
            foreach (string l in check.Lines) output.WriteLine(l);
            Assert.Null(check.Data);
            Assert.Contains("data: not run — the charges are typed; there is no S data.", check.Lines);
            Assert.Equal(0, check.Model!.GateRms);
            Assert.Equal(0, check.Model.DrainRms);
            Assert.NotNull(check.LargeSignal);

            // The intrinsic Y at 1 GHz: the model with its network replaced by the 1 µΩ straps, as S-parameters.
            var (y11, y12) = IntrinsicY(model, dir, 1e9);
            double w = 2 * Math.PI * 1e9;
            output.WriteLine($"Im Y11/ω {y11.Imaginary / w:R}, Im Y12/ω {y12.Imaginary / w:R}");
            Assert.True(Math.Abs(y11.Imaginary / w / (Cgs + Cgd) - 1) < 1e-6, $"Im Y11/ω {y11.Imaginary / w:R}");
            Assert.True(Math.Abs(y12.Imaginary / w / -Cgd - 1) < 1e-6, $"Im Y12/ω {y12.Imaginary / w:R}");

            // All four at zero: a model, said so on the step, at export and in explain.
            CmcFile.Write(cmc, ChargeFit.WithSettings(CmcFile.Read(cmc), ChargeFit.Settings(CmcFile.Read(cmc)) with { Typed = null }));
            Assert.Contains(ModelCommands.Fit(cmc, "charge"), l => l.Text == ChargeFit.ZeroLine);
            Assert.Contains(ChargeFit.ZeroLine, CmodelExport.Export(cmc, model).Lines);
            Assert.Contains(ModelCommands.Explain(cmc), l => l.Text == ChargeFit.ZeroLine);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void TypedValues_BesideSData_AreRefusedNamingBoth()
    {
        string cmc = ExportFixture.Document(charge: false);
        CmcFile.Write(cmc, ChargeFit.WithSettings(CmcFile.Read(cmc), ChargeFitSettings.Default with { Typed = new TypedCharges(Cgs) }));
        var ex = Assert.Throws<ModelingException>(() => ChargeFit.FitFile(cmc));
        Assert.Equal("Charge: typed capacitances and S data (bias) are both given; typing would discard the data. Clear the typed values to fit to it.",
                     ex.Message);
    }

    /// <summary>Y11 and Y12 of the exported core alone at vgs = vds = 0, from S through two 50 Ω ports.</summary>
    private static (Complex Y11, Complex Y12) IntrinsicY(string model, string dir, double f)
    {
        string core = Path.Combine(dir, "core.cmodel");
        CmodelFile.Write(CmodelFile.Read(model) with { Network = new ExtrinsicNetwork(ChargeFit.IntrinsicNetwork, []) }, core);
        string bench = $"""
            Port:P1 g 0 Num=1 Z=50 Ohm
            Port:P2 d 0 Num=2 Z=50 Ohm
            ComposedFet:X1 g d 0 File="{core}" N=4

            """;
        var (lib, tb) = new CnlReader().Read(bench);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [f])["S"];
        Complex s11 = (Complex)s[0, 0, 0], s12 = (Complex)s[0, 0, 1], s21 = (Complex)s[0, 1, 0], s22 = (Complex)s[0, 1, 1];
        // Y = (I − S)(I + S)⁻¹ / Z0.
        Complex a11 = 1 + s11, a12 = s12, a21 = s21, a22 = 1 + s22, det = a11 * a22 - a12 * a21;
        Complex i11 = a22 / det, i12 = -a12 / det, i21 = -a21 / det;
        Complex y11 = ((1 - s11) * i11 - s12 * i21) / 50, y12 = ((1 - s11) * i12 - s12 * (a11 / det)) / 50;
        return (y11, y12);
    }

    private static int Check(string path)
    {
        var (realOut, realErr) = (Console.Out, Console.Error);
        try
        {
            Console.SetOut(new StringWriter(CultureInfo.InvariantCulture));
            Console.SetError(new StringWriter(CultureInfo.InvariantCulture));
            JsonRun.Reset();
            return CliEntry.Run(["check", path]);
        }
        finally { Console.SetOut(realOut); Console.SetError(realErr); }
    }
}
