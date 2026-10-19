// brief-sar-6 R-sar6-7 — the files a SAR run leaves beside its raw dump in each driven port's directory, and the record that
// names them. The volumes are plain NumPy arrays — float32, C order over (x, y, z), the engine's own order (sar.md §6.2) — and
// are never inlined into the run's DataSet: a whole-body volume is hundreds of megabytes, and the DataSet is what the Data
// Display loads whole. They sit in the RUN directory, not in results/, because the Data Display lists every .npy in results/ as
// a data source, and a bare array is not one.
//
//   p<k>/sar<j>.h5           openEMS's raw SAR dump (DumpType 29) at the j-th SAR frequency
//   p<k>/sar<j>.json         SarRunRecord: what was computed there, AS SOLVED, and the volumes' names
//   p<k>/sar<j>.<Q>.npy      a volume: SAR (local), SAR<m>g per mass, B1p and B1m (|B1±|, T)
//   p<k>/sar<j>.x.npy …      the cell centres along x, y and z, float64, metres
//
// AS SOLVED (R-sar6-3) means openEMS's own scale: the dump's E and H exactly as openEMS wrote them, and the accepted power in
// openEMS's own DFT convention, so SAR / P_acc and B1 / √P_acc are per watt accepted exactly as openEMS's tutorials compute
// them. circuitRF's FdtdPortTransform.Dft carries no ×2 (FieldDrive.OpenEmsDumpScale), so P_acc here is 2·Re(U·I*) of its U
// and I — ½·Re of openEMS's own U and I, each twice circuitRF's.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.Em3d;

/// <summary>A peak as a SAR run found it: its quantity, its value as solved, its cell and centre, and — for an averaged quantity
/// — the cube it came from and how the cell got its value.</summary>
public sealed class SarPeakRecord
{
    public string Quantity { get; set; } = "";
    public double Value { get; set; }
    public int[] Cell { get; set; } = [];
    public double[] CentreM { get; set; } = [];
    public double[]? CubeLoM { get; set; }
    public double[]? CubeHiM { get; set; }
    public double? CubeMassKg { get; set; }
    /// <summary>Valid, Used or Unused (SarCubeKind); null for local SAR.</summary>
    public string? Kind { get; set; }
    /// <summary>The solid the peak cell lies in, and its material.</summary>
    public string? Object { get; set; }
    public string? Material { get; set; }
}

/// <summary>A mass's peak beside openEMS's sar_calc's on the same dump, as solved (R-sar5-6).</summary>
public sealed class SarCrossCheckRecord
{
    public double MassGrams { get; set; }
    public double Peak { get; set; }
    public double? SarCalcPeak { get; set; }
    public double? RelativeDifference { get; set; }
}

/// <summary>R-sar6-6/7 — one driven port at one SAR frequency: everything the readouts show, as solved.</summary>
public sealed class SarRunRecord
{
    public int Port { get; set; }
    public double FrequencyHz { get; set; }
    /// <summary>The SAR frequency's index in the setup (the <c>j</c> of <c>sar&lt;j&gt;</c>, 1-based).</summary>
    public int Index { get; set; }
    /// <summary>The power accepted at the driven port, W, in openEMS's convention (the header).</summary>
    public double AcceptedPowerW { get; set; }
    /// <summary>Radiated over accepted power, when the setup asked for a radiation pattern.</summary>
    public double? RadiationEfficiency { get; set; }
    public string Region { get; set; } = "";
    public double AbsorbedPowerW { get; set; }
    public double TissueMassKg { get; set; }
    public double UncountedPowerW { get; set; }
    public List<SarPeakRecord> Peaks { get; set; } = [];
    public string? B1Region { get; set; }
    public double? B1pMeanT { get; set; }
    public string? B0Axis { get; set; }
    public int[] Shape { get; set; } = [];

    /// <summary>brief-sar-8b — <c>cylindrical</c> when the volumes are on a cylindrical grid: their cells are (ρ, α, z) and the lines
    /// files hold ρ (m), α (rad) and z (m), so nothing that reads a Cartesian volume may read them. Null — every record before it —
    /// is Cartesian.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Grid { get; set; }

    /// <summary>The volumes are a cylindrical grid's.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCylindrical => Grid == CylindricalGrid;

