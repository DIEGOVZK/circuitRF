# SAR-8: openEMS's `MRI_Loop_Coil`, rebuilt from `.c3d` objects and run by circuitRF

**Everything under `reference-grid/` and `own-grid/` is circuitRF OUTPUT, not a reference.** It is what circuitRF computed
from the workspace in this folder (`docs/sonnet-briefs/brief-sar-8-replicate-mri-loop-coil.md`). The reference it is compared
with is openEMS's own run of the tutorial, `testdata/sar/openems-ref/MRI_Loop_Coil/`, which is kept on the developer machine
only (`testdata/sar/openems-ref/README.md`). What the comparison found is `docs/design/sar.md` §10.

## The workspace

| File | What it is |
|---|---|
| `.cws`, `tech.ctech` | the workspace and its technology: PEC and the bundled head phantom's three tissues, copied from the shipped `tissue-materials.cmat` (`openEMS head phantom skin/skull/brain (298 MHz)`, each with its density) |
| `Loop/3d/Loop.c3d` | the tutorial as `.c3d` objects: eight PEC sheets on the YZ plane at x = −130 mm making the 80 mm (z) × 60 mm (y) loop of 5 mm strips with gaps of a third of the strip width; three 5.4 pF lumped elements (`C = Cgap`, a VAR in pF) at the midpoints of the two sides and the top; a 2.5 Ω lumped port in the bottom gap; and a `Phantom` of shape `Ellipsoid3` at the origin (semi-axes 100/80/100 mm, shells 9 and 9 mm) in the three tissues, **voxelized at 2.5 mm** (`Voxel`), as the tutorial's bundled phantom is |

Two embedded setups, identical except for the grid: openEMS, 0.4–595.6 MHz in 497 points (the reference's own 1.2 MHz points,
centred on 298 MHz, so the pulse is f0 298 MHz, fc 297.6 MHz against the script's 300), the tutorial's air box (the mesh box
± 200 mm), `PmlCells` 0 on every face (`MUR`, as the script's `['MUR']*6`), end criterion −40 dB, at most 1,000,000 steps,
`CellConstantMaterial` on, and `Sar` at 298 MHz, 10 g, `Cube`, B1 with B0 along +z, with `sar_calc`'s cross-check.

| Setup | Grid | Output |
|---|---|---|
| `ReferenceGrid` | `OpenEms.GridLines` from the tutorial's own XML, `../../../../openems-ref/MRI_Loop_Coil/MRI_Loop_Coil.xml` (absent on a fresh clone, so this setup refuses there) | `reference-grid/` |
| `OwnGrid` | circuitRF's grid generator, `CellsPerWavelength` 34.67: 2 mm in the brain (εr 52.7) at 595.6 MHz, the script's phantom target | `own-grid/` |

## The output, per run

| File | What it is |
|---|---|
| `Loop.s1p` | the run's Touchstone (GHz, RI, 2.5 Ω), at the sweep's 497 points |
| `port1_u`, `port1_i` | the lumped port's time-domain probes, as openEMS wrote them: S11 and Yin at any frequency come from these |
| `sar1.json` | the SAR record (`SarRunRecord`, sar.md §8.6): the peaks with their cubes, accepted and absorbed power, the tissue mass, mean \|B1+\| over the brain, `sar_calc`'s peak — as solved |
| `sar1.SAR.z0.npy`, `sar1.SAR.y0.npy` | local SAR (float32, W/kg as solved) on the tutorial's two planes, z = 0 (x, y) and y = 0 (x, z), linear between the volume's cell layers either side of the plane |
| `sar1.B1p.z0.npy`, `sar1.B1p.y0.npy`, `sar1.B1m.z0.npy`, `sar1.B1m.y0.npy` | \|B1+\| and \|B1−\| (T as solved) on the same planes |
| `sar1.x.npy`, `sar1.y.npy`, `sar1.z.npy` | the volume's cell centres (m) |
| `run.json` | the grid line counts, the time step, the step openEMS stopped at and what stopped it, the energy there and openEMS's wall time, from its log |

The volumes themselves (3.7 MB each) stay in the run directory.

## Commands

`LoopCoilReplicationTests` (routine) compares `reference-grid/` with the reference and runs no process; `LoopPhantomVoxelTests`
(routine, needs openEMS and the reference set) has openEMS load the voxelized phantom on the reference's lines and holds it to
the tutorial's own material grid, cell for cell. `LoopCoilRerunTests` (`Category=Benchmark`) runs both setups again through the
CLI, holds run (a) to the same tolerances and reports run (b); with `CRF_WRITE_SAR8_OUTPUTS=1` it rewrites the two folders:

```
CRF_WRITE_SAR8_OUTPUTS=1 dotnet test tests/Ui.Tests --settings circuitrf.benchmark.runsettings --filter "FullyQualifiedName~LoopCoilRerunTests"
```

A single run by hand: `circuitrf em testdata/sar/circuitrf/loop/Loop/3d/Loop.c3d --setup ReferenceGrid` (or `OwnGrid`); it
writes `results/`, which is git-ignored.

## SAR-9: the same coil with ports for capacitors

`LoopPorts/3d/LoopPorts.c3d` is `Loop.c3d` on the reference's lines with the three capacitors replaced by 50 Ω lumped
ports (2, 3, 4) and the feed a 50 Ω port 1; its one setup, `CoSim`, keeps every port's fields (`Sar.PerPortFields`).
`cosim/LoopCosim.cnl` places its `.s4p` as an S-parameter block, puts the tutorial's 5.4 pF back across the gaps, drives
port 1 from 2.5 Ω, and declares the `fields` analysis `F1` at 298 MHz. **`cosim/output/` is circuitRF OUTPUT**: the
`.s4p`, the field set's manifest, the co-simulation record and the recombined SAR and B1 planes (its README says which
command wrote each). `CosimLoopCoilTests` holds it to `reference-grid/` (`docs/design/sar.md` §11.6). The field set itself
(201 MB) stays in `results/`.

```
CRF_WRITE_SAR9_OUTPUTS=1 dotnet test tests/Ui.Tests --settings circuitrf.benchmark.runsettings --filter "FullyQualifiedName~CosimLoopCoilRerunTests"
```
