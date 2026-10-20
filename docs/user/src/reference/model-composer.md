---
title: Model Composer
slug: reference/model-composer.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Model Composer
lede: A nonlinear FET model, or a passive one, built from measured or simulated data — in a document you can close and pick up again.
keywords: model extraction, device modeling, FET model, GaN HEMT, extrinsic network, de-embedding, pad de-embedding, cold-FET, multi-bias, bias plane, intrinsic, passive model, topology, .cmc, .cmodel
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What it does</a></li>
<li><a href="#steps">The steps</a></li>
<li><a href="#kinds">Data kinds and their conditions</a></li>
<li><a href="#preparing">Preparing the data</a></li>
<li><a href="#planes">Reference planes</a></li>
<li><a href="#six-pin">The six-pin network</a></li>
<li><a href="#defaults">Methods and their defaults</a></li>
<li><a href="#conservation">Charge conservation</a></li>
<li><a href="#temperature">Temperature</a></li>
<li><a href="#trap-data">Traps, and what pulsed data allows</a></li>
<li><a href="#scaling">Periphery scaling</a></li>
<li><a href="#passive-models">Passive models</a></li>
<li><a href="#example">The example</a></li>
<li><a href="#open">Opening a document</a></li>
<li><a href="#window">The window</a></li>
<li><a href="#tasks">Running, progress and Cancel</a></li>
<li><a href="#data">The Data step</a></li>
<li><a href="#pads">The Pads step</a></li>
<li><a href="#network">The Network step</a></li>
<li><a href="#method">The method picker</a></li>
<li><a href="#current">The Current step</a></li>
<li><a href="#charge">The Charge step</a></li>
<li><a href="#thermal">The Thermal step</a></li>
<li><a href="#traps">The Traps step</a></li>
<li><a href="#check">The Check step</a></li>
<li><a href="#validate">The Validate step</a></li>
<li><a href="#report">The Model Validation Report</a></li>
<li><a href="#export">The Export step</a></li>
<li><a href="#plots">Plots</a></li>
<li><a href="#passive">Passive documents: Data and Topology</a></li>
<li><a href="#undo">Undo</a></li>
<li><a href="#headless">Headless</a></li>
</ol>
</nav>

## What it does {#what}

