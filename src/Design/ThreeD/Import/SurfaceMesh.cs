// brief-sar-11 R-sar11-4 — per-tissue closed surfaces (STL, OBJ, PLY): read, checked, and ordered by containment.
//
// READ, WELDED BY EXACT COORDINATE. STL repeats every vertex per triangle; welding is what lets the check find an edge's two
// triangles. Nothing is snapped or merged within a tolerance: two vertices a hair apart are two vertices, and the check then
// reports the edge they leave open, which is the truthful answer.
//
// THE CHECK, PER FILE (a file that fails is reported, never imported half-way):
//   * CLOSED: every edge belongs to exactly two triangles, traversed once each way. An edge used once is a hole; more than
//     twice is non-manifold; twice the same way is a flipped triangle. Each is counted.
//   * NOT SELF-INTERSECTING: no two triangles that share no vertex cross (each one's edges tested against the other, through
//     a uniform grid so the cost stays near linear). Two coplanar triangles that overlap are not detected; a segmentation's
//     surface does not produce them.
//   * OUTWARD: the enclosed volume (divergence theorem) is positive; a surface whose triangles all face inward is turned
//     round and the import says so — openEMS reads a polyhedron's inside from its winding.
//
// NESTED BY CONTAINMENT. A tissue inside another must win the volume they share, which in a .c3d is the LATER construction
// order (em-3d.md §6.3a). So surfaces are ordered by how many others contain them, outermost first, exactly as a phantom's
// layers are. Containment is a vertex of the inner surface inside the outer (ray parity) with the inner's box inside the
// outer's; surfaces that cross each other are not nested and the import names them.

using System.Globalization;
using System.Text;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD.Import;

/// <summary>A triangle surface in its file's own units: welded vertices and triangles as vertex indices.</summary>
public sealed record SurfaceMesh(Point3[] Vertices, (int A, int B, int C)[] Triangles)
{
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1) Bounds()
    {
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        foreach (var v in Vertices)
        {
            x0 = Math.Min(x0, v.X); y0 = Math.Min(y0, v.Y); z0 = Math.Min(z0, v.Z);
            x1 = Math.Max(x1, v.X); y1 = Math.Max(y1, v.Y); z1 = Math.Max(z1, v.Z);
        }
        return (x0, y0, z0, x1, y1, z1);
    }

    /// <summary>The volume enclosed, by the divergence theorem: positive when the triangles face outward.</summary>
    public double SignedVolume()
    {
        double v = 0;
        foreach (var (a, b, c) in Triangles)
        {
            Point3 p = Vertices[a], q = Vertices[b], r = Vertices[c];
            v += p.X * (q.Y * r.Z - q.Z * r.Y) - p.Y * (q.X * r.Z - q.Z * r.X) + p.Z * (q.X * r.Y - q.Y * r.X);
        }
        return v / 6;
    }

    /// <summary>The same surface with every triangle turned round.</summary>
    public SurfaceMesh Flipped() => this with { Triangles = [.. Triangles.Select(t => (t.A, t.C, t.B))] };

    /// <summary>The same surface with every coordinate times <paramref name="k"/>, then moved by <paramref name="by"/>.</summary>
    public SurfaceMesh Scaled(double k, Point3 by = default)
        => this with { Vertices = [.. Vertices.Select(v => new Point3(v.X * k + by.X, v.Y * k + by.Y, v.Z * k + by.Z))] };

    /// <summary>Whether <paramref name="q"/> is inside the (closed) surface: the parity of a ray's crossings along +x, with the
    /// ray nudged off the axes so it does not graze an edge exactly.</summary>
    public bool Contains(Point3 q)
    {
        int crossings = 0;
        double dy = 1.234567e-7, dz = 2.345678e-7;
        double y = q.Y + dy * Math.Max(1, Math.Abs(q.Y)), z = q.Z + dz * Math.Max(1, Math.Abs(q.Z));
        foreach (var (a, b, c) in Triangles)
        {
            Point3 p0 = Vertices[a], p1 = Vertices[b], p2 = Vertices[c];
            // the triangle's projection onto (y, z) holds (y, z)?
            double d = (p1.Y - p0.Y) * (p2.Z - p0.Z) - (p2.Y - p0.Y) * (p1.Z - p0.Z);
            if (d == 0) continue;
            double u = ((y - p0.Y) * (p2.Z - p0.Z) - (p2.Y - p0.Y) * (z - p0.Z)) / d;
            double v = ((p1.Y - p0.Y) * (z - p0.Z) - (y - p0.Y) * (p1.Z - p0.Z)) / d;
            if (u < 0 || v < 0 || u + v > 1) continue;
            double x = p0.X + u * (p1.X - p0.X) + v * (p2.X - p0.X);
            if (x > q.X) crossings++;
        }
        return (crossings & 1) == 1;
    }
}

