namespace CircuitRF.Core.Devices.Composed.Traps;

/// <summary>
/// The <c>Traps</c> block of a <c>.cmodel</c> (brief-modelcomposer-10 R-mc10-1, D10): which trap model, which of its states
/// are enabled, its parameters, the trap-setting levels each state was trained at, and — for a model written as
/// expressions — the expressions, keyed <c>&lt;state&gt;.Dynamics</c> and <c>&lt;state&gt;.Dc</c>. A model with no traps
/// carries no block at all (<see cref="ComposedModel.Traps"/> is null), which is what keeps it byte-identical to one written
/// before traps existed.
/// </summary>
public sealed record ModelTraps
{
    /// <summary>The name that means no trap model; never written.</summary>
    public const string None = "None";

    /// <summary>The couplings (brief-modelcomposer-19 R-mc19-1): <c>Trained</c>, where <c>Id</c> takes the enabled states as
    /// inputs, and <c>Shift</c>, where it takes none and the core evaluates it at a shifted gate voltage.</summary>
    public const string Trained = "Trained", Shift = "Shift";

    public static readonly IReadOnlyList<string> Couplings = [Trained, Shift];

    public required string Model { get; init; }

    /// <summary>How the states reach the current; <see cref="Trained"/> is never written, so a block without the field reads
    /// and writes exactly as before couplings existed.</summary>
    public string Coupling { get; init; } = Trained;

    public bool Shifted => Coupling == Shift;

    /// <summary>The enabled states, in lane order (each a name the model declares).</summary>
    public IReadOnlyList<string> States { get; init; } = [];

    public IReadOnlyList<KeyValuePair<string, double>> Parameters { get; init; } = [];

    /// <summary>Per enabled state, the distinct trap-setting levels its training data held, volts — recorded so a model
    /// trained from one level says so (R-mc10-4). Optional.</summary>
    public IReadOnlyList<KeyValuePair<string, IReadOnlyList<double>>> Levels { get; init; } = [];

    /// <summary>A <c>Custom</c> model's expressions, keyed <c>&lt;state&gt;.Dynamics</c> and <c>&lt;state&gt;.Dc</c>.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Expressions { get; init; } = [];

    /// <summary>A parameter by name, or null.</summary>
    public double? Parameter(string name)
        => Parameters.FirstOrDefault(p => p.Key == name) is { Key: not null } kv ? kv.Value : null;

    /// <summary>An expression by key, or null.</summary>
    public string? Expression(string key)
        => Expressions.FirstOrDefault(e => e.Key == key) is { Key: not null } kv ? kv.Value : null;

    /// <summary>A state's trained levels, or empty.</summary>
    public IReadOnlyList<double> LevelsOf(string state)
        => Levels.FirstOrDefault(l => l.Key == state) is { Key: not null } kv ? kv.Value : [];
}

/// <summary>One parameter a trap model declares: its name, its unit, its default — null when the parameter is optional and
/// its absence switches a feature off — and what it means.</summary>
public sealed record TrapParameter(string Name, string Unit, double? Default, string Meaning);

/// <summary>
/// A trap model by name (R-mc10-1) — what <see cref="TrapModels"/> registers. It declares its states (at most
/// <see cref="TrapDynamics.MaxStates"/>, named <c>phi1</c> … in lane order) and its parameters, and compiles a <c>Traps</c>
/// block into <see cref="TrapDynamics"/>. A developer adds a model by deriving from this and registering it; nothing else
/// in circuitRF names a model.
/// </summary>
public abstract class TrapModelDefinition
{
    public abstract string Name { get; }

    /// <summary>Every state the model can carry, in lane order.</summary>
    public abstract IReadOnlyList<string> States { get; }

    /// <summary>The parameters it reads, with units. A model whose parameters are the user's own (<c>Custom</c>) declares
    /// none and checks them in <see cref="Build"/>.</summary>
    public abstract IReadOnlyList<TrapParameter> Parameters { get; }

    /// <summary>Whether <see cref="Parameters"/> is the whole list (any other name is refused), or the parameters are the
    /// user's own.</summary>
    public virtual bool DeclaresAllParameters => true;

    /// <summary>The parameter a <see cref="ModelTraps.Shift"/> coupling reads a state's strength from, V/V, or null when the
    /// model defines no shift coupling (R-mc19-1). It is not one of <see cref="Parameters"/>: a <c>Trained</c> block carries
    /// none.</summary>
    public virtual string? ShiftParameter(string state) => null;

    /// <summary>
    /// The block compiled: its own checks beyond the generic ones (<see cref="CmodelFile.Validate"/> has already checked
    /// the model name, the states and that every parameter is finite), each refused as a
    /// <see cref="CmodelFormatException"/> by field. <paramref name="label"/> names the model in an evaluation failure.
    /// </summary>
    public abstract TrapDynamics Build(ModelTraps traps, string label);