    /// <summary>What <see cref="Grid"/> says of a cylindrical grid.</summary>
    public const string CylindricalGrid = "cylindrical";
    /// <summary>The volumes written, by quantity name.</summary>
    public List<string> Volumes { get; set; } = [];
    public List<SarCrossCheckRecord>? CrossCheck { get; set; }

    /// <summary>brief-sar-9 — the power the per-watt values are referred to, W, when it is not <see cref="AcceptedPowerW"/>: a
    /// fields analysis normalised to its drive's AVAILABLE power. Null: accepted, as every 3D run is.</summary>
    public double? ReferencePowerW { get; set; }

    // ── normalised to 1 W accepted (R-sar6-3) ───────────────────────────────────────────────────────

    /// <summary>The power every per-watt value divides by: <see cref="ReferencePowerW"/>, else the accepted power.</summary>
    public double PerWattDivisor => ReferencePowerW ?? AcceptedPowerW;

    /// <summary>The peak of <paramref name="quantity"/>, W/kg per W accepted; null when the run found none.</summary>
    public double? PeakPerWatt(string quantity)
        => Peaks.FirstOrDefault(p => p.Quantity == quantity) is { } p && PerWattDivisor > 0 ? p.Value / PerWattDivisor : null;

    public double? WholeSarPerWatt => TissueMassKg > 0 && PerWattDivisor > 0 ? AbsorbedPowerW / TissueMassKg / PerWattDivisor : null;
    public double? AbsorbedFraction => PerWattDivisor > 0 ? AbsorbedPowerW / PerWattDivisor : null;
    public double? B1pMeanPerSqrtWatt => B1pMeanT is { } b && PerWattDivisor > 0 ? b / Math.Sqrt(PerWattDivisor) : null;

    /// <summary>(absorbed + radiated) / accepted, a fraction; null without a radiation pattern.</summary>
    public double? PowerBudget => RadiationEfficiency is { } eta && AbsorbedFraction is { } a ? a + eta : null;

    /// <summary>The mass SAR efficiency is taken over: 10 g when averaged, else the largest averaged mass.</summary>
    public string? EfficiencyQuantity
        => Peaks.Any(p => p.Quantity == "SAR10g") ? "SAR10g"
         : Peaks.Where(p => p.Quantity != SarVolumeFiles.LocalQuantity && p.Quantity.StartsWith("SAR", StringComparison.Ordinal))
                .Select(p => p.Quantity).LastOrDefault();

    /// <summary>|B1+|mean / √psSAR, T per √(W/kg): per watt or as solved, the same number.</summary>
    public double? SarEfficiency
        => B1pMeanPerSqrtWatt is { } b && EfficiencyQuantity is { } q && PeakPerWatt(q) is { } s && s > 0 ? b / Math.Sqrt(s) : null;
}

public static class SarVolumeFiles
{
    /// <summary>The local-SAR volume's quantity.</summary>
    public const string LocalQuantity = "SAR";

    /// <summary>B1+ and B1− magnitudes.</summary>
    public const string B1PlusQuantity = "B1p", B1MinusQuantity = "B1m";

    /// <summary>The stem every file of the j-th SAR frequency (0-based) shares with its raw dump: <c>sar1</c>.</summary>
    public static string Stem(int j) => Em3dSar.RawDump(j);

    public static string RecordFile(int j) => Stem(j) + ".json";
    public static string VolumeFile(int j, string quantity) => $"{Stem(j)}.{quantity}.npy";
    public static string LinesFile(int j, char axis) => $"{Stem(j)}.{axis}.npy";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void WriteRecord(string path, SarRunRecord record) => File.WriteAllText(path, JsonSerializer.Serialize(record, Json));

