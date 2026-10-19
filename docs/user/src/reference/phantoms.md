---
title: Tissues and phantoms
slug: reference/phantoms.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Tissues and phantoms
lede: The materials SAR is computed in — frequency-dependent body tissues and tissue-simulating liquids, each with its source — and the parametric phantoms a 3D view draws them as.
keywords: tissue, tissues, body tissue, Gabriel, Cole-Cole, dispersion, frequency-dependent, tissue-simulating liquid, head liquid, body liquid, target values, density, phantom, head phantom, body phantom, flat phantom, generic head, ear spacer, shell, ellipsoid, cylinder, voxel, voxelized, Flatten Phantom, layers, innermost wins, Add Tissue Materials, tissue-materials.cmat, saline gel, SAR, research use
---

SAR is computed in **tissue**: a lossy dielectric whose material states a density. circuitRF ships a library of body
tissues and tissue-simulating liquids, each record citing its source, and six **phantoms** — parametric bodies of nested
layers — to put them in. Your own segmented anatomy comes in through [Voxel and mesh import](anatomy-import.html).

<div class="callout note">
<span class="label">Research and engineering use</span>
<p>circuitRF's SAR is for research and engineering. It is not a compliance tool, and the shipped values are reference
data for research use, not a claim that any phantom or liquid meets a standard.</p>
</div>

## The tissue library {#library}

