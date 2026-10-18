// brief-sar-11 R-sar11-4 — a per-tissue surface (C3dMesh) from its file to the problem: read (cached by the file's identity),
// checked, scaled from its unit to metres, carried by its world transform, and lowered as an Em3dShapeSolid with NO B-rep whose
// display mesh IS the solid. openEMS writes it through the same PolyhedronReader PLY path a kernel solid takes (CsxcadWriter's
// KernelPly, its tessellator returning the mesh as it is); the geometry worker never sees it, so Palace and the thermal solver
// refuse it. Any rigid placement is fine: the grid staircases a mesh whatever way it faces.
//
// A surface that is not closed, or that crosses itself, is refused here as well as at import: a file edited after its import
// is read again, and openEMS's inside test on an open surface is undefined.

using System.Globalization;
using CircuitRF.Design.ThreeD.Import;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

public static class C3dMeshes
{
    /// <summary>A mesh file's length unit in metres; null for a spelling that is not one.</summary>
    public static double? UnitMetres(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "m" => 1, "mm" => 1e-3, "um" or "µm" or "micron" => 1e-6, "cm" => 1e-2, "in" or "inch" => 0.0254, "mil" => 25.4e-6,
        _ => null,
    };

    public static string FileOf(C3dMesh m, string c3dPath)
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(c3dPath))!, m.File));

    private sealed record Loaded(string Key, SurfaceMesh Mesh, SurfaceMeshCheck Check);
    private static readonly List<Loaded> Cache = [];
    private static readonly object Gate = new();

    /// <summary>The surface in <paramref name="path"/> and its check, or the refusal.</summary>
    public static (SurfaceMesh? Mesh, SurfaceMeshCheck? Check, string? Refusal) Load(string path)
    {
        if (!File.Exists(path)) return (null, null, $"'{Path.GetFileName(path)}' is not there.");
        var info = new FileInfo(path);
        string key = string.Create(CultureInfo.InvariantCulture, $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        lock (Gate)
            if (Cache.FirstOrDefault(c => c.Key == key) is { } hit) return (hit.Mesh, hit.Check, null);
        try
        {
            var mesh = SurfaceMeshFile.Read(path);
            var check = SurfaceMeshFile.Check(mesh);
            lock (Gate)
            {
                Cache.Insert(0, new Loaded(key, mesh, check));
                while (Cache.Count > 32) Cache.RemoveAt(Cache.Count - 1);
            }
            return (mesh, check, null);
        }
        catch (LabelVolumeException e) { return (null, null, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, null, $"'{Path.GetFileName(path)}' could not be read: {e.Message}"); }
    }

    /// <summary><paramref name="m"/> under <paramref name="world"/> (metres; its own placement then the instances above it) as a solid
    /// with no B-rep, or the refusal.</summary>
    public static (C3dLowered? Lowered, string? Refusal) Lower(C3dMesh m, string name, string c3dPath, C3dTransform world)
    {
        if (UnitMetres(m.Unit) is not { } unit)
            return (null, $"The mesh '{name}' states the unit '{m.Unit}'; it is one of m, cm, mm, um, in, mil.");
        var (mesh, check, refusal) = Load(FileOf(m, c3dPath));
        if (mesh is null) return (null, $"The mesh '{name}': {refusal}");
        if (check!.Fault() is { } fault) return (null, $"The mesh '{name}' ('{m.File}') {fault}.");
        bool flip = m.Flip ^ (check.SignedVolume < 0);
        var verts = new Point3[mesh.Vertices.Length];
        for (int i = 0; i < verts.Length; i++)
        {
            var v = mesh.Vertices[i];
            verts[i] = C3dLowering.Apply(world, new Point3(v.X * unit, v.Y * unit, v.Z * unit));
        }
        bool mirrored = Det(world) < 0;
        var tris = mesh.Triangles.Select(t => flip ^ mirrored ? new Em3dTriangle(t.A, t.C, t.B, name, 0) : new Em3dTriangle(t.A, t.B, t.C, name, 0)).ToList();
        double x0 = verts.Min(q => q.X), y0 = verts.Min(q => q.Y), z0 = verts.Min(q => q.Z), x1 = verts.Max(q => q.X), y1 = verts.Max(q => q.Y), z1 = verts.Max(q => q.Z);
        var display = new Em3dTriangleMesh(verts, tris);
        string hash = (m.Hash.StartsWith("sha256:", StringComparison.Ordinal) ? m.Hash[7..] : m.Hash).PadRight(16, '0')[..16];
        var solid = new Em3dShapeSolid(ReadOnlyMemory<byte>.Empty,
                                       $"mesh-{hash}-{Placed(world, unit, flip)}", display,
                                       [new Em3dShapeFace("surface", "other", (x0, y0, z0, x1, y1, z1), 0, 0, tris.Count)], [])
        {
            Tessellator = (_, _) => display,
        };
        return (new C3dLowered(solid, null, C3dMesh.FaceNameList, "mesh"), null);
    }

    /// <summary>A stable fingerprint of how the file was placed (HashCode is per-process, and this names a run's PLY file).</summary>
    private static string Placed(C3dTransform world, double unit, bool flip)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
               System.Text.Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{world}|{unit:R}|{flip}"))))[..8];

    private static double Det(C3dTransform t)
        => t.M00 * (t.M11 * t.M22 - t.M12 * t.M21) - t.M01 * (t.M10 * t.M22 - t.M12 * t.M20) + t.M02 * (t.M10 * t.M21 - t.M11 * t.M20);
}
