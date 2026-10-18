// brief-sar-6 R-sar6-1 — a setup's Sar group: what a driven 3D run is asked to give besides S, the specific absorption rate
// and, for an MRI coil, B1+ and B1−, at one or more frequencies. docs/design/sar.md §8.
//
// Declared in this assembly for R-em3d3-3c's reason (EmSetupPersistence's thermal types say it): the generated reference page
// expands a type only when it lives in CemFile's own assembly, so `reference em-setup` documents every key here the day it
// exists. The engine's SarMethod is an enum, which the page expands wherever it lives.
//
// UNITS ARE IN THE KEY, as SaveFieldsGHz's and OffsetUm's are: FrequenciesGHz and MassesGrams, where the brief wrote Frequencies
// and Masses. A bare frequency could be Hz, MHz or GHz, and all three are plausible for SAR.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Engine.Em3d.Sar;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// brief-sar-6 R-sar6-1 — the setup's <c>Sar</c> group: SAR and B1± as results of a driven openEMS run. Its presence turns
/// <c>OpenEms.CellConstantMaterial</c> on (unless the openEMS section says otherwise), and evaluates every frequency-dependent
/// material at the first SAR frequency instead of the sweep's centre.
/// </summary>
public sealed class CemSar
{
    /// <summary>The frequencies, GHz, SAR and B1 are computed at; each inside the sweep. Required. A setup with a
    /// frequency-dependent (tissue) material may name only one: an openEMS run sees each material at one frequency.</summary>
    public List<double>? FrequenciesGHz { get; set; }

    /// <summary>The averaging masses, grams. Omitted: 1 and 10.</summary>
    public List<double>? MassesGrams { get; set; }

    /// <summary>How an averaging cube is chosen. Omitted: Cube (the cube-averaging procedure of IEC/IEEE 62704-1 as openEMS's
    /// sar_calc implements it).</summary>
    public SarMethod? Method { get; set; }

    /// <summary>The objects every reported SAR number is restricted to, by name; a phantom's name stands for all its layers.
    /// Omitted: every object made of a lossy dielectric (σ above zero) — every tissue.</summary>
    public List<string>? Region { get; set; }

    /// <summary>Compute B1+ and B1− from the magnetic field. Omitted: on when every SAR frequency is below 500 MHz (an MRI
    /// coil), off above.</summary>
    public bool? B1 { get; set; }

    /// <summary>The static field's direction: <c>+z</c>, <c>-z</c>, <c>+x</c>, <c>-x</c>, <c>+y</c> or <c>-y</c>. Omitted:
    /// <c>+z</c>. A negative axis swaps B1+ and B1−.</summary>
    public string? B0Axis { get; set; }

    /// <summary>The objects whose mean |B1+| is reported, by name (a phantom's name stands for all its layers). Omitted: the
    /// innermost layer of the first phantom, else every object of <see cref="Region"/>.</summary>
    public List<string>? B1Region { get; set; }

    /// <summary>Also run openEMS's sar_calc on the same dump, when it is installed beside openEMS, and report its peak beside
    /// circuitRF's. Omitted: off.</summary>
    public bool? CrossCheck { get; set; }

    /// <summary>brief-sar-9 — also keep every port's E (and H, with B1) over the SAR box, per unit incident wave at that port with
    /// every other port matched, as files beside the <c>.sNp</c>: what a schematic's <c>fields</c> analysis recombines into the
    /// field of any circuit around the block, with no new EM run. Omitted: off.</summary>
    public bool? PerPortFields { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }

    public CemSar Clone()
    {
        var c = (CemSar)MemberwiseClone();
        c.FrequenciesGHz = FrequenciesGHz is { } f ? [.. f] : null;
        c.MassesGrams = MassesGrams is { } m ? [.. m] : null;
        c.Region = Region is { } r ? [.. r] : null;
        c.B1Region = B1Region is { } b ? [.. b] : null;
        c.Unread = Unread is { } u ? new(u) : null;
        return c;
    }
}

