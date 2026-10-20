using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Harmonica.Tests.Support;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// brief-modelcomposer-23 — a <c>.cmodel</c> as harmonicaRF's DUT, read at its intrinsic plane, on the example's
/// <c>synthetic-gan</c> model (two-shell network, a drain-lag trap node, self-heating). Each intrinsic reader is held
/// against a route that does not share its code: a full <c>HbEngine</c> solve, a finite-difference Jacobian, and the DC
/// engine on a bench of the core alone.
/// </summary>
public sealed class ComposedDutTests(ITestOutputHelper output)
{
    private static string N(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

    // ── the netlist, the device and the port map ─────────────────────────────

    [Fact]
    public void TheNetlistLine_RoundTripsAndElaborates_AndTheDeviceIsTheCore()
    {
        var switches = ComposedSwitches.Default.With(ComposedSwitches.LinearCName, true).With(ComposedSwitches.DrainLagName, false);
        var model = ComposedFixtures.Model(switches: switches, fingers: 8, m: 2);
        string text = HarmonicaNetlist.Build(model).Text;

        string line = text.Split('\n').Single(l => l.StartsWith("ComposedFet:DUT", StringComparison.Ordinal)).Trim();
        // With no package the gate and drain ARE the termination planes (HarmonicaNetlist.Series' own rule).
        Assert.Equal($"ComposedFet:DUT  n_srcterm n_ldterm 0  File=\"{ComposedFixtures.ExampleModel}\"  N=8  m=2  LinearC=true  DrainLag=false", line);

        // Read back, the instance carries exactly what the DutSpec says.
        var (lib, tb) = new CnlReader().Read(text);
        var inst = tb.Instances.Single(i => i.InstanceName == "DUT");
        Assert.Equal(new[] { "n_srcterm", "n_ldterm", "0" }, inst.NetBindings);
        Assert.Equal(switches, ComposedSwitches.Read(inst.Overrides, _ => throw new InvalidOperationException()));
        Assert.Equal("8", inst.Overrides.Single(o => o.Name == "N").Expression);

        using var nl = new Elaborator(lib).Elaborate(tb);
        Assert.Contains(nl.Components, c => c.InstancePath == HarmonicaContext.CoreInstancePath);

        var ctx = HarmonicaContext.Create(ComposedFixtures.Model());
        Assert.Equal(HarmonicaContext.CoreInstancePath, ctx.DutDevice.InstancePath);
        var core = Assert.IsType<ComposedCoreModel>(ctx.DutDevice.Model);
        Assert.True(core.PortCount > 2, "the core carries its trap and thermal ports past the two intrinsic ones");
        Assert.Equal(IntrinsicPortMap.TwoPort, ctx.IntrinsicPorts);
        Assert.True(ctx.IntrinsicPorts.LoadAvailable && ctx.IntrinsicPorts.SourceAvailable);
        Assert.Throws<InvalidOperationException>(() => ctx.DutComponent);
    }

    // ── intrinsic truth: harmonicaRF against a full HbEngine solve ───────────

    /// <summary>
    /// At one converged point, harmonicaRF's intrinsic drain voltage, conduction current and load impedance equal a full
    /// <c>HbEngine</c> solve of the same netlist — terminations stamped, drive a Thévenin source — read at the core's own
    /// ports with the conduction half from the core's own <c>i</c>.
    /// </summary>
    [Fact]
    public void IntrinsicLoadSide_EqualsAFullHbEngineSolve_ReadAtTheCoresPorts()
    {
        var model = ComposedFixtures.Model(comparable: true);
        int k = model.Settings.HarmonicCount;
        int gridN = HbFft.GridSize(k, model.Settings.FftOverSample);
        var terms = Terminations(k);
        const double pavl = 14.0;

        var ctx = HarmonicaContext.Create(model, ComposedFixtures.Comparable);
        var point = ctx.Solve(terms, pavl);
        Assert.True(point.Residual < 1e-7, $"‖F‖ {point.Residual:E2}");
        var spectra = IntrinsicPlane.Evaluate(ctx.DutDevice, point.V, ctx.Interface.DeviceNodes, k, gridN, ComposedFixtures.F0);
        var zl = HarmonicaDataSet.Intrinsic(ctx, point, spectra, includeSource: false).Z;

        // The oracle: the SAME netlist, the terminations stamped behind their DC blocks, solved by HbEngine.
        double drive = HarmonicaContext.DriveVolts(terms, pavl);
        var (engine, p, nl) = Stamped(model, terms, drive);
        var run = engine.Run(p);
        Assert.NotNull(run.InterfaceV);
        var core = nl.Components.Single(c => c.InstancePath == HarmonicaContext.CoreInstancePath);
        // The interface the engine solved over is its extractor's — the same construction, asked for its node list.
        int[] ifNodes = new HbLinearExtractor(nl, ComposedFixtures.Comparable).InterfaceNodes;
        var (iRef, vRef) = ReadCore(core, run.InterfaceV!, ifNodes, k, gridN);

        double worstV = 0, worstI = 0, worstZ = 0;
        double vScale = Enumerable.Range(0, k + 1).Max(h => vRef[1, h].Magnitude);
        double iScale = Enumerable.Range(0, k + 1).Max(h => iRef[1, h].Magnitude);
        for (int h = 0; h <= k; h++)
        {
            worstV = Math.Max(worstV, (spectra.portVoltages[1, h] - vRef[1, h]).Magnitude / vScale);
            worstI = Math.Max(worstI, (spectra.portCurrents[1, h] - iRef[1, h]).Magnitude / iScale);
            Complex zRef = -vRef[1, h] / iRef[1, h];
            worstZ = Math.Max(worstZ, (zl[(int)TerminationSide.Load, h] - zRef).Magnitude / zRef.Magnitude);
        }
        output.WriteLine($"V_d {worstV:E2}  I_d {worstI:E2}  Z_L,intr {worstZ:E2} (relative)");
        Assert.True(worstV <= 1e-9, $"V_d,k: {worstV:E3}");
        Assert.True(worstI <= 1e-9, $"I_d,k: {worstI:E3}");
        Assert.True(worstZ <= 1e-9, $"Z_L,intr: {worstZ:E3}");
    }

    // ── J′ with the trap and thermal ports present ───────────────────────────

    /// <summary>
    /// The source-side intrinsic impedance (§4.5.3's <c>J′</c> route) with the core's trap and thermal ports in the
    /// unknown vector equals <c>bᵀ J′⁻¹ b</c> built independently: the interface admittance as extracted, plus a central
    /// difference of the nonlinear injection with the core's gate-port current and charge evaluated at their converged
    /// gate voltage — which is exactly the gate-port self block J′ removes, and nothing else.
    /// </summary>
    [Fact]
    public void SourceImpedance_WithStateAndThermalPorts_EqualsAPerturbationSolve()
    {
        var model = ComposedFixtures.Model(comparable: true);
        int k = model.Settings.HarmonicCount;
        int gridN = HbFft.GridSize(k, model.Settings.FftOverSample);
        var ctx = HarmonicaContext.Create(model, ComposedFixtures.Comparable);
        var point = ctx.Solve(Terminations(k), 14.0);
        Assert.True(point.Residual < 1e-7, $"‖F‖ {point.Residual:E2}");
        var core = ctx.DutDevice;
        Assert.True(core.Model.PortCount > 2);

        var zShipped = IntrinsicPlane.SourceImpedance(ctx, point);

        int n = ctx.Interface.InterfaceCount;
        int[] nodes = ctx.Interface.DeviceNodes;
        int gPlus = Array.IndexOf(nodes, core.Nodes[0]), gMinus = Array.IndexOf(nodes, core.Nodes[1]);
        double w0 = 2 * Math.PI * ComposedFixtures.F0;

        // Real-split unknowns: Re V[i,h] for every h, Im V[i,h] for h ≥ 1.
        var dofs = new List<(int Node, int H, bool Im)>();
        for (int i = 0; i < n; i++)
            for (int h = 0; h <= k; h++)
            {
                dofs.Add((i, h, false));
                if (h > 0) dofs.Add((i, h, true));
            }
        int m = dofs.Count;
        var index = dofs.Select((d, j) => (d, j)).ToDictionary(t => t.d, t => t.j);

        // The pinned gate waveform — what the gate-port self term is evaluated at.
        double[] gate0 = PortWave(point.V, gPlus, gMinus, k, gridN);

        double[] Injection(Complex[,] v)
        {
            var (iNl, qNl, _, _, buckets) = HbNewton.EvaluateNonlinear(v, n, k, gridN, ctx.Netlist, nodes);
            Assert.Empty(buckets);
            var f = new Complex[n, k + 1];
            for (int i = 0; i < n; i++)
                for (int h = 0; h <= k; h++)
                    f[i, h] = iNl[i, h] + new Complex(0, h * w0) * qNl[i, h];

            // Remove the gate port's dependence on its OWN voltage: its current and charge minus the same with the gate
            // port pinned. The difference is zero at the converged point and has exactly the self block as its derivative.
            var (iNow, qNow) = GatePortWave(core, v, nodes, k, gridN, null);
            var (iPin, qPin) = GatePortWave(core, v, nodes, k, gridN, gate0);
            var di = new double[gridN]; var dq = new double[gridN];
            for (int t = 0; t < gridN; t++) { di[t] = iNow[t] - iPin[t]; dq[t] = qNow[t] - qPin[t]; }
            HbFft.Forward(di, k, out var dI, out _);
            HbFft.Forward(dq, k, out var dQ, out _);
            for (int h = 0; h <= k; h++)
            {
                Complex s = dI[h] + new Complex(0, h * w0) * dQ[h];
                if (gPlus  >= 0) f[gPlus,  h] -= s;
                if (gMinus >= 0) f[gMinus, h] += s;
            }

            var r = new double[m];
            foreach (var ((node, h, im), j) in index) r[j] = im ? f[node, h].Imaginary : f[node, h].Real;
            return r;
        }

        // J′ = Y (exact, from the extraction) + a central difference of the nonlinear injection.
        var jp = new double[m, m];
        foreach (var ((node, h, im), col) in index)
        {
            double at  = im ? point.V[node, h].Imaginary : point.V[node, h].Real;
            double eps = 1e-6 * Math.Max(1.0, Math.Abs(at));
            var vp = (Complex[,])point.V.Clone(); var vm = (Complex[,])point.V.Clone();
            vp[node, h] += im ? new Complex(0, eps) : eps;
            vm[node, h] -= im ? new Complex(0, eps) : eps;
            var rp = Injection(vp); var rm = Injection(vm);
            for (int row = 0; row < m; row++) jp[row, col] = (rp[row] - rm[row]) / (2 * eps);
        }
        foreach (var ((row, h, rowIm), r) in index)
            for (int col = 0; col < n; col++)
            {
                Complex y = point.YNN[h][row, col];
                if (!rowIm) jp[r, index[(col, h, false)]] += y.Real;
                if (h == 0) continue;
                if (!rowIm) jp[r, index[(col, h, true)]] -= y.Imaginary;
                else
                {
                    jp[r, index[(col, h, false)]] += y.Imaginary;
                    jp[r, index[(col, h, true)]]  += y.Real;
                }
            }

        double worst = 0;
        for (int h = 0; h <= k; h++)
        {
            var b = new double[m];
            if (gPlus  >= 0) b[index[(gPlus,  h, false)]] += 1;
            if (gMinus >= 0) b[index[(gMinus, h, false)]] -= 1;
            var x = Solve(jp, b);
            double Part(int node, bool im) => node < 0 || (im && h == 0) ? 0 : x[index[(node, h, im)]];
            var z = new Complex(Part(gPlus, false) - Part(gMinus, false), Part(gPlus, true) - Part(gMinus, true));
            double rel = (z - zShipped[h, h]).Magnitude / z.Magnitude;
            output.WriteLine($"h={h}  J′ route {zShipped[h, h]:G6}  perturbation {z:G6}  rel {rel:E2}");
            worst = Math.Max(worst, rel);
        }
        Assert.True(worst <= 1e-6, $"Z_S,intr: {worst:E3}");
    }

    // ── the DCIV family against the DC engine ────────────────────────────────

    /// <summary>
    /// Three points of the family equal <c>NonlinearDcEngine</c> on a bench of the core alone — ideal sources at the
    /// intrinsic nodes, its own Foster ladder on the thermal node — so the trap state, the self-heated junction and the
    /// drain current are all the DC solve's, not the family's own Newton's.
    /// </summary>
    [Fact]
    public void TheDcivFamily_AtThreePoints_EqualsTheDcEngineOnTheCoreAlone()
    {
        var ctx = HarmonicaContext.Create(ComposedFixtures.Model());
        var key = new DcivFamily.Key(ctx.Model.StructuralKey, -1.5, 0.5, 3, 2.0, 20.0, 2, DrainPort: 1);
        var family = DcivFamily.Compute(ctx, key);

        // The thermal resistance the elaborated cell carries, read from its own resistors.
        double rth = ctx.Netlist.Components.Where(c => c.InstancePath.StartsWith("DUT.Rth", StringComparison.Ordinal))
                                           .Sum(c => c.Parameters["R"].AsReal());
        Assert.True(rth > 0, "the example self-heats");
        var core = Assert.IsType<ComposedCoreModel>(ctx.DutDevice.Model);

        (double Vgs, double Vds, double Id)[] points =
        [
            (family[0].Vgs, family[0].Vds[1], family[0].Ids[1]),
            (family[1].Vgs, family[1].Vds[0], family[1].Ids[0]),
            (family[2].Vgs, family[2].Vds[0], family[2].Ids[0]),
        ];
        foreach (var (vgs, vds, id) in points)
        {
            string bench = $"""
                Vdc:VG g 0 Vdc={N(vgs)}
                Vdc:VD d 0 Vdc={N(vds)}
                ComposedCore:C1 g d 0 t File="{ComposedFixtures.ExampleModel}" N={core.N}
                R:RTH t 0 R={N(rth)}
                """;
            var (lib, tb) = new CnlReader().Read(bench);
            using var nl = new Elaborator(lib).Elaborate(tb);
            // The DC engine's own default stops at 1e-6 A on the state nodes; this one is asked for its floor.
            var dc = NonlinearDcEngine.Run(nl, new AnalysisSettings
            {
                ConductanceRegularization = RegularizationMode.Never, NonlinearAbsTol = 1e-13,
            });
            Assert.True(dc.Converged, $"the DC bench at ({vgs}, {vds}) did not converge");
            var c1 = nl.Components.Single(c => c.InstancePath == "C1");
            var pv = new double[c1.Model.PortCount];
            for (int q = 0; q < pv.Length; q++)
                pv[q] = NodeV(dc.NodeVoltages, c1.Nodes[2 * q]) - NodeV(dc.NodeVoltages, c1.Nodes[2 * q + 1]);
            double want;
            using (ChargeReading.NotRead()) want = c1.Evaluate(new PortVoltages(pv)).I[1];

            double rel = Math.Abs(id - want) / Math.Abs(want);
            output.WriteLine($"Vgs {vgs:F2} Vds {vds:F1}: family {id:G10} A, DC engine {want:G10} A (ΔT {pv[^1]:F2} K), rel {rel:E2}");
            Assert.True(rel <= 1e-9, $"Id at ({vgs}, {vds}): {rel:E3}");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    internal static TerminationSet Terminations(int k)
    {
        var terms = new TerminationSet(k);
        terms.Set(TerminationSide.Source, 1, new Complex(20, 15));
        terms.Set(TerminationSide.Load,   1, new Complex(35, 12));
        terms.Set(TerminationSide.Load,   2, new Complex(2, -8));
        return terms;
    }

    /// <summary>The open-port netlist with the terminations stamped as <c>Z_Port</c>s behind DC blocks and the drive a
    /// Thévenin source in series with the source termination — SchurReterminationTests' oracle, solved by HbEngine.</summary>
    private static (HbEngine Engine, HbAnalysisParams P, ElaboratedNetlist Nl) Stamped(
        CircuitModel model, TerminationSet terms, double drive)
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

        var (lib, tb) = new CnlReader().Read(HarmonicaNetlist.Build(model, string.Join('\n', lines)).Text);
        var nl = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
        return (new HbEngine(nl, tb, ComposedFixtures.Comparable),
                HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit), nl);
    }

    /// <summary>The core's port voltages and its own conduction current over one period, transformed — the reader the
    /// oracle uses, written here rather than borrowed from IntrinsicPlane.</summary>
    private static (Complex[,] I, Complex[,] V) ReadCore(ElaboratedComponent core, Complex[,] v, int[] nodes, int k, int gridN)
    {
        int p = core.Model.PortCount;
        var wave = new double[p][];
        for (int q = 0; q < p; q++)
            wave[q] = PortWave(v, Array.IndexOf(nodes, core.Nodes[2 * q]), Array.IndexOf(nodes, core.Nodes[2 * q + 1]), k, gridN);

        var iT = new double[p][];
        for (int q = 0; q < p; q++) iT[q] = new double[gridN];
        for (int t = 0; t < gridN; t++)
        {
            var pv = new double[p];
            for (int q = 0; q < p; q++) pv[q] = wave[q][t];
            var res = core.Evaluate(new PortVoltages(pv));
            for (int q = 0; q < p; q++) iT[q][t] = res.I[q];
        }

        var iOut = new Complex[p, k + 1];
        var vOut = new Complex[p, k + 1];
        for (int q = 0; q < p; q++)
        {
            HbFft.Forward(iT[q], k, out var iS, out _);
            HbFft.Forward(wave[q], k, out var vS, out _);
            for (int h = 0; h <= k; h++) { iOut[q, h] = iS[h]; vOut[q, h] = vS[h]; }
        }
        return (iOut, vOut);
    }

    /// <summary>One port's voltage over one period, from interface spectra (an index of −1 is ground).</summary>
    private static double[] PortWave(Complex[,] v, int plus, int minus, int k, int gridN)
    {
        double[] Node(int i)
        {
            var w = new double[gridN];
            if (i < 0) return w;
            var spec = new Complex[k + 1];
            for (int h = 0; h <= k; h++) spec[h] = v[i, h];
            HbFft.Inverse(spec, k, w);
            return w;
        }
        var a = Node(plus); var b = Node(minus);
        for (int t = 0; t < gridN; t++) a[t] -= b[t];
        return a;
    }

    /// <summary>The core's gate-port current and charge over one period, with the gate port optionally pinned.</summary>
    private static (double[] I, double[] Q) GatePortWave(ElaboratedComponent core, Complex[,] v, int[] nodes, int k, int gridN,
                                                         double[]? pinnedGate)
    {
        int p = core.Model.PortCount;
        var wave = new double[p][];
        for (int q = 0; q < p; q++)
            wave[q] = PortWave(v, Array.IndexOf(nodes, core.Nodes[2 * q]), Array.IndexOf(nodes, core.Nodes[2 * q + 1]), k, gridN);
        var i = new double[gridN]; var qg = new double[gridN];
        for (int t = 0; t < gridN; t++)
        {
            var pv = new double[p];
            for (int q = 0; q < p; q++) pv[q] = wave[q][t];
            if (pinnedGate is not null) pv[0] = pinnedGate[t];
            var res = core.Evaluate(new PortVoltages(pv));
            i[t] = res.I[0]; qg[t] = res.Q[0];
        }
        return (i, qg);
    }

    private static double NodeV(double[] dc, int node) => node <= 0 ? 0.0 : dc[node - 1];

    private static double[] Solve(double[,] a0, double[] b0)
    {
        int n = b0.Length;
        var a = (double[,])a0.Clone(); var b = (double[])b0.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int i = c + 1; i < n; i++) if (Math.Abs(a[i, c]) > Math.Abs(a[piv, c])) piv = i;
            if (piv != c)
            {
                for (int j = 0; j < n; j++) (a[c, j], a[piv, j]) = (a[piv, j], a[c, j]);
                (b[c], b[piv]) = (b[piv], b[c]);
            }
            for (int i = c + 1; i < n; i++)
            {
                double f = a[i, c] / a[c, c];
                if (f == 0) continue;
                for (int j = c; j < n; j++) a[i, j] -= f * a[c, j];
                b[i] -= f * b[c];
            }
        }
        var x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = b[i];
            for (int j = i + 1; j < n; j++) s -= a[i, j] * x[j];
            x[i] = s / a[i, i];
        }
        return x;
    }
}