/// <summary>What the check found on one surface; <see cref="Fault"/> is null when it is a closed, non-self-intersecting surface.</summary>
public sealed record SurfaceMeshCheck(int Triangles, int OpenEdges, int NonManifoldEdges, int FlippedEdges, int SelfIntersections,
                                      double SignedVolume, bool SelfIntersectionTested)
{
    public bool Closed => OpenEdges == 0 && NonManifoldEdges == 0 && FlippedEdges == 0;

    public string? Fault()
    {
        var parts = new List<string>();
        if (Triangles == 0) return "holds no triangles";
        if (OpenEdges > 0) parts.Add($"{OpenEdges:N0} open edge{(OpenEdges == 1 ? "" : "s")} (a hole)");
        if (NonManifoldEdges > 0) parts.Add($"{NonManifoldEdges:N0} edge{(NonManifoldEdges == 1 ? "" : "s")} shared by more than two triangles");
        if (FlippedEdges > 0) parts.Add($"{FlippedEdges:N0} edge{(FlippedEdges == 1 ? "" : "s")} whose triangles disagree on which way is out");
        if (SelfIntersections > 0) parts.Add($"{SelfIntersections:N0} pair{(SelfIntersections == 1 ? "" : "s")} of triangles that cross each other");
        if (parts.Count == 0 && SignedVolume == 0) return "encloses no volume";
        return parts.Count == 0 ? null : "is not a closed surface: " + string.Join(", ", parts);
    }
}

public static class SurfaceMeshFile
{
    /// <summary>Extensions read as a surface mesh.</summary>
    public static bool IsSurfaceMesh(string path) => Path.GetExtension(path).ToLowerInvariant() is ".stl" or ".obj" or ".ply";