    /// <summary>The record at <paramref name="path"/>, or null when it is absent or unreadable.</summary>
    public static SarRunRecord? ReadRecord(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<SarRunRecord>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static readonly Regex RecordName = new(@"^sar(\d+)\.json$", RegexOptions.CultureInvariant);

    /// <summary>Every record in run directory <paramref name="runDir"/>'s port folders, by port then frequency, each with its
    /// folder.</summary>
    public static IReadOnlyList<(string Directory, SarRunRecord Record)> ReadAll(string runDir)
    {
        var list = new List<(string, SarRunRecord)>();
        if (!Directory.Exists(runDir)) return list;
        foreach (string dir in Directory.EnumerateDirectories(runDir, "p*"))
            foreach (string file in Directory.EnumerateFiles(dir, "sar*.json"))
                if (RecordName.IsMatch(Path.GetFileName(file)) && ReadRecord(file) is { } r) list.Add((dir, r));
        return [.. list.OrderBy(x => x.Item2.Port).ThenBy(x => x.Item2.FrequencyHz)];
    }

    // ── plain .npy arrays ──────────────────────────────────────────────────────────────────────────

    /// <summary>Writes <paramref name="values"/> as a float32 array of <paramref name="shape"/>, C order.</summary>
    public static void WriteFloat32(string path, ReadOnlySpan<float> values, params int[] shape)
        => Write(path, "<f4", System.Runtime.InteropServices.MemoryMarshal.AsBytes(values), shape);

    /// <summary>Writes <paramref name="values"/> as a float64 vector.</summary>
    public static void WriteFloat64(string path, ReadOnlySpan<double> values)
        => Write(path, "<f8", System.Runtime.InteropServices.MemoryMarshal.AsBytes(values), values.Length);

    private static void Write(string path, string descr, ReadOnlySpan<byte> data, params int[] shape)
    {
        string dims = shape.Length == 1 ? $"{shape[0]}," : string.Join(", ", shape.Select(s => s.ToString(CultureInfo.InvariantCulture)));
        string dict = $"{{'descr': '{descr}', 'fortran_order': False, 'shape': ({dims}), }}";
        int total = 10 + dict.Length + 1;
        int pad = (64 - total % 64) % 64;
        string header = dict + new string(' ', pad) + "\n";
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            fs.Write([0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0]);
            fs.Write(BitConverter.GetBytes((ushort)header.Length));
            fs.Write(Encoding.ASCII.GetBytes(header));
            fs.Write(data);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>A float32 or float64 array written by <see cref="WriteFloat32"/> or <see cref="WriteFloat64"/> (or NumPy, C
    /// order, little-endian): its values as float and its shape.</summary>
    public static (float[] Values, int[] Shape) ReadFloat32(string path)
    {
        var (descr, shape, data) = Read(path);
        int n = shape.Aggregate(1, (a, x) => a * x);
        return descr switch
        {
            "<f4" => (System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(data.AsSpan(0, n * 4)).ToArray(), shape),
            "<f8" => ([.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(data.AsSpan(0, n * 8)).ToArray().Select(v => (float)v)], shape),
            _ => throw new InvalidDataException($"'{path}' holds {descr}; a SAR volume is float32."),
        };
    }

    /// <summary>A float64 (or float32) vector.</summary>
    public static double[] ReadFloat64(string path)
    {
        var (descr, shape, data) = Read(path);
        int n = shape.Aggregate(1, (a, x) => a * x);
        return descr switch
        {
            "<f8" => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(data.AsSpan(0, n * 8)).ToArray(),
            "<f4" => [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(data.AsSpan(0, n * 4)).ToArray().Select(v => (double)v)],
            _ => throw new InvalidDataException($"'{path}' holds {descr}; mesh lines are float64."),
        };
    }

    private static (string Descr, int[] Shape, byte[] Data) Read(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 10 || b[0] != 0x93 || Encoding.ASCII.GetString(b, 1, 5) != "NUMPY")
            throw new InvalidDataException($"'{path}' is not a NumPy array.");
        int headerLen = b[6] == 1 ? BitConverter.ToUInt16(b, 8) : BitConverter.ToInt32(b, 8);
        int start = (b[6] == 1 ? 10 : 12) + headerLen;
        string header = Encoding.ASCII.GetString(b, start - headerLen, headerLen);
        if (!header.Contains("'fortran_order': False", StringComparison.Ordinal))
            throw new InvalidDataException($"'{path}' is in Fortran order; a SAR volume is C order.");
        string descr = Regex.Match(header, @"'descr': '([^']+)'").Groups[1].Value;
        int[] shape = [.. Regex.Match(header, @"'shape': \(([^)]*)\)").Groups[1].Value
                              .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
        return (descr, shape, b[start..]);
    }
}
