// ================================================================
//  OpenEmsHdf5Tests.cs — the gate for brief-sar-4 R-sar4-2: openEMS 0.37.0's HDF5 files read through RfCore's
//  PureHDF adapter (RfCore.Hdf5.OpenEmsHdf5) to exactly the values openEMS's OWN Python reader wrote out of the same
//  files (exact: the data is float32 and nothing is converted on the way), and the array order pinned by sar_calc's
//  own statement of where its maximum is. The files are SAR-1's raw dipole dump, sar_calc's 10 g average of it, and
//  one E/H field dump, made by testdata/sar/openems-ref/tools/make_hdf5_samples.py; like the rest of the reference
//  data they live on the developer machine only, and every test needing them skips with a reason where they are
//  absent. The refusal test builds its own file and always runs.
// ================================================================

using System.Text.Json;
using PureHDF;
using RfCore.Hdf5;
using Xunit;
using Npy = CircuitRF.Ui.Tests.Sar.SarReferenceSetTests.Npy;

namespace CircuitRF.Ui.Tests.Sar;

public sealed class OpenEmsHdf5Tests
{
    internal static readonly string Root = Path.Combine(Em3d.PalaceBackendTests.RepoRoot(), "testdata", "sar", "openems-ref");
    private static readonly string Dipole = Path.Combine(Root, "Dipole_SAR");

    [SarHdf5SamplesFact]
    public void RawDipoleDump_ReadsToWhatOpenEmsOwnReaderWrote()
    {
        var raw = OpenEmsHdf5.ReadSarRaw(Path.Combine(Dipole, "SAR_raw_dump29.h5"));
        Assert.Equal(1e9, raw.FrequencyHz);
        AssertMesh(raw.Mesh, Dipole, "raw_mesh");
        Assert.Equal(1e-3, raw.Mesh.DrawingUnitM);

        var e = Npy.Read(Path.Combine(Dipole, "raw_e.npy"));
        Assert.Equal([3, raw.Mesh.Nx, raw.Mesh.Ny, raw.Mesh.Nz], e.Shape);
        AssertComponents(e, raw.Ex, raw.Ey, raw.Ez);
        foreach (var (name, values) in new[] { ("conductivity", raw.Conductivity), ("density", raw.Density), ("volume", raw.Volume) })
            Assert.Equal(Npy.Read(Path.Combine(Dipole, $"raw_{name}.npy")).Real, values.Select(v => (double)v));
        // openEMS's volume per cell is the product of the widths it states beside it (float32 rounding).
        int at = OpenEmsArrayOrder.Index(3, 5, 7, raw.Mesh.Ny, raw.Mesh.Nz);
        Assert.Equal(raw.CellWidthX[3] * raw.CellWidthY[5] * raw.CellWidthZ[7], raw.Volume[at], raw.Volume[at] * 1e-6);

        // A slab of x is the same cells, read on their own.
        var slab = OpenEmsHdf5.ReadSarRaw(Path.Combine(Dipole, "SAR_raw_dump29.h5"), xStart: 20, xCount: 4);
        int plane = raw.Mesh.Ny * raw.Mesh.Nz;
        Assert.Equal(raw.Mesh.X[20..24], slab.Mesh.X);
        Assert.Equal(raw.Ez[(20 * plane)..(24 * plane)], slab.Ez);
        Assert.Equal(raw.Density[(20 * plane)..(24 * plane)], slab.Density);
    }

