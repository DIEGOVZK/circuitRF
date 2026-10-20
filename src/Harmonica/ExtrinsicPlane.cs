using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;

namespace CircuitRF.Harmonica;

/// <summary>§7.3's two planes. The DCIV family and the loadline are always drawn in the same one.</summary>
public enum LoadlinePlane
{
    /// <summary>§4.5 — the device's own ports, conduction current only.</summary>
    Intrinsic,

    /// <summary>brief-modelcomposer-24 R-mc24-1 — the DUT's own terminals (inside any package and embedding), total
    /// terminal current.</summary>
    Extrinsic,
}

/// <summary>
/// brief-modelcomposer-24 — the DUT's own terminals at a converged operating point: what the extrinsic loadline is drawn
/// from (R-mc24-1/2/3).
///
/// <para><b>What "extrinsic" is.</b> The DUT's terminals, the plane <see cref="HarmonicaNetlist"/> calls the package
/// plane: inside any <c>LumpedPackage</c> and embedding, outside everything the DUT itself holds. For a <c>.cmodel</c>
/// that is the plane its measurements were taken at — after the pads — so its Rg/Rd/Rs, Lg/Ld/Ls and pad capacitances sit
/// between this plane and the intrinsic one. The voltage is <c>V(drain) − V(source)</c> there, and the current is the
/// TOTAL drain-terminal current: conduction, displacement and whatever the DUT's own network carries.</para>
///
/// <para><b>How the terminals are recovered.</b> The pre-terminated interface (§6.2) keeps only the device-facing nodes
/// and the two termination planes, and the DUT's terminals are neither, in general. So a second netlist is elaborated
/// with a zero-volt <c>IProbe</c> in series with the DUT's gate and drain
/// (<see cref="HarmonicaNetlist.Build"/>'s <c>terminalProbes</c>), and every terminal quantity is read off it as a linear
/// function of what the solve already knows: the converged device injection at each device-facing node, and the current
/// each termination delivers into its plane (<see cref="InterfaceNetwork.PlaneState"/>). Per harmonic that is</para>
/// <code>
///   Q(k) = Q₀(k) + Σ_device r_Q[j]·I_nl[j](k) − Σ_plane r_Q[j]·(I_del[j](k) + V_plane[j](k)/Z₀)
///   r_Q[j] = −(G⁻¹)[Q, node_j]
/// </code>
/// <para>with <c>r_Q</c> one row of the probe netlist's own inverse per quantity (<see cref="HbLinearExtractor.ControlSensitivityRow"/>,
/// computed once per structure) and <c>Q₀</c> what the bias supplies alone produce (recomputed only when the bias moves).
/// A frame therefore costs a product per harmonic on the solve already done — never another solve (R-mc24-6).</para>
///
/// <para><b>Why each plane carries a reference <c>Z₀</c> to ground.</b> Left open, a termination plane reaches the rest of
/// the circuit only through the ideal bias choke — ~10 GΩ at the fundamental with the default 1 H — so <c>G⁻¹</c> is
/// enormous there and the recovered voltage is the difference of two nearly equal large terms: measured, 4e-3 relative
/// on a bare native FET, against 1e-10 with the 1 µH choke the composed fixtures use. <see cref="InterfaceNetwork"/>'s
/// remarks describe the same trap. So the probe netlist carries <c>R = Z₀</c> from each plane to ground, and the plane is
/// injected with what the termination delivers PLUS the current that resistor draws at the plane voltage the solve
/// already knows. By superposition that is the same circuit, and it is a well-conditioned one.</para>
///
/// <para><b>Why not probe the netlist harmonicaRF solves.</b> The probes change no answer, but they are a different
/// matrix: the sparse ordering moves, and with it the last bits of every solve. The brief's own gate is that adding them
/// changes no published cube BIT FOR BIT, so the solve never sees them and this second netlist carries them instead. It
/// is elaborated lazily, once per structure, by the context that publishes — a pool worker solving a contour grid never
/// pays for it.</para>
/// </summary>
public sealed class ExtrinsicPlane
{
    /// <summary>The DUT's terminal spectra, <c>[harmonic]</c> each. Currents flow INTO the DUT; voltages are to ground.</summary>
    public sealed record TerminalSpectra(Complex[] GateVolts, Complex[] DrainVolts, Complex[] SourceVolts,
                                         Complex[] GateCurrent, Complex[] DrainCurrent)
    {
        /// <summary><c>Vds,ext = V(drain) − V(source)</c>, per harmonic.</summary>
        public Complex[] DrainSourceVolts => [.. DrainVolts.Select((v, h) => v - SourceVolts[h])];

        /// <summary><c>Vgs,ext = V(gate) − V(source)</c>, per harmonic.</summary>
        public Complex[] GateSourceVolts => [.. GateVolts.Select((v, h) => v - SourceVolts[h])];
    }

    private const int GateV = 0, DrainV = 1, SourceV = 2, GateI = 3, DrainI = 4, QuantityCount = 5;

