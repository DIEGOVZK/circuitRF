using System.Numerics;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Core.Tests.Composed;
using RfCore.Data;
using Xunit.Abstractions;
using static CircuitRF.Engine.Tests.Composed.TrapTestModels;

namespace CircuitRF.Engine.Tests.Composed;

/// <summary>
/// brief-modelcomposer-10 R-mc10-3: the <c>Custom</c> trap model. Written to equal the built-in <c>CaptureEmission</c> — its
/// capture exponential continued by the same tangent — it gives the same DC, S-parameters and HB; an expression naming
/// an unknown variable and a DC expression that is not a fixed point of the dynamics are refused, by field.
/// </summary>
public sealed class CustomTrapModelTests(ITestOutputHelper output)
{
    private const string U = "((vds-phi2-Xref)/Vc)";
    private static readonly string Dynamics =
        $"(vds-phi2)/TauEmit2+(Xref/TauCapt2)*(if({U}<20,exp({U}),exp(20)*(1+{U}-20))-exp(-Xref/Vc))";

    private static ModelTraps Custom(string dynamics = "", string dc = "vds") => new()
    {
        Model = CustomTraps.ModelName, States = ["phi2"],
        Parameters = [new("TauEmit2", 1e-3), new("TauCapt2", 1e-8), new("Xref", 1), new("Vc", 0.05)],
        Expressions = [new("phi2.Dynamics", dynamics.Length > 0 ? dynamics : Dynamics), new("phi2.Dc", dc)],
    };

    private static readonly string BuiltIn = ComposedTestModels.WriteTemp(DrainLag());
    private static readonly string Written = ComposedTestModels.WriteTemp(With(Custom()));

    private static string Fet(string path) => $"ComposedFET:X1 g d 0 File=\"{path}\"\n";

    private static void Close(double a, double b, string what) =>
        Assert.True(Math.Abs(a - b) <= 1e-9 * Math.Max(Math.Abs(b), 1e-3), $"{what}: {a:R} vs {b:R}");

    [Fact]
    public void ACustomModelEqualToCaptureEmission_GivesTheSameDcSpAndHb()
    {
        var tight = new AnalysisSettings { NonlinearAbsTol = 1e-14 };
        string bench = ComposedFetEquivalenceTests.Bench(-0.6, 6);
        var a = ComposedFetEquivalenceTests.Dc(bench + Fet(Written), tight);
        var b = ComposedFetEquivalenceTests.Dc(bench + Fet(BuiltIn), tight);
        foreach (var (node, vb) in b) Close(a[node], vb, node);

        double[] f = [0.5e9, 5e9];
        var sa = SParameterEngine.Run(ComposedFetEquivalenceTests.Elaborate(bench + Fet(Written)), f, tight)["S"];
        var sb = SParameterEngine.Run(ComposedFetEquivalenceTests.Elaborate(bench + Fet(BuiltIn)), f, tight)["S"];
        double worstS = 0;
        for (int k = 0; k < f.Length; k++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    worstS = Math.Max(worstS, ((Complex)sa[k, i, j] - (Complex)sb[k, i, j]).Magnitude);
        Assert.True(worstS < 1e-9, $"max |ΔS| {worstS:E3}");

        var ha = Hb(DrainDrive(-0.6, 6, 4, 1e6, 16) + Fet(Written));
        var hb = Hb(DrainDrive(-0.6, 6, 4, 1e6, 16) + Fet(BuiltIn));
        double worst = 0;
        foreach (var (node, yb) in hb)
            for (int k = 0; k < yb.Length; k++)
            {
                double d = (ha[node][k] - yb[k]).Magnitude, scale = Math.Max(yb[k].Magnitude, 1e-3);
                worst = Math.Max(worst, d / scale);
            }
        output.WriteLine($"max |ΔS| {worstS:E3}; HB worst relative difference {worst:E3}; phi2 DC {hb["phi2"][0].Real:F4} V");
        Assert.True(worst < 1e-9, $"HB relative difference {worst:E3}");
        Assert.True(hb["phi2"][0].Real > 9, "not vacuous: the state was captured towards the drain peak");
    }

    [Fact]
    public void ADynamicsNamingAnUnknownVariable_IsRefused()
    {
        var ex = Assert.Throws<CmodelFormatException>(() => CmodelFile.Validate(With(Custom(dynamics: "(vdx-phi2)/TauEmit2"))));
        Assert.Equal("Traps.Expressions.phi2.Dynamics", ex.Field);
        Assert.Contains("'vdx'", ex.Message);

        // A DC value is the state's own, so it cannot read the state.
        var own = Assert.Throws<CmodelFormatException>(() => CmodelFile.Validate(With(Custom(dc: "vds+0*phi2"))));
        Assert.Equal("Traps.Expressions.phi2.Dc", own.Field);
        Assert.Contains("'phi2'", own.Message);
    }

    [Fact]
    public void ADcExpressionThatIsNotAFixedPoint_IsRefused()
    {
        var ex = Assert.Throws<CmodelFormatException>(() => CmodelFile.Validate(With(Custom(dc: "vds+0.01"))));
        Assert.Equal("Traps.Expressions.phi2.Dc", ex.Field);
        Assert.Contains("not a fixed point", ex.Message);
    }
}
