// brief-sar-8b R-sar8b-1 / R-sar8b-2 — a conductor that is a patch of a circular cylinder, and a lumped gap on one.
//
// A NEW SHEET KIND, NOT A CYLINDER SECTOR (R-sar8b-2, decided here). A birdcage's rungs and rings are strips of zero radial
// thickness on the cylinder r = R. A sector of a solid cylinder would have to be given a thickness the tutorial does not have,
// and a grid line on its second radius that the reference's grid does not have; a planar sheet cannot be bent. So the strip is
// its own record, kept in its own list (Em3dProblem.CylinderSheets) rather than folded into Em3dSheet: forty-odd consumers read
// a sheet as a planar polygon, and one that did not know about the bend would solve a flat strip in silence. A lowering that
// cannot state a cylindrical sheet refuses it by name; openEMS's cylindrical grid about the same axis writes it as one
// (ρ, α, z) box of zero radial extent, exact on the grid (CsxcadWriter.Cylindrical).
//
// THE AZIMUTH is the angle about the axis from the world axis (Axis + 1) % 3 towards (Axis + 2) % 3 — for a z axis, from +x
// towards +y — which is openEMS's α on a cylindrical grid about that axis (FdtdCylinder.AngleOf). Radians here, metres for
// everything else (R-mom-1).

using System.Globalization;

namespace CircuitRF.Engine.Em3d;

/// <summary>
/// brief-sar-8b — a patch of the cylinder of radius <paramref name="Radius"/> about the world axis <paramref name="Axis"/> (0 x,
/// 1 y, 2 z) through <paramref name="Origin"/> (its coordinate along the axis is not read): azimuths <paramref name="Alpha0"/> to
/// <paramref name="Alpha1"/> radians (Alpha0 &lt; Alpha1 ≤ Alpha0 + 2π), and <paramref name="Lo"/> to <paramref name="Hi"/> along the
/// axis, metres.
/// </summary>
public sealed record Em3dCylinderPatch(int Axis, Point3 Origin, double Radius, double Alpha0, double Alpha1, double Lo, double Hi)
{
    /// <summary>The two world axes across the cylinder's, in cyclic order.</summary>
    public int U => (Axis + 1) % 3;
    public int V => (Axis + 2) % 3;

    /// <summary>The patch closes on itself round the axis (a ring).</summary>
    public bool FullCircle => Alpha1 - Alpha0 >= 2 * Math.PI * (1 - 1e-12);

    /// <summary>The world point at azimuth <paramref name="alpha"/> and axial coordinate <paramref name="s"/> on the patch's cylinder.</summary>
    public Point3 World(double alpha, double s) => World(alpha, s, Radius);

    /// <summary>The world point at azimuth <paramref name="alpha"/>, axial coordinate <paramref name="s"/> and radius <paramref name="r"/>.</summary>
    public Point3 World(double alpha, double s, double r)
    {
        var c = new double[3];
        c[U] = Get(Origin, U) + r * Math.Cos(alpha);
        c[V] = Get(Origin, V) + r * Math.Sin(alpha);
        c[Axis] = s;
        return new Point3(c[0], c[1], c[2]);
    }

