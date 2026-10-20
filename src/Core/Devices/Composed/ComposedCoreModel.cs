using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// The nonlinear core of a composed FET (D3, R-mc1-6): <c>Ig</c>, <c>Id</c>, <c>Qg</c> and <c>Qd</c> of the
/// intrinsic device on the nets <c>gi di si</c>, scaled by <c>N/N0</c>.
///
/// <para><b>Ports.</b> Two, sharing the source: port 0 = (gate, source), port 1 = (drain, source), so
/// <c>v[0]</c> is <c>vgs</c> and <c>v[1]</c> is <c>vds</c> — <see cref="Fet.FetModelBase"/>'s
/// arrangement, and the elaborator expands the three nets the same way.</para>
///
/// <para><b>Currents.</b> The gate current is <c>IgGs + IgGd</c>, the two branches of [P1] Fig. 6.30;
/// the gate–drain branch leaves through the drain, so the drain terminal carries
/// <c>Id − IgGd</c>.</para>
///
/// <para><b>Charges.</b> <c>q = (Qg, Qd)</c> and <c>dc = ∂q/∂v</c> come from the same evaluation, so the
/// model is terminal-charge-conserving by construction (D7) — <c>Qs = −Qg − Qd</c> is the KCL the
/// engine already writes. A transcapacitance (<c>∂Qd/∂vgs ≠ ∂Qg/∂vds</c>) is kept, not symmetrised. A drift
/// decomposition (MC-8, <see cref="DriftCharges"/>) reads the channel current of the same evaluation.</para>
///
/// <para><b>The internal-node seam</b> used by the thermal, trap and dispersion phases:
/// <see cref="InternalStateNodeCount"/> ground-referenced nodes, minted by the elaborator after the two
/// intrinsic ports as ordinary global unknowns — never eliminated locally, because their harmonic
/// content matters in HB.</para>
///
/// <para><b>DC/RF dispersion</b> (R-mc7-4, [P1] eq. 6.52) is the first user: one state node <c>x</c>, port
/// 2, whose voltage is a current in amps. The drain carries <c>IdRF + x</c>, and the node's own port
/// carries <c>i = x − (IdDC − IdRF)</c> with <c>q = τ·x</c>, so KCL there is
/// <c>τ dx/dt + x = IdDC − IdRF</c>. At DC <c>x = IdDC − IdRF</c> and the drain carries <c>IdDC</c>; far
/// above 1/τ, <c>x</c> cannot follow and the small-signal drain current is <c>IdRF</c>'s. Both currents
/// are taken in the terminal frame (after the drain–source image), so <c>x</c> means the same on either
/// side of <c>vds = 0</c>. The node is unscaled — <c>N/N0</c> multiplies the drain current it feeds.</para>
///
/// <para><b>Self-heating</b> (MC-9 R-mc9-2, [P1] §6.6.2) adds a fourth net, the thermal node, as the last port
/// <c>(t, 0)</c>: its voltage is the temperature rise ΔT and every constituent is evaluated at
/// <c>T = temp + ΔT</c>. The port carries <c>−P</c>, the dissipated power <c>Id·vds + IgGs·vgs + IgGd·vgd</c>
/// ([P1] eq. 6.67) in its terminal form <c>ig·vgs + id·vds</c>, so the power flows INTO the node, where the
/// synthesised cell's <c>Rth ∥ Cth</c> to ground closes [P1] eq. 6.67. It is not a minted unknown: the cell owns the
/// net, which is what lets an instance's fourth pin be it. <c>P</c> is the scaled (whole-device) power, and
/// <c>Rth</c> scales by <c>N0/N</c>, so ΔT does not depend on <c>N</c>.</para>
/// </summary>
public sealed class ComposedCoreModel : ComponentModel
{
    private readonly ComposedCoreProgram _p;
    private readonly double _scale;
    private readonly double _temperatureC;
    private readonly bool   _hasT;
    private readonly bool   _exchange;
    private readonly bool   _dispersive;
    private readonly double _tau;
    private readonly bool   _thermal;

    /// <summary>The model this core evaluates.</summary>
    public LoadedComposedModel Loaded { get; }

    /// <summary>The instance's finger count.</summary>
    public int N { get; }

    /// <summary>The temperature the constituents are evaluated at, °C — the run's ambient clamped to
    /// the model's range, or the model's own <c>T0</c> for a single-temperature model.</summary>
    public double TemperatureC => _temperatureC;

    public ComposedCoreModel(LoadedComposedModel loaded, int n, double ambientC)
    {
        Loaded = loaded;
        N      = n;
        _p     = loaded.Program;
        _scale = (double)n / loaded.N0;
        _exchange = _p.Symmetry == DrainSourceSymmetry.Exchange;
        _tau = _p.DispersionTau ?? 0.0;
        _dispersive = _p.IdRF is not null;
        _thermal = loaded.Model.Thermal is not null;

        var t = loaded.Model.Temperature;
        _hasT = t.Mode == TemperatureMode.Range;
        _temperatureC = _hasT ? Math.Clamp(ambientC, t.Tmin, t.Tmax) : t.T0;
    }

