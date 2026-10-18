// brief-sar-11 R-sar11-1/-3 — a voxel body from its file to the problem: loaded (cached by the file's identity), cropped, placed,
// its labels mapped to tissues, and each tissue given a DRAWING.
//
// LOADED ONCE. A whole-body volume is hundreds of megabytes of labels; re-reading it on every elaboration (every edit in the
// 3D view) would make the editor unusable. The volume is cached by the file's full path, length, write time and — for a raw
// file — its stated layout, so editing the label table or the placement re-uses it and replacing the file re-reads it.
//
// LABEL 0 IS AIR unless the table maps it; a row with Air is air too. Every other label a voxel of the cropped volume carries
// must have a row with a material, or the body is REFUSED, listing every unmapped label with its voxel count — a tissue left
// out in silence would be air in the run, and the SAR in its neighbours wrong with nothing to say so.
//
// CROPPED TO WHAT IS THERE. After the crop box (if any), the volume is cut down to the voxels that hold tissue, so neither the
// DiscMaterial file nor the grid carries a frame of air around the anatomy.
//
// PLACED BY QUARTER TURNS. The FDTD grid is axis-aligned, so the body's world transform must map each axis onto an axis:
// voxels are then moved, exactly, and non-uniform boundaries (a DiscMaterial file may have them) survive. Any other rotation
// is refused with its angle (VoxelAxes' rule, so a file's own orientation and a body's placement say the same thing).
//
// ONE TISSUE PER MAPPED ROW. Its solid is "<body>/<row>", the phantom's spelling, made of the row's material; its entry in the
// Em3dLabelVolume is its place among the rows used (1-based). CSXCAD's DiscMaterial addresses a uint8 index, so at most 255
// tissues: more is refused, naming the limit (sar.md §6.4).
//
// THE DRAWING is display only, never simulated: the volume decimated to at most DisplayBlocks blocks along its longest axis
// (each block the label at its centre voxel), and every block face between two different tissues (or tissue and air) emitted
// for the tissue on its side. It is an Em3dShapeSolid with no B-rep whose one face's box is the tissue's EXACT voxel bounds,
// so the grid, the SAR box and the view all read the true extent from it.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Design.ThreeD.Import;
using CircuitRF.Engine.Em3d;
using RfCore.Hdf5;

namespace CircuitRF.Design.ThreeD;

/// <summary>One tissue of a resolved body: its row, its entry in the volume, and its drawing.</summary>
public sealed record C3dVoxelTissue(C3dVoxelLabel Row, int Entry, long Voxels, Em3dShapeSolid Drawing);

/// <summary>A voxel body resolved for one placement: the world-aligned volume, its tissues, and what was said on the way.</summary>
public sealed record C3dVoxelResolved(Em3dLabelVolume Volume, IReadOnlyList<C3dVoxelTissue> Tissues, IReadOnlyList<string> Notes);

public static class C3dVoxelBodies
{
    /// <summary>The most blocks the drawing has along the body's longest axis.</summary>
    public const int DisplayBlocks = 96;

    /// <summary>The most tissues one body may have: CSXCAD's uint8 DiscMaterial index, less entry 0 (air).</summary>
    public const int MaxTissues = DiscMaterialWriter.MaxEntries - 1;

