// brief-sar-3 R-sar3-2 — a layered phantom: one object in the document, nested solids in the problem.
//
// ONE OBJECT, N SOLIDS. A phantom is edited, placed, hidden and turned off as one object, and the tree shows its layers under
// it like a group. What reaches a solver is its layers, each an ordinary solid of its own material, named
// "<phantom>/<layer>" — the spelling a placed cell's contents take, which no document object can have, so a layer can never
// collide with an object of the same name.
//
// EVERY LAYER IS A DIELECTRIC: a tissue, a liquid or a shell. A tissue record states εr and σ together, which on an
// ordinary object asks for a Role (em-3d.md §4.1a); a phantom's layer states it by being one.
//
// THE INNERMOST WINS. The layers are elaborated outermost first, so each inner layer has the later construction order and
// wins the volume it shares with the shells around it (em-3d.md §6.3a) — the priorities openEMS's examples give their
// skin, skull and brain (10, 11, 12). Every layer is a dielectric, so metal-over-dielectric never reorders them.
//
// THE SHAPES are the openEMS examples' own geometry: concentric ellipsoids with the shell thickness taken off every
// semi-axis (the head phantoms), coaxial cylinders of one length with radial shells (the body cylinder: every layer runs its
// full length), an open-topped box of liquid in a shell on its floor and four walls (a flat phantom), and the homogeneous
// sphere and box of the analytic checks — and brief-sar-12's generic head, circuitRF's own shape (an ellipsoid shell of liquid
// with a flat ear spacer each side), named so nobody takes it for a standard's mannequin.
//
// FLATTEN turns a phantom into those same solids as ordinary objects in a group named after it, so a flattened phantom and
// the phantom elaborate to the same problem.

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

public static class C3dPhantoms
{
    /// <summary>The name a layer's solid takes in the problem.</summary>
    public static string LayerName(string phantom, string layer) => $"{phantom}/{layer}";

