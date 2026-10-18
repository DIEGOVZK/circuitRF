// ================================================================
//  OpenEmsArrayOrder.cs — brief-sar-4 R-sar4-2: the ONE place that knows how openEMS and CSXCAD order a 3-D array
//  on disk, against the order circuitRF holds it in memory.
//
//  circuitRF's order is C order over (x, y, z): the flat index of cell (ix, iy, iz) is (ix·ny + iy)·nz + iz, z
//  fastest — the order NumPy hands back for openEMS's own reader, so a reference .npy compares element for element.
//
//  Two disk orders meet it, and they are opposite:
//
//    * openEMS 0.37.0's HDF5 dumps and sar_calc's output say d_order="XYZ" (a vector field "NXYZ": component
//      first) and store dimensions (nx, ny, nz) — C order over (x, y, z), circuitRF's own. Nothing is permuted.
//      openEMS's reader treats "ZYX"/"NZYX" (the legacy form --legacyHDF5Dumps writes) as reversed; that form is
//      not what 0.37.0 writes by default, so it is refused rather than read (docs/design/sar.md §6).
//    * CSXCAD's DiscMaterial reader has no d_order. It reads /DiscData into one flat buffer and indexes it
//      ix + iy·Nx + iz·Nx·Ny (CSPropDiscMaterial::GetWeightingPos) — x fastest, so the dataset's dimensions are
//      (nz, ny, nx). DiscMaterialWriter permutes into that here and nowhere else.
//
//  The tests that pin it: OpenEmsHdf5Tests (sar_calc's own maxSAR_idx names the maximum of the array this reads),
//  DiscMaterialWriterTests (openEMS, loading a file this wrote, assigns each voxel the tissue it was given).
// ================================================================

namespace RfCore.Hdf5;

/// <summary>Where a 3-D cell array's elements sit, in circuitRF's memory and on openEMS's and CSXCAD's disks.</summary>
public static class OpenEmsArrayOrder
{
    /// <summary>The d_order a scalar dataset of openEMS 0.37.0 carries: C order over (x, y, z).</summary>
    public const string Scalar = "XYZ";

    /// <summary>The d_order a vector dataset carries: the component, then C order over (x, y, z).</summary>
    public const string Vector = "NXYZ";

    /// <summary>circuitRF's flat index of cell (ix, iy, iz) in an (nx, ny, nz) array: z fastest.</summary>
    public static int Index(int ix, int iy, int iz, int ny, int nz) => (ix * ny + iy) * nz + iz;

    /// <summary>
    /// A circuitRF-ordered (x, y, z) array rewritten x fastest, as CSXCAD indexes /DiscData: the returned array's
    /// dimensions are (nz, ny, nx).
    /// </summary>
    public static T[,,] ToCsxcadOrder<T>(T[] values, int nx, int ny, int nz)
    {
        var disk = new T[nz, ny, nx];
        for (int ix = 0; ix < nx; ix++)
        for (int iy = 0; iy < ny; iy++)
        for (int iz = 0; iz < nz; iz++)
            disk[iz, iy, ix] = values[Index(ix, iy, iz, ny, nz)];
        return disk;
    }

    /// <summary>brief-sar-11 — the inverse of <see cref="ToCsxcadOrder{T}"/>: a /DiscData buffer read flat (x fastest, as its
    /// (nz, ny, nx) dimensions store it) back into circuitRF's C order over (x, y, z).</summary>
    public static T[] FromCsxcadOrder<T>(T[] disk, int nx, int ny, int nz)
    {
        var values = new T[disk.Length];
        for (int iz = 0; iz < nz; iz++)
        for (int iy = 0; iy < ny; iy++)
        for (int ix = 0; ix < nx; ix++)
            values[Index(ix, iy, iz, ny, nz)] = disk[(iz * ny + iy) * nx + ix];
        return values;
    }
}
