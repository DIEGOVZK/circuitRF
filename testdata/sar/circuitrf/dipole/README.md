# SAR-7: openEMS's `Dipole_SAR`, rebuilt from `.c3d` objects and run by circuitRF

**Everything under `reference-grid/` and `own-grid/` is circuitRF OUTPUT, not a reference.** It is what circuitRF computed
from the workspace in this folder (`docs/sonnet-briefs/brief-sar-7-replicate-dipole-sar.md`). The reference it is compared
with is openEMS's own run of the tutorial, `testdata/sar/openems-ref/Dipole_SAR/`, which is kept on the developer machine
only (`testdata/sar/openems-ref/README.md`). What the comparison found is `docs/design/sar.md` §9.

## The workspace

| File | What it is |
|---|---|
| `.cws`, `tech.ctech` | the workspace and its technology: PEC and the tutorial's three tissues, copied from the shipped `tissue-materials.cmat` (`openEMS Dipole_SAR skin/headbone/brain (1 GHz)`, each with its density) |
| `Dipole/3d/Dipole.c3d` | the tutorial as `.c3d` objects: two PEC sheets 0.2 mm wide on the XZ plane (the 0.48·λ0 dipole at 1 GHz, 143.90 mm, split by the feed), a 50 Ω lumped port across ±1.25 mm at its centre, and a `Phantom` of shape `Ellipsoid3` at x = 100 mm (semi-axes 80/100/100 mm, shells 5 and 10 mm) in the three tissues |

Two embedded setups, identical except for the grid: openEMS, 0.5–1.5 GHz in 11 points, the tutorial's air box
(x −200…350 mm, y and z ±250 mm), `PmlCells` 8 on every face, end criterion −40 dB, at most 30,000 steps,
`CellConstantMaterial` on, a radiation pattern (for the power budget), and `Sar` at 1 GHz, 10 g, `Cube`, with `sar_calc`'s
cross-check.

| Setup | Grid | Output |
|---|---|---|
| `ReferenceGrid` | `OpenEms.GridLines` from the tutorial's own XML, `../../../../openems-ref/Dipole_SAR/sar_dipole.xml` (absent on a fresh clone, so this setup refuses there) | `reference-grid/` |
| `OwnGrid` | circuitRF's grid generator, `CellsPerWavelength` 10.32: 2.5 mm in the brain (εr 60) at 1.5 GHz | `own-grid/` |

## The output, per run

| File | What it is |
|---|---|
| `Dipole.s1p` | the run's Touchstone (GHz, RI, 50 Ω), at the sweep's 11 points |
| `port1_u`, `port1_i` | the lumped port's time-domain probes, as openEMS wrote them |
| `sar1.json` | the SAR record (`SarRunRecord`, sar.md §8.6): the peaks with their cubes, accepted, absorbed and radiated power, the tissue mass, `sar_calc`'s peak — as solved |
| `sar1.SAR10g.npy`, `sar1.x.npy`, `sar1.y.npy`, `sar1.z.npy` | the 10 g averaged SAR volume (float32, W/kg as solved) and its cell centres (m) |
| `run.json` | the grid line counts, the step openEMS stopped at, the energy there and openEMS's wall time, from its log |

## Commands

`DipoleSarReplicationTests` (routine) compares the two folders with the reference and runs no process.
`DipoleSarRerunTests` (`Category=Benchmark`) runs both setups again through the CLI and holds them to the same tolerances;
with `CRF_WRITE_SAR7_OUTPUTS=1` it rewrites the two folders:

```
CRF_WRITE_SAR7_OUTPUTS=1 dotnet test tests/Ui.Tests --settings circuitrf.benchmark.runsettings --filter "FullyQualifiedName~DipoleSarRerunTests"
```

A single run by hand: `circuitrf em testdata/sar/circuitrf/dipole/Dipole/3d/Dipole.c3d --setup ReferenceGrid` (or
`OwnGrid`); it writes `results/`, which is git-ignored.
