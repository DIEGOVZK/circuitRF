// brief-sar-12 R-sar12-2 — putting a device against a phantom: a separation along the face's normal and a tilt, written as
// ORDINARY transforms.
//
// NOTHING IN THE FORMAT IS PHONE-SPECIFIC. What the helper writes is each moved object's Placement (one more rotation, a new
// Origin) — what a drag or a typed origin would have written — so a placed device is an ordinary document, and moving it
// afterwards by hand needs nothing to be kept in step. The relationship it was placed by is not stored; the 3D view's
// separation dimension is measured again from the geometry each time it is drawn (Measure).
//
// THE SEPARATION is measured along the face's outward normal, from the face's plane to the object's NEAREST point: the
// smallest n·(v − a) over every vertex v of the object's elaborated geometry. A face is a plane (an anchor and a normal), one
// of the phantom's own bounding-box faces in its own frame, carried by its placement — plus a phantom's named faces: a flat
// phantom's floor and its liquid's floor (the liquid's surface through the shell, which is the plane a system check's spacing
// is stated from), and the generic head's two ear spacers.
//
// THE TILT turns the objects about a world axis first; the translation is then solved, so the axis needs no pivot. The moved
// objects' footprint (their box, projected on the face) is centred on the face's anchor plus an in-plane offset.
//
// ONE RIGID MOTION FOR ALL OF THEM. Selected objects, instances and groups move together, as one device: each gets the same
// rotation appended and its origin carried by the same motion. A tilt that is not a quarter turn leaves each origin a double,
// rounded to the DBU, so two parts that touched may be a DBU apart afterwards (a nanometre at 1000 DBU per µm).
//
// CURVED SURFACES are measured at their tessellation's vertices, which lie on the surface: a sphere's or a cylinder's extreme
// point can fall between them, by at most the facets' sagitta. A box, a sheet, a polyhedron and a STEP solid's tessellation are
// exact.

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>A phantom face a device is placed against: its name, a point on it (the footprint's centre goes there) and its outward
/// normal, in the world, DBU.</summary>
public sealed record C3dPlacementFace(string Name, (double X, double Y, double Z) Anchor, (double X, double Y, double Z) Normal);

/// <summary>What <see cref="C3dDevicePlacement.Place"/> was asked: the objects, the phantom and its face, the separation (DBU), the
/// tilt about a world axis, and an in-plane offset of the footprint from the face's anchor (DBU; its part along the normal is
/// ignored).</summary>
public sealed record C3dPlacementRequest(IReadOnlyList<string> Movers, string Phantom, string Face, long Separation,
                                         double TiltDeg = 0, C3dAxis TiltAxis = C3dAxis.X, C3dPoint3 Offset = default);

/// <summary>The separation as found in the geometry: the face, the object's nearest point and its foot on the face (world, DBU),
/// and the distance between them along the normal.</summary>
public sealed record C3dSeparation(C3dPlacementFace Face, (double X, double Y, double Z) Nearest, (double X, double Y, double Z) Foot,
                                   double Distance);

public static class C3dDevicePlacement
{
    /// <summary>Every face of <paramref name="p"/> a device can be placed against, in the world.</summary>
    public static IReadOnlyList<C3dPlacementFace> Faces(C3dPhantom p)
    {
        if (C3dPhantoms.ShapeFault(p) is not null) return [];
        var (lo, hi) = OwnBox(p);
        double cx = 0.5 * (lo.X + hi.X), cy = 0.5 * (lo.Y + hi.Y), cz = 0.5 * (lo.Z + hi.Z);
        var own = new List<(string, (double, double, double), (double, double, double))>
        {
            ("xmin", (lo.X, cy, cz), (-1, 0, 0)), ("xmax", (hi.X, cy, cz), (1, 0, 0)),
            ("ymin", (cx, lo.Y, cz), (0, -1, 0)), ("ymax", (cx, hi.Y, cz), (0, 1, 0)),
            ("zmin", (cx, cy, lo.Z), (0, 0, -1)), ("zmax", (cx, cy, hi.Z), (0, 0, 1)),
        };
        if (p.Shape == C3dPhantomShape.FlatBox)
        {
            own.Insert(0, ("liquid", (cx, cy, lo.Z + p.Shell1), (0, 0, -1)));
            own.Insert(0, ("floor", (cx, cy, lo.Z), (0, 0, -1)));
        }
        else if (p.Shape == C3dPhantomShape.Head)
        {
            own.Insert(0, ("ear-right", (cx, lo.Y, cz), (0, -1, 0)));
            own.Insert(0, ("ear-left", (cx, hi.Y, cz), (0, 1, 0)));
        }
        var t = p.Placement.ToTransform();
        return [.. own.Select(f => new C3dPlacementFace(f.Item1, Point(t, f.Item2), Direction(t, f.Item3)))];
    }

    /// <summary>The phantom's box in its own frame, DBU: its layers' extent (the head's ear spacers included).</summary>
    public static (C3dPoint3 Lo, C3dPoint3 Hi) OwnBox(C3dPhantom p)
    {
        var c = p.Centre;
        C3dPoint3 h = p.Shape switch
        {
            C3dPhantomShape.Ellipsoid3 => p.SemiAxes,
            C3dPhantomShape.Head => p.SemiAxes with { Y = p.SemiAxes.Y + p.Length },
            C3dPhantomShape.Cylinder3 => (p.Axis ?? C3dAxis.Z) switch
            {
                C3dAxis.X => new(p.Length / 2, p.Radius, p.Radius),
                C3dAxis.Y => new(p.Radius, p.Length / 2, p.Radius),
                _ => new(p.Radius, p.Radius, p.Length / 2),
            },
            C3dPhantomShape.Sphere1 => new(p.Radius, p.Radius, p.Radius),
            _ => new(p.Size.X / 2, p.Size.Y / 2, p.Size.Z / 2),
        };
        return (c - h, c + h);
    }

    /// <summary>
    /// The objects and instances <paramref name="movers"/> names — each an object's or an instance's name, or a group's path,
    /// which stands for everything in it — as indices into <see cref="C3dDocument.Objects"/> and <see cref="C3dDocument.Instances"/>;
    /// or why not.
    /// </summary>
    public static (IReadOnlyList<int> Objects, IReadOnlyList<int> Instances, string? Refusal) Resolve(C3dDocument doc, IReadOnlyList<string> movers)
    {
        var objects = new SortedSet<int>();
        var instances = new SortedSet<int>();
        static bool InGroup(string? g, string name) => g is not null && (g == name || g.StartsWith(name + "/", StringComparison.Ordinal));
        foreach (string m in movers)
        {
            int before = objects.Count + instances.Count;
            for (int i = 0; i < doc.Objects.Count; i++)
                if (doc.Objects[i].Name == m || InGroup(doc.Objects[i].Group, m)) objects.Add(i);
            for (int i = 0; i < doc.Instances.Count; i++)
                if (doc.Instances[i].Name == m || InGroup(doc.Instances[i].Group, m)) instances.Add(i);
            if (objects.Count + instances.Count == before) return ([], [], $"Nothing in this 3D view is named '{m}', and no group is.");
        }
        if (objects.Count + instances.Count == 0) return ([], [], "Select the objects to place: an antenna, a device or its group.");
        foreach (int i in objects)
            if (doc.Objects[i] is C3dPhantom or C3dVoxelBody)
                return ([], [], $"'{doc.Objects[i].Name}' is a phantom; place a device against it, not the phantom itself.");
        foreach (var placement in objects.Select(i => doc.Objects[i].Placement).Concat(instances.Select(i => doc.Instances[i].Placement)))
            if (placement.Exprs is not null)
                return ([], [], "One of the objects to place has an origin given by an expression; placing it would overwrite the expression.");
        return ([.. objects], [.. instances], null);
    }

    /// <summary>The world vertices, DBU, of everything elaborated from the objects and instances <paramref name="movers"/>
    /// names.</summary>
    public static List<(double X, double Y, double Z)> Vertices(C3dDocument doc, C3dElaboration e, IReadOnlyList<string> movers)
    {
        var (objects, instances, _) = Resolve(doc, movers);
        var objectNames = new HashSet<string>(objects.Select(i => doc.Objects[i].Name), StringComparer.Ordinal);
        var instanceNames = new HashSet<string>(instances.Select(i => doc.Instances[i].Name), StringComparer.Ordinal);
        double perM = 1e6 * doc.DbuPerMicron;
        bool Mine(string name)
        {
            if (!e.Provenance.TryGetValue(name, out var p)) return objectNames.Contains(name);
            if (p.InstancePath.Length > 0) return instanceNames.Contains(p.InstancePath.Split('/', '[')[0]);
            return objectNames.Contains(p.TopObject ?? name);
        }
        var list = new List<(double, double, double)>();
        void Add(Em3dTriangleMesh mesh) { foreach (var v in mesh.Vertices) list.Add((v.X * perM, v.Y * perM, v.Z * perM)); }
        foreach (var s in e.Solids.Concat(e.UnassignedSolids).Where(s => Mine(s.Name))) Add(Em3dTessellation.Of(s));
        foreach (var s in e.Sheets.Concat(e.UnassignedSheets).Where(s => Mine(s.Name))) Add(Em3dTessellation.OfSheet(s));
        return list;
    }

    /// <summary>The face of the phantom named <paramref name="phantom"/> called <paramref name="face"/>, or why not.</summary>
    public static (C3dPhantom? Phantom, C3dPlacementFace? Face, string? Refusal) FaceOf(C3dDocument doc, string phantom, string face)
    {
        if (doc.Objects.FirstOrDefault(o => o.Name == phantom) is not C3dPhantom p) return (null, null, $"There is no phantom named '{phantom}'.");
        if (C3dPhantoms.ShapeFault(p) is { } why) return (p, null, $"The phantom '{phantom}' {why}.");
        var faces = Faces(p);
        return faces.FirstOrDefault(f => f.Name == face) is { } hit
            ? (p, hit, null)
            : (p, null, $"The phantom '{phantom}' has no face '{face}'; it has {string.Join(", ", faces.Select(f => f.Name))}.");
    }

    /// <summary>
    /// The objects of <paramref name="request"/> against its phantom's face: one rigid motion — the tilt, then the translation
    /// that puts their nearest point at the separation and their footprint's centre on the face's anchor (plus the offset) —
    /// written into each one's placement. <paramref name="e"/> is the document as it stands, elaborated. Null, or why not; the
    /// document is unchanged on a refusal.
    /// </summary>
    public static string? Place(C3dDocument doc, C3dElaboration e, C3dPlacementRequest request)
    {
        var (phantom, face, refusal) = FaceOf(doc, request.Phantom, request.Face);
        if (refusal is not null) return refusal;
        var (objects, instances, why) = Resolve(doc, request.Movers);
        if (why is not null) return why;
        if (request.Separation < 0) return "The separation is negative; give the distance from the face to the device's nearest point.";
        if (face!.Name == "liquid" && request.Separation < phantom!.Shell1)
            return $"A separation from the liquid below the shell's thickness ({phantom.Shell1} DBU) would put the device inside the shell.";
        if (!double.IsFinite(request.TiltDeg)) return "The tilt is not a number.";
        var vertices = Vertices(doc, e, request.Movers);
        if (vertices.Count == 0) return "The objects to place have no geometry to measure (none of them elaborated).";

        var tilt = C3dTransform.Rotation(request.TiltAxis, request.TiltDeg);
        var turned = vertices.Select(v => Rotate(tilt, v)).ToList();
        var n = face.Normal;
        var a = face.Anchor;
        // Along the normal: the nearest point (smallest n·v) goes to n·a + s.
        double near = turned.Min(v => Dot(n, v));
        double along = Dot(n, a) + request.Separation - near;
        // In the plane: the box's centre goes to the anchor plus the offset, both projected onto the plane.
        var lo = (turned.Min(v => v.X), turned.Min(v => v.Y), turned.Min(v => v.Z));
        var hi = (turned.Max(v => v.X), turned.Max(v => v.Y), turned.Max(v => v.Z));
        var centre = (0.5 * (lo.Item1 + hi.Item1), 0.5 * (lo.Item2 + hi.Item2), 0.5 * (lo.Item3 + hi.Item3));
        var target = (a.X + request.Offset.X, a.Y + request.Offset.Y, a.Z + request.Offset.Z);
        var lateral = InPlane(n, (target.Item1 - centre.Item1, target.Item2 - centre.Item2, target.Item3 - centre.Item3));
        var move = (lateral.X + along * n.X, lateral.Y + along * n.Y, lateral.Z + along * n.Z);

        void Carry(C3dPlacement placement)
        {
            if (request.TiltDeg != 0) placement.Rotate.Add(new C3dRotation { Axis = request.TiltAxis, Deg = request.TiltDeg });
            var o = Rotate(tilt, (placement.Origin.X, placement.Origin.Y, placement.Origin.Z));
            placement.Origin = new C3dPoint3((long)Math.Round(o.X + move.Item1), (long)Math.Round(o.Y + move.Item2), (long)Math.Round(o.Z + move.Item3));
        }
        foreach (int i in objects) Carry(doc.Objects[i].Placement);
        foreach (int i in instances) Carry(doc.Instances[i].Placement);
        return null;
    }

    /// <summary>The separation of the objects <paramref name="movers"/> names from the face, measured in <paramref name="e"/>: what
    /// the 3D view draws as a dimension. Null when the face or the objects cannot be found.</summary>
    public static C3dSeparation? Measure(C3dDocument doc, C3dElaboration e, IReadOnlyList<string> movers, string phantom, string face)
    {
        if (FaceOf(doc, phantom, face) is not (_, { } f, null)) return null;
        var vertices = Vertices(doc, e, movers);
        if (vertices.Count == 0) return null;
        var n = f.Normal;
        var nearest = vertices.MinBy(v => Dot(n, v));
        double d = Dot(n, nearest) - Dot(n, f.Anchor);
        var foot = (nearest.X - d * n.X, nearest.Y - d * n.Y, nearest.Z - d * n.Z);
        return new C3dSeparation(f, nearest, foot, d);
    }

    private static (double X, double Y, double Z) Point(C3dTransform t, (double X, double Y, double Z) p)
        => (t.M00 * p.X + t.M01 * p.Y + t.M02 * p.Z + t.Tx, t.M10 * p.X + t.M11 * p.Y + t.M12 * p.Z + t.Ty,
            t.M20 * p.X + t.M21 * p.Y + t.M22 * p.Z + t.Tz);

    private static (double X, double Y, double Z) Direction(C3dTransform t, (double X, double Y, double Z) d)
        => Rotate(t, d);

    private static (double X, double Y, double Z) Rotate(C3dTransform t, (double X, double Y, double Z) p)
        => (t.M00 * p.X + t.M01 * p.Y + t.M02 * p.Z, t.M10 * p.X + t.M11 * p.Y + t.M12 * p.Z, t.M20 * p.X + t.M21 * p.Y + t.M22 * p.Z);

    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static (double X, double Y, double Z) InPlane((double X, double Y, double Z) n, (double X, double Y, double Z) v)
    {
        double k = Dot(n, v);
        return (v.X - k * n.X, v.Y - k * n.Y, v.Z - k * n.Z);
    }
}
