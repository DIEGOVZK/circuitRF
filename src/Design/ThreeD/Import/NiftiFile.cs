// brief-sar-11 R-sar11-2 — NIfTI-1 and NIfTI-2 label maps (.nii, .nii.gz), read into a LabelVolume, and NIfTI-1 written back
// (a tetrahedral mesh voxelised by circuitRF is kept as one, so the .c3d references a file anyone's tools can open).
//
// FROM THE PUBLISHED HEADER LAYOUTS (nifti1.h, nifti2.h — public-domain format definitions, read, not copied): NIfTI-1 is a 348-byte
// header with its magic at byte 344, NIfTI-2 a 540-byte one with an 8-byte magic at byte 4. Either byte order is read: the
// header's own first field (348 or 540) says which.
//
// WHAT IS READ
//   * ONE FILE ONLY (magic "n+1" / "n+2"). The two-file .hdr/.img form ("ni1"/"ni2") is refused with a sentence: the data lives
//     in another file this reader is not handed.
//   * A 3D volume: dim[0] 3, or 4/5 with the extra dimensions 1. A time series is refused.
//   * INTEGER data (uint8, int8, uint16, int16, uint32, int32, int64, uint64). A float volume is read when every value is a
//     whole number from 0 to 65,535 (a label map saved as float is common) and the import says so; any other float is refused.
//   * scl_slope / scl_inter must leave a label alone (slope 0 or 1, intercept 0): a scaled label map is refused.
//   * THE AFFINE: sform when sform_code > 0, else the qform quaternion when qform_code > 0 (qfac from pixdim[0]), else
//     pixdim on the diagonal at the origin (NIfTI's "method 1", which the import notes). Then VoxelAxes: permutations and flips
//     are applied exactly, anything turned is refused with its angle.
//   * Units: xyzt_units' spatial bits — metre, millimetre, micrometre; unstated is taken as millimetre (the format's own
//     default in practice) and noted.
//
// WHAT IS WRITTEN: NIfTI-1, little-endian, uint8 or uint16, an axis-aligned sform (sform_code 2, aligned anatomical) on the
// volume's uniform spacing in millimetres, qform_code 0. Gzipped when the path ends .gz.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace CircuitRF.Design.ThreeD.Import;

/// <summary>A NIfTI volume read: the aligned labels, and what the reading had to decide (a float volume, an unstated unit).</summary>
public sealed record NiftiRead(LabelVolume Volume, IReadOnlyList<string> Notes, int Version, string DataType, double[,] Affine);

public static class NiftiFile
{
    /// <summary>Whether <paramref name="path"/> is spelled as a NIfTI file.</summary>
    public static bool IsNifti(string path)
        => path.EndsWith(".nii", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".nii.gz", StringComparison.OrdinalIgnoreCase);

    public static NiftiRead Read(string path)
    {
        byte[] bytes;
        try
        {
            using var file = File.OpenRead(path);
            bool gz = file.Length >= 2 && file.ReadByte() == 0x1f && file.ReadByte() == 0x8b;
            file.Position = 0;
            using var ms = new MemoryStream();
            if (gz) { using var unzip = new GZipStream(file, CompressionMode.Decompress); unzip.CopyTo(ms); }
            else file.CopyTo(ms);
            bytes = ms.ToArray();
        }
        catch (InvalidDataException e) { throw new LabelVolumeException(AnatomyDiagnostics.Volume($"'{Path.GetFileName(path)}' is not a readable gzip file: {e.Message}")); }
        return Read(bytes, Path.GetFileName(path));
    }

