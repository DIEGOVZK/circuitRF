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
/// engine already writes. A transcapacitance (<c>∂Qd/∂vgs ≠ ∂Qg/∂vds</c>) is kept, not symmetrised.</para>
///
/// <para><b>The internal-node seam</b> used by the thermal, trap and dispersion phases:
/// <see cref="InternalStateNodeCount"/> ground-referenced nodes, minted by the elaborator after the two
/// intrinsic ports as ordinary global unknowns — never eliminated locally, because their harmonic
/// content matters in HB. This phase declares none.</para>
/// </summary>
public sealed class ComposedCoreModel : ComponentModel
{
    private readonly ComposedCoreProgram _p;
    private readonly double _scale;
    private readonly double _temperatureC;
    private readonly bool   _hasT;

    /// <summary>The model this core evaluates.</summary>
    public LoadedComposedModel Loaded { get; }

    /// <summary>The instance's finger count.</summary>
    public int N { get; }

    /// <summary>The temperature the constituents are evaluated at, °C — the run's ambient clamped to
    /// the model's range, or the model's own <c>T0</c> for a single-temperature model.</summary>
    public double TemperatureC => _temperatureC;

    public ComposedCoreModel(LoadedComposedModel loaded, int n, double ambientC)
    {
        if (loaded.Model.Core.IdRF is not null)
            throw new ParameterRefusalException(
                $"'{System.IO.Path.GetFileName(loaded.Path)}' carries IdRF, the DC/RF dispersion, " +
                "which this circuitRF does not evaluate yet.");

        Loaded = loaded;
        N      = n;
        _p     = loaded.Program;
        _scale = (double)n / loaded.N0;

        var t = loaded.Model.Temperature;
        _hasT = t.Mode == TemperatureMode.Range;
        _temperatureC = _hasT ? Math.Clamp(ambientC, t.Tmin, t.Tmax) : t.T0;
    }

    public override int       PortCount => 2 + InternalStateNodeCount;
    public override ModelKind Kind      => ModelKind.Nonlinear;

    /// <summary>Internal state nodes (thermal, trap, dispersion) — none in this phase.</summary>
    public int InternalStateNodeCount => 0;

    public override string[] TerminalNames => ["gi", "si", "di", "si"];

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    /// <summary>The channel current's <c>gm</c> is a dependent source circuitRF can switch off exactly,
    /// as for the built-in FETs.</summary>
    public override Activity Activity => Activity.ActiveExact;

    public override IReadOnlyList<(int P, int Q)> ControlledConductances => [(1, 0)];

    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        var i  = new double[2];
        var q  = new double[2];
        var dg = new double[2, 2];
        var dc = new double[2, 2];
        EvaluateInto(v, i, q, dg, dc);
        return new NonlinearResult(i, q, dg, dc);
    }

    public override bool PrefersGridEvaluate => !NonlinearEvalDiagnostics.DisableGridEvaluate;

    protected override bool HasEvaluateInto => true;

    protected override void EvaluateInto(in PortVoltages v, double[] i, double[] q, double[,] dg, double[,] dc)
    {
        Span<double> seeds = stackalloc double[3];
        seeds[0] = v[0];
        seeds[1] = v[1];
        seeds[2] = _temperatureC;
        var s = seeds[.._p.Width];

        Span<double> gId = stackalloc double[3], gGs = stackalloc double[3], gGd = stackalloc double[3],
                     gQg = stackalloc double[3], gQd = stackalloc double[3];

        double id   = _p.Eval(_p.Id,   "Id",   s, gId);
        double igGs = _p.Eval(_p.IgGs, "IgGs", s, gGs);
        double igGd = _p.Eval(_p.IgGd, "IgGd", s, gGd);
        double qg   = _p.Eval(_p.Qg,   "Qg",   s, gQg);
        double qd   = _p.Eval(_p.Qd,   "Qd",   s, gQd);

        double k = _scale;
        i[0] = k * (igGs + igGd);
        i[1] = k * (id - igGd);
        q[0] = k * qg;
        q[1] = k * qd;
        for (int col = 0; col < 2; col++)
        {
            dg[0, col] = k * (gGs[col] + gGd[col]);
            dg[1, col] = k * (gId[col] - gGd[col]);
            dc[0, col] = k * gQg[col];
            dc[1, col] = k * gQd[col];
        }
    }
}
