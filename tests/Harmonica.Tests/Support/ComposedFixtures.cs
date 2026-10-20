using CircuitRF.Core.Devices.Composed;
using CircuitRF.Engine;

namespace CircuitRF.Harmonica.Tests.Support;

/// <summary>
/// brief-modelcomposer-23 — the example's <c>synthetic-gan</c> <c>.cmodel</c> (GaN two-shell network, a drain-lag trap,
/// self-heating) as harmonicaRF's DUT, and a series-only variant of it for the glyph-coincidence gate.
/// </summary>
internal static class ComposedFixtures
{
    public const double F0 = 2e9;

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    public static string ExampleModel
        => Path.Combine(RepoRoot(), "examples", "Model Composer", "synthetic-gan", "gan_4x125.cmodel");

    /// <summary>A class-AB bias at 2 GHz, K = 3. <paramref name="comparable"/> uses the bias tee the stamped oracle is
    /// itself accurate with (1 µH, 1 nF — SchurReterminationTests' finding) and drives Newton to its round-off floor: a
    /// trap node puts ‖F‖'s floor near 1e-8 A, so a tolerance cannot be met and the fixed iteration count is what stops
    /// both routes there (MC-25's record: two solves at the floor agree to ≤ 5e-12).</summary>
    public static CircuitModel Model(string? file = null, ComposedSwitches? switches = null, bool comparable = false,
                                     int? fingers = null, double m = 1.0) => new()
    {
        Dut = new DutSpec
        {
            Kind         = DutKind.Composed,
            TypeName     = "ComposedFET",
            ModelFile    = file ?? ExampleModel,
            Fingers      = fingers,
            Multiplicity = m,
            Switches     = switches ?? ComposedSwitches.Default,
        },
        Bias     = new BiasSpec { Vgs = -0.8, Vds = 8.0 },
        Settings = comparable
            ? new HarmonicaSettings
            {
                HarmonicCount = 3, FrequencyHz = F0, Tol = 1e-14, StateNodeTol = 1e-14, MaxIter = FloorIterations,
                BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9,
            }
            : new HarmonicaSettings { HarmonicCount = 3, FrequencyHz = F0 },
    };

    /// <summary>Newton iterations a floor-driven comparison runs.</summary>
    public const int FloorIterations = 40;

    /// <summary>The settings both routes run with in a comparison. gmin is ON, on every node: a trap node touches only
    /// the core, so the linear extraction has nothing else to hold it; the oracle's two extra nodes (the far side of each
    /// DC block) then carry 1e-12 S each, ~5e-11 of a termination. No drive ramp: a fixed iteration count is the stop.</summary>
    public static AnalysisSettings Comparable => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Always,
        DriveStepping             = DcBiasSteppingMode.Never,
        HbMaxIter                 = FloorIterations,
    };

    /// <summary>The series-only network's own values: a gate, drain and source lead, R then L, no capacitor anywhere.</summary>
    public const double Rg = 2.0, Rd = 3.0, Rs = 1.0, Lg = 40e-12, Ld = 45e-12, Ls = 5e-12;

    /// <summary>The example's model with its network replaced by series R–L leads only, written beside a fresh folder.</summary>
    public static string SeriesOnlyModel()
    {
        var model = CmodelFile.Read(ExampleModel);
        string net =
            "define sonly (P1 P2 P3 P4 P5 P6)\n" +
            $"parameters Rg={Rg} Rd={Rd} Rs={Rs} Lg={Lg:R} Ld={Ld:R} Ls={Ls:R}\n" +
            "L:LG P1 n1 L=Lg\nR:RG n1 P4 R=Rg\n" +
            "L:LD P2 n2 L=Ld\nR:RD n2 P5 R=Rd\n" +
            "L:LS P3 n3 L=Ls\nR:RS n3 P6 R=Rs\n" +
            "end\n";
        return Write(model with { Network = new ExtrinsicNetwork(net, []) }, "series.cmodel");
    }

    /// <summary>Writes a model into a fresh temporary folder; returns its path.</summary>
    public static string Write(ComposedModel model, string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"mc23-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        CmodelFile.Write(model, path);
        return path;
    }
}
