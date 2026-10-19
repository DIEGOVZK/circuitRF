namespace CircuitRF.Core.Devices.Composed;

/// <summary>
/// The periphery scaling rules of D9, by NAME. A rule is stored in a <c>.cmodel</c> as one of these
/// spellings, and a name not here is refused by name (<see cref="CmodelFile"/>) — which is the room
/// left for the later rules (a power law, unit-width scaling, affine rules from two or three device
/// sizes): each adds a name and its factor, nothing else.
/// </summary>
public static class PeripheryScaling
{
    public const string Fixed            = "fixed";
    public const string PerFinger        = "perFinger";
    public const string PerFingerInverse = "perFingerInverse";

    /// <summary>Every rule this build knows, in the order a refusal lists them.</summary>
    public static readonly IReadOnlyList<string> Known = [Fixed, PerFinger, PerFingerInverse];

    public static bool IsKnown(string rule) => Known.Contains(rule, StringComparer.Ordinal);

    /// <summary>What a network value is multiplied by at <paramref name="n"/> fingers of a model
    /// measured at <paramref name="n0"/>.</summary>
    public static double Factor(string rule, int n, int n0) => rule switch
    {
        Fixed            => 1.0,
        PerFinger        => (double)n / n0,
        PerFingerInverse => (double)n0 / n,
        _ => throw new ArgumentException($"'{rule}' is not a known scaling rule.", nameof(rule)),
    };
}
