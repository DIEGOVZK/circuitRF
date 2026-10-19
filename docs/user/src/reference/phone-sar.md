---
title: Phone SAR
slug: reference/phone-sar.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Phone SAR
lede: A device or antenna placed against a flat phantom or a head, run at its bands, and psSAR1g and psSAR10g read at its rated power beside published reference values — shown as reference values, never as pass or fail.
keywords: phone SAR, handset, wireless device, mobile, psSAR1g, psSAR10g, 1 g, 10 g, flat phantom, generic head, ear spacer, Place Against Phantom, separation, tilt, band sweep, Band Sweep, bands, rated power, conducted power, duty cycle, incident, accepted, reference value, limit, FCC, ICNIRP, 1.6 W/kg, 2 W/kg, system check, dipole, target value, tissue-simulating liquid, research use
---

A wireless-device engineer puts an antenna or a whole device against a phantom of tissue-simulating liquid, runs it at
the device's bands, and reads psSAR1g and psSAR10g at the device's rated power. circuitRF does each step on an ordinary 3D
view and an ordinary openEMS setup: the **placement** writes transforms, the **band sweep** writes setups, and the
**table** reads their results.

<div class="callout note">
<span class="label">Research and engineering use</span>
<p>circuitRF's SAR is for research and engineering. It is not a compliance tool: published limits are shown beside a
result as reference values, and nothing says pass or fail.</p>
</div>

The worked example is **SAR: Phone Dipole** (*File ▸ New ▸ Examples*): a system-check dipole under a flat phantom at
1.75 and 1.95 GHz. The phantoms are described in [Tissues and phantoms](phantoms.html), and SAR itself in
[SAR and B1](sar.html).

## 1. A phantom {#phantom}

*3D ▸ Draw ▸ Phantom*:

- **Flat Phantom** — a shell holding 150 mm of liquid, open on top; for a body-worn or flat position and for a system
  check. Set its liquid to the head or body tissue-simulating liquid.
- **Generic Head (circuitRF's own)** — an ellipsoid shell of head liquid with a flat ear spacer on each side; for the
  position against the ear. It is circuitRF's own shape, not a standard's mannequin.

Both liquids are target tables, so each band's run evaluates the liquid at that band.

## 2. Placement {#placement}

Select the antenna or the device — any objects, placed cells and groups — and *3D ▸ Draw ▸ Phantom ▸ Place Against
Phantom…* (or the canvas menu on the selection).

| Field | |
|---|---|
| Phantom, Face | a phantom's own box faces (`xmin` … `zmax`), and its named ones: a flat phantom's `floor` (the shell's outside) and `liquid` (the liquid's surface through the shell), the generic head's `ear-left` and `ear-right` |
| Separation | from the face's plane to the device's nearest point, measured on its geometry (a STEP part on its tessellation) |
| Tilt (°) about | a turn about a world axis, applied first; the move is then solved |
| Offset x, y, z | where the device's box sits on the face, from the face's centre |

It moves the selection as **one rigid body** and writes **ordinary transforms**, what a drag would write: nothing in the
file remembers the placement, and moving the device by hand afterwards is fine. The 3D view shows the separation as a
dimension, measured again from the geometry whenever the view changes. **Refused:** placing a phantom itself; a negative
separation; a separation from `liquid` thinner than the shell (the device would be inside it); an object whose position
is an expression (placing it would overwrite the expression).

A tilt that is not a quarter turn is for a device made of solids. openEMS cannot take a sheet tilted out of every axis
plane, or a lumped port pointing off the axes, so a drawn strip antenna fed by a lumped port refuses such a tilt before
the run.

## 3. Bands and power {#bands}

One run gives SAR at **one** frequency (the tissues are evaluated there). *3D ▸ Draw ▸ Phantom ▸ Band Sweep…* makes one
setup per band from a template:

| Field | |
|---|---|
| Template setup | an openEMS setup with a SAR group: the grid, the boundaries, the masses |
| Bands (GHz) | the frequencies |
| Rated power (W) | the device's rated conducted power (default 1 W, so the table reads per watt), referred to the **accepted** (the power entering the antenna), or **incident** (the power the transmitter delivers into 50 Ω, of which the antenna's mismatch reflects \|S11\|²) |
| Duty cycle | for a transmitter that sends in bursts: its scaling factor |
| Reference values for | head and trunk, or limbs: which published values are shown |
| Your own values, Targets | reference values of your own, and target values for a known configuration |

**Create Setups** writes one setup per band, `<sweep> <f> GHz`: the template with its SAR at that band, 1 g and 10 g added to
its masses, and **its sweep scaled by band / template frequency**, so each band keeps the template's relative bandwidth
and a grid drawn from its own highest frequency. Creating again replaces them, so an edited template brings every band up
to date. **Run** creates them and runs each in turn, as Simulate would; the dialog is not modal, because the runs take as
long as they take. Power, duty cycle, reference values and targets are read by the table alone, so changing them never
makes a result out of date.

## 4. The table {#table}

One row per band and driven port: psSAR1g and psSAR10g **at the rated power**, and per watt; the peak cube's centre and
tissue; and the difference from a target where you gave one. A band not run yet says so.

**Published reference values** are shown beside each number, each with its source and date:

| Reference value | Source |
|---|---|
| 1.6 W/kg over 1 g, head and trunk | FCC OET Bulletin 65 Supplement C (Edition 01-01, June 2001), general population, partial body |
| 4.0 W/kg over 10 g, extremities | the same table |
| 2.0 W/kg over 10 g, head and trunk | ICNIRP Guidelines (100 kHz to 300 GHz), *Health Phys.* 118(5):483–524 (2020), general public, local |
| 4.0 W/kg over 10 g, limbs | the same |

A number above one is coloured and says which reference value it is above.

Headless, `circuitrf em view.c3d --bands <sweep>` makes the same setups in memory, runs them and prints the table; MCP's
`run analysis=em` takes `bands`.

## A system check {#system-check}

A laboratory checks its measurement system with a half-wave dipole at a stated spacing below a flat phantom, and compares
the measured psSAR1g with a target value for that dipole. The example reproduces the configuration the FCC bulletin gives
for 1900 MHz — a 68.0 mm dipole, 10.0 mm from its axis to the liquid, a 2.0 mm shell, 15 cm of head liquid — and its
results are **circuitRF's own, not target values**: the target values come from a standard that is not reproduced here. If
you have them, enter them as the sweep's targets.

At 1.9 GHz, per watt accepted, the same check on three grids:

| `CellsPerWavelength` | 10 | **15** | 20 |
|---|---|---|---|
| cells | 0.87 M | 2.30 M | 4.22 M |
| psSAR1g | 39.08 W/kg | **40.87 W/kg** | 40.21 W/kg |
| psSAR10g | 20.00 W/kg | **20.97 W/kg** | 20.78 W/kg |

From 15 cells per wavelength on, psSAR agrees with the finest grid to 2 %; at 10 it reads 3–4 % low. The approach is not
monotonic, because each grid places the dipole's edges and feed differently and the dipole's own resonance moves with them.

## What is not here {#not-here}

Hand and body-worn positions with a hand phantom, absorbed power density above 6 GHz, and compliance reports.
