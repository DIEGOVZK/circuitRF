---
title: Writing a .cmodel
slug: reference/cmodel.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Writing a .cmodel
lede: The composed FET model file, field by field, with complete scripts that write one — so a model from equations, a table or a neural network you trained elsewhere runs as a ComposedFet without the Model Composer.
keywords: .cmodel, composed FET model, ComposedFet, FET model file format, model.json, write a transistor model, MATLAB, Python, numpy, table model, ANN model, neural network model, B-spline, extrinsic network, charge conservation, agent
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What a .cmodel is</a></li>
<li><a href="#example">A complete model in one script</a></li>
<li><a href="#reading">How circuitRF reads it</a></li>
<li><a href="#container">The container</a></li>
<li><a href="#fields">model.json, field by field</a></li>
<li><a href="#core">The core: terminals, signs and units</a></li>
<li><a href="#equation">Equation constituents</a></li>
<li><a href="#network">The extrinsic network</a></li>
<li><a href="#npy">Arrays: the .npy entries</a></li>
<li><a href="#table">Table constituents</a></li>
<li><a href="#ann">ANN constituents</a></li>
<li><a href="#domain">Training domain and extrapolation</a></li>
<li><a href="#optional">Temperature, self-heating, traps and dispersion</a></li>
<li><a href="#checking">Checking and running it</a></li>
<li><a href="#agents">For an AI agent</a></li>
<li><a href="#pitfalls">Tips and pitfalls</a></li>
</ol>
</nav>

## What a .cmodel is {#what}