    /// <summary>The patch's world bounding box: the arc's ends and every quarter turn between them.</summary>
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1) Bounds()
    {
        var angles = new List<double> { Alpha0, Alpha1 };
        for (double q = Math.Ceiling(Alpha0 / (Math.PI / 2)) * (Math.PI / 2); q < Alpha1; q += Math.PI / 2) angles.Add(q);
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        foreach (double a in angles)
            foreach (double s in new[] { Lo, Hi })
            {
                var w = World(a, s);
                x0 = Math.Min(x0, w.X); y0 = Math.Min(y0, w.Y); z0 = Math.Min(z0, w.Z);
                x1 = Math.Max(x1, w.X); y1 = Math.Max(y1, w.Y); z1 = Math.Max(z1, w.Z);
            }
        return (x0, y0, z0, x1, y1, z1);
    }

    /// <summary>The middle of the patch, on its surface.</summary>
    public Point3 Centre => World((Alpha0 + Alpha1) / 2, (Lo + Hi) / 2);

    /// <summary>Whether the patch is round about axis <paramref name="axis"/> through (<paramref name="ou"/>, <paramref name="ov"/>).</summary>
    public bool RoundAbout(int axis, double ou, double ov, double tol)
        => axis == Axis && Math.Abs(Get(Origin, U) - ou) <= tol && Math.Abs(Get(Origin, V) - ov) <= tol;

    /// <summary>The unit tangent along the azimuth at <paramref name="alpha"/> (increasing α), in the world.</summary>
    public Point3 AzimuthalAt(double alpha)
    {
        var c = new double[3];
        c[U] = -Math.Sin(alpha);
        c[V] = Math.Cos(alpha);
        return new Point3(c[0], c[1], c[2]);
    }

    /// <summary>The unit vector along the axis (increasing axial coordinate).</summary>
    public Point3 AxialDirection => Axis switch { 0 => new Point3(1, 0, 0), 1 => new Point3(0, 1, 0), _ => new Point3(0, 0, 1) };

    /// <summary>
    /// The patch as triangles, both faces' worth of surface in one layer: <paramref name="segmentsPerTurn"/> segments for a full
    /// circle (at least one per patch), one strip along the axis, each triangle naming <paramref name="name"/>. For drawing and for
    /// measuring a port's contact.
    /// </summary>
    public Em3dTriangleMesh Tessellate(string name, int segmentsPerTurn = 256)
    {
        int n = Math.Max(1, (int)Math.Ceiling((Alpha1 - Alpha0) / (2 * Math.PI) * segmentsPerTurn));
        var v = new List<Point3>(2 * (n + 1));
        var t = new List<Em3dTriangle>(2 * n);
        for (int i = 0; i <= n; i++)
        {
            double a = Alpha0 + (Alpha1 - Alpha0) * i / n;
            v.Add(World(a, Lo));
            v.Add(World(a, Hi));
        }
        for (int i = 0; i < n; i++)
        {
            int a = 2 * i, b = a + 1, c = a + 2, d = a + 3;
            t.Add(new Em3dTriangle(a, c, d, name));
            t.Add(new Em3dTriangle(a, d, b, name));
        }
        return new Em3dTriangleMesh(v, t);
    }

    /// <summary>Every structural problem of the patch, naming <paramref name="who"/>; empty when it is sound.</summary>
    public IReadOnlyList<string> Problems(string who)
    {
        var p = new List<string>();
        if (Axis is < 0 or > 2) p.Add($"{who}'s cylinder axis is {Axis}; it is 0 (x), 1 (y) or 2 (z).");
        if (!(Radius > 0) || !double.IsFinite(Radius)) p.Add($"{who}'s radius is {R(Radius)} m; a cylinder's radius is positive.");
        if (!(Alpha1 > Alpha0) || Alpha1 - Alpha0 > 2 * Math.PI * (1 + 1e-12) || !double.IsFinite(Alpha0) || !double.IsFinite(Alpha1))
            p.Add($"{who}'s azimuths run from {R(Alpha0)} to {R(Alpha1)} rad; the second is above the first by at most a full turn.");
        if (!(Hi >= Lo) || !double.IsFinite(Lo) || !double.IsFinite(Hi))
            p.Add($"{who}'s axial extent runs from {R(Lo)} to {R(Hi)} m; the second is not below the first.");
        return p;
    }

    internal static double Get(Point3 q, int axis) => axis switch { 0 => q.X, 1 => q.Y, _ => q.Z };
    private static string R(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}

/// <summary>
/// brief-sar-8b R-sar8b-2 — a conductor thin enough to be a surface, on a cylinder: the patch, its material (a perfect conductor
/// on every grid that writes it) and its construction order, as a planar <see cref="Em3dSheet"/> has.
/// </summary>
public sealed record Em3dCylinderSheet(string Name, string Material, Em3dCylinderPatch Patch, int Order)
{
    public (double X0, double Y0, double Z0, double X1, double Y1, double Z1) WorldBounds() => Patch.Bounds();
}

/// <summary>Which way a gap on a cylinder carries its current: round the axis, or along it.</summary>
public enum Em3dCylinderGapAxis { Azimuthal = 1, Axial = 2 }

/// <summary>
/// brief-sar-8b R-sar8b-1 — a lumped port's or lumped element's gap that is a patch of a cylinder: its current runs along
/// <paramref name="Along"/> (openEMS's α or z index on a cylindrical grid), from the negative end towards the positive one in the
/// direction of increasing α or axial coordinate when <paramref name="Sign"/> is +1, the other way when −1.
/// </summary>
public sealed record Em3dCylinderGap(Em3dCylinderPatch Patch, Em3dCylinderGapAxis Along, int Sign)
{
    /// <summary>The gap's direction at its centre, in the world: what <see cref="Em3dPort.Direction"/> carries for it.</summary>
    public Point3 Direction
    {
        get
        {
            var d = Along == Em3dCylinderGapAxis.Axial ? Patch.AxialDirection : Patch.AzimuthalAt((Patch.Alpha0 + Patch.Alpha1) / 2);
            return Sign < 0 ? new Point3(-d.X, -d.Y, -d.Z) : d;
        }
    }

    /// <summary>The refusal every lowering but openEMS's cylindrical grid gives a problem that has one, naming <paramref name="who"/>.</summary>
    public static string NotHere(string who, string lowering)
        => $"{who} is a gap on a cylinder, which circuitRF writes on openEMS's cylindrical grid about the same axis only, and {lowering} " +
           "cannot state it. Set the setup's solver to openEMS with OpenEms.Grid Cylindrical, or draw the gap on a plane.";
}
