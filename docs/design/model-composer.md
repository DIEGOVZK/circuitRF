# circuitRF — Model Composer and the composed FET

**Status:** MC-1 built (the `.cmodel` format and the `ComposedFet` device, equation constituents);
MC-2 built (table and ANN constituents, guided extrapolation, drain–source symmetry, grid evaluation);
MC-3 built (the `.cmc` document, data sets, importers, the cache, data hygiene);
MC-4 built (pads, the six-pin network, de-embedding, intrinsic re-referencing and quantities);
MC-5 built (the extrinsic fit); MC-6 built (the ANN trainer); MC-7 built (the current constituents and the
DC/RF dispersion); MC-8 built (the charge constituents and Check Charge Conservation); MC-9 built (temperature
as an input, the thermal node, Rth extraction, the single-temperature model) ·
**Source:** `docs/sonnet-briefs/brief-modelcomposer-0-overview.md` (decisions D1–D16) and the phase briefs
`brief-modelcomposer-1` … `-16` · **Primary reference:** [P1] J. C. Pedro, D. E. Root, J. Xu, L. C. Nunes,
*Nonlinear Circuit Simulation and Modeling*, Cambridge University Press, 2018.
**Reads with:** `sdd.md` (the expression engine and dual-number AD the constituents share),
`nonlinear-dc.md` (the `Evaluate` contract), `harmonic-balance.md`, `data-model.md` §5.

This note is the standing reference for the Model Composer series. Each phase updates the section it
builds; the overview remains the record of why each decision was taken.

---

## 1. What it is

**Model Composer** (Tools ▸ Model Composer…) turns measured or simulated device data into a nonlinear FET
model that DC, S-parameters and harmonic balance use, and turns S-parameter blocks into passive models.
Its document is the `.cmc`; what it exports for a transistor is a `.cmodel`, which a schematic places as a
`ComposedFet`. A passive model exports as an ordinary cell.

The device is not generated SDD text: its core needs table and ANN evaluation with exact derivatives,
guided extrapolation, internal state nodes and allocation-free grid evaluation, none of which an SDD
line can carry.

## 2. The decisions, in brief

| | Decision |
|---|---|
| D1 | `.cmc` is the composer document (JSON); `.cmodel` is a ZIP of `model.json` + `arrays/*.npy`; `<name>.cmc-cache/` is derived data. |
| D2 | A `.cmc` is `Active` (a three-terminal FET) or `Passive` (an N-port). |
| D3 | `ComposedFet:X1 g d s File=… N=…` elaborates **as a cell**: the network's elements plus one `ComposedCore` on `gi di si`. |
| D4 | Constituents `Id`, `Ig`, `Qg`, `Qd` (optional `IdRF`); each an Equation, a Table or an ANN. Charges are always charge functions; there is no transconductance delay (the charges carry it). |
| D5 | An extrinsic network is a six-pin `.csch`: extrinsic G, D, S, then intrinsic G, D, S; its variables are its element values. Stock networks ship as resources. |
| D6 | Every data set records where its bias voltages were measured; the composer models in intrinsic voltages. |
| D7 | `q` and `∂q/∂v` come from one function, so a model is terminal-charge-conserving by construction; Check Charge Conservation reports on data, model and large signal. |
| D8 | Two or more temperatures: `T` is an input, clamped to the range with one warning. One temperature: the model records `T0`, has no `T`, and a run elsewhere warns once naming both. |
| D9 | `N0`/`W0` are recorded; `N` scales the core by `N/N0` and each network variable by its rule. `M` keeps its meaning. |
| D10 | Traps are a setting — `None`, `CaptureEmission`, `Custom` — behind one contract. |
| D11 | Data sources are referenced, relative to the `.cmc`, and cached. |
| D12 | Active exports a `.cmodel` with symbol, rules, temperature, traps and provenance; passive exports a cell through `CellCreate`. |
| D13 | Every long operation is a `src/Design/Modeling` function taking a `RunControl`. |
| D14 | `src/Core/Devices/Composed/` (device, format, evaluators), `src/Engine/Modeling/` (numerics), `src/Design/Modeling/` (document and flows), `src/Ui/ModelComposer/` (window only). No new project. |
| D15 | Every operation has a `model` CLI noun calling the same function as the GUI. |
| D16 | Exit codes 0 / 1 / 3 / 130. |

## 3. The `.cmodel` container (as built)

`CmodelFile` (`src/Core/Devices/Composed/CmodelFile.cs`) is the only reader, validator and writer.

- **Entries.** `model.json` first, then `arrays/<name>.npy` in ordinal name order. Every entry's timestamp
  is 1980-01-01 00:00. Arrays go through RfCore's own `.npy` writer and reader (`DataSetExporter.ExportNpy`,
  `DataSetImporter.ImportNpy` — the stream forms of the file exporter), one cube named after the entry.
- **Deterministic.** JSON properties in the fixed order below, two-space indentation, `\n` line ends,
  doubles in shortest round-trip form. The writer stamps nothing — `Provenance.Created` is a field the
  caller supplies — so the same model writes the same bytes.
- **Validated both ways.** `Read` validates before returning and `Write` validates before writing.

### 3.1 `model.json`

```json
{
  "FormatVersion": 1,
  "Kind": "Fet",
  "Terminals": ["G", "D", "S"],
  "BasePeriphery": { "Fingers": 8, "UnitWidth": "125 um" },
  "Temperature": { "Mode": "Single", "T0": 25 },
  "Network": {
    "Netlist": "define gan (P1 P2 P3 P4 P5 P6)\nparameters rg=5.2 …\nR:rg n1 P4 R=rg\n…\nend\n",
    "Scaling": { "rg": "perFingerInverse", "Cpgi": "perFinger" }
  },
  "Core": {
    "Constituents": {
      "Id": { "Method": "Equation", "Inputs": ["vgs", "vds"], "Expression": "…", "Parameters": { "beta": 0.04 } },
      "Ig": { "Method": "Equation", "Inputs": ["vgs", "vds"], "IgGs": "…", "IgGd": "…", "Parameters": { } },
      "Qg": { "Method": "Equation", "Inputs": ["vgs", "vds"], "Expression": "…", "Parameters": { } },
      "Qd": { "Method": "Equation", "Inputs": ["vgs", "vds"], "Expression": "…", "Parameters": { } }
    }
  },
  "Symbol": "symbols/gan.csym",
  "Provenance": { "Created": "…", "CircuitRfVersion": "…", "Sources": [ { "Path": "…", "Sha256": "…" } ], "SettingsDigest": "…" }
}
```

`Temperature` is `{ "Mode": "Range", "Tmin": …, "Tmax": … }` for a ranged model. `Symbol` is omitted for
the standard FET symbol. `Traps` (MC-10) is absent until its phase, and `Traps.Model` must name a known model
(`None` today).

**`Thermal` (MC-9)**, after `Temperature`, makes the model self-heat (§12.2): `{ "Rth": 60, "Cth": 1.7e-8 }` for one
pole, or `{ "Ladder": [ { "Rth": …, "Cth": … }, … ] }` for a Foster ladder of up to five, in K/W and J/K at the base
periphery; `RthSlope` (K/W per K) with `Tref` (°C) when the thermal resistance varies with the ambient. Absent: no
thermal node.

**Table and ANN constituents (MC-2)** name arrays instead of carrying expressions, and store the domain
they were trained over. `Core.Symmetry` and the top-level `Extrapolation` appear only when used:

```json
"Core": {
  "Symmetry": "Exchange",
  "Constituents": {
    "Id": { "Method": "Table", "Inputs": ["vgs", "vds"], "Table": "Id", "Smoothing": 0,
            "Domain": { "Polygon": [[-3, 0], [1, 0], [1, 8], [0, 10], [-3, 10]] } },
    "Ig": { "Method": "ANN", "Inputs": ["vgs", "vds"], "IgGs": "IgGs", "IgGd": "IgGd", "Activation": "Sigmoid",
            "Domain": { "Polygon": [ … ] } },
    "Qg": { "Method": "ANN", "Inputs": ["vgs", "vds", "T"], "Network": "Qg", "Activation": "Tanh",
            "Domain": { "Polygon": [ … ], "Box": { "T": [25, 125] } } },
    "Qd": { … }
  }
},
"Extrapolation": { "Gx": { "Id": 2.0, "IgGs": 0.1, "IgGd": 0.1 } }
```

- A **table** `name` is `arrays/name.values.npy` (the grid, one axis per input, named for it and carrying
  its sites) and `arrays/name.coefficients.npy` (the spline's coefficients, one axis per input at its
  Greville abscissae). `ComposedArrays.Table` builds both; `TensorSplineFit.TableArrays` fits and builds.
- An **ANN** `name` is `name.inputOffset`, `name.inputScale` (`x̂ = (x − offset)·scale`), `name.W1 … name.WL`
  (`outputs × inputs`) with `name.b1 … name.bL`, the last layer being the single linear output, and
  `name.outputOffset`, `name.outputScale` (`y = offset + scale·ŷ`). `ComposedArrays.Ann` builds them.
- `Ig` names two payloads, `IgGs` and `IgGd`, under one method, one activation and one domain.
- **`Transform` (MC-7)** on a table or ANN current: `{ "Kind": "Asinh", "Scale": I0 }` — the payload computes `u`
  and the current is `I0·sinh(u)`, its gradient and Hessian-along-a-direction by the chain rule (`AsinhOutput`), so
  guided extrapolation continues the current itself. It exists for the gate current (§9.3).
- **`Core.Dispersion` (MC-7)**: `{ "Tau": τ }`, present exactly when `Core.Constituents.IdRF` is; `Id` is then
  `IdDC`. `IdRF` takes a `G_x` like `Id`.
- **`Drift` (MC-8)**, [I11]'s decomposition, on `Qg` and `Qd` together (§10.3). `Qg` names `QGS`, `QGD` and `Qdrift`,
  `Qd` names `QGD`, `Qdrift` and `QDS` (the same two networks) and carries `Parameters: { "Lambda": λ }`; each piece is
  an ANN of its own variables. The `Domain` is the data's hull, and its `Box` holds each piece's training interval,
  keyed `QGS`, `QGD`, `QDS`, `Qdrift.vgd`, `Qdrift.Id`; outside it a piece is continued to first order
  (`BoxContinuation`).