A `.cmodel` is a nonlinear FET model that a [`ComposedFet`](components.html#composedfet) places in a circuit. It holds
an extrinsic network of ordinary R, L, C and mutual elements, and a nonlinear core on the network's intrinsic gate,
drain and source. The core has four **constituents**: the drain current `Id`, the gate current `Ig`, and the gate and
drain charges `Qg` and `Qd`. Each constituent is one of three methods:

| Method | What the file holds | Needs arrays |
|---|---|---|
| `Equation` | An expression in `vgs`, `vds` (and `T`) with named constants | No |
| `Table` | Values on a grid and the coefficients of a cubic spline through them | Yes |
| `ANN` | The weights of a small feed-forward network | Yes |

The [Model Composer](model-composer.html) writes a `.cmodel` from measured or simulated data. Anything else can write
one too: a Python or MATLAB script, a fitting tool of your own, or an AI agent. The methods can be mixed, so a model
with a tabulated drain current and equation charges is ordinary. **An all-equation model is the shortest route**: it is
one JSON file inside a ZIP, with no arrays.

## A complete model in one script {#example}

This Python script writes `fet.cmodel`: a four-finger device with an equation for every constituent and a
series-and-pad network. It needs only the standard library.

```python
import json, zipfile

network = """define extrinsic (G D S gi di si)
  parameters Lg=50p H  Ld=50p H  Ls=5p H  Rg=4 Ohm  Rd=8 Ohm  Rs=6 Ohm  Cpg=20f F  Cpd=20f F
  C:CPG  G 0  C=Cpg
  C:CPD  D 0  C=Cpd
  L:LG   G g1  L=Lg
  R:RG   g1 gi R=Rg
  L:LD   D d1  L=Ld
  R:RD   d1 di R=Rd
  L:LS   S s1  L=Ls
  R:RS   s1 si R=Rs
end extrinsic
"""

model = {
  "FormatVersion": 1,
  "Kind": "Fet",
  "Terminals": ["G", "D", "S"],
  "BasePeriphery": {"Fingers": 4, "UnitWidth": "100 um"},
  "Temperature": {"Mode": "Single", "T0": 25},
  "Network": {
    "Netlist": network,
    "Scaling": {"Rg": "perFingerInverse", "Rd": "perFingerInverse", "Rs": "perFingerInverse"}
  },
  "Core": {
    "Constituents": {
      "Id": {
        "Method": "Equation",
        "Inputs": ["vgs", "vds"],
        "Expression": "Ipk*(1+tanh(P1*(vgs-Vpk)))*tanh(alpha*vds)*(1+lambda*vds)",
        "Parameters": {"Ipk": 0.08, "P1": 2.0, "Vpk": -1.2, "alpha": 1.5, "lambda": 0.02}
      },
      "Ig": {
        "Method": "Equation",
        "Inputs": ["vgs", "vds"],
        "IgGs": "Ij*(exp(min(vgs,Vmax)/Vt)-1)",
        "IgGd": "Ij*(exp(min(vgs-vds,Vmax)/Vt)-1)",
        "Parameters": {"Ij": 1e-9, "Vt": 0.08, "Vmax": 1.5}
      },
      "Qg": {
        "Method": "Equation",
        "Inputs": ["vgs", "vds"],
        "Expression": "Cgs0*vgs + Cgs1*Vw*log(cosh((vgs-Vpk)/Vw)) + Cgd0*(vgs-vds)",
        "Parameters": {"Cgs0": 0.1e-12, "Cgs1": 0.3e-12, "Vw": 0.5, "Vpk": -1.2, "Cgd0": 0.03e-12}
      },
      "Qd": {
        "Method": "Equation",
        "Inputs": ["vgs", "vds"],
        "Expression": "Cgd0*(vds-vgs) + Cds0*vds",
        "Parameters": {"Cgd0": 0.03e-12, "Cds0": 0.05e-12}
      }
    }
  }
}

with zipfile.ZipFile("fet.cmodel", "w", zipfile.ZIP_DEFLATED) as z:
    z.writestr("model.json", json.dumps(model, indent=2))
```

`circuitrf check fet.cmodel` reads it exactly as a placed device will:

```text
note: fet.cmodel: a composed FET model of 4 finger(s) × 100 um, valid at 25 °C only.
1 document(s) checked: 0 error(s), 0 warning(s), 1 note(s).
```

Place it in a netlist beside the `.cmodel` and run it:

```netlist
temp = 25
VGS = -1
VDS = 5
Vdc:VG  g 0  Vdc=VGS V
IProbe:ID  dd d
Vdc:VD  dd 0  Vdc=VDS V
ComposedFet:X1  g d 0  File="fet.cmodel"  N=4
analysis DC1 type=dc
measure Id_A = DC1.I("ID")
```

`circuitrf dc bench.cnl` reports `Id_A = 0.0599156`. The intrinsic voltages are `X1.gi`, `X1.di` and `X1.si`, and with
`N=8` the same file is an eight-finger device. A harmonic-balance or loadpull bench uses it the same way.

## How circuitRF reads it {#reading}

A `.cmodel` is read once per file and shared by every instance placed on it. It is read again when its size or
modification time changes, so a script can rewrite it between runs.

1. **The container.** The file must be a ZIP holding `model.json` at its root.
2. **`model.json`** is parsed as JSON. Each required field is read by name, and a missing or mistyped one is refused
   with a sentence that starts with the field's path: `Core.Constituents.Id.Method: 'Spline' is not a known method; the
   known methods are Equation, Table, ANN, Drift.`
3. **The arrays.** Every `arrays/<name>.npy` entry is read as one named array. A folder entry is skipped.
4. **Validation.** Every rule on this page is checked: each constituent's inputs and payload, each equation's names,
   each table's grid against its coefficients, each ANN's layer sizes, the training domains, the extrinsic
   network and its scaling rules. The first rule broken is the one refused.
5. **The network** is parsed as `.cnl` text, and each of its variables is evaluated to a number.
6. **Per instance**, the device is built as a small circuit: the network's elements with their values scaled to the
   instance's `N`, and the core on the intrinsic nets `gi`, `di`, `si`. The core's currents and charges are
   multiplied by `N/N0`.

`circuitrf check` and a placed `ComposedFet` call the same reader, so a file that `check` passes is a file that loads,
and the refusal sentence is the same in both.

**Fields that are not part of the format are ignored, not refused**, at the top level and inside `BasePeriphery`,
`Temperature`, `Network` and `Core`. A misspelt optional block (`Thermals`, `Core.Dispersion` spelt `Dispertion`) is
read as absent. Inside `Thermal`, `Traps`, `Extrapolation`, a `Domain` or a `Transform`, an unknown field is refused
by name, and inside a constituent an unknown field is read as a payload key and refused.

## The container {#container}

```text
fet.cmodel                      a ZIP archive
├── model.json                  the model, UTF-8 JSON
└── arrays/                     only for Table and ANN constituents
    ├── Id.values.npy
    ├── Id.coefficients.npy
    └── …
```

- `model.json` sits at the archive's root. An archive made by zipping a *folder* often stores
  `fet/model.json`, which is refused as `the container holds no model.json`.
- Each array is its own entry, `arrays/<name>.npy`, and `<name>` is the name the constituent refers to (`Id.values`).
- Entries other than `model.json` and `arrays/*.npy` are ignored. Compression and entry dates do not matter.

## model.json, field by field {#fields}

Numbers are in base SI units — volts, amps, coulombs, siemens, seconds, ohms — except temperatures, which are in °C.

| Field | What it holds |
|---|---|
| `FormatVersion` | `1`. A file from a newer circuitRF is refused, saying so. |
| `Kind`, `Terminals` | `Fet`; `G`, `D`, `S` — the symbol's pins 1, 2 and 3. Both are required and take exactly these values. |
| `BasePeriphery` | `Fingers` (N0) and `UnitWidth` (`"125 um"`, a number, a space and a length unit) of the device the constituents describe. |
| `Temperature` | `{ "Mode": "Single", "T0": 25 }` — valid at one temperature, with no temperature input — or `{ "Mode": "Range", "Tmin": …, "Tmax": … }`, in °C. |
| `Thermal` | Self-heating, on a `Range` model only: `{ "Rth": …, "Cth": … }` in K/W and J/K, or a `Ladder` of up to five such poles, at the base periphery; optionally `RthSlope` (K/W per K) and `Tref` (°C) for a thermal resistance that varies with the ambient. The device then has a thermal node `t` whose voltage is the temperature rise, into which it puts its dissipated power, with `Rth ∥ Cth` (or the ladder) from it to ground; every constituent is evaluated at the run's temperature plus that rise. A symbol's pin 4 is that node, so a heatsink network attached there is in parallel with the model's own. `Rth` scales by N0/N and `Cth` by N/N0. |
| `Traps` | Trapping: `Model` (`CaptureEmission`, `Custom`, or another installed model), `States` (the enabled ones: `phi1` gate-lag, `phi2` drain-lag), the model's `Parameters` and the `Levels` each state was trained at, in volts. Each state is a node of the device whose voltage is the state; the states are inputs of `Id` and `Qd`, never of `Ig` or `Qg`. `Coupling` is `Trained` (the default, never written) or `Shift`: under `Shift`, `Id` takes no state and is evaluated at a shifted gate voltage, `Id(vgs − ΔV, vds, T)` with `ΔV = ShiftDrain·(phi2 − vds) + ShiftGate·(vgs − phi1)` over the enabled states, so it equals the trap-free current at every DC bias; `ShiftDrain` and `ShiftGate` (V/V) are then in `Parameters`, one per enabled state. `Shift` is refused for a model other than `CaptureEmission`, with an `Id` that takes a trap state, and with a strength that is missing or not finite. `CaptureEmission`'s parameters are `TauCapt1`/`TauCapt2` and `TauEmit1`/`TauEmit2` (s), `Xref` and `Vc` (V), and with thermally activated emission `Ea1`/`Ea2` (eV) and `Tref` (°C). A `Custom` model adds `Expressions`: per state its `Dynamics` (dphi/dt, in V/s) and `Dc` (its DC value) over the state, `vgs`, `vds`, `T` (°C) and its parameters — the `Dc` without the state itself; the `Dc` must be a fixed point of the `Dynamics`. Absent: no traps. Not combined with `Core.Dispersion` or `Core.Symmetry: Exchange`. |
| `Network` | `Netlist`: `.cnl` text holding one `define` block with exactly six pins — extrinsic gate, drain, source, then intrinsic gate, drain, source — and its element values as variables. `Scaling`: each variable's rule, `fixed` (the default), `perFinger` or `perFingerInverse`. See [The extrinsic network](#network). |
| `Core.Constituents` | `Id`, `Ig`, `Qg` and `Qd`, each `{ "Method", "Inputs", … }`, the method one of `Equation`, `Table` or `ANN` (and `Drift` for the two charges). An `Equation` carries `Expression` and `Parameters` (named constants). A `Table` names its arrays in `Table` — a grid of values and the coefficients of the smooth spline through them — and records its `Smoothing`. An `ANN` names its arrays in `Network` and its hidden layers' `Activation`, `Tanh` or `Sigmoid`. `Ig` carries two of whichever its method uses, `IgGs` and `IgGd`. Inputs are `vgs` and `vds` in volts, `T` in °C for a `Range` model, and the trap states in volts for a model with `Traps`. Currents are in A, charges in C. An optional `IdRF` is the drain current at RF, with `Core.Dispersion`. |
| `Transform` (Table, ANN current) | `{ "Kind": "Asinh", "Scale": I0 }`: the arrays hold `u`, and the current is `I0·sinh(u)` — straight through zero within ±I0 and logarithmic beyond, so a current spanning many decades is held to the same ratio throughout. |
| `Drift` (`Qg`, `Qd`) | The charges as a gate–source and a gate–drain depletion charge, a drain–source charge, and a drift charge that depends on the gate–drain voltage and the drain current: `Qg = QGS + QGD + Qdrift`, `Qd = −QGD − (1 − Lambda)·Qdrift + QDS`. Both charges are `Drift` or neither. `Qg` names the networks `QGS`, `QGD` and `Qdrift`, `Qd` names `QGD`, `Qdrift` and `QDS` and carries `Parameters: { "Lambda": … }`, between 0 and 1. The `Domain`'s `Box` holds each network's training interval: `QGS`, `QGD`, `QDS`, `Qdrift.vgd` and `Qdrift.Id`. |
| `Core.Dispersion` | `{ "Tau": τ }`, in seconds, present exactly when `IdRF` is: the drain current follows `Id` at DC and `IdRF` well above 1/(2πτ), moving between them as one pole. |
| `Domain` (Table, ANN) | Where the constituent was trained: `Polygon`, a convex list of `[vgs, vds]` vertices, and `Box`, an interval per other input. Outside it the constituent is continued in a straight line from the nearest point of the domain; a current also gains a conductance that grows with the distance. |
| `Core.Symmetry` | `Exchange`: the constituents describe `vds ≥ 0`, and a negative `vds` is the same device with drain and source exchanged. Absent for an asymmetric device. |
| `Extrapolation` | `Gx`: for each table or ANN current (`Id`, `IgGs`, `IgGd`, `IdRF`), the conductance in S that current gains outside its domain. |
| `Symbol` | A library symbol's name, or a `.csym` path relative to the `.cmodel`. Absent: the standard FET symbol. |
| `Provenance` | Optional, and not read by the simulator: `Created`, `CircuitRfVersion`, `Sources` (`Path`, `Sha256`) and `SettingsDigest`; with linear charges, `Charges` (`Qg, Qd: Linear from 1 bias point(s)`, or with typed ones `Qg, Qd: typed (Cgs 300 fF, Cgd 50 fF, Cds 80 fF, Cm 0 F)`). A script that records where its model came from here makes it traceable. |

## The core: terminals, signs and units {#core}

The core sits between the intrinsic nets. Its two voltages are `vgs` (gate to source) and `vds` (drain to source), in
volts.

| Constituent | Payload keys | Means | Sign |
|---|---|---|---|
| `Id` | one | The channel current, A | Positive flowing into the drain and out of the source |
| `Ig` | `IgGs`, `IgGd` | The gate–source and gate–drain conduction currents, A | Positive flowing into the gate; `IgGd` leaves through the drain, so the drain terminal carries `Id − IgGd` |
| `Qg` | one | The charge at the gate, C | — |
| `Qd` | one | The charge at the drain, C | — |

The source charge is `−Qg − Qd`, so the model conserves charge by construction. **Write charges, not capacitances.**
The capacitances are the charges' derivatives, which circuitRF takes for you:

| | by `vgs` | by `vds` |
|---|---|---|
| `Qg` | `∂Qg/∂vgs = Cgs + Cgd` | `∂Qg/∂vds = −Cgd` |
| `Qd` | `∂Qd/∂vgs = Cm − Cgd` | `∂Qd/∂vds = Cgd + Cds` |

Constant `Cgs`, `Cgd` and `Cds` are `Qg = Cgs·vgs + Cgd·(vgs − vds)` and `Qd = Cds·vds − Cgd·(vgs − vds)`, which is
what the example writes. `Cm`, a transcapacitance, is allowed and kept; it makes `∂Qd/∂vgs` differ from `∂Qg/∂vds`.

**Every constituent describes the base periphery**: the device of `BasePeriphery.Fingers` fingers. An instance's `N`
multiplies all four by `N/N0`. Write the currents and charges of the device you measured or simulated, not of one
finger.

## Equation constituents {#equation}

```json
"Id": {
  "Method": "Equation",
  "Inputs": ["vgs", "vds"],
  "Expression": "Ipk*(1+tanh(P1*(vgs-Vpk)))*tanh(alpha*vds)*(1+lambda*vds)",
  "Parameters": {"Ipk": 0.08, "P1": 2.0, "Vpk": -1.2, "alpha": 1.5, "lambda": 0.02}
}
```

- **The payload key** is `Expression` for `Id`, `Qg`, `Qd` and `IdRF`, and the pair `IgGs`, `IgGd` for `Ig`. Both of
  `Ig`'s are required; write `"0"` for a branch the device does not have.
- **`Inputs`** names the voltages the expression may read: `vgs`, `vds`, and `T` (°C) on a `Range` model. An
  expression may read only its inputs, its `Parameters`, `pi` and `e`. Any other name is refused, naming it.
- **`Parameters`** are named numbers, and may be left out when there are none. A parameter may not share an input's
  name.
- **The language** is circuitRF's [expression language](expressions.html#constants): `+ − * / ^`, `exp`, `log`,
  `sqrt`, `tanh`, `cosh`, `sinh`, `atan`, `min`, `max`, `abs`, `if(cond, a, b)` and the rest of the real functions.
  Derivatives are exact, by automatic differentiation, so nothing needs a hand-written Jacobian.
- **An equation is global**: it has no training domain and is never extrapolated, so it is evaluated at whatever
  voltage the circuit reaches. An exponential that is harmless at the bias point overflows when harmonic balance swings
  the gate several volts. Limit its argument, as the example does with `min(vgs, Vmax)`.

## The extrinsic network {#network}

`Network.Netlist` is `.cnl` text — the [netlist format](netlist.html) — holding one `define` block:

```netlist
define extrinsic (G D S gi di si)
  parameters Lg=50p H  Rg=4 Ohm  Cpg=20f F
  C:CPG  G 0  C=Cpg
  L:LG   G g1  L=Lg
  R:RG   g1 gi R=Rg
  …
end extrinsic
```

- **Six pins, in order**: the extrinsic gate, drain and source, then the intrinsic gate, drain and source. The block's
  name and the pin names are yours; inside the device the last three are always called `gi`, `di` and `si`. A pin
  named `0` is refused, and `0` inside the block is ground.
- **Values are variables.** Each element value refers to a variable of the block's `parameters` line, or of a variable
  line inside it, or is a number with its unit (`Ls=5p H`). Every variable must evaluate to a finite real number.
- **`Scaling`** gives a variable's rule by its name: `fixed` (the default), `perFinger` (× N/N0) or `perFingerInverse`
  (× N0/N). The usual choice is series resistances `perFingerInverse`, intrinsic capacitances `perFinger`, and pad
  capacitances and inductances `fixed`. A rule naming no variable is refused.
- **Use R, L, C and `Mutual`.** With `m` on the instance, inductors and mutuals are divided by it and every other
  element carries it as its device multiplier, which the engine refuses on a branch element such as a source.
- **Reserved names.** An internal net may not be called `gi`, `di` or `si` unless it is one of pins 4–6, and the first
  three pins may not have those names. No element may be called `core`. With self-heating, no element may be called
  `Rth` or `Cth` (`Rth1`, `Cth1`, … for a ladder) and no net `t` (`t_1`, … for a ladder).
- **A network with nothing in it** still needs a path from each extrinsic pin to its intrinsic one. A resistor of a few
  milliohms is the usual stand-in.

## Arrays: the .npy entries {#npy}

`Table` and `ANN` constituents keep their numbers in `arrays/<name>.npy`. Each entry is a NumPy `.npy` file in the
layout circuitRF writes its [results](npy-export.html) in: a structured array of one record, with one field holding the
array and a `__meta__` field holding JSON that describes it. A plain `np.save(array)` file is refused.

```text
{'descr': [('Id.W1', '<f8', (8, 2)), ('__meta__', '|S180')], 'fortran_order': False, 'shape': (1,), }
```

| Part | Rule |
|---|---|
| The data field | Named as the entry is (`Id.W1` in `arrays/Id.W1.npy`); little-endian float64, `<f8`; its shape is the array's |
| Element order | **Row-major** (C order): the last axis varies fastest |
| `__meta__` | UTF-8 JSON, as a byte string of its own length (NumPy type `S<n>`) |

The JSON:

```json
{"format_version": 2, "groups": [""],
 "Id.W1": {"group": "", "cube": "Id.W1", "kind": "Real",
           "axes": [{"name": "output", "unit": "", "values": [0, 1, 2, 3, 4, 5, 6, 7]},
                    {"name": "input",  "unit": "", "values": [0, 1]}]}}
```

`format_version` is `2`. The key naming the field, and its `cube`, are the array's name. `axes` has one entry per
dimension, in order: `name`, `unit` (may be empty) and `values`, whose length is that dimension's length. `values` is a
JSON list even for a dimension of one. What an axis's name and values must be depends on the constituent, and is
given with each below.

**In Python** with NumPy:

```python
import io, json, numpy as np

def cube_npy(name, data, axes):
    """The bytes of arrays/<name>.npy: one real array, axes = [(axis_name, values), ...]."""
    data = np.ascontiguousarray(data, dtype="<f8")
    assert data.shape == tuple(len(v) for _, v in axes)
    meta = json.dumps({"format_version": 2, "groups": [""],
                       name: {"group": "", "cube": name, "kind": "Real",
                              "axes": [{"name": n, "unit": "", "values": [float(x) for x in v]}
                                       for n, v in axes]}}).encode()
    rec = np.zeros(1, dtype=[(name, "<f8", data.shape), ("__meta__", f"S{len(meta)}")])
    rec[name][0] = data
    rec["__meta__"][0] = meta
    buf = io.BytesIO()
    np.save(buf, rec, allow_pickle=False)
    return buf.getvalue()

def vector_npy(name, v):
    v = np.atleast_1d(np.asarray(v, dtype=float))
    return cube_npy(name, v, [("index", range(len(v)))])
```

**In MATLAB** (R2019b or later) or GNU Octave, save this as `crf_cube_npy.m`. MATLAB stores arrays column-major, so the
function permutes the data into row-major order before writing it.

```matlab
function crf_cube_npy(file, name, data, axes)
% Write one real array as a circuitRF .npy.
%   file  the .npy to write
%   name  the array's name: the entry's file name without .npy ('Id.values')
%   data  the array, one dimension per axis (a vector is one axis)
%   axes  {'vgs', vgsSites; 'vds', vdsSites}: one row per dimension, in order
if isvector(data) && size(axes, 1) == 1, data = data(:); end
shape = cellfun(@numel, axes(:, 2))';
assert(isequal(size(data, 1:numel(shape)), shape), 'data does not match the axes');
ax = cell(1, size(axes, 1));
for k = 1:size(axes, 1)
  ax{k} = struct('name', axes{k, 1}, 'unit', '', 'values', {num2cell(double(axes{k, 2}(:)'))});
end
cube = struct('group', '', 'cube', name, 'kind', 'Real', 'axes', {ax});
meta = ['{"format_version":2,"groups":[""],"' name '":' jsonencode(cube) '}'];
if numel(shape) == 1, shp = sprintf('(%d,)', shape);
else, shp = ['(' strjoin(arrayfun(@num2str, shape, 'UniformOutput', false), ', ') ')']; end
header = sprintf('{''descr'': [(''%s'', ''<f8'', %s), (''__meta__'', ''|S%d'')], ''fortran_order'': False, ''shape'': (1,), }', ...
                 name, shp, numel(meta));
pad = 64 - mod(10 + numel(header) + 1, 64);
header = [header repmat(' ', 1, mod(pad, 64)) newline];
f = fopen(file, 'w', 'ieee-le');
fwrite(f, [uint8(147) uint8('NUMPY') uint8([1 0])], 'uint8');
fwrite(f, numel(header), 'uint16');
fwrite(f, header, 'char');
fwrite(f, permute(double(data), ndims(data):-1:1), 'double');   % row-major order
fwrite(f, meta, 'char');
fclose(f);
end
```

## Table constituents {#table}

```json
"Id": {
  "Method": "Table",
  "Inputs": ["vgs", "vds"],
  "Table": "Id",
  "Smoothing": 0,
  "Domain": {"Polygon": [[-4, 0], [1, 0], [1, 12], [-4, 12]]}
}
```

`"Table": "Id"` names two arrays, `arrays/Id.values.npy` and `arrays/Id.coefficients.npy`. (`Ig` names two tables, one
in `IgGs` and one in `IgGd`.)

- **`<name>.values`** is the grid: one dimension per input, in `Inputs` order, and each axis named after its input,
  with the grid's sites as its values. The sites must be strictly increasing, with at least two per axis, and every
  value finite. Evaluation uses the sites; the values are a record of what the spline passes through.
- **`<name>.coefficients`** is what is evaluated: the coefficients of a tensor-product cubic B-spline, one per site
  along each axis. Its axes are conventionally the Greville abscissae `(t[i+1] + t[i+2] + t[i+3]) / 3`; only their
  lengths are checked.
- **The knots are not stored.** circuitRF derives them from the sites `x[0] … x[n−1]`: four copies of `x[0]`, the
  interior sites `x[2] … x[n−3]`, then four copies of `x[n−1]`. This is the **not-a-knot** cubic spline, and with
  `Smoothing` 0 its coefficients are the ones that interpolate the values. Use at least four sites per axis; with two
  or three the axis is one cubic piece with four coefficients.
- **`Smoothing`** is a record of the weight a smoothing fit used, 0 for interpolation. It does not change the
  evaluation.
- A Table must have `vgs` and `vds` among its inputs, a `Domain` inside its grid, and a `Gx` (see
  [Training domain](#domain)).

**In Python**, SciPy's `make_interp_spline` uses the same knots. This replaces the example's equation drain current with
a table of it and writes `fet_table.cmodel`, continuing the script above with `cube_npy` defined:

```python
import numpy as np
from scipy.interpolate import make_interp_spline

def Id(vgs, vds):
    return 0.08*(1+np.tanh(2.0*(vgs+1.2)))*np.tanh(1.5*vds)*(1+0.02*vds)

vgs = np.arange(-4.0, 1.0001, 0.1)
vds = np.arange(0.0, 12.0001, 0.25)
V = Id(vgs[:, None], vds[None, :])           # V[i, j] = Id(vgs[i], vds[j]): axes in Inputs order

# Interpolate along vgs, then along vds. make_interp_spline puts the interpolated axis first.
C = make_interp_spline(vds, make_interp_spline(vgs, V, k=3).c.T, k=3).c.T

def greville(x):
    t = np.r_[[x[0]] * 4, x[2:-2], [x[-1]] * 4]
    return (t[1:-3] + t[2:-2] + t[3:-1]) / 3

model["Core"]["Constituents"]["Id"] = {
    "Method": "Table", "Inputs": ["vgs", "vds"], "Table": "Id", "Smoothing": 0,
    "Domain": {"Polygon": [[-4, 0], [1, 0], [1, 12], [-4, 12]]},
}
model["Extrapolation"] = {"Gx": {"Id": 1e-3}}

with zipfile.ZipFile("fet_table.cmodel", "w", zipfile.ZIP_DEFLATED) as z:
    z.writestr("model.json", json.dumps(model, indent=2))
    z.writestr("arrays/Id.values.npy", cube_npy("Id.values", V, [("vgs", vgs), ("vds", vds)]))
    z.writestr("arrays/Id.coefficients.npy",
               cube_npy("Id.coefficients", C, [("vgs", greville(vgs)), ("vds", greville(vds))]))
```

Between the grid lines the table agrees with the equation it was sampled from to about 5×10⁻⁵ of the current on this
0.1 V grid.

**In MATLAB**, the coefficients are a linear solve against the spline's collocation matrix. Save this as
`crf_spline_coefs.m`; it needs no toolbox.

```matlab
function c = crf_spline_coefs(x, y)
% The B-spline coefficients of circuitRF's not-a-knot cubic through (x, y), along y's FIRST
% dimension. x: n >= 4 strictly increasing sites. c has y's size.
x = x(:); n = numel(x);
t = [repmat(x(1), 4, 1); x(3:n-2); repmat(x(n), 4, 1)];       % the knots
B = zeros(n);
for i = 1:n
  s = find(t(1:end-1) <= x(i) & x(i) < t(2:end), 1, 'last');   % knot span of x(i)
  if isempty(s), s = n; end                                     % the last site
  N = zeros(1, 4); N(1) = 1;                                    % Cox-de Boor, degree 3
  for d = 1:3
    saved = 0;
    for r = 1:d
      left = x(i) - t(s - d + r); right = t(s + r) - x(i);
      tmp = N(r) / (right + left);
      N(r) = saved + right * tmp; saved = left * tmp;
    end
    N(d + 1) = saved;
  end
  B(i, s-3:s) = N;
end
c = reshape(B \ reshape(y, n, []), size(y));
end
```

A script that writes the same `fet_table.cmodel`, with the other three constituents as equations:

```matlab
Id = @(vgs, vds) 0.08*(1+tanh(2*(vgs+1.2))).*tanh(1.5*vds).*(1+0.02*vds);
vgs = -4:0.1:1;  vds = 0:0.25:12;
V = Id(vgs', vds);                        % V(i,j) = Id(vgs(i), vds(j)): axes in Inputs order
C = crf_spline_coefs(vgs, V);             % along vgs ...
C = crf_spline_coefs(vds, C')';           % ... then along vds
greville = @(x) conv([x(1) x(1) x(1) x(3:end-2) x(end) x(end) x(end)], [1 1 1]/3, 'valid');

out = tempname; mkdir(out); mkdir(fullfile(out, 'arrays'));
crf_cube_npy(fullfile(out, 'arrays', 'Id.values.npy'), 'Id.values', V, {'vgs', vgs; 'vds', vds});
crf_cube_npy(fullfile(out, 'arrays', 'Id.coefficients.npy'), 'Id.coefficients', C, ...
             {'vgs', greville(vgs); 'vds', greville(vds)});

m.FormatVersion = 1;
m.Kind = 'Fet';
m.Terminals = {'G', 'D', 'S'};
m.BasePeriphery = struct('Fingers', 4, 'UnitWidth', '100 um');
m.Temperature = struct('Mode', 'Single', 'T0', 25);
m.Network.Netlist = sprintf([ ...
  'define extrinsic (G D S gi di si)\n' ...
  '  parameters Lg=50p H  Ld=50p H  Ls=5p H  Rg=4 Ohm  Rd=8 Ohm  Rs=6 Ohm  Cpg=20f F  Cpd=20f F\n' ...
  '  C:CPG  G 0  C=Cpg\n  C:CPD  D 0  C=Cpd\n' ...
  '  L:LG   G g1  L=Lg\n  R:RG   g1 gi R=Rg\n' ...
  '  L:LD   D d1  L=Ld\n  R:RD   d1 di R=Rd\n' ...
  '  L:LS   S s1  L=Ls\n  R:RS   s1 si R=Rs\n' ...
  'end extrinsic\n']);
m.Network.Scaling = struct('Rg', 'perFingerInverse', 'Rd', 'perFingerInverse', 'Rs', 'perFingerInverse');
c.Id = struct('Method', 'Table', 'Inputs', {{'vgs', 'vds'}}, 'Table', 'Id', 'Smoothing', 0, ...
              'Domain', struct('Polygon', [-4 0; 1 0; 1 12; -4 12]));
c.Ig = struct('Method', 'Equation', 'Inputs', {{'vgs', 'vds'}}, ...
              'IgGs', 'Ij*(exp(min(vgs,Vmax)/Vt)-1)', 'IgGd', 'Ij*(exp(min(vgs-vds,Vmax)/Vt)-1)', ...
              'Parameters', struct('Ij', 1e-9, 'Vt', 0.08, 'Vmax', 1.5));
c.Qg = struct('Method', 'Equation', 'Inputs', {{'vgs', 'vds'}}, ...
              'Expression', 'Cgs0*vgs + Cgs1*Vw*log(cosh((vgs-Vpk)/Vw)) + Cgd0*(vgs-vds)', ...
              'Parameters', struct('Cgs0', 0.1e-12, 'Cgs1', 0.3e-12, 'Vw', 0.5, 'Vpk', -1.2, 'Cgd0', 0.03e-12));
c.Qd = struct('Method', 'Equation', 'Inputs', {{'vgs', 'vds'}}, ...
              'Expression', 'Cgd0*(vds-vgs) + Cds0*vds', ...
              'Parameters', struct('Cgd0', 0.03e-12, 'Cds0', 0.05e-12));
m.Core.Constituents = c;
m.Extrapolation.Gx = struct('Id', 1e-3);

f = fopen(fullfile(out, 'model.json'), 'w'); fwrite(f, jsonencode(m), 'char'); fclose(f);
zip('fet_table.zip', {'model.json', 'arrays'}, out);   % zip always names the archive .zip ...
movefile('fet_table.zip', 'fet_table.cmodel');        % ... so rename it afterwards
rmdir(out, 's');
```

Inputs and other lists are written as cell arrays (`{{'vgs', 'vds'}}` inside `struct`) so `jsonencode` writes JSON
lists; a one-element list needs the same, or it is written as a bare value.

## ANN constituents {#ann}

```json
"Id": {
  "Method": "ANN",
  "Inputs": ["vgs", "vds"],
  "Network": "Id",
  "Activation": "Tanh",
  "Domain": {"Polygon": [[-4, 0], [1, 0], [1, 12], [-4, 12]]}
}
```

`"Network": "Id"` names the arrays of one feed-forward network with a single output:

| Array | Shape | Use |
|---|---|---|
| `Id.inputOffset`, `Id.inputScale` | one value per input, in `Inputs` order | `x̂ = (x − inputOffset) · inputScale`; no scale may be zero |
| `Id.W1` … `Id.WL` | `outputs × inputs` of that layer | Layer `k`: `a = σ(Wk · a + bk)`; the last layer `L` is linear and has one output |
| `Id.b1` … `Id.bL` | that layer's outputs | Its bias |
| `Id.outputOffset`, `Id.outputScale` | one value each | `y = outputOffset + outputScale · ŷ` |

- `σ` is `Activation`, `Tanh` or `Sigmoid`, on every hidden layer.
- There are 1 to 4 hidden layers, so 2 to 5 weight matrices, numbered from 1 without gaps; each hidden layer has at most
  64 neurons, and there are at most 7 inputs.
- A weight matrix's axes are `output` and `input`, and a vector's axis is `index`, each with the values 0, 1, 2, ….
  Only the lengths are checked.
- A network trained in another framework transfers directly. Frameworks differ in how they store a dense layer's
  weights: one stored `inputs × outputs` is transposed before it is written. Fold any input or output normalisation the
  training used into the offsets and scales.

```python
for k, (W, b) in enumerate(layers, 1):          # layers = [(W1, b1), (W2, b2), (W3, b3)]
    z.writestr(f"arrays/Id.W{k}.npy",
               cube_npy(f"Id.W{k}", W, [("output", range(W.shape[0])), ("input", range(W.shape[1]))]))
    z.writestr(f"arrays/Id.b{k}.npy", vector_npy(f"Id.b{k}", b))
for name, v in {"inputOffset": in_off, "inputScale": in_scale,
                "outputOffset": [out_off], "outputScale": [out_scale]}.items():
    z.writestr(f"arrays/Id.{name}.npy", vector_npy(f"Id.{name}", v))
```

## Training domain and extrapolation {#domain}

A Table or ANN constituent is trustworthy only where it was trained, so it carries that region and circuitRF continues
it in a straight line outside it.

- **`Domain.Polygon`** is the region in the `(vgs, vds)` plane, a list of `[vgs, vds]` vertices. It must be convex,
  with at least three vertices and some area, in either winding order. A Table's polygon must lie inside its grid.
- **`Domain.Box`** holds `[lo, hi]` for each input other than `vgs` and `vds` — `T` on a `Range` model, a trap state —
  and must have one for every such input.
- **`Extrapolation.Gx`** gives, for each Table or ANN *current* payload (`Id`, `IgGs`, `IgGd`, `IdRF`), the conductance
  in S that current gains in proportion to the distance outside the domain. Each such current needs one, and an
  `Equation` current must not have one. Charges have none.
- **`Transform`** on a Table or ANN current, `{"Kind": "Asinh", "Scale": I0}`, says the arrays hold `u` and the current
  is `I0·sinh(u)`. Fit a gate current that spans many decades in `u = asinh(I/I0)`.

## Temperature, self-heating, traps and dispersion {#optional}

These are optional and are fields of `model.json` (see [the table](#fields)). What they model is described in the
Model Composer chapter:

- **A temperature range** (`"Mode": "Range"`) adds the input `T`, in °C, which every constituent may read. A run's
  `temp` is held to `Tmin … Tmax`. A `Single` model is used as it is at any `temp`, with one warning naming both
  temperatures — set `temp` in the bench to `T0` to run it at its own temperature. See
  [Temperature](model-composer.html#temperature).
- **Self-heating** (`Thermal`) needs a `Range` model. See [Temperature](model-composer.html#temperature).
- **Traps** (`Traps`) add the states `phi1` and `phi2` as inputs of `Id` and `Qd`. See
  [Traps](model-composer.html#trap-data).
- **Dispersion** (`IdRF` and `Core.Dispersion`) is a second drain current that applies above `1/(2πτ)`.
- **`Core.Symmetry: Exchange`** lets constituents written for `vds ≥ 0` serve negative `vds` too.

## Checking and running it {#checking}

| Step | Command line | Over MCP |
|---|---|---|
| Is the file well formed? | `circuitrf check fet.cmodel` | `check path=fet.cmodel` |
| Does a bench that places it resolve? | `circuitrf check bench.cnl` | `check path=bench.cnl` |
| Run it | `circuitrf dc bench.cnl`, `hb`, `lp`, `sparam` | `run analysis=dc path=bench.cnl` |
| Do the charges conserve charge in large signal? | `circuitrf model check-charge bench.cnl --device X1 --bias -1.2,5` | `model noun=check-charge path=bench.cnl device=X1 bias=-1.2,5` |

`--bias` is `vgs,vds` at the intrinsic plane. `check-charge` drives the device in harmonic balance and reports any DC
the charges create; it exits 3 above the threshold. A model written as charge functions passes by construction, so a
failure means a charge does not read the voltages the core gives it.

A `.cmodel` written elsewhere runs wherever one from the Model Composer does: as a `ComposedFet` in any schematic or
netlist, and as [harmonicaRF](harmonicarf.html)'s device.

## For an AI agent {#agents}

The MCP server's `reference` tool serves this page as the topic `cmodel`. No MCP tool writes a file of your text, so
an agent writes the model with its own tools:

1. `reference topic=cmodel` — this page.
2. Write a script that builds `model.json` (and any arrays) and zips it, as in [the example](#example), and run it
   with the agent's own shell.
3. `check path=<f.cmodel>` — 0 errors, or one sentence naming the field to fix.
4. Write a bench `.cnl` placing `ComposedFet:X1 g d s File="<f.cmodel>"`, `check` it, and `run` it.
5. For a model with nonlinear charges, `model noun=check-charge path=<bench.cnl> device=X1 bias=<vgs>,<vds>`.

To build a model from measured data instead, use the `model` tool, whose steps write a `.cmodel` for you; the
`reference` topic `cmc` describes its document.

## Tips and pitfalls {#pitfalls}

- **Start from equations.** Get an all-equation model through `check` and a DC run, then replace one constituent at a
  time with a Table or ANN. A refusal then names a field of the part just changed.
- **Run a DC point before anything else.** Compare `Id` at one bias with your own evaluation of the same constituent at
  the intrinsic voltages `X1.gi − X1.si` and `X1.di − X1.si`. A sign, a unit or an axis order that is wrong shows here,
  in one number.
- **Constituents describe `BasePeriphery`.** A model written for one finger with `Fingers: 4` is four times too small
  at `N=4`.
- **Charges, not capacitances.** `Qg = Cgs(vgs)·vgs` is not the charge of a capacitance `Cgs(vgs)`: the derivative adds
  `vgs·Cgs′(vgs)`. Integrate the capacitance. The sign table in [The core](#core) gives each derivative.
- **Bound every exponential.** An `Equation` is evaluated wherever the circuit goes. Limit the argument with `min`, or
  continue it with `if`, before harmonic balance takes the gate to a voltage the data never reached.
- **Spell the optional blocks exactly.** `Thermal`, `Traps`, `Extrapolation`, `Symbol` and `Core.Dispersion` that are
  misspelt are read as absent, without a word. Names are case-sensitive throughout, inputs included: `vgs`, not `Vgs`.
- **`model.json` at the archive's root**, and each array under `arrays/` with the name the constituent gives it.
  Zipping a folder rather than its contents moves `model.json` down a level.
- **MATLAB's `zip` adds `.zip`** to an archive name that does not end in it; write `fet.zip` and rename it.
- **Row-major arrays.** A MATLAB array written with a plain `fwrite` is column-major, which transposes every matrix
  silently. `crf_cube_npy` permutes it.
- **Table axes follow `Inputs`.** `values` and `coefficients` have their dimensions in the order `Inputs` lists, each
  axis named after its input.
- **Use the not-a-knot knots.** Coefficients from another spline (natural, clamped, or a smoothing spline with its own
  knots) have the right count and the wrong values. Interpolate with the knots in [Table constituents](#table).
- **Keep the domain wider than the drive.** Outside the domain a Table or ANN current is a straight line plus `Gx`, not
  the device. Train over the voltages the circuit will reach, swing included.
- **Set `temp` for a `Single` model.** A bench's default temperature is 26.85 °C (300 K), so a model valid at 25 °C
  runs there with a warning naming both temperatures. `temp = 25` in the bench runs it at its own temperature, and
  `check` then reports that ambient instead.
- **Write `Provenance`.** It is optional and not simulated, but `Created` and `Sources` with each source's SHA-256 say
  later where a model came from.
