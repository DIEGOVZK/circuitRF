# SAR-12: a system check — a half-wave dipole below a flat phantom, at 1.9 GHz

**Everything under `output/` is circuitRF OUTPUT, and its values are circuitRF's own.** A laboratory system check compares
a measured psSAR1g against a target value for one dipole and one phantom; those target values come from a standard that is
not available here (`docs/sonnet-briefs/brief-sar-0-overview.md` L2), so nothing in this folder is a target value. What the
runs found, with their grid convergence, is `docs/design/sar.md` §14.

## The workspace

| File | What it is |
|---|---|
| `.cws`, `tech.ctech` | the workspace and its technology: PEC, and from the shipped `tissue-materials.cmat` the head tissue-simulating liquid (its target table, evaluated at 1.9 GHz: εr 40, σ 1.4 S/m) and circuitRF's own phantom shell (εr 3.7, lossless) |
| `SystemCheck/3d/SystemCheck.c3d` | the dipole, a group of two PEC sheets on the XY plane at z = 0 (68.0 mm end to end, 3.8 mm wide, a 2 mm feed gap with a 50 Ω lumped port), and a `FlatBox` phantom whose liquid's floor is at z = 10 mm |

The geometry is FCC OET Bulletin 65 Supplement C's (Edition 01-01) system-check configuration at 1900 MHz, as its table
"Dipole thickness, flat phantom sagging and separation distance requirements" and the list before it state it: a dipole
68.0 mm long and 3.8 mm in diameter (here a strip of that width), 10.0 mm from its axis to the liquid's surface, a shell of
at most 5.0 mm on the side the dipole couples to (here 2.0 mm), and 15 cm of liquid. The phantom's 160 × 100 mm of liquid
is circuitRF's own choice, above the bulletin's minimum of ¾ wavelength along the dipole by ½ across it (118 × 79 mm).

## The setups

| Setup | Grid | Output |
|---|---|---|
| `Template` | `CellsPerWavelength` 15 | not run itself: the band sweep `SystemCheck` (band 1.9 GHz) makes and runs `SystemCheck 1.9 GHz` from it |
| `SystemCheck 1.9 GHz` (made by the band sweep) | 15 | `output/medium/` |
| `Coarse` | 10 | `output/coarse/` |
| `Fine` | 20 | `output/fine/` |

Each: openEMS, 1.7–2.1 GHz in 41 points, 40 mm of air on every side, `PmlCells` 8, end criterion −40 dB, `Sar` at 1.9 GHz,
1 g and 10 g, `Cube`. Each output folder holds the run's Touchstone, its SAR record (`sar1.json`, as solved) and
`run.json` (the grid, the step openEMS stopped at and its wall time, from its log).

## Commands

```
circuitrf em testdata/sar/circuitrf/system-check/SystemCheck/3d/SystemCheck.c3d --bands SystemCheck
circuitrf em testdata/sar/circuitrf/system-check/SystemCheck/3d/SystemCheck.c3d --setup Coarse
```

Each writes `results/`, which is git-ignored. `SystemCheckResultTests` (routine) reads `output/medium/` and runs no
process; `SystemCheckRerunTests` (`Category=Benchmark`) runs all three again through the CLI and, with
`CRF_WRITE_SAR12_OUTPUTS=1`, rewrites `output/`:

```
CRF_WRITE_SAR12_OUTPUTS=1 dotnet test tests/Ui.Tests --settings circuitrf.benchmark.runsettings --filter "FullyQualifiedName~SystemCheckRerunTests"
```

A user with target values of their own for this dipole and phantom enters them in the band sweep's Targets
(`{"FrequencyGHz": 1.9, "MassGrams": 1, "PerWattWkg": …}`, or the Targets box of Band Sweep…), and the table shows the
difference.
