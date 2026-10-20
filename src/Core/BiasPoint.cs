namespace CircuitRF.Core;

/// <summary>
/// A model that evaluates part of itself AT A BIAS POINT rather than at the instantaneous voltages
/// (brief-modelcomposer-21 R-mc21-3) — today, a composed FET whose intrinsic charge is linearised. The engine's
/// freeze step (<c>BiasFreeze</c>, in <c>src/Engine</c>) solves the circuit's quiescent DC operating point and hands
/// each such model its own port voltages there, before any evaluation that reads charge.
///
/// <para><b>Set before a solve, never during one.</b> <see cref="FreezeAt"/> is called on the engine's own thread
/// before any parallel evaluation starts, and the frozen state is one immutable object swapped in whole, so a reader
/// never sees half of it.</para>
/// </summary>
public interface IBiasFrozen
{
    /// <summary>Whether this instance needs a bias point at all — false for every instance at its defaults, which is
    /// what keeps the freeze step free for a design that does not use one.</summary>
    bool NeedsBiasPoint { get; }

    /// <summary>Freezes at these port voltages (the DC solution's, in this model's own port order). Returns whether
    /// anything changed: a repeat call at the same point is a no-op.</summary>
    bool FreezeAt(ReadOnlySpan<double> portVoltages);
}

/// <summary>
/// The explicit statement that the current evaluations read no charge — the DC engines, whose solution charge never
/// affects. A model that cannot return a charge until it is frozen (<see cref="IBiasFrozen"/>) refuses outside this
/// scope and answers inside it; the flag is stated, never inferred from a caught exception.
///
/// <para>Per thread, and nested scopes count, so a DC solve run from inside another engine leaves the outer engine's
/// state exactly as it was when the DC solve returns.</para>
/// </summary>
public static class ChargeReading
{
    [ThreadStatic] private static int _unreadDepth;

    /// <summary>True while the evaluations on this thread read no charge.</summary>
    public static bool Unread => _unreadDepth > 0;

    /// <summary>Opens a scope in which charges are not read; dispose to close it.</summary>
    public static Scope NotRead()
    {
        _unreadDepth++;
        return new Scope();
    }

    public readonly struct Scope : IDisposable
    {
        public void Dispose() => _unreadDepth--;
    }
}
