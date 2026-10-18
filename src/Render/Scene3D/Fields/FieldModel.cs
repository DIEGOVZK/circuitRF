// brief-em3d-29 R-em3d29-2b / 2c — the field model, GENERIC (overview §4): a mesh plus named arrays, each a
// scalar or a 3-vector, real or complex, per node or per cell. Nothing here says "electric" — the friendly
// names are a lookup the UI consults, and an array with no friendly name is still listed under its own —
// so F3's temperature is one more array, drawn by the same code.
//
// MEMORY (R-em3d29-2c): flat float arrays exactly as the files store them (Float32), never boxed and never
// widened to double on load. A step's GEOMETRY is read when it is opened; an ARRAY only when something
// asks to draw it, so listing a million-unknown run's quantities reads no field values at all.

using System.Text.RegularExpressions;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>The cells of a field mesh: the volume's tetrahedra or a boundary's triangles.</summary>
public enum FieldCellShape { Tetrahedron, Triangle }

/// <summary>
/// One step's mesh, every rank's piece merged. Cell <c>c</c>'s nodes are <c>Cells[c·NodesPerCell ..]</c>
/// in VTK's Lagrange order: the corners, then one node per edge — (0,1),(1,2),(0,2),(0,3),(1,3),(2,3) for a
/// tetrahedron, (0,1),(1,2),(2,0) for a triangle. Palace gives each cell its OWN nodes (a Nédélec field is
/// not continuous across element faces, so it shares none); openEMS's grid, split into tetrahedra, shares
/// its grid nodes. Nothing here depends on which.
/// </summary>
public sealed class FieldMesh
{
    public required FieldCellShape Shape { get; init; }
    /// <summary>The element order the nodes describe: 1 (corners only) or 2 (corners and edge midpoints).</summary>
    public required int Order { get; init; }
    public required int NodesPerCell { get; init; }
    /// <summary>x, y, z per node, in the mesh's length unit (Palace's L0).</summary>
    public required float[] Points { get; init; }
    public required int[] Cells { get; init; }
    /// <summary>Each cell's mesh attribute — the Gmsh physical group, so the object it belongs to.</summary>
    public required int[] Attribute { get; init; }
    /// <summary>Metres per mesh unit.</summary>
    public required double ToMetres { get; init; }

    public int CellCount => Attribute.Length;
    public int NodeCount => Points.Length / 3;

    /// <summary>Bytes this mesh keeps resident.</summary>
    public long ResidentBytes => 4L * (Points.Length + Cells.Length + Attribute.Length);
}

/// <summary>What an array holds, before any of its values are read.</summary>
public sealed record FieldArrayInfo(string Name, int Components, bool IsComplex, bool PerCell)
{
    /// <summary>Values per node (or cell): the components, twice over when complex (real, then imaginary).</summary>
    public int Channels => Components * (IsComplex ? 2 : 1);
}

/// <summary>A loaded array. <see cref="Im"/> is null for a real one.</summary>
public sealed class FieldArray
{
    public required FieldArrayInfo Info { get; init; }
    public required float[] Re { get; init; }
    public float[]? Im { get; init; }
    public string Name => Info.Name;
    public int Components => Info.Components;
    public long ResidentBytes => 4L * (Re.Length + (Im?.Length ?? 0));
}

