# SAR-8b: openEMS's `MRI_LP_Birdcage`, rebuilt from `.c3d` objects on circuitRF's cylindrical grid

**Everything under `reference-grid/` is circuitRF OUTPUT, not a reference.** It is what circuitRF computed from the workspace in
this folder (`docs/sonnet-briefs/brief-sar-8b-replicate-birdcage.md`). The reference it is compared with is openEMS's own run of
the tutorial, `testdata/sar/openems-ref/MRI_LP_Birdcage/`, which is kept on the developer machine only
(`testdata/sar/openems-ref/README.md`). What the comparison found is `docs/design/sar.md` §10a.

## The workspace

| File | What it is |
|---|---|
| `.cws`, `tech.ctech` | the workspace and its technology: PEC and the bundled body phantom's three tissues, copied from the shipped `tissue-materials.cmat` (`openEMS body phantom skin/bone/tissue (128 MHz)`, each with its density) |
| `Birdcage/3d/Birdcage.c3d` | the tutorial as `.c3d` objects, from its stated parameters: eight rungs at 45° steps on the cylinder r = 120 mm, each two 10 mm `CylinderSheet` strips (z −125 to −2.5 mm and 2.5 to 125 mm; to 115 mm on the two fed rungs); the two end rings, `CylinderSheet`s round the full circle at z ±120 to ±130 mm; eight 2.6 pF lumped elements (`C = Ccap`, a VAR in pF) in the 5 mm gaps at the rungs' middles; two 50 Ω lumped ports in the gaps between rungs 1 and 3 and the upper ring (z 115 to 120 mm), every gap given as a `Cylinder` patch; and a `Phantom` of shape `Cylinder3` at the origin (radius 100 mm, 260 mm long, shells 8 and 10 mm) in the three tissues, **voxelized at 2.5 mm** (`Voxel`), as the tutorial's bundled phantom is |
| `cosim/BirdcageQuadrature.cnl` | the quadrature feed: the run's `.s2p` as an S-parameter block, both feeds driven from matched 50 Ω circuit ports with weights 1 and `polar(1,-90)`, and the `fields` analysis `F1` at 128 MHz that recombines the run's per-port fields |

One embedded setup, `ReferenceGrid`: openEMS, 0.75–149.25 MHz in 198 points (the reference's own 0.75 MHz points, so the writer's
pulse is f0 75 MHz, fc 74.25 MHz against the script's 75), a cylindrical grid about z on the tutorial's own lines
(`OpenEms.GridLines`, `../../../../openems-ref/MRI_LP_Birdcage/BirdCage.xml`, absent on a fresh clone, so the setup refuses there),
openEMS's MultiGrid at 10 and 20 mm, the air box's side faces PEC (the bore shield at r = 320 mm) and its ends absorbing with
`PmlCells` 8 (the tutorial's PML_8 in its 10 added lines), end criterion −40 dB, at most 1,000,000 steps, `CellConstantMaterial`
on, and `Sar` at 128 MHz with B1 (B0 along +z) and every port's fields kept (`PerPortFields`). On a cylindrical grid SAR is
local only: no cube-averaged SAR is computed.

## The output, in `reference-grid/`

| File | What it is |
|---|---|
| `Birdcage.s2p` | the run's Touchstone (GHz, RI, 50 Ω), at the sweep's 198 points |
| `p1.port1_u`, `p1.port1_i`, `p1.port2_u`, `p1.port2_i` | both ports' time-domain probes in the run with port 1 driven (port 2 terminated in its 50 Ω), as openEMS wrote them |
| `p2.port1_u` … `p2.port2_i` | the same with port 2 driven |
| `run1.json`, `run2.json` | each run's grid line counts, time step, the step openEMS stopped at and what stopped it, the energy there and openEMS's wall time, from its log |
| `fields.json` | the per-port field set's manifest (the field files themselves stay in `results/`) |
| `sar1.json`, `cosim.json` | the recombination's SAR record (`SarRunRecord`, as solved, its grid `cylindrical`) and its co-simulation record (the waves, the powers, the checks) |
| `sar1.SAR.z0.npy`, `sar1.B1p.z0.npy`, `sar1.B1m.z0.npy` | local SAR and \|B1±\| (float32, as solved) on the plane of cells nearest z = 0 — the cells at z = 1.25 mm, the tutorial's own plane — over (ρ, α) |
| `sar1.x.npy`, `sar1.y.npy`, `sar1.z.npy` | the recombined volumes' cell centres: ρ (m), α (rad) and z (m) |

## Commands

`BirdcageReplicationTests` (routine) compares `reference-grid/` with the reference and runs no process; `BirdcageLoweringTests`
(routine, needs the reference's XML) holds the lowering to the tutorial's structure box for box, and `BirdcagePhantomVoxelTests`
(routine, needs openEMS and the reference set's `material_raw.h5`) has openEMS load the voxelized phantom on the cylindrical lines
and holds it to the tutorial's own material grid, cell for cell. `BirdcageRerunTests` (`Category=Benchmark`, about two hours) runs
the setup and the recombination again through the CLI and holds them to the same tolerances; with `CRF_WRITE_SAR8B_OUTPUTS=1` it
rewrites `reference-grid/`:

```
CRF_WRITE_SAR8B_OUTPUTS=1 dotnet test tests/Ui.Tests --settings circuitrf.benchmark.runsettings --filter "FullyQualifiedName~BirdcageRerunTests"
```

By hand: `circuitrf em testdata/sar/circuitrf/birdcage/Birdcage/3d/Birdcage.c3d --setup ReferenceGrid`, then
`circuitrf fields testdata/sar/circuitrf/birdcage/cosim/BirdcageQuadrature.cnl`; both write `results/`, which is git-ignored.
`BirdcageCollectTests` (`Category=Benchmark`) collects and compares such a run without repeating it:
`CRF_SAR8B_RESULTS=<that results folder>`, with `CRF_SAR8B_HOLD=1` to hold the tolerances and `CRF_WRITE_SAR8B_OUTPUTS=1` to
rewrite `reference-grid/`.
