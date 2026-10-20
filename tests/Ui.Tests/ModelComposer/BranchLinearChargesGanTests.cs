using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Tests.Composed;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Modeling;
using CircuitRF.Engine;
using CircuitRF.Ui.Tests.Examples;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.ModelComposer;

/// <summary>
/// brief-modelcomposer-22 §4 on the shipped SyntheticGan model — ANN charges over <c>(vgs, vds, T)</c>, NOT separable, with
/// self-heating and a drain-lag trap: one capacitance at a time held at its bias-point value.
/// </summary>
public sealed class BranchLinearChargesGanTests(ITestOutputHelper output)
{
    private static readonly string ModelPath =
        Path.Combine(ModelCliFixture.RepoRoot(), "examples", ModelComposerExampleAuthoring.Folder, ModelComposerExampleAuthoring.GanModel);

    private const double Vgs = -0.8, Vds = 8;

    private static readonly string[] BranchNames =
        [ComposedSwitches.LinearCgsName, ComposedSwitches.LinearCdgName, ComposedSwitches.LinearCdsName, ComposedSwitches.LinearCmName];

    private static ComposedSwitches Switches(int set) => new()
    {
        LinearCgs = (set & 1) != 0, LinearCdg = (set & 2) != 0, LinearCds = (set & 4) != 0, LinearCm = (set & 8) != 0,
    };

    private static string Text(int set) => string.Join(" ", Enumerable.Range(0, 4).Where(b => (set & (1 << b)) != 0).Select(b => BranchNames[b] + "=true"));

    private static string SpBench(string switches) => $"""
        Port:P1 in 0 Num=1 Z=50 Ohm
        C:CB1 in g C=1e-6
        L:LB1 vg g L=1e-3
        Vdc:VG vg 0 V={Vgs:R}
        Port:P2 out 0 Num=2 Z=50 Ohm
        C:CB2 out d C=1e-6
        L:LB2 vd d L=1e-3
        Vdc:VD vd 0 V={Vds:R}
        ComposedFet:X1 g d 0 File="{ModelPath}" N=8 {switches}

        """;