    /// <summary>The file a body names, resolved against its <c>.c3d</c>.</summary>
    public static string FileOf(C3dVoxelBody b, string c3dPath)
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(c3dPath))!, b.File));

    /// <summary>The kind of file <paramref name="path"/> is read as: nifti, discmaterial, raw — by its spelling.</summary>
    public static string FormatOf(string path)
        => NiftiFile.IsNifti(path) ? "nifti" : Path.GetExtension(path).ToLowerInvariant() is ".h5" or ".hdf5" ? "discmaterial" : "raw";

    /// <summary>A raw layout from the body's DBU fields; null when it states none.</summary>
    public static RawVolumeLayout? LayoutOf(C3dVoxelRaw? raw, int dbuPerMicron)
    {
        if (raw is null) return null;
        double M(long v) => C3dLowering.Metres(v, dbuPerMicron);
        return new RawVolumeLayout(raw.Nx, raw.Ny, raw.Nz, RawVolumeLayout.ParseType(raw.Type) ?? (RawVoxelType)(-1), M(raw.VoxelX), M(raw.VoxelY),
                                   M(raw.VoxelZ), raw.BigEndian, raw.HeaderBytes);
    }

    // ── loading, cached ────────────────────────────────────────────────────────────────────────

    private sealed record Loaded(string Key, LabelVolume Volume, IReadOnlyList<string> Notes);
    private static readonly List<Loaded> Cache = [];
    private static readonly object Gate = new();

    /// <summary>Loads read from disk (cache misses) — a counter for the gates.</summary>
    public static int Loads { get; private set; }

    /// <summary>The volume in <paramref name="path"/>, read as its spelling says (a raw file by <paramref name="raw"/>) — or the
    /// refusal as a sentence.</summary>
    public static (LabelVolume? Volume, IReadOnlyList<string> Notes, string? Refusal) Load(string path, RawVolumeLayout? raw)
    {
        if (!File.Exists(path)) return (null, [], $"'{Path.GetFileName(path)}' is not there.");
        var info = new FileInfo(path);
        string key = string.Create(CultureInfo.InvariantCulture, $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{raw}");
        lock (Gate)
        {
            if (Cache.FirstOrDefault(c => c.Key == key) is { } hit) return (hit.Volume, hit.Notes, null);
        }
        LabelVolume volume;
        IReadOnlyList<string> notes = [];
        try
        {
            switch (FormatOf(path))
            {
                case "nifti":
                    var read = NiftiFile.Read(path);
                    volume = read.Volume;
                    notes = read.Notes;
                    break;
                case "discmaterial":
                    var disc = OpenEmsHdf5.ReadDiscMaterial(path);
                    volume = new LabelVolume(disc.X, disc.Y, disc.Z, Array.ConvertAll(disc.Indices, i => (ushort)i),
                                             disc.Tissues.Select((t, k) => (k, t.Name)).ToDictionary(t => t.k, t => t.Name));
                    break;
                default:
                    if (raw is null)
                        return (null, [], $"'{Path.GetFileName(path)}' is a raw volume, and the body states no Raw layout (dimensions, type, voxel size).");
                    volume = RawVolume.Read(path, raw);
                    break;
            }
        }
        catch (LabelVolumeException e) { return (null, [], e.Message); }
        catch (OpenEmsHdf5Exception e) { return (null, [], e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, [], $"'{Path.GetFileName(path)}' could not be read: {e.Message}"); }
        lock (Gate)
        {
            Loads++;
            Cache.Insert(0, new Loaded(key, volume, notes));
            while (Cache.Count > 2) Cache.RemoveAt(Cache.Count - 1);
        }
        return (volume, notes, null);
    }

    // ── the label table ────────────────────────────────────────────────────────────────────────

    /// <summary>Whether <paramref name="label"/> is air for <paramref name="b"/>: a row says so, or it is 0 with no row.</summary>
    public static bool IsAir(C3dVoxelBody b, int label)
        => b.Labels.FirstOrDefault(r => r.Label == label) is { } row ? row.Air : label == 0;

    /// <summary>The labels voxels carry that no row maps to a material (and that are not air), each with its voxel count.</summary>
    public static IReadOnlyList<(int Label, long Voxels)> Unmapped(C3dVoxelBody b, IReadOnlyDictionary<int, long> histogram)
        => [.. histogram.Where(h => !IsAir(b, h.Key) && b.Labels.FirstOrDefault(r => r.Label == h.Key) is not { Material.Length: > 0 })
                        .Select(h => (h.Key, h.Value))];

    /// <summary>The refusal for <paramref name="unmapped"/>, naming each label and its voxel count.</summary>
    public static string UnmappedRefusal(string body, IReadOnlyList<(int Label, long Voxels)> unmapped)
        => $"The voxel body '{body}' has {unmapped.Count} label{(unmapped.Count == 1 ? "" : "s")} its table maps to no material: " +
           string.Join(", ", unmapped.Select(u => string.Create(CultureInfo.InvariantCulture, $"{u.Label} ({u.Voxels:N0} voxel{(u.Voxels == 1 ? "" : "s")})"))) +
           ". Give each a material, or mark it Air.";

    /// <summary>The crop box, clamped to the volume; the whole volume without one.</summary>
    public static (int I0, int J0, int K0, int I1, int J1, int K1) CropOf(C3dVoxelBody b, LabelVolume v)
    {
        if (b.Crop is not { } c) return (0, 0, 0, v.Nx, v.Ny, v.Nz);
        return (Math.Clamp(c.I0, 0, v.Nx), Math.Clamp(c.J0, 0, v.Ny), Math.Clamp(c.K0, 0, v.Nz),
                Math.Clamp(c.I1, 0, v.Nx), Math.Clamp(c.J1, 0, v.Ny), Math.Clamp(c.K1, 0, v.Nz));
    }

    // ── resolving ──────────────────────────────────────────────────────────────────────────────

    private sealed record ResolvedEntry(string Key, C3dVoxelResolved Value);
    private static readonly List<ResolvedEntry> Resolved = [];

    /// <summary>
    /// <paramref name="b"/>, named <paramref name="name"/>, under <paramref name="world"/> (its own placement then every instance
    /// above it; translation in metres): the world-aligned volume of its tissues, or the refusal.
    /// </summary>
    public static (C3dVoxelResolved? Body, string? Refusal) Resolve(C3dVoxelBody b, string name, string c3dPath, C3dTransform world,
                                                                    int dbuPerMicron)
    {
        string file = FileOf(b, c3dPath);
        var (volume, loadNotes, refusal) = Load(file, LayoutOf(b.Raw, dbuPerMicron));
        if (volume is null) return (null, $"The voxel body '{name}': {refusal}");
        string key = string.Create(CultureInfo.InvariantCulture,
            $"{RuntimeHelpers(volume)}|{name}|{C3dPersistence.SerializeResolved(b)}|{world}|{dbuPerMicron}");
        lock (Gate)
        {
            if (Resolved.FirstOrDefault(r => r.Key == key) is { } hit) return (hit.Value, null);
        }

        var crop = CropOf(b, volume);
        if (crop.I1 <= crop.I0 || crop.J1 <= crop.J0 || crop.K1 <= crop.K0)
            return (null, $"The voxel body '{name}' crops its volume to nothing ({volume.Dimensions} voxels).");
        var histogram = volume.Histogram(crop);
        var unmapped = Unmapped(b, histogram);
        if (unmapped.Count > 0) return (null, UnmappedRefusal(name, unmapped));
        var rows = b.Labels.Where(r => !r.Air && r.Material is { Length: > 0 } && histogram.ContainsKey(r.Label)).ToList();
        if (rows.Count == 0) return (null, $"The voxel body '{name}' holds no tissue: every voxel it keeps is air.");
        if (rows.Count > MaxTissues)
            return (null, $"The voxel body '{name}' has {rows.Count} tissues, and openEMS's DiscMaterial addresses at most {MaxTissues} " +
                          "(a one-byte index). Map labels that share a material to one row.");
        var entryOf = new byte[65536];
        for (int k = 0; k < rows.Count; k++) entryOf[rows[k].Label] = (byte)(k + 1);
        var occupied = volume.Occupied(l => entryOf[l] == 0, crop)!.Value;

        // the cut-down volume, in the file's aligned frame
        var (i0, j0, k0, i1, j1, k1) = occupied;
        int nx = i1 - i0, ny = j1 - j0, nz = k1 - k0;
        var entries = new byte[(long)nx * ny * nz];
        Parallel.For(0, nx, i =>
        {
            for (int j = 0; j < ny; j++)
            {
                long src = volume.Index(i0 + i, j0 + j, k0);
                long dst = ((long)i * ny + j) * nz;
                for (int k = 0; k < nz; k++) entries[dst + k] = entryOf[volume.Labels[src + k]];
            }
        });
        double[] X = volume.X[i0..(i1 + 1)], Y = volume.Y[j0..(j1 + 1)], Z = volume.Z[k0..(k1 + 1)];

        // placed: quarter turns move voxels exactly; anything else is refused with its angle
        var t = C3dLowering.Snapped(world);
        if (!C3dLowering.IsSignedPermutation(t))
        {
            var affine = new double[3, 4] { { t.M00, t.M01, t.M02, 0 }, { t.M10, t.M11, t.M12, 0 }, { t.M20, t.M21, t.M22, 0 } };
            var (_, why) = VoxelAxes.Of(affine, $"The voxel body '{name}'");
            return (null, (why ?? $"The voxel body '{name}' is turned off the world's axes.").Replace("Resample the volume onto the world's axes first; circuitRF does not turn a label map.",
                          "Turn it by quarter turns only."));
        }
        var (wx, wy, wz, placed) = Place(X, Y, Z, entries, t);
        string[] tissueNames = [.. rows.Select(r => C3dPhantoms.LayerName(name, r.Name))];
        var em = new Em3dLabelVolume(name, wx, wy, wz, placed, tissueNames, C3dLowering.Metres(b.GridStep, dbuPerMicron));

        var notes = new List<string>(loadNotes);
        var tissues = Drawings(em, rows, BodyHash(b, c3dPath));
        var value = new C3dVoxelResolved(em, tissues, notes);
        lock (Gate)
        {
            Resolved.Insert(0, new ResolvedEntry(key, value));
            while (Resolved.Count > 4) Resolved.RemoveAt(Resolved.Count - 1);
        }
        return (value, null);
    }

    private static int RuntimeHelpers(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);

    private static string BodyHash(C3dVoxelBody b, string c3dPath)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(FileOf(b, c3dPath) + "|" + C3dPersistence.SerializeResolved(b))))[..16];

    /// <summary>The volume (boundaries X, Y, Z; entries in C order) carried by the signed permutation <paramref name="t"/>.</summary>
    private static (double[] X, double[] Y, double[] Z, byte[] Entries) Place(double[] X, double[] Y, double[] Z, byte[] entries, C3dTransform t)
    {
        double[][] src = [X, Y, Z];
        double[] m = [t.M00, t.M01, t.M02, t.M10, t.M11, t.M12, t.M20, t.M21, t.M22];
        double[] shift = [t.Tx, t.Ty, t.Tz];
        int[] n = [X.Length - 1, Y.Length - 1, Z.Length - 1];
        var worldOf = new int[3];        // source axis c → world axis
        var sign = new int[3];
        var lines = new double[3][];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                if (m[3 * r + c] != 0)
                {
                    worldOf[c] = r;
                    sign[c] = m[3 * r + c] > 0 ? 1 : -1;
                    lines[r] = sign[c] > 0 ? [.. src[c].Select(v => v + shift[r])] : [.. src[c].Reverse().Select(v => -v + shift[r])];
                }
        if (worldOf[0] == 0 && worldOf[1] == 1 && sign.All(s => s > 0)) return (lines[0], lines[1], lines[2], entries);
        var size = new int[3];
        for (int c = 0; c < 3; c++) size[worldOf[c]] = n[c];
        var placed = new byte[entries.LongLength];
        Parallel.For(0, n[0], i =>
        {
            var w = new int[3];
            w[worldOf[0]] = sign[0] > 0 ? i : n[0] - 1 - i;
            for (int j = 0; j < n[1]; j++)
            {
                w[worldOf[1]] = sign[1] > 0 ? j : n[1] - 1 - j;
                for (int k = 0; k < n[2]; k++)
                {
                    w[worldOf[2]] = sign[2] > 0 ? k : n[2] - 1 - k;
                    placed[((long)w[0] * size[1] + w[1]) * size[2] + w[2]] = entries[((long)i * n[1] + j) * n[2] + k];
                }
            }
        });
        return (lines[0], lines[1], lines[2], placed);
    }

    // ── drawings ───────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<C3dVoxelTissue> Drawings(Em3dLabelVolume v, IReadOnlyList<C3dVoxelLabel> rows, string hash)
    {
        int nx = v.Nx, ny = v.Ny, nz = v.Nz, count = rows.Count;
        // exact voxel bounds and counts per entry
        var lo = new int[count + 1, 3];
        var hi = new int[count + 1, 3];
        var voxels = new long[count + 1];
        for (int e = 0; e <= count; e++) { lo[e, 0] = lo[e, 1] = lo[e, 2] = int.MaxValue; hi[e, 0] = hi[e, 1] = hi[e, 2] = -1; }
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                long row = ((long)i * ny + j) * nz;
                for (int k = 0; k < nz; k++)
                {
                    int e = v.Entries[row + k];
                    if (e == 0) continue;
                    voxels[e]++;
                    if (i < lo[e, 0]) lo[e, 0] = i; if (i > hi[e, 0]) hi[e, 0] = i;
                    if (j < lo[e, 1]) lo[e, 1] = j; if (j > hi[e, 1]) hi[e, 1] = j;
                    if (k < lo[e, 2]) lo[e, 2] = k; if (k > hi[e, 2]) hi[e, 2] = k;
                }
            }

        // the decimated blocks: each the entry at its centre voxel
        int f = Math.Max(1, (int)Math.Ceiling(Math.Max(nx, Math.Max(ny, nz)) / (double)DisplayBlocks));
        int bx = (nx + f - 1) / f, by = (ny + f - 1) / f, bz = (nz + f - 1) / f;
        var block = new byte[bx * by * bz];
        for (int i = 0; i < bx; i++)
            for (int j = 0; j < by; j++)
                for (int k = 0; k < bz; k++)
                    block[(i * by + j) * bz + k] = v.Entries[((long)Math.Min(i * f + f / 2, nx - 1) * ny + Math.Min(j * f + f / 2, ny - 1)) * nz + Math.Min(k * f + f / 2, nz - 1)];
        double BX(int i) => v.X[Math.Min(i * f, nx)];
        double BY(int j) => v.Y[Math.Min(j * f, ny)];
        double BZ(int k) => v.Z[Math.Min(k * f, nz)];
        int At(int i, int j, int k) => i < 0 || j < 0 || k < 0 || i >= bx || j >= by || k >= bz ? 0 : block[(i * by + j) * bz + k];

        var list = new List<C3dVoxelTissue>();
        for (int e = 1; e <= count; e++)
        {
            string solid = v.Tissues[e - 1];
            var index = new Dictionary<(int, int, int), int>();
            var verts = new List<Point3>();
            var tris = new List<Em3dTriangle>();
            int V(int i, int j, int k)
            {
                if (index.TryGetValue((i, j, k), out int at)) return at;
                index[(i, j, k)] = verts.Count;
                verts.Add(new Point3(BX(i), BY(j), BZ(k)));
                return verts.Count - 1;
            }
            void Quad(int a, int b, int c, int d)
            {
                tris.Add(new Em3dTriangle(a, b, c, solid, 0));
                tris.Add(new Em3dTriangle(a, c, d, solid, 0));
            }
            for (int i = 0; i < bx; i++)
                for (int j = 0; j < by; j++)
                    for (int k = 0; k < bz; k++)
                    {
                        if (block[(i * by + j) * bz + k] != e) continue;
                        if (At(i + 1, j, k) != e) Quad(V(i + 1, j, k), V(i + 1, j + 1, k), V(i + 1, j + 1, k + 1), V(i + 1, j, k + 1));
                        if (At(i - 1, j, k) != e) Quad(V(i, j, k), V(i, j, k + 1), V(i, j + 1, k + 1), V(i, j + 1, k));
                        if (At(i, j + 1, k) != e) Quad(V(i, j + 1, k), V(i, j + 1, k + 1), V(i + 1, j + 1, k + 1), V(i + 1, j + 1, k));
                        if (At(i, j - 1, k) != e) Quad(V(i, j, k), V(i + 1, j, k), V(i + 1, j, k + 1), V(i, j, k + 1));
                        if (At(i, j, k + 1) != e) Quad(V(i, j, k + 1), V(i + 1, j, k + 1), V(i + 1, j + 1, k + 1), V(i, j + 1, k + 1));
                        if (At(i, j, k - 1) != e) Quad(V(i, j, k), V(i, j + 1, k), V(i + 1, j + 1, k), V(i + 1, j, k));
                    }
            var box = voxels[e] > 0
                ? (v.X[lo[e, 0]], v.Y[lo[e, 1]], v.Z[lo[e, 2]], v.X[hi[e, 0] + 1], v.Y[hi[e, 1] + 1], v.Z[hi[e, 2] + 1])
                : (0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
            var mesh = new Em3dTriangleMesh(verts, tris);
            var drawing = new Em3dShapeSolid(ReadOnlyMemory<byte>.Empty, $"voxel-{hash}-{e}", mesh,
                                             [new Em3dShapeFace("surface", "other", box, 0, 0, tris.Count)], [])
            {
                DisplayDeflectionM = f * Math.Max(v.X[1] - v.X[0], Math.Max(v.Y[1] - v.Y[0], v.Z[1] - v.Z[0])),
            };
            list.Add(new C3dVoxelTissue(rows[e - 1], e, voxels[e], drawing));
        }
        return list;
    }
}
