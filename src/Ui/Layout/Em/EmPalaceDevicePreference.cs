// brief-em3d-136 — where a 3D run's Palace solves, CPU or GPU: STORED here, in AppPreferences, and shown in the EM
// Setup panel under Cores. EmSolveCorePreference's shape and reasoning: a property of the MACHINE and not of the
// design, so never in the .cem/.c3d (owner decision D1), and handed to the run as an ARGUMENT — the run service
// lives in CircuitRF.Design and cannot read this file, and a headless run says what it means with `em --device`.

using CircuitRF.Design.Em3d;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>The stored device. Absent, or anything but <c>gpu</c>, is <see cref="PalaceDevice.Cpu"/>.</summary>
public static class EmPalaceDevicePreference
{
    /// <summary>Test seam, as <see cref="EmSolveCorePreference"/>'s: a test that exercises the control must not write
    /// the developer's real preferences file.</summary>
    internal static PalaceDevice TestOverrideStore;
    internal static bool         TestOverrideActive;

    /// <summary>What a stored value means: <c>gpu</c> is the GPU, anything else (absent included) the CPU.</summary>
    public static PalaceDevice FromStored(string? stored) => PalaceDevices.Parse(stored) ?? PalaceDevice.Cpu;

    /// <summary>What is stored for <paramref name="device"/>: <c>gpu</c>, or nothing at all for the default.</summary>
    public static string? ToStored(PalaceDevice device) => device == PalaceDevice.Gpu ? device.Id() : null;

    public static PalaceDevice Preferred
    {
        get => TestOverrideActive ? TestOverrideStore : FromStored(AppPreferencesIo.Load().EmPalaceDevice);
        set
        {
            if (TestOverrideActive) TestOverrideStore = value;
            else AppPreferencesIo.Update(p => p.EmPalaceDevice = ToStored(value));
        }
    }
}