    private static DataCube Sp(string cnl, double[] f)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), f)["S"];
    }

    /// <summary>
    /// R-mc22-1's second property: every residual and its gradient vanish at the bias point, so small-signal S there does
    /// not depend on the switches. Isothermal, that is the model's own S. Self-heating, the held surface carries no
    /// temperature column — this model's charges are ANNs over <c>T</c> — so every combination is <c>LinearC</c>'s S
    /// exactly, and that differs from the model's by the thermal node's reach into the charge (measured 6.8e-10 at
    /// 0.5 GHz, falling with frequency).
    /// </summary>
    [Fact]
    public void SParametersAtTheBiasPoint_DoNotDependOnTheSwitches_All16Combinations()
    {
        double[] f = [0.5e9, 2e9, 10e9];
        double Worst(DataCube a, DataCube b)
        {
            double w = 0;
            for (int k = 0; k < f.Length; k++)
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++) w = Math.Max(w, ((Complex)a[k, i, j] - (Complex)b[k, i, j]).Magnitude);
            return w;
        }

        var isothermal = Sp(SpBench("SelfHeating=false"), f);
        var linear = Sp(SpBench("LinearC=true"), f);
        for (int set = 1; set < 16; set++)
        {
            double cold = Worst(Sp(SpBench(Text(set) + " SelfHeating=false"), f), isothermal);
            Assert.True(cold <= 1e-12, $"{Text(set)}, isothermal: |ΔS| {cold:E2} against the model");
            double hot = Worst(Sp(SpBench(Text(set)), f), linear);
            Assert.True(hot <= 1e-12, $"{Text(set)}: |ΔS| {hot:E2} against LinearC");
        }
        double thermal = Worst(linear, Sp(SpBench(""), f));
        output.WriteLine($"LinearC against the model, self-heating: |ΔS| {thermal:E2}");
        Assert.True(thermal <= 1e-8, $"{thermal:E2}");
    }

    private static ComposedCoreModel Core(ComposedSwitches s, double[] at)
    {
        var core = new ComposedCoreModel(ComposedModelCache.Load(ModelPath), 8, 25, s);
        Assert.True(core.FreezeAt(at));
        return core;
    }

    /// <summary>The frozen point: the intrinsic bias, the trap state at the drain voltage, a few kelvin of self-heating.</summary>
    private static readonly double[] Q = [-1.1, 7.2, 7.2, 4.0];

    /// <summary>Points over the bias plane, trap state and temperature rise; <paramref name="inside"/> keeps them within the
    /// charges' training hull (vgs −2 to 0 V, vds 0.5 to 15 V).</summary>
    private static IEnumerable<double[]> Points(int seed, int count, bool inside = false)
    {
        var rng = new Random(seed);
        for (int t = 0; t < count; t++)
            yield return inside
                ? [-1.9 + 1.8 * rng.NextDouble(), 1 + 13 * rng.NextDouble(), 20 * rng.NextDouble(), 15 * rng.NextDouble()]
                : [-3.5 + 3.5 * rng.NextDouble(), 1 + 14 * rng.NextDouble(), 20 * rng.NextDouble(), 15 * rng.NextDouble()];
    }

    /// <summary>R-mc22-1's third property: all four together are <c>LinearC</c>, up to the quadrature's error — inside the
    /// training hull, where the ANN is smooth. A line that leaves the hull crosses its boundary, where the capacitance is
    /// continuous but its slope is not, and the quadrature is not as good there (1.8e-5 relative, measured at vgs ≈ −2.9 V).</summary>
    [Fact]
    public void AllFour_AreLinearC_ToTheQuadrature()
    {
        var four = Core(Switches(15), Q);
        var linear = Core(new ComposedSwitches { LinearC = true }, Q);
        double worst = 0;
        foreach (var v in Points(15, 10, inside: true))
        {
            var a = four.Evaluate(new PortVoltages(v)).Q;
            var b = linear.Evaluate(new PortVoltages(v)).Q;
            for (int p = 0; p < 2; p++) worst = Math.Max(worst, Math.Abs(a[p] - b[p]) / Math.Abs(b[p]));
        }
        output.WriteLine($"all four vs LinearC: {worst:E2} relative");
        Assert.True(worst <= 1e-6, $"{worst:E2}");
    }

    /// <summary>R-mc22-2: the Jacobian is the exact derivative of the quadrature sum, the ANN's own Hessian inside its
    /// domain and a difference of its gradients outside.</summary>
    [Fact]
    public void TheJacobian_IsTheDerivativeOfTheCharge()
    {
        for (int b = 0; b < 4; b++)
        {
            var core = Core(Switches(1 << b), Q);
            foreach (var v in Points(30 + b, 5))
            {
                double error = ComposedTestModels.IntrinsicJacobianError(core, v);
                Assert.True(error <= 1e-6, $"{BranchNames[b]} at ({v[0]:G4}, {v[1]:G4}): {error:E2}");
            }
        }
    }

    /// <summary>R-mc22-1's first property, through MC-8's large-signal check (c): the charge is single-valued, so the drive
    /// at 2f makes no DC the drive at f does not. It reads exactly 0 A — as it does for the model as fitted: harmonic balance
    /// writes a charge's current as <c>jkω·Q_k</c>, so any charge-based device has none at DC.</summary>
    [Theory]
    [InlineData(ComposedSwitches.LinearCgsName)]
    [InlineData(ComposedSwitches.LinearCdgName)]
    public void LargeSignalConservation_ReadsBelowItsThreshold(string branch)
    {
        string dir = Directory.CreateTempSubdirectory("crf-mc22-check-").FullName;
        try
        {
            string cnl = Path.Combine(dir, "device.cnl");
            File.WriteAllText(cnl, $"ComposedFet:X1 g d 0 File=\"{ModelPath}\" {branch}=true\n");
            var c = ConservationCheck.LargeSignal(cnl, "X1", new LargeSignalSettings
            {
                Bias = (-1.2, 7), Tolerance = ConservationCheck.StateNodeTolerance,
            });
            output.WriteLine($"{branch}: {c.Line}, {c.Relative:E2} of the fundamental");
            Assert.True(c.Relative <= ConservationCheck.DefaultThreshold, $"{branch}: {c.Relative:E2}");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    /// <summary>
    /// One power sweep per single-branch switch on the example's GanPowerSweep bench: every rung converges. The bench's sweep
    /// stops at 13 dBm here, not 22: the model as fitted does not converge at Pin 14, 16 to 21 dBm on it (measured), and each
    /// rung is warm-started from the last, so above that a switch's result says more about the rung before it than about
    /// the switch. The AM–PM and H2 differences against the model are printed, not asserted. Minutes: opt-in.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void ThePowerSweep_ConvergesAtEveryRung_ForEachSingleBranch()
    {
        string dir = Path.GetDirectoryName(ModelPath)!;
        var reference = Sweep("", dir);
        Assert.All(reference.Converged, c => Assert.True(c, "the model as fitted did not converge at a rung"));
        foreach (string branch in BranchNames)
        {
            var s = Sweep(branch + "=true", dir);
            for (int k = 0; k < s.Pin.Length; k++)
            {
                Assert.True(s.Converged[k], $"{branch}: Pin {s.Pin[k]} dBm did not converge");
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{branch} Pin {s.Pin[k],5:F1} dBm: ΔAM–PM {s.PhaseDeg[k] - reference.PhaseDeg[k],8:F3}°  ΔH2 {s.H2Db[k] - reference.H2Db[k],8:F3} dB"));
            }
        }
    }

    private sealed record PowerSweep(double[] Pin, bool[] Converged, double[] PhaseDeg, double[] H2Db);

    private PowerSweep Sweep(string switches, string dir)
    {
        string cnl = ModelComposerExampleAuthoring.PowerSweepNetlist
            .Replace($"File=\"{ModelComposerExampleAuthoring.GanModel}\"", $"File=\"{ModelPath}\"", StringComparison.Ordinal)
            .Replace("N=8", $"N=8 {switches}", StringComparison.Ordinal)
            .Replace("Stop=22", "Stop=13", StringComparison.Ordinal);
        var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, dir, dir), new CircuitEvaluationRequest { Measurements = false });
        Assert.True(r.Status == RunStatus.Success, $"{switches}: {r.Status} {r.StatusMessage}");
        var ds = r.Results.First(x => x.Data.Contains("V")).Data;
        bool[] converged = [.. ds["Converged"].RealValues.Select(c => c == 1)];

        var v = ds["V"];
        int nodeAx = v.Axes.ToList().FindIndex(a => a.Name == "node");
        int rungs = v.Axes[0].Length, nodes = v.Axes[nodeAx].Length, harmonics = v.Axes[nodeAx + 1].Length;
        int vin = Array.IndexOf(v.Axes[nodeAx].Labels!, "Vin"), vout = Array.IndexOf(v.Axes[nodeAx].Labels!, "Vout");
        Complex At(int k, int node, int h) => v.ComplexValues[(k * nodes + node) * harmonics + h];
        var phase = new double[rungs];
        var h2 = new double[rungs];
        for (int k = 0; k < rungs; k++)
        {
            phase[k] = (At(k, vout, 1) / At(k, vin, 1)).Phase * 180 / Math.PI;
            h2[k] = 20 * Math.Log10(At(k, vout, 2).Magnitude);
        }
        return new PowerSweep(v.Axes[0].Values, converged, phase, h2);
    }
}
