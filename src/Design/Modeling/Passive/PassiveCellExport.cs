// A fitted passive model as an ordinary cell (brief-modelcomposer-12 R-mc12-7; overview D12): created through
// CellCreate — the function the GUI's New Cell calls — so it is a cell like any other, placed, simulated and edited the
// ordinary way.
//
//   · a single fit: the schematic view is the topology itself, its VAR block removed: what the VAR held becomes the cell's
//     parameter interface, defaulting to the fitted values, so an instance binds the values and the drawing keeps reading
//     them by name;
//   · a family: the geometry is the ONLY parameter (defaulting to the held-out geometry, the one the family was checked
//     against, inside the range), and the VAR block stays on the schematic with every element value a law of it, wrapped in
//     validrange(…) so a geometry outside the training range warns at elaboration. The laws are the model, not knobs: on
//     the drawing they are read where the elements are, and an instance has one thing to set;
//   · the symbol is the auto-generated N-pin box (AutoSymbolGenerator, odd pins left, even right), or a .csym the user
//     chooses, whose pin count must match.
//
// An existing cell name is refused, never overwritten. Values are written in the engineering unit that puts them between 1
// and 1000 (nH, fF, Ω), every digit kept, so the number the elaborator reads is the number that was fitted.

using System.Globalization;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;

namespace CircuitRF.Design.Modeling.Passive;

/// <summary>An exported cell: the paths created and the parameter interface written.</summary>
public sealed record PassiveExportResult(CellCreateResult Cell, IReadOnlyList<CcellParameter> Parameters, IReadOnlyList<string> Lines);

public static class PassiveCellExport
{
    /// <summary>
    /// Exports <paramref name="topology"/> — a library id, or a <c>.csch</c> path — with <paramref name="values"/> (every
    /// variable, base SI) as cell <paramref name="cellName"/> in <paramref name="libraryDir"/>. <paramref name="symbolPath"/>
    /// null draws the N-pin box.
    /// </summary>
    public static PassiveExportResult Export(string libraryDir, string cellName, string topology,
                                             IReadOnlyDictionary<string, double> values, string? symbolPath = null)
    {
        var (network, model) = Load(topology);
        var parameters = new List<CcellParameter>();
        foreach (var v in network.Variables)
        {
            if (!values.TryGetValue(v.Name, out double x))
                throw new ModelingException(v.Name, "it has no fitted value; fit the topology before exporting it.");
            parameters.Add(Parameter(v, x));
        }
        return Write(libraryDir, cellName, network, model, parameters, symbolPath,
                     $"{cellName}: {network.Ports}-port {Path.GetFileNameWithoutExtension(network.SchematicPath)}, {parameters.Count} parameters");
    }

    /// <summary>
    /// Exports a family (R-mc12-6): the geometry is the first parameter and every element value an expression of it.
    /// </summary>
    public static PassiveExportResult ExportFamily(string libraryDir, string cellName, string topology, FamilyFitResult family,
                                                   string? symbolPath = null)
    {
        var (network, model) = Load(topology);
        string geo = family.Settings.Geometry;
        string g = $"validrange({geo},{R(family.Lo)},{R(family.Hi)})";
        var parameters = new List<CcellParameter>
        {
            // A held-out geometry the user put at an end lies outside the range; the default never warns.
            new() { Name = geo, DefaultExpression = R(Math.Clamp(family.HeldOutGeometry, family.Lo, family.Hi)), ShowOnSchematic = true },
        };
        var mid = family.ValuesAt(0.5 * (family.Lo + family.Hi));
        var rows = new List<EditableParameter>();
        // Every law is a function of one normalised geometry; written once as the block's first row, each law reads as a
        // short polynomial in it rather than repeating it in every term.
        string? x = null;
        var shaped = family.Laws.Where(l => l.Half != 0).ToList();
        if (shaped.Count > 0 && shaped.All(l => l.Mid == shaped[0].Mid && l.Half == shaped[0].Half))
        {
            x = new[] { "x", "xg", "x_" + geo }.First(n => n != geo && network.Variables.All(v => v.Name != n));
            rows.Add(new EditableParameter { Name = x, Expression = shaped[0].Normalised(g), Unit = "", ShowOnSchematic = true });
        }
        foreach (var v in network.Variables)
        {
            var (unit, scale) = Engineering(v.Kind, mid.GetValueOrDefault(v.Name));
            string expr = family.Laws.FirstOrDefault(l => l.Variable == v.Name) is { } law ? law.Expression(g, scale, x)
                        // A tie reads a variable that carries its own unit, so the row's unit is not applied to it.
                        : family.Ties.TryGetValue(v.Name, out var tie) ? $"{R(tie.Factor)}*{tie.To}"
                        : family.Locked.TryGetValue(v.Name, out double held) ? R(held / scale)
                        : throw new ModelingException(v.Name, "the family gives it no value.");
            rows.Add(new EditableParameter { Name = v.Name, Expression = expr, Unit = unit, ShowOnSchematic = true });
        }
        var block = model.Components.FirstOrDefault(c => c.Symbol == SymbolKind.Var);
        if (block is null)
        {
            block = new EditableComponent { Symbol = SymbolKind.Var, InstanceName = "VAR1" };
            model.Components.Add(block);
        }
        block.Parameters.Clear();
        block.Parameters.AddRange(rows);
        return Write(libraryDir, cellName, network, model, parameters, symbolPath,
                     $"{cellName}: {network.Ports}-port {Path.GetFileNameWithoutExtension(network.SchematicPath)} over {geo} = " +
                     $"{G(family.Lo)} to {G(family.Hi)}, valid in that range", keepVar: true);
    }