/// <summary>
/// The known arrays' friendly names and units (R-em3d29-2b): the UI lists every array a file holds, and
/// these are the ones it can say more about. Keyed by the name Palace writes.
/// </summary>
public static class FieldNames
{
    // brief-em3d-100 R-em3d100-3 — the third column is how the array follows a driven field's power P: it scales as P^Drive.
    // An amplitude ½ (×√k), a quadratic quantity 1 (×k), and 0 for what no drive moves (bookkeeping, a temperature).
    private static readonly Dictionary<string, (string Friendly, string Unit, double Drive)> Known = new(StringComparer.Ordinal)
    {
        ["E"]         = ("Electric field E", "V/m", 0.5),
        ["B"]         = ("Magnetic flux density B", "T", 0.5),
        ["H"]         = ("Magnetic field H", "A/m", 0.5),
        ["J_s"]       = ("Surface current J_s", "A/m", 0.5),
        ["Q_s"]       = ("Surface charge Q_s", "C/m²", 0.5),
        ["V"]         = ("Potential V", "V", 0.5),
        ["A"]         = ("Vector potential A", "Wb/m", 0.5),
        ["S"]         = ("Poynting vector S", "W/m²", 1),
        ["U_e"]       = ("Electric energy density", "J/m³", 1),
        ["U_m"]       = ("Magnetic energy density", "J/m³", 1),
        ["Indicator"] = ("Error indicator", "", 0),
        ["Rank"]      = ("MPI rank", "", 0),
        // brief-em3d-75 — the thermal run's temperature, °C at every boundary a user or a file touches (overview §1i).
        [TemperatureArray] = ("Temperature", "°C", 0),
        // brief-sar-6 R-sar6-5 — a SAR run's volumes (FieldRun.OpenSar): a power per mass follows the drive as P, B1 as √P.
        [LocalSar] = ("Local SAR", "W/kg", 1),
        [B1Plus] = ("B1+", "µT", 0.5),
        [B1Minus] = ("B1−", "µT", 0.5),
    };

    /// <summary>brief-sar-6 — the local-SAR array, and B1+ and B1− (magnitudes, µT as loaded).</summary>
    public const string LocalSar = "SAR", B1Plus = "B1p", B1Minus = "B1m";

    private static readonly Regex AveragedSar = new(@"^SAR(\d+(?:\.\d+)?)g$", RegexOptions.CultureInvariant);

    /// <summary>brief-sar-6 — a SAR array: local SAR, or an averaged one (<c>SAR10g</c>, any mass).</summary>
    public static bool IsSar(string name) => name == LocalSar || AveragedSar.IsMatch(name);

    /// <summary>brief-sar-6 — B1+ or B1−.</summary>
    public static bool IsB1(string name) => name is B1Plus or B1Minus;

    /// <summary>brief-sar-6 R-sar6-5 — a real array that is a magnitude, never negative: drawn on a sequential map from zero, as
    /// a field's |E| is, rather than as a signed value. SAR and B1.</summary>
    public static bool IsMagnitude(string name) => IsSar(name) || IsB1(name);

    /// <summary>
    /// brief-em3d-100 — the power of the drive ratio array <paramref name="name"/> scales by: ½ for an amplitude, 1 for a power
    /// or energy density, 0 for what a drive does not move (the error indicator, a port's mode field E0_k — a mode SHAPE, not a
    /// driven field). Null for an array the table does not know, which is drawn as written and said to be not referred.
    /// </summary>
    public static double? DriveExponent(string name)
        => Known.TryGetValue(name, out var k) ? k.Drive : PortMode.IsMatch(name) ? 0 : AveragedSar.IsMatch(name) ? 1 : null;

    /// <summary>brief-em3d-75 — the array a thermal run writes its temperature in (ThermalFieldFiles).</summary>
    public const string TemperatureArray = "T_C";

    private static readonly Regex PortMode = new(@"^E0_(\d+)$", RegexOptions.CultureInvariant);

    /// <summary>A display name for array <paramref name="name"/>: the friendly one, or the name itself.</summary>
    public static string Friendly(string name)
        => Known.TryGetValue(name, out var k) ? k.Friendly
         : PortMode.Match(name) is { Success: true } m ? $"Port {m.Groups[1].Value} mode field"
         : AveragedSar.Match(name) is { Success: true } a ? $"{a.Groups[1].Value} g averaged SAR" : name;

    /// <summary>The unit of array <paramref name="name"/>, or "" when it is not a known one.</summary>
    public static string Unit(string name)
        => Known.TryGetValue(name, out var k) ? k.Unit : PortMode.IsMatch(name) ? "V/m" : AveragedSar.IsMatch(name) ? "W/kg" : "";

    /// <summary>Geometry and bookkeeping, never a quantity to draw.</summary>
    public static bool IsBookkeeping(string name) => name is "attribute" or "Rank" or "material";
}
