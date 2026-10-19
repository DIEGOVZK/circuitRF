# SAR: Phone Dipole

A phone-SAR system check: a half-wave dipole under a flat phantom of head tissue-simulating liquid, run at two bands with
the **band sweep**, and psSAR1g and psSAR10g read at the rated power beside published reference values. circuitRF's SAR
is for research and engineering use; it is not a compliance tool, and the table never says pass or fail.

## The cell

**System Check** (its 3D view). The configuration a published regulator bulletin (FCC OET Bulletin 65 Supplement C,
Edition 01-01) gives for a 1900 MHz system check:

- a dipole 68.0 mm end to end, here two PEC strips 3.8 mm wide (`armLo`, `armHi`) on z = 0, with a 2 mm feed gap that
  is a 50 Ω lumped port;
- a **Flat Phantom** above it: a 2 mm shell (circuitRF's own shell material, εr 3.7, lossless) holding 150 mm of head
  tissue-simulating liquid over 160 × 100 mm. Its floor is at z = 8 mm, so the liquid starts **10 mm from the dipole's
  axis**, the bulletin's spacing.

The liquid is a table of target values, not one εr and σ: each band's run evaluates it at that band (εr 40.08, σ
1.371 S/m at 1.75 GHz; εr 40, σ 1.4 S/m at 1.95 GHz).

One setup, **Template**: openEMS, 1.7–2.1 GHz in 41 points, 40 mm of air on every side behind an 8-cell absorber,
`CellsPerWavelength` 15, an end criterion of −40 dB, and a **Sar** group at 1.9 GHz with 1 g and 10 g cube-averaged SAR.

One band sweep, **Bands**: the Template at **1.75 GHz and 1.95 GHz**, at **0.25 W incident** on the feed.

## Run it

Open the 3D view, then *3D ▸ Draw ▸ Phantom ▸ Band Sweep…*. The dialog shows the sweep: its template, bands, power and reference
values. **Run** makes one setup per band (`Bands 1.75 GHz`, `Bands 1.95 GHz`), each the Template at that one frequency
with its sweep scaled to the band, and runs them in turn. About 2.5 minutes a band. Headless, the same run is

```
circuitrf em "System Check/3d/System Check.c3d" --bands Bands
```

## The table

| Band | Accepted | psSAR1g | psSAR10g | per W accepted (1 g / 10 g) |
|---|---|---|---|---|
| 1.75 GHz | 0.181 W | **7.58 W/kg** | **3.90 W/kg** | 41.95 / 21.61 W/kg |
| 1.95 GHz | 0.248 W | **9.98 W/kg** | **5.13 W/kg** | 40.19 / 20.66 W/kg |

Both peaks are in the liquid's first cell above the feed, 10.6 mm up and within 1 mm of the dipole's centre. Every value is above the reference
values the table shows beside it — 1.6 W/kg over 1 g (the national regulator's) and 2.0 W/kg over 10 g (the
international guidelines') — and is meant to be: a system check puts a known power into a dipole a centimetre from the
liquid to give the measurement system a large, repeatable number. It is not a device.

**0.25 W incident, not accepted.** The dipole is 68 mm, so it is matched near 1.94 GHz (S11 −22 dB there) and not at
1.75 GHz, where S11 is −5.6 dB and 28 % of the incident power is reflected. The table takes the reflected share off before
the power reaches the dipole, which is why 1.75 GHz accepts 0.181 W of the 0.25. Choose *accepted* in the sweep to read the
same SAR per watt delivered instead.

**These are circuitRF's own results, not target values.** A laboratory compares its measured psSAR1g with a target value
for its dipole and phantom, from a standard that is not reproduced here. If you have target values of your own, enter them
in the sweep's *Targets* and the table shows the difference.

## The grid

Each band's grid is drawn from its own sweep, so the two runs are on different grids: 143 × 105 × 132 = 1.98 M cells at
1.75 GHz and 153 × 111 × 143 = 2.43 M at 1.95 GHz. `CellsPerWavelength` 15 was chosen from a convergence study of the same
check at 1.9 GHz (`docs/design/sar.md` §14.5): against a grid of 20 cells per wavelength, 15 reads psSAR1g 1.6 % and
psSAR10g 0.9 % high, and 10 reads 3–4 % low. Raise it in the Template and run the sweep again to see the difference; the
band setups are made again from the Template every time.

## Things to try

- **Place the dipole yourself.** Select both arms, *3D ▸ Draw ▸ Phantom ▸ Place Against Phantom…*, face `liquid`, separation 10 mm:
  the placement solves the move and the 3D view shows the dimension. Try 15 mm and run the sweep again.
- **A third band.** Add 1.85 GHz to the sweep's bands. The Template is not changed; one more setup is made from it.
- **A phone instead of a dipole.** Draw or import the device, place it against the phantom's `liquid` face or the Generic
  Head's ear spacer (*3D ▸ Draw ▸ Phantom ▸ Generic Head (circuitRF's own)*), set the sweep's power to the device's rated conducted
  power and its duty cycle if it transmits in bursts.