    /// <summary>A parameter's value: the block's, else the declared default.</summary>
    protected double ValueOf(ModelTraps traps, string name)
        => traps.Parameter(name) ?? Parameters.First(p => p.Name == name).Default
           ?? throw new InvalidOperationException($"'{name}' has no default.");
}

/// <summary>
/// The compiled dynamics of a model's enabled states (R-mc10-1): immutable, thread-safe and allocation-free, like every
/// evaluator the core holds. Each state is a ground-referenced internal node of the core through MC-1's seam; this class
/// supplies that node's current and charge and the state's DC value.
/// </summary>
public abstract class TrapDynamics(IReadOnlyList<string> states)
{
    /// <summary>The most states a model may carry.</summary>
    public const int MaxStates = 4;

    /// <summary>The enabled states, in lane order.</summary>
    public IReadOnlyList<string> States { get; } = states;

    /// <summary>
    /// One state's node at one point: its current <c>i</c> (the return value) and charge <paramref name="q"/>, and their
    /// derivatives by <c>(phi, vgs, vds, T)</c> into <paramref name="gi"/> and <paramref name="gq"/>. KCL at the node is
    /// <c>dq/dt + i = 0</c>. <paramref name="t"/> is the device temperature in °C — the junction temperature when the model
    /// self-heats (MC-9), else the ambient.
    /// </summary>
    public abstract double Evaluate(int state, double phi, double vgs, double vds, double t,
                                    Span<double> gi, out double q, Span<double> gq);

    /// <summary>The state's value at a DC operating point.</summary>
    public abstract double DcValue(int state, double vgs, double vds, double t);

    /// <summary>The state's DC value and its derivatives by <c>(vgs, vds, T)</c> into <paramref name="grad"/> — what a
    /// <see cref="ModelTraps.Shift"/> coupling differentiates through (R-mc19-1). A model that defines no shift coupling need
    /// not supply it.</summary>
    public virtual double DcValue(int state, double vgs, double vds, double t, Span<double> grad)
        => throw new NotSupportedException($"trap state {States[state]} has no DC gradient; its model defines no shift coupling.");

    /// <summary>
    /// Which way capture moves the state: +1 upwards (drain-lag, captured while <c>vds</c> exceeds it), −1 downwards
    /// (gate-lag, captured while <c>vgs</c> is below it). The composer maps pulsed data by it (R-mc10-4): a pulse whose own DC
    /// state lies on the far side of the trap-setting one, in this direction, may have been captured. The default is the
    /// convention a model written as expressions follows — odd states like gate-lag, even states like drain-lag.
    /// </summary>
    public virtual int CaptureSign(int state) => States[state] is "phi1" or "phi3" ? -1 : +1;

    /// <summary>The state's emission time at <paramref name="t"/> °C, seconds, when the model has one; what the composer
    /// compares a pulse's timing against. Null: not stated.</summary>
    public virtual double? EmissionTime(int state, double t) => null;
}

/// <summary>
/// A model written as a rate, <c>dphi/dt = D(phi, vgs, vds, T)</c>: the node carries <c>q = C·phi</c> and
/// <c>i = −C·D</c> with one normalised <see cref="NodeCapacitance"/>, so the node voltage IS the state in volts. Newton is
/// invariant to the scaling of a row, so <c>C</c> trades two floors, both measured on the gate device: the DC engine's
/// <c>gmin</c> moves a DC state by <c>gmin·phi·τ_emit/C</c> (1e-12 of it at 1 mF and τ_emit = 1 ms), and in HB the
/// round-off of the charge's DC part, times <c>kω</c>, floors the residual at about <c>6e-5·C</c> amps at 2 GHz with
/// eight harmonics (6e-8 A at 1 mF, against the 1e-6 A default tolerance; at 1 F it stalled at 1.6e-7 A).
/// </summary>
public abstract class RateTrapDynamics(IReadOnlyList<string> states) : TrapDynamics(states)
{
    public const double NodeCapacitance = 1e-3;

    /// <summary><c>dphi/dt</c>, V/s, and its derivatives by <c>(phi, vgs, vds, T)</c> into <paramref name="grad"/>.</summary>
    public abstract double Rate(int state, double phi, double vgs, double vds, double t, Span<double> grad);

    public sealed override double Evaluate(int state, double phi, double vgs, double vds, double t,
                                           Span<double> gi, out double q, Span<double> gq)
    {
        double d = Rate(state, phi, vgs, vds, t, gi);
        for (int k = 0; k < 4; k++) gi[k] *= -NodeCapacitance;
        gq[..4].Clear();
        gq[0] = NodeCapacitance;
        q = NodeCapacitance * phi;
        return -NodeCapacitance * d;
    }
}
