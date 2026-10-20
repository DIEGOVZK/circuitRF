using System.Globalization;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Modeling;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-8 §4: check (c) on devices that are not composed models, written as SDDs in a <c>.cnl</c>.
///
/// <list type="bullet">
/// <item>The synthetic device's own SDD — its gate and drain currents from charges — makes no spurious DC: the change
/// in DC gate current from f to 2f is zero to harmonic-balance tolerance.</item>
/// <item>The same device with its gate current written as capacitances times dV/dt ([P1] eq. 6.32), the derivatives
/// carried by two auxiliary ports weighted by <c>H[1] = jω</c>, makes a DC that grows linearly with f, as [P1] §6.3.7
/// says: the step from f to 2f equals the step from 2f to 3f.</item>
/// </list>
/// </summary>
public sealed class ConservationCheckLargeSignalTests(ITestOutputHelper output)
{
    /// <summary>Near the knee, where the planted curl Cx·sech²(vds) is not negligible.</summary>
    private static readonly LargeSignalSettings Drive = new() { Bias = (-0.8, 1.5), Amplitude = 0.2, DrainAmplitude = 1.0 };

    private static string Sdd(string eq) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(eq, @"\bvgs\b", "_v1"), @"\bvds\b", "_v2");

    private static string Write(string body)
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("crf-mc8-ls-").FullName, "dut.cnl");
        File.WriteAllText(path, ComposedTestModels.SddGlobals + body);
        return path;
    }

    /// <summary>The synthetic device as an SDD on (g s d s): currents, and the gate and drain charges.</summary>
    private static string ChargeDevice =>
        $"SDD:X1 g 0 d 0 I[1,0]=({Sdd(ComposedTestModels.IgGsEq)})+({Sdd(ComposedTestModels.IgGdEq)}) " +
        $"I[2,0]=({Sdd(ComposedTestModels.IdEq)})-({Sdd(ComposedTestModels.IgGdEq)}) " +
        $"I[1,1]={Sdd(ComposedTestModels.QgEq)} I[2,1]={Sdd(ComposedTestModels.QdEq)}\n";

    /// <summary>
    /// The same device with its gate displacement current as C11·dvgs/dt + C12·dvds/dt, where C11 keeps the truth's vds
    /// dependence but C12 drops the matching vgs dependence — so ∂C11/∂vds − ∂C12/∂vgs = Cx·sech²(vds) ≠ 0. Ports 3 and 4
    /// (nodes a, b) carry dv/dt × 1e-10 (KCL there: 10 mS·v3 = 1 pF·dv1/dt), scaled so their currents are milliamps — a
    /// 1 F charge there would make them ~1e9 A and floor the HB residual at ~1e-6 A.
    /// </summary>
    internal static string CapacitanceDevice =>
        $"SDD:X1 g 0 d 0 a 0 b 0 I[1,0]=({Sdd(ComposedTestModels.IgGsEq)})+({Sdd(ComposedTestModels.IgGdEq)})" +
        "+(C0+C1+Cx*tanh(_v2))*1e10*_v3-C1*1e10*_v4 " +
        $"I[2,0]=({Sdd(ComposedTestModels.IdEq)})-({Sdd(ComposedTestModels.IgGdEq)}) I[2,1]={Sdd(ComposedTestModels.QdEq)} " +
        "I[3,0]=1e-2*_v3 I[3,1]=-1e-12*_v1 I[4,0]=1e-2*_v4 I[4,1]=-1e-12*_v2\n";

    [Fact]
    public void ChargeBasedSdd_MakesNoSpuriousDc()
    {
        var r = ConservationCheck.LargeSignal(Write(ChargeDevice), "X1", Drive);
        output.WriteLine($"{r.Line}; {r.Relative:E3} of the fundamental {r.AtF.GateFundamental:E4} A; DC {r.AtF.GateDc:E4} A");
        Assert.True(r.Relative < 1e-6, $"{r.Relative:E3}");
    }

    [Fact]
    public void CapacitanceBasedSdd_MakesDcThatGrowsLinearlyWithF()
    {
        string cnl = Write(CapacitanceDevice);
        double f = Drive.FrequencyHz;
        double[] dc = [.. new[] { f, 2 * f, 3 * f }.Select(x => ConservationCheck.Drive(cnl, "X1", Drive, x).GateDc)];
        double step1 = dc[1] - dc[0], step2 = dc[2] - dc[1];
        var r = ConservationCheck.LargeSignal(cnl, "X1", Drive);
        output.WriteLine($"DC at f, 2f, 3f: {string.Join(", ", dc.Select(v => v.ToString("E6", CultureInfo.InvariantCulture)))} A");
        output.WriteLine($"steps {step1:E6}, {step2:E6}; {r.Line}; {r.Relative:P3} of the fundamental");
        // Measured: the two steps agree to 5e-6; the spurious DC is 0.41 % of the fundamental (the charge device's, 7e-23).
        Assert.True(r.Relative > 1e-4, $"{r.Relative:E3}: not vacuous");
        Assert.True(Math.Abs(step2 - step1) < 1e-4 * Math.Abs(step1), $"{step1:E6} then {step2:E6}");
    }
}