    [SarHdf5SamplesFact]
    public void SarCalcResult_ReadsToSar1sReference_AndItsOwnPeakIndexPinsTheArrayOrder()
    {
        var result = OpenEmsHdf5.ReadSarResult(Path.Combine(Dipole, "SAR_10g.h5"));
        Assert.Equal(Npy.Read(Path.Combine(Dipole, "sar_10g.npy")).Real, result.Sar.Select(v => (double)v));
        AssertMesh(result.Mesh, Dipole, "sar_10g_mesh");

        // sar_calc names the cell of its maximum; in circuitRF's order that cell holds the array's maximum.
        var m = result.Mesh;
        Assert.Equal((float)result.MaxSar, result.Sar[OpenEmsArrayOrder.Index(result.MaxSarIndex[0], result.MaxSarIndex[1], result.MaxSarIndex[2], m.Ny, m.Nz)]);
        Assert.Equal((float)result.MaxSar, result.Sar.Max());

        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dipole, "summary.json")));
        var attrs = summary.RootElement.GetProperty("sar_10g");
        Assert.Equal(attrs.GetProperty("mass").GetDouble(), result.MassKg);
        Assert.Equal(attrs.GetProperty("power").GetDouble(), result.PowerW, result.PowerW * 1e-7);
        Assert.Equal(attrs.GetProperty("used_cubes").GetUInt64(), result.UsedCubes);
    }

    [SarHdf5SamplesFact]
    public void FieldDumps_ReadToWhatOpenEmsOwnReaderWrote()
    {
        string d = Path.Combine(Root, "field_dump");
        foreach (var (name, quantity) in new[] { ("E_fd", OpenEmsFieldQuantity.E), ("H_fd", OpenEmsFieldQuantity.H) })
        {
            var freqs = Npy.Read(Path.Combine(d, $"{name}_freq_hz.npy")).Real;
            for (int k = 0; k < freqs.Length; k++)
            {
                var field = OpenEmsHdf5.ReadField(Path.Combine(d, name + ".h5"), k);
                Assert.Equal(quantity, field.Quantity);
                Assert.Equal(freqs, field.FrequenciesHz);
                Assert.False(field.DualMesh);
                AssertMesh(field.Mesh, d, $"{name}_mesh");
                var expected = Npy.Read(Path.Combine(d, $"{name}_f{k}.npy"));
                Assert.Equal([3, field.Mesh.Nx, field.Mesh.Ny, field.Mesh.Nz], expected.Shape);
                AssertComponents(expected, field.X, field.Y, field.Z);
            }
        }
    }

    [Fact]
    public void AFileWhoseGroupLayoutDiffers_IsRefused_NamingThePath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf-sar4-{Guid.NewGuid():N}.h5");
        try
        {
            // A raw dump's header and mesh, but /CellWidth a dataset where openEMS writes a group.
            var mesh = new H5Group { ["x"] = new double[] { 0, 1 }, ["y"] = new double[] { 0, 1 }, ["z"] = new double[] { 0, 1 } };
            mesh.Attributes = new() { ["mesh_type"] = 0, ["mesh_scaling"] = 1e-3 };
            var file = new H5File { ["Mesh"] = mesh, ["CellWidth"] = new double[] { 1, 1 } };
            file.Attributes = new() { ["openEMS_HDF5_version"] = 0.3, ["dump_type"] = 29, ["dual_mesh"] = (byte)1 };
            file.Write(path);

            var ex = Assert.Throws<OpenEmsHdf5Exception>(() => OpenEmsHdf5.ReadSarRaw(path));
            Assert.Equal("/CellWidth", ex.ObjectPath);
            Assert.Equal("hdf5.openems.unexpected", ex.Diagnostic.Id);
            Assert.Contains("/CellWidth is a dataset", ex.Message);
        }
        finally { File.Delete(path); }
    }

    private static void AssertMesh(OpenEmsMesh mesh, string dir, string stem)
    {
        Assert.Equal(Npy.Read(Path.Combine(dir, $"{stem}_x_m.npy")).Real, mesh.X);
        Assert.Equal(Npy.Read(Path.Combine(dir, $"{stem}_y_m.npy")).Real, mesh.Y);
        Assert.Equal(Npy.Read(Path.Combine(dir, $"{stem}_z_m.npy")).Real, mesh.Z);
    }

    /// <summary>A (3, nx, ny, nz) NumPy array against three components in circuitRF's order — the same flat order.</summary>
    private static void AssertComponents(Npy expected, params ComplexF[][] components)
    {
        int n = components[0].Length;
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < n; i++)
            {
                var want = expected.Values![c * n + i];
                var got = components[c][i];
                if (got.Re != want.Real || got.Im != want.Imaginary)
                    Assert.Fail($"component {c}, flat cell {i}: read {got.Re}{got.Im:+0.########e+0;-0.########e+0}j, openEMS's reader {want}");
            }
    }
}

/// <summary>Skips, naming the script, unless the SAR-4 HDF5 samples are on this machine (developer-only data, like the
/// rest of testdata/sar/openems-ref; tools/make_hdf5_samples.py makes them).</summary>
public sealed class SarHdf5SamplesFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(() =>
        new[] { "Dipole_SAR/SAR_raw_dump29.h5", "Dipole_SAR/raw_e.npy", "Dipole_SAR/SAR_10g.h5", "field_dump/E_fd.h5", "field_dump/H_fd.h5" }
            .All(f => File.Exists(Path.Combine(OpenEmsHdf5Tests.Root, f)))
            ? null
            : "needs the SAR-4 HDF5 samples, which are not in the repository: testdata/sar/openems-ref/README.md says how to make them");

    public SarHdf5SamplesFactAttribute()
    {
        if (Reason.Value is { } why) Skip = why;
    }
}
