# Create from Image

**Series:** `docs/sonnet-briefs/brief-img-0-overview.md` (decisions D1–D18) · **Code:** `src/Design/Imaging/` (the
raster core), later `src/Design/Layout/Recognition/Image/` and `src/Design/Schematic/Recognition/` · **Builds on:** the
whole Create Schematic from Artwork series (`docs/design/artwork-to-schematic.md`), the bitmap primitives, the drawn
netlist (`src/Design/Schematic/NetlistSchematic.cs`), AS-10's stroke-glyph reader and the `convert` pipeline.

This note records what the feature is for, the pipelines, the decisions and why, then one section per phase. IM-1
wrote §1–§4; each later phase adds its section.

---

## 1. What it is for

Create Schematic from Artwork turns copper into a schematic. Most of the circuits a user wants to start from are not
copper — they are **pictures**: a datasheet's application circuit, a figure in an application note, a screenshot of
another tool, a die plot, a board rendered by a Gerber viewer. This series reads such a picture and makes:

- **a schematic** — whether the picture is a schematic or a layout;
- **a layout** — when the picture is a layout.

It is a best attempt, like the artwork command: the user cleans up a little afterwards, with the source picture lying
under the result so cleaning up is comparing in place. Many pictures hold no circuit at all, and those fail gracefully
with a sentence saying what was seen.

**It is feasible for the pictures most users will actually have, with no machine learning and no new native
dependency:**

| Picture | Expectation | Why |
|---|---|---|
| A layout rendered by a tool | **Good.** Copper comes back to within ½ pixel; the rest is the artwork pipeline. | Flat colours, sharp edges, rectilinear geometry — classical colour separation, sub-pixel contours and snapping. |
| A machine-drawn schematic | **Good topology, partial values.** Wires, junctions and the common RF symbols read reliably; text when it is clean, a tunable variable when not. | One stroke width, straight orthogonal wires, a small symbol vocabulary. |
| A photograph of a real board | **Partial at best** (optional IM-13). | Perspective, glare, solder mask over copper, parts covering pads. |
| A hand-drawn schematic | **Not a goal.** | Lines are not straight, symbols are not standard. |

What makes it cheap is that the hard back halves already exist: a traced layout feeds the existing artwork
recognition, a recognised schematic is a `TestBench` the existing `NetlistSchematic.Build` draws, an unknown value is
the existing global-variable-plus-`tune` rule, and text is the existing stroke-glyph matcher fed with a skeleton's
centre lines. The one thing a picture never states is **scale**, and D6 treats that the way the repo treats an
unstated Excellon format — never a silent guess.

## 2. The pipelines (D3)

```
picture (file │ clipboard │ placed bitmap)
   └─ IM-2  ImageSource → decoded, oriented, flattened onto white; kind = schematic │ layout │ none
        │
        ├─ layout picture ── IM-3  trace → LayoutShapes on technology layers (+ the underlay)
        │                        ├─ Make Layout    → .clay  (new cell, or into the layout the bitmap sits in)
        │                        └─ Make Schematic → IM-4 → RecognitionInput → ArtworkRecognition (AS-3…AS-6, unchanged)
        │
        └─ schematic picture ─ IM-7 wires/regions → IM-8 symbols → IM-9 text → IM-10 TestBench → NetlistSchematic.Build

every reader of pixels ── IM-1  src/Design/Imaging/ (no circuit knowledge)
surfaces ── IM-5 the dialog · IM-6 entry points · IM-11 `convert` / `recognize` / MCP
```

**A schematic picture does not make a layout directly.** That is Create Schematic from Image followed by the existing
Update Layout from Schematic; the dialog says so in one line.

## 3. The decisions, and why

| | Decision | Why |
|---|---|---|
| **D1** | No machine learning, no native dependency, all in-house: decoding through SkiaSharp's `SKCodec`, every algorithm written here under MIT. The symbol classifier and the text reader sit behind interfaces. An OS text-recognition service is deferred, not rejected. | Nothing to train, ship or license; nothing that behaves differently per platform. The interfaces are where an extension would plug in. |
| **D2** | Everything that decides anything lives below the firewall (`src/Design/Imaging/`, `…/Layout/Recognition/Image/`, `…/Schematic/Recognition/`); `src/Ui` holds the dialog, overlay and entry points only. | A rule living only in the dialog is a rule the CLI does not apply. IM-11's gate is byte identity between GUI, CLI and MCP. |
| **D3** | The pipelines above; the artwork recognition and `NetlistSchematic.Build` are reused unchanged. | Two readers of copper would be two answers; a second schematic writer would drift. |
| **D4** | The source picture is copied into the target cell folder as `<cell>.source.<ext>` and referenced by path, never embedded; provenance records the original file NAME (no path), SHA-256, pixel size and options. | Documents reference images by path (the bitmap primitives' rule); no personal paths in a workspace; a re-run compares the hash. |
| **D5** | The result carries the source picture as a locked underlay at 35 % opacity, placed so every recognised item lies on what it was read from. A setting turns it off. | Cleaning up becomes comparing in place, not flipping between windows. A bitmap is already excluded from export, DRC and EM. |
| **D6** | Scale is never a silent guess. Evidence, strongest first: the placed bitmap's own placement; two picked points and a typed distance with an SI unit; agreeing land patterns (≥ 3 within 3 %); a line of stated Z0; the file's resolution metadata (offered, never chosen). The GUI may default to an inferred scale one click from being changed; the CLI needs `--scale`, and `--scale auto` is refused when the land-pattern evidence is not met. | A wrong scale is off by orders of magnitude while looking fine. A screenshot's 72/96 dpi describes a screen. |
| **D7** | Layers come from colour: at most eight CIELAB clusters, deterministic k-means, ΔE < 10 merged; each mapped by the user to a technology layer, Drill, Board outline, Silkscreen or Ignore. The border-majority cluster defaults to Background. Mappings save as per-user presets. | A picture's colours are its only layer information; pictures from one source always use the same colours. |
| **D8** | The technology is the user's; the report says the stackup was not read from the picture. | A picture says nothing about the stackup. |
| **D9** | A schematic picture can hold R, L, C, ground, port/terminal/connector, transmission line, short and open stubs, diode, and multi-pin devices (cut out as artwork D9). An unmatched two-pin symbol is kind `?`. | The common RF vocabulary; the cut-out is what matching work needs. |
| **D10** | Text is designators, values, net labels and port names only, read by the AS-10 stroke matcher on the skeleton under a grammar; an unread value becomes a global variable with a `tune` entry. | Prose is not needed; a grammar is what makes a stroke matcher reliable; an unknown is a knob, not a failure. |
| **D11** | A T-junction connects; a four-way crossing connects only at a junction dot (option); equal net labels connect; a wire end within half a stroke gap of a pin connects; dangling ends are listed. | The conventions of machine-drawn schematics. |
| **D12** | The schematic is drawn as the picture draws it (exact placement hints to `NetlistSchematic.Build`); Tidy is one combo away. | The user compares the result against the picture under it. |
| **D13** | Ports from port symbols, terminals, connectors and port-like net labels; else the left and right dangling extremes. No port is not a refusal for a schematic picture. | A drawn circuit is useful before it has ports. |
| **D14** | Default target: a new cell. Started on a placed bitmap: over that bitmap, in that document, as one undo step. | The owner's choice; an editor command is not a file write, so the artwork replace rule is not engaged. |
| **D15** | No new verb: picture → layout is a `convert` import; picture → schematic is a `recognize` input; `--overlay out.png`; the MCP tools return the overlay as image content. | The format is the contract; an agent with vision sees what was read and corrects it through the parts CSV or the `.cnl`. |
| **D16** | Refused only when undecodable (naming the formats read, saying *export it as PNG*), smaller than 64 px a side, holding no drawing, a layout with no scale, or nothing recognised. Everything else is a finding with a count. Above 50 MP the picture is reduced with a note. | Graceful failure: each refusal says what to do next. |
| **D17** | Same picture, same options → same bytes on every platform: seeded clustering, no order-dependent parallelism, no system font in any decision. | IM-11's byte identity and IM-12's committed fixtures depend on it. |
| **D18** | Non-goals: hand drawings, vector sources, multi-page documents, handwriting, trained models, stackup inference, coupled lines, device models, and (until IM-13) photographs. | Scope. |

## 4. IM-1 — the raster core

`src/Design/Imaging/`: pixels in, geometry out, with **no circuit knowledge at all**. Every later phase reads a picture
only through this folder, so one decoder, one clustering and one skeleton serve the layout reader, the schematic reader
and the text reader alike. Gates: `tests/Ui.Tests/Imaging/` (pictures drawn in memory with SkiaSharp, anti-aliased —
never a committed file).

### 4.1 Decode and coordinates (R-im1-1, R-im1-10) — `RasterImage.cs`, `PixelFrame.cs`

- `RasterImage.Decode(ReadOnlySpan<byte>)` / `.Load(path)` through `SKCodec`, accepting PNG, JPEG, BMP, GIF (first
  frame) and WebP only — anything else, including a format Skia happens to decode, gets the refusal, so what is read
  does not vary with the Skia build. The refusal names the formats read, names TIFF and HEIC (what phones and
  screenshot tools produce), and says *export it as PNG*.
- Decoded into sRGB RGBA8 (Skia converts an embedded profile), **alpha composited onto white**, then turned upright
  from `SKCodec.EncodedOrigin`. `SKCodec` reports the orientation and leaves the pixels alone, so the eight EXIF cases
  are applied here, on the bytes.
- The stated resolution (PNG `pHYs` in metres, JFIF density in dpi or dpcm, the BMP header) is parsed from the file
  bytes and **recorded, never acted on** (D6 (5)).
- Above 50 megapixels the raster is area-averaged (box filter with fractional edge weights, one destination row at a
  time so the working set is a row) to at most 50 MP, and `ReductionFactor` says by how much.
- **Pixel space is x right, y down, origin at the top-left pixel's corner, pixel centres at ½.** `PixelFrame` (scale,
  offset, flip) is the one conversion out of it; `YUp` lays a picture on a layout with its bottom-left at a point, and
  carrying a Clipper2 path through a flip reverses it so an outer boundary stays positive.

### 4.2 Colour (R-im1-2) — `CieLab.cs`, `ColourClusters.cs`, `CoverageMap.cs`

sRGB → CIELAB (D65, table-driven per channel byte). `ColourClusters.Find(raster, maxK = 8)`:

1. **Sample**: a stride over the pixel index with at most 250 k samples. The stride is bumped until it is coprime with
   the width — a stride sharing a factor with it samples the same columns on every row and misses a thin vertical
   line entirely. Samples are deduplicated by (colour, edge flag) into weights, in sorted key order.
2. **k-means++** for each k = 1 … maxK with a fixed seed and a PRNG of our own (SplitMix64), so the sequence does not
   change with the runtime's `System.Random`. An empty cluster restarts on the farthest sample — a deterministic
   choice, not a draw.