/// <summary>
/// brief-sar-6 R-sar6-1 — a <see cref="CemSar"/> RESOLVED: every omitted key at its default. The defaults live here and
/// nowhere else.
/// </summary>
/// <param name="FrequenciesHz">The SAR frequencies, Hz, in the order given.</param>
/// <param name="MassesGrams">The averaging masses, grams.</param>
/// <param name="B0Axis">0, 1 or 2 for x, y or z.</param>
/// <param name="B0Sign">+1 or −1.</param>
public sealed record SarSettings(IReadOnlyList<double> FrequenciesHz, IReadOnlyList<double> MassesGrams, SarMethod Method,
                                 IReadOnlyList<string>? Region, bool B1, int B0Axis, int B0Sign, IReadOnlyList<string>? B1Region,
                                 bool CrossCheck, bool PerPortFields = false)
{
    /// <summary>The masses an omitted <see cref="CemSar.MassesGrams"/> averages over.</summary>
    public static IReadOnlyList<double> DefaultMassesGrams { get; } = [1, 10];

    /// <summary>Below this every SAR frequency must lie for B1 to be on by default: MRI's fields are 64–300 MHz (1.5 T to 7 T),
    /// phone SAR's 700 MHz and up.</summary>
    public const double B1DefaultBelowHz = 500e6;

    /// <summary>The spelling of each B0 axis, in the order <see cref="ParseB0Axis"/> accepts them.</summary>
    public static IReadOnlyList<string> B0AxisSpellings { get; } = ["+z", "-z", "+x", "-x", "+y", "-y"];

    /// <summary><paramref name="sar"/> resolved. Values that cannot be run are kept as given and named by <see cref="Problems"/>.</summary>
    public static SarSettings Resolve(CemSar sar)
    {
        ArgumentNullException.ThrowIfNull(sar);
        var hz = (sar.FrequenciesGHz ?? []).Select(g => g * 1e9).ToList();
        var (axis, sign) = ParseB0Axis(sar.B0Axis) ?? (2, 1);
        bool b1 = sar.B1 ?? (hz.Count > 0 && hz.All(f => f < B1DefaultBelowHz));
        return new SarSettings(hz, sar.MassesGrams is { Count: > 0 } m ? [.. m] : DefaultMassesGrams, sar.Method ?? SarMethod.Cube,
                               sar.Region is { Count: > 0 } r ? [.. r] : null, b1, axis, sign,
                               sar.B1Region is { Count: > 0 } b ? [.. b] : null, sar.CrossCheck ?? false, sar.PerPortFields ?? false)
        {
            B0AxisText = sar.B0Axis,
        };
    }

    /// <summary>What <see cref="CemSar.B0Axis"/> said, for <see cref="Problems"/>.</summary>
    public string? B0AxisText { get; init; }

    /// <summary><c>+z</c> … <c>-y</c> (a bare <c>z</c> is <c>+z</c>; case and spaces ignored) as an axis and a sign; null for
    /// anything else, and for null.</summary>
    public static (int Axis, int Sign)? ParseB0Axis(string? text)
    {
        if (text is null) return null;
        string t = text.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        int sign = 1;
        if (t.StartsWith('-')) { sign = -1; t = t[1..]; }
        else if (t.StartsWith('+')) t = t[1..];
        return t switch { "x" => (0, sign), "y" => (1, sign), "z" => (2, sign), _ => null };
    }

    /// <summary>The axis as it is written: <c>+z</c>.</summary>
    public string B0AxisName => (B0Sign < 0 ? "-" : "+") + "xyz"[B0Axis];

    /// <summary>The name an averaged quantity takes: <c>SAR10g</c> for 10 g, <c>SAR0.5g</c> for 0.5 g.</summary>
    public static string AveragedName(double grams) => "SAR" + grams.ToString("G6", CultureInfo.InvariantCulture) + "g";

    /// <summary>Every value that cannot be run, as sentences naming the key — empty when all can.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (FrequenciesHz.Count == 0)
            p.Add("Sar.FrequenciesGHz names no frequency: give the frequency, in GHz, SAR is computed at (0.298 for 7 T, 1.0 for a 1 GHz phone).");
        foreach (double f in FrequenciesHz)
            if (!(f > 0) || double.IsInfinity(f))
            {
                p.Add($"Sar.FrequenciesGHz holds {G(f / 1e9)}; every SAR frequency must be a positive number of GHz.");
                break;
            }
        if (FrequenciesHz.Distinct().Count() < FrequenciesHz.Count) p.Add("Sar.FrequenciesGHz names one frequency twice.");
        foreach (double m in MassesGrams)
            if (!(m > 0) || double.IsInfinity(m))
            {
                p.Add($"Sar.MassesGrams holds {G(m)}; every averaging mass must be a positive number of grams.");
                break;
            }
        if (MassesGrams.Distinct().Count() < MassesGrams.Count) p.Add("Sar.MassesGrams names one mass twice.");
        if (B0AxisText is { } a && ParseB0Axis(a) is null)
            p.Add($"Sar.B0Axis is '{a}'; give one of {string.Join(", ", B0AxisSpellings)}.");
        return p;
    }

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
