// ================================================================
//  OpenEmsHdf5Diagnostics.cs — brief-sar-4: the refusals of the openEMS HDF5 adapter, as coded diagnostics
//  (src/Design/Layout/Em/EmDiagnostics.cs is the worked example of the pattern).
//
//  Every reader refusal names the HDF5 OBJECT PATH and what the pinned openEMS 0.37.0 layout has there
//  (R-sar4-2): a file whose groups or attributes differ is refused, never read on a guess.
// ================================================================

using System;
using CircuitRF.Diagnostics;

namespace RfCore.Hdf5;

/// <summary>
/// Thrown when an openEMS HDF5 file is not the layout this adapter is pinned to, or a DiscMaterial file cannot be
/// written as CSXCAD reads it. <see cref="ObjectPath"/> is the HDF5 path of the object in question.
/// </summary>
public sealed class OpenEmsHdf5Exception(Diagnostic diagnostic, string objectPath) : Exception(diagnostic.Render())
{
    public Diagnostic Diagnostic { get; } = diagnostic;
    public string ObjectPath { get; } = objectPath;
}

/// <summary>The adapter's refusals. Reword a template freely; an id is the durable part.</summary>
public static class OpenEmsHdf5Diagnostics
{
    /// <summary>An object the pinned layout always has is absent.</summary>
    public static Diagnostic Missing(string file, string path, string expected) => Diagnostic.Create(
        "hdf5.openems.missing",
        DiagnosticSeverity.Error,
        "'{file}' has no {path}. The openEMS 0.37.0 layout this reader is pinned to has {expected} there.",
        ("file", file), ("path", path), ("expected", expected));

    /// <summary>An object is there, in a form the pinned layout does not have.</summary>
    public static Diagnostic Unexpected(string file, string path, string found, string expected) => Diagnostic.Create(
        "hdf5.openems.unexpected",
        DiagnosticSeverity.Error,
        "'{file}': {path} is {found}. The openEMS 0.37.0 layout this reader is pinned to has {expected}.",
        ("file", file), ("path", path), ("found", found), ("expected", expected));

    /// <summary>A DiscMaterial input CSXCAD could not read back as given.</summary>
    public static Diagnostic DiscMaterialRefused(string path, string why) => Diagnostic.Create(
        "hdf5.discmaterial.refused",
        DiagnosticSeverity.Error,
        "The voxel material file cannot be written: {path} {why}",
        ("path", path), ("why", why));
}