3. **The elbow** reads an error that treats an **edge pixel** (ΔE > 8 against a 4-neighbour) as a *mix* of two
   centres, scored by its distance to the segment between them, and a **flat pixel** by its distance to the nearest
   centre. Without the first, the pink of an anti-aliased red-on-white edge always looks worth a fourth cluster; without
   the second, a flat grey region between black and white would be absorbed as "a mix". k is the smallest whose mean
   error is at or below the noise floor (ΔE² 4), else the first after which one more cluster removes less than 5 % of
   everything clustering removed.
4. **Refine** each centre to the mean of its flat members alone, so anti-aliasing does not pull a colour toward the
   background (the gate checks a red and a blue come back as their exact bytes).
5. **A small colour of its own** (IM-4): a group of flat samples ΔE 30 or more from every chosen centre, carried by at
   least 8 of them, is added as a centre of its own. A single drill hole is a few hundred pixels of a million — its
   contribution to the mean error is far under the noise floor, so the elbow stops before it and the hole is folded into
   the copper around it; and it is too light for k-means++ to seed on reliably at a larger k. The heaviest far sample
   seeds each group, which takes every far flat sample within ΔE 15 of it — greedy and deterministic. An anti-aliased
   edge's mix never qualifies: edge samples are not flat.
6. **Merge** clusters closer than ΔE 10, closest pair first.
7. **Every pixel as a two-colour mix**: its nearest cluster, and the cluster whose segment with it passes closest to the
   pixel; the mix fraction is measured **in the sRGB bytes**, the space Skia and most tools blend in, so a coverage of
   0.5 sits on the drawn edge. Stored as three bytes a pixel (a, b, fraction in 1/255ths) rather than eight float maps;
   `Coverage(k)` materialises one map on demand. Clusters are ordered largest share first; `BorderShare` names the
   background (D7's default).

### 4.3 Line art, morphology, components, distance (R-im1-3 … R-im1-5)

- `LineArt.Binarise`: Sauvola (`T = m·(1 + k·(s/R − 1))`, window 25, k 0.34, R 128) from integral images; a grey level
  at or below 64 is always foreground, because Sauvola reads the inside of a large solid dark area as background (m and
  s both collapse there). A dark ground is detected from the share of dark border pixels and inverted first.
- `DistanceTransform`: Felzenszwalb–Huttenlocher's separable lower envelope, **exact** integer squared distances,
  measured between pixel centres, with **outside the picture counted as background** — the edge of the picture is the
  paper.
- `Morphology`: erosion and dilation by a disc are thresholds of the distance transform (to background, and to
  foreground), so the disc is true at any radius. Open, close, despeckle.
- `Components.Label`: two-pass union–find, 8-connected foreground / 4-connected background, labels renumbered in raster
  order of first pixel. A component's holes are the bounded background components whose **first pixel's upper
  neighbour** belongs to it — what is above a hole's top edge is the shape enclosing it, never one sitting inside it.
- `StrokeWidth.Estimate`: the mode of `2·DT − 1` over the skeleton's path pixels. The `−1` is because the transform
  measures to the nearest background pixel's *centre*, half a pixel beyond the stroke's edge: a 3 px stroke centred on
  a row has background centres 2 px either side. The mode is a ¼ px histogram's fullest bin, refined to the mean of the
  samples within ½ px of it. An even width reads about 1 px narrow (the skeleton sits on one of the two middle pixels);
  the schematic reader uses it as a scale for tolerances, where that is well inside the slack.

### 4.4 Skeleton and its graph (R-im1-6) — `Skeleton.cs`, `SkeletonGraph.cs`

- Zhang–Suen, each sub-iteration deciding on its input and deleting afterwards, so the result does not depend on visit
  order.
- **Staircase corners removed** before the walk: Zhang–Suen leaves a pixel at the inside corner of a diagonal step
  whose two 4-neighbours already touch diagonally; it has three skeleton neighbours without being a junction. Removing
  it never disconnects anything.
- Nodes: end pixels (≤ 1 neighbour) and junction pixels (≥ 3), adjacent junction pixels merged into one node at their
  mean. Edges are walked from every node pixel, 4-neighbours first; a closed loop with no node is anchored at its first
  pixel in raster order.
- **Spurs** (end-to-junction branches shorter than the stroke width) are pruned, and a node left with two edges is
  dissolved into one edge, until nothing changes. A drawn T is three edges and one junction.
- Each edge's chain is simplified (Douglas–Peucker, 0.5 px) from node centre to node centre, records which chain index
  each vertex stands on, and carries the mean `2·DT − 1` along it.

### 4.5 Contours (R-im1-7) — `Contours.cs`

- **Marching squares at 0.5** on a coverage map sampled at pixel centres, padded with 0, so every ring closes. A saddle
  cell is decided by its corners' mean. Segments are oriented by one local rule (from the crossing where a clockwise
  walk round the cell leaves the inside to where it re-enters), so outer boundaries and holes come out with opposite
  windings in one pass.
- **The crossing is placed exactly on a clean step.** Linear interpolation between two centres is off by up to
  0.09 px, periodically along a slanted edge — enough to read a rectangle rotated 1.5° as 0.9° on its 20 px sides, which
  a 1° snap tolerance then snapped. For a box-filtered step at e, the two coverages straddling the crossing sum to
  `1.5 − (e − first centre)` whichever pixel the edge is in, so where the centre beyond each is fully out and fully in,
  the edge is placed by that sum; elsewhere (a feature thinner than two pixels, a corner) it falls back to
  interpolation. The 37.3 × 12.6 px rectangle gate comes back within its 0.25 px on every edge.
- **Nesting**: the largest ring is an outer boundary, so its winding names the outer sign; each hole goes in the
  smallest outer ring containing one of its vertices (no rings cross). Regions are returned as Clipper2 paths, outer
  positive and holes negative, ordered top-left first.
- **Simplify then snap**: Douglas–Peucker (0.35 px) on the closed ring, cut at its first vertex and the vertex
  farthest from it. Each edge is refitted (total least squares) to the contour points it stands for, less those within
  1 px of its ends, which belong to the corners as much as to it. Then, in this order:
  1. a short edge (< 2 px) between two longer ones meeting near a right angle is an anti-aliased corner's rounding and
     is dropped **before snapping** — its direction is noise, and a noisy 1 px edge within the tolerance of an axis
     would otherwise be snapped;
  2. an edge within the angle tolerance (3°) of 0°/90° (and 45° when asked) is snapped onto it about its fitted
     centroid, with an exact unit direction;
  3. a short unsnapped edge left between two snapped, non-parallel ones is dropped; neighbouring edges on one line
     (within ½°, offsets within 0.5 px) are joined;
  4. a run of unsnapped edges meeting within 2° of a right angle shares one orientation (the length-weighted mean mod
     90°), so its corners are exact. **A snapped neighbour does not fix that orientation**: an early version let it,
     and a spuriously snapped corner edge squared a 1.5°-rotated rectangle onto the axes — snapping by another name,
     past the stated tolerance;
  5. vertices are re-intersected, an axis-aligned line giving its coordinate exactly so a snapped corner is exact
     rather than nearly so; two lines that are parallel or meet more than 3 px from where the contour turned keep the
     turn, projected onto each.

### 4.6 Fitted primitives (R-im1-8) — `Fit.cs`, `Segments.cs`

- `Fit.Circle`: Kåsa's algebraic fit on centred points, refined by Gauss–Newton on the radial distances, with the RMS
  radial residual — a via comes back as a circle, not a 40-gon (radius 6.4 px within 0.2 px on the raw ring).
- `Fit.Segment`: total least squares (the scatter's principal direction), endpoints at the extreme projections, RMS
  perpendicular residual as its straightness.
- `Segments.FromSkeleton`: each edge cut where its polyline turns by more than 3°, each run refitted to its skeleton
  pixels, a run ending on a node reaching the node's centre; runs meeting at a node on one line (within the angle, and
  within half a stroke width laterally) merged greedily by angle — a wire with a T part-way along is one wire. Runs
  shorter than two stroke widths are not reported.

### 4.7 Determinism (R-im1-9, D17) — `Bands.cs`

Parallel loops (the coverage assignment, Sauvola, both passes of the distance transform) cut their rows or columns
into bands of a **fixed** size (64) that does not depend on the thread count, and each band writes only its own slice;
nothing is reduced in completion order. Every sort has a total tie-break; every dictionary that is iterated is iterated
in sorted key order. The CIELAB table is computed with `Math.Pow`/`Math.Cbrt` once per channel byte: two platforms whose
libm disagreed in the last place would disagree in a table entry, never in an order. `RasterDeterminismTests` runs
clustering, coverage, contours, the threshold, the transform, the skeleton graph and the segments on 1 and 4 threads
and twice, and compares the bytes.

## 5. IM-2 — the picture: where it came from, what kind it is, where it is kept

`src/Design/Imaging/ImageSource.cs`, `ImageKind.cs`, `ImageKeep.cs`, `ImageProvenance.cs`; `src/Cli/DocumentKinds.cs`,
`PictureKinds.cs`. Gates: `tests/Ui.Tests/Imaging/{ImageKind,ImageSource,ImageKeep}Tests.cs` and two cases in
`CheckAndExplainCliVerbTests`.

### 5.1 One record for three origins (R-im2-1) — `ImageSource`

- **File** — the bytes read once. **Bytes** — a clipboard picture (the GUI encodes the clipboard bitmap as PNG) with a
  suggested name, `pasted_image.png` when none. **Placed bitmap** — a `BitmapShape` (its `.clay`, DBU rect and layer) or
  an `EditableBitmap` (its `.csch`, rect and rotation).
- A placed bitmap's path resolves as the primitive's own does: `RefPath.Resolve(documentDir, ref)`, a rooted reference
  winning. (Both loaders already make the path absolute on load; an in-memory shape with a relative reference resolves
  the same way.) A missing file, or an empty reference, is refused with the sentence naming **Resolve Path…** — the
  command that answers it.
- The record keeps the **original bytes** beside the decoded raster: `ImageKeep` copies what the user gave, not a
  re-encoding, and the SHA-256 is of those bytes so a re-run on the same file compares equal. The original file
  **name** is kept, never its path. `Format` is what the bytes announce (`RasterImage.DetectFormat`, signatures only),
  so a PNG named `.dat` is kept as `.png`.

### 5.2 What kind of drawing it is (R-im2-2, R-im2-3) — `ImageKind`

Measured features, in the order they are taken; every threshold is a named constant in `ImageKind.cs`.

