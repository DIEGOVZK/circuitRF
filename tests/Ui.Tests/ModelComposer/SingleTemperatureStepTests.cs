using System.Text;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using Xunit;
using static CircuitRF.Ui.Tests.ModelComposer.SyntheticFetData;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-9 R-mc9-4 (D8): data at one temperature gives a single-temperature model — no T input, no
/// self-heating with "one temperature" as the only reason, and a model recording T0.
/// </summary>
public sealed class SingleTemperatureStepTests
{
    [Fact]
    public void OneTemperature_OffersNoTInputNorSelfHeating_AndExportsASingleModelAtT0()
    {
        var (doc, data, intrinsic) = ThermalDocument(ThermalSet("dc", pulsed: false, 25));

        var plan = ThermalFit.Plan(doc, data);
        Assert.True(plan.IsSingle);
        Assert.False(plan.HasTemperatureInput);
        Assert.False(plan.SelfHeatingAvailable);
        Assert.Equal(TemperaturePlan.OneTemperature, plan.SelfHeatingUnavailable);
        Assert.Equal(25, plan.T0);

        var ex = Assert.Throws<ModelingException>(
            () => ThermalFit.Run(doc, data, intrinsic, new ThermalFitSettings { SelfHeating = true, Rth = 50 }));
        Assert.EndsWith(TemperaturePlan.OneTemperature + ".", ex.Message);

        var fit = CurrentFit.Run(doc, data, intrinsic, new CurrentFitSettings { Id = ThermalIdEquation with { Expression = ComposedTestModels.IdEq,
            Parameters = ThermalIdEquation.Parameters.Where(p => p.Key != "kT").ToDictionary() }, Ig = ThermalIgEquation }).Record;
        Assert.Equal(["vgs", "vds"], fit.Id.Inputs);
        Assert.Equal(["vgs", "vds"], fit.Ig.Inputs);

        var truth = ComposedTestModels.Model();
        var model = truth with
        {
            Temperature = ThermalFit.ModelTemperature(plan, [fit.Id, fit.Ig]),
            Thermal = ThermalFit.Network(ThermalFit.Record(doc)),
            Core = truth.Core with { Id = fit.Id, Ig = fit.Ig },
        };
        string json = Encoding.UTF8.GetString(CmodelFile.ModelJson(model));
        Assert.Contains("\"Temperature\": {\n    \"Mode\": \"Single\",\n    \"T0\": 25\n  }", json);
        Assert.DoesNotContain("\"Thermal\"", json);
        CmodelFile.Validate(model);
    }
}
