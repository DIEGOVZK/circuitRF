using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-8 §4: a model whose capacitance matrix is not reciprocal — the synthetic composed model, whose
/// <c>∂Qd/∂vgs ≠ ∂Qg/∂vds</c> — passes check (b), read through a <c>ComposedFET</c> in a <c>.cnl</c>, and its
/// transcapacitance is reported as information ([P1] §6.3.8).
/// </summary>
public sealed class TranscapacitanceReportTests(ITestOutputHelper output)
{
    [Fact]
    public void NonReciprocalCharges_PassCheckB_AndReportCm()
    {
        string cmodel = ComposedTestModels.WriteTemp(ComposedTestModels.Model());
        string cnl = Path.Combine(Path.GetDirectoryName(cmodel)!, "dut.cnl");
        File.WriteAllText(cnl, "ComposedFET:X1 g d s File=gan.cmodel\n");

        var r = ConservationCheck.Model(cnl, "X1", (-2, 0.5, 0, 10));
        output.WriteLine($"curl Qg {r.GateRms:E3}, Qd {r.DrainRms:E3}; Cm {r.Transcapacitance:E3} F RMS ({r.TranscapacitanceRelative:P3} of C11)");
        Assert.True(r.GateRms < 1e-3 && r.DrainRms < 1e-3, $"{r.GateRms:E3} {r.DrainRms:E3}");
        Assert.True(r.Transcapacitance > 1e-15, $"{r.Transcapacitance:E3} F: the truth has Cm = Cy·tanh(vds) − Cx·vgs·sech²(vds)");
    }
}
