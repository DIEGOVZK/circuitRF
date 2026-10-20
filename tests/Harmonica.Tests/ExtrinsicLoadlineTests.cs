using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Harmonica.Tests.Support;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// brief-modelcomposer-24 — the loadline at the DUT's own terminals. The recovered terminal spectra are held against a
/// route that shares none of their code (a full <c>HbEngine</c> solve of the probed netlist), the solve is shown to be
/// untouched by the probes, and on a native FET the two planes are shown to differ by exactly the device's own <c>jωq</c>.
/// </summary>
public sealed class ExtrinsicLoadlineTests(ITestOutputHelper output)
{
    private static string N(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

    private static readonly LumpedPackage Package = new()
    {
        Rg = 1.5, Lg = 60e-12, Rd = 0.8, Ld = 70e-12, Rs = 0.2, Ls = 8e-12, Cpg = 0.15e-12, Cpd = 0.12e-12,
    };

    /// <summary>
    /// On the <c>synthetic-gan</c> <c>.cmodel</c> behind a package, the recovered <c>V(drain) − V(source)</c> and drain-terminal
    /// current equal, at every harmonic, a full <c>HbEngine</c> solve of the same netlist with the probes in it and the
    /// terminations stamped — its back-solved node voltages and the probe's own branch current.
    /// </summary>
    [Fact]
    public void TerminalSpectra_EqualAFullHbEngineSolve_OfTheProbedNetlist()
    {
        var model = ComposedFixtures.Model(comparable: true) with
        {
            Embedding = new EmbeddingStack { Package = Package },
        };
        int k = model.Settings.HarmonicCount;
        var terms = ComposedDutTests.Terminations(k);
        const double pavl = 14.0;

        var ctx = HarmonicaContext.Create(model, ComposedFixtures.Comparable);
        var point = ctx.Solve(terms, pavl);
        Assert.True(point.Residual < 1e-7, $"‖F‖ {point.Residual:E2}");
        var t = ctx.ExtrinsicTerminals(point, terms);

        var ds = OracleRun(model, terms, HarmonicaContext.DriveVolts(terms, pavl));
        var vd = Row(ds["V"], HarmonicaNetlist.DutDrainNode);
        var vs = Row(ds["V"], HarmonicaNetlist.SourceTerminal);
        var id = Row(ds["I"], HarmonicaNetlist.DrainProbe);

        var vds = t.DrainSourceVolts;
        double vScale = vd.Max(c => c.Magnitude), iScale = id.Max(c => c.Magnitude);
        double worstV = 0, worstI = 0;
        for (int h = 0; h <= k; h++)
        {
            worstV = Math.Max(worstV, (vds[h] - (vd[h] - vs[h])).Magnitude / vScale);
            worstI = Math.Max(worstI, (t.DrainCurrent[h] - id[h]).Magnitude / iScale);
            output.WriteLine($"h={h}  Vds,ext {vds[h]:G8} vs {vd[h] - vs[h]:G8}   Id,ext {t.DrainCurrent[h]:G8} vs {id[h]:G8}");
        }
        output.WriteLine($"worst: Vds {worstV:E2}, Id {worstI:E2} (relative)");
        Assert.True(worstV <= 1e-9, $"Vds,ext: {worstV:E3}");
        Assert.True(worstI <= 1e-9, $"Id,ext: {worstI:E3}");

        // The recovery read the package: the extrinsic and intrinsic drain voltages are not the same quantity here.
        var spectra = IntrinsicPlane.Evaluate(ctx.DutDevice, point.V, ctx.Interface.DeviceNodes, k,
                                              HbFft.GridSize(k, model.Settings.FftOverSample), ComposedFixtures.F0);
        Assert.True((spectra.portVoltages[1, 1] - vds[1]).Magnitude > 1e-3 * vScale);
    }

    /// <summary>
    /// The probes live in a second netlist and never in the one harmonicaRF solves, so every cube published before this
    /// phase — the spectra, the intrinsic plane, the plane state the FOMs are read from — is bit-identical between a
    /// context that built the extrinsic recovery and one that never did.
    /// </summary>
    [Fact]
    public void TheProbes_ChangeNoPublishedCube()
    {
        var model = ComposedFixtures.Model() with { Embedding = new EmbeddingStack { Package = Package } };
        int k = model.Settings.HarmonicCount;
        var terms = ComposedDutTests.Terminations(k);

        var probed = HarmonicaContext.Create(model);
        Assert.Equal(HarmonicaNetlist.Build(model).Text, probed.NetlistText);
        Assert.DoesNotContain("IProbe", probed.NetlistText, StringComparison.Ordinal);
        var withRecovery = HarmonicaDataSet.Build(probed, probed.Solve(terms, 14.0), terms);
        Assert.Equal(1, probed.ExtrinsicBuildCount);
        Assert.True(withRecovery.Contains("Vds_ext_t") && withRecovery.Contains("Ids_ext_t"));

        // A context that never builds the recovery: the same solve, the same intrinsic evaluation, the same plane state.
        var plain = HarmonicaContext.Create(model);
        var p = plain.Solve(terms, 14.0);
        var spectra = IntrinsicPlane.Evaluate(plain.DutDevice, p.V, plain.Interface.DeviceNodes, k,
                                              HbFft.GridSize(k, model.Settings.FftOverSample), model.Settings.FrequencyHz);
        var (planeV, planeI) = plain.Interface.PlaneState(terms, HarmonicaContext.DriveVolts(terms, 14.0), p.INlTotal,
                                                          model.Settings.DcBlockFarads);
        Assert.Equal(0, plain.ExtrinsicBuildCount);

        AssertSame(p.V, withRecovery["V"]);
        AssertSame(p.INl, withRecovery["INl"]);
        AssertSame(spectra.portVoltages, withRecovery["V_intr"]);
        AssertSame(spectra.portCurrents, withRecovery["I_intr"]);
        AssertSame(planeV, withRecovery["V_ext"]);
        AssertSame(planeI, withRecovery["Iin"]);

        // Pout and DE come from the drive-up, which the recovery never touches; the first context's is run after it built.
        var a = PinSearch.Sweep(probed, terms, 0.0, 16.0, 4.0);
        var b = PinSearch.Sweep(plain,  terms, 0.0, 16.0, 4.0);
        Assert.Equal(b.Steps.Select(s => s.PoutW), a.Steps.Select(s => s.PoutW));
        Assert.Equal(b.Steps.Select(s => s.De), a.Steps.Select(s => s.De));
    }

    /// <summary>
    /// A native FET with no package: with charge off the two planes draw the same loadline, and with charge on the
    /// extrinsic drain current exceeds the intrinsic conduction current by exactly the device's own <c>jωq</c>, harmonic
    /// by harmonic (§4.5 consequence 1). The voltages coincide either way.
    ///
    /// <para>At DC they coincide to the bias choke's regularisation and no further: the solve's open-port extraction adds
    /// <c>InductanceRegR</c> (1 µΩ) in series with each choke at ω = 0 and the full-network stamp the recovery reads does
    /// not, so the two DC drain voltages differ by <c>Idc · InductanceRegR</c> — ~1e-7 V here, against every RF harmonic's
    /// 1e-9 relative.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeFet_PlanesDifferByTheDisplacementCurrentAlone(bool charge)
    {
        var model = new CircuitModel
        {
            Dut = new DutSpec
            {
                Kind = DutKind.NativeFet, TypeName = "FET_Angelov",
                Parameters = charge
                    ? new Dictionary<string, string> { ["Cgs"] = "1.2e-12", ["Cgd"] = "0.15e-12" }
                    : new Dictionary<string, string> { ["CapModel"] = "0" },
            },
            Bias = new BiasSpec { Vgs = -0.4, Vds = 10.0 },
            // Driven to its floor: the recovery reads the linear network at the point the solve stopped, so a residual
            // left at the default tolerance (~1e-8 A) shows up as ~1e-8 of Vds.
            Settings = new HarmonicaSettings { HarmonicCount = 3, FrequencyHz = 2e9, Tol = 1e-14, MaxIter = 30 },
        };
        int k = model.Settings.HarmonicCount;
        var terms = ComposedDutTests.Terminations(k);
        var ctx = HarmonicaContext.Create(model);
        var point = ctx.Solve(terms, 10.0);
        Assert.True(point.Residual < 1e-8, $"‖F‖ {point.Residual:E2}");

        var spectra = IntrinsicPlane.Evaluate(ctx.DutDevice, point.V, ctx.Interface.DeviceNodes, k,
                                              HbFft.GridSize(k, model.Settings.FftOverSample), model.Settings.FrequencyHz);
        var t = ctx.ExtrinsicTerminals(point, terms);
        var vds = t.DrainSourceVolts;

        double iScale = Enumerable.Range(0, k + 1).Max(h => spectra.portCurrents[1, h].Magnitude);
        double vScale = Enumerable.Range(0, k + 1).Max(h => spectra.portVoltages[1, h].Magnitude);
        double regDrop = HarmonicaContext.DefaultSettings(model).InductanceRegR * t.DrainCurrent[0].Magnitude;
        double worstV = 0, worstI = 0, displacement = 0;
        for (int h = 0; h <= k; h++)
        {
            Complex expected = spectra.portCurrents[1, h] + spectra.portChargeCurrents[1, h];
            double dv = (vds[h] - spectra.portVoltages[1, h]).Magnitude - (h == 0 ? 2 * regDrop : 0.0);
            worstV = Math.Max(worstV, dv / vScale);
            worstI = Math.Max(worstI, (t.DrainCurrent[h] - expected).Magnitude / iScale);
            displacement = Math.Max(displacement, spectra.portChargeCurrents[1, h].Magnitude / iScale);
        }
        output.WriteLine($"charge {charge}: Vds {worstV:E2} (DC less 2·Idc·Rreg = {2 * regDrop:E2} V), " +
                         $"Id − (i + jωq) {worstI:E2}, |jωq| {displacement:E2} (relative)");
        Assert.True(worstV <= 1e-9, $"Vds: {worstV:E3}");
        Assert.True(worstI <= 1e-9, $"Id: {worstI:E3}");
        if (charge) Assert.True(displacement > 1e-3, "charge on: the two currents are not the same quantity");
        else        Assert.Equal(0.0, displacement);

        if (!charge)
        {
            // Charge off: the loadlines coincide sample by sample, as K-harmonic loops. The intrinsic panel's own curve
            // evaluates i(v(t)) pointwise and so also carries the current's harmonics above K, which the circuit — and
            // so the terminal — never does; that difference is reported, not asserted.
            int n = model.Settings.LoadlineSamples;
            var (ve, ie) = ExtrinsicPlane.Loadline(t, n);
            var none = new Complex[k + 1];
            var (vi, ii) = ExtrinsicPlane.Loadline(new ExtrinsicPlane.TerminalSpectra(
                none, Port(spectra.portVoltages, 1), none, none, Port(spectra.portCurrents, 1)), n);
            double iMax = ii.Max(Math.Abs), vMax = vi.Max(Math.Abs);
            for (int s = 0; s < n; s++)
            {
                Assert.True(Math.Abs(ve[s] - vi[s]) <= 1e-9 * vMax + 2 * regDrop, $"Vds at sample {s}");
                Assert.True(Math.Abs(ie[s] - ii[s]) <= 1e-9 * iMax, $"Id at sample {s}");
            }
            var (_, pointwise) = IntrinsicPlane.Loadline(ctx.DutDevice, point.V, ctx.Interface.DeviceNodes, k, n);
            output.WriteLine($"harmonics above K in the pointwise intrinsic current: " +
                             $"{pointwise.Zip(ii, (a, b) => Math.Abs(a - b)).Max() / iMax:E2} of its peak");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Complex[] Port(Complex[,] spectra, int port)
        => [.. Enumerable.Range(0, spectra.GetLength(1)).Select(h => spectra[port, h])];

    private static void AssertSame(Complex[,] want, DataCube got)
    {
        var flat = got.ComplexValues;
        int c = want.GetLength(1);
        for (int i = 0; i < want.GetLength(0); i++)
            for (int j = 0; j < c; j++)
                Assert.Equal(want[i, j], flat[i * c + j]);
    }

    /// <summary>One labelled row of a <c>[label, harmonic]</c> cube.</summary>
    private static Complex[] Row(DataCube cube, string label)
    {
        var labels = cube.Axes[0].Labels ?? throw new InvalidOperationException("unlabelled axis");
        int i = Array.IndexOf(labels, label);
        Assert.True(i >= 0, $"'{label}' is not on the cube's {cube.Axes[0].Name} axis");
        int c = cube.Axes[1].Values.Length;
        return [.. cube.ComplexValues.Skip(i * c).Take(c)];
    }

    /// <summary>The probed netlist with the terminations stamped (ComposedDutTests' oracle), solved by HbEngine.</summary>
    private static DataSet OracleRun(CircuitModel model, TerminationSet terms, double drive)
    {
        int k = model.Settings.HarmonicCount;
        double f0 = model.Settings.FrequencyHz;
        var lines = new List<string>();
        for (int side = 0; side < 2; side++)
        {
            string plane = side == 0 ? HarmonicaNetlist.SourcePlane : HarmonicaNetlist.LoadPlane;
            string tag   = side == 0 ? "S" : "L";
            var expr = new System.Text.StringBuilder();
            for (int h = 1; h <= k; h++)
            {
                Complex z = terms.Z((TerminationSide)side, h);
                expr.Append($"if(freq<{N((h + 0.5) * f0)},complex({N(z.Real)},{N(z.Imaginary)}),");
            }
            expr.Append("complex(1e-6,0)").Append(new string(')', k));
            lines.Add($"C:CBLK{tag}  {plane} n_zt{tag}  C={N(model.Settings.DcBlockFarads)}");
            lines.Add(side == 0
                ? $"Z_Port:ZT{tag}  n_zt{tag} n_zdrv  Z[1,1]={expr}"
                : $"Z_Port:ZT{tag}  n_zt{tag} 0  Z[1,1]={expr}");
        }
        lines.Add($"V_1Tone:VDRV  n_zdrv 0  Freq={N(f0)}  V={N(drive)}  Phase=0");
        lines.Add($"analysis HB1 type=hb Tone={N(f0)} MaxHarm={k} Tol={N(model.Settings.Tol)} MaxIter={model.Settings.MaxIter}");

        var text = HarmonicaNetlist.Build(model, string.Join('\n', lines), terminalProbes: true).Text;
        var (lib, tb) = new CnlReader().Read(text);
        var nl = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
        var engine = new HbEngine(nl, tb, ComposedFixtures.Comparable);
        return engine.Run(HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit)).DataSet;
    }
}
