// The placement payload (brief-modelcomposer-11 R-mc11-3): one ComposedFet instance — File, N = N0 and the symbol — in
// the schematic clipboard format, which the window puts on the clipboard so the user pastes it into any .csch (the Smith
// Chart tool's pattern). It is SchematicPersistence's own selection JSON, so a paste is the editor's ordinary paste.
//
// File is the model's absolute path: where the paste lands is unknown here, and an absolute File resolves from anywhere.

using System.Globalization;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Modeling;

public static class ComposedFetPlacement
{
    /// <summary>
    /// The instance a placement of <paramref name="cmodelPath"/> creates: <c>N</c> the model's base finger count, and the
    /// symbol <paramref name="symbol"/> or, when that is null, the model's own.
    /// </summary>
    public static EditableComponent Instance(string cmodelPath, string? symbol = null, string instanceName = "X1")
    {
        string path = Path.GetFullPath(cmodelPath);
        var model = CmodelFile.Read(path);
        var c = new EditableComponent { InstanceName = instanceName, Symbol = SymbolKind.ComposedFet };
        foreach (var p in ComponentTypeRegistry.DefaultParameters(SymbolKind.ComposedFet, 3))
            c.Parameters.Add(new EditableParameter
            {
                Name = p.Name, Unit = p.Unit, ShowOnSchematic = p.ShowOnSchematic, Dimension = p.Dimension,
                Expression = p.Name switch
                {
                    ComposedFetSymbolProvider.FileParameter => path.Replace('\\', '/'),
                    "N" => model.BasePeriphery.Fingers.ToString(CultureInfo.InvariantCulture),
                    ComposedFetSymbolProvider.SymbolParameter => symbol ?? model.Symbol ?? "",
                    _ => p.Expression,
                },
            });
        return c;
    }

    /// <summary>The clipboard text of one placed instance — what Copy instance puts on the clipboard.</summary>
    public static string Payload(string cmodelPath, string? symbol = null) =>
        SchematicPersistence.SerializeSelection([Instance(cmodelPath, symbol)], [], []);
}
