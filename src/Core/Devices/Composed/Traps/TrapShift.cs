namespace CircuitRF.Core.Devices.Composed.Traps;

/// <summary>
/// The <see cref="ModelTraps.Shift"/> coupling compiled (brief-modelcomposer-19 R-mc19-1): the gate-voltage shift
/// <c>ΔV = Σₖ aₖ·sₖ·(φₖ − φₖ,DC(vgs, vds, T))</c> over the enabled states, <c>sₖ</c> each state's
/// <see cref="TrapDynamics.CaptureSign"/>. For <c>CaptureEmission</c> that is <c>a_d·(φ2 − vds) + a_g·(vgs − φ1)</c>: a
/// drain-lag state held above <c>vds</c>, or a gate-lag state held below <c>vgs</c>, lowers the current. At any DC operating
/// point every <c>φₖ = φₖ,DC</c>, so <c>ΔV = 0</c> exactly. Immutable and allocation-free, like the dynamics it reads.
/// </summary>
public sealed class TrapShift
{
    private readonly TrapDynamics _dynamics;
    private readonly double[] _c;   // aₖ·sₖ per enabled state

    private TrapShift(TrapDynamics dynamics, double[] c) { _dynamics = dynamics; _c = c; }

    /// <summary>Null for a <see cref="ModelTraps.Trained"/> block; the registry has already checked the strengths.</summary>
    public static TrapShift? Build(ModelTraps traps, TrapDynamics dynamics)
    {
        if (!traps.Shifted) return null;
        var model = TrapModels.Find(traps.Model)!;
        var c = new double[traps.States.Count];
        for (int k = 0; k < c.Length; k++)
            c[k] = traps.Parameter(model.ShiftParameter(traps.States[k])!)!.Value * dynamics.CaptureSign(k);
        return new TrapShift(dynamics, c);
    }

    /// <summary>
    /// <c>ΔV</c> at the states <paramref name="phi"/> and the bias, with its derivatives: by <c>(vgs, vds, T)</c> into
    /// <paramref name="dBias"/>, and by each state into <paramref name="dPhi"/>.
    /// </summary>
    public double Delta(ReadOnlySpan<double> phi, double vgs, double vds, double t, Span<double> dBias, Span<double> dPhi)
    {
        Span<double> g = stackalloc double[3];
        dBias[..3].Clear();
        double dv = 0;
        for (int k = 0; k < _c.Length; k++)
        {
            double dc = _dynamics.DcValue(k, vgs, vds, t, g);
            dv += _c[k] * (phi[k] - dc);
            dPhi[k] = _c[k];
            for (int j = 0; j < 3; j++) dBias[j] -= _c[k] * g[j];
        }
        return dv;
    }
}
