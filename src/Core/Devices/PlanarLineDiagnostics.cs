using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices;

/// <summary>
/// The refusals of a planar line (MLIN, CPWG, SLIN) stated by its electrical length, as coded diagnostics rather than
/// bare sentences (tests/Firewall.Tests/user-facing-text-allowlist.txt says why).
/// </summary>
public static class PlanarLineDiagnostics
{
    public static Diagnostic ElectricalLengthFrequency(double fHz) => Diagnostic.Create(
        "device.planar-line.angle-frequency", DiagnosticSeverity.Error,
        "A line states its electrical length E at F = {f} Hz. An electrical length is stated at a positive frequency.",
        ("f", fHz));
}