| Feature | Measured how | Threshold and why |
|---|---|---|
| Size | the decoded raster | under **64 px** a side: *too small to read (w × h px)* — below it a stroke and a letter are a few pixels (D16) |
| Flat share | pixels within **3 sRGB levels** of their right and lower neighbours on every channel | rendered drawings measure above 0.85 (the gate's layout 0.986, schematic 0.980); a Gaussian-noise gradient 0.0001. Below **0.6** the picture is graded like a photograph |
| Colours | `ColourClusters.Find` (IM-1) | one cluster: *a blank picture* |
| Ink | pixels whose cluster is not the background (the border-majority cluster) | under **0.05 %** of the picture: *a blank picture* — 120 pixels of a 600 × 400 picture is dust, not a drawing. The colour mask rather than a grey threshold, so a light copper colour is ink |
| Filled share | ink pixels deeper than **max(3 px, 0.6 % of the shorter side)** from the background | a schematic's strokes are 1/150–1/400 of its side and never that deep (the gate's: 0); a layout's pads and pours mostly are (0.85) |
| One-width share | skeleton length (pixels) on edges whose mean width is within **±30 %** of the dominant width | one pen draws a schematic (1.0); a layout's pads and traces spread |
| Straight-run share | `Segments.FromSkeleton` runs longer than **10 stroke widths**, over the skeleton length | below **0.3**, no line work — a letter's stem is short; a wire is long |
| Text share | ink in components **5 px to max(14 px, 5 % of the height)** tall, no wider than 3 heights, with a like-height neighbour on the same row within 1.2 heights | above **0.6** with no line work: *a page of text*. The first pass of IM-9's grouping |
| Chromatic ink | ink clusters with CIELAB chroma above **20** | two or more lean layout — a weak vote, because a schematic tool may draw wires and symbols in two colours |

**None**, in order: too small; blank; **photograph-like** when the flat share is below **0.2** whatever else it reads,
or below 0.6 with no line work; a page of text. The 0.2 floor exists because below it nothing is flat, the ink mask is
the noise itself, and the skeleton of noise is a mesh whose merged junction nodes join into long, perfectly straight
two-point edges — the Gaussian-noise gradient measured a straight-run share of 0.34 and would otherwise have passed as
line work.

**Schematic or layout** is a weighted vote, each feature linear across a span about its centre: ink share (centre 0.11,
span 0.05, weight 1), filled share (0.3, 0.2, weight **1.5** — the strongest single tell), one-width share (0.6, 0.2,
weight 1, schematic-positive), chromatic ink (weight 0.5). The sign picks the kind; confidence is
`0.5 + 0.5·|score|`. The gate's layout reads 0.78, its schematic 0.94.

**The override** (R-im2-3): `ImageKindResult.Force(kind)` sets Schematic or Layout, keeps the reading it overrode in
`Suggested` and its evidence, and is recorded as `KindForced` in the provenance. A forced read of a None picture is not
refused for its kind; D16 refuses it later only if nothing at all is recognised. `ImageKind.TryParseOverride` reads
`--image-kind auto|schematic|layout`.

### 5.3 Where the picture is kept (R-im2-4, D4) — `ImageKeep`

`ImageKeep.Into(cellDir, cellName, source)` writes the original bytes to `<cell>.source.<format>` at the cell folder's
top (a sibling temporary renamed into place). The same name holding the **same hash** is reused and nothing is written;
a different hash moves to `<cell>.source-2.<format>`, `-3`, … each checked the same way, and no existing file is ever
overwritten. `KeptPicture.RefFrom(documentDir)` is the stored relative reference a document uses —
`../amp.source.png` from `amp/schematic/`. Nothing before the user's Create calls it, so a cancelled dialog leaves
nothing behind; reading and classifying write nothing (the gate checks the folder stays empty).

### 5.4 Provenance (R-im2-5) — `ImageProvenance`

The kept copy's reference, the original name, the hash, the pixel size, any reduction factor, the kind and whether it
was forced, the scale and its evidence (IM-3), the layer map (IM-3), the reading options, the circuitRF version (AS-6's
reading of it) and the time. Stored as an **`ImageSource` block** beside AS-6's `ArtworkSource` in the `.csch`, and the
same block at the top of a `.clay` (`ClayFile.ImageSource` ↔ `LayoutView.ImageSource`). Optional and null by default,
so no `FormatVersion` bump and every existing file re-serialises byte for byte. Its presence is what marks a result as
this command's to replace (AS D4's rule).

### 5.5 The CLI names a picture (R-im2-6)

`DocumentKinds` gains **`picture`**: by extension (`.png`, `.jpg`/`.jpeg`, `.bmp`, `.gif`, `.webp`), and by signature
when a NAMED path's extension is missing or wrong — checked before `convert`'s classifier, which reads text formats.
A BMP's two-byte signature is confirmed by its info-header size, or every file starting "BM" would be a picture.
`check x.png` reports `check.picture.kind` (or `check.picture.no-drawing` with the phrase) as an INFO and exits 0; an
undecodable picture is `check.file.unreadable` carrying the decoder's own refusal. A folder walk skips pictures, as it
skips Touchstone: a workspace's screenshots are not documents. `find` lists every picture in a workspace with its kind
(`--no-analyses` lists the paths alone, since each kind is a decode). `explain x.png` prints the format, the size (and
any stated resolution, never used as a scale), each measurement with the way it leans, and the kind; on a schematic it
also prints the `ImageSource` block. None of them reads past IM-2. Every other verb that switches on the kind refuses a
picture by name (`render`, `recognize`, `lvs`, the run verbs); `read`, whose catch-all reads a document's text, gets an
arm of its own (`read.file.picture`) — without it a picture that used to be refused as an unknown extension would have
been returned as its bytes decoded as text.

## 6. IM-3 — a layout picture traced to layout shapes

`src/Design/Layout/Recognition/Image/`: `ImageTrace.cs` (input, options, result, `Trace`), `ImageTraceTarget.cs`
(`Run` and its three targets), `ImageScale.cs`, `ImageLayerMap.cs`, `ImageTracePresets.cs`; the scale search is
`LandPatternMatch.ScaleSearch`. The GUI's half is one helper, `src/Ui/Recognition/ImageTraceEditCommand.cs`. Gates:
`tests/Ui.Tests/Imaging/{ImageTraceGeometry,ImageScale,ImageLayerMap,ImageTraceTarget}Tests.cs`, every picture drawn in
memory from a known layout.

### 6.1 The split (R-im3-1)

`ImageTrace.Trace(input)` is pure — it posts nothing and writes nothing — and `ImageTrace.Run(input, target)` traces and
then writes, the split `ArtworkRecognition.Recognize`/`Run` already has. Two stages:

1. **Pixels** (`Prepare`): IM-1's clusters; the layer map (Auto, or the user's edited one rebound by colour); per mapped
   layer the clusters' coverage maps **summed** (an overlap colour counts towards both of its layers, so a pixel half
   top-only and half overlap is fully top), traced by IM-1's contours, despeckled, de-blipped (§6.4); the drill
   circles; the rectangles on each copper layer, as pads for the scale search. Everything the dialog's overlay needs,
   and everything the scale needs.
2. **DBU** (`Build`), only once there is a scale: one `PixelFrame`, the shapes, the vias, the outline paths, the
   silkscreen centre lines, the underlay, the resolution line.

A trace is refused for no technology or **no shape on any copper layer** (*the picture has no copper-coloured regions
at the mapped colours*). With no scale it is not refused — the result has the clusters, the map, the candidates and the
pixel regions, and no shapes — which is the dialog's waiting state; `Run` refuses it, listing what evidence it found.

### 6.2 Scale (R-im3-2, R-im3-3, D6)

`ImageScale.Candidates` lists every piece of evidence; `Choose` takes the placed bitmap's rect when the target is its
layout, else what the user stated (a number, two points and a distance, or a trace of stated Z0), else land patterns
with support ≥ 3, else nothing. Resolution metadata is listed with support 0 and never chosen.

- **Placement**: the rect over the pixel size, along each axis separately, so the trace lies on the bitmap even when it
  was stretched (which is reported when the axes disagree by more than 1 %). The layout's own DBU per micron is read
  from its file (`LayoutPersistence.TryReadUnits`).
- **Two points**: a distance whose text does not end in a unit is refused, naming the three plausible spellings.
- **Parts**: `LandPatternMatch.ScaleSearch` runs `Find` — the artwork matcher's own fit, not a second one — over a 1 %
  log-spaced sweep of 0.5–500 µm/px; a scale's support is the pairs whose RMS error there is within 3 %. Each peak is
  refined to the mean, in log scale, of the scales its pairs fit exactly (every dimension of a pair against its case,
  least squares), then counted again. Peaks within 3 % are one scale. Density aliases do not compete: the densities
  differ by fillet *offsets*, not a factor, so no other scale fits every dimension of a pair within 3 %.
- **Impedance**: `LineCalculator` gives W for the stated Z0 on the layer; the picture's width at the picked point is
  `h·v / √(h² + v²)` from the coverage summed along its row and column — exact for a straight strip at any angle.
- **Resolution line**: `1 px = 10 µm; a 160 µm trace is read to ± 3 %` — half a pixel over the narrowest line, whose
  width is the strip `L·W = area, L + W = half the perimeter` solved for W (a disc has no real root and is not a line).

### 6.3 Layers (R-im3-4, R-im3-8, D7)

