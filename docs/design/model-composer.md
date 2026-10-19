# circuitRF — Model Composer and the composed FET

**Status:** MC-1 built (the `.cmodel` format and the `ComposedFet` device, equation constituents);
MC-2 built (table and ANN constituents, guided extrapolation, drain–source symmetry, grid evaluation) ·
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
the standard FET symbol. `Thermal` (MC-9) and `Traps` (MC-10) are absent until their phases; a file
carrying `Thermal` is refused by field until then, and `Traps.Model` must name a known model (`None` today).

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
- `Extrapolation.Gx`: missing for a table or ANN current, present for an equation current or an unknown
  key, or not a positive number. `Core.Symmetry`: anything but `None`/`Exchange`.

## 4. The device

### 4.1 Elaboration as a cell (R-mc1-5)

`Elaborator.ComposedFetCell` intercepts a `ComposedFet` line before the primitive branch:

1. Three nets, or a refusal (a fourth is the thermal node, refused until a model has one). Parameters
   `File`, `N`, `m` (or `M`) and `Symbol` (artwork, ignored); anything else is refused by name.
2. `File` resolves exactly as an SnP's (`BaseDirectory`, workspace-relative), with the same named
   refusals: none given, a folder, not found; an unreadable model names the validator's field.
3. The model loads through `ComposedModelCache` — keyed by full path, size and modification time,
   `TouchstoneCache`'s pattern — so a hundred instances read the file once (`LoadCountFor`).
4. `ComposedNetworkTemplate.Synthesize` builds the cell for this (file, N, m), cached per elaboration:
   every network variable resolved, multiplied by its rule's factor, and every element parameter that
   evaluates to a real number written into the instance **as that number**. Pins 1–3 keep the network's
   names and become the cell's ports; pins 4–6 become `gi`, `di`, `si`. One `ComposedCore:core gi di si
   File="<absolute>" N=<N>` is added.
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
- A model carrying `IdRF` is refused at construction until MC-7 evaluates it.
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

### 4.4 Temperature (R-mc1-8, D8)

A `Single` model evaluates with no `T`; a run whose `temp` differs from `T0` gets one warning per model
file per run, naming both. A `Range` model evaluates at `temp` clamped to `[Tmin, Tmax]`, with one warning
when it clamps. Both go through the netlist's own warning channel (`AddWarningOnce`).

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

## 5. Where it lives

| Path | What |
|---|---|
| `src/Core/Devices/Composed/` | `ComposedModel` (records), `CmodelFile`, `ComposedNetworkTemplate`, `ComposedCoreProgram`, `ComposedModelCache`, `ComposedCoreModel`, `PeripheryScaling`; MC-2: `CubicBSpline`, `RuntimeEvaluators` (`TableEvaluator`, `AnnEvaluator`), `GuidedExtrapolation` (with `TrainingDomain`), `ComposedArrays` (the payload arrays, built and validated) |
| `src/Engine/Modeling/TensorSplineFit.cs` | fitting a table's coefficients, with smoothing |
| `src/Core/Elaboration/Elaborator.cs` | `ComposedFetCell`, `ResolveComposedParameters`, the core's node expansion and temperature warnings |
| `src/Design/Schematic/ComposedFetSymbolProvider.cs` | the symbol rule above |
| `src/Cli/Check.cs`, `DocumentKinds.cs` | `check` on a `.cmodel` |

## 6. Gates (MC-1, MC-2)

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