    public override int       PortCount => 2 + InternalStateNodeCount + (_thermal ? 1 : 0);
    public override ModelKind Kind      => ModelKind.Nonlinear;

    /// <summary>Internal state nodes the elaborator mints (trap, dispersion): one, <c>x</c>, for a dispersive model.</summary>
    public int InternalStateNodeCount => _dispersive ? 1 : 0;

    /// <summary>Whether the model self-heats: the core then takes a fourth net, the thermal node, as its last port.</summary>
    public bool HasThermalNode => _thermal;

    /// <summary>The thermal node's port, or −1.</summary>
    public int ThermalPort => _thermal ? 2 + InternalStateNodeCount : -1;

    public override string[] TerminalNames =>
        [ "gi", "si", "di", "si", .. (_dispersive ? (string[])["x", "0"] : []), .. (_thermal ? (string[])["t", "0"] : []) ];

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    /// <summary>The channel current's <c>gm</c> is a dependent source circuitRF can switch off exactly,
    /// as for the built-in FETs.</summary>
    public override Activity Activity => Activity.ActiveExact;

    /// <summary>With dispersion the drain's dependence on <c>x</c> is the other controlled source: zeroing
    /// both leaves the drain a conductance.</summary>
    /// <summary>The thermal coupling is a controlled source both ways — the currents' dependence on ΔT, and the power
    /// the voltages drive into the thermal node — so passivated, the thermal node is decoupled.</summary>
    public override IReadOnlyList<(int P, int Q)> ControlledConductances
    {
        get
        {
            var list = new List<(int, int)> { (1, 0) };
            if (_dispersive) list.Add((1, 2));
            if (_thermal)
            {
                int th = ThermalPort;
                for (int p = 0; p < PortCount; p++) { list.Add((p, th)); if (p != th) list.Add((th, p)); }
            }
            return list;
        }
    }

    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        int n = PortCount;
        var i  = new double[n];
        var q  = new double[n];
        var dg = new double[n, n];
        var dc = new double[n, n];
        EvaluateInto(v, i, q, dg, dc);
        return new NonlinearResult(i, q, dg, dc);
    }

    public override bool PrefersGridEvaluate => !NonlinearEvalDiagnostics.DisableGridEvaluate;

    protected override bool HasEvaluateInto => true;

    protected override void EvaluateInto(in PortVoltages v, double[] i, double[] q, double[,] dg, double[,] dc)
    {
        int n = PortCount;
        Span<double> ri = stackalloc double[MaxPorts], rq = stackalloc double[MaxPorts],
                     rg = stackalloc double[MaxPorts * MaxPorts], rc = stackalloc double[MaxPorts * MaxPorts];
        Core(v[0], v[1], _dispersive ? v[2] : 0.0, _thermal ? v[ThermalPort] : 0.0, ri, rq, rg, rc);
        for (int p = 0; p < n; p++)
        {
            i[p] = ri[p];
            q[p] = rq[p];
            for (int col = 0; col < n; col++) { dg[p, col] = rg[p * n + col]; dc[p, col] = rc[p * n + col]; }
        }
    }

    /// <summary>
    /// The whole HB time grid in one pass, written straight into the caller's buffers (R-mc2-5): no
    /// per-sample arrays, no per-grid arrays — the base class's <see cref="EvaluateInto"/> loop still
    /// allocates its five scratch buffers once per call, and HB calls once per Newton iteration.
    /// </summary>
    public override void EvaluateGrid(ReadOnlySpan<double> portVoltages, ReadOnlySpan<double> controlCurrents,
                                      int sampleCount, GridResult into)
    {
        int n = PortCount;
        into.EnsureShape(n, 0, sampleCount);
        into.ResetTerms();
        Span<double> ri = stackalloc double[MaxPorts], rq = stackalloc double[MaxPorts],
                     rg = stackalloc double[MaxPorts * MaxPorts], rc = stackalloc double[MaxPorts * MaxPorts];
        int th = ThermalPort;
        for (int t = 0; t < sampleCount; t++)
        {
            Core(portVoltages[t], portVoltages[sampleCount + t], _dispersive ? portVoltages[2 * sampleCount + t] : 0.0,
                 _thermal ? portVoltages[th * sampleCount + t] : 0.0, ri, rq, rg, rc);
            for (int p = 0; p < n; p++)
            {
                into.I[into.PortBase(p) + t] = ri[p];
                into.Q[into.PortBase(p) + t] = rq[p];
                for (int col = 0; col < n; col++)
                {
                    into.Dg[into.JacBase(p, col) + t] = rg[p * n + col];
                    into.Dc[into.JacBase(p, col) + t] = rc[p * n + col];
                }
            }
        }
    }

    /// <summary>
    /// One operating point: port currents <c>i</c>, charges <c>q</c>, and their Jacobians row-major.
    ///
    /// <para><b>Drain–source exchange</b> (R-mc2-4). With <see cref="DrainSourceSymmetry.Exchange"/> and
    /// <c>vds &lt; 0</c>, the constituents are evaluated at the image point <c>(vgs − vds, −vds)</c>
    /// ([P1] eq. 6.61) and transformed back: the channel current changes sign, the two gate branches
    /// exchange ([P1] eq. 6.64), <c>Qg</c> carries over and <c>Qd = −Qg′ − Qd′</c> (the image's source
    /// charge, [P1] eq. 6.63 in charge form). Each gradient maps through the image's Jacobian:
    /// <c>∂/∂vgs = ∂′/∂vgs′</c>, <c>∂/∂vds = −∂′/∂vgs′ − ∂′/∂vds′</c>.</para>
    /// </summary>
    private void Core(double vgs, double vds, double x, double dT, Span<double> i, Span<double> q, Span<double> dg, Span<double> dc)
    {
        int n = PortCount;
        i[..n].Clear(); q[..n].Clear(); dg[..(n * n)].Clear(); dc[..(n * n)].Clear();

        bool image = _exchange && vds < 0;
        Span<double> seeds = stackalloc double[3];
        seeds[0] = image ? vgs - vds : vgs;
        seeds[1] = image ? -vds : vds;
        seeds[2] = _temperatureC + dT;
        var s = seeds[.._p.Width];

        Span<double> gId = stackalloc double[3], gGs = stackalloc double[3], gGd = stackalloc double[3],
                     gQg = stackalloc double[3], gQd = stackalloc double[3], gRf = stackalloc double[3];

        double id   = _p.Eval(_p.Id,   "Id",   s, gId);
        double igGs = _p.Eval(_p.IgGs, "IgGs", s, gGs);
        double igGd = _p.Eval(_p.IgGd, "IgGd", s, gGd);
        double qg   = _p.Eval(_p.Qg,   "Qg",   s, gQg);
        double qd   = _p.Eval(_p.Qd,   "Qd",   s, gQd);
        double idRf = _dispersive ? _p.Eval(_p.IdRF!, "IdRF", s, gRf) : 0.0;
        if (_p.Drift is { } drift)
            drift.Evaluate(s[0], s[1], _dispersive ? idRf : id, _dispersive ? gRf : gId, _p.Width, out qg, gQg, out qd, gQd);

        if (image)
        {
            FromImage(gId); FromImage(gGs); FromImage(gGd); FromImage(gQg); FromImage(gQd); FromImage(gRf);
            id = -id;
            idRf = -idRf;
            for (int k = 0; k < 3; k++)   // the T lane maps as the voltages do: the image changes no temperature
            {
                gId[k] = -gId[k];
                gRf[k] = -gRf[k];
                (gGs[k], gGd[k]) = (gGd[k], gGs[k]);
                gQd[k] = -gQg[k] - gQd[k];
            }
            (igGs, igGd) = (igGd, igGs);
            qd = -qg - qd;
        }

        double k0 = _scale;
        // The voltage lanes are columns 0 and 1; the T lane, when the model self-heats, is the thermal port's column.
        int th = ThermalPort;
        int lanes = _thermal ? 3 : 2;
        int Col(int lane) => lane < 2 ? lane : th;

        i[0] = k0 * (igGs + igGd);
        q[0] = k0 * qg;
        q[1] = k0 * qd;
        for (int lane = 0; lane < lanes; lane++)
        {
            int col = Col(lane);
            dg[col]     = k0 * (gGs[lane] + gGd[lane]);
            dc[col]     = k0 * gQg[lane];
            dc[n + col] = k0 * gQd[lane];
        }

        if (!_dispersive)
        {
            i[1] = k0 * (id - igGd);
            for (int lane = 0; lane < lanes; lane++) dg[n + Col(lane)] = k0 * (gId[lane] - gGd[lane]);
        }
        else
        {
            // [P1] eq. 6.52 through the state node: the drain follows IdRF + x, and x relaxes to IdDC − IdRF with τ.
            i[1] = k0 * (idRf + x - igGd);
            i[2] = x - (id - idRf);
            q[2] = _tau * x;
            for (int lane = 0; lane < lanes; lane++)
            {
                int col = Col(lane);
                dg[n + col]     = k0 * (gRf[lane] - gGd[lane]);
                dg[2 * n + col] = -(gId[lane] - gRf[lane]);
            }
            dg[n + 2] = k0;
            dg[2 * n + 2] = 1.0;
            dc[2 * n + 2] = _tau;
        }

        if (!_thermal) return;

        // [P1] eq. 6.67: the dissipated power, in its terminal form, flows into the thermal node; its port carries −P.
        double p = i[0] * vgs + i[1] * vds;
        i[th] = -p;
        for (int col = 0; col < n; col++)
        {
            double dp = dg[col] * vgs + dg[n + col] * vds + (col == 0 ? i[0] : col == 1 ? i[1] : 0.0);
            dg[th * n + col] = -dp;
        }
    }

    /// <summary>The most ports a core has: two intrinsic, the dispersion node, the thermal node.</summary>
    private const int MaxPorts = 4;

    private static void FromImage(Span<double> g) => g[1] = -g[0] - g[1];
}
