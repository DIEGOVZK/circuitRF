---
title: Voxel and mesh import
slug: reference/anatomy-import.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Voxel and mesh import
lede: Your own anatomy in a 3D view — a segmented label map (NIfTI, raw or a DiscMaterial file) or one closed surface per tissue (STL, OBJ, PLY) — each tissue given a material, and solved on openEMS.
keywords: voxel, voxel model, voxel body, VoxelBody, label map, segmentation, segmented, NIfTI, nii, nii.gz, raw volume, DiscMaterial, h5, HDF5, label table, CSV, crop, grid step, anatomical model, anatomy, surface mesh, STL, OBJ, PLY, closed surface, self-intersection, nested surfaces, tetrahedral mesh, node, ele, voxelise, import voxels, import surfaces, Import Voxel Model, Import Surface Meshes, tissue, SAR, research use
---

circuitRF ships no anatomical model; it reads the one you have. Two forms come in:

- a **segmented label map**: one integer per voxel naming a tissue, usually a NIfTI file, kept as a **voxel body**;
- **one closed surface per tissue**, each kept as a **mesh**.

Both land in a 3D view as ordinary objects, take a material per tissue from the technology, and run on openEMS with
[SAR and B1](sar.html). Neither is meshed by Palace or the thermal solver.

<div class="callout note">
<span class="label">Research and engineering use</span>
<p>circuitRF's SAR is for research and engineering. It is not a compliance tool. A model you import is yours: circuitRF
copies it into your workspace and sends it nowhere, and whatever its licence allows is up to you.</p>
</div>

## Importing a label map {#voxels}

*File ▸ Import ▸ Voxel Model…* on the active 3D view. Pick the file; the dialog shows the import's **report** and a
**label table**, one row per label the file holds:

| Column | |
|---|---|
| Label | the integer in the file |
| Voxels | how many voxels carry it |
| Tissue | the name the tissue takes, `<body>/<tissue>` in the solve |
| Material | a technology material, or **Air** |

**Every label the voxels carry must be mapped**, to a material or to Air: one left unmapped would be solved as air, and
the SAR in its neighbours would be wrong with nothing said, so the import refuses it, naming each label and its voxel
count. Label 0 is air unless a row maps it. Names come from a label list you give, then the file's own names, then
`label k`; a material is filled in only where a technology material has exactly the tissue's name.

| File | Read as |
|---|---|
| `.nii`, `.nii.gz` | NIfTI-1 or NIfTI-2, either byte order, 3D integer data (or a float volume of whole numbers); the orientation from the file's sform, else its qform, else its voxel sizes; millimetres unless the file says otherwise |
| raw | you state the dimensions, the type (`uint8` … `int32`), the voxel size, the byte order and any header to skip; x varies fastest. The file's length must match exactly, which catches a wrong dimension or type |
| `.h5` | an openEMS DiscMaterial file; its own εr, σ and density tables are **not** used — the label table's materials are, and the import says so |
| label list `.csv` | `label,name[,material]`, a header row allowed, `#` comments, `air` as a material |

**Refused rather than guessed:** an orientation turned off the axes (the angle is named: resampling a label map onto a
turned grid is a decision about the anatomy that belongs to whoever segmented it — a flip or an axis permutation is
fine); a NIfTI pair (`.hdr`/`.img`); a 4D series; a scaled volume; more than 255 tissues in one body (openEMS addresses its
voxel materials with one byte — map labels that share a material to one row).

**The body** in the 3D view is one object, drawn as tissue-coloured surfaces at a reduced resolution; picking a tissue
selects it. Properties shows its file, the air labels, the crop, its voxel extent, the grid step and one material per
tissue; the tree lists its tissues under it.

- **Crop** keeps a box of voxel indices; the rest of the file is never read into the solve.
- **Its placement** moves it, and may turn it by quarter turns only, so voxels move exactly.
- **Grid step** 0 (the default) puts an FDTD grid line on every voxel boundary, so each cell is one voxel. A positive
  step caps the cell size inside the body instead; each cell then takes the voxel its centre lies in. A whole body at
  1 mm is about 270 M voxels.
- **The file is copied** into the 3D view's folder, and its hash recorded; `check` warns when it has changed since.
- **A SAR region** names a tissue (`head/brain`) or the whole body (`head`), as it names a phantom's layer. Name **B1 mean
  over** explicitly: a body has no innermost layer to default to.

**Size, before the run.** The report gives the voxel count, the dimensions and voxel size, what the crop keeps, the label
histogram with each row's material, and the FDTD cells inside the body with openEMS's memory for them. Past 50 M cells it
is a **warning, not a refusal** — a whole body at 1 mm is a legitimate run, only a slow one — naming a crop or a coarser
grid step as the remedy.

## Importing surfaces {#surfaces}

*File ▸ Import ▸ Surface Meshes…*, with one file per tissue (STL ASCII or binary, OBJ, PLY ASCII or binary). A mesh file
states no unit, so you give one. Each file is checked, and **nothing is imported if one fails**:

- **closed**: every edge in exactly two triangles, once each way; open, non-manifold and flipped edges are counted and
  reported;
- **not self-intersecting** (checked up to 2 M triangles; a larger surface is checked for closure only, and the report
  says so);
- **outward**: a surface wound inside out is turned round, and the report says so.

The surfaces are **nested by containment**, outermost first, so an inner tissue wins the volume it shares with an outer
one, as a phantom's layers do; two surfaces that cross rather than nest are named. A material is matched to each by file
name, and is set in Properties per mesh. A tetrahedral mesh (`.node`/`.ele`) is **voxelized** at a size you choose, each
voxel the region of the tetrahedron holding its centre, and kept as a `.nii.gz` beside the 3D view: from then on it is a
voxel body.

## Headless {#headless}

<pre><code class="cmd"><span class="prompt">$ </span>circuitrf import voxels head.nii.gz --into model/3d/model.c3d --labels head.csv --report
<span class="prompt">$ </span>circuitrf import voxels head.raw --into model/3d/model.c3d --dims 256,256,180 --type uint8 --voxel 1mm --material 0=air
<span class="prompt">$ </span>circuitrf import surfaces tissues/ --into model/3d/model.c3d --unit mm --material "skull=Bone (cortical)"</code></pre>

A length always carries its unit (`2.5mm`); a bare number is refused. `--crop`, `--grid-step` and `--name` set what the
dialog sets; `--report` prints the report without importing. The `.c3d` is created when it is absent. MCP's `import` tool
takes `what=voxels` or `what=surfaces` with the same choices.