**Add Tissue Materials** copies `tissue-materials.cmat` beside the technology and names it as one of its libraries
(see [Materials](materials.html#cmat-and-ctech)). It is on the technology editor's Materials tab, and at the top of the
3D view's Materials dialog (a phantom layer's *New Material…*, or *Assign Material…*), where it lists the tissues at once
to assign and names the library when you press the dialog's OK. Either way the technology is left unsaved until you
save it. It holds:

| Records | Values | Density |
|---|---|---|
| **16 body tissues**: skin (dry, wet), fat, muscle, cortical and cancellous bone, brain grey and white matter, cerebrospinal fluid, blood, vitreous humour, lung (inflated, deflated), heart, liver, kidney | the 1996 parametric (Cole-Cole) model of Gabriel, Lau and Gabriel, *Phys. Med. Biol.* 41, 2271–2293, evaluated by circuitRF at any frequency | ICRU Report 44 for fat, muscle, cortical bone, blood, brain and deflated lung; **the other eight state none** |
| **Head and body tissue-simulating liquids** | target values from 150 MHz to 5.8 GHz, FCC OET Bulletin 65 Supplement C (Edition 01-01), interpolated linearly between rows, as the bulletin prescribes, and refused outside them | 1000 kg/m³, as the bulletin states |
| **Phantom shell (circuitRF's own)** | εr 3.7, lossless: circuitRF's own value, inside the bounds the same bulletin gives for a shell (εr and tanδ below 5.0 and 0.05) | none: a shell is never tissue |
| **Saline gel (implant heating)** | σ 0.47 S/m, εr 80, from the published implant-heating literature its record cites | water's |
| **openEMS's example tissues** | the head phantom at 298 MHz, the body cylinder at 128 MHz and the dipole example's head at 1 GHz, exactly as openEMS's scripts state them | the same scripts |

**A tissue with no density cannot carry SAR**: a SAR setup whose region holds one is refused, naming it. No free source
gave a density that could be checked for both skins, cancellous bone, cerebrospinal fluid, vitreous humour, inflated lung,
heart, liver and kidney, so those records state none rather than an unchecked number. Add one, with its source, to your
own copy. Thermal properties (conductivity, specific heat, perfusion, metabolic heat) are empty for every tissue, for the
same reason.

**openEMS's example tissues are not circuitRF's.** openEMS's scripts label their phantoms' values "at 298 MHz" and "at
128 MHz", but they are not the Gabriel model's values there — the 298 MHz skull is εr 11.4, σ 0.29 S/m, where cortical
bone is εr 13.4, σ 0.082 S/m. They are kept as the scripts state them so a replication of those examples compares like
with like; for your own work, use the Gabriel records.

## One frequency per run {#frequency}

A tissue's εr and σ move strongly with frequency, and an FDTD run sees each material as one constant pair. So a 3D run
evaluates every frequency-dependent material **once**: at the setup's first SAR frequency when it computes SAR, otherwise at
the sweep's centre. The run's notes give the frequency and each value — *"Frequency-dependent materials are evaluated once,
at 1.75 GHz (the SAR frequency) … Head tissue-simulating liquid εr 40.08, σ 1.371 S/m"* — and `circuitrf explain view.c3d
--materials` lists every material as the solver will receive it.

Selecting a tissue in the Materials editor shows its **Frequency dependence** card: εr and σ at a frequency you type
(`298M`, `1.9 GHz`), and both plotted against frequency. The model or the table is in the record, and is edited in the
file.

## Phantoms {#phantoms}

*3D ▸ Draw ▸ Phantom* adds one:

| Phantom | Layers, outermost first | Starts as |
|---|---|---|
| **Head (3 ellipsoids)** | skin, skull, brain | openEMS's head phantom: semi-axes 100, 80, 100 mm, shells 9 mm |
| **Body (3 cylinders)** | skin, bone, tissue | openEMS's body phantom: radius 100 mm, 260 mm long, shells 8 and 10 mm |
| **Flat Phantom** | shell, liquid: the liquid in an open-topped box, the shell on its floor and four walls | a 2.0 mm shell and 150 mm of body liquid over 296 × 196 mm |
| **Generic Head (circuitRF's own)** | ear-left, ear-right, shell, liquid | an ellipsoid shell of head liquid, semi-axes 100, 78, 115 mm, a 2.0 mm shell, and a flat ear spacer each side, 4.0 mm thick and 25 mm in radius |
| **Sphere**, **Box** | one homogeneous body | radius 50 mm; a 100 mm cube |

- **One object.** A phantom moves, turns, hides and is left out of the solve as one; the tree lists its layers under it,
  and Properties shows the dimensions its shape uses and one material per layer. In the solve each layer is a solid
  named `<phantom>/<layer>`, so a SAR region can name a layer (`head/brain`) or the whole phantom (`head`).
- **The innermost wins** where layers overlap, so the brain is brain, not skin. Every layer is solved as a lossy
  dielectric.
- **The flat phantom** follows the bulletin where it gives a value: a shell of 2.0 ± 0.2 mm and a liquid 15.0 ± 0.5 cm
  deep. The bulletin gives no floor size, so the 300 × 200 mm outside is circuitRF's own, chosen to be at least ¾ by ½ of a
  wavelength from 900 MHz up. Its named faces are `floor` (the shell's outside) and `liquid` (the liquid's surface through
  the shell, the plane a system check's spacing is measured from).
- **The generic head** is circuitRF's own shape and is named so, so that nobody takes it for a standard's mannequin. x is to
  the front, y ear to ear (left is +y), z up. The spacers' 4.0 mm comes from the same bulletin; their 25 mm radius is
  circuitRF's own. Its named faces are `ear-left` and `ear-right`, each spacer's flat face: the face a handset is placed
  against ([Phone SAR](phone-sar.html#placement)).
- **Voxelized.** A head or body phantom can be given to openEMS as voxels of a stated pitch (**Voxel size** in
  Properties; 0 is the smooth layers): each voxel takes the innermost layer holding its centre. openEMS's own example phantoms are 2.5 mm voxels of
  exactly these ellipsoids, so a voxelized phantom reproduces them cell for cell. The 3D view, Palace and the thermal
  solver still draw and mesh the smooth layers, and the run says so.
- **Flatten Phantom** (its menu) replaces it with one ordinary solid per layer, `<phantom>_<layer>`, in a group named
  after it; the solve is the same. It is refused on a voxelized phantom, whose layers would become smooth.

What each object is in the file, field by field: `circuitrf reference 3d`.
