using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The refusals of the op-amp and the ideal transformer, as coded diagnostics rather than bare
/// sentences (tests/Firewall.Tests/user-facing-text-allowlist.txt says why): each names the instance,
/// the value it was given and what would be accepted.
/// </summary>
public static class IdealDeviceDiagnostics
{
    public static Diagnostic NetCount(string type, string instance, int expected, string terminals, int got) => Diagnostic.Create(
        "device.net-count", DiagnosticSeverity.Error,
        "{type} '{instance}': expected {expected} nets ({terminals}); got {got}.",
        ("type", type), ("instance", instance), ("expected", expected), ("terminals", terminals), ("got", got));

    public static Diagnostic OpAmpPins(string got) => Diagnostic.Create(
        "device.opamp.pins", DiagnosticSeverity.Error,
        "OpAmp: Pins is 3 (in+, in−, out) or 5 (adding V+, V−); got {got}.", ("got", got));

    public static Diagnostic OpAmpGain(string instance, double got) => Diagnostic.Create(
        "device.opamp.gain", DiagnosticSeverity.Error,
        "OpAmp '{instance}': A is the open-loop gain and must be a positive number, or 0 for the ideal op-amp; got {got}.",
        ("instance", instance), ("got", got));

    public static Diagnostic OpAmpRout(string instance, double got) => Diagnostic.Create(
        "device.opamp.rout", DiagnosticSeverity.Error,
        "OpAmp '{instance}': Rout must not be negative; got {got}.", ("instance", instance), ("got", got));

    public static Diagnostic TransformerRatio(string instance, double got) => Diagnostic.Create(
        "device.transformer.ratio", DiagnosticSeverity.Error,
        "Transformer '{instance}': N is the turns ratio, primary to secondary, and must be a positive number; got {got}. "
        + "Swap the windings for a step-down the other way.",
        ("instance", instance), ("got", got));
}

/// <summary>A refused device parameter or wiring, carrying its <see cref="Diagnostic"/>.</summary>
public sealed class DeviceRefusalException(Diagnostic diagnostic) : InvalidOperationException(diagnostic.Render())
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}