    private static (PassiveNetwork, SchematicEditModel) Load(string topology)
    {
        if (topology.EndsWith(".csch", StringComparison.OrdinalIgnoreCase))
            return (PassiveNetwork.Load(topology), SchematicPersistence.LoadFromFile(topology).model);
        var t = PassiveTopologies.Find(topology);
        return (t.Load(), SchematicPersistence.Deserialize(PassiveTopologies.Text(t.Id)).model);
    }

    private static CcellParameter Parameter(NetworkVariable v, double value)
    {
        var dim = v.Kind switch
        {
            NetworkElementKind.Resistance => UnitDimension.Resistance,
            NetworkElementKind.Inductance or NetworkElementKind.Mutual => UnitDimension.Inductance,
            NetworkElementKind.Capacitance => UnitDimension.Capacitance,
            _ => UnitDimension.None,
        };
        var (unit, scale) = Engineering(v.Kind, value);
        return new CcellParameter { Name = v.Name, DefaultExpression = R(value / scale), Unit = unit, Dimension = dim, ShowOnSchematic = true };
    }

    /// <summary>The unit, and its scale, that puts <paramref name="value"/> between 1 and 1000: 6.7e-9 H is 6.7 nH.</summary>
    internal static (string Unit, double Scale) Engineering(NetworkElementKind kind, double value)
    {
        (string, double)[] ladder = kind switch
        {
            NetworkElementKind.Resistance => [("mOhm", 1e-3), ("Ohm", 1), ("kOhm", 1e3), ("MOhm", 1e6)],
            NetworkElementKind.Inductance or NetworkElementKind.Mutual => [("fH", 1e-15), ("pH", 1e-12), ("nH", 1e-9), ("uH", 1e-6), ("mH", 1e-3), ("H", 1)],
            NetworkElementKind.Capacitance => [("fF", 1e-15), ("pF", 1e-12), ("nF", 1e-9), ("uF", 1e-6), ("mF", 1e-3), ("F", 1)],
            _ => [("", 1)],
        };
        double a = Math.Abs(value);
        if (!(a > 0) || !double.IsFinite(a))
            return kind switch
            {
                NetworkElementKind.Resistance => ("Ohm", 1),
                NetworkElementKind.Inductance or NetworkElementKind.Mutual => ("nH", 1e-9),
                NetworkElementKind.Capacitance => ("pF", 1e-12),
                _ => ("", 1),
            };
        var pick = ladder[0];
        foreach (var u in ladder) if (a >= u.Item2) pick = u;
        return pick;
    }

    private static PassiveExportResult Write(string libraryDir, string cellName, PassiveNetwork network, SchematicEditModel model,
                                             List<CcellParameter> parameters, string? symbolPath, string summary, bool keepVar = false)
    {
        if (Directory.Exists(Path.Combine(libraryDir, cellName)))
            throw new ModelingException(cellName, $"a cell of that name already exists in {libraryDir}; choose another name.");
        Symbol.Symbol symbol;
        if (symbolPath is null) symbol = AutoSymbolGenerator.Generate(cellName, network.Ports);
        else
        {
            symbol = SymbolPersistence.LoadFromFile(symbolPath);
            if (symbol.PortCount != network.Ports)
                throw new ModelingException(Path.GetFileName(symbolPath), $"it has {symbol.PortCount} pins and the model {network.Ports} ports.");
        }

        // A single fit: what the VAR block held is the cell's interface now; the drawing reads the same names.
        if (!keepVar) model.Components.RemoveAll(c => c.Symbol == SymbolKind.Var);

        var created = CellCreate.Create(libraryDir, cellName, CellViews.Schematic, model);
        string symbolFile = CellCreate.WriteSymbolView(created.CellDir, cellName, symbol);
        string ccellPath = Path.Combine(created.CellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.NumPorts = network.Ports;
        ccell.Parameters.Clear();
        ccell.Parameters.AddRange(parameters);
        CellPersistence.SaveToFile(ccellPath, ccell);
        return new PassiveExportResult(created with { SymbolPath = symbolFile }, parameters, [summary]);
    }

    private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
