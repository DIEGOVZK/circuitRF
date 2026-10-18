// brief-sar-11 — whether a point is inside a closed triangle mesh (an imported tissue surface): the parity of a ray's crossings
// along +x. A SAR run asks this once per cell of the dump box, so each mesh gets a (y, z) bin grid on first use (kept while the
// mesh lives) and a ray tests only the triangles whose (y, z) shadow covers its bin. The ray is nudged off the bin lines so it
// does not graze an edge exactly.

using System.Runtime.CompilerServices;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

public static class C3dMeshInside
{
    private const int Bins = 64;

    private sealed class Accel
    {
        public double Y0, Z0, Dy, Dz, X0, X1;
        public List<int>[] Cells = [];
    }

    private static readonly ConditionalWeakTable<Em3dTriangleMesh, Accel> Built = new();

    public static bool Contains(Em3dTriangleMesh m, Point3 q)
    {
        var a = Built.GetValue(m, Build);
        if (a.Cells.Length == 0 || q.X < a.X0 || q.X > a.X1) return false;
        int i = (int)Math.Floor((q.Y - a.Y0) / a.Dy), j = (int)Math.Floor((q.Z - a.Z0) / a.Dz);
        if (i < 0 || j < 0 || i >= Bins || j >= Bins) return false;
        double y = q.Y + 1.234567e-9 * a.Dy, z = q.Z + 2.345678e-9 * a.Dz;
        int crossings = 0;
        foreach (int t in a.Cells[i * Bins + j])
        {
            var tri = m.Triangles[t];
            Point3 p0 = m.Vertices[tri.A], p1 = m.Vertices[tri.B], p2 = m.Vertices[tri.C];
            double d = (p1.Y - p0.Y) * (p2.Z - p0.Z) - (p2.Y - p0.Y) * (p1.Z - p0.Z);
            if (d == 0) continue;
            double u = ((y - p0.Y) * (p2.Z - p0.Z) - (p2.Y - p0.Y) * (z - p0.Z)) / d;
            double v = ((p1.Y - p0.Y) * (z - p0.Z) - (y - p0.Y) * (p1.Z - p0.Z)) / d;
            if (u < 0 || v < 0 || u + v > 1) continue;
            if (p0.X + u * (p1.X - p0.X) + v * (p2.X - p0.X) > q.X) crossings++;
        }
        return (crossings & 1) == 1;
    }

    private static Accel Build(Em3dTriangleMesh m)
    {
        var a = new Accel();
        if (m.Vertices.Count == 0) return a;
        double y0 = m.Vertices.Min(v => v.Y), y1 = m.Vertices.Max(v => v.Y), z0 = m.Vertices.Min(v => v.Z), z1 = m.Vertices.Max(v => v.Z);
        a.X0 = m.Vertices.Min(v => v.X); a.X1 = m.Vertices.Max(v => v.X);
        a.Y0 = y0; a.Z0 = z0;
        a.Dy = Math.Max((y1 - y0) / Bins, 1e-30) * (1 + 1e-9);
        a.Dz = Math.Max((z1 - z0) / Bins, 1e-30) * (1 + 1e-9);
        a.Cells = [.. Enumerable.Range(0, Bins * Bins).Select(_ => new List<int>())];
        for (int t = 0; t < m.Triangles.Count; t++)
        {
            var tri = m.Triangles[t];
            Point3 p = m.Vertices[tri.A], q = m.Vertices[tri.B], r = m.Vertices[tri.C];
            int i0 = Math.Max(0, (int)((Math.Min(p.Y, Math.Min(q.Y, r.Y)) - y0) / a.Dy)), i1 = Math.Min(Bins - 1, (int)((Math.Max(p.Y, Math.Max(q.Y, r.Y)) - y0) / a.Dy));
            int j0 = Math.Max(0, (int)((Math.Min(p.Z, Math.Min(q.Z, r.Z)) - z0) / a.Dz)), j1 = Math.Min(Bins - 1, (int)((Math.Max(p.Z, Math.Max(q.Z, r.Z)) - z0) / a.Dz));
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++) a.Cells[i * Bins + j].Add(t);
        }
        return a;
    }
}
