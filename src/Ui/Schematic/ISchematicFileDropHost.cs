using System.Threading.Tasks;

namespace CircuitRF.Ui.Schematic;

/// <summary>Builds a schematic design file dropped on an open schematic into it (brief-schimp-5 R-si5-3) — the workspace's,
/// reached from the canvas through its document.</summary>
public interface ISchematicFileDropHost
{
    /// <summary><paramref name="path"/> built into <paramref name="doc"/> with its top-left at <paramref name="at"/> (world
    /// coordinates), as one undo step.</summary>
    Task BuildSchematicFileIntoAsync(SchematicDocument doc, string path, (double X, double Y) at);
}