    private readonly HbLinearExtractor _extractor;
    private readonly VdcModel?  _gateSupply;
    private readonly VdcModel?  _drainSupply;
    private readonly int        _k;
    private readonly double     _f0;
    private readonly double     _z0;
    private readonly int        _interfaceCount;
    private readonly int[]      _deviceIndex;   // probe-interface position → index into the solve's DeviceNodes, or −1
    private readonly int[]      _planeSide;     // probe-interface position → termination side, or −1
    private readonly int[]      _quantityRow;   // x index of each quantity, or −1 (ground)
    private readonly Complex[][][] _rows;       // [quantity][k][interface position]

    private Complex[][]? _c0;                   // [quantity][k]
    private (double Vgs, double Vds) _c0Bias;

    private ExtrinsicPlane(HbLinearExtractor extractor, VdcModel? gateSupply, VdcModel? drainSupply, int k, double f0,
                           double z0, int[] deviceIndex, int[] planeSide, int[] quantityRow, Complex[][][] rows)
    {
        _z0             = z0;
        _extractor      = extractor;
        _gateSupply     = gateSupply;
        _drainSupply    = drainSupply;
        _k              = k;
        _f0             = f0;
        _interfaceCount = deviceIndex.Length;
        _deviceIndex    = deviceIndex;
        _planeSide      = planeSide;
        _quantityRow    = quantityRow;
        _rows           = rows;
    }

    /// <summary>
    /// Elaborates the probe netlist for <paramref name="model"/> and extracts its rows. <paramref name="solveNetlist"/> and
    /// <paramref name="solveDeviceNodes"/> are the context's own, so each device-facing node is matched by NAME to the
    /// unknown the solve converged it as.
    /// </summary>
    public static ExtrinsicPlane Build(CircuitModel model, AnalysisSettings settings,
                                       ElaboratedNetlist solveNetlist, int[] solveDeviceNodes)
    {
        double z0 = model.Settings.Z0;
        string reference = $"R:{ReferenceLoad}S  {HarmonicaNetlist.SourcePlane} 0  R={HarmonicaNetlist.Num(z0)}\n" +
                           $"R:{ReferenceLoad}L  {HarmonicaNetlist.LoadPlane} 0  R={HarmonicaNetlist.Num(z0)}";
        var (lib, tb) = new CnlReader().Read(HarmonicaNetlist.Build(model, reference, terminalProbes: true).Text);
        var nl = new Elaborator(lib).Elaborate(tb);

        var gateProbe  = nl.Components.Single(c => c.InstancePath == HarmonicaNetlist.GateProbe);
        var drainProbe = nl.Components.Single(c => c.InstancePath == HarmonicaNetlist.DrainProbe);
        string gateNet  = nl.Nodes.NameOf(gateProbe.Nodes[0]);    // the package side, which the solve calls the gate
        string drainNet = nl.Nodes.NameOf(drainProbe.Nodes[0]);

        int srcPlane = nl.Nodes.IndexOf(HarmonicaNetlist.SourcePlane);
        int ldPlane  = nl.Nodes.IndexOf(HarmonicaNetlist.LoadPlane);
        var extractor = new HbLinearExtractor(nl, settings, [srcPlane, ldPlane]);
        int[] iface = extractor.InterfaceNodes;

        // Each interface position is a termination plane, a device-facing node the solve also has, or neither. A plane
        // wins: with no embedding the solve's source plane IS the gate, but here the gate sits behind its probe.
        var nonlinear  = nl.NonlinearNodes.ToHashSet();
        var deviceIdx  = new int[iface.Length];
        var planeSide  = new int[iface.Length];
        var matched    = new HashSet<int>();
        for (int j = 0; j < iface.Length; j++)
        {
            deviceIdx[j] = -1;
            planeSide[j] = iface[j] == srcPlane ? (int)TerminationSide.Source
                         : iface[j] == ldPlane  ? (int)TerminationSide.Load : -1;
            if (planeSide[j] >= 0 || !nonlinear.Contains(iface[j])) continue;

            string name = nl.Nodes.NameOf(iface[j]);
            string solveName = name == HarmonicaNetlist.DutGateNode  ? gateNet
                             : name == HarmonicaNetlist.DutDrainNode ? drainNet : name;
            if (solveNetlist.Nodes.TryGetIndex(solveName, out int solveNode)
                && Array.IndexOf(solveDeviceNodes, solveNode) is var pos and >= 0)
            {
                deviceIdx[j] = pos;
                matched.Add(pos);
            }
        }
        if (matched.Count != solveDeviceNodes.Length)
            throw new InvalidOperationException(
                "The extrinsic plane could not match every device-facing node of the solve to the probe netlist, so the " +
                "DUT's terminals cannot be recovered.");

        int k = model.Settings.HarmonicCount;
        double f0 = model.Settings.FrequencyHz;

        // Node rows first: the stamp they trigger is what numbers the probes' branches.
        var quantityRow = new int[QuantityCount];
        quantityRow[GateV]   = nl.Nodes.IndexOf(HarmonicaNetlist.DutGateNode) - 1;
        quantityRow[DrainV]  = nl.Nodes.IndexOf(HarmonicaNetlist.DutDrainNode) - 1;
        quantityRow[SourceV] = nl.Nodes.TryGetIndex(HarmonicaNetlist.SourceTerminal, out int s) ? s - 1 : -1;

        var rows = new Complex[QuantityCount][][];
        for (int q = 0; q < QuantityCount; q++) rows[q] = new Complex[k + 1][];
        for (int h = 0; h <= k; h++)
            foreach (int q in (int[])[GateV, DrainV, SourceV])
                rows[q][h] = quantityRow[q] >= 0
                    ? extractor.ControlSensitivityRow(Omega(h, f0), quantityRow[q])
                    : new Complex[iface.Length];

        quantityRow[GateI]  = ((SeriesProbeModelBase)gateProbe.Model).LastBranchIndex;
        quantityRow[DrainI] = ((SeriesProbeModelBase)drainProbe.Model).LastBranchIndex;
        for (int h = 0; h <= k; h++)
            foreach (int q in (int[])[GateI, DrainI])
                rows[q][h] = extractor.ControlSensitivityRow(Omega(h, f0), quantityRow[q]);

        VdcModel? Supply(string name)
            => nl.Components.FirstOrDefault(c => c.InstancePath == name)?.Model as VdcModel;

        return new ExtrinsicPlane(extractor, Supply(HarmonicaNetlist.GateSupply), Supply(HarmonicaNetlist.DrainSupply),
                                  k, f0, z0, deviceIdx, planeSide, quantityRow, rows);
    }