    public static NiftiRead Read(byte[] b, string name)
    {
        string what = $"'{name}'";
        if (b.Length < 348) throw new LabelVolumeException(AnatomyDiagnostics.Volume($"{what} is {b.Length} bytes, shorter than a NIfTI header."));
        int first = BinaryPrimitives.ReadInt32LittleEndian(b);
        bool little = first is 348 or 540;
        int size = little ? first : BinaryPrimitives.ReadInt32BigEndian(b);
        if (size is not (348 or 540))
            throw new LabelVolumeException(AnatomyDiagnostics.Volume($"{what} is not a NIfTI file: its header size reads {first}, and NIfTI-1 says 348, NIfTI-2 540."));
        bool v2 = size == 540;
        if (v2 && b.Length < 540) throw new LabelVolumeException(AnatomyDiagnostics.Volume($"{what} is shorter than a NIfTI-2 header."));

        short I16(int o) => little ? BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(o));
        int I32(int o) => little ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(o));
        long I64(int o) => little ? BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(o));
        float F32(int o) => little ? BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(o));
        double F64(int o) => little ? BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(o));

        string magic = Encoding.ASCII.GetString(b, v2 ? 4 : 344, 3);
        if (magic is "ni1" or "ni2")
            throw new LabelVolumeException(AnatomyDiagnostics.Volume($"{what} is the header of a two-file NIfTI pair (.hdr/.img); give circuitRF a single-file .nii or .nii.gz."));
        if (magic != (v2 ? "n+2" : "n+1"))
            throw new LabelVolumeException(AnatomyDiagnostics.Volume($"{what} has the NIfTI-{(v2 ? 2 : 1)} header size but the magic '{magic.TrimEnd('\0')}', not '{(v2 ? "n+2" : "n+1")}'."));

        long[] dim = new long[8];
        double[] pixdim = new double[8];
        for (int k = 0; k < 8; k++)
        {
            dim[k] = v2 ? I64(16 + 8 * k) : I16(40 + 2 * k);
            pixdim[k] = v2 ? F64(104 + 8 * k) : F32(76 + 4 * k);
        }
        int datatype = v2 ? I16(12) : I16(70);
        long offset = v2 ? I64(168) : (long)F32(108);
        double slope = v2 ? F64(176) : F32(112), inter = v2 ? F64(184) : F32(116);
        int qcode = v2 ? I32(344) : I16(252), scode = v2 ? I32(348) : I16(254);
        int units = v2 ? I32(500) : b[123];

        if (dim[0] < 3 || dim[0] > 7) throw new LabelVolumeException(AnatomyDiagnostics.Volume($"{what} has {dim[0]} dimensions; a label map has 3."));
        for (int k = 4; k <= dim[0]; k++)
            if (dim[k] > 1) throw new LabelVolumeException(AnatomyDiagnostics.Volume($"{what} has {dim[k]} volumes along dimension {k} (a series); a label map is one volume."));
        int[] n = new int[3];
        for (int c = 0; c < 3; c++)
        {
            if (dim[c + 1] < 1 || dim[c + 1] > int.MaxValue) throw new LabelVolumeException(AnatomyDiagnostics.Volume($"{what} gives dimension {c + 1} as {dim[c + 1]}."));
            n[c] = (int)dim[c + 1];
        }
        long count = (long)n[0] * n[1] * n[2];
        if (count > int.MaxValue) throw new LabelVolumeException(AnatomyDiagnostics.Volume($"{what} holds {count:N0} voxels, more than one volume holds here."));
        if (!(slope == 0 || slope == 1) || inter != 0)
            throw new LabelVolumeException(AnatomyDiagnostics.Volume(string.Create(CultureInfo.InvariantCulture,
                $"{what} scales its values (scl_slope {slope}, scl_inter {inter}); a label map's values are its labels, so a scaled one is refused.")));

        var notes = new List<string>();
        double unit = (units & 7) switch { 1 => 1.0, 2 => 1e-3, 3 => 1e-6, _ => double.NaN };
        if (double.IsNaN(unit)) { unit = 1e-3; notes.Add($"{what} does not state its length unit; its voxel sizes are read as millimetres."); }

        var (typeName, width) = datatype switch
        {
            2 => ("uint8", 1), 256 => ("int8", 1), 4 => ("int16", 2), 512 => ("uint16", 2), 8 => ("int32", 4), 768 => ("uint32", 4),
            1024 => ("int64", 8), 1280 => ("uint64", 8), 16 => ("float32", 4), 64 => ("float64", 8),
            _ => throw new LabelVolumeException(AnatomyDiagnostics.Volume($"{what} stores datatype {datatype}, which is not a label map's (an integer type, or a float holding whole numbers).")),
        };
        if (offset < size || offset + count * width > b.Length)
            throw new LabelVolumeException(AnatomyDiagnostics.Volume($"{what} is truncated: its {count:N0} {typeName} voxels from byte {offset} need {offset + count * width:N0} bytes, and it has {b.Length:N0}."));

        var data = new ushort[count];
        long bad = 0, badAt = -1;
        double badValue = 0;
        Func<int, double> value = typeName switch
        {
            "uint8" => o => b[o], "int8" => o => (sbyte)b[o], "int16" => o => I16(o), "uint16" => o => (ushort)I16(o), "int32" => o => I32(o),
            "uint32" => o => (uint)I32(o), "int64" => o => I64(o), "uint64" => o => (ulong)I64(o), "float32" => o => F32(o), _ => F64,
        };
        for (long v = 0; v < count; v++)
        {
            double x = value((int)(offset + v * width));
            if (x >= 0 && x <= ushort.MaxValue && x == Math.Floor(x)) data[v] = (ushort)x;
            else { if (bad++ == 0) { badAt = v; badValue = x; } }
        }
        if (bad > 0)
            throw new LabelVolumeException(AnatomyDiagnostics.Volume(string.Create(CultureInfo.InvariantCulture,
                $"{what} holds {bad:N0} voxel(s) that are not a label from 0 to 65,535 (voxel {badAt} is {badValue}); a label map holds whole, non-negative labels.")));
        if (typeName.StartsWith("float", StringComparison.Ordinal))
            notes.Add($"{what} is stored as {typeName}; every voxel holds a whole number, so it was read as labels.");

        var affine = new double[3, 4];
        if (scode > 0)
        {
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 4; c++)
                    affine[r, c] = (v2 ? F64(400 + 32 * r + 8 * c) : F32(280 + 16 * r + 4 * c));
        }
        else if (qcode > 0)
        {
            double qb = v2 ? F64(352) : F32(256), qc = v2 ? F64(360) : F32(260), qd = v2 ? F64(368) : F32(264);
            double qa = Math.Sqrt(Math.Max(0, 1 - (qb * qb + qc * qc + qd * qd)));
            double[,] rot =
            {
                { qa * qa + qb * qb - qc * qc - qd * qd, 2 * (qb * qc - qa * qd), 2 * (qb * qd + qa * qc) },
                { 2 * (qb * qc + qa * qd), qa * qa + qc * qc - qb * qb - qd * qd, 2 * (qc * qd - qa * qb) },
                { 2 * (qb * qd - qa * qc), 2 * (qc * qd + qa * qb), qa * qa + qd * qd - qc * qc - qb * qb },
            };
            double qfac = pixdim[0] < 0 ? -1 : 1;
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++) affine[r, c] = rot[r, c] * pixdim[c + 1] * (c == 2 ? qfac : 1);
                affine[r, 3] = v2 ? F64(376 + 8 * r) : F32(268 + 4 * r);
            }
        }
        else
        {
            for (int c = 0; c < 3; c++) affine[c, c] = pixdim[c + 1];
            notes.Add($"{what} states no orientation (qform and sform codes 0); its voxels are placed from the origin along the world's axes at its pixdim sizes.");
        }
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++) affine[r, c] *= unit;

        return new NiftiRead(VoxelAxes.Align(n, data, affine, what), notes, v2 ? 2 : 1, typeName, affine);
    }

    /// <summary>
    /// <paramref name="volume"/> written as a NIfTI-1 file — gzipped when the path ends <c>.gz</c>. The volume's voxels must be
    /// uniform along each axis (an sform states one size per axis).
    /// </summary>
    public static void Write(string path, LabelVolume volume) => File.WriteAllBytes(path, ToBytes(volume, path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)));

    public static byte[] ToBytes(LabelVolume v, bool gzip)
    {
        var (dx, dy, dz) = v.Spacing;
        if (dx is null || dy is null || dz is null)
            throw new LabelVolumeException(AnatomyDiagnostics.Volume("The volume's voxels are not uniform along every axis, which a NIfTI file cannot state."));
        bool wide = v.Labels.Any(l => l > byte.MaxValue);
        const int offset = 352;
        var b = new byte[offset + v.Count * (wide ? 2 : 1)];
        var s = b.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s, 348);
        short[] dim = [3, (short)v.Nx, (short)v.Ny, (short)v.Nz, 1, 1, 1, 1];
        if (v.Nx > short.MaxValue || v.Ny > short.MaxValue || v.Nz > short.MaxValue)
            throw new LabelVolumeException(AnatomyDiagnostics.Volume($"The volume is {v.Dimensions} voxels; a NIfTI-1 dimension holds at most {short.MaxValue}."));
        for (int k = 0; k < 8; k++) BinaryPrimitives.WriteInt16LittleEndian(s[(40 + 2 * k)..], dim[k]);
        BinaryPrimitives.WriteInt16LittleEndian(s[70..], (short)(wide ? 512 : 2));
        BinaryPrimitives.WriteInt16LittleEndian(s[72..], (short)(wide ? 16 : 8));
        float[] pix = [1, (float)(dx.Value * 1e3), (float)(dy.Value * 1e3), (float)(dz.Value * 1e3), 1, 1, 1, 1];
        for (int k = 0; k < 8; k++) BinaryPrimitives.WriteSingleLittleEndian(s[(76 + 4 * k)..], pix[k]);
        BinaryPrimitives.WriteSingleLittleEndian(s[108..], offset);
        BinaryPrimitives.WriteSingleLittleEndian(s[112..], 1);
        b[123] = 2;                                                     // millimetres
        BinaryPrimitives.WriteInt16LittleEndian(s[254..], 2);          // sform: aligned anatomical
        double[] size = [dx.Value, dy.Value, dz.Value];
        double[][] lines = [v.X, v.Y, v.Z];
        for (int r = 0; r < 3; r++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(s[(280 + 16 * r + 4 * r)..], (float)(size[r] * 1e3));
            BinaryPrimitives.WriteSingleLittleEndian(s[(280 + 16 * r + 12)..], (float)((lines[r][0] + size[r] / 2) * 1e3));
        }
        Encoding.ASCII.GetBytes("n+1\0").CopyTo(s[344..]);
        // NIfTI's order: i fastest
        int nx = v.Nx, ny = v.Ny, nz = v.Nz;
        long at = 0;
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++, at++)
                {
                    ushort l = v.Labels[v.Index(i, j, k)];
                    if (wide) BinaryPrimitives.WriteUInt16LittleEndian(s[(int)(offset + 2 * at)..], l);
                    else b[offset + at] = (byte)l;
                }
        if (!gzip) return b;
        using var ms = new MemoryStream();
        using (var z = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(b);
        return ms.ToArray();
    }
}
