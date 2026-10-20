namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// HB convergence trace — per-iteration and per-continuation-step diagnostics.
/// Mirrors the Phase-3 ConvergenceTrace pattern, extended to the HB power sweep.
/// Primary diagnostic: if Hero 2 doesn't converge, report this trace rather than grinding.
/// </summary>
public sealed class HbConvergenceTrace
{
    /// <param name="Iter">0-based Newton iteration index.</param>
    /// <param name="ResidualNorm">‖F‖₂ in amperes at the START of this iteration (design §12.2 —
    /// the criterion is absolute on ‖F‖). The line search compares this quantity before and after a
    /// step, except when a device's state node is on the interface: it then compares the
    /// self-admittance-scaled norm (<see cref="HbNewton.LineSearchWeights"/>, MC-25), and this
    /// record still carries ‖F‖₂.</param>
    /// <param name="Lambda">
    /// The step fraction the backtracking line search actually accepted at this iteration (HB-P3
    /// M1). Starts at the user's fixed <c>Lambda</c> damping (default 1 = full Newton step) and
    /// halves per rejected trial. The FINAL record of a converged solve carries the entry value
    /// <c>1</c> and <c>Backtracks = 0</c> because no step is taken from a converged iterate.
    /// </param>
    /// <param name="Backtracks">How many trials the line search rejected before this step.</param>
    /// <param name="Stalled">
    /// True when the line search exhausted <see cref="HbNewton.MaxBacktracks"/> halvings without
    /// ‖F‖ decreasing and took the smallest step anyway rather than standing still.
    /// </param>
    /// <param name="WorstNode">
    /// The interface node (an index into the solve's interface, named by <see cref="NodeNames"/>)
    /// whose row of F carries the largest share of ‖F‖² at the START of this iteration, every
    /// harmonic of it together; −1 when not recorded (MC-25 R-mc25-1). A non-convergence warning
    /// that says where is worth more than one that says how much.
    /// </param>
    /// <param name="WorstShare">That node's share of ‖F‖², 0 … 1.</param>
    /// <param name="StepNode">The interface node the accepted step moved furthest, −1 when none.</param>
    /// <param name="StepSize">How far: the largest |λ·ΔV| over that node's harmonics, in the node's own unit.</param>
    public record IterRecord(int Iter, double ResidualNorm,
        double Lambda = 1.0, int Backtracks = 0, bool Stalled = false,
        int WorstNode = -1, double WorstShare = 0, int StepNode = -1, double StepSize = 0);
    public record StepRecord(double Pin_dBm, int Iterations, bool Converged,
        IReadOnlyList<IterRecord> IterTrace);

    private readonly List<StepRecord> _steps = [];

    /// <summary>The interface nodes' names, in the solve's order, for <see cref="IterRecord.WorstNode"/> and
    /// <see cref="IterRecord.StepNode"/>; empty when the engine did not set them.</summary>
    public IReadOnlyList<string> NodeNames { get; internal set; } = [];

    /// <summary>The name of interface node <paramref name="n"/>, or its index when unnamed.</summary>
    public string NameOf(int n) => n < 0 ? "" : n < NodeNames.Count ? NodeNames[n] : $"if[{n}]";
    public IReadOnlyList<StepRecord> Steps => _steps;

    internal void AddStep(StepRecord s) => _steps.Add(s);
    public int TotalSteps      => _steps.Count;
    public int TotalIterations => _steps.Sum(s => s.Iterations);

    /// <summary>Write a compact table to the given writer (stderr by default).</summary>
    public void Print(TextWriter? writer = null)
    {
        writer ??= Console.Error;
        writer.WriteLine("[HB trace] Pin_dBm  Iters  Converged  FinalResidual  Backtracks  MinLambda  WorstRow");
        foreach (var s in _steps)
        {
            double finalRes = s.IterTrace.Count > 0 ? s.IterTrace[^1].ResidualNorm : double.NaN;
            int    bt       = s.IterTrace.Sum(r => r.Backtracks);
            double minLam   = s.IterTrace.Count > 0 ? s.IterTrace.Min(r => r.Lambda) : 1.0;
            bool   stalled  = s.IterTrace.Any(r => r.Stalled);
            writer.WriteLine(
                $"[HB trace] {s.Pin_dBm,8:F1}  {s.Iterations,5}  " +
                $"{(s.Converged ? "YES" : " NO")}       {finalRes:E3}  {bt,10}  {minLam,9:G4}" +
                (stalled ? "  STALLED" : "") +
                (s.IterTrace.Count > 0 && s.IterTrace[^1].WorstNode >= 0
                    ? $"  {NameOf(s.IterTrace[^1].WorstNode)} {s.IterTrace[^1].WorstShare * 100:F0} %" : ""));
        }
    }
}
