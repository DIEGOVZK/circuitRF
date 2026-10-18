// brief-sar-5 R-sar5-1 — the SAR engine's grid, built from openEMS's raw SAR dump (DumpType 29). The dump holds
// exactly what the engine takes, as openEMS assigned it per cell — the E phasor, σ, ρ and the cell volume — so no
// material is looked up again and nothing can disagree about which tissue a cell holds.
//
// STREAMED. RfCore reads the dump one x slab at a time (OpenEmsHdf5.ReadSarRaw) and SarGridBuilder reduces each slab's
// E to ½σ|E|² as it arrives, so the complex field is never in memory whole (R-sar5-5). The engine sees arrays only;
// it never opens a file and knows nothing about openEMS (overview L5, R-sar4-5).

using CircuitRF.Engine.Em3d.Sar;
using RfCore.Hdf5;

namespace CircuitRF.Design.Em3d;

public static class OpenEmsSarGrid
{
    /// <summary>Roughly how many cells each slab read holds: ~2 M cells is ~60 MB of slab, whatever the grid.</summary>
    public const int SlabCells = 2_000_000;

    /// <summary>The engine's grid from the raw SAR dump at <paramref name="rawDumpPath"/>, at frequency index
    /// <paramref name="frequencyIndex"/>, read in x slabs of about <paramref name="slabCells"/> cells.</summary>
    public static SarGrid Read(string rawDumpPath, int frequencyIndex = 0, int slabCells = SlabCells)
    {
        var layout = OpenEmsHdf5.ReadSarRawLayout(rawDumpPath);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frequencyIndex, layout.FrequenciesHz.Length);
        var mesh = layout.Mesh;
        var builder = new SarGridBuilder(SarAxis.FromCentres(mesh.X, layout.CellWidthX), SarAxis.FromCentres(mesh.Y, layout.CellWidthY),
                                         SarAxis.FromCentres(mesh.Z, layout.CellWidthZ));
        int plane = mesh.Ny * mesh.Nz;
        int step = Math.Clamp(slabCells / Math.Max(1, plane), 1, mesh.Nx);
        for (int x = 0; x < mesh.Nx; x += step)
        {
            int count = Math.Min(step, mesh.Nx - x);
            var slab = OpenEmsHdf5.ReadSarRaw(rawDumpPath, frequencyIndex, x, count);
            builder.AddSlab(x, count, SarComplex64.AsFloats<ComplexF>(slab.Ex), SarComplex64.AsFloats<ComplexF>(slab.Ey),
                            SarComplex64.AsFloats<ComplexF>(slab.Ez), slab.Conductivity, slab.Density, slab.Volume);
        }
        return builder.Build();
    }

    /// <summary>The dump's frequency at <paramref name="frequencyIndex"/>, Hz.</summary>
    public static double FrequencyHz(string rawDumpPath, int frequencyIndex = 0) =>
        OpenEmsHdf5.ReadSarRawLayout(rawDumpPath).FrequenciesHz[frequencyIndex];
}
