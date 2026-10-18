// brief-sar-11 R-sar11-2 — a raw binary label volume: nothing in the file says what it is, so everything is stated — the
// dimensions, the integer type, the voxel size, the byte order and a header to skip — by the import dialog or by CLI flags.
// The voxels are x fastest, then y, then z (the order NIfTI and every raw export the format sees use); the volume's lowest
// corner is at the origin of its own frame, which the body's placement then moves.
//
// The file's length is checked against what the layout states, both ways: a file too short is refused, and so is one LONGER
// than the stated voxels plus header, because a wrong dimension or type almost always shows up exactly there and reading the
// first part of a larger file would give a volume of the right size and the wrong anatomy.

using System.Buffers.Binary;
using System.Globalization;

namespace CircuitRF.Design.ThreeD.Import;

/// <summary>The integer types a raw label volume is read as.</summary>
public enum RawVoxelType { UInt8, Int8, UInt16, Int16, UInt32, Int32 }

/// <summary>What a raw volume's bytes mean: voxels along x, y, z, their type, size (metres), byte order and a header to skip.</summary>
public sealed record RawVolumeLayout(int Nx, int Ny, int Nz, RawVoxelType Type, double VoxelXM, double VoxelYM, double VoxelZM,
                                     bool BigEndian = false, long HeaderBytes = 0)
{
    public int Width => Type switch { RawVoxelType.UInt8 or RawVoxelType.Int8 => 1, RawVoxelType.UInt16 or RawVoxelType.Int16 => 2, _ => 4 };

    /// <summary>What is wrong with the layout itself, or null.</summary>
    public string? Fault()
    {
        if (!Enum.IsDefined(Type)) return "The raw volume's type must be one of uint8, int8, uint16, int16, uint32 or int32.";
        if (Nx < 1 || Ny < 1 || Nz < 1) return "Each of the raw volume's dimensions must be at least 1.";
        if (!(VoxelXM > 0 && VoxelYM > 0 && VoxelZM > 0)) return "The raw volume's voxel size must be positive along every axis.";
        if (HeaderBytes < 0) return "The raw volume's header length cannot be negative.";
        if ((long)Nx * Ny * Nz > int.MaxValue) return $"The raw volume would be {(long)Nx * Ny * Nz:N0} voxels, more than one volume holds here.";
        return null;
    }

    /// <summary>The type as the CLI spells it.</summary>
    public static RawVoxelType? ParseType(string text) => text.ToLowerInvariant() switch
    {
        "uint8" or "u8" => RawVoxelType.UInt8, "int8" or "i8" => RawVoxelType.Int8, "uint16" or "u16" => RawVoxelType.UInt16,
        "int16" or "i16" => RawVoxelType.Int16, "uint32" or "u32" => RawVoxelType.UInt32, "int32" or "i32" => RawVoxelType.Int32,
        _ => null,
    };
}