### 3.2 What the validator refuses

Every refusal is a `CmodelFormatException` whose message begins with the field: `Core.Constituents.Id.Method:
'Table' is not a known method; the known methods are Equation.`

- `FormatVersion` above the reader's — "written by a newer circuitRF".
- `Kind` other than `Fet`; `Terminals` other than `G D S`.
- `BasePeriphery.Fingers` below 1; `UnitWidth` not a positive number with a length unit.
- `Temperature.Mode` other than `Single`/`Range`; `Tmin ≥ Tmax`.
- `Network.Netlist`: not exactly one `define` block; not exactly six distinct pins; a pin named `0`; an
  element outside the block; any line the `.cnl` reader does not understand; a variable that does not
  resolve to a finite real number; an internal net named `gi`/`di`/`si`, or an element named `core`
  (the names the synthesised cell gives the intrinsic pins and the core).
- `Network.Scaling.<var>`: an unknown rule, by name; a variable the network does not have.
- A constituent: an unknown `Method`, by name; an input listed twice, or other than `vgs`/`vds` (and `T`
  for a `Range` model) — `T` on a single-temperature model and `phi1`/`phi2` without traps say why. `Ig`
  must carry exactly `IgGs` and `IgGd`; the others their method's key (`Expression`, `Table`, `Network`).
- An equation: an expression that does not parse or names something that is neither an input nor a
  parameter; a parameter sharing an input's name or not finite; a `Domain`, `Smoothing` or `Activation`
  (an equation is global and is not extrapolated).
- A table or ANN: inputs without both `vgs` and `vds`; any `Parameters`; a missing `Domain`; a polygon of
  fewer than three vertices, with no area, or not convex; a `Box` missing an input other than `vgs`/`vds`,
  naming one it does not have, or with `lo ≥ hi`; a missing or misshapen array, named; a value that is
  not finite. A table additionally: an axis not named for its input in order, fewer than two sites, sites
  not strictly increasing, coefficients that do not match the grid, a domain reaching outside the grid,
  a negative `Smoothing`, an `Activation`. An ANN: no hidden layer or more than four, more than 64 neurons
  in one, an output layer wider than one, a zero input scale, a `Smoothing`.
- `Thermal`: on a `Single` model ("self-heating needs a model over a temperature range"); both `Rth`/`Cth` and a
  `Ladder`, or neither; an unknown key; a pole count outside 1–5; an `Rth` or `Cth` that is not positive; an
  `RthSlope` that makes the thermal resistance non-positive inside `[Tmin, Tmax]`; a network element named `Rth`,
  `Cth` (`Rth1` … for a ladder) or a net named `t` (`t_1` …), the names the synthesised cell gives the thermal network.
- `Extrapolation.Gx`: missing for a table or ANN current, present for an equation current or an unknown
  key, or not a positive number. `Core.Symmetry`: anything but `None`/`Exchange`.
- `Transform`: on an equation or a charge; a `Kind` other than `Asinh`; a `Scale` that is not a positive current.
  `Core.Dispersion`: missing with `IdRF`, present without it, or a `Tau` that is not a positive time.
- `Drift`: on a current; on one charge and not the other; inputs other than `vgs, vds`; a missing or extra piece; a
  `QGD` or `Qdrift` naming a different network in `Qg` and `Qd`; a parameter other than `Qd`'s `Lambda`, or a
  `Lambda` outside [0, 1]; a box missing a piece's interval or holding another key; a `Smoothing`.

## 4. The device

### 4.1 Elaboration as a cell (R-mc1-5)

`Elaborator.ComposedFetCell` intercepts a `ComposedFet` line before the primitive branch:

1. Three nets, or four when the model self-heats — the fourth is the thermal node `t` (§12.2); four on a model
   without `Thermal` is refused naming the file. Parameters `File`, `N`, `m` (or `M`) and `Symbol` (artwork,
   ignored); anything else is refused by name.
2. `File` resolves exactly as an SnP's (`BaseDirectory`, workspace-relative), with the same named
   refusals: none given, a folder, not found; an unreadable model names the validator's field.
3. The model loads through `ComposedModelCache` — keyed by full path, size and modification time,
   `TouchstoneCache`'s pattern — so a hundred instances read the file once (`LoadCountFor`).
4. `ComposedNetworkTemplate.Synthesize` builds the cell for this (file, N, m), cached per elaboration:
   every network variable resolved, multiplied by its rule's factor, and every element parameter that
   evaluates to a real number written into the instance **as that number**. Pins 1–3 keep the network's
   names and become the cell's ports; pins 4–6 become `gi`, `di`, `si`. One `ComposedCore:core gi di si
   File="<absolute>" N=<N>` is added — `gi di si t` for a self-heating model, with the thermal network's R and C
   from `t` to ground (§12.2), `t` the cell's fourth port when the instance binds a fourth net.
5. The cell flattens through `FlattenCell` like any sub-cell, so paths and probes are ordinary
   (`X1.rg`, `V(X1.gi)`, `X1.core`). It is never added to a library, the palette or a file.

### 4.2 The core (R-mc1-6)

`ComposedCoreModel`: nonlinear, two ports sharing the source — (gi, si) and (di, si), so `v = (vgs, vds)`,
the FET family's arrangement and the elaborator's same three-to-four net expansion.

- `i = (IgGs + IgGd, Id − IgGd)`, `q = (Qg, Qd)`, with `dg`/`dc` from the same dual-number evaluation, all
  times `N/N0`. A transcapacitance is kept.
- Constituents compile once per model (`ComposedCoreProgram`, `CompiledSddExpr.CompileNamed`) with the
  seeds `vgs`, `vds` (and `T`) in that lane order. `EvaluateInto` runs them through
  `CompiledSddExpr.EvalDualInto`, which writes the gradient into caller memory — no allocation per sample.
- A value or derivative that is NaN or infinite is a `ComposedEvaluationException` naming the model, the
  constituent and the operating point — never a silent zero.
- `InternalStateNodeCount` is the seam for MC-7, MC-9 and MC-10: that many ground-referenced pairs follow
  the two intrinsic ports, minted by the elaborator as `__composed_<path>_x<k>` with ordinary global rows
  (never eliminated locally — their harmonics matter in HB). MC-1 declares none.
- `Activity` is `ActiveExact` with `gm` (port 1 on port 0) as the controlled conductance, as for the
  built-in FETs.
- **Dispersion (MC-7, [P1] eq. 6.52).** A model with `IdRF` has one state node `x` (port 2, `(x, 0)`), whose voltage
  is a current in amps. The drain carries `IdRF + x − IgGd`; the node's port carries `i = x − (IdDC − IdRF)` and
  `q = τ·x`, so KCL there is `τ dx/dt + x = IdDC − IdRF` — at DC the drain carries `IdDC`, far above 1/τ its small
  signal is `IdRF`'s. Both currents are taken in the terminal frame, after the drain–source image, so `x` means the
  same on either side of `vds = 0`. `x` is unscaled; `N/N0` multiplies the drain current it feeds.
  `ControlledConductances` adds `(1, 2)`: zeroing it with `gm` leaves the drain a conductance.
- **Self-heating (MC-9).** A model with `Thermal` takes a fourth net, the thermal node, as its last port `(t, 0)` —
  a net of the cell, not a minted node, because the cell's `Rth ∥ Cth` and an instance's fourth pin attach to it.
  Every constituent is evaluated at `T = temp + ΔT`, `ΔT` the node's voltage, and the T lane's derivatives become
  that column of `dg` and `dc`. The port carries `−P`, `P = ig·vgs + id·vds` from the (scaled) terminal currents
  ([P1] eq. 6.67), with `∂P/∂v` from the same Jacobian. Every entry of the thermal row and column is a controlled
  conductance, so the passivated device is thermally decoupled.
- `ComposedCore` is also a `.cnl` token in its own right — the intrinsic device alone.
- A table or ANN payload compiles to a `RuntimeFunction`: its inputs gathered from the seed lanes, the
  evaluator run inside its `GuidedExtrapolation`, its gradient scattered back (zero in a lane it does not
  read).

### 4.3 Periphery scaling (R-mc1-7, D9)

`PeripheryScaling` holds the rules by name: `fixed` (×1, the default), `perFinger` (×N/N0),
`perFingerInverse` (×N0/N). A later rule adds a name and a factor. `N` must be a positive whole number;
anything else is refused with a sentence saying `N` is a finger count.

**The multiplier.** `m` identical devices in parallel multiplies every admittance by `m`. Inductors and
mutuals are branch elements, which the device multiplier refuses, so their values (`L`, `M`) are divided
by `m` instead; every other element, and the core, carries `m` and the engine's multiplier applies it.

**The thermal network (MC-9)** scales like any other element: `Rth × N0/N`, `Cth × N/N0` (D9), and both carry `m`. The
core's power scales by `N/N0` and by `m`, so `ΔT` depends on neither.

### 4.4 Temperature (R-mc1-8, D8)

A `Single` model evaluates with no `T`; a run whose `temp` differs from `T0` gets one warning per model
file per run, naming both. A `Range` model evaluates at `temp` clamped to `[Tmin, Tmax]`, with one warning
when it clamps. Both go through the netlist's own warning channel (`AddWarningOnce`). A self-heating model
adds `ΔT` to the clamped ambient; the junction temperature itself is not clamped, so beyond the training interval a
table or ANN is continued by its guided extrapolation and an equation is global. An `RthSlope` is taken at the
clamped ambient.

### 4.5 The symbol (R-mc1-9)

`SymbolKind.ComposedFet` (palette: Devices, with Nonlinear and DataFiles as filters) carries `File`, `N`
and `Symbol`. Its glyph is a virtual reference, `composedfet://<symbol>|<file>`, resolved by
`ComposedFetSymbolProvider` through the `CellSymbolResolver` seam SpiceModel and wBond use:

- the instance's `Symbol`, or the model's own when the instance's is blank;
- a library symbol by its kind's name, or a `.csym` relative to the `.cmodel` whose pins 1, 2 and 3
  exist — they bind to `g d s` by number;
- otherwise the standard FET glyph and pin geometry, with one extraction warning naming the symbol.

