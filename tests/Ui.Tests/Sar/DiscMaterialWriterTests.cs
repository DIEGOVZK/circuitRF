// ================================================================
//  DiscMaterialWriterTests.cs — the gate for brief-sar-4 R-sar4-3: whether CSXCAD accepts a DiscMaterial file
//  circuitRF wrote, judged by CSXCAD itself and not by a generic HDF5 tool. A 3 × 2 × 2 voxel file, every voxel a
//  different tissue, is loaded by the installed openEMS in a 20-step run with a raw SAR dump (DumpType 29) over it;
//  openEMS's dump states the conductivity and density it assigned each cell, read back through RfCore's own
//  OpenEmsHdf5, and each must be the tissue the file declares for the voxel that cell sits in. That also pins the
//  writer's x-fastest index order (OpenEmsArrayOrder): a transposed file assigns the wrong tissue to 10 of 12 cells.
//  Permittivity is not in any per-cell dump; it is read by the same CSXCAD call as conductivity and density.
//  Skips with a reason where openEMS is absent.
// ================================================================

using System.Diagnostics;
using CircuitRF.Design.Em3d;
using CircuitRF.Ui.Tests.Em3d;
using RfCore.Hdf5;
using Xunit;

namespace CircuitRF.Ui.Tests.Sar;

public sealed class DiscMaterialWriterTests
{
    [OpenEmsFact]
    public void AFileCircuitRfWrote_LoadedByOpenEms_GivesEachVoxelTheMaterialItDeclares()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"crf-sar4-disc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // 3 × 2 × 2 voxels of 1 mm from the origin; voxel (x, y, z) holds tissue 1 + x + 3y + 6z.
            const int nx = 3, ny = 2, nz = 2;
            var indices = new byte[nx * ny * nz];
            for (int x = 0; x < nx; x++) for (int y = 0; y < ny; y++) for (int z = 0; z < nz; z++)
                indices[OpenEmsArrayOrder.Index(x, y, z, ny, nz)] = (byte)(1 + x + 3 * y + 6 * z);
            var tissues = Enumerable.Range(0, 13)
                .Select(i => i == 0 ? new DiscTissue("Background", 1, 0, 0) : new DiscTissue($"T{i}", 1 + i, 0.1f * i, 1000 + 10 * i))
                .ToList();
            DiscMaterialWriter.Write(Path.Combine(dir, "disc.h5"), [0, 1e-3, 2e-3, 3e-3], [0, 1e-3, 2e-3], [0, 1e-3, 2e-3], indices, tissues);
            File.WriteAllText(Path.Combine(dir, "model.xml"), Model);

            var psi = new ProcessStartInfo(SolverDiscovery.OpenEms.Find(out _)!.Path)
            {
                WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.ArgumentList.Add("model.xml");
            using var proc = Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEndAsync();
            string log = proc.StandardOutput.ReadToEnd() + stderr.Result;
            proc.WaitForExit();
            Assert.DoesNotContain("Error", log, StringComparison.Ordinal);

            var dump = OpenEmsHdf5.ReadSarRaw(Path.Combine(dir, "SAR.h5"));
            var m = dump.Mesh;
            int inside = 0;
            for (int ix = 0; ix < m.Nx; ix++) for (int iy = 0; iy < m.Ny; iy++) for (int iz = 0; iz < m.Nz; iz++)
            {
                int at = OpenEmsArrayOrder.Index(ix, iy, iz, m.Ny, m.Nz);
                int vx = (int)Math.Floor(m.X[ix] / 1e-3), vy = (int)Math.Floor(m.Y[iy] / 1e-3), vz = (int)Math.Floor(m.Z[iz] / 1e-3);
                if (vx is < 0 or >= nx || vy is < 0 or >= ny || vz is < 0 or >= nz)
                {
                    Assert.Equal(0f, dump.Density[at]);
                    continue;
                }
                var t = tissues[indices[OpenEmsArrayOrder.Index(vx, vy, vz, ny, nz)]];
                Assert.True(Math.Abs(t.Kappa - dump.Conductivity[at]) <= 1e-6 * t.Kappa && t.Density == dump.Density[at],
                            $"voxel ({vx}, {vy}, {vz}) is {t.Name} (σ {t.Kappa}, ρ {t.Density}); openEMS assigned σ {dump.Conductivity[at]}, ρ {dump.Density[at]}");
                inside++;
            }
            Assert.Equal(nx * ny * nz, inside);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A mm setup, one cell per voxel, cell-constant material (each cell takes the material at its centre),
    /// a soft source off to the side and the raw SAR dump over the voxels. DiscFile Scale 1000: mm to the file's metres.</summary>
    private const string Model = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes" ?>
        <openEMS>
          <FDTD NumberOfTimesteps="20" endCriteria="1e-5" CellConstantMaterial="1" f_max="2e9">
            <Excitation Type="0" f0="1e9" fc="1e9"></Excitation>
            <BoundaryCond xmin="0" xmax="0" ymin="0" ymax="0" zmin="0" zmax="0"></BoundaryCond>
          </FDTD>
          <ContinuousStructure CoordSystem="0">
            <Properties>
              <DiscMaterial Name="voxels">
                <Property Epsilon="1,1,1" Mue="1,1,1" Kappa="0,0,0" Sigma="0,0,0" Density="0" />
                <Weight Epsilon="1,1,1" Mue="1,1,1" Kappa="1,1,1" Sigma="1,1,1" Density="1" />
                <DiscFile Type="0" File="disc.h5" UseDBBackground="1" Scale="1000" />
                <Primitives><Box Priority="10"><P1 X="0" Y="0" Z="0" /><P2 X="3" Y="2" Z="2" /></Box></Primitives>
              </DiscMaterial>
              <Excitation Name="src" Type="0" Excite="0,0,1">
                <Primitives><Box Priority="0"><P1 X="-2" Y="-2" Z="-2" /><P2 X="-2" Y="-2" Z="4" /></Box></Primitives>
              </Excitation>
              <DumpBox Name="SAR" DumpType="29" DumpMode="2" FileType="1">
                <Primitives><Box Priority="0"><P1 X="0" Y="0" Z="0" /><P2 X="3" Y="2" Z="2" /></Box></Primitives>
                <FD_Samples>1e9</FD_Samples>
              </DumpBox>
            </Properties>
            <RectilinearGrid DeltaUnit="0.001" CoordSystem="0">
              <XLines>-3,-2,-1,0,1,2,3,4,5</XLines>
              <YLines>-3,-2,-1,0,1,2,3,4</YLines>
              <ZLines>-3,-2,-1,0,1,2,3,4</ZLines>
            </RectilinearGrid>
          </ContinuousStructure>
        </openEMS>
        """;
}
