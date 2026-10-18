// brief-sar-11 — what the anatomy importers refuse, as coded diagnostics (StepDiagnostics' pattern): one id per KIND of refusal,
// so the Messages window and `--json` can tell a file that is not what it claims (a volume, a mesh, a label list) from an
// option the caller did not give and from a name the document cannot take. Each carries the sentence the importer wrote; the
// dialog, the status line and the CLI's stderr all render the same one.

using CircuitRF.Diagnostics;

namespace CircuitRF.Design.ThreeD.Import;

/// <summary>A label volume, a mesh or a label list that cannot be read or does not hold together.</summary>
public sealed class LabelVolumeException(Diagnostic diagnostic) : Exception(diagnostic.Render())
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

public static class AnatomyDiagnostics
{
    /// <summary>A label volume (NIfTI, raw, DiscMaterial, or one in memory) that cannot be read or does not hold together.</summary>
    public static Diagnostic Volume(string why) => Diagnostic.Create(
        "c3d.import.volume", DiagnosticSeverity.Error, "{why}", ("why", why));

    /// <summary>A surface (STL, OBJ, PLY) or a tetrahedral mesh (.node/.ele) that cannot be read or is not a closed solid.</summary>
    public static Diagnostic Mesh(string why) => Diagnostic.Create(
        "c3d.import.mesh", DiagnosticSeverity.Error, "{why}", ("why", why));

    /// <summary>A label list (CSV) that cannot be read.</summary>
    public static Diagnostic Labels(string why) => Diagnostic.Create(
        "c3d.import.labels", DiagnosticSeverity.Error, "{why}", ("why", why));

    /// <summary>Something the file does not say and the caller did not give: a raw layout, a voxel size, a unit.</summary>
    public static Diagnostic Option(string why) => Diagnostic.Create(
        "c3d.import.option", DiagnosticSeverity.Error, "{why}", ("why", why));

    /// <summary>A name the document cannot take.</summary>
    public static Diagnostic Name(string why) => Diagnostic.Create(
        "c3d.import.name", DiagnosticSeverity.Error, "{why}", ("why", why));

    /// <summary>Labels the table maps to no material, each with its voxel count (R-sar11-2).</summary>
    public static Diagnostic Unmapped(string why) => Diagnostic.Create(
        "c3d.import.unmapped", DiagnosticSeverity.Error, "{why}", ("why", why));
}