When `File` changes in the Inspector, the new model's `Symbol` is copied into the instance unless the
instance carries a symbol of its own choosing. `Symbol` never reaches the netlist.

### 4.6 `check`

`DocumentKinds` classifies `.cmodel` as `composed-model`; `check` calls `CmodelFile.Read` and reports
its sentence (error, exit 1) or a one-line summary (periphery and temperatures). It has no rule of its own.

### 4.7 Tables (R-mc2-1)

`TableEvaluator`: a tensor-product cubic B-spline over a rectilinear grid, non-uniform axes allowed.
The knot vector is not stored — `CubicBSpline.Knots` derives it from the sites by one rule the fitter and
the evaluator share:

- four or more sites: the **not-a-knot** spline (end knots of multiplicity four, every interior site a
  simple knot except the second and the second-to-last) — one coefficient per site, C² at every knot, and
  O(h⁴) error up to the ends, where a natural spline's zero-curvature end condition costs O(h²);
- two or three sites: one cubic piece over the axis, carrying the line or parabola through them exactly.

Value, gradient and the second derivatives come from the basis (Cox–de Boor with derivatives), never from
a difference. **Never bilinear**: its slope jumps at every knot, which is what makes table models wrong at
low drive ([P1] §6.2.6.2). Even a C² cubic has piecewise-constant third derivatives and none beyond, so a
table cannot predict high-order distortion at very low drive ([X06] §I); where low-level IM matters, an
ANN is the default.

`TensorSplineFit` (`src/Engine/Modeling/`) fits the coefficients once, when a model is written. It is
separable — one-dimensional fits along each axis in turn — and with a smoothing weight λ > 0 each axis
minimises `Σ (yᵢ − s(xᵢ))² + λ ∫ s″(u)² du` over the axis mapped to [0, 1], so one λ means the same on
every axis. The normal equations are banded (half-bandwidth three) and solved by a banded Cholesky factor
computed once per axis. λ = 0 is interpolation; an axis of two or three sites is never smoothed.

### 4.8 ANNs (R-mc2-2)

`AnnEvaluator`: affine input normalisation, one to four hidden layers of one to 64 neurons (`Tanh`, the
default, or logistic `Sigmoid`, [P1] Fig. 6.10), a linear output and its de-normalisation. The gradient
with respect to the inputs is carried forward through the layers alongside the value — the derivative
network of [P1] §6.2.7 — and, when asked, the Hessian along one direction with it. Scratch is on the
stack (64 neurons × 5 inputs), so the evaluator is immutable, thread-safe and allocates nothing.

### 4.9 Guided extrapolation (R-mc2-3)

Each table or ANN constituent stores its training domain (`TrainingDomain`): a convex polygon in
(`vgs`, `vds`) and an interval per other input. `GuidedExtrapolation` works in coordinates normalised by
the domain's extent along each input, so a distance mixes volts and degrees on equal terms. Inside, it
is the evaluator. Outside, with `p_b` the nearest domain point and `d = |p − p_b|`:

```
f(p) = f(p_b) + ∇f(p_b)·(p − p_b)                     charges stop here
     + G_x · (v_own − v_own,b) · d / d₀                currents; d₀ = 0.05 (5 % of the extent)
```

`v_own` is `vds` for `Id`, `vgs` for `IgGs`, `vgs − vds` for `IgGd`. The added term is a conductance on
the constituent's own branch that grows with distance and vanishes, with its derivative, at the boundary
([P1] Fig. 6.12b). `G_x` is stored per current in `Extrapolation.Gx`; `GuidedExtrapolation.DefaultGx` is
ten times the largest training conductance, for the phases that write models.

**The gradient is exact.** `p_b` moves with `p`, so the derivative of the linear continuation is
`∇f(p_b) + Pᵀ H(p_b)(p − p_b)`, `P` being the projection's Jacobian — `ê êᵀ` beyond an edge, zero in a
vertex's normal cone, one along an unclamped box axis. That is why the evaluators return the Hessian
along a direction. Value and gradient are continuous across the boundary; across a vertex's normal cone,
outside the domain, the gradient may jump. The polygon must be convex, or the nearest point — and with
it the value — would jump outside it.

Equations are not extrapolated: an equation is global by construction ([P1] §6.2.5.2).

### 4.10 Drain–source symmetry (R-mc2-4)

`Core.Symmetry: "Exchange"` (off by default — a field-plated power device is deliberately asymmetric,
[P1] §6.5.5). The constituents are read for `vds ≥ 0`; for `vds < 0` the core evaluates them at the image
`(vgs − vds, −vds)` ([P1] eq. 6.61) and transforms back: `Id` changes sign, `IgGs` and `IgGd` exchange
([P1] eq. 6.64), `Qg` carries over and `Qd = −Qg′ − Qd′` ([P1] eq. 6.63 in charge form). Each gradient
maps through the image's Jacobian (`∂/∂vgs = ∂′/∂vgs′`, `∂/∂vds = −∂′/∂vgs′ − ∂′/∂vds′`). Continuity at
`vds = 0` is a property of correct data; MC-7/MC-8 report the jump.

### 4.11 Grid evaluation (R-mc2-5)

`ComposedCoreModel.EvaluateGrid` writes an HB time grid straight into the caller's `GridResult` — no
per-sample arrays and no per-grid scratch (the base class's `EvaluateInto` loop allocates five buffers per
call, and HB calls once per Newton iteration). `ComposedCoreGridTests` holds it at zero bytes with an
allocation counter, never a timing.

## 5. The `.cmc` document and its data (MC-3)

### 5.1 The document (R-mc3-1, R-mc3-2)