Auto, in order: the background (most border contact, ties to the lightest — not the largest share); Drill (≥ 80 % of
the components round and ringed by other non-background colour); Silkscreen (nothing deeper than a few pixels, and
IM-2's text share ≥ 0.3); **overlaps**; the rest by area onto the stackup's conductors, top first, any extra ignored.

An overlap is tested with the compositing a viewer actually does. A top layer of colour T and opacity α over a
background W shows `P = αT + (1−α)W`; over a bottom layer showing Q it shows `αT + (1−α)Q = P + β(Q − W)` with
`β = 1 − α`. So a colour within ΔE 12 of `P + β(Q − W)` for some β is the overlap of P over Q — the same formula holds
when both layers are translucent. A plain mix of P and Q (the segment between them) is the wrong test: it misses the
`−βW` term. An overlap is never larger than both of its parents.

An edited map is never replaced: the input's map, when `Edited`, is rebound to the re-run's clusters (nearest colour
within ΔE 8), and an unmatched cluster is ignored. Presets are the same rebinding from a file in
`<UserStateDirectory>/image-presets/`; one is offered when every preset colour has a cluster within ΔE 8 and every
cluster a preset colour.

### 6.4 Shapes (R-im3-5, R-im3-6)

- **Rectangles** (four vertices, axis-aligned, no holes) become `RectShape`, **circles** (≥ 8 vertices on one fit,
  area within 6 % of the disc) `CircleShape`, everything else `PolygonShape` with its holes.
- **Drill circles become vias**: the technology's via barrel layer (a plated via entry first), landing on the topmost
  copper region holding the centre; when that region is a ring around it, its outer diameter is the pad and the ring
  is consumed — the via owns that copper, as `DrillViaPairing` makes a paired flash the via's. Otherwise the pad is
  the technology's default via pad (or twice the drill). A hole a drill sits in is dropped from the copper polygon. A
  drill cluster with no via layer is reported and written on no layer.
- **Outline** is the skeleton's edges as paths at the measured stroke width; **silkscreen** is filled shapes *and* its
  centre lines, kept in the result for IM-4.
- **The underlay** (D5): the picture as a locked `BitmapShape` at 35 % at exactly `frame.ToTarget(0,0)`…
  `frame.ToTarget(w,h)`, on the first documentation layer, else on the layer with the most copper (reported). Not
  written into the layout the bitmap already sits in. `Trace` points it at the source file; `Run` at the kept copy.

**Junction blips.** Where three or four colours meet in one pixel — a bottom layer's corner landing on a top layer's
edge — the pixel is no two-colour mix (IM-1's model, §4.2), and the top layer's edge picks up a third-of-a-pixel
excursion that splits one snapped edge into two, each snapped about its own centroid a hair apart. A vertex-only test
does not see it: the neighbours are not on one line. `Deblip` merges two edges on one snap direction whose lines are
within ½ px, joined by at most four edges totalling ≤ 4 px that stay within ½ px, onto their length-weighted common
line, sliding the outer ends along the edges before and after so those keep their directions.

**Rounding that keeps a snap.** Pixel coordinates leave through one `PixelFrame` and are rounded per EDGE, not per
vertex: an axis edge's one coordinate and a 45° edge's `x ∓ y` are rounded once, and each vertex is solved from the two
edges meeting at it. Rounding each vertex's x and y alone leaves a 45° jog a DBU off 45° as often as not.

### 6.5 Targets (R-im3-7, D14)

*New cell* creates the folder, keeps the picture (`ImageKeep`), and writes the layout view with the technology
reference, the shapes, the underlay pointing at the kept copy and the `ImageSource` provenance (scale and its evidence,
the layer map, the options). *Replace* rewrites a cell whose primary layout carries that block, after a history
checkpoint, and refuses any other. *Into the layout* returns an `ImageTraceEdit` — the shapes and the `.clay` — and
writes nothing; `ImageTraceEditCommand.For` chains the editor's `AddShapeCommand`s into one `CompositeCommand`, so one
Undo takes the trace off the picture.

---

## 7. IM-4 — a layout picture made into a schematic

**Code:** `src/Design/Layout/Recognition/Image/ImageRecognition.cs`, `PictureTechnology.cs`,
`RecognitionInput.FromTrace` (in `ArtworkRecognition.cs`), `Silkscreen/RasterSilkscreenEvidence.cs`.

This phase is glue, and the measure of it is how little of the artwork pipeline it touches: **nothing in AS-3 … AS-6
names a picture**, and a comment-stripped source scan (`RasterSilkscreenEvidenceTests.NoArtworkStage_NamesThePicture`)
holds it: no file under `Recognition/` names `ImageSource` but `Image/`, the raster evidence source and the file holding
`FromTrace`.

### 7.1 The one conversion (R-im4-1, R-im4-2)

`RecognitionInput.FromTrace(trace, technology, options)` hands the recognition the traced shapes as the flattened
artwork, on a `LayoutView` holding them, with the user's technology (D8), no `.cem` and no placement or BOM unless the
caller adds them. The silkscreen's filled shapes are left out of the artwork — the silkscreen arrives as an evidence
source read from its strokes (§7.3), and its fills would otherwise be counted as filled text the reader does not read.

`ImageRecognition.Circuit` is the no-write form (the CLI's read-only default, the dialog's preview). `Run` traces
**once**, recognises, emits, draws with `NetlistSchematic.Build` and the artwork hints, and only then writes: the new
cell (D14) holding **both** views — the traced `.clay` (through IM-3's own write, `ImageTrace.Write`, so the layout is
byte for byte the one Create Layout from Image would write) and the `.csch` — each with the kept picture under it. A
refusal at any step before the write writes nothing.

### 7.2 What a picture does not state — `PictureTechnology` (R-im4-3)

The stackup is the user's, but a picture states only what it draws, and the recognition reads three things from the
**technology** that almost no picture draws. Each is answered through that one seam: the recognition reads a copy of the
technology (an ordinary one — nothing is told it came from a picture), and the report says once what was implied.

| The technology declares | A picture | So the copy |
|---|---|---|
| a reference conductor (the plane) | shows the top copper only | draws that conductor on no layer — AS-3's *undrawn reference*: the plane is metal at ground, every via carried down to it reaches ground, and the trace review reads a microstrip over it. Ground is the largest top-side pour (AS-3's own `IsWide`, handed in as the ground point a user would have clicked) when there is one, else the plane alone, reached through drill circles — AS D7's VIAGND |
| solder mask and paste | shows no mask | leaves out every layer the picture draws nothing on (the stackup's own excepted), so AS-4 reads pads from the copper as it does for a technology with no mask |
| nothing about the board's edge (no outline drawn) | stops where the board stops | adds the traced frame — the scope, else the whole picture — as a rectangle on the Outline layer. Without it AS-3 takes the copper's extent, and a line lying along that extent (the first line of a board, beside its bottom edge) became an edge port in its middle |

The frame outline is in the recognition's input only, never in the written layout. A picture that maps a colour to the
board outline keeps its own.

**What a copper picture cannot state.** AS-4 reads a land pattern from the mask's openings where the technology has a
mask, and from the copper where it has none; a part standing **on** its line — a shunt part whose first pad is centred on
the trace — is a pad inside a line, which only an opening shows. A picture of the copper alone does not find it, exactly
as the artwork read without its mask does not; the via to ground under it is then reported as a stray. The gate compares
like with like for that reason (`ImageRecognitionTests`' header).

### 7.3 Silkscreen from pixels (R-im4-4) — `RasterSilkscreenEvidence`

An `IPartEvidenceSource`: the skeleton strokes IM-3 kept for a *Silkscreen* cluster, already in DBU through the frame, are
the centre-line form `StrokeGlyphs` reads a Gerber pen's strokes in, so the AS-10 matcher, its designator rule, the
association and Learn These Glyphs (through `PartClaim.Line`) apply unchanged. Strokes under 1.5 px are reported too
thin and contribute nothing.

One repair on the way in: **a skeleton cuts a sharp corner.** The "2"'s foot came back 1.7 px inside the font's apex at a
3 px pen, and the built-in font sets "1" and "2" 0.57 cap heights apart against the matcher's 0.6 letter-gap limit, so
"C12" read as "C1" and a stray "2". `RestoreCorners` simplifies each stroke to half a pen and moves every corner sharper
than 120° — a vertex, or a chamfer under 1.5 pens — to where its two legs meet, each leg fitted to the resampled original
centre line clear of the corner by 1.5 pens. The restored apex landed on the font's own to 0.1 px, and every glyph's
match distance fell (the "C" 0.043 → 0.016).

Auto (IM-3) maps a colour to Silkscreen only when its strokes read as rows of text; one short designator does not, so a
picture with a single word of legend needs the colour mapped by the user.

### 7.4 Provenance, the underlay and a re-run (R-im4-2, R-im4-5, R-im4-6)

The schematic's `ArtworkSource` names the traced `.clay` (so Show in Artwork opens it); both views carry the
`ImageSource` block, with the picture referenced relative to each. The schematic's underlay is placed by the drawing's own
artwork hints: the similarity — one scale, the y axis flipped, no rotation, so the picture keeps its aspect — that best
carries each component's copper onto where the drawing put it; with fewer than two usable hints, the picture spans the
drawing's width. Locked, 35 %, behind everything (D5), and off with the same setting as the layout's.

A re-run onto a cell this command made (its layout carries `ImageSource`, and its schematic, when it has one, both
blocks) replaces both views after **one** history checkpoint, intent *Create Schematic from Image*.

The report is the trace's findings, then what the picture did not state (the implied plane, the silkscreen read), then
the recognition's — with two of its sentences reworded where what they say of the technology is the picture's doing:
the ground point nobody clicked, and the mask the picture did not draw.


## 8. IM-5 — the dialog

One window for every source (`src/Ui/Recognition/CreateSchematicFromArtworkDialog`). The body every source shares —
target, the recognition's options, the parts table, the report, Create — is `RecognitionSessionViewModel`, with the
edits-as-CSV-overlay and the debounced, cancellable re-run AS-8 already had; the two sources subclass it:
`CreateSchematicFromArtworkViewModel` (a layout on disk) and `ImageSourceViewModel` (a picture). Each row is its own
control (`RecognitionTargetRow`, `RecognitionOptionsRow`, `PartsTablePanel`, `RecognitionReportStrip`), so none exists
twice; the window picks the source pane and the body by a `DataTemplate` on the view model's type.

The picture source decides nothing. Its preview is `ImageTrace.Trace` (Make Layout) or `ImageRecognition.Circuit`
(Make Schematic), its write `ImageTrace.Run` or `ImageRecognition.Run` — behind `IImageRecognitionRunner`, so a test
records what crosses. Every control is a field of `ImageTraceInput`/`ImageTraceOptions`/`ImageScaleStatement`/
`ImageLayerMap`. The overlay (`ImageOverlay`) is built from the reading only, carried into pixels through the trace's
`PixelFrame`; the canvas draws it. For that the trace result gained `Drills` (the circles in pixels, before any scale),
`ImageTraceOptions` gained `MergeDeltaE` (written to the provenance only when not the default), and `ImageLayerMap`
gained `WithRole`, which fills a Drill/Outline/Silkscreen row's layer as Auto does.

Two rules of the dialog that are easy to break: an edited layer row is held as an edited map and REBOUND by the next
trace, never replaced; and Make follows the kind only while the kind forces it — a misread Auto that forced Make to
Schematic must hand the user's Make back when they set the kind to Layout.

## 9. IM-7 — a schematic picture: wires, words and symbol regions

`src/Design/Schematic/Recognition/` splits a schematic picture into the three things it is made of and says which wire
ends reach which symbol. Nothing here names a part (IM-8) or reads a word (IM-9). The entry point is
`SchematicImageReading.Read(ImageSource, SchematicImageOptions, RunControl?)`; it is pure, and on a refusal it still
returns everything it read, so the overlay can show what was seen. Every tolerance is in units of the stroke width w.

### 9.1 Normalise (R-im7-2) — `SchematicImageReading.cs`

A coloured picture keeps its **darkest** colours — those at least half as far from the ground's lightness as the
darkest is — so a pale highlight or a tinted grid drops out and a coloured wire layer stays. A lean between 0.3° and
5° is straightened by turning the picture onto a larger white canvas; `ToSource` takes a point back. Then the border
(a component reaching over 60 % of the picture each way and inking 90 % of each side of its box) is removed **with
everything drawn on it** — a title block and zone marks are connected to the frame, and a wire never is — and a dot
grid (25 or more dot-sized components, most on one lattice).

Two traps, both measured on a 2 px picture:
- **w is the mode of the shorter ink run through each skeleton pixel**, not IM-1's 2·DT − 1. The distance transform
  reads only odd widths on an orthogonal stroke: a 2 px wire is one pixel from the background on both sides and reads
  1, which halves every tolerance.
- **The lean is measured on runs cut where the skeleton leaves a line by 1.5 px.** A wire at 1.2° binarises as a
  staircase of level steps; the skeleton's own 0.5 px polyline keeps every step, and each step reads exactly level —
  the first version measured a lean of 0 on a 1.2° picture.

### 9.2 Words (R-im7-3) — `TextRegions.cs`

The candidates are the small pieces left when every horizontal and vertical run longer than the tallest glyph
(`MaxTextHeight` w) is set aside — so a label touching a wire is still a piece of its own. They are grouped by AS-10's
own `StrokeGlyphs.Groups`, fed boxes instead of pen strokes, horizontal first and then vertical on what is left; a
group is offered whole and split at its widest gap. A group touching the set-aside line work along more than 2 w, or at
**two separate places** (a capacitor's plates on their leads, an inductor's humps, a hop's arc), stays as symbol
detail and is reported — unless, without its touching pieces, two glyph-height pieces remain, which is a supply's name
beside the two halves of its bar. A word's pixels are removed before the wires are traced, and its skeleton strokes are
kept for IM-9. A single glyph is never a word: `Groups` needs two.

### 9.3 Wires (R-im7-4, R-im7-5) — `WireGraph.cs`

Straight runs of the skeleton are cut where the chain leaves a line by more than a pixel and turns by more than 8°. A
run is a wire candidate when its RMS residual is under 0.25 w (never under 0.6 px — one pixel step is 0.5) and it is
orthogonal and over 3 w, or diagonal and over 8 w.

**The brief's criteria alone read a capacitor's plates, a ground's bars and a resistor's box as wire** — each is a
straight orthogonal run longer than 3 w. Two shapes are therefore set aside as symbol strokes before the graph is made:
- a straight line (runs paired straight through nodes) of at most 16 w whose **both** ends stop in bare paper — a
  plate, a bar. A wire ends on a symbol, a junction or at most one net label;
- every candidate on a **cycle** of candidates (bridge-finding, with a four-way crossing's two straight pairs kept
  apart). A loop made only of wire shorts two nodes and nobody draws one; a box or a triangle outline is exactly that.

**Two further shapes are symbol, found by IM-8's gates on drawings IM-7's own did not try:**
- **A filled area** — what survives an opening by a disc 1.5 w in radius, larger than a dot or far from round, with its
  anti-aliased rim — is taken out of the wire mask before the skeleton is made and put back into the residue. The
  skeleton runs straight through a filled rectangle between two collinear leads: without this an IEC inductor read as
  one wire with a junction dot on it, and the picture was refused for having no symbol.
- **A small box's sides.** A run within 12° of an axis whose both ends meet runs near the other axis is snapped to its
  axis. A 3 w side of a box leans 6.5° on the pixel steps of its two corners, and a half side beside its lead's T 3–5°;
  read as diagonals they were no wire candidates, the box's cycle stayed open, and its other sides went to the wire
  graph as wire. A circle's flat bottom leans as little but never meets a square corner, which is why the rule asks
  for both ends — snapping every run that rises less than a pixel turned a terminal circle's bottom into a wire.
- **A small round loop's runs** (IM-10). A terminal circle of radius 4 w at an integer centre rounds its bottom onto
  the grid as a LEVEL run of 3 w — a wire candidate by length, on neither rule above (its ends are not bare, and its
  cycle is closed by curved, non-candidate runs). It gave the circle three attachments, and the terminal was cut out as
  a three-pin device. A run cut from a skeleton edge that closes on its own node, no longer than a 12 w circle and
  simplifying to six vertices or more, is never a wire: no wire is part of a loop, and the size and roundness limits
  keep a box's straight sides as the candidate cycle that sets the box aside.

Ends cluster into nodes within 1.5 w, or within one junction dot (a filled blob 2–6 w across, found as a component of
distance-transform peaks); an end on another wire's interior splits it (a T). A degree-2 node with two collinear wires is
dissolved — and two wires both read horizontal (or both vertical) are collinear whatever their ends' rounding says. A
degree-3 node connects; a degree-4 node connects per `CrossingRule` (*Never* / *With a dot* / *Always*); each is in
`Crossings` with its reading. Nets are counted with a not-connected crossing's two straight pairs kept apart.

### 9.4 Symbols (R-im7-5 … R-im7-7) — `SymbolRegions.cs`

The residue is the wire mask with every wire painted out (a capsule w/2 + 1 px wide, flat at an end that meets
something, round at a bare end so the stroke's cap is not left behind as a speck) and the junction dots on nodes of
three or more. Its pieces are grown by 1 w and joined, then merged when they face each other across a gap shorter than
the larger one's extent along the faces — **unless a wire runs through the gap**, which is a lead between two parts (a
shunt capacitor and the ground under it face each other across their lead). An end within 1.5 w of a region is its
attachment, with the direction its wire arrives in.

A **hop** is two attachments facing each other 2–10 w apart on one line with another wire between them. The wire hopped
passes under the arc's apex and cuts it in two, and the rule above keeps the halves apart, so a hop is one region with
both ends or two small regions with one each. Its two halves become one wire crossing the other at a `Hop` crossing,
whatever the rule. A region with one attachment that is a short bar across the end, or an arrow whose apex the wire
reaches, is a **supply mark** named by the word beside it; a word within 2 w of a dangling end and in line with its wire
is that wire's **net label**; a region with no attachment is a **decoration**, reported and dropped.

The refusals are the brief's two: no wire at all, and wires with no symbol region.

## 10. IM-8 — symbols: what each region is, which way it faces, where its pins are

`SchematicSymbols.Read(SchematicImageRead, IImageSymbolClassifier?)` names every symbol region IM-7 found. It asks the
classifier and nothing else (R-im8-6); the template matcher, `TemplateSymbolClassifier`, is the one implementation, and
D9 is applied to its answer the same whichever classifier gave it. Pure.

### 10.1 Templates (R-im8-1, R-im8-2) — `SymbolTemplates.cs`

**Built-in** templates are sampled from `BuiltInSymbols.Primitives` — the one source of the geometry every renderer
draws — for the resistor, inductor, capacitor, ground, Term and Pin, TLIN, MLIN, CPWG and SLIN, the diode, the two FET
and the two BJT glyphs. The brief asks for them to be drawn "with the renderer"; `src/Render` references `src/Design`,
never the reverse, so the templates read the primitives the renderer strokes instead — the same single drawing, and if
a symbol's look changes its template follows. The **stubs** have no symbol of their own and are composed of the TLIN
drawing as TermG is of Term and Ground: one lead left off (open), and a ground's bars set against its far end (short).

**Alternates** are stroke files in `src/Design/resources/image-symbol-templates/`, one drawing each, embedded by a glob,
so adding a drawing is adding a file: the IEC resistor and inductor, zig-zags of four, five, seven and eight vertices
(circuitRF's own has six, and a zig-zag with a different count is 0.9–1.4 w from it — most of the error budget), coils
of four and three humps, the flat, curved and polarised capacitors, the triangle and chassis grounds, the terminal
circle, the coaxial and box transmission lines, the amplifier and the IC box. **Each is drawn in the frame of its
kind's built-in symbol** (two-terminal parts upright, pin 1 on top; lines and amplifiers across), so an orientation
means the same thing whichever drawing matched — IM-10 places the built-in with it.

A drawing becomes a template in one step whatever its source: each pin's **lead** — the straight run from its tip to
the first turn — is cut off, because on a picture it is wire and IM-7 took it away. A pin is where its lead met the body,
with the direction the lead left in; the body is scaled to unit pin span. Text and filled dots (the inductor's polarity
mark) are left out. A template drawn with no filled shape is **hollow** and is never tried on a filled region — a
hollow rectangle is the IEC resistor and a filled one the IEC inductor, and without the rule a diode's filled triangle
lost to the transmission-line boxes.

### 10.2 Matching (R-im8-3) — `SymbolMatch.cs`

The region's ink is **hollowed** — a filled area reduced to the one-pixel ring at its edge — and skeletonised into
centre lines. Every template with as many pins as the region has attachments is tried in the eight orientations (mirror
in x, then four turns — `SchematicGeometry.LocalToWorld`'s order) and every assignment of its pins to the attachments
whose wires arrive within ± 20° of the pin's lead; it is scaled and moved so its pins lie on the attachments (least
squares; one pin: the body's size against the region's), and scored by **AS-10's own `Glyph.Distance`**, the
modified-Hausdorff distance, on the stroke set — `Glyph` gained a sample count (128 here, 48 for a glyph), nothing else.
In units of w. Accepted at ≤ 1.0 w and at most 85 % of the best template of a **different** kind; drawings of one kind
never compete. A later orientation must win by more than 0.05 w, so a symmetric drawing settles on the first in
order (unmirrored, R0 first) rather than on pixel noise.

**A pin is fitted where its wire meets the ink**, not where the wire ends: the first ink within 3 w straight ahead of
the attachment. A wire cut away from a filled body ends about 2 w short of it, and fitted at the wire end a template
was scaled 15–25 % too long.

Two real ambiguities, both reported rather than resolved: MLIN's plain box against the IEC resistor (a rectangle a
third as wide as it is long is either), and a hollow rectangle drawn as an inductor (it reads as the resistor).

### 10.3 Structure checks (R-im8-4)

In the frame from the first pin's contact to the second's (a ground's: its one attachment, inward):
- **capacitor** — a band of bare paper across the axis between the contacts, with ink at least 2.5 w long on each side.
  Two bars joined at their ends (a drawn rectangle) have none.
- **zig-zag** — the centre line's mean offset, binned by w, swings past a third of its amplitude to alternate sides at
  least three times.
- **coil** — the centre line's *nearest* approach to the axis on the bulge side, binned by a pixel, comes within w/2 of
  its peak and drops a whole w below it three times, and the other side is reached by under a third of the peak (a
  zig-zag reaches both). A stroke width, not a fraction: where two humps meet their strokes run together, and the
  skeleton of a 6 px hump drawn 2 px wide comes no nearer the axis than 4 px.
- **ground** — the ink's extent across, row by row going in, never grows by more than w and ends 2 w shorter than it
  starts. The chassis ground's tines do not shrink, so it has no check.

The best template failing its check is set aside (a `SymbolDemotion`) and the next taken.

### 10.4 D9, the record and the report (R-im8-5, R-im8-7, R-im8-8) — `RecognisedSymbol.cs`

Three or more attachments, or a transistor, amplifier or IC match whatever the count, is **cut out**; two attachments
and no match is kind `?`; one and no match is an **unread terminal**, left open. `RecognisedSymbol` carries the region,
kind, disposition, template (its convention), orientation, pins in template order with their wire nodes, score,
runner-up, confidence (`PartConfidence`: High within half the threshold and twice the margin with nothing demoted) and
demotions; `SymbolReport` counts symbols by kind, unknowns, cut-out devices with their pin counts, demotions by check and
unread terminals.

## 11. IM-9 — words: designators, values, net and port names

`ImageText.Read(SchematicImageRead, IImageTextReader?)` reads every word region IM-7 found; `LabelAssociation.Associate`
attaches what was read to IM-8's symbols and IM-7's wire ends. Both pure.

### 11.1 The reader is AS-10's (R-im9-1) — `ImageText.cs`, `IImageTextReader.cs`

A word region's skeleton is a set of centre lines — what `StrokeGlyphs` reads a silkscreen pen's strokes as — so a word
is read by `StrokeGlyphs.ReadWord`: AS-10's split into glyphs, its normalisation to the line's cap height and its
modified-Hausdorff distance, with no distance limit and no designator rule (the grammar decides). The skeleton is taken
into y-up coordinates; a level word is read at 0° only and a turned one at 90° or 270° — a schematic is never upside
down or seen from behind. `IImageTextReader` is the seam (D1): `StrokeTextReader` is the one that ships, and an operating
system's recognition would replace it and nothing else.

Four things the silkscreen path never met, each found by the gate words:
- **Corner repair is not used.** `RasterSilkscreenEvidence.RestoreCorners` puts back corners a pen-drawn stroke font's
  skeleton cuts; on a typeset F it moved the top-left corner outside the glyph, and the F read as a T.
- **End kinks are trimmed.** Thinning curls a pixel or two at a square stroke end; small as it is it widens the
  glyph's box, and a glyph is centred on its box — IBM Plex Sans's I stood 0.08 cap heights off centre and read as a t,
  so `IN` was the name `tN`. A FREE end's final run of at most min(pen, 2 px) is dropped when it turns more than 35°
  off the next four times that, which must be straight. Each limit was found the hard way: trimming junction ends cut
  an N in two, and trimming against a pen's length (4 px in a bold face) turned a bold 2 into a 7 — a wrong reading,
  where the untrimmed one had been right.
- **A full stop has no skeleton** (a 2 × 3 px dot thins to an isolated pixel with no edge). A glyph box with no skeleton
  point in it becomes a dot stroke at its centre.
- **Touching glyphs are cut.** A typeset k and Ω touch at 14 px and are one component, which reads as a poor m. A glyph
  reading worse than 0.1 cap heights and at least 0.7 wide is cut across the line at each twentieth from 30 % to 70 %,
  strokes stopped a pen short each side; the best cut is kept when both halves read better than the whole, and the cut
  line stands only when it leaves fewer words unread — or as few, nearer the templates. A W cut in two is a t and a V,
  which say nothing; `1kΩ` uncut is the grammatical `1m`, read worse.
- **Spaces.** A skeleton stops short of its ink and a typeset digit is narrower than its advance, so letters of one word
  stand up to ~0.6 cap heights apart and a space is ~0.9: `SpaceGap` is 0.75. A line is read whole first (with its
  spaces, as a line set or a value with its unit apart — `0.5 pF`) and otherwise word by word, each word its own
  `ImageWord` with its own box. Two line sets on one line within their height of each other (`Z=50Ω, E=90°`, split at
  its comma-space by IM-7) are joined when together they still are one.

**Words are found for typeset text** (`TextRegions`, IM-7 code): AS-10's `Groups` takes `typeset: true`, under which two
tall units of a line may differ in height by 1.6 times (an `n` beside a `2`; a stroke font's capitals need 1.35), and a
word then **grows** by the pieces grouping left beside it — a `°`, a comma, the `Z` of `Z=50` whose `=` the rest of the
word took — while they sit in its band, within a letter gap, and touch no line work. The word's height there is its
tallest glyph's: a median over its members counted the `=` bars and the full stop, and a W was then too wide to join.

### 11.2 The glyphs (R-im9-2) — `GlyphTemplates.cs`

Two **classes**. Designator glyphs are all the silkscreen reader consults — `BuiltIn` and `ForUser` hold nothing else, and
`StrokeGlyphTests` holds that. Value glyphs — the font's own lower case and `. / = ° µ Ω ( ) ,` (one copy of the font:
`resources/stroke-font/`), plus `resources/silkscreen-glyphs/hershey-value-variants.txt`'s θ (the Simplex 0 with a bar)
and a typeset Ω (the Simplex Ω 1.4 times as wide: a sans-serif Ω's skeleton loses its feet and read as O at 0.078
against 0.096) — are added only by `GlyphTemplates.Text` / `ForUserText`. A taught glyph carries its class in
`taught.json` (`"class": "value"`; a designator glyph is written with none, as AS-10 wrote it).

### 11.3 The grammar (R-im9-3) — `ValueGrammar.cs`

A word is one of: a **designator** (`^[A-Z]{1,3}[1-9][0-9]{0,3}$` — AS-10's no-leading-zero rule); a **value**, read by
`BomTablePaste.TryReadValueOfAnyKind` — `TryReadValue` for no kind, then for each kind that could supply a unit left
off, returning every dimension that did (`10p` is a capacitance or an inductance; the part decides) — or `DNP`/`NC`
through `IsNotFittedMarker`; a **line set** (`Z`, `Z0`, `E`, `θ`, `L`, `W`, `F`, comma or space separated, each key
once; `Z=50G` is refused — an impedance never leaves its Ω off); or a **name**, `[A-Za-z][A-Za-z0-9_+-]{1,15}`, flagged
when it is a port word. **A name is at least two characters**: one glyph gives the grammar nothing to check, and a
stray mark is always near some letter.

Each glyph offers the characters within AS-10's runner-up margin of its best (`best / 0.8`) or within 0.02 of it,
whichever is wider, at most four — a clean typeset glyph at 0.03 would otherwise offer nothing past 0.0375, and IBM Plex
Sans's 5 and S, 0 and O, θ and 8 stand 0.01–0.02 apart; a beam of
512 combinations, cheapest first, is parsed; the best **rank** wins — designator, port word, value, line set, other name
— then a known designator prefix, then the total distance. The rank is why `IN` reads as the port word and not as
`1N`, a 1 nF value: an I and a flagless 1 are one bar. A designator keeps AS-10's rule: no letter where a digit fits
better, and a digit where a letter fits better only after a known prefix.

**Unread** is a word with no grammatical reading, a glyph farther than 0.2 cap heights from every template, or a mean
over 0.09. The mean is the line between text and scrawl: typeset DejaVu glyphs read at 0.02–0.07 (a W at 0.13), the
pieces of a random scrawl at 0.07–0.2, each near some letter — a ⊠ is a fair B at 0.055 and a filled scratch a t, which
is why the gate's scribble is a seeded random scrawl.

### 11.4 Association, teaching, the report (R-im9-4 … R-im9-7) — `LabelAssociation.cs`

Names first: a word IM-7 put at a wire end (`NetLabel`, `SupplyMark`) names that node, whatever else it reads as. Then
AS-10's `RefdesAssociation.Assign` (pixels carried in sixteenths), once per class: designators to every symbol but a
ground; values to R, L, C and `?`, then checked — a value whose dimensions do not include the symbol's is a
`KindMismatch`, reported and not attached (`?` takes any value: the value is evidence of what it is); line sets to lines
and stubs. A designator whose prefix plainly names another of R, L, C, D, Q than the drawing is kept, flagged, and the
drawing's confidence drops a step. A symbol with no designator gets the next free one of its kind's prefix (`?` → C, as
the artwork series generates it). `ImageText.Lesson(word, corrected)` is what a corrected picture row teaches: a
designator teaches designator glyphs, anything else value glyphs; IM-10's parts table calls it with
`GlyphTemplates.Learn`'s class-carrying overload. `ImageTextReport` counts words by class, unread (with boxes), kind
mismatches, contradictions, unattached words and generated designators.

### 11.5 A second face: IBM Plex Sans (shipped), the hard cases

Checked once by hand (not a gate) at the 14 px cap height of the gate words — `l0pF`, `1kΩ`, `2n2`, `Z=50Ω, E=90°`,
`0.5 pF`, `L=3.2mm W=0.3mm`, `θ=45°`, `IN`, `RFIN`, `C12`, `4R7`, `DNP`. **Regular** reads all twelve. **Condensed**
reads eight and leaves four unread (its Ω reads as O), none wrong. **Bold** at this size is past the matcher: its
strokes are 4 px, 0.29 of the cap height, so a `=` closes up and an `N`'s diagonal collapses — seven read, two unread,
and three are WRONG (`IN` → the value `1H`, `RFIN` → `RF1H`, `DNP` → `QmP`), each under the 0.09 mean.

**So heavy type is refused** (owner, 2026-10-10). `TextRegions` measures each word's own stroke width the way the
picture's w is measured (`InkWidth`: the mode of the shorter ink run through each skeleton pixel), and the reader
divides it by the word's cap height (the median of its glyph heights at least half the tallest, so `=` bars and full
stops do not count). Over `StrokeTextReader.MaxWeight` = 0.22 the word is unread with `TooHeavy`, and the report says
how many. Measured across DejaVu Sans and Plex at 14 and 24 px caps: regular faces 0.12–0.20, Bold 0.23–0.29. The run
width is whole pixels, so at a 14 px cap the steps are 0.14 / 0.21 / 0.29 — Plex Medium and SemiBold mostly land on
0.21 and are read; that is where the remaining wrong readings are (SemiBold `0.6 pF`, `1H`; Medium at 24 px `Z=60Ω`;
Condensed at 24 px `fN` for `IN`), and the next thing to look at if a field picture shows it. The same survey caught
`Z=5GΩ`: an impedance is now refused past a kΩ.

## 12. IM-10 — a schematic picture made into a schematic

`SchematicImageRecognition.Circuit` (no write: the CLI's read-only default and the dialog's preview), `.Run` (a cell)
and `.IntoSchematic` (the drawing a placed bitmap's schematic takes, D14) — `src/Design/Schematic/Recognition/`. All
three read the picture through IM-7, IM-8 and IM-9 and hand the reading to `SchematicImageEmit.Build`, which makes ONE
`TestBench`; the drawing is `NetlistSchematic.Build`'s, never a second writer.

### 12.1 The circuit (R-im10-2, R-im10-3) — `SchematicImageEmit.cs`

**Nets** are the wire graph's (crossings per D11 and the user's corrections), joined by equal names (net labels and
supply names, case-insensitively), a ground symbol's net made 0, and a dangling end the user dragged onto a pin joined to
it. A supply net is named by its word, else `Vsupply<n>`, and is listed as having no source.

**Parts go through the parts table** — R, L, C and `?` rows, and cut-out devices as `MultiPin` rows — so an edited CSV
is laid over a picture's rows by exactly the artwork's overlay rules (rename by X/Y, kind, value, model). The emit then
reads the rows, not the symbols: a `?` is a C (AS D10), an unread value a global variable `<Refdes>_<Param>` at the
row's transparent start with AS's tune range (`RecognitionEmit.TuneEntry`, now shared). A row is `Shunt` when one pin
is on ground, `Series` on two signal nets, `Bridged` on one, `Shorted` on ground both — measured, as on a board.

**Lines**: a TLIN from its line set — Z and E at F (F written, 1 GHz when unread), or, when the set gives a length, the
physical form with L and an εeff: the technology's (Hammerstad–Jensen for a Z microstrip on the dielectric under its
top conductor), else a variable. Every unread Z, E or εeff is a variable with a tune entry, as a part's value is. A
short stub is a TLIN to ground, an open stub a TLIN to a net of its own. A diode is the built-in Diode.

**Ports, D13 in priority**: port symbols (a read designator names one; a port word on its net names it otherwise); then
port words naming a net no symbol ports; only with neither, dangling ends within 4 w of the drawing's left and right
extremes, left first, top to bottom. The drawing's own ports are numbered by the numbers their names carry when every
one carries a distinct one, else left to right; a cut-out device's wired pins follow, device by device. No port: no
analysis, and the report line. With ports, AS D15's SP1.

A picture refuses only when nothing at all — part, port or line — was recognised (D16), or an edited table is refused.

### 12.2 The drawing as drawn (R-im10-4, D12) — `NetlistSchematic.Build(…, exact:)`

`ExactPlacement` gives each instance an origin and an orientation and each net its routes. **No hint, no change**: with
`exact` null `Build` runs exactly as before, which `NetlistSchematicHintsTests` holds with a hash taken from the code
before the parameter existed. With hints, `PlaceExact` puts each hinted instance at its snapped origin, in netlist
order, unless one of its pins lands on another's pin of a different net, inside another's glyph (a quarter cell in from
its box), or its glyph over another's pin — then that one alone goes to the automatic "everything else" rows below the
drawing and is named in `PlacementFallbacks`. `WireExact` snaps each run, makes it orthogonal (a diagonal step becomes
an L) and re-joins each pin the runs miss with at most one dog-leg leaving along the pin's lead. **A net whose wiring
would touch another net's wire or pin is labelled at its pins instead** — snapping can bring two runs onto one grid
line, and a label is a real connection where a touching wire is a short. Every separate group of a net's wires carries
the net's name, because a picture joins two runs by giving them one.

**The frame**: source pixels × k, with k the median over two-pin instances of the symbol's own pin span over the
picture's (the fallback 100 / 4 w). **The fit** tries all eight orientations against `NetlistSchematic.PinOffsets` — the
pins the drawing will use, so the fit and the drawing cannot disagree — least squares over the pins seen; a one-pin
symbol (a port, a cut-out pin, a stub) is turned so its lead points back along the wire it is on. **A not-connected
crossing's two straight halves are ONE run** in the drawing: as two runs both would end on the crossing point, and four
wire ends at one point are connected in a schematic, whatever the picture said.

### 12.3 Targets, underlay, provenance (R-im10-5, R-im10-6, R-im10-10)

A new cell, or a replace of a cell whose schematic carries an `ImageSource` block of kind schematic (a checkpoint
first). The picture is kept with `ImageKeep`; **As drawn** lays it under the drawing at the frame, locked at 35 %;
**Tidy** does not, and says why. `IntoSchematic` frames the drawing on the placed bitmap's rect, adds no underlay, gives
a variable or a net name the schematic already has a suffix (and reports it), and drops the analysis when the schematic
already has one; the GUI adds the result through `SchematicPasteCommand`, `PlaceNetLabelCommand` (a label a paste
would drop, at a pin), `SetTuningSetupCommand` and `AddAnalysisCommand`, chained into one `CommandBatch`. The provenance
records the reading options that are not defaults, Arrange, and the corrections as the CLI spells them.

### 12.4 The parts table and the corrections (R-im10-7, R-im10-8)

A picture's table has `Pixels` set: X and Y are integers in source pixels, and only then does the header state a unit —
`X (px)`, `Y (px)` — so every artwork CSV is the bytes it was. A CSV states its kind by that header: a pixel table over
artwork, or a length table over a picture, is a refusal. `PartEvidenceSource.Picture` is what a picture read; a row it
named, like one the silkscreen named, can be renamed by X/Y. **Corrections** are `ConnectionOverride`s in source pixels:
`--disconnect x,y` and `--connect x,y` set the crossing within 2 w of the point (`WireGraph.Build`, which marks it
`Overridden`); `--connect x,y:x2,y2` joins the dangling end nearest the first point to the pin nearest the second, in
the emit. The dialog holds them like table edits and lays them over every re-run.


## 13. IM-12 — acceptance, field pictures, the example, and what does not read

Gates: `tests/Ui.Tests/ImageRecognition/` — `ImageRoundTripTests` (R-im12-1), `ImageForeignDrawingTests` (R-im12-2,
over the committed pictures in `Fixtures/`, drawn once by `ForeignPictures.cs`), `ImageNothingHereTests` (R-im12-3),
`ImageFieldTests` (R-im12-4, the git-ignored `testdata/image-sources/`, schema in its README). The example is
`examples/Schematic from Image/`, built by `tests/Ui.Tests/Examples/SchematicFromImageExampleAuthoring.cs`.

### 13.1 Round trips through circuitRF's own renderer (R-im12-1) — measured

**Layout.** AS-9's round-trip board as imported from its Gerber set, drawn by `circuitrf render --detail full`, ONE
copper layer to a picture (drawn together, one layer's per-shape outlines lie inside the other's fill and cut it), at
10, 20 and 40 µm/px, traced back at that scale with every colour but the paper mapped to the layer:

| µm/px | Top XOR / perimeter | Bottom XOR / perimeter |
|---|---|---|
| 10 | 0.03 px | 0.09 px |
| 20 | 0.25 px | 0.09 px |
| 40 | 0.23 px | 0.07 px |

AS-9's two hand-drawn boards (the grounded coplanar line, the stripline) are not drawn: they add no shape the trace
treats differently from the main board's lines, pads, bend, tee and plane, and the stripline's line lies under two
pours, so only a picture of its inner layer alone would show it.

That needed one thing the picture cannot say: **the layout view strokes every shape's edge with a line
`LayoutRenderer.GeometryStrokeDevicePixels` (2 px) wide, centred on it**, so its picture's copper is a pixel fat all
round, and read as drawn it was 1.24–1.30 px fat. `ImageTraceOptions.EdgeStrokePx` (CLI `--edge-stroke`, dialog
Advanced ▸ Edge stroke) states that line's width and pulls every traced region in by half of it — except where the
region runs off the picture, whose edge drew no outline (`ImageTrace.Inset`); without that exception a line running off
the picture lost a pixel of length. The residue at 20 and 40 µm/px is the renderer's anti-aliasing, not the trace: an
edge on a whole pixel is drawn with exactly 1.0 px of stroke outside it, an edge at a quarter pixel with ~1.46 px.

At 40 µm/px the top copper alone (the plane implied by the stackup, as §7.2) is recognised and compared with the artwork
route read from the same copper — like for like: the layout view draws a via as two hairline rings, which no colour
clustering keeps below ~10 µm/px (a ring is all anti-aliased edge, and the small-distinct-colour step wants flat
samples; a bright drill colour does not help), and draws no mask. The same elements by type, the same parts by case and
connection, and **|ΔS| 0.019** over the board's own band (AS-9's 0.1–3 GHz) — 0.012 at 20 µm/px, 0.004 at 10, the
pixel's share. Most of it is length: the lines come back 30–60 µm short at 40 µm/px (the anti-aliasing above, at pads).
Over the recognition's default 0.1–6 GHz sweep the same picture gives 0.026.

**Schematic.** The shipped `Optimization/LSectionMatch` and `LVS/Attenuator` drawn by `render` and read back: the same
parts, values, ports (and port impedance) as their own extraction, compared as graphs. Zooms 3 to 5 read both exactly,
but for one 5/6 tie at zoom 4 (IBM Plex's 5 sits 0.049 from its template and 0.048 from a 6); zoom 2.5 — about a 2 px
pen — loses the wiring. The gate runs zooms 3 and 4.5. An attenuator's interface pins are no Port in its own netlist;
on a picture they are port symbols, so its ports are counted against its pin symbols.

### 13.2 What a circuitRF picture taught the schematic reader

Before IM-12 the reader made nothing of circuitRF's own schematics. In the order the defects were found:

- **Hairlines.** Every tolerance is in w, tuned on drawings whose text stands 5–7 w tall; circuitRF draws 14 px text
  with a 1 px pen, so in w its symbols and words are three times the size — humps read as wire, glyphs as symbols, text
  over the 12 w cap went unseen. When the text (median glyph height of typeset lines, `TextHeight`) stands more than
  `HairlineTextHeight` (8) times the **line work's** width (`LineWidth`: the long straight runs only — a text-heavy
  drawing's plain w is its type's weight), words are found on the ink as drawn at the drawing's scale, and then the line
  work alone is dilated to `cap / TextHeightInStrokes` (5), and w is set to that target rather than re-measured (a
  2.6 px pen binarises to 2, 3 or 4 px by colour and phase; a hexagon side read 8.0 w at one zoom and 7.1 at the next,
  either side of the diagonal-wire threshold).
- **A word on its wire** — circuitRF's italic net label stands on the wire — touched line work and was kept as a symbol
  detail. A word touching only along its bottom fifth, at two places or more, with the line unbroken under it and bare
  paper below the line is a word, marked `OnWire`, and read only as a name (its italic `in` matched `1Ω`). The bare-paper
  test is what keeps half a diode's triangle, which straddles its lead, a symbol.
- **Values written with their name and spaces** — `L = 5 nH`, `C = 0.5 pF`: `ValueGrammar.ReadNamedValue` reads
  `R=`/`L=`/`C=` as a value in that dimension only (a length after `L=` stays a line set), and `ImageText` joins a run
  of words on one line, gap ≤ their height, holding no designator and at most one value, when together they read as a
  value or a line set — longest run first.
- **Label blocks.** circuitRF prints a part's name, designator and value stacked beside it, and a small part's block can
  stand nearer the next drawing than its own (the attenuator's `C = 1 pF` sits above R2). A value on the line under an
  attached designator, left-aligned with it, is that part's (`LabelAssociation`); a value stating its unit then goes
  first among the symbols of its kind; only then by distance. A `Z = …` under a port's designator sets the port's Z.
- **The termination's box** — rounded corners, a zig-zag inside, the lead running through the top. A short non-wire run
  joining a horizontal and a vertical candidate end — one of each axis there, so a hop's half-arc is not one — is the
  corner between them (`WireGraph.Corners`); a candidate wholly inside a closed outline's box is symbol; a residue region
  inside another's box is one region with it (`SymbolRegions.Facing`); and a template's lead stops where it crosses
  another stroke (`SymbolTemplates.Crossing`), so the Term template's pins are where the picture's wires stop.
- **Specks.** Ink pieces and holes smaller than a quarter of w² are removed after binarising: salt beside a word read as
  glyphs (`10nH°°`), and white specks cut a ground triangle's outline.

### 13.3 Pictures drawn another way (R-im12-2)

The committed IEC schematic (boxes for R and L, a 4 px pen, designators in DejaVu Sans Oblique, values in the condensed
faces) reads as its netlist as drawn, as a quality-60 JPEG, turned 1.5° and salted at 0.2 %. At half size (2 px pen,
12 px caps) the circuit reads but values do not hold: `22R` read `77R` in the condensed face and `4.7pF` `+.7pF` in the
oblique — the claim there is the circuit's shape, and §13.7 lists small slanted and condensed type. An unslanting step
was tried and taken out: per-word lean from stems is noise on two-glyph words (an upright `22R` measured 10.9° from the
R's leg, an oblique `C1` 0°).

The dark-viewer layout (translucent red over blue over near-black, no outlines) traces within 0.04 px per unit of
perimeter as a PNG, and within 0.48 px as a quality-60 JPEG, which keeps colour at half resolution (4:2:0): its edges
are a pixel wide before anyone reads them, so that case is held to 1 px. A JPEG's fringe is its own colour cluster, and
either reading of it is wrong by half a pixel — ignored, the red comes back thin; mapped to the red, fat. The automatic
map gives copper by area (D7) and so puts the larger blue on top; the gate maps as a person does.

### 13.4 Pictures with nothing in them (R-im12-3)

A noisy gradient, a blank page and a page of text are IM-2's None, each with its sentence. A **Smith chart** classified
as line work and came back as a 64-pin cut-out device wired by 59 diagonal "wires": its arcs are straight only in short
slanting pieces. More than half of a picture's wire length in slanting pieces, eight or more of them, is now refused
(`SchematicImageReport.CurvedLinesRefusal`), and the dialog offers **Read it as a layout** for it — each override link
now shows only the kind the picture is not being read as.

The noisy gradient took 7 s to classify (k-means for every k over ~100,000 distinct sampled colours); above
`ColourClusters.MaxDistinctColours` (8,192) the samples are pooled to 5 bits a channel first — 1.5 s. And `convert` at
10 µm/px spent minutes in `Skeleton.Thin`, which rescanned the whole picture on each of a filled plane's hundreds of
passes; it now re-examines only pixels a deletion touched (same result, held against the full scan on random masks).

### 13.5 The example (R-im12-5)

`examples/Schematic from Image/`: a schematic picture and a board picture of one series C–L network, and the two cells
Create Schematic from Image makes from them. The network is in series on purpose. Drawn first with the inductor shunt
on a stub to a via, the board's inductor was not found from copper alone — the stub's end was read as an open line end,
not a pad — and a line 2.4 mm long running off the picture was not an edge launch where a 3.6 mm one was. Both are
observed, not explained; neither is the picture reader's (the traced copper is right), so they are left to the artwork
recognition's own work.

### 13.6 Field pictures (R-im12-4)

`testdata/image-sources/<generic-name>/` holds a picture and its `expected.json` (picture, kind, scale and technology
for a layout, parts, nets, kinds by type); nothing but the README is ever committed, and the theory skips on a fresh
clone.

### 13.7 What does not read (R-im12-7)

- Hand-drawn schematics (not a goal); photographs except a board's top side set square on its corners (§14).
- Serif and script type; type that is bold, very small, or slanted or condensed at a small size (12 px caps).
- Values printed far from their parts, other than on the line under their designator.
- Dense layouts below ~4 px per trace width — widths read, loosely (the resolution line says how loosely).
- The stackup, never stated by a picture (D8).
- Vector sources — PDF, SVG, EMF — which are the better source when one exists; they are not read (D18).
- Vias drawn as hairline rings (circuitRF's own layout view) below ~10 µm/px.
- A part on a short stub to a via in a picture with no mask (§13.5).

## 14. IM-13 — photographs of boards

Brief: `brief-img-13-board-photos.md`. Gates: `tests/Ui.Tests/Imaging/PerspectiveTests`, `BoardPhotoTraceTests` (the
trace, and the dialog's corner tool driven headlessly), one case in `ImageKindTests`; the synthetic photo is
`BoardPhotoPictures.cs` — a 60 × 40 mm board seen through a perspective WRITTEN OUT BY HAND (not solved by the code
under test), lit 0.8 → 1.15 left to right with a warm cast, σ = 3 levels of noise, and two clipped highlights.

**A photo is not traced as taken; it is turned into a picture the IM-3 trace already reads**, and the trace runs
unchanged on it. `BoardPhoto.Read(photo, corners)` → `BoardPhotoReading`; `BoardPhoto.Apply(input, reading)` swaps
the trace input's `Source` for the rectified picture and sets `ImageTraceInput.Photo`, whose clusters replace
`ColourClusters.Find`, whose scale is a candidate (`ImageScaleKind.Corners`) and whose map is the default when the
user has not edited one. The GUI (Board corners… in the scale combo) and the CLI (`--corners`) both call those two.

### 14.1 Kind (R-im13-1) — `ImageKind.BoardEvidence`

Taken only for a photographic picture (flat share < 0.6), before the line-work measurements, on a grid of at most
160,000 samples:

| Feature | Threshold | Gate photo | Noisy gradient (IM-2's None case) |
|---|---|---|---|
| mask-hue share — chroma ≥ 15, outside the copper hues (CIELAB 35°–105°), within ±30° of the dominant such hue | ≥ 0.2 | 0.43 | 0.18 |
| largest connected region of it | ≥ 0.12 | 0.43 | 0.14 |
| rectilinear edge share — the strongest tenth of the gradients (3 × 3-smoothed luminance), weight within ±8° of two directions ≥ 30° apart | ≥ 0.4 | 0.59 | 0.22 |

All three must hold; the confidence is 0.55–0.75 by the narrowest margin, so a photo never reads as surely as a drawing.
The smoothing is there because a coarse sensor grain otherwise crowds a board's edges out of the strongest tenth: the
dialog figure's photo measured 0.34 without it. `DrawingKind.BoardPhoto` spells `photo`; `ImageKindResult.AsLayout()`
keeps it as read wherever a layout is traced.

### 14.2 Rectify (R-im13-2) — `Perspective.cs`

DLT with Hartley normalisation and h₂₂ = 1 (safe once normalised: the centroid of a convex quad maps inside it),
Gaussian elimination, least squares beyond four pairs. The corners must make a convex quad and go **clockwise on the
picture** — anticlockwise would be a mirror image, which no camera takes, so it is a refusal naming the order rather
than a silent flip. The output samples at the photo's FINEST spacing along any side (nothing on the near side is lost),
extends over the photo as mapped within the rectangle grown by its own size each way, and is held to 4× the photo's
pixels and 50 MP. Warp is bilinear at pixel centres in fixed bands. Measured: the gate board's four edges land
within 0.16 px of the rectangle at 81.3 µm/px.

### 14.3 Light (R-im13-3) — `WhiteBalance.cs`

Grey world over the NEAR-GREY pixels only, in two passes (chroma < 20 as taken, then < 12 as corrected): a board is
mostly mask, and grey world over every pixel bleaches the mask and turns copper pink. Clipped pixels are left out;
gains are held to ×½…×2. The flat field is the luminance blurred (three box passes, radius ⅛ of the shorter side,
normalised by the blurred inside-mask so the warp's empty corners do not darken the board) and divided out with ONE
factor per pixel, so the hue is kept.

### 14.4 Classes (R-im13-4) — `BoardPhoto.Classify`

Six classes in FIXED colours (`BoardPhoto.Rgb`), so a map, preset or `--layer-map` written for one photo rebinds to the
next exactly: off the board `#9e9e9e`, mask `#1f6b3a`, copper under the mask `#74c476`, exposed copper `#c87533`,
silkscreen `#f5f5f5`, part body `#202020`. In order:

1. **Highlights** — clipped blobs at least 2 px from their edge somewhere (a 0.2 mm legend stroke never is), grown by a
   2.5 px bloom.
2. **Mask hue** — the peak of the chromatic, non-copper hue histogram; its family (±30°, chroma ≥ 8) must hold 5 % of
   the photo. **Copper under the mask** is the family's lighter half (Otsu on L) when the halves are ≥ 8 L apart.
3. **The board** — the family opened (1.5 px: a stray mask-hued pixel on the bench, closed with the board, pulled a
   star of bench into it, read as tin) and closed over 1.5 mm, holes filled, with the rectangle. Off it is off the board.
4. **On it** — copper hue at chroma ≥ 15 is exposed copper; near grey below L 30 is a part body; near grey above L 70 is
   tin where an opening at 0.35 mm keeps it and silkscreen where it does not; other near grey is tin; any other colour
   reads as mask.
5. **Clean-up** — copper narrower than 0.1 mm (≥ a 1 px opening) is the edge of something else and reads as mask: it
   took the gate photo's top copper from 46 shapes to 1. Legend strokes are closed over 1.5 px.
6. **Highlight fill** — a class that CONTINUES across the highlight (both ends of a ray pair through the pixel — across,
   down, either diagonal — on it) wins, copper first; nearest-rim fill otherwise. Nearest-rim alone pinches a line under
   a glint to a point, because the glint's middle is equidistant from the line and the mask beside it. What remains is
   a ~2 px waist where the glint's bloom meets the line's own anti-aliased edge.

The cluster set is built from the hard classes with each pixel mixed with the commonest other class among its eight
neighbours (a box filter of the labels), which puts 0.5 coverage on the boundary and turns a staircase into a line.
Part bodies at least 0.8 mm on both sides are counted as unread parts; pads under them are NOT inferred — the brief
allows it only from a placement or BOM, which this phase does not wire in.

### 14.5 The report and the provenance (R-im13-5)

`ImagePhotoRectified` (the rectangle, and each corner's residual in µm — the distance from the rectangle's corner to
the corner the rectified picture shows there, found by Förstner's sub-pixel corner iteration; `—` where no corner
shows), `ImagePhotoHighlights`, `ImagePhotoUnreadParts`, and `ImagePhotoWidths` (widths are read, a Z0 from them is
estimated). The kept picture and underlay are the RECTIFIED photo (it is what the copper lies on); the provenance's
kind is `photo`, and its options name the photo as taken (`photo`: name and SHA-256) and the `corners`.
