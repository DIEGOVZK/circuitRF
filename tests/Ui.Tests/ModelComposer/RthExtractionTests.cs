using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Modeling;
using Xunit;
using static CircuitRF.Ui.Tests.ModelComposer.SyntheticFetData;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-9 R-mc9-3: Rth from an isothermal model and self-heated DC data ([P1] §6.6, [R11] §2.8), and the
/// DC-only path that de-heats the data with Rth typed.
/// </summary>
public sealed class RthExtractionTests
{
    [Fact]
    public void FromPulsedAndDcData_TheExtractedRthIsTheTruths()
    {
        // Pulsed at three chuck temperatures (isothermal), DC at two ambients (self-heated by the truth's 60 K/W); the
        // isothermal Id is the default ANN.
        var (doc, data, intrinsic) = ThermalDocument(
            ThermalSet("p25", pulsed: true, 25), ThermalSet("p100", pulsed: true, 100), ThermalSet("p175", pulsed: true, 175),
            ThermalSet("dc25", pulsed: false, 25), ThermalSet("dc100", pulsed: false, 100));

        var r = ThermalFit.Run(doc, data, intrinsic, new ThermalFitSettings { SelfHeating = true, TauTh = TruthTau }).Record;

        Assert.Equal(ThermalFitRecord.Extracted, r.RthSource);
        Assert.InRange(r.Rth!.Value, 0.97 * TruthRth, 1.03 * TruthRth);
        var e = r.Extraction!;
        Assert.True(e.LowPower > 0 && e.Used > 20, $"{e.Used} used, {e.LowPower} left out");
        Assert.Equal(0, e.Unresolved);
        Assert.Equal([25.0, 100.0, 175.0], r.Temperatures);
        var pole = Assert.Single(r.Poles);
        Assert.Equal(TruthTau, pole.Rth * pole.Cth, 1e-12);
    }

    [Fact]
    public void DcOnlyWithRthTyped_DeheatsTheDataToTheIsothermalTruth()
    {
        var (doc, data, intrinsic) = ThermalDocument(ThermalSet("dc25", pulsed: false, 25), ThermalSet("dc100", pulsed: false, 100));
        var settings = new CurrentFitSettings { Id = ThermalIdEquation, Ig = ThermalIgEquation };

        double Worst(CmcDocument d)
        {
            var id = CurrentFit.Run(d, data, intrinsic, settings).Record.Id;
            Assert.Contains("T", id.Inputs);
            var f = EquationFit.Function(id.Expression("Expression")!, id.Parameters, "Id", ["vgs", "vds", "T"]);
            double worst = 0;
            foreach (double t in (double[])[25, 90, 160])
                foreach (double vgs in (double[])[-0.6, 0.1, 0.3])
                    foreach (double vds in (double[])[2.5, 7])
                        worst = Math.Max(worst, Math.Abs(f([vgs, vds, t]).F / TruthId(vgs, vds, t) - 1));
            return worst;
        }

        // Each DC point moved to Tamb + Rth·P recovers the isothermal truth everywhere, far from the DC data's own Tj.
        var deheated = ThermalFit.WithSettings(doc, new ThermalFitSettings { SelfHeating = true, Rth = TruthRth });
        double error = Worst(deheated);
        Assert.True(error < 0.005, $"{error:P2}");

        // Left at the ambient, the same fit reads the heating as a temperature law it does not have.
        Assert.True(Worst(doc) > 0.03);
    }
}