`CmcFile` reads, validates and writes it in `CmodelFile`'s shape: a `JsonDocument` walk in which every
miss names its field (`DataSets[1].Conditions.Quiescent.Vgs: it is not a number.`), and a `Utf8JsonWriter`
in a fixed order with `\n` line ends — the same document writes the same bytes, and the writer validates
before it writes. **An unknown key anywhere is a refusal**: a misspelt condition silently dropped would
leave a set measured under conditions nobody stated. `Pads`, `Network` and `Steps` are the exception —
opaque `JsonNode`s, written back verbatim, so this build loses nothing a later phase wrote. `Panels` is
written only when a panel is hidden (railRF's rule).

Rules the validator holds: a Passive document takes only `PassiveSParams` sets and no periphery, an Active
one never takes them; names are unique; sources are relative; pulse conditions (`PulseWidth`, `Period`,
`Quiescent`, `SampleDelay`, `TrapState`, `PrePulse`) appear only on a pulsed kind, with `Period` longer
than the pulse and the sample inside it; `BiasPathOhms` only with the `instrument` plane. A condition a
later step needs is reported by that step, never here. `CmcFile.Warnings` is the one thing a well-formed
document still says: a pre-pulse dissipating ≥ 1 mW, whose heating is not modelled.

**Exclusions live in the document**, by stable point id, because the cache must hold nothing a re-import
cannot rebuild. `NormalisedData.WithExclusions` applies them; nothing excludes a point automatically.

### 5.2 The normalised form and the importers (R-mc3-3)

`NormalisedData`: per point a `BiasPoint` (`PointId`, `Vgs`, `Vds`, `Ig`, `Id`, ambient, intrinsic
voltages), and for S kinds `S[point, freq, i, j]` with one `Z0`, or a capacitance table `C[point, i, j]`.
**A quantity a source does not carry is NaN, never zero** — a zero gate current is a measurement.

`DataImport` — each importer refuses with its source and why, never a guessed bias or a skipped row:
- **Touchstone sets**: one S-parameter file per point, every file sharing one frequency axis and one real
  Z0. `BiasPattern` turns `Vgs{vgs}_Vds{vds}` into a regex: fields may state a unit (`{id:mA}`), whitespace
  matches any run, `*` matches anything; against a name it must match the whole stem, against comments it
  is searched in the header comment lines joined by spaces.
- **CSV/TSV**: delimiter inferred from the first row (tab, then `;` without `,`, then `,`); a cell's own
  unit wins over the column's, through `TunableValue.TryParse`.
- **`.npy` / `.mat`**: bias arrays are per point, or the axes of a grid whose current or table array is
  shaped `[vgs, vds]` (or `[vds, vgs]`, detected from the shape). A Y table converts to S at the import's
  Z0 — exact and invertible, so one normalised form serves both.

`MatReader` (`src/RfCore/Export`) reads MAT v5 — numeric matrices, 1 × 1 structs of them (`s.field`),
compressed elements, either byte order — and v7.3 through PureHDF. **Every array comes back in C order
over MATLAB's logical shape**: v5 is column-major and is transposed; a v7.3 dataset with a `MATLAB_class`
attribute is the same bytes with reversed HDF5 dimensions and is transposed too; an HDF5 dataset without
one (`MatWriter`'s own, h5py's) is already C order. Given a name set, an unrequested variable is skipped —
a compressed one without inflating past its name. Cell arrays, objects, sparse matrices and struct arrays
are refused by name when asked for (or when everything is).

### 5.3 The cache (R-mc3-4, R-mc3-6)

`CmcCache` keeps `<name>.cmc-cache/index.json` (per set: its `.npy`, a settings key — kind, sources and
import settings as the `.cmc` writes them — and per source path, size, modification ticks, SHA-256) and
one circuitRF `.npy` per set. **Open reads no source when size and time match** (`SourceReads` is the
counter); a moved size or time is hashed, and the same hash refreshes the index without re-importing. A
changed hash re-imports the set with one status line; a missing source with a cache warns and loads; a
missing source with no cache throws `DataImportException` naming it. A rebuild writes a whole sibling
folder and swaps it in by rename; failure or cancellation before the swap deletes it, so the old cache
survives untouched.

### 5.4 Hygiene (R-mc3-5)

`DataHygiene.Check` flags, never excludes: oscillation (|S11| or |S22| > 1, or K < 1 with |Δ| > 1, at or
below 2 GHz), compliance (a stated limit, or with none a plateau of ≥ 3 points within 10⁻⁴ of the set's
largest current), drift (a repeated bias within 1 mV, in one set or across sets of the same kind and
conditions, whose current differs by more than 2 % + 1 µA or whose S differs by more than 0.02). Every
tolerance is a `HygieneSettings` property. `DataHygiene.Coverage` is the convex hull of the non-excluded
(Vgs, Vds).

## 6. Pads, the network and the intrinsic data (MC-4)

### 6.1 The blocks (R-mc4-1, R-mc4-2)

`Pads` and `Network` stay `JsonNode`s on `CmcDocument`, written back verbatim, and `CmcExtrinsic` is their one
reader and writer: `CmcFile.Validate` parses both through it, so a misspelt key is refused by field like the rest.
`PadsSettings` is `Kind` (`None`, `OpenShort`, `TwoPorts`, `Network`) and the paths that kind needs; a path another
kind would read is refused, not ignored. `NetworkSettings` is the `Schematic`, per variable `Min`, `Max`, `Lock`,
`Tie` (`To`, `Factor`; a chain of ties is refused) and `Scaling` (a `PeripheryScaling` name), and an optional
`FlatnessBand`.

### 6.2 Networks as schematics (`PinNetwork`)

A network is read the way the application reads any schematic — `SchematicCircuit.Extract`, `CnlTextOf`,
`RoundTrip` — and solved by the engine as it is. Nothing here stamps a matrix.

- **Pins.** Exactly N `Pin` symbols numbered 1…N, each once, or a refusal listing what was found
  (`found 1, 2, 3, 4, 4, 6`). Two pins on one net are refused too: the extraction names a net once, so the second
  pin's name would reach no element. Join them through a `Short`.
- **Variables.** The VAR block, each with its value in base SI (the elaborator's `ResolvedGlobals`) and the kind of
  element whose value names it (R, L, C, mutual M, or other) — which is what its default bounds follow: `Min` 0 for
  the four, `Max` 100× the starting value. Overrides replace a variable's expression with its number before
  elaboration, so a trial point is a different circuit, never a patched matrix.
- **The coupling constraint.** `CouplingFactors` gives `|M|/√(L1·L2)` per mutual at any values; a fit holds it below
  1 as a constraint, never as a bound on `M`.
- **Pin 3 is ground.** Data is common-source, so the source pin is node 0 of every bench, as it is in the
  measurement and in any simulation that generated the data. Ports C (4–6) and D (5–6) float on pin 6, which the
  engine handles as any port with a reference.

`SixPinNetwork.Blocks` runs one S-parameter analysis with four 50 Ω ports and converts to Y (`RFNetwork.SToY`):
`Y_N = [[Y_aa, Y_ab], [Y_ba, Y_bb]]` per frequency. `SixPinNetwork.Dc` is the DC solve of R-mc4-4 — `LinearDcEngine`
with `Vdc` sources on pins 1 and 2 and `I_1Tone` DC sources (circuitRF's only DC current source) drawing from 4 and
5 into 6 — done once per unit source: the network is linear, so the four solves are its `DcTransfer` and every
point is a dot product. `PadNetwork` is the two-pin form for `Network` pads.

**Stock networks** (`StockNetworks`, resources `src/Design/Modeling/StockNetworks/*.csch`): GaN two-shell (D5's
values), classic single shell and series only. They were drawn from their netlists by `NetlistSchematic`, with six
numbered pins on labelled stubs. `Choose` copies one beside the `.cmc` (never over an existing file:
`-2`, `-3`, …) and writes a network block with every variable at its defaults and D9's scaling rules.

### 6.3 The algebra (`src/Engine/Modeling/NetworkAlgebra.cs`)

`C2`, a 2×2 complex matrix, carries everything: a FET's two ports, a pad, each block of the 4-port. Closed forms
throughout. The condition number is `σmax²/|det|` from the Frobenius norm and the determinant — no eigen-solve,
and no cancellation in `σmin`.

- Open-short: `Y_dev = ((Y_m − Y_o)⁻¹ − (Y_s − Y_o)⁻¹)⁻¹`.
- Two-ports: `T_dev = T_gate⁻¹ · T_meas · reverse(T_drain)⁻¹`, in ABCD from S directly (finite for a pure series
  or shunt pad, where Y or Z is not). Each pad file has port 1 at the probe.
- Through the network: `Y_int = Y_ba (Y_aa − Y_ext)⁻¹ Y_ab − Y_bb`, the exact inverse of
  `Y_ext = Y_aa − Y_ab (Y_bb + Y_int)⁻¹ Y_ba`. A point and frequency where the condition number of
  `(Y_aa − Y_ext)`, `Y_ab` or `Y_ba` exceeds 1e10 is flagged and left out of the flatness metric.
- A pad is interpolated linearly in its own form (Y or ABCD) between samples; outside its axis it is a refusal
  naming both ranges.

### 6.4 Re-referencing and the quantities (R-mc4-4, R-mc4-5)

A set's plane defaults to `probe`. `instrument` subtracts the bias-path drops (the source path carries `Ig + Id`);
`instrument` and `probe` then pass through a network pad's DC map; every plane but `intrinsic` then goes through the
network's `DcTransfer`. A missing gate current is taken as 0 A with one note; a missing drain current is a refusal
when anything between the stated plane and the core drops volts, because the answer would depend on it. A set that
carries intrinsic voltages of its own gets `max |ΔV|` in one note.

`IntrinsicQuantities.Of` gives `Gm`, `Gds`, `C11…C22`, `Cgs`, `Cgd`, `Cds`, `Cm` and `Tau = −Cm/Gm` from one
`Y_int`; `Flatness` is `(max − min)/|mean|` over the band, flagged frequencies left out.

### 6.5 The result and its cache (R-mc4-6, R-mc4-7)

`IntrinsicExtraction.Run` returns one `DataSet` per data set, on a `point` axis carrying the point ids:
`Vgs`, `Vds`, `Ig`, `Id` as measured and `Vgsi`, `Vdsi` computed (both clouds, for [P1] Fig. 6.5), `Excluded`;
for S data also `Y [point, freq, port, port]`, `Cond`, `Flagged`, a cube per quantity on `(point, freq)`,
`Flatness [point, quantity]`, the `Band` used, and `Notes` (a label axis) when there were any. `Compute` is the same
work for one set with no cache. `Run` solves the network at the variables' document `Value`s (MC-5) with the
caller's overrides on top, so a fitted value changes the key and reopens nothing stale.

The cache is `<name>.cmc-cache/intrinsic/<key>.npy`, the key a SHA-256 over the data's own `.npy` bytes, the set's
conditions and exclusions as the `.cmc` writes them, the pads block and each pad file's bytes, the network file's
bytes, the variable values and the band. An unchanged network reopens with no solve (`Computations` counts sets
computed). Entries are written only when every set is done — a cancellation writes nothing — each by a temporary
file and a rename, and entries no set uses are removed. `CmcCache`'s folder swap carries the subfolder across.
**Known gap:** a file a network schematic itself references (an `SnP` block) is not in the key.

## 7. Fitting the network (MC-5)

### 7.1 One least-squares problem (`NetworkFit`, R-mc5-1, R-mc5-2)

Every method is a residual vector over the network's free variables, the network solved by the engine at each
trial point (`SixPinNetwork.Blocks`). Three kinds of term:

- **(a) Flatness**, at the active points: each of `Re Y_ij` and `Im Y_ij/ω` of the de-embedded intrinsic Y about its
  own mean over the flatness band, divided by the point's admittance scale (its measured capacitance at the band's
  bottom, and `max(|G|, ω_mid·C)` for the conductances). A correct shell leaves the intrinsic device a lumped
  `G + jωC` ([P1] §5.6.3), so the minimum is the shell that makes it one.
- **(b) The cold-FET models** ([P1] §5.5.2), compared in the MEASURED domain by variable projection: each trial
  de-embeds the cold point, fits the cold core in closed form, embeds that core back and compares.
  - *Pinch-off*, Vds = 0: `Gm = 0`, `R_DS → ∞` and intrinsic `C_DS = 0` (the book's own assumption), so the core is
    `[[Cgs + Cgd, −Cgd], [−Cgd, Cgd]]`, linear in its two unknowns. Residual: Y per entry ÷ ω·C_scale — eq. 5.4's
    quantities, with the series elements kept, which is the only thing that separates an outer capacitance from an
    inner one on one device (the book separates them with a scaled array instead).
  - *Forward gate, extreme* (§5.5.2.4.1): the intrinsic device is one node, so the model is the network's `Y_aa` —
    its admittance with the intrinsic ports shorted. Residual: Z ÷ Z_scale (eq. 5.7's domain). Embedding through
    `Y_aa − Y_ab Z (I + Y_bb Z)⁻¹ Y_ba` keeps a zero core impedance finite.
  - *Forward gate, moderate* (Fig. 5.17): `Z_int = [[Z1 + Rch/3, Rch/2], [Rch/2, Rch]]`, `1/Z1 = 1/R1 + jωC1`
    linear in its unknowns, `R_ch` a document setting (a test structure's; a refusal when absent).
- **(c)** optionally, S of the band-mean `G + jωC` embedded, against the measured S at every frequency.

Each term's cost is its weight times its mean square (defaults `Flatness` 1, `Cold` 1, `SmallSignal` 0). A mutual is
solved at `|k| ≤ 0.999` — the engine is never handed a non-physical coupling, whose warning goes straight to stderr —
and the excess is a penalty residual that is zero inside. Bounds default to R-mc4-2's (`Min` 0, `Max` 100× start).
A tie is `value(To) × Factor`, applied whenever values are composed, so it holds exactly; a tied or locked variable
is not a coordinate. A locked variable's `Value` is the fixed value — typed, or taken from EM.

### 7.2 The points

A point is at Vds = 0 V within 20 mV. Pinch-off, when not named: the lowest Vgs whose low-frequency `Y22` is
capacitive (`Re < ½|Y22|`). Forward gate: the highest Vgs whose low-frequency `Y11` is resistive and whose gate
current, when measured, flows in. Active, when not named: off (lowest Vgs at the highest Vds), on (highest Vgs at
the lowest Vds), the knee (highest Vgs, the first Vds whose Id reaches 90 % of the saturated one; without currents
the Vds nearest a quarter of the highest) and saturation. The chosen points are written back into `Fit`, so the user
edits a choice rather than starting one. Cold-FET methods refuse a missing cold point naming the bias it needs; the
multi-bias fit notes a missing one and goes on.

### 7.3 Search (R-mc5-3)

Coordinates are the unit box: logarithmic for a positive quantity (from `Min`, or a millionth of `Max` when `Min`
is 0), linear otherwise; a free variable without both bounds is a refusal. **Cold-FET direct** alternates
Levenberg–Marquardt on the capacitances (pinch-off term, series held) and on the R, L and M values (forward term,
capacitances held) until a round moves nothing by 1e-7, at most eight rounds, then polishes both terms together —
the moderate core's `R1` against `Rg` is a valley the alternation zig-zags along (measured: cost 2.4e-3 stalled,
4.2e-8 with the joint polish). **Multi-bias** runs the registry's CMA-ES (default) or DE per start with seed
`Seed + start`, from the same start point, `Budget` evaluations each (default 200 per variable), polishes each
start's best with Levenberg–Marquardt and keeps the lowest cost. A batch is evaluated in parallel and told back in
order, so the same seed gives the same values on any core count. Progress is a stage per start; cancellation is
checked per batch and throws — nothing is returned, nothing written.

### 7.4 Identifiability (R-mc5-4)

At the solution: the residual Jacobian by central differences (in ln v for a logarithmic variable), `s² =
SSR/(m − n)`, `Cov = s²(JᵀJ)⁻¹` through the SVD of the unit-diagonal `JᵀJ` (a direction the data does not see is a
huge variance, not a failure). Per variable: the standard error, the relative error, the most-correlated partner and
`ρ`, the spread of the polished starts, and `at bound Min`/`Max` within 0.5 % of the box. **Poorly determined** is a
relative error above `PoorRelativeError` (0.2) or `|ρ|` above `PoorCorrelation` (0.95). Lines: `Cpgi poorly
determined (ρ = −0.98 with Cpga)` (or `(±35 %)`), `rg at bound Max (520)`, and one suggestion per variable or pair:
`Lock Cpga, or tie it to Cpgi × 1.43.` Measured on the GaN network: `ρ(Cpga, Cpgi)` is −0.999 with data to 2 GHz and
−0.70 with data to 40 GHz.

### 7.5 The result in the document (R-mc5-5)

`Run` returns `NetworkFitResult`; `Apply` writes each fitted and tied variable's `Value`, the resolved `Fit`
settings and the `Result` record (`Method`, `Cost`, per-variable `StdError`/`Spread`/`AtBound`, the report lines and
the `Digest` of the schematic path and values); `FitFile` reads, fits, applies and writes — what the window and the
`model` verb call. The intrinsic cache keys on the values, so it invalidates itself. A later step records the digest
it was computed through as `"Network"` in its `Steps` entry; `StaleSteps` names every entry whose digest differs.

## 8. Training an ANN (MC-6)

`src/Engine/Modeling/Ann/` trains MC-2's network and hands back the runtime `AnnEvaluator` itself, with its
`.cmodel` arrays (`AnnTrainingResult.Arrays`), so a trained network loads with no conversion. Which data a
constituent trains on, and its weights and anchors, belong to MC-7 … MC-10.

### 8.1 The problem (R-mc6-1 … R-mc6-3b)

`AnnTrainer.Train(AnnTrainingProblem, AnnTrainingSettings, RunControl?)`. A problem has 1–5 inputs and any of:
**values** (`AnnValueData`: points, targets, a weight per point), **derivatives** (`AnnDerivativeData`: points, the
input directions `j`, the targets of `∂f/∂x_j`, a weight per point and direction — 0 marks a component not measured)
and an **anchor** that fixes a derivative-trained function's constant. Values and derivatives together are [X03]'s
combined training (eq. 8), each over its own point set.

- **Normalisation.** Each input maps its range over every point onto [−1, 1]. The output maps the values' range onto
  [−1, 1]; with no values it is scaled so the derivative targets are of order one, and offset to the anchor's value.
- **The loss.** Each term is a weighted *mean* in normalised units — value residuals ÷ the output scale, derivative
  residuals ÷ the RMS of their targets — times `ValueWeight` or `DerivativeWeight`, so the term weights are
  dimensionless. A constant per term moves no minimiser: within a term it is `Σ w (f − y)²` and
  `Σ w (∂f/∂x_j − t)²` exactly, and non-conservative derivative data gets the least-squares compromise ([P1] §6.3.6
  ¶2). The anchor adds `Weight·((f(x₀) − y₀)/outputScale)²`; `L2` adds `λ·ΣW²` over the normalised weights.
- **The adjoint network** (`AnnObjective`). The input derivatives are carried forward with the value (the derivative
  network of MC-2); the loss is back-propagated through both networks at once — [X03] eq. 17's three paths:
  `z̄ = ā ⊙ σ′ + Σⱼ ūⱼ ⊙ σ″ ⊙ vⱼ`, `v̄ⱼ = ūⱼ ⊙ σ′`, `∂L/∂W += z̄ aᵀ + Σⱼ v̄ⱼ uⱼᵀ`. Never a finite difference.
- **Size.** Default 2 × 8 `tanh`. When the parameters exceed a tenth of the targets (one per value, one per
  derivative component, the anchor) the result carries one warning ([L26]).

### 8.2 The optimiser (R-mc6-4)

In-house **L-BFGS** (`Lbfgs`): two-loop recursion, memory 10, a strong-Wolfe line search with cubic-interpolation
zoom, a pair with unsafe curvature skipped, one fall-back to steepest descent before giving up. Stops on the
gradient's largest component, the relative loss change or the iteration cap (defaults 1e-9, 1e-14, 2000).
Initialisation is Glorot-uniform from `SplitMix64`, biases zero. `Restarts` (default 3) seeded starts; the start with
the lowest held-out loss is kept (training loss when nothing is held out).

### 8.3 Held-out error (R-mc6-5)

`HeldOutSplit`: each point's normalised coordinates are quantised and interleaved into a Morton key; the points are
sorted by that key (ties by the exact coordinates, then the targets — so by content, never by array position), cut
into as many contiguous strata as points are held out (default 10 %), and one point is drawn per stratum by the seed.
Values and derivatives are split separately. `AnnFitError` reports training and held-out RMS, absolute and relative to
the targets' range — computed through the runtime network itself. Early stopping (off by default) keeps the iterate
with the lowest held-out loss and stops after `EarlyStoppingPatience` iterations without a new one.

### 8.4 Determinism and long work (R-mc6-6, R-mc6-7)

Single-threaded training gives the same weights bit for bit on every platform: the points are summed in fixed blocks
of 64 in a fixed order, and `tanh`, the logistic function and `exp` are computed from IEEE arithmetic alone
(`DeterministicMath`) because the platform's `Math.Tanh` comes from its C library and differs in the last place
between operating systems. `Parallel` evaluates the blocks on several threads and is promised only to 1e-12 relative.
Progress is a stage per start and a tick per iteration on `RunControl`; a cancel throws `OperationCanceledException`
out of `Train`, so the caller keeps whatever network it had (D13).

## 9. The current constituents (MC-7)

`CurrentFit` (`src/Design/Modeling/`): `Run` from the open data and its intrinsic result, `Apply` into
`Steps.Current`, `FitFile` (read, open the cache, de-embed, fit, write — what the window and the `model` verb call),
`Load` (the record and its arrays back from the cache, or null to fit again). Settings and record:
`CurrentFitSettings`, `CurrentFitRecord`; templates: `CurrentTemplates`; parameter fits: `EquationFit`.

### 9.1 Training points (R-mc7-1)

The intrinsic points (`Vgsi`, `Vdsi`) of the chosen sets — by default every `DcIv`, `PulsedIv` and
`SimulatedTables` set — excluded points removed. One trap state (all DC, or all pulsed from one `Quiescent` and one
`PrePulse`); a mix is refused naming the condition. Temperatures (set `AmbientC` and per-point `T`, within 0.01 °C)
may differ: two or more make `T` an input (§12.1). A point with no drain current is left out, one with no gate current is left out of `Ig` only; with no
gate current at all `Ig` is the equation `0`.

**The gate split.** `Sign`: for `vds ≥ 0` a positive (forward) gate current is all `IgGs`, a negative one all `IgGd`
(breakdown at the drain side); for `vds < 0` the roles exchange. At `vds = 0` with the gate forward, both junctions
conduct and the split gives it all to `IgGs` — the terminal currents are still right, since the drain terminal
carries `Id − IgGd` and the channel target is **`Id_terminal + IgGd`**. `GateSource`/`GateDrain` put it all in one.

The training domain is the points' convex hull (`DataHygiene.Coverage`), refused when it has no area.

### 9.2 Methods (R-mc7-2)

Every method holds out the same points (`HeldOutSplit` over the domain, `HeldOutFraction`, seeded), so errors
compare; a network is trained on the rest with no split of its own.

- **ANN** (default, 2 × 8 `tanh`, 3 starts, 6000 iterations): MC-6 value training. `Id` weighs a point
  `1/max(|Id|, IdFloor·max|Id|)²`. Measured on the synthetic device: `IdFloor` 0.1 gives 0.6 % worst held-out error
  over the range where 0.01 gives 2.7 % for no better sub-threshold fit and uniform weights 0.1 % with sub-threshold
  errors three times larger; the stiffness of relative weights also needs ~6000 L-BFGS iterations (2000 left 5.6 %).
- **Table**: the scattered points re-gridded on the hull's bounding box at `GridStep` (exactly; the last site at or
  beyond the hull), then MC-2's spline. The source is a network trained on the training points — continued
  **linearly** outside the hull, never with its `G_x` term, which grows with the square of the distance and made the
  spline ring back into the boundary cells (7.7 % against the network's own 1.5 %) — or `Regrid.Rbf` through the
  points (multiquadric, coordinates normalised over the points). A step over twice `SmallestSwing` (0.05 V) reports
  `table step 0.2 V: low-level IM below ~0.1 V swing unreliable` ([P1] Fig. 6.8).
- **Equation**: a template — the `Id` laws of the built-in Angelov, Curtice quadratic and cubic, Statz and Materka
  FETs, copied as expressions (no temperature coefficients, n-channel), and a `Diode` gate — or the user's own, its
  parameters fitted by the registry's `cmaes` (or `de`) then Levenberg–Marquardt over the unit box (logarithmic for
  a positive range of two decades), weighted as above. The expression compiles once with `vgs`, `vds` and the free
  parameters as seed lanes, so at most 14 are free. Each template estimates its start from the data (peak `gm`, the
  threshold, the knee). The gate's two branches share one parameter list, so a template's free parameters are
  suffixed `Gs` and `Gd`.

### 9.3 The gate current

**Weighting alone cannot fit a gate current in a network or a table.** With a linear output and `1/|Ig|²` weights,
the ~160 near-zero reverse points outweigh the handful of forward ones by ten orders of magnitude and the forward
conduction is not fitted at all (8 % of its range, held-out points 2–3 decades off). So a table or network `Ig` is
fitted in `u = asinh(Ig/I0)`, unweighted — an equal error in `u` is an equal ratio above `I0` — and exported with
`Transform: Asinh, Scale: I0`, `I0 = IgFloor·max|Ig|` (default 1e-4: 0.17 decades at the faintest held-out points
where 1e-6 gave 1.1). An equation `Ig` already has the decades in its form and is fitted to `Ig` weighted by
`1/max(|Ig|, I0)²`.

### 9.4 Domain, symmetry and the report (R-mc7-3, R-mc7-5)

A table or ANN current carries the hull as its `Domain` and `G_x` = `GuidedExtrapolation.DefaultGx` of the largest
own-branch conductance over the training points (never below 1e-12 S). With `Symmetry: Exchange` only `vds ≥ 0`
points are used and the record carries the jumps at `vds = 0` over the domain's `vgs`: `2·Id(vgs, 0)` (value),
`∂Id/∂vgs(vgs, 0)` (the slope in `vds` jumps by exactly that) and `IgGs − IgGd` (the gate branch). The report also
has, per current, training and held-out RMS error, absolute and relative to the targets' range, and `Conductance`:
the fitted `Gm`/`Gds` against the data's DC derivatives from `LocalGradients` (a local quadratic through the 12
nearest points in normalised coordinates), RMS and relative.

### 9.5 DC/RF dispersion (R-mc7-4)

Off by default; the window presents it as the simpler alternative to modelling heating and traps (MC-9, MC-10),
which [P1] prefers. `IdRF` is a network trained by MC-6 **derivative** training on the band-mean intrinsic `Gm` and
`Gds` of the S sets (flagged frequencies left out), anchored to `IdDC` at `Reference` (default: the S point nearest
the middle of the S data). `Tau` defaults to 1 µs ([X06]). **Eq. 6.49's residual** is the share of the RF
conductances no single current reproduces — RMS `|∇IdRF − (Gm, Gds)|` over RMS `|(Gm, Gds)|` — zero when 6.49 holds
(measured 0.08 % on conservative data). Testing 6.49 by differencing the conductances was tried and dropped: on a
0.3 V grid it called exactly conservative data 86 % non-conservative.

### 9.6 The record and the cache

`Steps.Current` = `{ Network, Settings, Result }`. `Result.Constituents` are written by
`CmodelFile.ConstituentJson`, so the `.cmc` and the `.cmodel` cannot spell one differently; the arrays are one
grouped `.npy` in `<name>.cmc-cache/current/<key>.npy`, `key` a SHA-256 of their bytes, written by a temporary file
and a rename after the fit, other entries removed. `CmcFile.Validate` parses `Steps.Current` (an unknown key is a
refusal by field); other steps stay opaque. `NetworkFit.StaleSteps` names the step when the network moves on.

## 10. The charge constituents (MC-8)

`ChargeFit` (`src/Design/Modeling/`): `Gather` (the capacitance data, a `ChargeData`), `Run` from the open data or from
a `ChargeData`, `Apply` into `Steps.Charge`, `FitFile`, `Load`, and for the report and the window `Model` (the fitted
charges and currents as a `ComposedModel`), `Capacitances` and `Slice` (each `Cij` along one voltage with the other
fixed — MC-15's 1-D plots). Settings and record: `ChargeFitSettings`, `ChargeFitRecord`; templates: `ChargeTemplates`.

### 10.1 Training data (R-mc8-1)

Every set with intrinsic S (or those named), one trap state as for the currents; two or more temperatures make `T` an
input of an ANN charge (§12.1). At each point,
`C11 … C22` averaged over the flatness band with flagged frequencies left out. A point whose worse **diagonal**
flatness (`C11` or `C22` — the diagonals never cross zero, so theirs is always defined) exceeds `FlatnessLimit`
(0.1) weighs `(limit/flatness)²`, and the report counts them. Both charges are zero at the anchor: `(0, 0)` or the
stated point, moved to the nearest data point when it lies outside the hull.

### 10.2 Methods (R-mc8-2)

- **ANN** (default, 2 × 8 `tanh`, 3000 iterations): MC-6 derivative training, `∂Q/∂vgs → Ci1`, `∂Q/∂vds → Ci2`,
  anchored. Domain: the hull; charges are continued linearly (`G_x = 0`). Measured on the synthetic device through
  the GaN network: held-out capacitances within 0.49 % of the row's largest, the charges within 0.06 % of their range.
- **Table**: each capacitance interpolated over the points, both paths of [P1] Fig. 6.16 integrated onto the hull's
  bounding grid (`LineIntegral.OntoGrid`, adaptive Simpson, cumulative over the sites), `Path` A, B or their mean
  (default) kept as MC-2's table; the RMS of `Q_A − Q_B` over the charge's range is recorded. **The interpolant is a
  network by default**: an RBF through each capacitance alone is exact at the points but the two are not mutually
  conservative between them, and the integrals carry that across the domain — C11 fitted to 15.6 % of its range
  through an RBF, 0.34 % through a network.
- **Equation**: `Depletion` ([P1] eq. 6.11 for a gate–source and a gate–drain junction, `min`/`max` so neither branch
  is evaluated outside its range, `Fc` locked at 0.8) or `Angelov` (each junction Angelov's `Cp + C0·(1 + tanh ψ1)·(1 ± tanh ψ2)` —
  a constant plus a step from 0 to `2·C0` in its own voltage, switched by `tanh` in `vds` — integrated along that
  voltage as `Cp·v + C0·(v + ln(cosh ψ1)/P1)·(1 ± tanh ψ2)`), or the user's own; fitted by
  `EquationFit` with **derivative terms** (the expression's gradient lanes are the targets), then shifted by a
  parameter `Qanchor` so it is zero at the anchor.

### 10.3 The drift decomposition (R-mc8-3)

`Qg = QGS(vgs) + QGD(vgd) + Qdrift(vgd, Id)`, `Qd = −QGD(vgd) − (1 − λ)·Qdrift(vgd, Id) + QDS(vds)` ([I11] eq. 3).
`Id` is the current step's channel current as the core evaluates it (`ComposedCoreProgram.Channel`: `IdRF` when
dispersive), and the core takes `∂/∂v` through it by the chain rule, so the charges inherit whatever the current
depends on — when MC-10's traps arrive, `Qg` depends on the trap states through `Id`, D10's one exception.

1. `QGS` and `QGD`: 1-D adjoint fits to `Cgs = C11 + C12` and `Cgd = −C12` at the points with `|vds| ≤ VdsZero`
   (0.05 V) — [I11] §III defines the depletion charges as what is measured there.
2. `Qdrift`: with `R1 = C11 − QGS' − QGD'` and `R2 = C12 + QGD'`, the decomposition says `R1 = ∂Qdr/∂vgd + τ·gm` and
   `R2 = −∂Qdr/∂vgd + τ·gds`, so **exactly** `τ = (R1 + R2)/(gm + gds)` ([I11] eq. 4's first line) and
   `∂Qdr/∂vgd = (R1·gds − R2·gm)/(gm + gds)`. [I11] eq. 4 prints the second as `½(Cii-sum terms)`, which drops
   `τ·(gm − gds)/2`; the exact form is used. A point weighs `(gm + gds)²` relative to the largest — the solve's
   conditioning — and `Qdrift = 0` along `Id = 0` is 21 value targets trained with the derivatives.
3. `QDS`: a 1-D fit to `C22 + C12 − λ·∂Qdrift/∂vds` at every point. Its residual is the spread of `Cds` across `vgs`
   at fixed `vds` — what the decomposition cannot represent — recorded as `CdsSpread` and reported in one line.

It needs the current step (refused otherwise). A training problem whose values are all one number now takes its
output scale from its derivatives (`AnnTrainer`), since zero targets carry no size.

### 10.4 The report and the record (R-mc8-4)

Per capacitance, the RMS of the model's `∂Q/∂v` against it over every point, absolute and as a fraction of its range.
`Steps.Charge` = `{ Network, Settings, Result }` as `Steps.Current`; arrays in `<name>.cmc-cache/charge/<key>.npy`.

## 11. Check Charge Conservation (MC-8)

`ConservationCheck` (`src/Design/Modeling/`), numerics `CurlMetric` and `LineIntegral` (`src/Engine/Modeling/`). Each
part returns a number and, where it has one, a map; `RunFile`/`Run` do all three for a `.cmc`, read-only.

- **(a) The data** (`Data`): per row the curl `∂Ci1/∂vds − ∂Ci2/∂vgs` by central differences of a smooth interpolant
  — a network per capacitance by default — normalised by the RMS over the points of `√((t1² + t2²)/2)`, as a map
  and an RMS; and the two-path difference over the grid sites inside the hull, as a fraction of the charge's range.
  An RBF interpolant read 33 % curl on exactly conservative synthetic data (its errors at the hull's edges, divided
  by terms that are small); the networks read 0.33 % and 1.5 %. **The curl is read only away from the hull's edge**
  (`EdgeMargin`, 2 % of each axis' span): a value interpolant's derivative across its edge is constrained from one
  side only, and on conservative depletion data over a rectilinear grid the network read 0.43 % inside and 54 % on
  the edge — a third of a grid's points, and an RMS of 32 % that reported a defect the data does not have. Edge
  points are NaN in the map; a hull with no interior point is read everywhere.
- **(b) The model** (`Model`): the same curl on a capacitance matrix over a grid (step 0.05 V), differenced a tenth of
  the step apart. On a device in a `.cnl` (`DeviceCapacitance`): the instance's one nonlinear component, ports 1 and
  2 gate–source and drain–source sharing a source, any further ports its own state nodes — solved at DC, eliminated
  at the check's frequency, `C = Im Y / ω` — so a capacitance written through an auxiliary node is read as a
  charge's `∂q/∂v` is. The transcapacitance's RMS is reported as information ([P1] §6.3.8).
- **(c) Large signal** (`LargeSignal`, `Drive`): the device alone on a testbench of ideal tone sources through 1 mΩ
  (an ideal source on an HB interface node has no finite admittance), solved by HB at order ≥ 5 at `f` and `2f`;
  `ΔIdc = Igate,dc(2f) − Igate,dc(f)`, relative to the fundamental gate current at `f`, and one line through the gate
  bias resistor. **The drain is driven too, in quadrature** (`DrainAmplitude`, 1 V): with the drain held at a
  constant voltage the trajectory is a line in `(vgs, vds)`, `∮C11 dvgs = 0` along it for any `C11`, and no
  capacitance makes DC — the check as first written could not fail. An ellipse encloses area, the same ellipse at
  every `f` makes the displacement DC exactly `f·∮C·dv`, and conduction DC cancels in the difference.
- The composer's model is run as a `ComposedFet` of a temporary `.cmodel` whose network joins each pin to its
  intrinsic one through 1 µΩ (`ChargeFit.IntrinsicNetwork`), with zero currents until the current step has run.

## 12. Temperature and self-heating (MC-9)

`ThermalFit` (with `TemperaturePlan`, `ThermalHeating`), `ThermalFitSettings` (with `ThermalFitRecord`); the
`.cmc` block is `Steps.Thermal`.

### 12.1 Temperature as an input (R-mc9-1, R-mc9-4)

`ThermalFit.Plan` takes the distinct ambient temperatures (within 0.01 °C) of every I–V and S set. **One**: the
model is `Single` at `T0`, no constituent reads `T`, self-heating is unavailable and the reason is the only text —
"one temperature" — and a run at another `temp` warns once (§4.4). **Two or more**: the current step's points carry
`T` and every table or ANN constituent gains it as a third input — an ANN's third input, a table's third axis (spline
in `T` too, sites every `TemperatureStep`, 25 °C by default; such a table re-grids through a network, the RBF being
two-dimensional), and an equation may read `T`. A template's `TemperatureCoefficients` names parameters made linear
in temperature, `p → (p + p_T·(T − Tnom))`, `p_T` free from 0 within the parameter's own range over the data's span,
`Tnom` the lowest temperature, locked. The domain's `Box` holds `T`'s training interval;
`ThermalFit.ModelTemperature` gives the `Range` covering it and every ambient.

What `T` is for each point:

| Data | `T` |
|---|---|
| pulsed, from a quiescent bias that dissipates nothing (or states no current) | the chuck temperature |
| pulsed, from a dissipating quiescent bias | chuck + `Rth·Pq` once `Rth` is known; until then the chuck, with one line |
| DC, self-heating modelled | the junction temperature `Tamb + Rth(Tamb)·P`, `P = ig·vgsi + id·vdsi` |
| DC, self-heating not modelled | `Tamb`, with one line: the DC heating is embedded statically |

The charges gain `T` only when their S data spans two or more temperatures ([X10] eqs. 1.1, 1.5), each S point at its
ambient (its DC junction temperature with self-heating); otherwise one line says they are temperature-independent.
Only an ANN charge takes `T` (one line names a Table, Equation or drift charge fitted over every temperature's points
instead). The adjoint problem then has three inputs: the capacitances constrain `∂Q/∂v`, and the charge's zero holds
at the anchor bias at **every** temperature — `∂Q/∂T = 0` at nine temperatures along it plus the value at the middle —
because a charge moving with `T` there would carry a displacement current `dQ/dT·dT/dt` nobody measured.

With self-heating an ANN `Id` trains by MC-6's combined mode: the I–V values at each point's `Tj`, and every
SParams point's band-averaged RF `Gm`/`Gds` as `∂Id/∂vgs`, `∂Id/∂vds` at `(vgs, vds, Tj)`, `Tj` frozen at the point's DC
value ([X03] §IV-C, [X10] §VI) — so one function gives both of [P1] eq. 6.69's output conductances. A Table or
Equation `Id` trains on the values only, with one line. The Gm/Gds-against-DC-slopes report is skipped (a self-heated
DC slope includes the heating); without self-heating it compares each temperature's points among themselves.

### 12.2 The thermal node (R-mc9-2)

The convention is the compact-model one for an exposed thermal terminal: the node whose voltage is `ΔT` carries the
power, and the model's own network goes from it to thermal ground — [P1] Fig. 6.32's `Rth ∥ Cth` from `Tj` to `T0`;
[R11] ch. 7's fourth terminal "loaded by the thermal impedance"; [T20] §4.3's lumped network on the model's thermal
node. So **a network on pin 4 sits in parallel with the model's own** (`Rth ∥ Rext` at DC): the brief's gate asked
for "series", and the owner chose the convention the references and the industry use.

`ComposedNetworkTemplate.Synthesize` adds `R:Rth t 0` and `C:Cth t 0` (one pole), or a Foster ladder `t —(Rth1 ∥ Cth1)—
t_1 — … —(Rthn ∥ Cthn)— 0`, values scaled by D9 and `RthAt(temp)/Rth`. With four nets `t` is the cell's fourth port;
with three it is an internal net, still probed as `V(X1.t)`. A ladder's impedance is `Σ Ri/(1 + jωRiCi)`.

### 12.3 Rth (R-mc9-3)

`ThermalFit.Run` with `SelfHeating` and no typed `Rth`:

1. **The isothermal model.** `CurrentFit.IsothermalId` trains `Id(vgs, vds, T)` on the pulsed sets (default: every
   PulsedIv set whose quiescent bias does not dissipate; a dissipating one named is refused) by `Isothermal` (default:
   the current step's `Id` method); fewer than two chuck temperatures is a refusal that says to type `Rth`.
2. **Each DC point's junction temperature** — the `T` at which that model gives the point's channel current
   (`id + IgGd` by the current step's gate split): a 5 K scan from 50 K below the ambient to 500 K above, then
   bisection to 1 mK at the first sign change. No root: unresolved, counted.
3. Points below `MinPower` (0.1) of the largest DC power are left out and counted — their rise is not resolvable
   against the isothermal model's error.
4. **The fit**: `Tj − Tamb = Rth·P` through the origin, or (`RthModel: Linear`) `(Rth + slope·(Tamb − Tref))·P`,
   `Tref` the lowest DC ambient. The record keeps the RMS residual in K and relative to the RMS rise, the counts, and
   the vgs, vds and power spans of the points used.

A typed `Rth` skips all of it, and the current step "de-heats" the DC data by putting each point at its `Tj` — which
needs two or more ambients for `T` to be an input at all.

**The dynamics** are one of: `Cth`; `TauTh` (`Cth = τ/Rth`); a `Ladder` of `{ Fraction, Tau }` (`Ri = f·Rth`,
`Ci = τ/Ri`, fractions adding to 1); or `FitTau` — one `τ` for every S point's `Re Y22(f) = a + b/(1 + (2πfτ)²)`, each
point's `a` and `b` solved exactly per trial `τ`, searched in log `τ` (a 61-point scan, then golden section). `FitTau` is
refused while `Steps.Traps.Settings.Model` names a trap model: in GaN that roll-off also holds trap time constants,
measured near equilibrium ([N22] §III). `ThermalFit.TauFitNote` is the window's tooltip for it. Without any of them
the record has no poles, one line says so, and `ThermalFit.Network` gives no thermal network.

Every long part takes the `RunControl`; `FitFile` writes the document only when the run completes (D13).

## 13. Where it lives

| Path | What |
|---|---|
| `src/Core/Devices/Composed/` | `ComposedModel` (records), `CmodelFile`, `ComposedNetworkTemplate`, `ComposedCoreProgram`, `ComposedModelCache`, `ComposedCoreModel`, `PeripheryScaling`; MC-2: `CubicBSpline`, `RuntimeEvaluators` (`TableEvaluator`, `AnnEvaluator`), `GuidedExtrapolation` (with `TrainingDomain`), `ComposedArrays` (the payload arrays, built and validated) |
| `src/Engine/Modeling/TensorSplineFit.cs` | fitting a table's coefficients, with smoothing |
| `src/Core/Elaboration/Elaborator.cs` | `ComposedFetCell`, `ResolveComposedParameters`, the core's node expansion and temperature warnings |
| `src/Design/Schematic/ComposedFetSymbolProvider.cs` | the symbol rule above |
| `src/Cli/Check.cs`, `DocumentKinds.cs` | `check` on a `.cmodel` and a `.cmc` |
| `src/Design/Modeling/` | MC-3: `CmcDocument`, `CmcFile` (with `BiasPattern`), `NormalisedData`, `DataImport`, `CmcCache`, `DataHygiene` |
| `src/RfCore/Export/MatReader.cs` | MAT v5 and v7.3 |
| `src/Design/Modeling/` | MC-4: `CmcExtrinsic`, `PinNetwork` (with `ModelingException`), `SixPinNetwork` and `PadNetwork`, `StockNetworks` and `StockNetworks/*.csch`, `PadDeembedding`, `IntrinsicExtraction`, `IntrinsicQuantities` |
| `src/Engine/Modeling/NetworkAlgebra.cs` | MC-4: `C2` and the conversions and de-embeddings |
| `src/Design/Modeling/` | MC-5: `NetworkFit` (with `NetworkFitProblem`, the residual terms, the drivers and the report), `NetworkFitSettings` and `NetworkFitRecord` |
| `src/Engine/Modeling/Ann/` | MC-6: `AnnTrainer` (with its problem, settings and result records), `AnnObjective` (the loss and its adjoint gradient), `Lbfgs`, `HeldOutSplit`, `DeterministicMath` |
| `src/Design/Modeling/` | MC-7: `CurrentFit`, `CurrentFitSettings` (with `CurrentFitRecord`), `CurrentTemplates`, `EquationFit` |
| `src/Engine/Modeling/` | MC-7: `Regrid` (grid axes, sampling, the RBF source), `LocalGradients` |
| `src/Core/Devices/Composed/` | MC-7: `ModelDispersion` and `OutputTransform` (records), `AsinhOutput` (evaluator), the state node in `ComposedCoreModel` |
| `src/Design/Modeling/` | MC-8: `ChargeFit` (with `ChargeData`), `ChargeFitSettings` (with `ChargeFitRecord`), `ChargeTemplates`, `ConservationCheck` (with `DeviceCapacitance`) |
| `src/Engine/Modeling/LineIntegral.cs` | MC-8: `LineIntegral` (paths A and B onto a grid), `CurlMetric` |
| `src/Core/Devices/Composed/` | MC-8: `DriftCharges` (with `BoxContinuation`); `ComposedCoreProgram.Channel` and `.Charges` |
| `src/Core/Devices/Composed/` | MC-9: `ModelThermal`, `ThermalPole` (records); the thermal port in `ComposedCoreModel`; the thermal network in `ComposedNetworkTemplate.Synthesize` |
| `src/Design/Modeling/` | MC-9: `ThermalFit` (with `TemperaturePlan`, `ThermalHeating`), `ThermalFitSettings` (with `ThermalFitRecord`); `T` through `CurrentFit`, `EquationFit` and `ChargeFit` |

## 14. Gates (MC-1 … MC-9)

`tests/Core.Tests/Composed/CmodelFormatTests`; `tests/Engine.Tests/Composed/` — `ComposedFetEquivalenceTests`
(against an SDD with the same expressions in the same network: DC to 1e-12, S at three biases to 1e-9, HB
harmonics to 1e-9), `PeripheryScalingTests`, `ComposedFetProbeTests`, `ComposedFetTemperatureTests`;
`tests/Ui.Tests/ModelComposer/` — `CheckCmodelTests`, `ComposedFetSymbolTests`. The shared device is
`tests/Core.Tests/Composed/Support/ComposedTestModels.cs`, linked into the other two projects.

MC-2: `tests/Core.Tests/Composed/AnnEvaluatorTests` (value, gradient and Hessian-along-a-direction
against finite differences, both activations); `tests/Engine.Tests/Composed/` — `TableEvaluatorTests`
(the cubic's error bound, C² across a knot, λ = 0 is interpolation), `GuidedExtrapolationTests`
(continuity across three edges, the exact Jacobian outside edges and vertices, 10⁴ finite points over ±10×
the domain, a DC solve from 2.5 extents outside landing on the in-domain answer), `DrainSourceSymmetryTests`
(eq. 6.55 at the terminals in both half-spaces, the swapped-terminal circuit), `ComposedCoreGridTests`
(zero bytes per grid; an ANN model against its equation twin in DC, S and HB to 1e-9). Their models are
`tests/Engine.Tests/Composed/Support/RuntimeTestModels.cs`: hand-set networks that also write themselves
out as the expression they compute, and a table-and-ANN model over a pentagon with an oblique edge.

MC-3: `tests/Ui.Tests/ModelComposer/` — `CmcFormatTests`, `TouchstoneSetImportTests`, `CmcCacheTests`,
`DataHygieneTests`; `tests/RfCore.Tests/Export/MatReaderTests` (a v5 file the test writes byte by byte and
a v7.3 file from `MatWriter` read the same arrays; a cell array refused by name).

MC-4: `tests/Ui.Tests/ModelComposer/` — `DeembedThroughNetworkTests` (a known linear core inside the GaN and the
classic networks, written by hand, de-embeds through the stock schematics to its own Y — measured ~1e-15 relative;
the pin refusal; the cache), `PadDeembedTests` (open-short and two-port pads around the core removed to 1e-9; a short
pad refused naming both ranges), `IntrinsicReReferencingTests` (intrinsic voltages against the SDD truth's internal
nodes to 1e-9; instrument with bias-path R equals device), `IntrinsicQuantitiesTests` (the elements to 1e-8 —
measured ~1e-14; a 1.5× `Lg` takes `Cgs` flatness from ~1e-15 to ~0.11). The truth is
`Support/SyntheticFetData.cs`.

MC-5: `tests/Ui.Tests/ModelComposer/` — `ColdFetExtractionTests` (the classic shell from a pinch-off and a forward
point with 1e-4 noise on S, both forward variants, every element from starts 25–40 % off: moderate within 0.2 %,
extreme within 0.7 % except `Cpgd` 3 %, its collapsed core's 0.02 Ω landing on the series resistances; the refusal),
`MultiBiasNetworkFitTests` (the GaN network, six series elements and a tied `Cpga`/`Cpda` within 0.4 %, the
de-embedded elements at the true network's own noise floor, bounds, the tie, the document round trip and a stale
step, the same seed bit for bit), `IdentifiabilityReportTests`, `NetworkFitCancelTests` (cancelled 20 evaluations
into the first start, the `.cmc` byte for byte unchanged). The per-bias cores are hand-written in
`Support/SyntheticFetData.cs`: a linear SDD per active point, plain R and C for the cold states.

MC-6: `tests/Engine.Tests/Modeling/Ann/` — `AnnValueTrainingTests` (1 × 10 `tanh` on `tanh(x)·y²` at 200 scattered
points: held-out RMS below 0.5 % of the range, measured 0.15 %), `AnnAdjointTrainingTests` (gradient-only training of
a non-separable `Q` within 0.5 % of its range — measured 0.02 %; a target field with curl 0.6 lands within 1 % of the
least-squares projection a grid solver computes — measured 0.05 %, where `Q` itself is 4.5 % off; the adjoint
weight-gradient against central differences, both activations, measured 1e-10; combined training of
`f(v1, v2, s)` with `s` tied to the bias at the value points recovers values and partials at fixed `s` within 1 % —
measured 0.01 % and 0.03 % — where value-only training fits the values but misses the partials by 10 %),
`AnnDeterminismTests` (same seed bit for bit, parallel within 1e-12, another seed differs; the held-out set is the
same after a shuffle and touches every octant), `AnnTrainingCancelTests` (cancelled ten iterations into the second
start: no result, the caller's network unchanged).

MC-7: `tests/Engine.Tests/Composed/DispersionNodeTests` (IdDC ≠ IdRF: DC node voltages as the `Id = IdDC` device's to
1e-12; S at 0.5–20 GHz with τ = 1 ms as the `Id = IdRF` device biased at the same intrinsic point — measured 2.9e-8;
HB's fundamental monotonic across ±2.5 decades of the corner, no step over a quarter of the swing, each end on its
reference); `tests/Ui.Tests/ModelComposer/` — `CurrentFitAnnTests` (192 DC points of the SDD truth through the GaN
network, one start, `Ig` at 2000 iterations: held-out `Id` 1.5 % of the range and 4.1 % of its own value, `Ig` 0.13
decades where it conducts above I0; the record round-trips through the document), `CurrentFitTableTests` (the table
1.5 %, as its network; the step warning at 0.2 V and not 0.1 V), `CurrentFitEquationTests` (the Angelov template
recovers all seven parameters of Angelov data to 1e-3). `CmcFormatTests` refuses a misspelt `Steps.Current` key. The
I–V grid (`SyntheticFetData.Iv`) is denser below 1 V, and the held-out biases lie between its lines.

MC-8: `tests/Engine.Tests/Modeling/ChargeLineIntegralTests` (both paths equal a non-separable charge to 1e-21 C; with a
constant curl κ they differ by `−κ·Δvgs·Δvds` at every site); `tests/Ui.Tests/ModelComposer/` — `ChargeFitAdjointTests`
(90 S points of the SDD truth through the GaN network, one start: held-out capacitances within 0.49 % of the row's
largest, charges within 0.06 % of their range up to the anchor), `ConservationCheckDataTests` (closed-form
capacitances at a jittered grid: 2.5 % and 0.17 % away from the edge; a Gaussian planted in `C12` peaks 2.93 inside
it, 0.12 at most beyond three widths, the drain row untouched), `ConservationCheckLargeSignalTests` (the truth as a charge SDD: 7e-23
of the fundamental; with its gate current as capacitances times dV/dt through two auxiliary ports, the f→2f and
2f→3f steps agree to 5e-6 and the spurious DC is 0.41 % of the fundamental), `TranscapacitanceReportTests` (the
synthetic `.cmodel` through a `ComposedFet`: curl 6e-6, `Cm` 39 fF RMS reported), `DriftChargeTests` (an [I11]
truth as an SDD at its intrinsic plane, `Id` the truth's: QGS, QGD and QDS within 0.34 %, `Qdrift` at `Id = 0`
0.27 % of its size, held-out capacitances within 0.29 %), `ChargeMethodsTests` (the Depletion template recovers a
depletion truth's capacitances to 1e-15 of their range through `EquationFit`'s derivative terms, and the composer's
whole `Run` on it reads 1.5 % in (a) on a rectilinear grid, ~1e-13 in (b) and 0 in (c); the Angelov template, by
Levenberg–Marquardt from its own start, recovers all thirteen parameters of capacitances written by hand from
Angelov's published form; a drift fit with no currents is refused). `SyntheticFetData.BiasedS` simulates multi-bias S through
ideal bias tees; `SDocument` de-embeds it through the stock network or not at all.

MC-9: `tests/Engine.Tests/Composed/ThermalPinTests` (a four-pin instance: `V(t) = Rth·P` to 1e-6 from the currents in
`rg` and `rd`; an external R and C on pin 4 give `(Rth ∥ Rext)·P`; three nets probe the same node as `X1.t`; `N = 2·N0`
halves `Rth` and doubles `Cth`); `tests/Ui.Tests/ModelComposer/` — `RthExtractionTests` (pulsed I–V at 25, 100 and
175 °C and self-heated DC at 25 and 100 °C of a 60 K/W truth: the default ANN isothermal `Id` gives 59.6 K/W, gated at
±3 %; DC only with `Rth` typed de-heats to the isothermal truth within 3e-10, against 13 % with the points left at the
ambient), `ElectrothermalModelTests` (that DC data and an equation `Id` through the thermal node: the self-heated DC
current within 0.5 % along vgs = 0.4 V, its negative DC output conductance kept; `Re Y22` at 1 GHz, far above the
~160 kHz corner, within 2 % of the truth's isothermal `Gds` at its junction temperature), `SingleTemperatureStepTests`
(one temperature: no `T` input, self-heating refused with "one temperature", the model `Mode: Single` at `T0`). The
truth is `SyntheticFetData.ThermalCell` — an SDD at its intrinsic plane whose third port is the thermal node.