    /// <summary>The reference resistors' instance-name stem (see the class remarks).</summary>
    private const string ReferenceLoad = "RXREF";

    /// <summary>The same ω every extraction of this structure uses, so the factorisation cache keys agree.</summary>
    private static double Omega(int h, double f0) => h * 2.0 * Math.PI * f0;

    /// <summary>
    /// The DUT's terminals at <paramref name="point"/>. <paramref name="planeVolts"/> and <paramref name="deliveredCurrent"/>
    /// are <see cref="InterfaceNetwork.PlaneState"/> at the same point, <c>[side, harmonic]</c>.
    /// <paramref name="vgs"/>/<paramref name="vds"/> are the supplies the point was solved at.
    /// </summary>
    public TerminalSpectra Recover(OperatingPoint point, Complex[,] planeVolts, Complex[,] deliveredCurrent,
                                   double vgs, double vds)
    {
        EnsureBias(vgs, vds);
        var outQ = new Complex[QuantityCount][];
        for (int q = 0; q < QuantityCount; q++)
        {
            var spec = new Complex[_k + 1];
            for (int h = 0; h <= _k; h++)
            {
                var r = _rows[q][h];
                Complex v = _c0![q][h];
                for (int j = 0; j < _interfaceCount; j++)
                {
                    if (_deviceIndex[j] >= 0)    v += r[j] * point.INlTotal[_deviceIndex[j], h];
                    else if (_planeSide[j] >= 0)
                        v -= r[j] * (deliveredCurrent[_planeSide[j], h] + planeVolts[_planeSide[j], h] / _z0);
                }
                spec[h] = _quantityRow[q] >= 0 ? v : Complex.Zero;
            }
            outQ[q] = spec;
        }
        return new TerminalSpectra(outQ[GateV], outQ[DrainV], outQ[SourceV], outQ[GateI], outQ[DrainI]);
    }

    /// <summary>
    /// R-mc24-3 — the extrinsic loadline: <c>Vds,ext(t)</c> against <c>Id,ext(t)</c> at <paramref name="sampleCount"/>
    /// points over one period, by the same direct sum <see cref="IntrinsicPlane.Loadline"/> uses (R-h9b-13).
    /// </summary>
    public static (double[] Vds, double[] Id) Loadline(TerminalSpectra t, int sampleCount)
    {
        int k = t.DrainCurrent.Length - 1;
        return (IntrinsicPlane.ResampleSpectrum(t.DrainSourceVolts, k, sampleCount),
                IntrinsicPlane.ResampleSpectrum(t.DrainCurrent, k, sampleCount));
    }

    /// <summary><c>Q₀</c> — what the bias supplies alone produce at each quantity, at the bias the point was solved at.</summary>
    private void EnsureBias(double vgs, double vds)
    {
        if (_c0 is not null && _c0Bias == (vgs, vds)) return;
        _gateSupply?.SetVdc(vgs);
        _drainSupply?.SetVdc(vds);

        var c0 = new Complex[QuantityCount][];
        for (int q = 0; q < QuantityCount; q++) c0[q] = new Complex[_k + 1];
        var none = new Complex[_interfaceCount];
        for (int h = 0; h <= _k; h++)
        {
            double w = Omega(h, _f0);
            var x = _extractor.SolveFullNetwork(w, none, _extractor.BuildSourceRhs(w));
            for (int q = 0; q < QuantityCount; q++)
                if (_quantityRow[q] >= 0) c0[q][h] = x[_quantityRow[q]];
        }
        _c0 = c0;
        _c0Bias = (vgs, vds);
    }
}
