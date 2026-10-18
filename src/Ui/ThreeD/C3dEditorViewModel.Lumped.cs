// brief-sar-3 R-sar3-3 — the 3D view's lumped elements: R, L and C across a gap, drawn with the Port tool's gesture and kept as
// RECORDS beside the ports, never objects. Every edit is one records entry (C3dRecordsEdit carries them). The view draws each
// one in the overlay: its sheet's outline and a capacitor glyph across the axis its current runs along, as the port's own
// contact rule resolved it on the scene's last build (RecordsView.Lumped). It is selected from the tree, as a thermal place is.

using System.Text.RegularExpressions;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>A lumped element's tree row.</summary>
    public const string LumpedElementKind = "Lumped element";

    /// <summary>The units a value's chooser offers, by key; the first is a new value's.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> LumpedUnits = new Dictionary<string, string[]>
    {
        ["R"] = ["Ohm", "kOhm", "MOhm", "mOhm"], ["L"] = ["nH", "pH", "uH"], ["C"] = ["pF", "fF", "nF"],
    };

    /// <summary>What a new element starts as: the next free name C1, C2 …, 1 pF in parallel.</summary>
    public C3dLumpedElement NewLumpedElementTemplate() => new() { Name = NextName("C"), C = "1", CUnit = "pF" };

    /// <summary>The element named <paramref name="name"/>, or null.</summary>
    public C3dLumpedElement? LumpedElement(string name) => Document.LumpedElements.FirstOrDefault(l => l.Name == name);

    /// <summary>The elements as the current scene resolved them.</summary>
    public IReadOnlyList<C3dLumpedResult> LumpedResults => _recordsView?.Lumped ?? [];

    /// <summary>Adds a lumped element: one undo entry; the status line says which way its current runs, or why it cannot.</summary>
    public void AddLumpedElement(C3dLumpedElement element)
    {
        _hiddenPlaces.Remove(element.Name);
        ChangeRecords($"Add lumped element {element.Name}", d => d.LumpedElements.Add(element));
        ToolCommits++;
        var r = C3dLumpedElements.Resolve(element, Document.DbuPerMicron, CurrentPortContext(), Elaboration?.Resolution);
        StatusMessage = r.Refusal ?? $"Added lumped element '{element.Name}' ({Describe(element)}), its current along {AxisOf(r.Resolved!.Direction)}.";
    }

    /// <summary>Renames an element. Null on success, else why not.</summary>
    public string? RenameLumpedElement(string old, string name)
    {
        name = name.Trim();
        if (name == old) return null;
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            return $"'{name}' cannot be a lumped element's name: use letters, digits and _, starting with a letter.";
        var used = Document.Objects.Select(o => o.Name).Concat(Document.Instances.Select(i => i.Name)).Concat(Document.Ports.Select(p => p.Name))
                                   .Concat(ThermalPlaceNames()).Concat(NestedNames()).Concat(Document.LumpedElements.Select(l => l.Name))
                                   .Where(n => !string.Equals(n, old, StringComparison.Ordinal));
        if (used.Contains(name, StringComparer.OrdinalIgnoreCase)) return $"This 3D view already has something named '{name}'.";
        if (_hiddenPlaces.Contains(old)) _hiddenPlaces.Add(name);
        ChangeRecords($"Rename {old} to {name}", d => { foreach (var l in d.LumpedElements.Where(l => l.Name == old)) l.Name = name; });
        return null;
    }

    public void DeleteLumpedElement(string name)
        => ChangeRecords($"Delete {name}", d => d.LumpedElements.RemoveAll(l => l.Name == name));

    /// <summary>
    /// One field of an element, as text: <c>Topology</c> (Parallel or Series), <c>R</c>, <c>L</c>, <c>C</c> (an expression; empty
    /// removes the value), <c>RUnit</c>, <c>LUnit</c>, <c>CUnit</c>, and <c>Model</c> (true or false). One undo entry; null on
    /// success, else why not. A value is checked in the document's scope when there is one.
    /// </summary>
    public string? SetLumpedText(string name, string key, string text)
    {
        text = text.Trim();
        if (LumpedElement(name) is not { } le) return $"This 3D view has no lumped element named '{name}'.";
        Em3dRlcTopology topology = le.Topology;
        bool model = le.Model;
        switch (key)
        {
            case "Topology" when !Enum.TryParse(text, true, out topology):
                return "The topology is Parallel or Series.";
            case "Model":
                if (text.ToLowerInvariant() is "true" or "on" or "yes" or "1") model = true;
                else if (text.ToLowerInvariant() is "false" or "off" or "no" or "0") model = false;
                else return "Model is true or false.";
                break;
            case "R" or "L" or "C" when text.Length > 0:
            {
                string unit = (key switch { "R" => le.RUnit, "L" => le.LUnit, _ => le.CUnit }) ?? LumpedUnits[key][0];
                if (C3dLumpedElements.Value(Elaboration?.Resolution, text, unit, out string? why) is null && Elaboration?.Resolution is not null)
                    return $"{key} '{text}' does not resolve: {why}.";
                break;
            }
            case "R" or "L" or "C":
                if ((key == "R" ? le.L ?? le.C : key == "L" ? le.R ?? le.C : le.R ?? le.L) is null)
                    return "A lumped element keeps at least one of R, L and C; delete it instead.";
                break;
            case "RUnit" or "LUnit" or "CUnit" when !LumpedUnits[key[..1]].Contains(text):
                return $"{key[..1]}'s unit is one of {string.Join(", ", LumpedUnits[key[..1]])}.";
            case "RUnit" or "LUnit" or "CUnit":
                break;
            case not ("Topology"):
                return $"A lumped element has no {key}.";
        }
        ChangeRecords($"Set {name} {key}", d =>
        {
            if (d.LumpedElements.FirstOrDefault(l => l.Name == name) is not { } e) return;
            string? v = text.Length == 0 ? null : text;
            switch (key)
            {
                case "Topology": e.Topology = topology; break;
                case "Model": e.Model = model; break;
                case "R": e.R = v; e.RUnit = v is null ? null : e.RUnit ?? LumpedUnits["R"][0]; break;
                case "L": e.L = v; e.LUnit = v is null ? null : e.LUnit ?? LumpedUnits["L"][0]; break;
                case "C": e.C = v; e.CUnit = v is null ? null : e.CUnit ?? LumpedUnits["C"][0]; break;
                case "RUnit": e.RUnit = text; break;
                case "LUnit": e.LUnit = text; break;
                case "CUnit": e.CUnit = text; break;
            }
        });
        return null;
    }

    /// <summary>The element's values in a line: <c>5.4 pF</c>, <c>R 2.5 Ohm ∥ C Ctune pF</c>.</summary>
    public static string Describe(C3dLumpedElement le)
    {
        var parts = new List<string>();
        if (le.R is { } r) parts.Add($"R {r} {le.RUnit ?? C3dLumpedElements.OhmUnit}");
        if (le.L is { } l) parts.Add($"L {l} {le.LUnit ?? C3dLumpedElements.HenryUnit}");
        if (le.C is { } c) parts.Add($"C {c} {le.CUnit ?? C3dLumpedElements.FaradUnit}");
        return parts.Count == 0 ? "no value" : string.Join(le.Topology == Em3dRlcTopology.Series ? " + " : " ∥ ", parts);
    }

    private static string AxisOf(Point3 d) => Math.Abs(d.X) > 0.5 ? "x" : Math.Abs(d.Y) > 0.5 ? "y" : "z";

    // ── the tree ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The Lumped elements group, after the ports: each row its values, or its refusal.</summary>
    private void RebuildLumpedTree()
    {
        foreach (var g in Tree.Where(g => g.Role == C3dTreeGroupRole.LumpedElements).ToList())
        {
            DetachExpansion([g]);
            Tree.Remove(g);
        }
        var rows = Document.LumpedElements.Select(le =>
        {
            var r = LumpedResults.FirstOrDefault(x => x.Element.Name == le.Name);
            string detail = Describe(le) + (r?.Refusal is { } why && le.Model ? " — refused" : "") + (le.Model ? "" : C3dModelled.Suffix);
            return new C3dTreeItem(this, le.Name, LumpedElementKind, detail, -1, -1, IsPlaceShown(le.Name))
            {
                IsModelled = le.Model, Refusal = le.Model ? r?.Refusal : null,
            };
        }).ToList();
        if (rows.Count > 0) Tree.Add(new C3dTreeGroup("Lumped elements", rows, C3dTreeGroupRole.LumpedElements));
    }

    /// <summary>A lumped element's row menu: rename, model, hide, delete.</summary>
    private List<Viewer3DMenuItem> LumpedTreeItems(C3dTreeItem item)
    {
        string name = item.Name;
        var items = new List<Viewer3DMenuItem>
        {
            new("Rename…", () => TextRequested?.Invoke($"Rename {name}", "Name:", name, text => RenameLumpedElement(name, text))),
        };
        if (LumpedElement(name) is { } le)
            items.Add(new Viewer3DMenuItem(le.Model ? "✓ Model" : "Model", () => Report(SetLumpedText(name, "Model", le.Model ? "false" : "true")),
                Tip: "Unticked, the element is kept and drawn, and its gap is left as the geometry makes it in every run."));
        items.Add(new Viewer3DMenuItem(item.IsVisible ? "Hide" : "Show", () => SetRowsVisible([item], !item.IsVisible, $"{(item.IsVisible ? "Hide" : "Show")} {name}")));
        items.Add(Viewer3DMenuItem.Separator);
        items.Add(new Viewer3DMenuItem("Delete", () => DeleteLumpedElement(name)));
        return items;
    }

    // ── drawing ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Each lumped element: its sheet's outline and, at its middle, two plates square to the axis its current runs
    /// along (resolved by the port's rule; the sheet's first axis while it is refused) with a lead to each end.</summary>
    private void FillLumpedOverlay(Viewer3DDrawOverlay overlay)
    {
        int dbu = Document.DbuPerMicron;
        double per = C3dLowering.Metres(1, dbu);
        string? selected = SelectedTreeItem is { Kind: LumpedElementKind } row ? row.Name : null;
        foreach (var le in Document.LumpedElements)
        {
            if (!IsPlaceShown(le.Name)) continue;
            var into = le.Name == selected ? overlay.Selected : overlay.LumpedElements;
            var plane = new DrawingPlane(le.Plane, le.Offset);
            var r = le.Rect;
            var corners = new List<C3dPoint2> { r.Min, new(r.Min.U + r.Size.U, r.Min.V), new(r.Min.U + r.Size.U, r.Min.V + r.Size.V), new(r.Min.U, r.Min.V + r.Size.V) };
            DrawGeometry.Chain([.. corners.Select(plane.FromUv)], true, dbu, into);
            // along u unless the resolution says the current runs along the plane's v axis
            var dir = LumpedResults.FirstOrDefault(x => x.Element.Name == le.Name)?.Resolved?.Direction;
            var (uAxis, _) = C3dPorts.PlaneAxes(le.Plane);
            bool alongU = dir is not { } d || Math.Abs(d.X * uAxis.X + d.Y * uAxis.Y + d.Z * uAxis.Z) > 0.5;
            double cu = r.Min.U + r.Size.U / 2.0, cv = r.Min.V + r.Size.V / 2.0;
            double len = alongU ? r.Size.U : r.Size.V, wid = alongU ? r.Size.V : r.Size.U;
            double gap = len / 10, plate = wid * 0.35;
            Point3 P(double a, double b)
            {
                var q = plane.FromUvw((long)Math.Round(alongU ? cu + a : cu + b), (long)Math.Round(alongU ? cv + b : cv + a), le.Offset);
                return new Point3(q.X * per, q.Y * per, q.Z * per);
            }
            foreach (double s in new[] { -1.0, 1.0 })
            {
                into.Add(new DrawSegment(P(s * gap, -plate), P(s * gap, plate)));
                into.Add(new DrawSegment(P(s * gap, 0), P(s * len / 2, 0)));
            }
            overlay.Labels.Add((P(0, plate), $"{le.Name}: {Describe(le)}"));
        }
    }
}