Model Composer builds a model from data. For a transistor it builds a **nonlinear FET model**, a `.cmodel` that a
schematic places as a [`ComposedFet`](components.html#composedfet) and that DC, S-parameters, harmonic balance and
loadpull all use. For an N-port it builds a **passive model**, an ordinary cell of R, L, C and mutuals with the fitted
values as its parameters.

The data can be measured (DC and pulsed I–V, multi-bias S-parameters, pulsed S-parameters, large-signal sweeps and
loadpull) or simulated (tables of currents, admittances and intrinsic voltages written by another simulator). The work
is kept in a `.cmc` document. It names the data where it lies, records the conditions it was measured under, and holds
every setting and every result, so a model can be rebuilt, checked and changed later.

A transistor model has four parts, called **constituents**: the drain current `Id`, the gate current `Ig` (a gate–source
and a gate–drain branch), and the gate and drain charges `Qg` and `Qd`. Each one is an ANN, a table or an equation. They
sit inside an extrinsic network of pads, inductances and resistances. A model can add self-heating, trap states, or a
simpler DC/RF dispersion.

## The steps {#steps}

An active document runs through the steps below, in order. Each step's result is stored in the `.cmc` and stamped with
the data and network it came from. When either changes, the step is marked stale.

| Step | What it does |
|---|---|
| Data | Imports each data set and states its conditions. The data stays in its files and is cached beside the document. |
| Pads | Removes the probe pads: open and short dummies, a Touchstone two-port per side, or a two-pin schematic per side. |
| Network | Chooses the extrinsic network and fits its values. Every data set is then re-referenced to the intrinsic plane. |
| Current | Fits `Id` and `Ig`. |
| Charge | Fits `Qg` and `Qd`, or, with no S data, writes them from typed capacitances. |
| Thermal | Adds self-heating: Rth (typed, or extracted from pulsed I–V) and the thermal time constants. |
| Traps | Chooses a trap model and its coupling: fits the currents again with the trap states as inputs, or fits the strengths of a threshold shift from multi-bias S. |
| Check | Checks that the charges conserve charge. |
| Validate | Compares the model with every data set at the plane it was measured at. |
| Export | Writes the `.cmodel`. |

**Fit all** (`model fit --step all`) runs every step the document needs, in this order: the network if any value is
free, then thermal, current, traps when a trap model is chosen, and charge. With no S data, the network is held at its
values (`network: held at its values — no S data to fit to`) and the charge step writes the typed capacitances; a fit
of the network step alone still refuses.

## Data kinds and their conditions {#kinds}

| Kind | Holds | Read from |
|---|---|---|
| `DcIv` | Terminal voltages and currents at DC | CSV, `.npy`, `.mat` |
| `PulsedIv` | The same, sampled during a pulse from a stated quiescent point | CSV, `.npy`, `.mat` |
| `SParams` | Two-port S at each bias, and the bias currents when the files state them | A folder of Touchstone files |
| `PulsedSParams` | S sampled during a pulse | A folder of Touchstone files |
| `SimulatedTables` | Currents, and an admittance or S table, a capacitance table, or both; optionally intrinsic voltages | `.npy`, `.mat` |
| `LargeSignal` | A power sweep, a two-tone sweep or a loadpull file; used for validation only | CSV, `.spl`, `.lpcwave` |
| `PassiveSParams` | An N-port block, or a family of blocks over a geometry value | Touchstone files |

A Touchstone set takes its bias from each file's name or comment lines through a pattern such as
`Vgs{vgs}_Vds{vds}_Id{id:mA}`. The fields are `{vgs}`, `{vds}`, `{T}`, `{id}`, `{ig}` and, for a passive family,
`{geometry}`.

Each data set states the conditions it was measured under. Every condition is optional when the set is imported, and the
step that needs one names it when it is missing.

| Condition | Meaning |
|---|---|
| `AmbientC` | The chuck temperature, °C |
| `ReferencePlane` | Where the bias voltages were measured: `instrument`, `probe`, `device` or `intrinsic` ([below](#planes)) |
| `BiasPathOhms` | For `instrument`: the bias-path resistance at the gate, drain and source |
| `Quiescent` | A pulsed set's quiescent bias (`Vgs`, `Vds`, and `Id` when it conducts) |
| `PrePulse` | A double pulse's trap-setting pulse: its `Vgs`, `Vds`, width and gap before the measuring pulse |
| `PulseWidth`, `Period`, `SampleDelay` | The pulse timing, seconds; the sample delay is from the pulse's start to the sample |
| `TrapState` | How pulsed points beyond the trap-setting bias are used: `auto`, `setting` or `extreme` ([below](#trap-data)) |
| `SoakTime`, `Compliance` | How long the bias was held, and the instrument's current limits |
| `LargeSignal` | For a large-signal set: what it measured, the frequency, the bias and the terminations |
| `Note` | Free text, kept with the set |

## Preparing the data {#preparing}

The data can come from a lab bench or from another simulator. What matters is that the right data arrives; the file
layout is secondary.

### What a model needs {#needs}

| Data | Needed | What it gives the model |
|---|---|---|
| I–V: DC, or a simulator's DC currents; 4 points or more, varying both Vgs and Vds | **Required** | `Id` and `Ig`. With no gate current in the data, `Ig` is zero |
| S, Y or a capacitance table | Optional | `Qg` and `Qd`. None: the charges are constant capacitances you type, or zero. One bias or more: fitted linear charges — constant capacitances, exact at a single bias — with the `Linear` equation; four biases or more allow ANN or Table charges. Across the I–V plane: nonlinear charges |
| S at Vds = 0 V, pinched off and with the gate forward-biased | Optional | The cold-FET network fit |
| Open and short pad dummies | When measured at the probe tips | Removal of the pads |
| Any set at two or more temperatures | Optional | `T` as an input: the model follows the run's `temp`, and self-heating becomes available |
| Pulsed I–V at two or more chuck temperatures, from a quiescent point that dissipates nothing, plus DC I–V at the same biases | Optional | An extracted Rth. Without this data, type Rth |
| S or Y down to the thermal roll-off of `Re Y22` (typically kHz to MHz) | Optional | A fitted τth |
| Pulsed I–V from two or more quiescent drain voltages | Optional | Drain lag |
| Pulsed I–V from two or more quiescent gate voltages | Optional | Gate lag |
| A double pulse | Optional | The clearest trap data |
| Pulsed I–V from one quiescent point | Optional | One trap level, with a warning |
| Pulsed S | Optional | A trap-dependent `Qd` |
| No pulsed data; DC I–V and multi-bias S | Optional | Threshold-shift traps: time constants typed, strengths typed or fitted from the multi-bias S |
| Power sweeps, two-tone sweeps, loadpull | Optional | Validation only |

Trained trap states need pulsed I–V. Without it a model can still have traps: the states shift the threshold of the
trap-free current, with strengths fitted from the difference between the DC and RF conductances in the multi-bias S, or
typed. **DC/RF dispersion** is the simpler alternative, with one linear time constant and no fast capture.

### Bias ranges and steps {#ranges}

| | Measured | Simulated |
|---|---|---|
| Vgs | From about 1 V below pinch-off to forward gate conduction | Wider costs nothing: from 2 V below pinch-off to well into forward conduction |
| Vds | From 0 V to the peak drain swing (about 2.5 × the supply for class AB, more for switching classes), staying inside the safe operating area | The same range, within the source model's valid range |
| High-power corner | Pulsed I–V, where DC would overheat the device | DC, with self-heating off |
| I–V step | 0.1 V in Vgs (0.05 V near threshold); 0.25–0.5 V in Vds below the knee, 1–2 V above it | A uniform 0.05–0.1 V grid |
| S biases | 100 or more: every 0.25–0.5 V in Vgs and 1–2 V in Vds, plus the two cold points | The same bias grid as the I–V |
| Frequencies | 20–50, from 0.1–0.5 GHz to at least three times the operating frequency | One low frequency (1 GHz, say) for an intrinsic table; a span when the data includes parasitics |

The model is continued linearly beyond its data, so cover the whole swing the circuit will make. The charges are
read between a tenth and a half of the top frequency. The default ANNs want a few hundred points or more, and a fit
reports when its data is thin for its network.

### From another simulator {#from-simulator}

| Data | Analysis to run | What to save |
|---|---|---|
| I–V | DC, with Vgs swept inside a Vds sweep | The terminal voltages and the gate and drain currents; optionally the internal intrinsic voltages (`vgsi`, `vdsi`), which the composer checks against its own |
| S or Y | S-parameter (small-signal) analysis at every bias of the DC sweep | Y or S of the two-port: gate port 1, drain port 2, source grounded; the DC currents at each bias. Or, for the charges alone, the core's operating-point capacitances at each bias |
| Temperature | Both of the above at two or more `temp` values | One set per temperature, or a `T` array |
| Pulsed I–V | Transient: hold the quiescent bias until the trap and thermal states settle, step to the pulse bias, and sample the currents at the sample delay | One set per quiescent point, stating `Quiescent`, `PulseWidth` and `SampleDelay` |
| Large signal | Harmonic balance: power sweep, two-tone or loadpull | Validation only |

In the source simulator:

- **Turn self-heating off** (Rth = 0) for the DC and S runs. Keep the temperature dependence: the `temp` runs need
  it. To have self-heating in the model, type the source model's Rth and Cth on the Thermal step.
- **Turn traps and dispersion off**, unless they are to be modelled from pulsed transients or as DC/RF dispersion. A
  DC sweep with traps on and an S analysis with the traps frozen disagree.
- **Choose the plane.** Export the core alone, or its internal node voltages, as `ReferencePlane=intrinsic`, with no
  network. Or export at the terminals as `device`, with a six-pin network holding the source model's parasitic values.
- **Keep to the model's valid range.** A model that clamps its inputs outside that range gives flat data there.

### File formats {#import-formats}

**Measured data** is read as the instruments write it:

- **S-parameters**: Touchstone files from the network analyser (RI, MA or DB), one per bias, with the bias in the file
  name or in the comment lines. A `--pattern` reads it.
- **I–V**: CSV or TSV from the source-measure unit or the pulsed I–V system. Columns are mapped by their header names;
  any common delimiter works, lines starting `#`, `!` or `%` are skipped, and units are given per column or per cell
  (`12 mA`).

**Simulated data** is read most easily as a MATLAB `.mat` file (v5, v7 or v7.3). It can be written by MATLAB or by
Python (`scipy.io.savemat`, h5py). Name the arrays as you like and map them: `vgs` and `vds` (one value per point, or
the two axes of a grid), `id`, `ig`, `T`, `vgsi`, `vdsi`, `freq`, and `Y` or `S` shaped `[point, freq, 2, 2]`
(`[vgs, vds, freq, 2, 2]` on a grid). Currents are positive into the terminal.

Capacitances are either the matrix `C` shaped `[point, 2, 2]`, with `Cij = ∂Qi/∂vj` (gate 1, drain 2, so `C12 = −Cgd`),
or the circuit set `cgs`, `cgd`, `cds` and optionally `cm`, one value per point. A capacitance table is the core's own,
so it is read at `ReferencePlane=intrinsic` only. On its own it feeds the charges at every point, with nothing to fit
the network, `Gm`, `Gds`, dispersion or τth to. Beside Y or S, Y feeds the charges and the table is checked against it:
`max |ΔC| 0.02 % between its capacitance table and Im Y/ω`.

```python
import scipy.io as sio
# vgs (nG,), vds (nD,) in V; id, ig (nG, nD) in A; freq (nF,) in Hz; Y (nG, nD, nF, 2, 2) in S
sio.savemat("tables.mat", {"vgs": vgs, "vds": vds, "id": id, "ig": ig, "freq": freq, "Y": Y})
```

```
circuitrf model import fet.cmc tables.mat --as SimulatedTables --map vgs=vgs --map vds=vds --map id=id --map ig=ig --map freq=freq --map Y=Y --condition ReferencePlane=intrinsic --condition AmbientC=25
```

A `.npy` here means circuitRF's own structured `.npy`, as its exports write it. A single array saved with
`numpy.save` is not read; save a `.mat` from Python instead. Touchstone files and a CSV, which most simulators can
write, work too.

If your tool writes the right data in a layout none of these read, send a sample: the importer is extended to read
what tools actually write.

### Checklist {#checklist}

- The I–V covers the circuit's whole Vgs and Vds swing, gate forward conduction included.
- S, Y or capacitances, if there are any, are at one bias or more for fitted linear charges, 4 or more for ANN or Table charges, and a
  hundred or more for nonlinear ones. Without it, the capacitances are typed (a datasheet's will do) or left at zero.
- Currents are positive into the terminal, and every column has its unit.
- Every set states its reference plane and ambient temperature.
- Measured: the compliance limits are stated, so points at the limit are flagged.
- Simulated: self-heating is off, and traps are off unless they are modelled.
- Pulsed sets state their quiescent point (and pre-pulse), pulse width and sample delay.
- N0 and W0 are those of the device the data came from.

## Reference planes {#planes}

The composer always models in **intrinsic** voltages, between the core's own gate, drain and source. Each data set states
the plane its voltages were measured at:

| Plane | Means |
|---|---|
| `instrument` | At the source-measure unit; the bias-path resistances are subtracted |
| `probe` | At the probe tips; the pads are removed, then the network |
| `device` | Pads already removed; the network is removed |
| `intrinsic` | Already intrinsic, as a simulator's tables can be |

The intrinsic voltages come from a DC solve of the extrinsic network carrying the measured currents. Intrinsic points
therefore do not lie on a grid, even when the measured ones do. ANNs train on them directly, and tables re-grid them.

A simulated table that also carries intrinsic voltages gets the computed ones checked against them. `model deembed`
reports the largest difference, for example `max |ΔV| 3.1e-10 V between its intrinsic voltages and the computed ones`.

## The six-pin network {#six-pin}

The extrinsic network is an ordinary schematic with **six numbered pins**: 1, 2 and 3 are the extrinsic gate, drain and
source, and 4, 5 and 6 are the intrinsic gate, drain and source. Pin 3 is the ground the data is referenced to. Each
pin is numbered once. A network that breaks this rule is refused, and nothing is written.

The network's values are its variables. Each one can be locked, bounded, tied to another (`rd × 1.2`) and fitted. Three
stock networks are provided: **GaN two-shell** (outer pad capacitances, inductances with a gate–drain mutual, inner
capacitances, then the series resistances), **classic single shell**, and **series only**. The network is fitted
cold-FET (a pinched-off and a forward-gate point), multi-bias (the intrinsic capacitances made flat in frequency at
every chosen bias), or both. After a fit, each value has a standard error, and a value the data barely determines is
marked with the partner it is correlated with.

## Methods and their defaults {#defaults}

| Setting | Default |
|---|---|
| Method, every constituent | ANN |
| ANN | Two hidden layers of 8, `tanh`; 6000 iterations for a current, 3000 for a charge; 3 restarts; 10 % held out |
| Drain-current weighting | Relative, with a floor at 10 % of the largest current (`IdFloor` 0.1) |
| Gate current | Fitted as `asinh(Ig/I0)` with `I0` at 1e-4 of the largest gate current (`IgFloor`); an equation `Ig` is fitted directly |
| Table | Grid step 0.1 V; a charge table integrates along the mean of paths A and B |
| Charges | An ANN trained on the capacitances alone (the adjoint method), anchored to zero charge at a stated bias |
| DC/RF dispersion | Off; τ 1 µs when on |
| Self-heating | Off |
| Traps | None |
| Network fit | Multi-bias |

A model never extrapolates freely. Outside its training domain each constituent is continued linearly from the domain's
edge. A current also gains a conductance on its own branch, which grows with distance from the edge. Keep the data wider
than the voltages the circuit will reach: a gate swinging past the last measured `Vgs` meets that conductance, and its
current is not the device's.

## Charge conservation {#conservation}

The charges are charge functions, so a model is charge-conserving by construction. Its capacitances are derivatives
of one charge each, and no DC is ever made by displacement current. **Check Charge Conservation** checks three things:

| Check | Reads | A good result |
|---|---|---|
| (a) The data | The curl of the measured capacitance matrix, row by row, and the difference between the two line-integral paths | A few percent: the data's own noise and the network's error |
| (b) The model | The same curl on the fitted model's capacitances | Near zero |
| (c) Large signal | A harmonic-balance drive at *f* and at 2*f*; the change in DC gate current is DC made by displacement current | Below 0.1 % of the fundamental gate current |

Check (a) needs S at 4 biases or more across the bias plane; with fewer it reports *not run* and the number of biases,
which is not a failure. With typed charges it reports *not run*, as there is no S data. A linear or typed charge's (b)
reads zero.

A non-reciprocal capacitance matrix (a transcapacitance, `C21 ≠ C12`) is reported for information. It is physical, and
it is not an error. Checks (b) and (c) also run on any nonlinear device in a netlist:
`model check-charge design.cnl --device X1 --bias vgs,vds`.

## Temperature {#temperature}

**Data at two or more temperatures** makes `T` an input of every constituent. The model then follows the run's ambient
`temp`, clamped to the data's range with one warning, and self-heating is available.

**Data at one temperature** makes a model valid at that temperature, `T0`, with no temperature dependence. Self-heating
is unavailable. A run at another `temp` prints one warning naming both temperatures and uses the model as it is.

Self-heating needs an Rth. Type it, or extract it from pulsed I–V at two or more chuck temperatures, pulsed from a
quiescent point that dissipates nothing, together with DC I–V at the same biases. Extraction trains an isothermal current
on the pulses, finds the junction temperature at which it gives each DC point's current, and fits the rise against the
dissipated power. The thermal dynamics are a typed Cth, a typed τth, a Foster ladder, or τth fitted to the low-frequency
roll-off of `Re Y22`. That fit is not offered while traps are modelled, because the roll-off holds trap time constants
too.

When drain lag is modelled, type Rth (from a thermal simulation, for example) rather than extract it. A pulse from a
0 V drain leaves the trap state short of its DC value, and extraction reads that shortfall as heating.

## Traps, and what pulsed data allows {#trap-data}

Traps are a setting. **None** is the default. **CaptureEmission** has a gate-lag state and a drain-lag state, each
switched on separately. A state is captured quickly when its voltage moves one way and emits slowly when it moves back.
The defaults are 10 ns capture at a 1 V gap and 1 ms emission, and emission can be made thermally activated.
**Custom** lets you write each state's dynamics and DC value yourself.

The **coupling** says how the states reach the drain current:

| Coupling | The current | Needs |
|---|---|---|
| **Trained** (the default with pulsed I–V) | `Id` takes the enabled states as inputs and is fitted again with them | Pulsed I–V |
| **Shift** | `Id` is the trap-free current fitted to the DC I–V, evaluated at a shifted gate voltage: `Id(vgs − ΔV, vds)`, with `ΔV = ShiftDrain·(φ2 − vds) + ShiftGate·(vgs − φ1)` | Nothing beyond DC I–V; multi-bias S to fit the strengths |

Under **Shift** a drain-lag state held above `vds`, or a gate-lag state held below `vgs`, lowers the current. At any DC
bias every state equals its DC value, so the shift is zero and the model's DC is exactly the trap-free current. At RF
the states are frozen, which gives `gm_RF = (1 − ShiftGate)·gm` and `gds_RF = gds + ShiftDrain·gm`, where `gm` and
`gds` are the trap-free current's. The step fits each strength from these relations over the active S biases. A
strength can be typed and locked, and is then not fitted. The time constants are typed in both couplings. Shift is
defined for CaptureEmission; the charges take no trap state under it. With self-heating modelled, `gm` and `gds` are
read at each point's junction temperature, so heating is not counted as traps. Without self-heating, `ShiftDrain`
includes any thermal part of the DC/RF difference.

A trained trap state is fitted from the **trap-setting levels** in the pulsed data. The level of a set is its pre-pulse
bias for a double pulse, and its quiescent bias otherwise.

| Pulsed data | What it allows |
|---|---|
| Double pulse | The pre-pulse sets the state, and the measuring pulse reads it before emission moves it. This is the clearest data. |
| Ordinary pulses from two or more quiescent points | Two or more levels: the state is fitted fully. |
| One quiescent point | One level: the state is fitted with one warning, and the model records the level it came from. |
| No pulsed data | Threshold-shift traps, time constants typed, strengths typed or fitted from multi-bias S. |

A pulsed point **toward** its setting (pulsed no higher in `Vds` for drain lag, no lower in `Vgs` for gate lag) sits at
the setting's state exactly, because only slow emission acted. A point **beyond** it was partly captured during the
pulse. The set's `TrapState` decides what happens to it: `auto` keeps it for validation only, `setting` takes the
setting's state, and `extreme` takes the pulse's own voltage as the state.

DC/RF dispersion is the simpler alternative to traps. It fits a second drain current, `IdRF`, to the RF transconductance
and output conductance, and the model follows `Id` at DC and `IdRF` above the corner set by τ. Dispersion and traps are
not combined, in either coupling.

## Periphery scaling {#scaling}

A model records the measured device's finger count `N0` and unit gate width `W0`. Export needs both. An instance's `N`
scales the model:

| What | Scales by |
|---|---|
| The core (`Id`, `Ig`, `Qg`, `Qd`) | `N/N0` |
| A network value, by its rule | `fixed`, `perFinger` (`N/N0`) or `perFingerInverse` (`N0/N`) |
| `Rth`, `Cth` | `N0/N`, `N/N0` |
| Trap time constants | Not scaled |

For the GaN two-shell network the series resistances are `perFingerInverse`, the inner capacitances `perFinger`, and the
pad capacitances, inductances and mutual `fixed`. Every rule can be changed per value. `m` places whole devices in
parallel, so `m=2 N=8` is two 8-finger devices.

## Passive models {#passive-models}

A passive document holds one N-port block, or a **family** of blocks over a geometry value such as the turns of a
spiral. **Analyse** reports the block's reciprocity, passivity, symmetry, low-frequency asymptotes and self-resonances, and fits a rational model. **Propose** ranks a library of
twelve topologies (series and parallel RLC, an inductor with skin effect, capacitor and resistor parasitics, π, T, bond
wire, via, spiral, MIM and a line with pads) against the data, or fits a topology you draw. **Fit** gives each value
with its error.

A family is fitted as one law per value, a polynomial or a rational function of the geometry. The middle geometry is
held out and predicted, and the result reports its error. Export writes a cell whose parameters are the geometry and the
fitted values. A family cell takes the geometry as its parameter, and warns once when it is placed outside the fitted
range.

## The example {#example}

**File ▸ New ▸ Examples ▸ Model Composer** opens a workspace with three documents built this way:

- `synthetic-gan`: a GaN FET built from synthetic DC, pulsed and multi-bias S data, with a fitted network, ANN
  currents, adjoint-ANN charges, self-heating and drain lag. Its 8-finger model drives a class-AB power sweep and a
  loadpull.
- `passive-spiral`: a family of spiral inductors over turns, fitted and placed in a filter at a turn count between two
  measured ones.
- `simulated-tables`: a model built from a simulator's tables, showing the intrinsic-voltage cross-check.

Its README lists every setting it uses, and the value a real job would use where it differs.

## Opening a document {#open}

**Tools ▸ Model Composer…** opens a window on a new document and asks one question: **Active** (a three-terminal FET)
or **Passive** (an N-port). Nothing is written until the first save, which asks where the `.cmc` goes; importing data
or choosing a network saves first, because the document's data and network files are found relative to it.

A `.cmc` in a workspace opens on double-click in the project tree, and the window's **Open** button opens one from
anywhere. Each document has one window; opening a document that is already open brings its window forward.

Closing a window with unsaved work asks whether to save it. A document is reopened where it was left: the same panels,
the same step, and the same bias cursor.

## The window {#window}

| Part | What it holds |
|---|---|
| Toolbar | Open, Save, Save As · Undo, Redo · the four panel lamps · the bias cursor (Vgs, Vds, T, Q) · Check Charge Conservation, Model Validation Report · Run ▾, Cancel · Help |
| **Steps** (left) | The step rail, and under it the selected step's settings |
| **Plots** (centre) | A tab strip and a Data Display plot |
| **Network** (right, top) | The extrinsic network, drawn by the schematic canvas |
| **Values** (right, bottom) | The network's values: value, lock, min, max, tie, scaling rule, ± error |
| Status strip | One line — a result, a warning or a refusal — and, while something runs, its stage, a progress bar and Cancel |

**Panel lamps.** Each panel has a lamp on the toolbar; a lit lamp means the panel is on screen. Hiding a panel gives its
space to the others. The last lit lamp cannot be turned off. Which panels are hidden is saved in the document. The
splitters between panels drag in both directions.

**The step rail.** Active documents have Data · Pads · Network · Current · Charge · Thermal · Traps · Check · Validate ·
Export; passive ones have Data · Topology · Fit · Export. A step that cannot run yet is dimmed and its tooltip says what
it needs. A dot beside a step marks a result computed before the data or the network last changed.

**The bias cursor.** Vgs, Vds, the ambient temperature and the quiescent point. Each list holds only measured values, so
the cursor always sits on a measured bias; with a list focused, the arrow keys step it. Every frequency plot is drawn
at the measured point nearest the cursor.

## Running, progress and Cancel {#tasks}

**Run** runs the selected step's main task; the ▾ beside it lists the step's others. Every run happens off the user
interface, which stays responsive: the status strip shows the stage and one progress bar, and **Cancel** stops it.

- One task runs at a time. Starting another while one runs is refused in the status strip.
- A cancelled task changes nothing: the previous result stays exactly as it was.
- A run that changes results (an import, a fit) saves the document first and reads the result back; an edit made
  while it ran is kept on top of the result.

## The Data step {#data}

**Import** (the database button, or Run) opens the importer:

| Field | Meaning |
|---|---|
| Kind | DC I–V, pulsed I–V, S-parameters, pulsed S-parameters, simulated tables, large signal |
| Source | Files, or a folder — a Touchstone set is a folder of files |
| Pattern | Touchstone: how each file name (or its comments) states its bias, e.g. `Vgs{vgs}_Vds{vds}_Id{id:mA}` |
| Map | CSV, `.npy`, `.mat`: what each column or array holds, `name=quantity[:unit]` |
| Conditions | Ambient °C, the reference plane, the quiescent point, and any other condition as `Key=Value` |

The right side previews the first lines of the first file and the first points as they will be read; the line at the
bottom is what the import will read, or why it cannot.

The **data-set list** shows each set's name, kind, ambient, reference plane, quiescent point (and pre-pulse) and point
count, and where its charges read their capacitances: `Y`, its capacitance `table` or its `circuit set`.
`circuitrf model explain` says the same. A capacitance table spelled as the matrix whose `C12` is positive at most of its
points is warned about, since the matrix convention has `C12 = −Cgd`: the circuit set was probably meant. Selecting a set
points the plots and the cursor's lists at it.

| Plot tab | Shows |
|---|---|
| Output | Id against Vds, one curve per Vgs; excluded points as × |
| Transfer | Id against Vgs, one curve per Vds |
| Bias plane | Every bias point: extrinsic ○, intrinsic ●, excluded ×, hygiene flags ▲, the cursor ✶ |
| S | The four S-parameters in dB at the cursor's bias |
| Smith | S11 and S22 at the cursor's bias |

**Excluding points.** On the bias plane, light the × lamp, choose a reason, then click a point or drag a lasso around
several. Points already excluded are included again. Each click or lasso is one undo step.

**Base periphery.** N0 (fingers) and W0 (unit gate width, with its unit, e.g. `125 um`) of the measured device. Export
needs them; until they are set the Data step shows the one warning that says so.

## The Pads step {#pads}

The pad mode — None, OpenShort, TwoPorts or Network — and its files. The S and Smith tabs show S at the cursor's bias
before (dashed) and after (solid) the pads come off.

## The Network step {#network}

**The network.** The book button picks a stock network (GaN two-shell, classic single shell, series only) or none; the
folder button opens a six-pin `.csch`; the pencil opens the network in the schematic editor. **Copy** puts the network
on the clipboard as a schematic selection that pastes into any schematic; **Paste** takes a selection from a schematic
as the network. A network whose pins are not numbered 1 to 6, each once, is refused in one line, and nothing is written.

**The values table.** Every value is editable: type a value with or without its unit (`39.5 pH`, `39.5p`), tick
the lock to hold it, set a minimum and a maximum, tie it to another value (`rd` or `rd × 1.2`), and choose its scaling
rule. After a fit, ± is each value's standard error, and ⚠ marks a poorly determined value — its tooltip names the
correlated partner or the bound it sits at.

**The fit.** Method (ColdFet, MultiBias, ColdFetThenMultiBias, or Fixed, which holds every value), seed and the three
weights. The multi-bias fit's bias points are marked ■ on the bias plane; with the × lamp lit on the Network step a
click or lasso adds points to that set or takes them out.

| Plot tab | Shows |
|---|---|
| C | Intrinsic Cgs, Cgd and Cds against frequency at the cursor's bias, in fF |
| G | Intrinsic Gm and Gds against frequency, in mS |
| Flatness | The flatness of Cgs, Cgd and Cds across bias, one curve per Vds, in % |
| Bias plane | As on the Data step, with the fit's points |

A right network leaves C and G flat in frequency.

## The method picker {#method}

The Current and Charge steps pick a method for each constituent with the same control:

| Method | Settings |
|---|---|
| ANN | Hidden layers (1 to 4, neurons each, default `8, 8`), activation, iterations. A charge needs S at 4 biases or more |
| Table | Grid step and smoothing; a charge table also takes its line-integral path — A, B or their mean. A charge needs S at 4 biases or more |
| Equation | A template, or your own expression in `vgs`, `vds` and named parameters; after a fit, each parameter's value. A charge needs at least as many capacitances as free parameters, two per bias, so `Linear` fits from one |

With fewer S biases than a method needs, its entry in the picker gives the reason when you hover over it, and a fit
with it is refused rather than run with another method.

Restarts, the seed and the held-out percentage belong to the step. After a fit the status strip reads, for example,
`Id: ANN 2×8 · held-out 0.8 % · domain 412 pts`.

## The Current step {#current}

Pickers for `Id` and `Ig` — an equation `Ig` is two expressions, `IgGs` and `IgGd` — and **DC/RF dispersion**, the
simpler alternative to self-heating and traps.

| Plot tab | Shows |
|---|---|
| Output, Transfer | Measured Id (symbols) and the model (lines), one member per measured Vgs or Vds, at the cursor's temperature and quiescent point |
| Error | Model minus data over the bias plane, in % of the current's range |
| Gm, Gds | The data's own derivative (symbols) against the model, at the cursor's Vds or Vgs; with dispersion, RF Gm and Gds from the S data as well |
| Domain | The training points and the outline of the domain beyond which the model extrapolates |

## The Charge step {#charge}

Pickers for `Qg` and `Qd` (an adjoint-trained ANN by default, a table integrated along a path, or an equation), and
**Drift charge in Qg**. The charge templates are `Depletion`, `Angelov` and `Linear`.

`Linear` is `Qg = C11·vgs + C12·vds` and `Qd = C21·vgs + C22·vds`: constant capacitances, exact at a single measured
bias. From two or three biases it is the least-squares constant matrix, and the report gives each value's spread across
the biases, with the circuit set `Cgs = C11 + C12`, `Cgd = −C12`, `Cds = C22 + C12` and `Cm = C21 − C12`. Its C–V
plots are flat lines through the measured points. The exported model's provenance and `circuitrf model explain` say
`Qg, Qd: Linear from 1 bias point(s)`. A linear charge has no training domain, so no extrapolation applies to it.

**With no S, Y or capacitance data**, the step shows four capacitances in place of the pickers — `Cgs`, `Cgd`, `Cds` and `Cm`, each in
farads and 0 unless you type a value — and Run writes them as `Linear` charges. A datasheet gives them as
`Cgd = Crss`, `Cgs = Ciss − Crss` and `Cds = Coss − Crss`; `Cm` is a transcapacitance and may be negative. Export,
the provenance and `circuitrf model explain` say `Qg, Qd: typed` with the four values. With all four at zero the step,
the export and `model explain` say `the charges are zero: S-parameters and harmonic balance see the currents alone.`
— a model meant only for DC. Validation then has DC and large-signal items and no S items. Typed values beside S data
are refused, since typing would discard the data; **Fit to the S data** clears them.

| Plot tab | Shows |
|---|---|
| C–V (vgs), C–V (vds) | The model's capacitances along one voltage with the other at the cursor's value, measured points as symbols. The selector beside the tabs picks one `Cij` or the circuit set Cgs, Cgd, Cds, Cm |
| Qg, Qd | The model's charge over (vds, vgs) |
| ΔQg, ΔQd | The model's charge against the line integral of the measured capacitances, in % of the charge's range |

## The Thermal step {#thermal}

Self-heating, `Rth` (blank extracts it), `Cth` or a ladder of `fraction:τ` pairs; **Extract** (Run) runs the extraction
and the line under the settings gives its Rth and residual. A document whose data is at one temperature shows its `T0`
and the words *one temperature* instead: the model it builds has no temperature dependence.

| Plot tab | Shows |
|---|---|
| Tj | Each DC point's junction-temperature rise against its dissipated power, and the model's Rth line |
| DC I–V | Measured Id, the model with self-heating (solid) and the isothermal model (dashed) |

## The Traps step {#traps}

The trap model (None, CaptureEmission, Custom), its coupling, a toggle per state, the time constants, and for Custom
each state's dynamics and DC expressions with the result of the fixed-point check. A state the data cannot support shows
why in place of its toggle; a state trained from one trap-setting level shows that level. Without pulsed I–V the
coupling list holds Shift only, and one line under it says why. Under Shift, each enabled state's strength has a lock,
its value and, after a fit, its ± standard error. Typing a value locks it. The step's report gives each strength and the
RMS residual of both relations.

| Plot tab | Shows |
|---|---|
| Pulsed I–V | Trained: per trap-setting level, measured Id against the model at the cursor's Vgs; points pulsed beyond the trap-setting bias are hollow — validation only |
| DC I–V | Trained: the DC data against the model, as validation |
| Gm | Shift: Gm against Vgs at the cursor's Vds — RF from S (symbols), the shifted model with its states frozen (lines), the trap-free current (dashed) |
| Gds | Shift: Gds against Vds at the cursor's Vgs, drawn the same way |

## The Check step {#check}

**Check Charge Conservation** — also on the toolbar — checks the data, the fitted model and a large-signal drive. The
result is one line, e.g. `Data gate 0.4 % · drain 3.8 % · Model 0 % · HB 0.2 µA`, and the transcapacitance as
information.

| Plot tab | Shows |
|---|---|
| Curl Qg, Curl Qd | Each charge row's curl over the bias plane, % |
| Paths Qg, Paths Qd | The two line-integral paths' difference, % of the charge's range |
| Spectrum | The gate current's spectrum with the drive at f and at 2f; a change in its DC is DC made by a non-conservative charge |

## The Validate step {#validate}

One row per data set with its tolerance and, after a run, its result line. **Run all**, or ▶ on one row. The
tolerances are edited below the rows. **Show testbench** writes each item's generated `.cnl` beside the `.cmc` and shows
it in the file manager.

| Plot tab | Shows |
|---|---|
| S, Smith | S at the measurement plane at the cursor's bias, measured (symbols) against model (lines) |
| I–V | The I–V family |
| Power, IM | Pout, gain, PAE and Id, or IM3 and IM5, against input power |
| Loadpull | Measured and modelled Pout contours |

## The Model Validation Report {#report}

**Model Validation Report…** (toolbar, or the Validate step) opens a dialog with every option of the report: the
sections, the S biases, the fixed voltages of the C–V families, the PA classes, the frequency, bias, R_opt and the
source's fundamental and 2f0 terminations, the load disc (radius, rings, spokes), the drive ladder and the compression.
Each opens on the value stored in the document or its default; **Apply** stores them.

**Generate** writes the report into `<name>-report/` beside the `.cmc`, and its Data Display document,
`<name>-report.cdd`, beside that folder: one tab per section — I–V families, the ΔQ maps, the C–V families with the fixed
voltage as the legend, S at the chosen biases, the Rth plot, and per PA class its loadpull contours, its performance
table and the region where the drive-up converged. **Export PDF…** writes every tab as one page each. `model report
--pdf` writes the same file.

## The Export step {#export}

`N0` and `W0`, the scaling rule of each network value, the symbol — only symbols with gate, drain and source pins (and a
fourth for the thermal node of a self-heating model) are offered — and the `.cmodel`'s path. **Export** writes it;
**Copy instance** puts an instance of it on the clipboard, ready to paste into a schematic.

A step whose result predates the data or the network blocks Export with one line naming it; **Run stale** re-runs the
stale steps in order.

A passive document exports as a cell: its name, the library or workspace folder it goes in, and the fitted values as
its parameters.

## Plots {#plots}

Every plot is a Data Display plot, with its own copy and inspector. Measured traces are symbols and model traces lines.
The button at the right of the tab strip, **Open in Data Display**, sends the plot's data to a new display.

## Passive documents: Data and Topology {#passive}

The Data step lists the block, or the family's blocks with their geometry values, and the analysis lines. The S and
Smith tabs show the block's S-parameters.

On the Topology step, **Propose** ranks the library's topologies against the block and lists the top three, each drawn
small with its |ΔS|; double-click one to choose it. A topology of your own is pasted into the network panel.

## Undo {#undo}

Every edit — a value, a lock, a bound, a tie, an exclusion, a step setting, a network choice — is one undo step
(⌘Z / ⇧⌘Z). A run is not: it is repeated by running again, and its result stays until the next run replaces it.

## Headless {#headless}

Every run of this window is a [`model`](cli.html#model) verb noun — `model import`, `model deembed`, `model fit`,
`model check-charge`, `model validate`, `model export`, `model report` — calling the same function, so a document built in
the window runs the same way from a script or an agent.
