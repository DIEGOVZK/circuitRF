// ================================================================
//  CoupledInductorsExampleTests.cs — the shipped CoupledInductors example reports a REAL k
//
//  Owner, 2026-09-15: the example's `k_implied = mag(SP1.S(2,1))` is not a coupling factor. It never
//  was — on this circuit it runs 0.013 at 100 MHz to 0.436 at 5 GHz while k is 0.5 everywhere, so it
//  is a transmission magnitude wearing a k's name, and an example that names a quantity wrongly
//  teaches the wrong thing to everyone who opens it.
//
//  The replacement is the extraction itself: S -> Z on the two-port, which for two grounded windings
//  gives Z11 = jwL1, Z22 = jwL2, Z21 = jwM directly, and then k = M/sqrt(L1*L2). Both Z0 and w cancel
//  in that ratio, which is why the answer is flat and exact rather than a curve that happens to pass
//  through 0.5 somewhere.
//
//  Gated end to end — the example is RUN, not just parsed — because the claim is about the numbers.
//  ~1 s: 99 linear points on a four-component circuit.
// ================================================================

using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using RfCore.Data;
using CircuitRF.Cli;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class CoupledInductorsExampleTests(ITestOutputHelper output)
{
    /// <summary>L1 = L2 = 1 nH and M = 500 pH on the schematic, so k is exactly one half.</summary>
    private const double ExpectedK  = 0.5;
    private const double ExpectedL  = 1e-9;
    private const double ExpectedM  = 500e-12;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string SchematicPath() => Path.Combine(
        RepoRoot(), "examples", "S-Parameters", "CoupledInductors", "schematic", "CoupledInductors.csch");

    /// <summary>
    /// Runs the example exactly as Simulate does — <b>through the <c>.cnl</c></b>, in memory.
    ///
    /// <para>Not a detail. The GUI's <c>SchematicRunService</c> and every CLI run verb both go
    /// <c>NetExtractor</c> → <c>CnlWriter</c> → <c>CnlReader</c>, and the two readers do not agree
    /// about a measurement's trailing <c>; comment</c>: <c>CnlReader</c> strips it, the expression
    /// evaluator does not. Calling <c>NetExtractor.Extract</c> directly here reported every
    /// measurement in this example as "Parse error: Unexpected character ';'" — five failures the
    /// application does not have, against a file that runs correctly in it.</para>
    /// </summary>
    private static (DataSet Results, IReadOnlyList<string> Errors) Run()
    {
        var (lib, tb) = CircuitSource.FromSchematic(SchematicPath());

        var netlist  = new Elaborator(lib).Elaborate(tb);
        var analysis = Assert.IsType<SParameterAnalysis>(Assert.Single(tb.Analyses));

        double[] freqs = analysis.Sweeps.SelectMany(sw => sw.Expand()).Distinct().Order().ToArray();
        var ds = SParameterEngine.Run(netlist, freqs);
        var results = new Dictionary<string, DataSet> { [analysis.Name] = ds };

        var run    = new DataSet();
        foreach (var (name, cube) in ds.Cubes) run.Add(name, cube);
        var errors = new MeasurementEvaluator(tb, netlist, results).EvaluateInto(run);
        return (run, errors);
    }

    /// <summary>
    /// <b>k is 0.5 at every frequency in the sweep</b> — the property that makes it a coupling factor
    /// rather than a transmission magnitude. Flatness is asserted as well as the value, because a
    /// quantity that is only right at one frequency is the failure being fixed.
    /// </summary>
    [Fact]
    public void TheCouplingFactorIsFlatAtOneHalf_AcrossTheWholeSweep()
    {
        var (run, errors) = Run();
        Assert.True(errors.Count == 0, string.Join("\n", errors));

        Assert.True(run.Contains("k"),
            "the example publishes no 'k' — every measurement it does publish: " +
            string.Join(", ", run.Cubes.Select(c => c.Key)));

        var k = run["k"];
        Assert.Equal(DataKind.Real, k.DataKind);
        Assert.True(k.RealValues.Length >= 90, $"expected the full sweep, got {k.RealValues.Length} point(s)");

        output.WriteLine($"k over {k.RealValues.Length} points: " +
                         $"min {k.RealValues.Min():F9}, max {k.RealValues.Max():F9}");

        Assert.All(k.RealValues, v => Assert.Equal(ExpectedK, v, 9));
    }

    /// <summary>
    /// The extraction the coupling factor is built on, checked against the component values on the
    /// schematic. The published rows are REACTANCES (ωL, in ohms) because a measurement expression has
    /// no frequency accessor to divide by — so the inductance is recovered here, which is the same
    /// division the example's own comment tells a reader to do.
    /// </summary>
    [Fact]
    public void TheExtractedSelfAndMutualInductances_AreTheValuesOnTheSchematic()
    {
        var (run, errors) = Run();
        Assert.True(errors.Count == 0, string.Join("\n", errors));

        double[] f = run["S"].Axes.Single(a => a.Name == "freq").Values;

        foreach (var (name, expected) in ((string, double)[])
                 [("wL1", ExpectedL), ("wL2", ExpectedL), ("wM", ExpectedM)])
        {
            Assert.True(run.Contains(name), $"the example publishes no '{name}'");
            double[] v = run[name].RealValues;
            Assert.Equal(f.Length, v.Length);

            for (int i = 0; i < v.Length; i++)
                Assert.Equal(expected, v[i] / (2 * Math.PI * f[i]), 15);
        }
    }

    /// <summary>
    /// <b>The old expression is gone, not merely renamed.</b> <c>mag(S21)</c> is a legitimate thing to
    /// plot and the example still plots it, as <c>S21_dB</c> — what must not come back is a row CALLED
    /// a coupling factor that holds it. Asserted against the numbers rather than the text: on this
    /// circuit |S21| spans more than an order of magnitude over the sweep, so anything flat at 0.5
    /// cannot be it.
    /// </summary>
    [Fact]
    public void MagS21_IsNotWhatKHolds()
    {
        var (run, _) = Run();

        var s21 = run.S(2, 1).Mag().RealValues;
        Assert.True(s21.Max() / s21.Min() > 10,
            "this circuit no longer has the |S21| spread the test rests on");

        Assert.All(run["k"].RealValues, v => Assert.Equal(ExpectedK, v, 9));
    }
}
