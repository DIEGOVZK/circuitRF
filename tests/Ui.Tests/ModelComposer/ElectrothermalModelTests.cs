using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using CircuitRF.Engine;
using Xunit;
using static CircuitRF.Ui.Tests.ModelComposer.SyntheticFetData;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-9 R-mc9-1/2: a self-heating model built from self-heated DC data at two ambients reproduces the
/// truth's DC I–V — negative DC output conductance included — while its RF output conductance, from S far above the
/// thermal corner, is the truth's ISOTHERMAL Gds at the frozen junction temperature: [P1] eq. 6.69's two limits.
/// </summary>
public sealed class ElectrothermalModelTests
{
    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    [Fact]
    public void TheModel_HasTheSelfHeatedDcConductance_AndTheIsothermalRfOne()
    {
        var (doc, data, intrinsic) = ThermalDocument(ThermalSet("dc25", pulsed: false, 25), ThermalSet("dc100", pulsed: false, 100));
        doc = ThermalFit.WithSettings(doc, new ThermalFitSettings { SelfHeating = true, Rth = TruthRth, TauTh = TruthTau });
        var thermal = ThermalFit.Run(doc, data, intrinsic, ThermalFit.Settings(doc));
        doc = ThermalFit.Apply(doc, thermal);
        var fit = CurrentFit.Run(doc, data, intrinsic, new CurrentFitSettings { Id = ThermalIdEquation, Ig = ThermalIgEquation }).Record;

        var truth = ComposedTestModels.Model();
        var model = truth with
        {
            Temperature = ThermalFit.ModelTemperature(ThermalFit.Plan(doc, data), [fit.Id, fit.Ig]),
            Thermal = ThermalFit.Network(thermal.Record),
            Network = new ExtrinsicNetwork(ChargeFit.IntrinsicNetwork, []),
            Core = truth.Core with { Id = fit.Id, Ig = fit.Ig },
        };
        Assert.Equal(TemperatureMode.Range, model.Temperature.Mode);
        string path = ComposedTestModels.WriteTemp(model);

        // DC at 25 °C along vgs = 0.4 V, where the truth's heating turns the output conductance negative.
        double[] vds = [6, 8, 10];
        var tj = new List<double>();
        var heated = ThermalIv([0.4], vds, 25, selfHeated: true, tj).Points;
        var modelId = vds.Select(v => DcDrainCurrent(path, 0.4, v)).ToArray();
        for (int k = 0; k < vds.Length; k++)
            Assert.True(Math.Abs(modelId[k] / heated[k].Id - 1) < 0.005, $"vds {vds[k]}: {modelId[k]:R} vs {heated[k].Id:R}");
        Assert.True(heated[2].Id < heated[1].Id, "the truth's DC output conductance is negative here");
        Assert.True(modelId[2] < modelId[1], "and so is the model's");

        // RF: Re Y22 at 1 GHz (the thermal corner is ~160 kHz) against the truth's isothermal ∂Id/∂vds at the same Tj.
        const double h = 1e-4;
        double gdsIso = (TruthId(0.4, 10 + h, tj[2]) - TruthId(0.4, 10 - h, tj[2])) / (2 * h);
        double gdsRf = ReY22(path, 0.4, 10, 1e9);
        Assert.True(gdsIso > 0);
        Assert.True(Math.Abs(gdsRf / gdsIso - 1) < 0.02, $"Re Y22 {gdsRf:R} S vs isothermal Gds {gdsIso:R} S");
    }

    private static string Device(string path, double vgs, double vds) =>
        "temp = 25\n" +
        $"Vdc:VG vg 0 V={R(vgs)}\nIProbe:IPG vg lg\nL:LG lg g L=1\nC:CG pa g C=1\nPort:P1 pa 0 Num=1 Z=50 Ohm\n" +
        $"Vdc:VD vd 0 V={R(vds)}\nIProbe:IPD vd ld\nL:LD ld d L=1\nC:CD pb d C=1\nPort:P2 pb 0 Num=2 Z=50 Ohm\n" +
        $"ComposedFet:X1 g d 0 File=\"{path}\"\n";

    private static double DcDrainCurrent(string path, double vgs, double vds)
    {
        var (lib, tb) = new CnlReader().Read(Device(path, vgs, vds));
        using var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);
        Assert.True(dc.Converged);
        return dc.ProbeCurrents["IPD"];
    }

    /// <summary>Re Y22 of the biased device, from its 50 Ω S: <c>Y = (I − S)(I + S)⁻¹ / Z0</c>.</summary>
    private static double ReY22(string path, double vgs, double vds, double f)
    {
        var (lib, tb) = new CnlReader().Read(Device(path, vgs, vds));
        using var nl = new Elaborator(lib).Elaborate(tb);
        var s = SParameterEngine.Run(nl, [f])["S"];
        Complex s11 = (Complex)s[0, 0, 0], s12 = (Complex)s[0, 0, 1], s21 = (Complex)s[0, 1, 0], s22 = (Complex)s[0, 1, 1];
        // (I + S)⁻¹, then (I − S) times it.
        Complex a = 1 + s11, b = s12, c = s21, d = 1 + s22, det = a * d - b * c;
        Complex i11 = d / det, i12 = -b / det, i21 = -c / det, i22 = a / det;
        Complex y22 = (-s21 * i12 + (1 - s22) * i22) / 50.0;
        return y22.Real;
    }
}
