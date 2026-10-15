// brief-em3d-136 — where Palace solves: on the CPU, or on the GPUs a GPU build of Palace finds.
//
// A property of the MACHINE, never of the design (owner decision D1, R-em3d136-1a): a .cem travels with the
// workspace, so opening a colleague's setup must not switch a run onto a GPU this machine does not have, or off
// one it does. It is therefore an ARGUMENT to the run, exactly as the core cap is — the GUI fills it from its
// preference, `em --device` from the command line — and it enters no provenance hash: it is the same problem
// solved on different hardware.

namespace CircuitRF.Design.Em3d;

/// <summary>Palace's <c>Solver.Device</c>. <see cref="Cpu"/> is the default everywhere.</summary>
public enum PalaceDevice { Cpu, Gpu }

public static class PalaceDevices
{
    /// <summary>The spelling the preference, the CLI and the MCP tool all use: <c>cpu</c> or <c>gpu</c>.</summary>
    public static string Id(this PalaceDevice device) => device == PalaceDevice.Gpu ? "gpu" : "cpu";

    /// <summary>The value Palace's configuration takes: <c>"CPU"</c> or <c>"GPU"</c>.</summary>
    public static string ConfigValue(this PalaceDevice device) => device == PalaceDevice.Gpu ? "GPU" : "CPU";

    /// <summary><c>cpu</c> or <c>gpu</c>, any case; null for anything else (the caller refuses it, listing the two).</summary>
    public static PalaceDevice? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "cpu" => PalaceDevice.Cpu,
        "gpu" => PalaceDevice.Gpu,
        _     => null,
    };
}
