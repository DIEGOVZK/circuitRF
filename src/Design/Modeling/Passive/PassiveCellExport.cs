// A fitted passive model as an ordinary cell (brief-modelcomposer-12 R-mc12-7; overview D12): created through
// CellCreate — the function the GUI's New Cell calls — so it is a cell like any other, placed, simulated and edited the
// ordinary way.
//
//   · the schematic view is the topology itself, its VAR block removed: what the VAR held becomes the cell's parameter
//     interface, so an instance binds the values and the drawing keeps reading them by name;
//   · the parameters, in the .ccell, default to the fitted values — or, for a family, the geometry parameter first
//     (defaulting to the held-out geometry, the one the family was checked against) and every element value an
//     expression of it, wrapped in validrange(…) so a geometry outside the training range warns at elaboration;
//   · the symbol is the auto-generated N-pin box (AutoSymbolGenerator, odd pins left, even right), or a .csym the user
//     chooses, whose pin count must match.
//
// An existing cell name is refused, never overwritten. Values are written in base SI with the base unit (Ω, H, F), so
// the number that was fitted is the number the elaborator reads.

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
            parameters.Add(Parameter(v, R(x), show: true));
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
            new() { Name = geo, DefaultExpression = R(family.HeldOutGeometry), ShowOnSchematic = true },
        };
        foreach (var v in network.Variables)
        {
            string expr = family.Laws.FirstOrDefault(l => l.Variable == v.Name) is { } law ? law.Expression(g)
                        : family.Ties.TryGetValue(v.Name, out var tie) ? $"{R(tie.Factor)}*{tie.To}"
                        : family.Locked.TryGetValue(v.Name, out double x) ? R(x)
                        : throw new ModelingException(v.Name, "the family gives it no value.");
            parameters.Add(Parameter(v, expr, show: false));
        }
        return Write(libraryDir, cellName, network, model, parameters, symbolPath,
                     $"{cellName}: {network.Ports}-port {Path.GetFileNameWithoutExtension(network.SchematicPath)} over {geo} = " +
                     $"{G(family.Lo)} to {G(family.Hi)}, valid in that range");
    }

    private static (PassiveNetwork, SchematicEditModel) Load(string topology)
    {
        if (topology.EndsWith(".csch", StringComparison.OrdinalIgnoreCase))
            return (PassiveNetwork.Load(topology), SchematicPersistence.LoadFromFile(topology).model);
        var t = PassiveTopologies.Find(topology);
        return (t.Load(), SchematicPersistence.Deserialize(PassiveTopologies.Text(t.Id)).model);
    }

    private static CcellParameter Parameter(NetworkVariable v, string expression, bool show)
    {
        var (unit, dim) = v.Kind switch
        {
            NetworkElementKind.Resistance => ("Ohm", UnitDimension.Resistance),
            NetworkElementKind.Inductance or NetworkElementKind.Mutual => ("H", UnitDimension.Inductance),
            NetworkElementKind.Capacitance => ("F", UnitDimension.Capacitance),
            _ => ("", UnitDimension.None),
        };
        return new CcellParameter { Name = v.Name, DefaultExpression = expression, Unit = unit, Dimension = dim, ShowOnSchematic = show };
    }

    private static PassiveExportResult Write(string libraryDir, string cellName, PassiveNetwork network, SchematicEditModel model,
                                             List<CcellParameter> parameters, string? symbolPath, string summary)
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

        // What the VAR block held is the cell's interface now; the drawing reads the same names.
        model.Components.RemoveAll(c => c.Symbol == SymbolKind.Var);

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