    public static SurfaceMesh Read(string path)
    {
        string what = $"'{Path.GetFileName(path)}'";
        byte[] b = File.ReadAllBytes(path);
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".stl" => Stl(b, what),
                ".obj" => Obj(Encoding.UTF8.GetString(b), what),
                ".ply" => Ply(b, what),
                _ => throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"{what} is not an STL, OBJ or PLY file.")),
            };
        }
        catch (Exception e) when (e is FormatException or OverflowException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"{what} could not be read: {e.Message}"));
        }
    }

    private sealed class Welder
    {
        private readonly Dictionary<Point3, int> _index = [];
        public readonly List<Point3> Vertices = [];
        public readonly List<(int, int, int)> Triangles = [];

        public int Vertex(Point3 p)
        {
            if (_index.TryGetValue(p, out int i)) return i;
            _index[p] = Vertices.Count;
            Vertices.Add(p);
            return Vertices.Count - 1;
        }

        public void Polygon(IReadOnlyList<int> ring)
        {
            for (int k = 1; k + 1 < ring.Count; k++)
                if (ring[0] != ring[k] && ring[k] != ring[k + 1] && ring[0] != ring[k + 1]) Triangles.Add((ring[0], ring[k], ring[k + 1]));
        }

        public SurfaceMesh Mesh() => new([.. Vertices], [.. Triangles]);
    }

    private static SurfaceMesh Stl(byte[] b, string what)
    {
        var w = new Welder();
        if (b.Length >= 84 && 84 + 50L * BitConverter.ToUInt32(b, 80) == b.Length)
        {
            uint n = BitConverter.ToUInt32(b, 80);
            for (int t = 0; t < n; t++)
            {
                int o = 84 + 50 * t + 12;
                var ring = new int[3];
                for (int k = 0; k < 3; k++, o += 12)
                    ring[k] = w.Vertex(new Point3(BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8)));
                w.Polygon(ring);
            }
            return w.Mesh();
        }
        string text = Encoding.ASCII.GetString(b);
        if (!text.TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase))
            throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"{what} is neither a binary STL (its length does not match its triangle count) nor an ASCII one."));
        var current = new List<int>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                current.Add(w.Vertex(new Point3(D(f[1]), D(f[2]), D(f[3]))));
            }
            else if (line.StartsWith("endloop", StringComparison.OrdinalIgnoreCase))
            {
                w.Polygon(current);
                current = [];
            }
        }
        return w.Mesh();
    }

    private static SurfaceMesh Obj(string text, string what)
    {
        var w = new Welder();
        var verts = new List<int>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("v ", StringComparison.Ordinal) || line.StartsWith("v\t", StringComparison.Ordinal))
            {
                var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                verts.Add(w.Vertex(new Point3(D(f[1]), D(f[2]), D(f[3]))));
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal) || line.StartsWith("f\t", StringComparison.Ordinal))
            {
                var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var ring = new List<int>();
                for (int k = 1; k < f.Length; k++)
                {
                    int i = int.Parse(f[k].Split('/')[0], CultureInfo.InvariantCulture);
                    int at = i > 0 ? i - 1 : verts.Count + i;
                    if (at < 0 || at >= verts.Count) throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"{what} names vertex {i}, and it has {verts.Count} so far."));
                    ring.Add(verts[at]);
                }
                w.Polygon(ring);
            }
        }
        return w.Mesh();
    }

    private static SurfaceMesh Ply(byte[] b, string what)
    {
        // the header, as text up to end_header
        int end = IndexOf(b, "end_header");
        if (end < 0 || !Encoding.ASCII.GetString(b, 0, Math.Min(3, b.Length)).Equals("ply", StringComparison.Ordinal))
            throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"{what} is not a PLY file (no 'ply' magic or no end_header)."));
        int body = end + "end_header".Length;
        while (body < b.Length && b[body] != '\n') body++;
        body++;
        var header = Encoding.ASCII.GetString(b, 0, end).Split('\n').Select(l => l.Trim()).ToList();
        string format = "";
        var elements = new List<(string Name, int Count, List<(string Name, string Type, string? CountType)> Props)>();
        foreach (string line in header)
        {
            var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 0) continue;
            if (f[0] == "format") format = f[1];
            else if (f[0] == "element") elements.Add((f[1], int.Parse(f[2], CultureInfo.InvariantCulture), []));
            else if (f[0] == "property" && elements.Count > 0)
                elements[^1].Props.Add(f[1] == "list" ? (f[4], f[3], f[2]) : (f[2], f[1], null));
        }
        if (format is not ("ascii" or "binary_little_endian" or "binary_big_endian"))
            throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"{what} has the PLY format '{format}', which is not ascii or binary."));
        var w = new Welder();
        var verts = new List<int>();
        bool big = format == "binary_big_endian";
        int pos = body;
        string[] tokens = format == "ascii" ? Encoding.ASCII.GetString(b, body, b.Length - body).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : [];
        int tok = 0;
        double Next(string type)
        {
            if (format == "ascii") return D(tokens[tok++]);
            var s = b.AsSpan(pos);
            double v;
            switch (type)
            {
                case "char" or "int8": v = (sbyte)s[0]; pos += 1; break;
                case "uchar" or "uint8": v = s[0]; pos += 1; break;
                case "short" or "int16": v = big ? System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(s) : System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(s); pos += 2; break;
                case "ushort" or "uint16": v = big ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(s) : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(s); pos += 2; break;
                case "int" or "int32": v = big ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(s) : System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(s); pos += 4; break;
                case "uint" or "uint32": v = big ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(s) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s); pos += 4; break;
                case "float" or "float32": v = big ? System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(s) : System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(s); pos += 4; break;
                case "double" or "float64": v = big ? System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(s) : System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(s); pos += 8; break;
                default: throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"{what} has a property of type '{type}', which PLY does not define."));
            }
            return v;
        }
        foreach (var (name, count, props) in elements)
            for (int e = 0; e < count; e++)
            {
                double x = 0, y = 0, z = 0;
                List<int>? ring = null;
                foreach (var (pname, type, countType) in props)
                {
                    if (countType is not null)
                    {
                        int n = (int)Next(countType);
                        var items = new List<int>(n);
                        for (int k = 0; k < n; k++) items.Add((int)Next(type));
                        if (name == "face" && pname is "vertex_indices" or "vertex_index") ring = items;
                        continue;
                    }
                    double v = Next(type);
                    if (name == "vertex") { if (pname == "x") x = v; else if (pname == "y") y = v; else if (pname == "z") z = v; }
                }
                if (name == "vertex") verts.Add(w.Vertex(new Point3(x, y, z)));
                else if (name == "face" && ring is not null)
                {
                    if (ring.Any(i => i < 0 || i >= verts.Count)) throw new LabelVolumeException(AnatomyDiagnostics.Mesh($"{what} has a face naming a vertex it does not have."));
                    w.Polygon([.. ring.Select(i => verts[i])]);
                }
            }
        return w.Mesh();
    }

    private static int IndexOf(byte[] b, string text)
    {
        var t = Encoding.ASCII.GetBytes(text);
        for (int i = 0; i + t.Length <= Math.Min(b.Length, 1 << 16); i++)
            if (b.AsSpan(i, t.Length).SequenceEqual(t)) return i;
        return -1;
    }

    private static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    // ── the check ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The closedness and self-intersection check of the file header. A surface past
    /// <paramref name="intersectionLimit"/> triangles is not tested for self-intersection, and the check says so.</summary>
    public static SurfaceMeshCheck Check(SurfaceMesh m, int intersectionLimit = 2_000_000)
    {
        var edges = new Dictionary<(int, int), (int Count, int Forward)>();
        foreach (var (a, b, c) in m.Triangles)
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = p < q ? (p, q) : (q, p);
                var e = edges.GetValueOrDefault(key);
                edges[key] = (e.Count + 1, e.Forward + (p < q ? 1 : 0));
            }
        int open = 0, nonManifold = 0, flipped = 0;
        foreach (var (count, forward) in edges.Values)
        {
            if (count == 1) open++;
            else if (count > 2) nonManifold++;
            else if (forward != 1) flipped++;
        }
        bool tested = m.Triangles.Length <= intersectionLimit;
        int crossings = tested ? SelfIntersections(m) : 0;
        return new SurfaceMeshCheck(m.Triangles.Length, open, nonManifold, flipped, crossings, m.SignedVolume(), tested);
    }

    private static int SelfIntersections(SurfaceMesh m)
    {
        if (m.Triangles.Length < 4) return 0;
        var (x0, y0, z0, x1, y1, z1) = m.Bounds();
        double span = Math.Max(x1 - x0, Math.Max(y1 - y0, z1 - z0));
        int cellsPerAxis = Math.Clamp((int)Math.Ceiling(Math.Cbrt(m.Triangles.Length)), 1, 256);
        double h = span / cellsPerAxis * (1 + 1e-9);
        if (!(h > 0)) return 0;
        var grid = new Dictionary<(int, int, int), List<int>>();
        var boxes = new (double X0, double Y0, double Z0, double X1, double Y1, double Z1)[m.Triangles.Length];
        for (int t = 0; t < m.Triangles.Length; t++)
        {
            var (a, b, c) = m.Triangles[t];
            Point3 p = m.Vertices[a], q = m.Vertices[b], r = m.Vertices[c];
            var bx = (Math.Min(p.X, Math.Min(q.X, r.X)), Math.Min(p.Y, Math.Min(q.Y, r.Y)), Math.Min(p.Z, Math.Min(q.Z, r.Z)),
                      Math.Max(p.X, Math.Max(q.X, r.X)), Math.Max(p.Y, Math.Max(q.Y, r.Y)), Math.Max(p.Z, Math.Max(q.Z, r.Z)));
            boxes[t] = bx;
            for (int i = (int)((bx.Item1 - x0) / h); i <= (int)((bx.Item4 - x0) / h); i++)
                for (int j = (int)((bx.Item2 - y0) / h); j <= (int)((bx.Item5 - y0) / h); j++)
                    for (int k = (int)((bx.Item3 - z0) / h); k <= (int)((bx.Item6 - z0) / h); k++)
                    {
                        if (!grid.TryGetValue((i, j, k), out var list)) grid[(i, j, k)] = list = [];
                        list.Add(t);
                    }
        }
        var pairs = new HashSet<(int, int)>();
        foreach (var list in grid.Values)
            for (int u = 0; u < list.Count; u++)
                for (int v = u + 1; v < list.Count; v++)
                {
                    int s = list[u], t = list[v];
                    var (sa, sb, sc) = m.Triangles[s];
                    var (ta, tb, tc) = m.Triangles[t];
                    if (sa == ta || sa == tb || sa == tc || sb == ta || sb == tb || sb == tc || sc == ta || sc == tb || sc == tc) continue;
                    var A = boxes[s]; var B = boxes[t];
                    if (A.X1 < B.X0 || B.X1 < A.X0 || A.Y1 < B.Y0 || B.Y1 < A.Y0 || A.Z1 < B.Z0 || B.Z1 < A.Z0) continue;
                    var key = s < t ? (s, t) : (t, s);
                    if (pairs.Contains(key)) continue;
                    if (Cross(m, s, t) || Cross(m, t, s)) pairs.Add(key);
                }
        return pairs.Count;
    }

    /// <summary>Whether an edge of triangle <paramref name="s"/> passes through triangle <paramref name="t"/>.</summary>
    private static bool Cross(SurfaceMesh m, int s, int t)
    {
        var (a, b, c) = m.Triangles[s];
        var (ta, tb, tc) = m.Triangles[t];
        Point3 p0 = m.Vertices[ta], p1 = m.Vertices[tb], p2 = m.Vertices[tc];
        foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
            if (SegmentHits(m.Vertices[u], m.Vertices[v], p0, p1, p2)) return true;
        return false;
    }

    private static bool SegmentHits(Point3 o, Point3 e, Point3 p0, Point3 p1, Point3 p2)
    {
        // Möller–Trumbore on the segment o → e, its ends excluded
        double dx = e.X - o.X, dy = e.Y - o.Y, dz = e.Z - o.Z;
        double e1x = p1.X - p0.X, e1y = p1.Y - p0.Y, e1z = p1.Z - p0.Z, e2x = p2.X - p0.X, e2y = p2.Y - p0.Y, e2z = p2.Z - p0.Z;
        double px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
        double det = e1x * px + e1y * py + e1z * pz;
        double scale = Math.Sqrt((e1x * e1x + e1y * e1y + e1z * e1z) * (e2x * e2x + e2y * e2y + e2z * e2z) * (dx * dx + dy * dy + dz * dz));
        if (Math.Abs(det) <= 1e-12 * scale) return false;                  // parallel or coplanar
        double inv = 1 / det;
        double sx = o.X - p0.X, sy = o.Y - p0.Y, sz = o.Z - p0.Z;
        double u = (sx * px + sy * py + sz * pz) * inv;
        if (u <= 0 || u >= 1) return false;
        double qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
        double v = (dx * qx + dy * qy + dz * qz) * inv;
        if (v <= 0 || u + v >= 1) return false;
        double tt = (e2x * qx + e2y * qy + e2z * qz) * inv;
        return tt > 1e-9 && tt < 1 - 1e-9;
    }

    // ── nesting ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The order <paramref name="surfaces"/> are added in — outermost first — and, per surface, how many of the others contain
    /// it; plus every pair that neither contains the other but whose boxes overlap and that cross (named by index).
    /// </summary>
    public static (int[] Order, int[] Depth, IReadOnlyList<(int A, int B)> Crossing) Nest(IReadOnlyList<SurfaceMesh> surfaces)
    {
        int n = surfaces.Count;
        var boxes = surfaces.Select(s => s.Bounds()).ToArray();
        var depth = new int[n];
        var crossing = new List<(int, int)>();
        bool Inside((double X0, double Y0, double Z0, double X1, double Y1, double Z1) a, (double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
            => a.X0 >= b.X0 && a.Y0 >= b.Y0 && a.Z0 >= b.Z0 && a.X1 <= b.X1 && a.Y1 <= b.Y1 && a.Z1 <= b.Z1;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                if (Inside(boxes[i], boxes[j]) && surfaces[j].Contains(surfaces[i].Vertices[0])) depth[i]++;
                else if (i < j && Overlap(boxes[i], boxes[j]) && !(Inside(boxes[j], boxes[i]) && surfaces[i].Contains(surfaces[j].Vertices[0]))
                         && (surfaces[j].Contains(surfaces[i].Vertices[0]) || surfaces[i].Contains(surfaces[j].Vertices[0])))
                    crossing.Add((i, j));
            }
        var order = Enumerable.Range(0, n).OrderBy(i => depth[i]).ThenBy(i => i).ToArray();
        return (order, depth, crossing);
    }

    private static bool Overlap((double X0, double Y0, double Z0, double X1, double Y1, double Z1) a, (double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
        => a.X0 < b.X1 && b.X0 < a.X1 && a.Y0 < b.Y1 && b.Y0 < a.Y1 && a.Z0 < b.Z1 && b.Z0 < a.Z1;
}
