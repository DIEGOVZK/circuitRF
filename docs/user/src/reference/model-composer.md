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
count. Selecting a set points the plots and the cursor's lists at it.

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
| ANN | Hidden layers (1 to 4, neurons each, default `8, 8`), activation, iterations |
| Table | Grid step and smoothing; a charge table also takes its line-integral path — A, B or their mean |
| Equation | A template, or your own expression in `vgs`, `vds` and named parameters; after a fit, each parameter's value |

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
**Drift charge in Qg**.

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

The trap model (None, CaptureEmission, Custom), a toggle per state, the time constants, and for Custom each state's
dynamics and DC expressions with the result of the fixed-point check. A state the data cannot support shows why in
place of its toggle; a state trained from one trap-setting level shows that level.

| Plot tab | Shows |
|---|---|
| Pulsed I–V | Per trap-setting level, measured Id against the model at the cursor's Vgs; points pulsed beyond the trap-setting bias are hollow — validation only |
| DC I–V | The DC data against the model, as validation |

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
