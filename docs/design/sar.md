# circuitRF — SAR and B1 for MRI coil designers and phone SAR

**Status:** SAR-1 done (§1–§3), SAR-2 done (§4) · **Series:** [`brief-sar-0-overview.md`](../sonnet-briefs/brief-sar-0-overview.md) ·
**Reference data:** [`testdata/sar/openems-ref/`](../../testdata/sar/openems-ref/README.md) · **Builds on:**
[`em-3d.md`](em-3d.md) (the openEMS backend) and [`em-3d-f0-findings.md`](em-3d-f0-findings.md) (its validated versions).

This note is for research and engineering use (overview L1). Nothing here claims compliance with any standard: the
averaging **follows the cube-averaging procedure of IEC/IEEE 62704-1 as openEMS implements it**, and later phases
agree with openEMS's `sar_calc` to the tolerances in overview §4.

---

## 1. What SAR, psSAR and B1± are

### 1.1 Local SAR

The specific absorption rate is the power dissipated in tissue per unit mass. For a time-harmonic field given as a
**peak** phasor **E** (openEMS's frequency-domain dumps are peak amplitudes), in a cell of conductivity σ (S/m) and
density ρ (kg/m³):

```
SAR = σ |E|² / (2 ρ)          W/kg          (|E|² = |Ex|² + |Ey|² + |Ez|²)
```

The ½ is the time average of a peak phasor; with an RMS phasor it is `σ|E|²/ρ`. openEMS's `sar_calc` computes the
local power density `½ σ|E|²` per cell, zeroes it where ρ = 0 (air), and divides by ρ. `testdata/sar/openems-ref/
analytic/` holds the check: σ 0.5 S/m, ρ 1000 kg/m³, |Ex| 1 V/m peak gives 2.5e-4 W/kg in every cell.

### 1.2 Absorbed power, mass, whole-body SAR

Over the cells i of a region, with cell volume Vᵢ:

```
P_abs = Σ ½ σᵢ |Eᵢ|² Vᵢ  = Σ SARᵢ ρᵢ Vᵢ          (W)
m     = Σ ρᵢ Vᵢ                                   (kg, cells with ρ > 0)
SAR_wb = P_abs / m                                (W/kg; "whole-phantom" for a phantom)
```

`sar_calc` writes `power` and `mass` as attributes of its result file. `Dipole_SAR` prints `power / P_acc / mass` as
its "whole body SAR".

### 1.3 Peak spatial-average SAR (psSAR1g, psSAR10g)

psSAR_m is the largest average of local SAR over a **cube of mass m** (1 g or 10 g of tissue). The average over a
cube is mass-weighted:

```
SAR_avg(cube) = Σ SARᵢ ρᵢ Vᵢ,cube / Σ ρᵢ Vᵢ,cube
```

where Vᵢ,cube is the part of cell i inside the cube (cells cut by its faces count in part). How the cube is chosen is
the procedure, and is what SAR-5 reproduces. As openEMS implements it (`tools/sar_calculation.cpp`, read to learn
from, never copied, overview L3), per voxel:

1. a cube **centred on the voxel** is grown until its tissue mass equals m. `IEEE_62704` allows a mass error of 1e-6
   relative and up to 100 iterations;
2. the cube is **valid** if, in addition, its air (background) fraction is below **10 %** and none of its faces lies
   entirely in air. A valid cube's average is that voxel's averaged SAR;
3. a tissue voxel that is neither the centre of a valid cube nor inside one is **unused**. For it, a cube of the
   right mass is built with one face on the voxel, in each of the six directions, and it takes the largest of their
   averages among those whose volume is within 5 % of the smallest. A direction with no such cube contributes zero,
   and `sar_calc` prints `can't find fitting cubical averaging volume` for it. `analytic/random_sar_calc.log` shows
   that line repeatedly, because a 1 g cube does not fit everywhere in an 8 g phantom.

`IEEE_C95_3` is the older procedure: 5 % mass tolerance, no air limit, partial cubes counted as used. `SIMPLE` is the
local SAR only. The result file counts `valid_cubes`, `used_cubes`, `unused_cubes` and `air_cubes`.

### 1.4 Normalisation (overview L7)

Every SAR, power and B1 value openEMS writes is at the run's excitation, which is arbitrary and tiny
(`Dipole_SAR` accepts 1.6e-27 W). The tutorials normalise to **1 W accepted** at the feed: SAR per watt is
`SAR / P_acc`, B1 per √W is `B1 / √P_acc`, with `P_acc = ½ Re{V I*}` at the port at f0 (`port.P_acc`). circuitRF keeps
that default (L7).

### 1.5 B1+ and B1− — derived once (overview L8)

openEMS uses the phasor convention `b(t) = Re{B e^{jωt}}`, with **B** = μ₀**H**. Take B0 along **+z** and split the
transverse phasor (Bx, By) into two circularly polarised parts:

```
Bx x̂ + By ŷ  =  B1+ (x̂ − j ŷ)  +  B1− (x̂ + j ŷ),     B1+ = (Bx + j By)/2,   B1− = (Bx − j By)/2
```

(check: x̂ (B1+ + B1−) + j ŷ (B1− − B1+) = x̂ Bx + j ŷ (−j By) = x̂ Bx + ŷ By). In time:

```
Re{B1+ (x̂ − j ŷ) e^{jωt}} = |B1+| ( x̂ cos(ωt+φ₊) + ŷ sin(ωt+φ₊) )    rotates from +x toward +y
Re{B1− (x̂ + j ŷ) e^{jωt}} = |B1−| ( x̂ cos(ωt+φ₋) − ŷ sin(ωt+φ₋) )    rotates from +x toward −y
```

So |B1+| is the amplitude of the part rotating counter-clockwise seen from +z, and |B1−| that of the part rotating
clockwise. **This series takes openEMS's definition as it stands** (L8): it is exactly what `MRI_Loop_Coil.py` and
`MRI_LP_Birdcage.m` compute. Which of the two is the transmit field depends on the sign convention for precession, and
the literature uses both; circuitRF does not re-derive that choice. A B0 along **−z** swaps the two (the setup key
SAR-6 adds). The birdcage computes Bx, By from its cylindrical Br, Bα first: `Bx = Br cos α − Bα sin α`,
`By = Br sin α + Bα cos α`.

---

## 2. What openEMS 0.37.0 offers (surveyed 2026-10-17, run on 0.37.0)

- **Dump types** (CSXCAD's `AddDump`): 10/11 the E/H field in the frequency domain; **20** local SAR, **21** 1 g and
  **22** 10 g averaged SAR (frequency domain); **29** the **raw SAR data**: the E-field phasor, the cell volume, the
  conductivity and the density per cell, for `sar_calc` to average afterwards. File type 1 is HDF5; dump mode 2 is
  cell interpolation (values at cell centres, `dual_mesh = 1` in the file).
- **The raw dump** (`Dipole_SAR`'s `SAR.h5`, read with h5py): `/Mesh/{x,y,z}` the cell centres and `/CellWidth/{x,y,z}`
  their widths (float64, in the drawing unit's metres); `/CellData/{Conductivity,Density,Volume}` float32 (nx, ny,
  nz); `/FieldData/FD/f0` the E phasor, (3, nx, ny, nz) complex64 stored as an `r`/`i` compound; the frequency as an
  attribute of `/FieldData/FD`; file attributes `dump_type = 29`, `dual_mesh = 1`, `openEMS_HDF5_version = 0.3`.
  83 × 90 × 91 cells is **24.5 MB**.
- **`sar_calc`** (the GPL executable beside `openEMS`): `-i <raw.h5> -o <out.h5> --method IEEE_62704|IEEE_C95_3|SIMPLE
  --mass <g> [--autorange <dB>] [--numThreads n] [--export_cube_stats] [--legacyHDF5Dumps]`. `--method` defaults to
  `SIMPLE`; `--mass 0` gives local SAR. Its output carries the averaged SAR and the attributes `maxSAR`, `maxSAR_idx`,
  `power`, `mass`, `frequency` and the four cube counts. **Its exit status is inverted**: `main` returns
  `CalcFromHDF5`'s bool, so **1 is success and 0 failure**. SAR-5's runner must judge by the output file, as
  `testdata/sar/openems-ref/tools/run_analytic.py` does. Python's `SAR_Calculation(mass, method).CalcFromHDF5(in, out)`
  and Octave's `CalcSAR.m` wrap the same C++; `openEMS.sar_utils.readSAR` reads a result.
- **`CellConstantMaterial`** (an FDTD setting, on in all three tutorials): the cell takes the material at its centre
  rather than an average of its edges. The tutorials say it is less accurate and required for averaging per 62704-1.
  circuitRF's writer does not set it today (SAR-3).
- **Lumped elements**: `AddLumpedElement(name, ny, R|L|C)`, series or parallel (`MRI_Loop_Coil`'s three 5.4 pF
  capacitors; the birdcage's 2.6 pF). **`--exact-endcriteria`** evaluates the energy end criterion every Nyquist
  interval rather than every few seconds of wall-clock time (§3.2). `DiscMaterial` reads a voxel material from HDF5
  (the bundled phantoms). Graceful Ctrl-C; no MPI engine.

---

## 3. The reference set (SAR-1)

openEMS's own tutorials at the `v0.37.0` tag, run unmodified on openEMS 0.37.0 (`305df56`, CSXCAD `c46e320`) on an
Apple M4, and kept as fixed data **on the developer machine, not in the repository** (owner, 2026-10-18): the
repository holds the README (files, commands, versions) and the wrappers that made it, and `SarReferenceSetTests`
skips with a reason where the data is absent. Files, commands and versions: `testdata/sar/openems-ref/README.md`.

### 3.1 The numbers

**`Dipole_SAR`** (1 GHz, 0.48 λ dipole, 50 Ω feed, 20 mm from a three-layer ellipsoid head; 1.52 M cells, stopped by
the end criterion at step 2,272, 28 s):

| Quantity | Value |
|---|---|
| psSAR10g per W accepted | **2.435 W/kg** |
| whole-phantom SAR per W | **0.1509 W/kg** (phantom 4.348 kg) |
| radiated / absorbed / accepted | 32.3 % / 65.6 % / 100 % |
| **power budget** (radiated + absorbed) / accepted | **97.9 %** (the tutorial expects 95–100 %) |
| Zin at f0 | 53.14 + j3.73 Ω |

**`MRI_Loop_Coil`** (7 T, 298 MHz, 80 × 60 mm loop, three 5.4 pF capacitors, 2.5 Ω feed, beside the bundled ellipsoid
head phantom; 3.04 M cells, 77,409 steps, 266 s):

| Quantity | Value |
|---|---|
| S11 minimum | **−24.35 dB at 298.0 MHz** (on the script's 1.2 MHz grid) |
| Yin at f0 | 0.3955 + j0.0481 S |
| \|B1+\| at the phantom centre (x = y = z = 0) | **0.249 µT/√W** |
| peak local SAR per W, axial / sagittal plane | **5.67 / 5.09 W/kg** |

**`MRI_LP_Birdcage`**: see §3.3. **Analytic**: `test_sar_analytic.py`'s six tests pass on the installed build; the
uniform phantom gives 2.5e-4 W/kg exactly (to float32).

### 3.2 Run-to-run spread (R-sar1-5)

`Dipole_SAR` five times on the same machine (`Dipole_SAR/run-to-run.json`):

| Run | Stopped at step | Energy at stop | psSAR10g /W | Whole-phantom /W | Budget |
|---|---|---|---|---|---|
| 1 (the reference) | 2,272 | −46.15 dB | 2.4350 | 0.15095 | 97.93 % |
| 2 | 1,972 | −43.91 dB | 2.4408 | 0.15045 | 97.70 % |
| 3 | 1,988 | −44.31 dB | 2.4408 | 0.15061 | 97.76 % |
| 4, `--exact-endcriteria` | 1,775 | −41.50 dB | 2.4378 | 0.14992 | 97.39 % |
| 5, `--exact-endcriteria` | 1,775 | −41.50 dB | 2.4378 | 0.14992 | 97.39 % |

**The unmodified runs are not identical**, and the cause is the stop step, not the arithmetic: openEMS checks the
energy end criterion every few seconds of **wall-clock** time, so the step at which it stops depends on how loaded the
machine is. Across runs 1–3: psSAR10g **0.24 %**, accepted power **1.3e-5**, every 10 g cell within **0.29 %** of the
peak, |ΔS11| up to **9.4e-4**. With `--exact-endcriteria` the check runs every Nyquist interval (71 steps here) and
**the two runs are bit-identical**, in every array.

**For SAR-7:** the 0.24 % spread is the floor under overview §4's 2 % psSAR10g tolerance, and it is a property of the
reference, not of circuitRF. A circuitRF run of the same problem stops at its own step, so its difference from run 1
includes up to that spread. Running circuitRF's side with `--exact-endcriteria` makes it repeatable; it does not make
it equal to run 1, which was not.

The same effect appears in the F0 re-run on 0.37.0 (`em-3d-f0-findings.md` §6): the three short cases stopped at a
different step from rc3 and moved by up to 3.6e-6 in |S|, while the long ones, whose stop step matched, are
bit-identical.

### 3.3 MRI_LP_Birdcage

3 T (128 MHz) 8-rung low-pass birdcage, r 120 mm, 2.6 pF capacitors, driven in quadrature at two rungs (1 and −j,
the second as a quarter-period delay), inside a PEC bore shield, around the bundled layered-cylinder body phantom.
4.02 M cells on a cylindrical grid with sub-grids at 10 and 20 mm; the time step is 3.65e-13 s, set by the small cells
near the axis; the end criterion (−40 dB) stopped it at step 225,918, after **53 min** (288 MC/s; the engine chose 4
threads). The script's comment estimated 7 h at 65 MC/s.

| Quantity | Value |
|---|---|
| S11 minimum (both ports driven) | −3.68 dB at 127.5 MHz (0.75 MHz grid) |
| mean \|B1+\| at the smallest dumped radius (1.9 mm), per √W accepted at both ports | **0.498 µT/√W** (derived) |
| mean \|B1+\| / mean \|B1−\| over the plane (r ≤ 0.1 m) | 3.8 |
| peak local SAR on the plane, per W | **0.340 W/kg** (derived) |

The script normalises nothing; the two values marked derived are the converter's (`tools/birdcage_to_npy.py`),
by the other tutorials' rule. Neither SAR-7 nor SAR-8 gates on the birdcage: it is the reference for a later
birdcage replication and a check that a cylindrical, multi-port, quadrature-driven setup reads back as expected.

Two things it showed about the Octave route: `ReadHDF5Dump` compiles its HDF5 attribute reader (an `.oct` file) on
first use, into the openEMS install, which needs the Homebrew HDF5 headers; and Octave buffers the solver's console
output until it exits, so the run's progress cannot be followed from its log.

### 3.4 What the set does not hold, and why

- **Any of it in the repository.** The whole set, the raw dump-type-29 file of `Dipole_SAR` (24.5 MB) included, is
  on the developer machine only (owner, 2026-10-17 for the raw file, 2026-10-18 for the rest). SAR-5's comparison
  against `sar_calc` on the same file, and every later gate on these numbers, skips with a reason where it is absent.
- **The bundled phantom `.h5` files** (L3). The loop's XML names `phantom_head_298MHz.h5` under `${OPENEMS_INSTALL}`.
- **The FDTD settings as XML.** CSXCAD's `Write2XML` writes the structure and the grid; the excitation, boundaries and
  end criterion are in the scripts, and the README tables them.
- **S11 finer than the script's frequency step.** `MRI_Loop_Coil`'s vector steps 1.2 MHz, coarser than SAR-8's
  0.5 MHz gate on the S11 minimum, so the port's time-domain probes are kept and SAR-8 evaluates S11 from them.

---

## 4. Tissue materials, evaluated at one frequency (SAR-2)

### 4.1 The model

A body tissue's εr and σ move strongly with frequency (muscle σ is 0.69 S/m at 64 MHz and 0.98 S/m at 1 GHz), so a
tissue is not one constant pair. A material record (`TechMaterial`, em-3d.md §4.1a) may state one of two optional
dispersions, both additive and nullable, with no `FormatVersion` bump:

- **`ColeCole`** — `EpsInf`, `SigmaIonic` (S/m) and one to four `Terms` of `DeltaEps`, `TauSeconds`, `Alpha`, giving
  ε̂(ω) = ε∞ + Σ Δεₙ / (1 + (jωτₙ)^(1−αₙ)) + σᵢ/(jωε₀), the form of Gabriel, Lau and Gabriel (1996). With the e^{jωt}
  convention εr = Re ε̂ and σ = −ωε₀ Im ε̂, the **total** conductivity: ionic conduction and every dispersion's loss.
  The formula is `CircuitRF.Engine.Em3d.ColeCole` (numbers only). τ is stored in seconds, never in the paper's
  ps/ns/µs/ms columns.
- **`DielectricVsFrequency`** — target (εr, σ) rows at named frequencies, the way a tissue-simulating liquid is
  specified: interpolated linearly between the two nearest rows and **refused outside them**, never extrapolated.

`MaterialDispersion` (`src/Design/Layout`) is the one rule. The elaborator, `check`, `explain --materials` and the
material editor all call it. A record with either form is a **dielectric by declaration**: it states εr and σ₂₀, which
on their own would make it ambiguous, but its σ is a dielectric's loss at one frequency, never a metal's. So an object
made of it needs no Role (`C3dMaterialRole.Implied`).

**The plain numbers stay.** A dispersive record still states `Epsr` and `Sigma20`, its model's values at 1 GHz, which
is what a build without the model reads (it keeps the model's keys in `Unread` and writes them back). `check` warns,
as `tech.material.model-disagrees`, when they differ from the model at 1 GHz by more than 1 %, or when one is missing.
This is em-3d.md §4.1a's rule for an edited number that nothing reads.

`PerfusionRate` (1/s, Pennes' ω_b) and `MetabolicHeat` (W/m³, Pennes' Q_m) are stored for a later bioheat series. Nothing
reads them yet.

### 4.2 Where the frequency comes from (overview L6)

Every dispersive material in a 3D problem is evaluated **once, before lowering**, at one frequency. Each solver still
receives a plain εr and σ, with **tanδ 0**: the evaluated σ already carries the dielectric loss, and CSXCAD's Kappa is
σ + ωε₀εr·tanδ (`CsxcadWriter.Kappa`), so a non-zero tanδ beside it would count the loss twice. Nothing is evaluated
per FDTD time step. `C3dElaborationOptions.MaterialFrequencyHz` carries the frequency.

- **A setup that requests SAR** will name its SAR frequency (SAR-6), and that is the frequency.
- **Until then, and for any setup without SAR**, it is the sweep's centre (`C3dProblemAssembly.MaterialFrequency`): the
  arithmetic middle of a linear sweep, the geometric middle of a log one. The run's notes say so in one line:
  *"Frequency-dependent materials are evaluated once, at 298 MHz (the sweep's centre), and the solver sees those values
  at every frequency: Muscle εr 58.23, σ 0.77 S/m."*
- **A table asked outside its rows refuses the run**, naming the material, its range and the frequency asked.
- Without a frequency (an editor preview, a thermal run, a comparison between technologies) a dispersive record is its
  plain numbers.

An openEMS run is broadband, but its tissues are constant, so its SAR and B1 are valid at that one frequency only.
SAR-6's result says so (L6).

**Not reached: a layout stackup.** A layout placed in a 3D view resolves its stackup's materials through
`Em3dLayoutSolids`, which takes no frequency. A stackup entry naming a tissue gets its plain (1 GHz) numbers there. No
phase needs a tissue in a layout stackup. If one does, `Em3dLayoutSolidsOptions` takes the same frequency.

### 4.3 The shipped tissue library

`tissue-materials.cmat` ships beside `generic-materials.cmat`. No shipped technology names it. The technology editor's
**Add Tissue Materials** copies it beside a `.ctech` and names it, exactly as *Add Generic Materials* does
(`MaterialLibraries.CopyShippedBeside`), and an existing file of that name that differs is refused, never
overwritten. Its 28 records:

| Records | Values from | Density from |
|---|---|---|
| 16 Gabriel tissues: skin (dry, wet), fat, muscle, cortical and cancellous bone, brain grey and white matter, cerebrospinal fluid, blood, vitreous humour, lung (inflated, deflated), heart, liver, kidney | Gabriel, Lau and Gabriel, *Phys. Med. Biol.* 41 (1996) 2271–2293, Table 1; the same parameters as Gabriel's report AL/OE-TR-1996-0037, Appendix C, entered from the IFAC-CNR copy of it | ICRU Report 44 (1989), as NIST SRD 126 Table 2 tabulates it, for fat (adipose), muscle (skeletal), cortical bone, blood, brain grey and white (one row), and deflated lung. **The other eight state no density** (below) |
| Head and body tissue-simulating liquids | FCC OET Bulletin 65 Supplement C (Edition 01-01), Appendix C: 150 MHz to 5.8 GHz, the 1800–2000 MHz row entered at both ends. The bulletin itself prescribes linear interpolation between target frequencies | 1000 kg/m³, stated under the same table |
| Saline gel (implant heating) | σ 0.47 S/m: Fujimoto et al., *Front. Physiol.* 9 (2018) 1439 (open access). εr 80: the recommended value as Aboyewa et al., AAPM 2021, quote it | water's, NIST SRD 126 (neither source states the gel's) |
| 9 openEMS example tissues: the head phantom (298 MHz) and body cylinder (128 MHz) of `resources/phantoms/create_phantoms.py`, and `Dipole_SAR`'s three ellipsoids (1 GHz) | the values each script states, kept exactly, so SAR-7 and SAR-8 match without a model evaluation in between | the same scripts |

Names are plain (`Muscle`, `Fat`). None collides with a generic material, so none needs a `Tissue/` prefix. The fat
record is the paper's *not infiltrated* fat (5.46, 0.051 S/m at 900 MHz). The *infiltrated* row was not entered,
because no verifiable copy of its parameters was to hand.

**Two things the openEMS records show.** The fallback phantoms' script says its values are "at 298 MHz" and "at
128 MHz" from the Gabriel model, but they are not that model's values at those frequencies. Its 298 MHz skull is εr 11.4,
σ 0.29 S/m, while Gabriel cortical bone at 298 MHz is εr 13.4, σ 0.082 S/m (the script's pair is close to Gabriel's
1.8 GHz value). Its brain is εr 52.7, σ 0.95 S/m against grey matter's 60.1, 0.69 S/m. This is why R-sar2-5 keeps them
as the script states them, not as circuitRF's tissues. A replication that substituted circuitRF's Muscle or Bone would
disagree with the reference for a reason that has nothing to do with SAR. Each record's `Source` gives both pairs.

**What the library does not hold, and why (owner to decide).** *No record carries a number without a source*
(R-sar2-4), so where no source was verified, the record states nothing:

- **Density for eight tissues**: both skins, cancellous bone, CSF, vitreous humour, inflated lung, heart, liver and
  kidney. ICRU 46 / Woodard and White (1986) tabulate them, but no free copy was reachable to verify against. SAR in
  such a tissue cannot be computed until a density is stated, and SAR-5 must refuse it, naming the material.
- **Thermal conductivity, specific heat, perfusion and metabolic heat for every tissue.** The usual compilations are a
  purchased book or the online database O2 excludes. The fields exist and are empty.

### 4.4 Readouts

- **`explain <.c3d|.cem> --materials`** (MCP: `explain materials=true`): the problem's materials as the solver receives
  them; a dispersive one is marked `at <f> (Cole-Cole)` or `(table)`, and the frequency's one line follows. The JSON row
  gains `dispersion` and `evaluatedAtHz` (omitted for a constant material). The ordinary `explain` of a 3D setup marks
  them the same way.
- **The material editor** shows a *Frequency dependence* card for a dispersive record: εr and σ **at f** (a field, 1 GHz
  until a host sets `MaterialsTableViewModel.ReadoutFrequencyHz`; SAR-6 sets it to the active setup's SAR frequency),
  and a small εr/σ-against-log-frequency plot, 10 MHz–10 GHz for a model and the table's own span for a table, with f
  marked. The model is edited in the file. The card reads it.
- `reference shipped-materials` lists the records with the role `dielectric (frequency-dependent)`.

### 4.5 Gates

`tests/Ui.Tests/Em3d/TissueMaterialTests.cs`:

- `ColeColeTests`: muscle, fat and cortical bone at 64, 128, 298, 900 and 1800 MHz, through the **shipped** records, to
  four significant figures. **The reference values are the Gabriel model evaluated by an independent implementation**
  (a separate script, run once), not values printed in the paper: Part III prints its parameters and its figures,
  not a table at these frequencies. The familiar published pairs (muscle 55.03 / 0.943 S/m and fat 5.46 / 0.051 S/m at
  900 MHz, muscle 53.55 / 1.34 S/m at 1.8 GHz) agree with the rows.
- `TissueLibraryTests`: the library ships, every record has a `Source`, and `check` finds nothing in it; a plain number
  5 % off its model is the warning; the library round-trips; with no frequency a record is its plain numbers, exactly as
  the record with its model stripped; a 3D run evaluates muscle at 298 MHz for a 100–496 MHz sweep, with tanδ 0, no Role
  and the one note; and the head liquid asked at 100 MHz refuses the run.

### 4.6 Not done in SAR-2

- **R-sar2-6, the importer for a user's own downloaded tissue table** (optional in the brief). The CSV reader is
  simple. What it needs decided is its surface: a CLI noun (`import tissues <csv> --into <file.cmat>`) plus a second
  mode on the MCP `import` tool (R-aut-13), which turns that tool from single-mode into one with a selector. Left for
  the owner.
- Debye or Lorentz fits for broadband FDTD tissue (overview §8); Palace, which will read the same evaluated values.