    /// <summary>Why <paramref name="p"/>'s dimensions and layers do not make its shape, as the end of a sentence about it; null
    /// when they do.</summary>
    public static string? ShapeFault(C3dPhantom p)
    {
        int want = C3dPhantom.LayerCount(p.Shape);
        if (p.Layers.Count != want)
            return $"has {p.Layers.Count} layer(s), and a {p.Shape} phantom has {want}, outermost first";
        if (p.Layers.FirstOrDefault(l => string.IsNullOrWhiteSpace(l.Name)) is not null) return "has a layer with no name";
        if (p.Layers.Any(l => l.Name.Contains('/'))) return "has a layer whose name holds '/'";
        if (p.Layers.GroupBy(l => l.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            return $"has two layers named '{twice.Key}'";
        long[] shells = want switch { 3 => [p.Shell1, p.Shell2], 2 or 4 => [p.Shell1], _ => [] };
        if (shells.Any(t => t <= 0)) return $"has a shell that is not positive (a {p.Shape} phantom has {shells.Length} shell(s), Shell1 outermost)";
        if (want < 3 && p.Shell2 != 0) return $"states Shell2, which a {p.Shape} phantom does not have";
        if (want < 2 && p.Shell1 != 0) return $"states Shell1, which a {p.Shape} phantom does not have";
        if (p.Voxel < 0) return "has a voxel size that is negative (0 gives the smooth layers)";
        long wall = shells.Sum();
        switch (p.Shape)
        {
            case C3dPhantomShape.Ellipsoid3:
                if (p.SemiAxes.X <= 0 || p.SemiAxes.Y <= 0 || p.SemiAxes.Z <= 0) return "has a semi-axis that is not positive";
                if (wall >= Math.Min(p.SemiAxes.X, Math.Min(p.SemiAxes.Y, p.SemiAxes.Z)))
                    return "has shells as thick as its smallest semi-axis, which leaves the innermost layer nothing";
                break;
            case C3dPhantomShape.Cylinder3:
                if (p.Radius <= 0 || p.Length <= 0) return "has a radius or a length that is not positive";
                if (wall >= p.Radius) return "has shells as thick as its radius, which leaves the innermost layer nothing";
                break;
            case C3dPhantomShape.FlatBox:
                if (p.Size.X <= 0 || p.Size.Y <= 0 || p.Size.Z <= 0) return "has a size that is not positive";
                if (2 * wall >= Math.Min(p.Size.X, p.Size.Y) || wall >= p.Size.Z)
                    return "has a shell as thick as half its width or its whole depth, which leaves no liquid";
                break;
            case C3dPhantomShape.Head:
                if (p.SemiAxes.X <= 0 || p.SemiAxes.Y <= 0 || p.SemiAxes.Z <= 0) return "has a semi-axis that is not positive";
                if (wall >= Math.Min(p.SemiAxes.X, Math.Min(p.SemiAxes.Y, p.SemiAxes.Z)))
                    return "has a shell as thick as its smallest semi-axis, which leaves no liquid";
                if (p.Radius <= 0 || p.Length <= 0) return "has an ear spacer whose radius or thickness is not positive";
                if (p.Radius >= Math.Min(p.SemiAxes.X, p.SemiAxes.Z))
                    return "has an ear spacer as wide as the head (its radius must be below the x and z semi-axes)";
                break;
            case C3dPhantomShape.Sphere1:
                if (p.Radius <= 0) return "has a radius that is not positive";
                break;
            case C3dPhantomShape.Box1:
                if (p.Size.X <= 0 || p.Size.Y <= 0 || p.Size.Z <= 0) return "has a size that is not positive";
                break;
        }
        return null;
    }

    /// <summary>
    /// <paramref name="p"/>'s layers as ordinary objects, outermost first — each named as its layer, made of its layer's
    /// material, under the phantom's placement, and carrying its drawing state (hidden, transparency, appearance, model) — or
    /// none when its shape is at fault.
    /// </summary>
    public static IReadOnlyList<(C3dPhantomLayer Layer, C3dObject Solid)> Layers(C3dPhantom p)
    {
        if (ShapeFault(p) is not null) return [];
        var list = new List<(C3dPhantomLayer, C3dObject)>();
        long inset = 0;
        for (int i = 0; i < p.Layers.Count; i++)
        {
            if (i == 1) inset = p.Shell1;
            else if (i == 2) inset = p.Shell1 + p.Shell2;
            C3dPoint3 c = p.Centre;
            C3dObject solid = p.Shape == C3dPhantomShape.Head ? HeadLayer(p, i) : p.Shape switch
            {
                C3dPhantomShape.Ellipsoid3 => new C3dEllipsoid
                {
                    Centre = c, SemiAxes = new C3dPoint3(p.SemiAxes.X - inset, p.SemiAxes.Y - inset, p.SemiAxes.Z - inset),
                },
                C3dPhantomShape.Cylinder3 => new C3dCylinder
                {
                    Axis = p.Axis ?? C3dAxis.Z, Length = p.Length, Radius = p.Radius - inset,
                    Base = (p.Axis ?? C3dAxis.Z) switch
                    {
                        C3dAxis.X => c with { X = c.X - p.Length / 2 },
                        C3dAxis.Y => c with { Y = c.Y - p.Length / 2 },
                        _         => c with { Z = c.Z - p.Length / 2 },
                    },
                },
                // The liquid is inset on the floor and the four walls; its top is the box's, open to the air above it.
                C3dPhantomShape.FlatBox => new C3dBox
                {
                    Min = new C3dPoint3(c.X - p.Size.X / 2 + inset, c.Y - p.Size.Y / 2 + inset, c.Z - p.Size.Z / 2 + inset),
                    Size = new C3dPoint3(p.Size.X - 2 * inset, p.Size.Y - 2 * inset, p.Size.Z - inset),
                },
                C3dPhantomShape.Sphere1 => new C3dSphere { Centre = c, Radius = p.Radius },
                _ => new C3dBox { Min = new C3dPoint3(c.X - p.Size.X / 2, c.Y - p.Size.Y / 2, c.Z - p.Size.Z / 2), Size = p.Size },
            };
            var layer = p.Layers[i];
            solid.Name = layer.Name;
            solid.Material = layer.Material;
            solid.Role = Em3dRole.Dielectric;
            solid.Placement = p.Placement.Clone();
            solid.Hidden = p.Hidden;
            solid.Transparency = p.Transparency;
            solid.Appearance = p.Appearance;
            solid.Model = p.Model;
            list.Add((layer, solid));
        }
        return list;
    }

    /// <summary>
    /// brief-sar-12 R-sar12-1 — layer <paramref name="i"/> of circuitRF's generic head: the two ear spacers, then the shell, then
    /// the liquid. Each spacer is a cylinder about y from the head's centre plane out to <see cref="C3dPhantom.Length"/> past the
    /// head's surface, so the shell and the liquid (later in construction order) take everything inside the head and what is
    /// left is a flat disc standing off each side: the face a handset is placed against.
    /// </summary>
    private static C3dObject HeadLayer(C3dPhantom p, int i)
    {
        C3dPoint3 c = p.Centre;
        long reach = p.SemiAxes.Y + p.Length;
        return i switch
        {
            0 => new C3dCylinder { Axis = C3dAxis.Y, Base = c, Length = reach, Radius = p.Radius },
            1 => new C3dCylinder { Axis = C3dAxis.Y, Base = c with { Y = c.Y - reach }, Length = reach, Radius = p.Radius },
            2 => new C3dEllipsoid { Centre = c, SemiAxes = p.SemiAxes },
            _ => new C3dEllipsoid
            {
                Centre = c, SemiAxes = new C3dPoint3(p.SemiAxes.X - p.Shell1, p.SemiAxes.Y - p.Shell1, p.SemiAxes.Z - p.Shell1),
            },
        };
    }

    /// <summary>
    /// Flatten: the phantom at <paramref name="index"/> replaced, in place, by its layers as ordinary objects named
    /// <c>&lt;phantom&gt;_&lt;layer&gt;</c> (made unique against the document), in a group named after the phantom inside its own
    /// group — or the refusal. Construction order is kept: the layers take the phantom's place, outermost first.
    /// </summary>
    public static (IReadOnlyList<C3dObject> Made, string? Refusal) Flatten(C3dDocument doc, int index)
    {
        if (index < 0 || index >= doc.Objects.Count || doc.Objects[index] is not C3dPhantom p)
            return ([], "There is no phantom there to flatten.");
        if (ShapeFault(p) is { } why) return ([], $"The phantom '{p.Name}' {why}, so it cannot be flattened.");
        // brief-sar-8 — its layers as ordinary objects would be smooth solids, a different problem from its voxels.
        if (p.Voxel > 0)
            return ([], $"The phantom '{p.Name}' is voxelized, and its flattened layers would be smooth solids: set its Voxel to 0 to flatten it.");
        var taken = new HashSet<string>(doc.Objects.Select(o => o.Name).Concat(doc.Instances.Select(i => i.Name)), StringComparer.Ordinal);
        string group = p.Group is { Length: > 0 } outer ? $"{outer}/{p.Name}" : p.Name;
        var made = new List<C3dObject>();
        foreach (var (layer, solid) in Layers(p))
        {
            string name = $"{p.Name}_{layer.Name}";
            for (int k = 2; taken.Contains(name); k++) name = $"{p.Name}_{layer.Name}{k}";
            taken.Add(name);
            solid.Name = name;
            solid.Group = group;
            made.Add(solid);
        }
        doc.Objects.RemoveAt(index);
        doc.Objects.InsertRange(index, made);
        return (made, null);
    }
}