public static class RawVolume
{
    /// <summary>Extensions read as a raw volume.</summary>
    public static bool IsRaw(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".raw" or ".bin" or ".vol";

    public static LabelVolume Read(string path, RawVolumeLayout layout, IReadOnlyDictionary<int, string>? names = null)
    {
        if (layout.Fault() is { } fault) throw new LabelVolumeException(AnatomyDiagnostics.Volume(fault));
        string what = $"'{Path.GetFileName(path)}'";
        long count = (long)layout.Nx * layout.Ny * layout.Nz;
        long want = layout.HeaderBytes + count * layout.Width;
        long have = new FileInfo(path).Length;
        if (have != want)
            throw new LabelVolumeException(AnatomyDiagnostics.Volume(string.Create(CultureInfo.InvariantCulture,
                $"{what} is {have:N0} bytes, and {layout.Nx} × {layout.Ny} × {layout.Nz} {layout.Type} voxels after a {layout.HeaderBytes}-byte header are {want:N0}.") +
                " Check the dimensions, the type and the header length."));
        byte[] b = File.ReadAllBytes(path);
        var data = new ushort[count];
        long bad = 0, badAt = -1, badValue = 0;
        for (long v = 0; v < count; v++)
        {
            var s = b.AsSpan((int)(layout.HeaderBytes + v * layout.Width));
            long x = layout.Type switch
            {
                RawVoxelType.UInt8 => s[0],
                RawVoxelType.Int8 => (sbyte)s[0],
                RawVoxelType.UInt16 => layout.BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s),
                RawVoxelType.Int16 => layout.BigEndian ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s),
                RawVoxelType.UInt32 => layout.BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s),
                _ => layout.BigEndian ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s),
            };
            if (x is >= 0 and <= ushort.MaxValue) data[v] = (ushort)x;
            else if (bad++ == 0) { badAt = v; badValue = x; }
        }
        if (bad > 0)
            throw new LabelVolumeException(AnatomyDiagnostics.Volume($"{what} holds {bad:N0} voxel(s) outside 0 to 65,535 (voxel {badAt} is {badValue}); a label map holds non-negative labels."));
        var affine = new double[3, 4];
        affine[0, 0] = layout.VoxelXM; affine[1, 1] = layout.VoxelYM; affine[2, 2] = layout.VoxelZM;
        affine[0, 3] = layout.VoxelXM / 2; affine[1, 3] = layout.VoxelYM / 2; affine[2, 3] = layout.VoxelZM / 2;
        return VoxelAxes.Align([layout.Nx, layout.Ny, layout.Nz], data, affine, what, names);
    }
}

/// <summary>One row of a label table read from CSV: a label, its name, and the material it is mapped to when the CSV says.</summary>
public sealed record LabelRow(int Label, string Name, string? Material);

/// <summary>
/// brief-sar-11 — a label list as CSV: <c>label,name[,material]</c> per row; a first row whose label is not a number is a header;
/// blank lines and lines starting with <c>#</c> are skipped. A name may be quoted. A label given twice is refused.
/// </summary>
public static class LabelTableCsv
{
    public static IReadOnlyList<LabelRow> Read(string path) => Parse(File.ReadAllLines(path), Path.GetFileName(path));

    public static IReadOnlyList<LabelRow> Parse(IEnumerable<string> lines, string name)
    {
        var rows = new List<LabelRow>();
        var seen = new HashSet<int>();
        int n = 0;
        foreach (string raw in lines)
        {
            n++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var cells = Split(line);
            if (!int.TryParse(cells[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int label))
            {
                if (rows.Count == 0 && seen.Count == 0 && n == FirstDataLine(lines)) continue;     // a header
                throw new LabelVolumeException(AnatomyDiagnostics.Labels($"'{name}' line {n}: '{cells[0]}' is not a label number."));
            }
            if (label is < 0 or > ushort.MaxValue) throw new LabelVolumeException(AnatomyDiagnostics.Labels($"'{name}' line {n}: label {label} is outside 0 to 65,535."));
            if (!seen.Add(label)) throw new LabelVolumeException(AnatomyDiagnostics.Labels($"'{name}' gives label {label} twice (line {n})."));
            string tissue = cells.Count > 1 && cells[1].Length > 0 ? cells[1] : $"label {label}";
            string? material = cells.Count > 2 && cells[2].Length > 0 ? cells[2] : null;
            rows.Add(new LabelRow(label, tissue, material));
        }
        return rows;
    }

    private static int FirstDataLine(IEnumerable<string> lines)
    {
        int n = 0;
        foreach (string raw in lines)
        {
            n++;
            string line = raw.Trim();
            if (line.Length > 0 && !line.StartsWith('#')) return n;
        }
        return -1;
    }

    private static List<string> Split(string line)
    {
        var cells = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cur.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c is ',' or ';' or '\t') { cells.Add(cur.ToString().Trim()); cur.Clear(); }
            else cur.Append(c);
        }
        cells.Add(cur.ToString().Trim());
        return cells;
    }
}
