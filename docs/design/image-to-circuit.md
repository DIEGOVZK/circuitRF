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
| **D5** | The result carries the source picture as an underlay at 35 % opacity (an ordinary, unlocked bitmap since §16), placed so every recognised item lies on what it was read from. A setting turns it off. | Cleaning up becomes comparing in place, not flipping between windows. A bitmap is already excluded from export, DRC and EM. |
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
at the mapped colours*) — unless the source picture is kept under the result and is not already in the target layout:
then the layout is the picture alone, at the stated scale, for tracing by hand, and the report says so. With no scale it is not refused — the result has the clusters, the map, the candidates and the
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

**A photograph** (not one read square on its corners, §14, which classes its own pixels) breaks the area rule: it shows
one side of a real board, and its largest colour is the laminate or the mask. A picture is a photograph to the trace when
its 256 most common exact colours cover less than 0.85 of it (`ImageKind.IsPhotograph`) — every drawing measured is
above 0.94, a dense layout at JPEG quality 60 0.955, a bare board's photo cropped into a paper's figure 0.42; flat share
does not separate that figure (0.77), whose paper and smoothed photo are both flat. A photograph is clustered on its
white-balanced picture (`WhiteBalance.Correct`), and after background, drill and silkscreen every colour that is copper
(§14.4's exposed-copper hue and chroma) maps to the TOP conductor and every other colour is ignored. With no copper-hued
colour the area rule applies.

An impedance scale (§6.2) takes only a SOLVED width — the circuit model's, else a cross-section that found a return
conductor. A layer with nothing to return on (the bottom copper of a two-layer board whose ground is the bottom) has
neither, and is refused; the cross-section's own search start, 100 µm, was once taken as the width.

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
returns everything it read, so the overlay can show what was seen. Every tolerance is in units of the stroke width w at
the reference profile, and scales with the picture's own text height or symbol scale outside it (§18).

### 9.1 Normalise (R-im7-2) — `SchematicImageReading.cs`

A coloured picture keeps its **darkest** colours — those at least half as far from the ground's lightness as the
darkest is — so a pale highlight or a tinted grid drops out and a coloured wire layer stays; and every other colour
drawn as thin strokes that stands clear of the ground and is not a darker line's anti-aliased rim (§18.4). A lean between 0.3° and
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
- Script type; type very small, or slanted or condensed at a small size (12 px caps). Bold sans and serif or serif
  italic type read since IMF-7 (§23), a subscript included.
- Values printed far from their parts, other than on the line under their designator.
- On schematic pictures from datasheets and the web (§26): a coil in a hairline pen, which can come apart into several
  `?` parts; two capacitors drawn side by side, read as one part; an IC box divided by pin-number boxes, read as
  several blocks; a solid box drawn round a part or a module (a fixture, a bias tee, a DUT frame), read as one block
  with what it holds; a transistor drawn without its circle; a hatched or dotted zig-zag; a not-fitted part marked only
  by its colour; an op-amp or a dense evaluation board, whose parts read but whose connections largely do not.
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

## 15. IM-14 — plots to Touchstone

Brief: `brief-img-14-sparameter-plots-to-touchstone.md`. Code: `src/Design/Imaging/Plots/`. Gates:
`tests/Ui.Tests/PlotRecognition/` — `PlotRoundTripTests`, `PlotForeignDrawingTests`, `RectAxesTests`, `SmithFrameTests`,
`PlotFrequencyTests`, `TouchstoneAssemblyTests`, `MinimumPhaseTests`, `PlotCliVerbTests` — and one case in
`ImageKindTests`. Pictures drawn by circuitRF's own plotter come from `PlotPictureSet` (the `sparam`, `plot` and
`render` verbs run in process); pictures drawn another way are the committed PNGs `ForeignPlots.cs` writes
(`CRF_IM12_FIXTURES=1`, `ForeignPlotsGenerator`), every one drawn from the band-pass in that file so a test has its
answer.

**A picture of a plot is digitised, not recognised.** A datasheet that shows S-parameter plots and ships no file is the
typical source. A plot is the most tractable picture there is — straight axes, periodic grids, numeric labels,
distinct colours — so the reading is a measurement with a stated resolution per trace, and what a picture cannot hold
(phase beside a magnitude, frequency along a Smith trace, the entries never plotted) is a stated rule one click from
being changed, recorded in the file it produced.

### 15.1 One session, the entry points (R-im14-2, R-im14-15)

`PlotReading.Read(pictures, options)` → `PlotSession`; `TouchstoneAssembly.Build(session, options)` → the matrix, its
header, the report and the health check; `TouchstoneAssembly.WriteFile` writes it and keeps the sources; `PlotCsv` the
raw traces; `ImageOverlay.BuildPlot` the overlay. The dialog, `recognize` and the MCP tool call those and nothing else —
`PlotCliVerbTests` scans `src/Cli` for any other type of the namespace. Every picture handed in is read into one
session: plots are numbered across it (plot 1 is the first picture's first plot) and so are traces (T1, T2 …), and
every flag, row and report line names them that way.

### 15.2 Kind and frames (R-im14-1, R-im14-4) — `PlotKind`, `PlotFrame`, `SmithFrame`, `PolarFrame`

`ImageKind.Classify` asks `PlotKind.Detect` **before** the schematic-against-layout vote, because a plot's grid is
one-width straight line work — what a schematic's wires measure as. A rectangular plot is two long perpendicular ink
runs meeting at a corner with periodic ticks or grid lines; a Smith chart an outer circle holding circles tangent at
one point of it (right: impedance; left: admittance; both: Z-Y, read as Z); a polar chart rings about the centre and
radial lines. A Smith chart needs no other calibration: centre, radius and orientation, checked against at least two
constant-r circles, map any pixel to Γ. An admittance chart is the impedance chart turned half a turn and its curves are
drawn at the same Γ, so one `GammaAt` serves both (`SmithFrameTests`: the same pixel reads the same Γ on both
fixtures). The reference impedance is a `Z0 =` word when one reads, else 50 Ω, said in the report and in the header
(`Z0 50 ohm (assumed)`).

### 15.3 Axes (R-im14-3) — `RectAxes`

Tick labels are read by IM-9's stroke reader under a numeric grammar (`PlotText`): signed decimals, exponents,
multiplier suffixes and a unit on the label or in the axis title. A tick stub on a frame line is not a minus: with the
line taken out it is a short dash beside the label, and grouped with it read as `-25 -`. Labels are paired with their
ticks and fitted by consensus — every pair of labels proposes a line (linear, and logarithmic), the one most labels
agree with within 2.5 px wins, two agreeing out of many prove nothing (at least half must) — then least squares on the
inliers; a misread label is rejected, counted, and cannot bend the axis. The residual is reported in pixels. The
frequency unit is a typed one, else a label's own suffix, else the x title's (`Frequency (GHz)`, read at a small size
as `freq fCHz1`: a G comes back as C, O, Q or 6), else hertz when the labels run past 1e5 (`1e8 … 1e10` — no RF plot is
labelled to 100,000 GHz), else GHz, stated. Fewer than two labels and the axis waits: the dialog's **x axis** / **y
axis** rows take two pixel positions and their values as typed text (`120px=1GHz,900px=5GHz`), the spelling
`--x-axis` / `--y-axis` take — they stand for the two clicks the brief describes.

**A y axis on the right** (brief-pdg-1-two-y-axes.md R-pdg1-1) is read as the left one's mirror image: number words
right of the frame within the same distance and span limits, only the innermost column, paired with
`PlotFrame.Y2Ticks` — stubs on the right frame line plus the grid lines crossing it, since a right axis's ticks often do
not line up with the left grid — and fitted by the same consensus, so a misread right label is rejected the same way.
Its title is a turned word right of those labels. A plot with both is `RectCalibration.TwoY`, the right axis `Y2`
(`--y2-axis`, `--y2-log`, the dialog's **y2 axis (right)** row). A plot with **not one number left of its frame** and
right labels that fit has its only y axis on the right: it is stored as `Y` with `YSide = Right`, so everything that
reads `Y` keeps reading it, and its clicks are `--y-axis`'s. `PlotFrames` accepts a box whose right frame line carries
the ticks its left one lacks. Words right of a frame are read numeric-first, as those left of it are. `Calibrated` is x
and at least one y axis.

### 15.4 Traces (R-im14-5) — `PlotTraces`

The plotting area's colours are clustered (IM-1); the background, the grid (a cluster lying mostly on the frame's lines
or, for a chart, on the usual constant-r/x circles — an uncoloured one only, since a coloured trace may lie exactly on a
grid circle), text and faint clusters are set aside, and words, the legend box and excluded rectangles are cleared
inside what is left. A trace in a frame line's own colour loses **those** lines — a black curve on a black frame's axes
— never the light grid, which it would otherwise be cut along wherever it runs level with a grid line.

A rectangular trace is a function of x: each column's coverage is cut into runs, a run's coverage-weighted centre is y
to a fraction of a pixel, and runs are linked into tracks predicting from their recent slope. Two tracks sharing a run
are crossing; when the runs part each takes the run its frozen slope points at, and a parting where both pairings fit
about equally is ambiguous and drawn amber. A track shares a run only when it predicts inside it — a dashed track in a
dash gap predicts beside the other curve, and taking its run there swaps the two at the crossing. Gaps up to 2 % of the
axis are bridged and counted, longer ones left open. A chart's trace is the coverage skeletonised and walked end to
end, turning least at every junction, and the walks whose ends meet are joined.

**A JPEG line is one line.** Compression leaves a blurred line as a body cluster with a darker core cluster inside it;
traced apart, the core is a hole that cuts every column's run in two and the halves are followed as parallel tracks a
pen apart. So clusters within 64 levels per channel of each other are traced as one coverage, and a curve lying within
a pen of a longer one for 80 % of its length is that curve read twice and is merged into it (`MergeAlongside`). The
JPEG-60 fixture went from five tracks to one, its worst error from 0.26 dB to 0.054 dB.

### 15.5 What each trace is (R-im14-6, R-im14-7) — `PlotQuantity`, `PlotMarkers`

Evidence, strongest first: the legend entry in its colour (its line style decides between two of one colour); a word
in its colour near the curve, or naming an entry and lying along it; the y-axis title; the plot's title — for a chart,
the word just above it, the one naming an entry when several stand there. The entry grammar reads the stroke reader's
spellings: a bracket is any stroke it is read as (`Sf2 1)`), a Z inside an entry is a 2, and a 1 is t, l, I or | — a
title's `S11` at 18 px reads `Stt` (and, as a number, `511`). A chart's trace is complex by its chart whatever quantity an assignment names. A marker readout (`m1 freq=2.400GHz
dB(S(2,1))=-0.512`) is an exact point: it replaces the pixel reading within one pixel of it.

**Which y axis** (R-pdg1-2). On a plot with one y axis every trace is read against it (`PlotTraceRead.Axis` is that
axis's side, `AxisEvidence` empty). On a plot with two, the evidence, strongest first: the user's choice (`--trace
T3=S21:phase,axis=right`, the traces table's **Axis** column); the legend entry's own axis mark (`(R)`, `right`, `Y2`,
an arrow — only what the legend reader already gives); **colour** — an axis title or labels in the trace's colour,
within `PlotTraces.MergeColour`, read even when the title's words are not, since datasheets (and circuitRF's own
plotter) colour an axis like its trace; the trace's quantity (from every word but the axis titles) agreeing with one
axis title and not the other; and only to break a tie, the trace lying inside one axis's labelled span and not the
other's — weak, both mappings being linear across the same pixels. With none, the left axis is assumed and **said**:
an amber swatch in the table, an amber label on the canvas, and an `axis` finding naming the trace and the control that
answers it. The axis is decided before the assignment, because the "axis title" evidence of what a trace is reads **its
own** axis's title; each trace is valued through its own axis, its resolution and unit with it. A trace's axis is not
the Touchstone's concern — its samples are already in the right units — so `TouchstoneAssembly` changes only in its
header (each plot's y2 calibration and residual, each trace's axis and its evidence) and in refusing a trace whose axis
is uncalibrated, naming `--y2-axis`. The CSV gains `y_axis` (`left`/`right`, empty for a chart trace) as its LAST column.

### 15.6 Frequency along a chart trace (R-im14-8)

Strongest first: markers with frequency readouts (two or more); a rectangular magnitude trace of the same entry, matched
by |Γ| in order (monotone dynamic time warping in both directions, the cheaper wins; anchors only where |S| moves a tenth
of its range over a tenth of the band); a typed start and stop. Markers set the direction, and a matched trace that
agrees adds anchors between and beyond them. Between anchors frequency is spread **in proportion to the angle swept
about the chart's centre**, which is exact for a uniform line and an approximation otherwise; outside the outermost
anchors the trace is cut, never extrapolated. Direction is clockwise (Foster) unless markers or the match say
otherwise; the header says which rule set it. With neither markers nor a matching trace the trace waits for a typed
range (`PlotFrequencyTests`).

**What the swept angle costs.** On the uniform line the three gates draw (25 Ω, a quarter wave at 1 GHz), two markers
place every point within |ΔΓ| 0.0044 of the line at its own frequency. On the band-pass's S11, the swept angle alone
put |ΔS11| at 0.6 near the resonance; with three markers and the log-frequency dB(S11) matched, 0.030. A |Γ| match
places frequency only as well as the rectangular plot's pixel resolves |Γ|, over how fast |Γ| moves there — poorly
just past a resonance, where |S11| turns — so `PlotRoundTripTests` puts that term in the bound.

### 15.7 The file (R-im14-9, R-im14-11) — `TouchstoneAssembly`

Ports are the largest assigned index, up to four. The grid is the overlap of every trace's range at the densest trace's
pixel resolution, rounded to a 1-2-5 step, never extrapolated; the report names the traces that bounded it. Resampling
is per quantity: dB linearly in dB, phase unwrapped then linear, Γ in the complex plane, in log f on a log axis.
Magnitude and phase of one entry combine; a chart and a rectangular trace of one entry are cross-checked and the larger
difference reported, the chart written by default for a reflection (`--prefer`). An entry drawn only as a phase, or
only one of re/im, has nothing to put it on and refuses (`magnitude`).

Missing phase: minimum phase (default, *estimated*), zero, or a linear phase from a typed delay. Missing entries for a
two-port: S12 = S21 when |S21| ≤ 0 dB throughout, else zero (an amplifier's isolation is not plotted); S22 = S11
(symmetric). Three- and four-ports default to zero. **A fill copies a DRAWN entry, never another fill** — with neither
S11 nor S22 drawn, S22 is not "= S11" of a zero given a moment before. The file is refused only while a trace is
unassigned, a chart trace has no frequency, an axis is uncalibrated, or a fill names an entry that is not plotted; each
refusal carries the flag and the dialog control that answers it. Version 1.1, DB by default, the header recording each
picture's name and SHA-256, every calibration's residual, every trace's assignment, evidence, resolution and frequency
rule, every fill and phase rule, the grid, and the health check (passivity, reciprocity, causality).

### 15.7.1 Fitting a trace (brief-pdg-2-curve-fit.md) — `TraceFit`

A digitised trace is a pixel reading: a step of one pixel's worth of value and the jitter of a pen's edge. A fit smooths
that and gives the curve a formula. It runs **after** reading, on samples; the reader is untouched. Models: linear,
exponential `a·exp(b·x) + c`, polynomial (degree 1–8) and a cubic smoothing spline.

- **In the trace's own plotted units** — dB for a dB trace, degrees for phase (unwrapped first, so a wrapped sawtooth is
  one curve), the axis's value otherwise — against x in the axis's DISPLAY unit (`f in GHz`), or `log10(f)` on a log x
  axis. Rectangular traces only; a chart trace offers None.
- **Well conditioned.** Every least-squares fit is Householder QR on x centred and scaled to [−1, 1]; a polynomial is
  expanded back to raw x for display only, and evaluated in its centred form. A degree-6 polynomial on x in hertz
  (1e9…1e10) comes back to 1e-9 relative, where the raw normal equations would span ~1e120.
- **The exponential** starts from a log-linear fit of `y − c₀` (`c₀` beyond the data's extreme on the side its
  curvature points to, tried at five distances), refined by Levenberg–Marquardt on the y residuals. It refuses, with a
  sentence, when LM does not converge, when the rate goes to zero (a straight line), or when it explains under ~91 % of
  the variation (RMS above 0.3 of the trace's standard deviation) — a sine reads "explains only 15 %".
- **The spline** is Reinsch's: `Σ wᵢ(yᵢ − g)² + α∫g″²`, a pentadiagonal LDLᵀ per α, α bisected in log until the RMS
  residual against the raw reading equals the target — **one pixel's worth (the trace's resolution) by default**, a
  typed RMS otherwise. It has no compact equation: `spline · N knots`.
- **Exact stays exact**: a marker's readout weighs ×100 in every least-squares fit and has σ = 0 in the spline, which
  therefore passes through it. **Never extrapolates**: `Evaluate` is NaN outside the trace's own x range.
- **Residuals** against the raw reading, in units and in pixels (each sample's own `PerPixel`): under ~0.5 px fits the
  drawing; several px is the wrong model. A degree-4 polynomial on the band-pass's dB(S21) hump is 8 px RMS.
- **The expression** is circuitRF expression text in `f` (frequency axis) or `x`, coefficients at round-trip precision,
  each term's sign the operator before it (`- 0.00041*f^2`, never a negative literal raised to a power); the unit stands
  beside it. `TraceFitTests` evaluates it through Core's `Evaluator` against the fit to 1e-9.

**Where it applies.** Pieces TraceJoin joins share one fit made on the joined curve, whichever session the caller has:
a joined trace lists its pieces (`PlotTraceRead.Pieces`), so a fit asked of any piece is the joined trace's, and
`TraceFit.Session` keys the result under every piece's name. `TouchstoneAssembly` evaluates a fit at each grid
frequency in place of resampling the samples; the per-quantity handling after that (dB → magnitude, phase → radians) is
unchanged, and an unfitted trace is exactly as before. The CSV gains `y_fit` (a phase fit put on the sample's branch);
`y` stays what was read. Each fit is a line of the report and the header; a refused fit leaves the trace as read and is
a finding, never a refusal of the file. The overlay draws a fit thin and dashed in magenta (teal beside a magenta trace)
over its trace, and it is never hit-tested.

Measured on a crisp circuitRF plot (a 2 Ω + 5 nH low-pass, 1–5 GHz, `TraceFitTests`): degree 4 sits 0.04 px from the
reading and writes |Δ|S21|| 0.00593 against the reading's 0.00592 — both are the y calibration's bias, which a fit
carries unchanged. A fit removes noise; on a picture with little, it can only match the reading, to its model's error.

### 15.8 Minimum phase (R-im14-10) — `MinimumPhase`

The cepstral Hilbert transform of ln|S|, on **the bilinear map of the whole axis**: f = f_top·tan(θ/2) for θ in
[0, π], so the band is the first half of the DFT circle and everything above it, out to infinity, the second. The
bilinear map takes a minimum-phase response to a minimum-phase one with the same phase at corresponding frequencies,
so the discrete answer is the continuous one with no tail left out. Below the band the first value is held to DC; above
it the magnitude continues at its own end slope on log-log axes (clamped to ±120 dB/decade), never rising above where
it ended. The report states the middle 80 % of the band as trustworthy.

Measured on a two-pole low-pass (f0 = 3 GHz, Q = 0.7, band 0.1–10 GHz): 0.7° worst over the middle 80 %. The
remaining error is the end-slope guess — the slope over the band's top tenth is not yet the asymptote's: Q = 0.5 gives
4.4°, and a band stopping at 5/3·f0 gives 8.4°. A datasheet that shows the phase plot should have it digitised.

### 15.9 Measured (`PlotRoundTripTests`, `PlotForeignDrawingTests`)

Every comparison is bounded by the reading's own resolution: 1.5 × (the trace's y resolution + what the curve moves
through within its pixel column and half a pen either side). The second term is why a notch's tip is read high — a
2.5 px stroke spreads the steep walls over the tip's columns — and is part of the bound rather than an error.

| Picture | Worst, as a share of its bound |
|---|---|
| circuitRF dB(S21), 1–5 GHz (±0.02 dB per pixel) | 0.53 |
| circuitRF four pictures → `.s2p`: S21 from dB + phase / S11 from the Smith chart matched to log dB(S11) | 0.30 (\|ΔS21\| 0.0063) / 0.81 (\|ΔS11\| 0.030) |
| circuitRF Smith S11, three markers | \|ΔΓ\| 0.0046 against the locus |
| dark background / log axis labelled `1e9` / legend inside | 0.38 / 0.45 / 0.39 |
| two black traces, solid and dashed, crossing | 0.34 (no swap) |
| admittance Smith chart | \|ΔΓ\| 0.0048 |
| JPEG at quality 60 | 0.33, one trace |

A black curve within a pen of a black frame line still loses the rows the frame line takes, and its centre there is
read off what is left: `PlotForeignDrawingTests` leaves those columns out of the two-black comparison.

### 15.10 What is not done

Click-to-follow and drag-to-exclude (R-im14-5) exist in the reader (`TraceHints`) but neither the dialog nor the CLI
sets them yet. Polar charts are read but no gate draws one.

## 16. The first field paste — drawing what the picture draws, and pasting with no dialog

The first real use (2026-10-11) was a screenshot of circuitRF's own editor — one Term, one S2P with its Ref grounded, a
wire running off the right edge — pasted back with every default. It came back with six Terms, no S2P, a net label on
every wire and extra nets. Three causes, none in the pixels:

- **The drawing was a netlist redrawn.** IM-10 made the reading a `TestBench` and drew it with `NetlistSchematic.Build`,
  whose contract is a round trip of NAMES: it labels every net. A wire that reached no pin was not in the netlist and so
  was not drawn. **As drawn now places the instances only** (`ExactPlacement.PlaceOnly`) and `SchematicImageDrawing`
  draws the picture's own wire graph: every node a vertical segment joins shares one x and every node a horizontal one
  joins shares one y, a node a pin stands on takes the pin's coordinate, a non-connecting crossing node is passed
  straight through (so the wires cross mid-segment), and diagonals become an L. Ground symbols go where the picture draws
  them; a net label only where the picture writes a name. The drawing is extracted and compared with the circuit, and a
  disagreement is reported rather than labelled away. **Tidy** is still the netlist redrawn.
- **A box was cut out** (AS D9's rule for a multi-pin part on a board): each wired pin a port. On a schematic that
  invents ports. **A multi-pin symbol is now an SnP** — one port per non-ground wired pin, ordered left to right then
  top to bottom, the first grounded pin its reference (`RefNetBinding`).
- **A dangling end was a port** (D13's third source). Removed: a wire off the edge is a wire.

Two reading fixes the screenshot needed: the editor draws its labels large enough to cover a ground symbol, so (a) a tee
at the foot of a wire with shorter bars beneath it, all read as wire, is a ground (`WireGrounds`; its bars are hidden
from the drawing), and (b) a lone supply bar under a wire coming down to it, labelled by nothing that reads as a supply
(`V…`, a voltage), is a ground. And **the frame**: a symbol's pins are read where its wires reach its glyph, inside its
leads, so the symbol-ratio frame drew circuitRF's own symbols at about a third of the picture's size. `PictureGrids.Detect`
reads an editor's canvas grid from the PAPER pixels' column and row profiles (high-passed against a running median; the
pitch is the longest period with half the strongest comb strength, climbed to its lobe's top, refined to 0.01 px) and
the frame puts one pitch on one schematic grid step with the grid lines on the schematic's — the picture then lies
exactly under the drawing. The symbols override only by a factor beyond 2.5.

**Edit ▸ Paste Image as Schematic / Layout ask nothing** and paste into the document in focus (enabled only while one of
their kind is): `SchematicImageRecognition.PasteDrawing` reads off the UI thread, `PasteInto` moves the drawing by whole
grid steps to a top-left the editor chooses (the middle of the view when that is clear of the sheet, else beside it),
renames variables and nets the sheet already has, adds no analysis, and puts the picture under it; one CommandBatch adds
it all, selected. `ImageTrace.PasteTrace` traces with every default and falls back on 96 px/in when no scale is stated
or inferred — the pasted shapes are selected, and a selection of two or more shapes has corner scale handles. Every
underlay is now unlocked. The options live in Design ▸ Create ▸ Schematic from Artwork / Schematic from Image / Layout
from Image.

**What does not read (measured, not fixed).** Four textbook schematics from a public image library — a tee/pi/L pad set,
an RC low-pass with terminal circles and voltage arrows, a common-emitter stage with IEC boxes, an RC divider — all read
badly: heavy-pen IEC boxes and capacitor plates are taken for wire, labels beside zig-zags merge into one "box" region
with them, voltage arrows become wire, and a terminal circle is a port. That is the IM-7/IM-8 reader, not the drawing:
the drawing faithfully draws a wrong reading. The gate is `ImageScreenshotPasteTests`.

## 17. Pictures from datasheets and the web — the baseline (IMF-1)

Paste reads every picture with one set of defaults, and pictures from datasheets and the web do not come back as usable
schematic data (`brief-imf-0-overview.md` §1). Before any reader change, IMF-1 made the reading a number:
`FieldScore` (`tests/Ui.Tests/ImageRecognition/FieldScore.cs`) reads a picture as Paste does — every option at its
default, the kind forced to a schematic — and scores it against the picture's `expected.json`, one line per picture with
the same columns for every one, printed to the test output so the TRX carries the table. Every later phase reports
against this table.

**The corpus.** Committed: seventeen pictures our own code draws (`Fixtures/DatasheetStylePictures.cs`,
`Fixtures/WebStylePictures.cs`, written into `Fixtures/style-pictures/` by `CRF_IMF1_FIXTURES=1`), each a small circuit
with its answer netlist — five datasheet application circuits at 1× and 2×, six textbook or web drawings, and one shaded
illustration that is no circuit. The serif faces they need are a test asset (`Fixtures/fonts`), never shipped. Local
only: the twenty-five field pictures under the git-ignored `testdata/image-sources/` — sixteen datasheet crops
(`picture-ds-a1` … `picture-ds-h2`, 1 = screen 1×, 2 = Retina 2×) and nine web pictures (`picture-a` … `picture-i`).
`ImageDatasheetStyleTests` and `ImageWebStyleTests` hold one fact per committed picture — the circuit exactly, every
designator (overview §4) — each skipped with the phase that turns it on; `ImageFieldTests` prints both tables.

**The columns.** `auto`: the kind the picture classifies as on its own (`!` when that is not what it is). `read`: the
forced reading refused or read (`!` against `refused`). `kinds`: parts counted right by kind, of those drawn — an
over-count costs as much as an under-count, and a symbol of no known kind (emitted as a stand-in C) counts as no part.
`?`: symbols of no known kind. `topo`: the part–net graph isomorphic to the answer's (`NetlistShape.SameTopology` — kinds
and connections, ground and ports included; names, values and port numbers not). `ports`, `gnd`, `term`: ports, ground
symbols, terminal circles. `dev`: transistors, sources, amplifiers, ICs and transformers. `block`: the answer's amplifier
or IC read as one part on as many nets. `desig`, `values`, `names`: words read and attached right, of those drawn.
`region`: the most pins any one symbol region has. `ms`: the read, Debug build. `-`: not stated for that picture.

### 17.1 The baseline (2026-10-11)

Committed style pictures:

```
picture                  auto       read     kinds  ?   topo ports gnd   term  dev   block desig values names region     ms
ds-gain-block-1x         schematic  refused  0/5    0   no   0/2   0/4   -     0/1   0/1   0/5   0/2    0/1   0         176
ds-gain-block-2x         layout!    read     1/5    11  no   1/2   0/4   -     0/1   0/1   0/5   0/2    0/1   4         775
ds-ldo-1x                schematic  read     0/2    1   no   2/2   3/3   2/2   0/1   1/1   0/2   0/2    0/2   7         149
ds-ldo-2x                schematic  read     0/2    1   no   2/2   0/3   2/2   0/1   0/1   0/2   0/2    0/2   3         452
ds-regulator-1x          schematic  read     0/7    5   no   -     4/4   -     0/1   1/1   0/7   0/7    0/2   5         241
ds-regulator-2x          schematic  read     0/7    4   no   -     2/4   -     0/1   0/1   0/7   0/7    0/2   9         827
ds-rf-amp-1x             schematic  read     1/5    2   no   0/2   4/4   -     0/1   0/1   2/5   1/5    0/1   45        733
ds-rf-amp-2x             schematic  read     1/5    3   no   0/2   1/4   -     0/1   0/1   2/5   1/5    0/1   9         669
ds-test-circuit-1x       schematic  read     1/3    3   no   0/2   1/3   -     0/1   0/1   2/3   2/3    0/1   7         205
ds-test-circuit-2x       schematic  read     0/3    4   no   0/2   0/3   -     0/1   0/1   2/3   1/3    0/1   6         565
heavy-iec                schematic  read     0/8    4   no   0/1   1/6   -     0/3   -     0/9   -      -     40        670
illustration             layout!    read!    -      2   -    -     -     -     -     -     -     -      -     2         385
ladder-dashed            schematic  read     0/6    4   no   0/2   -     4/4   -     -     0/6   -      -     6         294
loop-coil-names          schematic  read     1/2    0   no   0/3   -     -     -     -     -     -      0/3   2         161
low-res                  schematic  read     1/2    2   no   0/2   -     2/4   -     -     0/2   -      -     2          60
textbook-zigzag          schematic  read     0/2    2   no   1/2   -     4/4   -     -     0/2   -      -     2         254
typeset                  schematic  read     1/5    4   no   -     1/1   -     0/1   -     0/6   0/5    -     4         427
```

Field pictures (local, git-ignored; datasheet crops first):

```
picture                  auto       read     kinds  ?   topo ports gnd   term  dev   block desig values names region     ms
picture-ds-a1            schematic  read     0/6    5   -    0/2   -     -     0/1   -     0/6   -      0/1   12       1010
picture-ds-a2            schematic  read     0/6    4   -    0/2   -     -     0/1   -     1/6   -      0/1   20       1710
picture-ds-b1            schematic  read     2/4    3   -    1/2   -     -     0/1   -     2/4   -      -     4         382
picture-ds-b2            schematic  read     1/4    5   -    0/2   -     -     0/1   -     3/4   -      -     4         513
picture-ds-c1            schematic  read     1/5    3   -    1/2   -     -     0/1   -     -     -      0/1   12        234
picture-ds-c2            layout!    read     1/5    2   -    1/2   -     -     0/1   -     -     -      0/1   17        582
picture-ds-d1            schematic  read     0/7    1   -    -     1/1   -     0/1   -     0/4   0/3    -     20        401
picture-ds-d2            schematic  read     0/7    2   -    -     0/1   -     0/1   -     0/4   0/3    -     17        885
picture-ds-e1            schematic  read     0/3    3   -    0/2   -     -     0/1   -     -     -      -     9         454
picture-ds-e2            schematic  read     0/3    4   -    0/2   -     -     0/1   -     -     -      -     9         755
picture-ds-f1            schematic  read     -      3   -    -     -     -     0/2   -     -     -      -     65        543
picture-ds-f2            layout!    read     -      2   -    -     -     -     0/2   -     -     -      -     104      2472
picture-ds-g1            schematic  read     -      1   -    -     -     -     0/4   -     -     -      -     270       821
picture-ds-g2            layout!    read     -      2   -    -     -     -     0/4   -     -     -      -     128      3433
picture-ds-h1            schematic  read     0/2    1   -    2/2   1/3   -     0/1   -     0/2   0/2    0/2   8         297
picture-ds-h2            schematic  read     0/2    1   -    2/2   2/3   -     0/1   -     0/2   0/2    0/2   8         529
picture-a                schematic  refused  0/8    0   -    -     0/4   0/6   -     -     -     -      -     0         172
picture-b                schematic  read     1/4    1   -    -     -     -     -     -     0/2   -      -     2         463
picture-c                schematic  read     0/8    2   -    -     -     -     0/3   -     0/8   -      -     32        475
picture-d                schematic  read     0/2    8   -    -     1/1   -     -     -     -     -      0/2   3         722
picture-e                schematic  read     0/5    17  -    -     -     -     -     -     -     -      -     5          54
picture-f                schematic  read     0/6    8   -    -     -     4/4   -     -     -     -      -     4         222
picture-g                layout!    read!    -      2   -    -     -     -     -     -     -     -      -     5         816
picture-h                schematic  read     6/7    9   -    -     -     -     0/2   -     -     0/7    -     5         398
picture-i                schematic  read     1/2    1   -    -     -     -     -     -     -     -      0/3   2         320
```

Both refusals are the curved-lines rule's: "most of the lines in this picture curve or slant".

**No picture reads.** Not one committed picture has its topology; the best datasheet-style reading (`ds-ldo-1x`) finds
its two ports and three grounds and still swallows both capacitors into the IC's region. On the field crops the passives
by kind are 0 of 6 or 7 on every datasheet circuit but the gain block, and no IC or amplifier is one part. The committed
pictures reproduce the field failures they were drawn for, which is what lets a fresh clone measure a fix.

### 17.2 The causes, with the evidence (overview §2)

Each sentence is from the scored lines above and the `recognize --overlay` pictures of the committed fixtures.

- **F1 — one fixed unit (IMF-2).** A datasheet hairline measures `w` = 2 px at 1× and at 2× alike, so every tolerance in
  `w` is half the drawing's size at 2×: `ds-rf-amp`'s IC region holds 45 pins at 1× and 9 at 2×, and `ds-ldo` reads all
  three grounds at 1× and none at 2×.
- **F2 — colour and fills (IMF-2).** The grey amplifier fill makes `ds-gain-block-2x` a layout on its own reading
  (`recognize` then maps the fill to Top Copper and the blue type to silk, and stops for want of a scale), as are the
  coloured crops `picture-ds-c2`, `-f2`, `-g2` and both illustrations.
- **F3 — plates read as wire (IMF-3).** `ds-rf-amp-1x`'s C1, two hairline plates 8 px apart, is read as a diode; on
  `heavy-iec` the 152 px plates and the IEC box sides become wire that joins one 40-pin region.
- **F4 — annotation strokes read as wire (IMF-3).** `ds-test-circuit`'s dashed fixture box becomes a 7-pin block
  (designated by the words inside it), and the dashed continuation of `ladder-dashed` joins its rails to a 6-pin block.
- **F5 — zig-zags refuse a picture (IMF-3).** `picture-a` is refused by the curved-lines rule — and so is the committed
  `ds-gain-block-1x`, whose coil, zig-zag and triangle are most of its line work at 1×.
- **F6 — the region merge runs away (IMF-4).** The largest region holds 45 pins on `ds-rf-amp-1x`, 40 on `heavy-iec`, 65
  on `picture-ds-f1` and 270 on `picture-ds-g1`, where the drawings' largest part has 8.
- **F7 — labels touching symbols (IMF-4).** `ladder-dashed`'s designators touch their coils and plates, and the reading
  finds no word at all on it: every one went into a symbol region.
- **F8 — two-pin vocabulary (IMF-5).** `ds-regulator` reads none of its 7 passives and diodes at either resolution —
  curved plates, filled diodes and the variable resistor are all `?` or block pins — and the looped coil of
  `loop-coil-names` is not read.
- **F9 — ports, terminals, rails, far names (IMF-6).** `textbook-zigzag`'s four terminal circles are read as terminals
  and become three ports to ground, never two pairs on a rail; and the three names of `loop-coil-names`, set 4–6 text heights beyond their wire ends, name
  nothing: 0 of 3 ports.
- **F10 — type (IMF-7).** `ds-regulator-1x`'s bold labels are refused as "too heavy for its size" (16 of 23 words), the
  serif italic of `typeset` gives 0 of 6 designators and 0 of 5 values, and `heavy-iec` finds 2 words among its 12 labels.
- **F11 — datasheet conventions (IMF-8).** No IC or amplifier reads as one part on its own nets except `ds-ldo-1x`'s and
  `ds-regulator-1x`'s boxes by accident of their pin counts; `ds-rf-amp`'s pin names inside the box read as `Cr`, `nf`,
  `CrO`, and its two-line notes under the blocking capacitors as garbage words (`BiOCi`, `C1EOF`).
- **F12 — one reading, whatever it is (IMF-9).** The shaded illustration and `picture-g` are read as 2- and 5-pin
  circuits rather than refused: Paste forces the schematic kind and nothing asks whether the result is plausible.

## 18. IMF-2 — the picture's own scales, and tolerances that follow them

Every tolerance of the schematic reader was a number of pens w. That is right for wire geometry and wrong for anything
the drawing sizes: a datasheet hairline at 1× draws 8 px type with a 1.1 px pen, a textbook's heavy pen draws 28 px type
with an 8 px one, and the same tolerance in w is a different fraction of the drawing on each. IMF-2 measures the
picture first (`PictureProfile.cs`) and gives each tolerance the unit it is about. It changes units, not what any
stroke or region *is*; that is IMF-3's and IMF-4's.

### 18.1 The profile (R-imf2-1) — `PictureProfile.cs`

Measured once per reading, before the wires are traced, and reported line by line in `SchematicImageReport` and in the
corner of the `--overlay` picture (`ImageOverlay.Legend`), each with how it was measured and over how many pieces:

- **w, the pen.** The reading still runs on the run-width mode (§9.1) — the width of the BINARISED line work, which the
  pen tolerances are about. The profile reports the pen as DRAWN: the darkness summed across each stroke (the shorter of
  its horizontal and vertical ink runs, a pixel either side, in units of the darkest ink), the mode over the skeleton.
  Anti-aliasing keeps that sum, so a 1.1 px hairline that Sauvola binarises 2 px wide — at 1× and, read enlarged, 6 px
  at ×3 — measures 1.1.
- **h, the text height.** The median height of the glyph-shaped pieces clear of the line work (2–20 w tall, 0.3–1.2 as
  wide as tall, inking 15–60 % of their box) that stand in a row with another piece of type of a like height (within
  1.6×, overlapping 40 %, at most 0.6 h apart), counting only a row's tall letters (none shorter than 85 % of a
  neighbour) and no subscript or superscript (a piece just after one 1.3× its height, lowered or raised by 15 %, and the
  pieces running on after it at its own size and level). With fewer than three in rows, every glyph-shaped piece is
  counted and the upper quartile taken — a drawing labelled R, C and v_in.
- **s, the symbol scale.** The median drawn length of the orthogonal lines with two bare ends that carry a lead (a T in
  them) or stand in a stack of such lines (a plate, a ground's bars), and of the orthogonal sides of closed outlines no
  more than 8 h across (a resistor's box, a pin-number box) — measured on the line work as the DEFAULT tolerances read
  it, before any has moved (`WireGraph.SymbolStrokes`' sample). A bare line is its skeleton plus one pen; an end stopped
  by a label kept as a symbol detail is bare, one stopped by a fill (an arrow's head) is not; three or more bare lines
  on one line are a dashed line, not plates. With fewer than two, s = 3 h.
- **g, the drawing grid.** `PictureGrids.Detect`'s canvas grid, else the modal spacing of three or more pairs of
  parallel wires; reported, used by IMF-9.
- **Resolution.** Low when the drawn pen is under 1.6 px or h is under 12 px (§18.3).

Measured against the drawings' own sizes (`PictureProfileTests`), all within 15 %:

| picture | pen drawn → read | h drawn → read | s drawn → read |
|---|---|---|---|
| ds-rf-amp-1x | 1.1 → 1.1 | 8 → 8.7 | 18 → 17.0 |
| ds-rf-amp-2x | 2.2 → 2.2 | 16 → 16.0 | 36 → 35.0 |
| heavy-iec | 8 → 8.0 | 28 → 29.0 | 64 → 63.0 |
| low-res | 1.8 → 1.9 | 7.2 → 7.7 | 40.5 → 38.3 |

Four measurement traps found on the way, each of which moved a number by 20–80 %: `StrokeGlyphs.Groups` returns a line
**and** its halves, so taking its groups whole counts a glyph two or three times; a filled arrowhead cut down its middle
by the shaft's long run is two glyph-shaped halves side by side — a "row of type" — until pieces touching line work are
excluded; a dashed fixture box is dozens of short bare lines and swamped s at 9 px until runs of collinear dashes were
excluded; and an arrow's shaft with an open head has two bare ends exactly like a plate, until a plate had to carry a
lead or stand in a stack.

### 18.2 Tolerances by what they measure (R-imf2-2) — `SchematicImageOptions.cs`

Each option keeps its number, stated in w **at the reference profile**, and takes its class's factor:

- **pen** — always w;
- **text** — `TextFactor`: 1 while h stands 5–7.5 w, else h / w over the band's nearer edge;
- **symbol** — `SymbolFactor`: 1 while s is 9–26 w, else s / w over the nearer edge.

The reference profile is a **band**, not a point, because the pictures every default was measured on span one: the
IEC drawing h = 5.0–6.25 w, s = 11.8–14.8 w (and 6.0 / 14.5 at half size); circuitRF's renders at the round trips'
zooms s = 9.2–9.5 w; the editor screenshot s = 23.4 w. A picture inside the band reads with exactly the numbers it
always had — that is what keeps `ImageScreenshotPasteTests`, the IM-12 round trips and `ImageForeignDrawingTests`
untouched by construction rather than by luck. A **hairline drawing** that IM-12's thickening brings to h = 5 w takes
text factor 1 by construction: the thickening has already set its text to the reference.

| option | default | class | scales with |
|---|---|---|---|
| `SnapAngleDeg`, `MinDeskewDeg`, `MaxDeskewDeg` | 3°, 0.3°, 5° | angle | nothing |
| `HairlineTextHeight`, `TextHeightInStrokes` | 8 w, 5 w | profile — they decide the hairline thickening | nothing |
| `MinTextHeight` | 2 w | text | `TextFactor` |
| `MaxTextHeight` | 12 w | text | `TextFactor` |
| `TextContact` | 2 w | text | `TextFactor` |
| `LabelGap` | 2 w | text | `TextFactor` |
| `MinWireLength` | 3 w | pen | — |
| `MaxResidual` | 0.25 w | pen | — |
| `MinDot`, `MaxDot`, `DotCentre` | 2 w, 6 w, 1 w | pen | — |
| `Attach` | 1.5 w | pen | — |
| `MinDiagonalLength` | 8 w | symbol — a zig-zag's leg against a diagonal wire | `SymbolFactor` |
| `MaxSymbolStroke` | 16 w | symbol | `SymbolFactor` |
| `MaxSymbolStrokeScales` (IMF-3) | 1 s | symbol — a bare line meeting no wire, in s itself | — |
| `Grow` | 1 w | symbol — the region grow | `SymbolFactor` |
| `MaxHopSpan` | 10 w | symbol — the hop span, a constant until IMF-2 | `SymbolFactor` |
| `LineArt` (window, k) | 25 px, 0.34 | binarisation, in pixels | — |

The **merge gap** of `SymbolRegions.Group` is no option: two pieces merge across a gap shorter than the larger one's
extent along the faces, already a length of the drawing. `PictureProfile.Apply` is the one place a factor is applied.

### 18.3 Low pictures are read enlarged (R-imf2-3) — `Resample.cs`

A picture whose pen is drawn thinner than 1.6 px or whose type is under 12 px is **Low** and is read enlarged ×2, or
×3 when ×2 leaves the pen under 3 px or the type under 18 px — never past 4 MP (×3 of a dense 1100 × 605 evaluation
board took 8.8 s to read in a Debug build; ×2 a third of that). The brief's "w ≤ 2 px" is read on the DRAWN pen: every
pen under 1.6 px binarises 2 px wide, while a crisp 2 px pen with 14 px type — what this repository's own test pictures
draw — reads as it is (enlarged, a bold 14 px "IN" read as `tH`). A hairline drawing is never Low: IM-12's thickening
already serves it. The enlargement is a **managed Lanczos-3**, not SkiaSharp's: the Skia this build carries (m119)
offers only cubic resamplers, and a managed filter gives the same bytes on every machine. A whole factor k maps a source
pixel's edges onto k of the enlarged picture's, so `SchematicImageRead.ToSource` divides by `Scale` after undoing the
deskew, and `FromSource` multiplies first; every consumer of a read coordinate already went through them, and the three places
that used the read's pen as a length in the file's pixels (the overlay's dot radii, the emit's fallback frame, the
dialog's join reach) divide by `Scale`. The report says so in one line.

### 18.4 Colour and fills (R-imf2-4)

**Kind** (`ImageKind`). Before the area vote, line art is weighed: when the straight runs are line work, at least 80 %
of their length runs across or down, at least 5 % of the ink is type in rows, and at least 40 % of the ink is in colours
drawn as thin strokes (a colour's median run — the shorter of its horizontal and vertical runs through each pixel — no
more than 3 strokes), the picture is a schematic whatever it fills. Measured: every schematic in both corpora is
0.46–1.0 thin (the shaded crops `ds-gain-block-2x`, `picture-ds-c2`, `-f2`, `-g2` at 0.46–0.53 — the fill is half their
ink, which is why "most" could not be the bar); IM-12's dark layouts 0.25–0.26, the shaded illustration 0, `picture-g`
0.23. All four coloured crops that read as layouts now read as schematics; both layouts and both illustrations stay
non-schematic. A fill's edge lying on ink rather than paper was tried as a second tell and does not separate (0.48 on
the gain block, 0.61–0.78 on layouts and illustrations).

**Colour is not darkness** (`SchematicImageReading.Binarise`). Besides the darkest colours (§9.1), every colour drawn
as thin strokes is ink when it stands clear of the ground — a quarter of the darkest ink's contrast, or ΔE 40 — and is
not another line's anti-aliased rim (more than half its pixels touching ink already kept). Without that last test
circuitRF's own render kept its blue wires' lighter rim (`#6a8ef6`, a 1 px run) and its profile left the reference band.
What drops out: a pale tint (a grid, a gradient, a highlight) and a wide colour that is not dark (a shaded triangle).

**Fills are not ink.** A fill (`Fills`, §9.3) wider or taller than 3 s keeps only its rim — the pixels within a pen of
its edge — in the symbol residue; its inside is recorded on the region it lies in (`SymbolRegion.Fills`) and reported.
It was already never wire or junction; now it is not a region's ink either. A small fill (a diode's triangle, a dot) is
ink as before. `ImageDatasheetStyleTests.GainBlock_TheTrianglesFill_MakesNoWireAndNoJunction` holds the triangle.

### 18.5 The options expose the profile (R-imf2-5)

`SchematicImageOptions.Profile` is `PictureProfileOptions.Auto` or any of pen, text height and symbol scale stated in the
file's pixels; a stated value replaces only itself (`AStatedScale_ReplacesOnlyItself`) and is recorded in the
provenance. The dialog's Advanced row has three fields that show the measured value while empty; the CLI takes
`--pen`, `--text-height`, `--symbol-scale`; the MCP `recognize` tool `pen`, `textHeight`, `symbolScale`.

### 18.6 The field table after IMF-2

Scored exactly as §17.1 (`ImageFieldTests`, read from its TRX; `ms` is a Debug build under a parallel test run).

Committed style pictures:

```
picture                  auto       read     kinds  ?   topo ports gnd   term  dev   block desig values names region     ms
ds-gain-block-1x         schematic  read     1/5    6   no   1/2   3/4   -     0/1   1/1   0/5   0/2    0/1   3        1329
ds-gain-block-2x         schematic  read     1/5    11  no   1/2   0/4   -     0/1   0/1   0/5   0/2    0/1   4         698
ds-ldo-1x                schematic  read     0/2    1   no   2/2   3/3   2/2   0/1   1/1   0/2   0/2    0/2   7         949
ds-ldo-2x                schematic  read     0/2    1   no   2/2   0/3   2/2   0/1   0/1   0/2   0/2    0/2   3         535
ds-regulator-1x          schematic  read     0/7    3   no   -     3/4   -     0/1   1/1   0/7   0/7    0/2   8        1950
ds-regulator-2x          schematic  read     0/7    4   no   -     2/4   -     0/1   0/1   0/7   0/7    0/2   9         925
ds-rf-amp-1x             schematic  read     1/5    13  no   0/2   2/4   -     0/1   1/1   3/5   1/5    0/1   16       1730
ds-rf-amp-2x             schematic  read     1/5    3   no   0/2   1/4   -     0/1   0/1   2/5   1/5    0/1   9         862
ds-test-circuit-1x       schematic  read     0/3    2   no   0/2   0/3   -     0/1   0/1   1/3   0/3    0/1   16       1225
ds-test-circuit-2x       schematic  read     0/3    4   no   0/2   0/3   -     0/1   0/1   2/3   1/3    0/1   6         722
heavy-iec                schematic  read     0/8    5   no   0/1   5/6   -     0/3   -     0/9   -      -     35        834
illustration             layout!    read!    -      2   -    -     -     -     -     -     -     -      -     2         390
ladder-dashed            schematic  read     0/6    4   no   0/2   -     4/4   -     -     0/6   -      -     6         362
loop-coil-names          schematic  read     1/2    0   no   0/3   -     -     -     -     -     -      0/3   2         205
low-res                  schematic  read     1/2    3   no   1/2   -     2/4   -     -     0/2   -      -     2         528
textbook-zigzag          schematic  read     0/2    2   no   1/2   -     4/4   -     -     0/2   -      -     2         317
typeset                  schematic  read     1/5    2   no   -     1/1   -     0/1   -     0/6   0/5    -     4        1297
```

Field pictures (local, git-ignored; datasheet crops first):

```
picture                  auto       read     kinds  ?   topo ports gnd   term  dev   block desig values names region     ms
picture-ds-a1            schematic  read     0/6    3   -    0/2   -     -     0/1   -     1/6   -      0/1   12       4066
picture-ds-a2            schematic  read     0/6    4   -    0/2   -     -     0/1   -     1/6   -      0/1   20       2089
picture-ds-b1            schematic  read     2/4    3   -    1/2   -     -     0/1   -     2/4   -      -     4         411
picture-ds-b2            schematic  read     1/4    5   -    0/2   -     -     0/1   -     3/4   -      -     4         619
picture-ds-c1            schematic  read     1/5    3   -    1/2   -     -     0/1   -     -     -      0/1   12        265
picture-ds-c2            schematic  read     1/5    2   -    1/2   -     -     0/1   -     -     -      0/1   17        999
picture-ds-d1            schematic  read     0/7    6   -    -     0/1   -     0/1   -     0/4   0/3    -     8        5488
picture-ds-d2            schematic  read     0/7    2   -    -     0/1   -     0/1   -     0/4   0/3    -     17       1143
picture-ds-e1            schematic  read     0/3    1   -    0/2   -     -     0/1   -     -     -      -     7        1715
picture-ds-e2            schematic  read     0/3    4   -    0/2   -     -     0/1   -     -     -      -     9         947
picture-ds-f1            schematic  read     -      3   -    -     -     -     0/2   -     -     -      -     70       3581
picture-ds-f2            schematic  read     -      2   -    -     -     -     0/2   -     -     -      -     104      2991
picture-ds-g1            schematic  read     -      6   -    -     -     -     0/4   -     -     -      -     116      4247
picture-ds-g2            schematic  read     -      2   -    -     -     -     0/4   -     -     -      -     157      4441
picture-ds-h1            schematic  read     1/2    1   -    2/2   3/3   -     0/1   -     0/2   0/2    0/2   6        1424
picture-ds-h2            schematic  read     0/2    1   -    2/2   2/3   -     0/1   -     0/2   0/2    0/2   8         651
picture-a                schematic  refused  0/8    0   -    -     0/4   0/6   -     -     -     -      -     0         231
picture-b                schematic  read     1/4    1   -    -     -     -     -     -     0/2   -      -     2         610
picture-c                schematic  read     0/8    2   -    -     -     -     0/3   -     0/8   -      -     32        607
picture-d                schematic  read     0/2    8   -    -     1/1   -     -     -     -     -      0/2   3         936
picture-e                schematic  read     0/5    2   -    -     -     -     -     -     -     -      -     12        512
picture-f                schematic  read     0/6    8   -    -     -     4/4   -     -     -     -      -     4         302
picture-g                layout!    read!    -      3   -    -     -     -     -     -     -     -      -     7        1328
picture-h                schematic  read     6/7    9   -    -     -     -     0/2   -     -     0/7    -     5         482
picture-i                schematic  read     1/2    1   -    -     -     -     -     -     -     -      0/3   2         782
```

The units move scores little, as expected. What moved: the four coloured crops are schematics; `ds-gain-block-1x` is no
longer refused as curved line work; `heavy-iec` finds 5 of its 6 grounds (1); the largest region falls on `ds-rf-amp-1x`
(45 → 16 pins), `picture-ds-g1` (270 → 116), `picture-ds-d1` (20 → 8) and `heavy-iec` (40 → 35); `picture-ds-h1`
reads all three grounds and one passive. Worse: `ds-test-circuit-1x`'s largest region grows from 7 to 16 pins and its
one passive is lost, and `picture-e`'s from 5 to 12 — both read enlarged, where the default region merge now meets a
drawing three times its size in pixels at the same w; that is IMF-4's merge, not this phase's units.

## 19. IMF-3 — what is wire: plates, boxes, annotation arrows, dashes

IMF-2 gave every tolerance its unit; IMF-3 makes the wire graph take for wire only what is wire, whatever the pen.
Two places change. `WireGraph.SymbolStrokes` learns the picture's own plates and boxes once s is known (it keeps IM-7's
rules exactly for the symbol scale's own measurement, which runs before s exists). A new step,
`StrokeMarks.Find` (`StrokeMarks.cs`), runs on the runs the final tolerances cut and finds what is drawn on a schematic
but is not part of the circuit — arrows, dashed lines — and the zig-zags. What it calls an annotation leaves the ink,
and the line work is skeletonised again without it; it then runs once more on the new runs for what does not erase
(dashed wires, zig-zag legs, supply arrows), and the removed annotations are kept as first found.

### 19.1 Plates at any pen weight (R-imf3-1) — `WireGraph.SymbolStrokes`, `PlateRules`

A straight line with two **bare** ends — the skeleton stops in paper, or on a label kept as a symbol detail — is:

- a **plate** when exactly one lead meets it from the side, in its middle 60 %, and nothing crosses it — at any length
  up to 4 s (`MaxPlate`; the heavy IEC drawing's plates are 152 px = 19 w = 2.4 s). A ground's top bar is one too;
- a stroke when it meets no wire and is no longer than `MaxSymbolStroke` w **or** `MaxSymbolStrokeScales` s (1 s) —
  a datasheet's hairline bars at 2× run past 16 pens;
- either way still a stroke under `MaxSymbolStroke` w, as IM-7 had it, whatever meets it.

A line with a word **off both ends** — clear of its middle — is a labelled wire, never a plate. "Clear of its middle"
matters: a coax connector's ground drawn at 1× has its two lower bars read as a word lying under the top bar, and
counting that as a label at both ends made the bar a wire.

Two more shapes are plates. A **pair**: two such lines side by side, within 10°, 1 w to 1.5 s apart, overlapping over
half the shorter, no more than 2.5× different in length, with the strip between them clear of ink — or a line with
**one** bare end and a lead (its other end running into a glyph of a label that touches it) beside a plate already
found, or beside a parallel stroke of ink as long across a clear strip (the partner can be dropped as part of the
touching label). A **curved plate**: a chain of runs turning gently (each turn ≤ 45°) between two bare ends, its chord
2 w to 1.5 s, its sagitta under 0.35 of the chord, with one lead meeting it — or within 45° of a plate's direction
across a clear strip.

A half plate beside its lead's T leans on the T's pixels (C4's on `ds-rf-amp-1x` fitted 4° off level) and was no
candidate, so the plate's line was cut in two. `WireGraph.Square` now also snaps a short run within 12° of an axis that
carries straight on, end to end, from a candidate on that axis **to a bare end of its own** — without the bare end, an
italic F touching both plates of a capacitor was snapped into a loop.

### 19.2 Boxes at any pen weight (R-imf3-2) — `WireGraph.Sides`

The cycle rule (§9.3) was right; heavy pens defeated it. An 8 px pen fills a 40 px box's corners, and the box's short
sides, cut in two by their leads' Ts, are 2.3–2.5 w — under `MinWireLength`, so no candidates, so no cycle, so the long
sides were wire. A **side** is an orthogonal run too short to be a wire, straight, with neither end in paper, both of
whose ends meet the end of an orthogonal run within 1.5 w — at a corner (a perpendicular run) on at least one, and in
line only with another short run (a long run in line is a wire, and its last pixels before a plate are no side).
Sides take part in the **cycle search only** (`SymbolStrokes` assembles them with the candidates for it); they never
become wire and never enter the line analysis, where they cut plates' lines. A side wholly inside a kept label no
taller than 1.5 h is no side. On the heavy IEC drawing all five resistor boxes are set aside.

### 19.3 Annotation arrows (R-imf3-3) — `StrokeMarks.Arrows`

**A head** is read off the ink's width across a run's line, from half a symbol scale back down the shaft to two past
its end, every half pixel: a pen-wide shaft; within half a symbol scale of the run's end an abrupt widening to at least
max(1.8 w, pen + 0.8 w) and 2.2 w; widest within the first 40 % of its length; narrowing steadily (no rise over a pen),
within a third of its width of a straight taper; 2 w to 2 s long, 0.3–1.8 as wide as long; and solid — 90 % of a
grid inside the triangle is ink. A dot widens and narrows symmetrically, a plate or a ground bar is a pen deep, a
diamond is widest in its middle, two glyph strokes meeting at an angle leave paper between them: none is a head.
What lies past the apex is read off the same profile: paper; a **bar** as wide as half the head again within half a pen
and narrow again within 1.6 w (a filled diode's cathode); a **wire across** the tip — wider than twice the head within
1.5 w (a dimension's head touching a rail, often stopping a pen or two short of it); the **shaft's wire carrying on** a
pen wide for 2 w; or anything else (a terminal's dot).

| class | read when | done |
|---|---|---|
| dimension | heads at both ends; or one head into a wire across its tip (the shaft is often broken by its own label: each half is one); or tail on a wire with a wire just past the apex | shaft and heads removed, other wires spared |
| current, inline | one head, the wire carrying on past it, head ≤ 1 s | head removed but the wire's own stripe through it kept |
| current, beside | one head, tail in paper, a parallel wire within 2 s | removed |
| voltage | one head, tail in paper, no wire alongside | removed; kept in `Annotations` as evidence for IMF-6's port pairs |
| into a terminal | one head ending in a blob, the shaft a wire (or its tail on one), head ≤ 1 s | head removed, the stripe kept — the wire runs into the terminal |
| supply | one head into paper, the tail on a wire or a symbol, a word within 2 h of the apex | left as drawn (IM-7's supply mark) |
| on a symbol | a diagonal shaft crossing other line work, head into paper | left in its region for IMF-5 |

A diagonal shaft crossing nothing — a transistor's emitter arrow — is no annotation. A filled head is often a *fill*
(§9, `Fills`), taken out of the wire mask with the wire through it; the kept stripe is put back as line work
(`StrokeMarks.Restore`), or an inline head cut its wire in two. `Fills` itself now keeps a dot-sized blob as a junction
only when it is **symmetric about its centre** (92 % of its pixels' point reflections in it): a filled diode's triangle
at a datasheet's scale is dot-sized, and read as a junction it drew a wire straight through the diode — which closed a
loop through the regulator's IC box and took the whole circuit for one symbol (§17's "one 17–20-pin region").

### 19.4 Dashed lines (R-imf3-4) — `StrokeMarks.Dashes`

A **dash** is an orthogonal straight run 0.75 w to 3 s long that stops in paper at both ends, or at a wire crossing it
(a coil's humps, cut into runs, line up as evenly but each carries on into the next). Three or more on one line, within
half a pen across, lengths within 1.5× of each other, gaps 0.5 w to 1.5 s and no more than three dashes long, each gap
like the last, and **paper between them** (but for a wire crossing) are a dashed line. Dashed lines of the two
directions whose ends meet within a gap and a dash (and 2 w) — a corner's own dash bends round it and belongs to
neither line — are a **box**. A box, or a dashed line with no wire in line beyond either end, is an annotation: its ink
is removed along its whole length and a dash and a gap past each end, sparing every other wire. A dashed line with a
wire in line beyond an end — collinear within a pen, its end within 1.5 gaps and 2 w, and longer than 1.5 dashes (a
corner's dash or one cut by a crossing is no wire) — is **one wire** from that wire's end to the other's, flagged
`WireSegment.Dashed`, and named once in the report: "A dashed line was read as a wire; it usually stands for repeated
sections left out." At a datasheet's 1× (`ds-test-circuit-1x`) the fixture box's dashes are read as type and removed
before the wires are; it makes no wire either way.

### 19.5 Zig-zags never refuse (R-imf3-5) — `StrokeMarks.Zigzags`

A chain of three or more slanted runs — each a mirror of the next (folded angles within 15°, turning at least 40° at
the peak), or a leg the skeleton cut in two carrying on — end to end within 3 w, all at one slope either way (folded
angles within 20°), rising and falling legs each 25–75 % of the chain's length, crossing the line of the two collinear
orthogonal leads at its extreme ends, its axis within 15° of theirs. Its legs are never wire, so the curved-lines
refusal (§13.4) never counts them. A coil's arcs, cut into runs, lean every way and fail the slope rule; an amplifier
triangle's two sides meet the lead line only at the apex and fail the crossing rule.

**Deviation from the brief:** its legs are 1.5 w to 1.5 s, not "1–6 s". Every zig-zag measured has legs under one symbol
scale — 0.4 s on `picture-a`, 0.6 s on `textbook-zigzag` — so the brief's range would have found none.
`picture-a` (six attenuator zig-zags, refused at the baseline) now reads, its four grounds and four of six terminals
right; a Smith chart has no chains and is still refused (`ImageNothingHereTests`).

### 19.6 Junction marks of any shape (R-imf3-6)

Round, square and diamond marks were already found by `WireGraph.Blobs` (2–6.9 w across, the 6 w bound with the
distance transform's slack) at every scale the corpus has, the datasheet's 4 w dot on a 1.1 px hairline included once
IMF-2 reads it enlarged; what was missing was keeping a dot-sized filled *triangle* out of them (§19.3). Each junction
mark is now classified by rays from its centre (`StrokeMarks.ShapeAt`): along the diagonals, where no orthogonal wire
leaves, a square reaches 1.25× or more of the free axes' reach; along an axis no wire leaves by, a diamond reaches 1.25×
the diagonals; a mark under 4 px in inscribed radius is round. A distance-transform core does not work: the core of a
disc bulges along every wire leaving it, and every round dot read as a diamond.

### 19.7 The report and the overlay (R-imf3-7)

`SchematicImageReport` counts plates met by a lead, pairs and curved plates, closed outlines, arrows by class, dashed
lines removed and read as wire, zig-zags left out of the curved-lines count, and junction marks by shape (only when one
is not round). `SchematicImageRead.Annotations` carries every arrow and dashed line with what it was read as and
whether its ink was removed; the overlay draws them as a new class, `ImageOverlayClass.Annotation`, dashed grey with the
reading as the label, shown with the wires.

### 19.8 The field numbers after IMF-3

The committed style pictures (`ImageFieldTests.TheCommittedStylePictures_AreScored`), against §18's:

| picture | kinds | gnd | region | what moved |
|---|---|---|---|---|
| ds-regulator-1x / 2x | 0/7 → 3/7, 0/7 → 2/7 | 3/4 → 4/4, 2/4 → 4/4 | 8 → 5, 9 → 6 | the diodes are fills, the Vref arrow is a dimension: the output and adjust rails are two nets and the IC box is no longer swallowed |
| ds-rf-amp-1x / 2x | 1/5 → 2/5, 1/5 → 3/5 | 2/4 → 3/4, 1/4 → 3/4 | 16 → 5, 9 → 5 | hairline plates are plates; the supply arrow is found |
| ds-test-circuit-2x | 0/3 → 2/3 | 0/3 → 2/3 | 6 → 4 | the dashed fixture box is removed |
| ds-gain-block-2x | 1/5 → 2/5 | 0/4 → 4/4 | 4 | the hairline ground bars are bars at 2×; the current arrow beside the bias resistor is removed |
| heavy-iec | 0/8 | 5/6 | 35 → 23 | plates and box sides are no wire; the arrow into v_o is a terminal arrow |
| ladder-dashed | 0/6 | — | 6 → 2 | each rail one net through its dashes |
| low-res | 1/2 → 2/2 | terminals 2/4 → 4/4 | 2 | the voltage arrows are removed |
| typeset | 1/5 | 1/1 | 4 → 10 | **worse**: see below |

On the downloaded corpus, `picture-a` reads instead of being refused; the real regulator's dimension arrow — cut in two
by its own label, each head touching a rail — is a dimension at both scales; the real gain blocks' current arrows and
the test circuit's dashed fixture boxes are found; `picture-c`'s largest region falls 32 → 20.

**`typeset` is worse, and the cause is IMF-4's.** Its C1 is labelled in italic touching both plates, and the label's F
joins the plates' left ends; with it the right-hand mesh (C1 and R3 between the rails) is a closed loop of candidates,
and the cycle rule takes the mesh for an outline. At IMF-2 the same mesh read as wire with C1 a short through the F.
Both readings are wrong in the same place; lifting a touching label off its symbol (F7, IMF-4) removes the loop.
Excluding every candidate inside a kept label fixed `typeset` and broke the L-network and the attenuator round trips,
whose kept labels hold real wire.


## 20. IMF-4 — regions that stop at parts; labels lifted off the symbols they touch

IMF-3 left wire as wire; IMF-4 makes one symbol region one part. Four places change. `WireGraph.SymbolStrokes` decides
which loops are outlines by their faces. A new step, `GlyphLift.Lift` (`GlyphLift.cs`), takes the type touching a
symbol out of the residue before the regions are grown, and `TextRegions` keeps a word whose glyphs are cut by the line
work it is written across. `SymbolRegions.Group` merges closest first under the new rules, closes regions on their
outlines and envelope circles, and re-cuts a runaway. The reader drops the wire runs drawn inside an envelope circle.

### 20.1 Outlines are faces no junction dot stands on (R-imf4-2) — `WireGraph.OutlineFaces`

IM-7's cycle rule set aside every candidate on a cycle. On a datasheet an IC's GND pins are tied outside the box —
pin 2 and pin 3 to one vertical wire with a dot — and that loop of stubs, the vertical and the box's side is a cycle:
the stubs went into the box's region, which then reached 5 of its 8 pins. With the final rules (not the symbol scale's
own measurement, which keeps IM-7's), the cycle edges are embedded in the plane, each vertex's edges in angular order,
and the faces traced (next edge clockwise); a bounded face (positive area, y up) is an **outline** unless a junction dot
stands on one of its vertices. Only an outline face's edges are strokes. The loop of pin stubs runs through the dot
where the wires meet, so its stubs are wire and the box's side between the pins — which also bounds the box's own face
— is the box's. A loop of wires read as wire is counted in the report. `PlateRules.Outlines` carries each outline's
box and whether it is drawn across and down only (a box) or not (a triangle).

### 20.2 Glyphs come off symbols first (R-imf4-4) — `GlyphLift.cs`, `TextRegions`

Before regions are grown the residue is read for type. A **piece** is a residue component no larger than a glyph, or a
component cut where it meets a symbol stroke:

- a straight run longer than 1.5 h **and** 0.6 s is a symbol's stroke (a plate, a bar); without its runs a capital
  touching a plate's end (`ladder-dashed`'s C₁) is a component of its own. Not shorter: an italic L's foot lying along a
  coil's humps makes a 25 px run of its own and cut the coil in three;
- otherwise its skeleton's **bridges**: at each skeleton node, the branches hanging off it by a bridge that no wire end
  reaches, together, are a piece when some branch of that node is reached (the symbol) and they are glyph-sized. A
  letter touching a coil at its corner hangs off it by two strokes, stem and foot, from one junction — one bridge
  alone gave the stem without the foot.

A piece any wire end reaches is never lifted (a terminal circle, a hop's half arc, a coil's end hump). A piece is a
glyph when it is 0.7–2 h tall, at least 0.3 as wide as tall and a sixth to six tenths ink, and lies in a row with
another glyph or a word's glyph — or carries a subscript, 0.25–0.8 its height, starting at its right side with its top
in the base's lower part. Inside an orthogonal outline a glyph-height piece needs no row. Lifted glyphs are new
`TextRegion`s (`Lifted`), numbered with the rest in reading order.

**Two deviations, measured.** The upper bound is 2 h, not 1.4 h: on `ladder-dashed` every capital touches its symbol,
so the profile measures h on the glyphs standing free — the subscripts — and the italic capitals stand 1.5–1.9 h. And
a subscript may be 0.25 of its base, not 0.5: a subscript standing on a coil's hump is cut at its foot (L₂'s 2 keeps
7 of its 15 px). A cut subscript is also wider than tall, so a subscript's shape allows 1.5:1.

`typeset`'s C1 needed the same thing earlier, at the words: its italic `100pF` is written across both plates, and the
F's stem between them joined the plates into a loop that IMF-3's cycle rule took for the whole right-hand mesh. In
`TextRegions` a word's piece touching line work now joins the word when it is a **fragment** — no taller than 0.6 of
the word, not long and thin — and touches only line work running *along* the line of text (a plate under the word); a
curved plate or a hump touches its lead, which runs across the line, and stays. And a line of text sharing pieces with
a word found first is read without them when they were only that word's symbol detail: `Cadj` took its capacitor's top
plate as detail, and the `10µF` line below it, which shared those pieces, was skipped whole, so its `1` became a wire
between the plates.

### 20.3 One part per region (R-imf4-1, R-imf4-2, R-imf4-3) — `SymbolRegions.Group`

Two pieces merge only when all hold: they face each other across a gap shorter than **both** 1 s and the larger one's
extent along the faces (IM-7's rule kept as a ceiling — `low-res`'s `C` beside its v_out label, 0.6 s apart, merged
under 1 s alone); no wire runs through the gap; no wire end arrives at either from the side facing the gap; their box
is no larger than 4 s on its shorter side; and they would not have more than four wire ends unless both already have
three. One inside the other still merges. Pairs are merged **closest first**: a capacitor's two plates join before
either joins the part beside it, so the re-cut below falls between parts.

A piece that is a closed outline (one of §20.1's filling most of its box — the pin-number boxes on an IC's side are
the IC's) takes in every piece inside it and nothing outside. So does an **envelope circle**: ink running 85 % of the
way round a circle 1–4 s across, with ink inside, found from a residue piece's box (as wide as the circle one way,
against one side of it the other) or from a circle fitted through an arc of it — the leads crossing a circle cut its
outline into arcs. Every residue piece inside the circle is the circle's, and the wire runs the reader took inside it
(a straight stretch of the outline, the base bar, the collector's diagonal) are dropped and the wires read again:
heavy-iec's transistor had five wire ends, three of them its own outline's flat sides. **Deviation:** the brief's
circles are 3–8 s across; the symbol scale is a plate's length, and the corpus' circles are 1.6–2.8 of them.

### 20.4 A runaway is re-cut (R-imf4-5)

A region is a runaway when it has more than eight wire ends and no closed outline, holds more than one outline
(touching outlines are one) or more than one plate pair (two plate lines, parallel, overlapping, 1 w–1.5 s apart), or
— not in the brief — has two pairs of wire ends each arriving from opposite sides on one line, on parallel lines at
least 1.5 s apart: two two-pin parts side by side, which no rule of R-imf4-1 separates (the regulator's variable
resistor and Cadj, 1 s apart, two wire ends each). It is split on the graph of its pieces — an edge between two pieces
facing each other within 1 s, weighted by the gap — removing the widest gap until no part is a runaway. A region no
cut splits stays, and is named in the report as read as an S-parameter block.

### 20.5 The report (R-imf4-6)

Loops of wire through a junction dot; regions closed on boxes, triangles and circles; pieces merged; merges refused by
rule (too far apart, a lead on the facing side, too large together, too many wire ends together, outside a closed
outline); glyphs lifted; regions split and into how many; regions left as blocks. Words carry `Lifted` and `Place`
(`InsideBox`, `AtPin` beside a triangle) for IMF-7 and IMF-8.

### 20.6 The field numbers after IMF-4

The committed style pictures (`ImageFieldTests.TheCommittedStylePictures_AreScored`), against §19.8's:

| picture | kinds | gnd | topo | region | what moved |
|---|---|---|---|---|---|
| ds-ldo-1x / 2x | 0/2 → 2/2 | 3/3 | no → **yes** | 7 → 4 | the IC box has its four pins, CIN and COUT are their own |
| ds-regulator-1x / 2x | 3/7 → 4/7, 2/7 → 5/7 | 4/4 | no | 5, 6 → 3 | one region per part at both scales |
| ds-rf-amp-1x / 2x | 2/5 → 3/5, 3/5 → 4/5 | 3/4 | no | 5 → 8 | the IC box has its 8 pins (the largest region is now the IC's, rightly) |
| ds-test-circuit-1x | 0/3 → 3/3 | 0/3 → 2/3 | no | 14 → 2 | |
| heavy-iec | 0/8 → 6/8 | 5/6 → 6/6 | no | 23 → 3 | one region per part; the transistor's circle is one region of three wire ends |
| ladder-dashed | 0/6 → 6/6 | — | no | 2 | every designator is a word |
| typeset | 1/5 → 2/5 | 1/1 | no | 10 → 2 | `100pF` across C1 is a word, C1 two wire ends, the mesh is wire again |

`block` fell from 1/1 to 0/1 on `ds-rf-amp` and `ds-gain-block-1x`, and neither is a loss. The scorer counts a block
right when some Amp or SnP has as many nets as the answer's. The RF amplifier's 5-pin region happened to match; its
8-pin box's nets differ from the answer's U1 while one of its grounds is unread (gnd 3/4, IMF-6). `ds-gain-block-1x`'s
"block" was the bypass capacitor and the RF choke merged into one 3-pin region; its triangle is not closed at 1× (its
top corner opens) and its sides are wire — IMF-5's.

On the downloaded corpus the largest region falls everywhere: `picture-ds-g2` 184 → 16 wire ends, `picture-ds-f2`
102 → 8, `picture-ds-a2` 17 → 4, `picture-ds-d2` 16 → 2, `picture-c` 20 → 3, `picture-e` 12 → 2, `picture-a` 16 → 4.

**Seen, not fixed (IMF-3's).** A wire through two IEC resistor boxes on one line, its three runs of one length, reads
as a dashed line at a large stated symbol scale: each run stops at a box side, which the dash rule takes for a wire
crossing it. `SymbolRegionsMergeTests` draws its runs of different lengths for that reason.

## 21. IMF-5 — two-pin parts, transformers, transistors and sources, as datasheets and textbooks draw them

IMF-4 left one region per part; IMF-5 names it. New drawings are `.stroke` files (`capacitor-wide`, `coil-looped-3…5`,
`source-ac`, `source-dc`, `source-plus`, `supply-plus`, `source-current`, `battery`, `battery-two-cells`,
`transformer-core`, `transformer-air`, `transformer-centre-tap`); the four transistors gain an enveloped twin composed
from their built-in drawing (`SymbolTemplates.Enveloped`). A template carries a `Variant` (`npn`, `ac`, `looped` …)
which the circuit is made with. The structure checks measure a drawing's lengths in the symbol scale s and a pen's in w
(the classifier is handed `SchematicImageRead.SymbolScale`), and three are new.

### 21.1 Capacitors (R-imf5-1)

`capacitor-wide` draws a datasheet's plates, two and a half gaps long. The capacitor check wants a band of bare paper
no wider than 1.5 s between two plates each at least min(2.5 w, 0.4 s) long — and neither under 0.55 of the other,
which is a battery's. The ink it looks at is the ink within the plates' own reach of the axis: the unbroken ink across
the axis just inside each lead, the lead's hole at the axis allowed (the wire is taken off the plate there). A label
standing beyond a plate's end is then beside the part; a rectangle's joining sides are not. Where no template takes a
two-pin region, it is read once more with the ink beyond the plates' reach trimmed off (`PlateTrim`) and taken only as
a capacitor — `ds-rf-amp-1x`'s C3, whose `C3` touches its plate's end. A `+` word beside one plate makes that plate's
lead pin 1 (the emit). The curved plate is pin 2 in `capacitor-curved`; the datasheet curves read best as
`capacitor-wide`, and the circuit's C has no polarity.

### 21.2 Diodes (R-imf5-2)

The diode template already took filled regions; what lost was the filled IEC inductor, within the kind margin of it.
`iec-inductor` now asks that the filled ink fill three quarters of its box (`FilledBox`), measured on the ink deeper
than 0.6 w — the opening `Solid` is decided by rounds a triangle toward a disc. The diode asks for no band of bare
paper between its leads (`Diode`), which a capacitor has. Diodes are parts-table rows (`D`); a word under a diode's or
transistor's designator is its value word, and the row takes it as its `PartNumber`.

### 21.3 Resistors (R-imf5-3)

The zig-zag's swing must reach 0.1 s (and half a pen), binned at the finer of w and s/16: a heavy and a hairline
zig-zag both alternate. A **variable** resistor: a two-pin body crossed by a straight run of ink at 20°–70° to its axis,
crossing it between the leads, longer than 1.2 s or half the body (`CrossingArrow`) — no zig-zag leg is — is read again
with that run and the arrowhead at its wider end erased, and is a resistor `Variable`, reported "its value is one
setting". Where the arrow's tail stands out of the body it is read as a short slanting wire with one bare end; the
reader takes such a wire off (`ArrowTails`) before the regions attach, or the resistor has three pins.

**Hatched** (deviation): the brief closes a hatched zig-zag inside its region. On the picture the ticks never reach a
region — the word finder takes a row of glyph-sized pieces for type and lifts them. `Hatching.Close`, at normalisation,
finds a chain of twelve or more small pieces of one size, most 1.6 times as long as wide and lying ACROSS the chain
(a dashed line's dashes lie along it), and redraws it as the line through the ticks — the shortest tree joining their
centres, at the line work's pen. The region-level close by w stays for a region that still arrives in many pieces.

### 21.4 Coils and beads (R-imf5-4)

`coil-looped-3…5` are prolate cycloids that start and end on the leads' line; the coil check passes a looped coil on
three loops in its centre line, or (loops the skeleton merged) three dips of a whole w in its nearest approach to the
axis. `loop-coil-names`' coil has no wire end at all — its foot stands on its rail at a junction dot and its head is a
free lead — so two things not in the brief: a **tap** (`Taps`) draws a stub from a dot on a wire's interior to a region
no wire end reaches, snapped across or down; and a **free lead** (`FreeLead`) gives a one-ended region drawn along its
wire, twice as long as across, a second pin with no node where it ends in a straight stub on that line. The free pin
stands only when it makes a part; it is an open net of its own. A box marked `BEAD`, or a filled box on a supply net (a
supply mark's, or one a `V…` word names), is a ferrite bead: `Bead`, its parameters the defaults, reported.

### 21.5 Transformers (R-imf5-5)

Two coils facing across a core or a gap, four pins, or five with a centre tap (the classifier reads regions of up to
five ends). Emitted as drawn: an L per coil — a centre tap splits its coil in two — and a `Mutual` for each pair. A
Mutual takes M, not k, so M is `k*sqrt(L1*L2)` written over the coils' own values or variables; k from a word beside it
(`k=0.95`), else the tunable global `<ref>_K` at 0.99. In the parts table a transformer is three rows on one symbol —
its coils (`T1_L1`, `T1_L2`) and its coupling (`T1`, kind `K`). No committed style picture draws one; the
`SymbolMatchTests` drawing holds it.

### 21.6 Transistors (R-imf5-6, O1)

Each transistor template has an enveloped twin: the built-in drawing with a circle a tenth wider than its body, its
leads cut where they cross it, as IMF-4 cuts a picture's wires. The envelope's inner disc must be mostly paper
(`Envelope`): a large junction dot with three wires on a field picture matched the circle alone. **Polarity is read off
the arrow**, not the template: the arrowhead is the region's deepest ink, its axis the main direction of the ink round
it, and it tapers toward its tip — sampled inside the head, within its inradius, since beyond the base the arm's own
width is read. A BJT's arrow pointing at its emitter pin is an n-p-n; a FET's pointing away from its gate pin an
n-channel. Emitted as `BJT_NPN`, `BJT_PNP`, `FET_Curtice` or `PFET_Curtice` on the template's pins (c, b, e; g, d, s),
every parameter its default, reported "with the default model — set its parameters". A three-pin region that is no
transistor is still an S-parameter block. **Not read** (deviation): a transistor drawn without its circle — its bar
and arms are straight strokes the wire reader takes for wire; the corpus draws every transistor in a circle, and the
brief's PNP gate drawing is drawn in one.

### 21.7 Sources (R-imf5-7, O2)

Circles with a sine, with + and −, with a + alone (two leads, or one: a supply), with an arrow, and the long-short
battery (one and two cells). A source circle must have its mark inside (`Marked`) — an empty circle between two leads
is no source — and a battery's plates must differ (`Battery`). AC is a `P1Tone`, numbered first among the ports, its
`Pavl` and `Freq` from a word beside it (`-10 dBm`, `105MHz`) or the tunable globals `<ref>_Pavl` (0 dBm) and
`<ref>_Freq` (1 GHz); its minus is the pin on ground. DC, a battery and a supply are a `Vdc` whose value is a word
beside it (`6V`, the last word of `Vs 6V`) or the global `<ref>_V` at 0 V, + first, minus on ground (a supply circle's
minus is ground); a current circle an `I_1Tone` with its `Idc`.

### 21.8 The parts table, the report, the overlay (R-imf5-8)

`PartKind` gains `D`, `Q`, `V`, `I`, `K` and `Bead`, and `PartRow` a `Variant`, written in the Kind column after the
kind — `Q NPN`, `V AC` — and read back (`PartsTable.ParseKindAndVariant`). A V or I row's value is volts or amps and a
K row's is k (`PartsTableCsv.TryReadValue`). A Q row may change only its polarity and a K row only its value; the
picture's kinds are refused on an artwork's table. The symbol report counts the variants and the variable resistors;
the emit names each device and its default model; the overlay's label carries the variant (`Transistor NPN`,
`Source AC`, `Resistor (variable)`).

### 21.9 The field numbers after IMF-5

The committed style pictures (`ImageFieldTests.TheCommittedStylePictures_AreScored`), against §20.6's:

| picture | kinds | ? | topo | dev | what moved |
|---|---|---|---|---|---|
| ds-regulator-1x / 2x | 4/7, 5/7 → **7/7** | 4, 3 → 1 | no → **yes** | — | filled diodes, R2 variable at both scales; the circuit is the answer's |
| ds-rf-amp-1x / 2x | 3/5, 4/5 → **5/5** | 3, 6 → 2, 5 | no | — | hairline wide-gap plates; C3 read with its label trimmed off |
| ds-gain-block-1x / 2x | 2/5 → 4/5 | 5, 14 → 5, 12 | no | — | hairline plates |
| ds-ldo-1x / 2x | 2/2 | 1 | yes | — | unchanged |
| heavy-iec | 6/8 → 7/8 | 5 → 1 | no | 0/3 → **3/3** | the BJT (NPN, pins by role), the P1Tone, the Vdc; C1's heavy plates |
| loop-coil-names | 1/2 → **2/2** | 0 | no | — | the looped coil, by its tap and its free lead |
| typeset | 2/5 | 1 → 0 | no | 0/1 → **1/1** | the ± circle is a Vdc |
| textbook-zigzag | 0/2 | 2 | no | — | its C's italic label is tucked inside the plates' span and joins both (IMF-7) |

The rest topology is still no for IMF-6's ports, IMF-7's type and IMF-8's IC boxes and triangles. On the downloaded
corpus: `picture-c` devices 0/3 → 3/3 and `?` 5 → 3; `picture-h` devices 0/2 → 1/2, `?` 11 → 10; `picture-ds-d1`/`d2` kinds 2/7, 0/7 →
4/7 both, `?` 5 → 3, 1; `picture-b` 1/4 → 2/4; `picture-e` 4/5 → 5/5; `picture-d`'s hatch is redrawn but its italic `R`
joins the region (IMF-7). One crop lost a part by kind, `picture-ds-b2` 3/4 → 2/4; not examined here.

**Not done here.** The gain block's triangle is still open at 1× and the test circuit's solid block box still two
regions; both skip reasons name IMF-5 and stay — they are the triangles' and boxes' phase (IMF-8) as much as this one.

## 22. IMF-6 — ports, terminals, return rails and names, as datasheets and textbooks draw them

IMF-5 named the parts; IMF-6 finds where the circuit is driven and measured. Everything here is in
`PictureTerminals` (the terminals, the port vocabulary, the names) and the emit's port pass.

### 22.1 Terminals and their names (R-imf6-1)

A **terminal** is a one-pin port symbol drawn as an open circle (`terminal-circle`) or a filled dot, standing on a
wire's free end. A filled dot reaches the reader two ways: as a one-pin region read as a disc
(`SchematicSymbols.FilledDot` — 3–8 w each way, filled out across its rows and columns it fills seven tenths of its
box and is the same turned half a turn, and its ink, with the run the wire reading took out of it given back, is
nearly all of that), whatever template it came nearest (a 1× dot matched the chassis ground); or, drawn small, as the
junction blob on a free end that no region reaches (`WireNode.Terminal`). A circle with two wires on it is no terminal.

A terminal takes the nearest name within 3 h, any side, unless the word stands nearer another part — `V_CC` beside its
supply circle is the circle's. The words of one line a word space apart are one name (`RF` `In`). **The port vocabulary
is a grammar** (`PortWords`): a word in a terminal's place, or at an open end, is read once more against the names a
port is called by — `IN`, `OUT`, `RF In`, `VIN`, `Vo`, `RF+DC`, `DC`, `RF`, `LO`, `IF`, `ANT` and the supply names —
each glyph allowed any character within 0.4 of its best (and 0.04 more) rather than ImageText's 0.2: `V1N` is `VIN`,
`K4` is `RF`, `Ωut` is `Out`. A net label at a wire end is snapped the same way. A supply name (`VCC`, `+Vs`, `V+`, a
voltage) that is no input or output makes the terminal a supply terminal — its net named, no port (IMF-8 draws it).

### 22.2 Pairs and the return rail (R-imf6-2, R-imf6-3)

Two terminals are **one port** when a voltage arrow spans them (each end within ¾ s of one), when they are stacked at
the drawing's left or right edge within ½ s across, or when one is on the return rail and the other is the nearest off
it within ½ s across or along. The upper — or, side by side, the left — is `+`; on a rail, the one off it. The port's
minus is the other terminal's net.

With **no ground drawn anywhere** (no ground symbol, no ground read from wire bars), the net holding the reference
terminals of two ports, or of one port and a source's minus, is ground. One ground symbol is placed at its left end —
the only thing the drawing gains — and the report says "the common return was taken as ground". **Not in the brief:**
when no net qualifies but a pair exists, the leftmost pair's reference is taken: a circuit with no ground cannot be
simulated, and the corpus's 359 px ladder (one pair, a load resistor at the far end) otherwise floated. A drawn ground
anywhere suppresses all of this: the pairs' references stay a net of their own.

### 22.3 Names at a distance (R-imf6-4)

An open end — a wire end nothing reaches, or a lead ending in paper (IMF-5's free lead) — takes a name in line with it
(its line within 1 h of the word), beyond its end within 6 h, when no symbol and no other wire is nearer the word. It
becomes a port carrying the name (`FarName`); a supply name stays a supply net. A free lead named so is a net of its own
(a root past the wire graph's), and the drawing joins the part's pin to the port's with a wire, as the lead does. Names
at a junction stay net labels.

### 22.4 Numbering (R-imf6-5)

AC sources first (P1Tone, O2), then — unless every port name carries a distinct number, which still orders them — an
input name, an output name, then the rest left to right, top to bottom. `OUT` drawn left of `IN` is port 2.

### 22.5 Parts of no known kind (R-imf6-6, O3)

A two-pin region of no known kind is a **0 Ω resistor** designated `?n` (a designator read off the picture is kept), no
variable, reported once with every `?n`; `SchematicImageCircuit.UnknownParts` lists them for Paste's outcome line
(IMF-9), and Paste already selects everything it adds. `SchematicImageOptions.UnknownAs = Capacitor` restores the
artwork's rule; it is recorded in the `ImageSource` block when set. The netlist reader takes `R:?1` as written, and a
run stamps it as the engine's near-short with its own one-line warning.

### 22.6 The drawing and the report (R-imf6-7, R-imf6-8)

Each port is placed at its terminal (the `+` of a pair) by its one pin, as before; a pair's minus is wired to its
reference terminal (`SchematicImageCircuit.PortReferences`), down to a rail below or across to one beside and then to
it, so the wire stays clear of the `+` terminal's. A name a port was made of is the port's name, not a label beside it.
The report counts ports by source — AC sources, port symbols, named terminals, terminal pairs, pairs a voltage arrow
spans, names beyond open ends, single terminals, net names — the rails taken as ground, and the parts of no known kind.

**Known:** `heavy-iec`'s terminal port is placed against its supply circle, which the exact placement then moves aside
(a placement-fallback note) — ports are placed in netlist order, first.

### 22.7 The field numbers after IMF-6

The committed style pictures, against §21.9's:

| picture | ports | topo | names | what moved |
|---|---|---|---|---|
| ds-ldo-1x / 2x | 2/2 | yes | **2/2** | VIN and VOUT named, in order |
| ds-rf-amp-1x / 2x | 0/2 → **2/2** | no | — | the filled dots, `RF In` / `RF Out` |
| ds-gain-block-1x | 1/2 → 0/2 | no | 1/1 | the `Vcc` terminal is a supply, no longer a port (the coax ports are IMF-8's) |
| heavy-iec | 0/1 → **1/1** | no → **yes** | — | the terminal dot; RL read `?` and the subscripted designators are left (IMF-7) |
| ladder-dashed | 3 → **2/2** | yes | — | the pairs, the rail as ground |
| low-res | 3 → **2/2** | no → **yes** | — | the arrow pairs |
| textbook-zigzag | 3 → **2/2** | no | — | the arrow pairs; its R and C still read `?` (IMF-7) |
| loop-coil-names | 0/3 → **3/3** | no → **yes** | 0/3 → **3/3** | names at a distance; **reads exactly** |

On the downloaded corpus: `picture-ds-c1`, `-h1`, `-h2` ports 1 → 2, the expected count (on `-h1` the second is the
switch's `ON` end, not `VIN`, whose word is read joined to its small circle — IMF-7), `picture-ds-e1`/`-e2` 0 → 1 of 2
(the supply dot, unnamed; the coax ports are IMF-8's), `picture-i` 0 → its 3 named ports, `picture-b` and `picture-f`
3 → 2 with their rails grounded.

## 23. IMF-7 — words in the type datasheets and textbooks use

IMF-6 found where a circuit is driven; IMF-7 reads what its labels say. The reader is still §11's stroke-glyph matcher,
in-house (O4): what changed is what it is shown, what it is matched against and what a reading may be.

### 23.1 Bold, thinned rather than refused (R-imf7-1)

A word heavier than a regular face (its stroke width over its cap height above `TextRegions.RegularWeight` = 0.15) is
**eroded by (weight − 0.15)·h/2 a side before it is skeletonised** (`TextRegions`), so a bold stem thins to a regular
one and a `=` reopens. The erosion is done four times finer than the picture (`ErodeScale`, nearest-neighbour, the exact
distance transform): a 3 px bold stem thinned by a pixel at its own resolution fell apart on every curve and diagonal. An
erosion that cuts a crumb off a piece (a hairline join eroded through) is retried lighter, three times, then the word is
read as drawn. `TextRegion.Thinned` says by how much. The refusal stays only above `MaxBoldWeight` = 0.30 after
thinning — where the survey shows thinning cannot recover the glyphs (Bold at 9 px caps, 3 px stems).

A thinned word is **also read against the faces** (§23.8) and the better reading kept: its centre line keeps the bold
face's wide `D` and open `4`, nearer the complex faces than the simplex font — at 2× the regulator's `D1` read `Ω1` and
`240Ω` read `2d0Ω` against the stroke font alone.

The survey (scratch harness, not a gate): each word drawn alone on a 500 × 140 picture with a wire above it, read by
`ImageText.Read`. The twelve words of §11.5 plus `VIN`, `GND`, `RFOUT`, `1.0 µF`, `240 Ω`, `1N4002` — eighteen.

| face | caps | right | wrong | unread | wrong / unread |
|---|---|---|---|---|---|
| DejaVu Sans | 9 | 17 | 1 | 0 | `1kΩ` → `1K1` |
| DejaVu Sans | 14 | 18 | 0 | 0 | |
| DejaVu Sans | 24 | 17 | 1 | 0 | `1kΩ` → nothing |
| DejaVu Sans Bold | 9 | 3 | 0 | 15 | 14 refused as heavy (0.33), `θ=45°` |
| DejaVu Sans Bold | 14 | 14 | 1 | 3 | `1kΩ`; unread `L=3.2mm W=0.3mm`, `RFIN`, `DNP` |
| DejaVu Sans Bold | 24 | 11 | 4 | 3 | `1kΩ` → `t1t`, `4R7` → `D R7`, `1.0 µF` → `1.0 UF`, the line set; unread `l0pF`, `0.5 pF`, `θ=45°` |
| Plex Sans | 9 | 11 | 6 | 1 | `IN` → `1n`, `RFIN` → `RMn`, `VIN` → `VfN`, `RFOUT` → `RFOU1`, `4R7` → `aR7`, `1kΩ` → `1k1` |
| Plex Sans | 14 | 18 | 0 | 0 | |
| Plex Sans | 24 | 16 | 1 | 1 | `l0pF` → `t0pF`; unread the line set |
| Plex Sans Bold | 9 | 7 | 5 | 6 | `l0pF`, `1kΩ`, `2n2`, `C12`, `RFOUT` wrong; `IN`, `VIN` refused as heavy |
| Plex Sans Bold | 14 | 14 | 3 | 1 | `2n2` → `Zl12`, `RFIN` → `9F 1m`, `VIN` → `V1m`; unread `1N4002` |
| Plex Sans Bold | 24 | 15 | 1 | 2 | `l0pF` → `0 F`; unread the line set, `θ=45°` |

Against §11.5 (every Bold word refused, 0 of 12 read): Bold at 14 and 24 px caps now reads 54 of 72, at a cost of 9
wrong readings — the wrong ones are where §11.5's were (`IN`/`RFIN`/`VIN` in Plex Bold, `1kΩ` everywhere). Words in a
picture are read with their neighbours, which a lone survey word does not have: on the style pictures a bold `VIN` is
held right by the known-word list (§23.2) and a bold designator by the part beside it.

### 23.2 The datasheet vocabulary (R-imf7-2)

`KnownWords` and its list (`resources/schematic-words/known-words.txt`, one word per line: pin and net names, the
not-fitted markers, the word designators) rank a known word above any other name (`WordReading.IsKnownWord`, rank 1 with
the port words). A known word **wins over the reading the glyphs gave** when it differs from it only where a glyph was
read as a character the matcher confuses with the word's (`KnownWords.Confusions`: `I 1 L l /`, `O 0 D Q G Ω o`,
`S 5 s`, `B 8 R`, `U V u v`, `Z 2`, and the small bowls `a e s o`), each such glyph still within
`MaxGlyphDistance` of it, at most half the word's glyphs so confused, the rest within `PortWords`' margin — `V1N` is
`VIN`, `QNO` is `GND`, a bold `ΩUT` is `OUT`. Over a word that has a reading, only confusion stands in for a glyph's
best: a clean `RF` is no `NF`. A word with no reading at all is searched once more with each glyph also offering its
confusions (a bold `1AI10OV` is `1A/100V`). Matching ignores case, spaces, hyphens and underscores; a snapped word
keeps each glyph's own case (`Cadj` stays `Cadj`).

**A glyph with a dot standing clear above its stem is an `i` or a `j`, never a digit** (`StrokeTextReader.Dotted`; a
normalised glyph's y runs up): the regulator's `Ci` read `C1` at 2×, the designator rule letting a digit stand after a
known prefix. At 1× (9 px caps, read at 3×) the dot is fused into the stem and `Ci` still reads `C1`.

### 23.3 Word designators (R-imf7-3, R-imf7-10)

`ValueGrammar.WordDesignates` gives the kinds a word may designate: a lone prefix letter (`R C L Q D V`), a prefix and
a letter or an n-expression (`Ra`, `RL`, `Cn`, `Ln-1`), a prefix and letters (`Cblock`, `Rbias`, `CIN`, `Ci`, `CADJ`,
`Ldrain`), an inductor's function word (`RFC`, `BEAD`, `FB`). A port or supply name is none (`RF`, `RFIN`, `LO`,
`Vcc`), and a capital prefix is required — a textbook's `v` is a quantity. Such a word is a designator **only for a part
of a matching kind within `LabelAssociation.WordDesignatorReach` = 3 text heights**, after every ordinary designator is
placed. The designator written is the word made legal (`LegalDesignator`: `Ln-1` → `Ln_1`) and unique (`_2`, `_3`),
noted on the part ("read as …") and listed once in the report; the parts table keeps the word as read.

A designator assigned to a part whose kind its prefix contradicts is read once more with its first glyph as the part's
prefix, when that glyph's best is confused with it (`LabelAssociation.Respelled`): a bold `D1` read `Q1` or `O1` beside a
diode, a serif italic `R₂` read `B2` beside a resistor. **Not in the brief:** a plain box read as a transmission line
whose designator says R is a resistor (`LabelAssociation.KindsByDesignator`, then the labels are associated again) —
typeset's IEC boxes are the same drawing as a TLIN's, and the designator is the only thing that tells them apart.

A lone letter cut off the symbol it touched — a textbook's `C` tucked against its plates — is tried as one glyph of the
text height from the pieces a quarter of h apart, cut from one component only (a hatched zig-zag's ticks are separate
components, and once read as a letter). A lone letter touching nothing (`R` beside its zig-zag) is taken before the
wires are traced, when it reads plainly as a prefix in any face: within 0.06 cap heights, every other character half
again as far, and glyph-shaped (≥ 0.3 as wide as tall, a sixth to six tenths of its box inked).

### 23.4 µ, Ω, the value block, ratings and value lists (R-imf7-4)

Two value variants (`hershey-value-variants.txt`): a sans `µ` (the simplex `u` with its left stem carried down — the
simplex µ is the Greek letter's italic shape, and a small bold sans µ read nearer a `p`), and a sans `/` (1.2 cap heights
at 17°; against the simplex one a rating's slash read `I`). The **label block** is read on datasheet layouts: a value on
the next line, left-aligned within ½ h or centred within ½ h, 0.3–1.2 h under (3.5 h left-aligned), or one right after it
on its line (`LabelAssociation.Under`/`After`). A value written **with its designator** (`CIN = 1µF`, beside the drawing)
is that part's wherever it stands (`ImageWord.For`); one for a designator no part has (`Vref = 1.25 V`) is an
annotation. A **rating** (`1A/100V`) is a `PartNumber` word: under or after a diode's, transistor's, IC's or source's
designator it is the part's number. **A value list** (`1.2V/1.8V/2.5V…`, three or more) and a parenthetical are
comments. A capacitance or inductance of exactly 0 is no part's value (a textbook's `v_out` read `0Uf`).

### 23.5 Annotation phrases (R-imf7-5)

Words of one line, or two lines stacked, that together parse as no designator, value, known word or single name are
joined into one `Comment` (`ImageText.JoinPhrases`). A comment goes to the nearest part within 10 h (never a ground) and
is written into its notes ("'…' is written beside it") — never a net name. The report counts them and how many were
attached. The rf-amp's two `DC Blocking Capacitor` notes are comments on C1 and C2 at both scales; their text reads
poorly (`DC B1acking E8Q8C1ZOF`) — a comment is kept as read, and nothing is built from it.

### 23.6 Serif feet (R-imf7-6)

`StrokeTextReader.TrimSerifs` removes every terminal branch no longer than 0.15 h leaving its stroke at more than 45°,
and carries a stem on to where its feet reached. A schematic's words are read both ways and the better kept. **Measured
on IMF-1's serif pictures, mean glyph distance with the trim / without:** typeset 0.0385 / 0.0386, textbook-zigzag
0.0580 / 0.0580, heavy-iec 0.0446 / 0.0446, ladder-dashed 0.2708 / 0.2708 — no reading changes. The faces (§23.8) already
drop their feet, so a trimmed skeleton and an untrimmed one land on the same template; the trim stays for the
bracketed serifs the corpus does not yet hold, and costs one more match per schematic word.

### 23.7 Slant per style (R-imf7-7)

`ImageText.Upright` groups the words into styles (cap heights within 1.3 of each other and weights within 0.05,
chained), measures each word's lean as the shear from −10° to 30° under which its glyphs' skeletons are narrowest, and
takes the style's glyph-weighted median and spread (1.4826 × the median deviation). A style of at least six glyphs
leaning 8–20° with a spread under 4° is sheared upright about each word's baseline. An italic word is read three ways —
in the stroke font as drawn, and in the faces sheared and as drawn — and the best kept: a subscript's digit leans less
than its style and reads worse sheared. An upright picture is untouched (no style on the datasheet pictures qualifies).

### 23.8 The serif and italic faces (R-imf7-8)

`resources/stroke-font/hershey-roman-complex.txt` and `hershey-italic-complex.txt` — the public-domain stroke drawings
of the same distribution as the simplex font, their licence in `LICENSE-hershey.txt` — load as further **variants** of
each character (`GlyphTemplates.Typeset`), in the simplex frame, so a glyph's match is its best over every face and a
word may mix them. The complex faces draw a thick stem as two or three strokes a unit apart and a serif as a short bar;
a picture's skeleton has one centre line, so a stroke within 1.5 units of a longer one is dropped, and a level bar of at
most 9.5 units on the cap line, x-height or baseline that meets another stroke (a foot). `λ` and `π` are not offered:
no grammar reads either, and offered they took a bold `=` (`Z=50Ω` read `Zπ50Ω`).

**The faces are offered to an italic style's words and to thinned bold words only.** Offered to every word they turned a
sans `F` into a serif `f` — the rf-amp's `RF In` port became `Rf In` — and doubled the reading time; neither a plot's
tick labels nor the silkscreen reader ever sees them (`GlyphTemplates.Text` and the silkscreen sets are unchanged, so
AS-10's gates hold as they were).

### 23.9 Subscripts and designator-then-value runs (R-imf7-9, R-imf7-11)

`StrokeGlyphs` joins a subscript to the glyph before it in a typeset word (0.25–0.85 of its height, starting at its right
side, its foot 0.1–0.6 of its height below the glyph's, its top well below); a whole run in the subscript's place
(`v_out`'s `out`) joins a lone base glyph. `StrokeTextReader.ReadSubscripted` reads the base and the subscript as two
runs, the subscript in the value class, and joins them: `R` + `1` → `R1`, `L` + `n−1` → `Ln-1`; what follows the
subscript's end on the base line is read as a value or, for a `Q`/`D`/`U`, a part number (`R₃10kΩ`, `Q₁2N2222`). A source
takes its value from the word after or under its designator (`V₁12V`) **as read or as its glyphs' bests spell it**: a
volt is no passive's value, so the beam reads an italic `12V` as `12U` (12 µ), whose `V` was the glyph's best.

### 23.10 Distances in h (R-imf7-12)

Every association reach in `LabelAssociation` is in the picture's text height when it is known
(`ReachInTextHeights` = 10, the reach circuitRF's own drawing needs for a label block six heights under its part — the
assignment, not the reach, decides between neighbours); word designators 3 h. With no h, AS-10's three diagonals.

### 23.11 The style pictures after IMF-7

| picture | desig | values | what moved |
|---|---|---|---|
| ds-regulator-2x | **7/7** | **7/7** | bold read; `Ci`, `Co`, `Cadj` word designators; diodes' ratings |
| ds-regulator-1x | 6/7 | 6/7 | as 2×, but `Ci` reads `C1` (its dot fused into its stem) |
| ds-ldo-1x / 2x | **2/2** | **2/2** | `CIN`, `COUT` and `CIN = 1µF`; **reads exactly** |
| ds-rf-amp-2x | 4/5 | 3/5 | pin names `VCC RFIN RFOUT GND NC` inside the box; the notes are comments; C4 reads `Cn`, C2's `100pF` loses a glyph |
| ds-rf-amp-1x | 4/5 | 3/5 | the notes are comments; `RFIN`/`RFOUT` (6 px caps) not read |
| typeset | **6/6** | 4/5 + V1 | every designator, `V₁12V` applied; C1's `100pF`, set across its plates, reads `10116r`; **reads exactly** |
| textbook-zigzag | **2/2** | — | the lone `R` and the tucked `C`; **reads exactly** |
| heavy-iec | 7/9 | — | `R₄` reads `Rd`, `Q₁` unread |
| ladder-dashed | 4/6 | — | `L₂`, `L₃` lifted off their coils come apart into pieces |
| low-res | 0/2 | — | the 7 px serif italic `R` refused as heavy, `C` unread |

Turned on: `Ldo_1x`, `Ldo_2x`, `TypesetFigure`, `TextbookZigzag`; and the IMF-7 facts in
`ImageDatasheetStyleTests`/`ImageWebStyleTests`. The skips left say what was measured short.

**Measured short, in the DejaVu Serif Italic test face** (`ImageTextTests`, skipped stating what is read): `C_n` reads
`C9` (the italic n subscript nearer a 9), `v_out` reads `UOUZ` (an italic v reads U, and no grammar says which),
`R₃10kΩ` reads `R3` then `1Ω` (the italic k and 0 touch and are lost), `Q₁2N2222` reads `Q`, `2N2222` (the subscript is
not found under the Q's tail). The style picture's own `R₃10kΩ`, in its face, reads.

Gate: `ImageTextTests`, the two style classes, and the image/glyph regression set — `ImageAcceptance`, `Imaging`,
`Recognition` (with `StrokeGlyphTests` and the AS-10 silkscreen gates), `StrokeTextRoundTrip`, the recognise CLI verb
and the MCP image tool, 386 tests.

On the downloaded corpus (`ImageFieldTests`, 26 pass), designators and values read where the picture states them:
`ds-a1`/`-a2` 5/6, `ds-b1` 2/4 and `-b2` 3/4, `ds-d1` 2/4 (values 1/3) and `-d2` 4/4 (3/3), `ds-h1` 2/2 (0/2) and `-h2` 1/2
(1/2), `picture-c` 5/8, `picture-b` 1/2, `picture-h` values 2/7. Reading time is over O8's 10 s on `ds-g1` (18 s) and
`ds-g2` (12 s); where that time goes is not measured here (IMF-9's speed target).

## 24. IMF-8 — the part a datasheet's drawing is for

Scoped down by the owner mid-phase: the IC box, the amplifier triangle, coax connectors and pin markers are read; a
drawing none of them fits falls back to what IM-8 always did (a multi-pin S-parameter block, a `?` part), which is the
graceful failure. The System block symbols (R-imf8-7) followed afterwards, below. Not done: a `Vdc` on a supply arrow that states a voltage
(R-imf8-5), a not-fitted part marked by a second colour (R-imf8-6; the `DNP`/`NC` word rule IM-10 already had stands),
and a solid box drawn round a part read as a frame (the test circuit's DUT box).

- **Triangle corners close** (`WireGraph.AcuteCorners`): where two sides meet at a sharp angle in a thin pen the skeleton
  breaks into short non-candidate runs, so the triangle's cycle stayed open and its sides were wire. A chain of such runs
  no longer than 3 w, joining two candidates not in line (one slanting) or two slanting ones in line (a side a lead's T
  cuts), is a `Side`: cycle search only, never wire. The gain block's triangle is now one region at 1× and 2×.
- **Amplifier** (`DatasheetSymbols.Triangle`/`Amplifier`): the region's ink hull has three corners and ink along each
  side; the apex is the corner a lead leaves. One lead at the apex and one on the side facing it is the System `Amp`
  (`in+`, `out+`, `in−`/`out−` on ground, `Gain` a tunable global at 20 dB); leads on the other sides go to the reference
  or end at the body. Two inputs or outputs make it an IC.
- **IC box** (`DatasheetSymbols.IcBox`, `IcPins`): three or more leads round a closed orthogonal outline. Pins are taken in
  package order (down the left, along the bottom, up the right, back along the top); pin numbers read beside the pins
  replace that order only when every signal pin has a distinct one. Grounded or GND-named pins are the reference; a pin
  wired to nothing else, or named NC, is left out. Port names go to `SchematicImageCircuit.Ics` and the report (an SnP
  carries none). Parts-table kinds `IC`, `Amp` and `Block` (variant = the System block) added.
- **Coax connector**: a ring 4 w–2 s across with a filled dot (≥ 0.6 of its box — a `+` sign fills under half) and
  nothing else inside; a terminal, its shell's lead the port's minus. A short wire whose two ends stop on one region is
  that region's outline (a ring's flat run read as wire).
- **Pin marker**: a round ring 2.5 w–0.8 s with two wire ends meeting it from opposite sides is joined through as a hop is
  (`HopJoin.Marker`), so the lead is one wire.

- **Block symbols** (R-imf8-7), each the System component with default parameters, reported "set them"; a drawing none
  fits stays the S-parameter block or `?` it was. Read by shape in `DatasheetSymbols`, before the IC box: a **filter** is
  a box with two leads on opposite sides and one to three waves inside (a column holding two runs of ink is a strike;
  top = highest frequency, so struck at the top is `Form=Lowpass`, at the bottom `Highpass`, both ends `Bandpass`; a
  struck middle is a band-stop, reported and left at the default); a **coupler** a box with 3–4 leads and a straight
  line across it joining two on opposite sides (input left or top, coupled = the other lead nearer the input; a third
  lead missing leaves the isolated port open, reported); a **circulator** a ring with three leads and an arc inside clear
  of its centre (a mixer's X crosses it and stays an SnP), ports numbered from the leftmost lead the way the arc's
  heavier end turns. A **switch** is gathered in the reading (`SymbolRegions.Switches`, before the pin markers and the
  arrow tails, both of which would otherwise take its parts): a slanting wire with each end on a small open ring, or one
  end on a ring and the other free pointing at one, is the blade; the throws are the rings as far from the common contact
  as the blade's, within 75°; `Throws` and `State` come from them. An **attenuator** (a two-pin box holding `dB`/`d8`,
  `ATT`, `PAD`) and a **balun** (an IC box holding `BALUN` with one signal pin facing two, or a transformer with `BALUN`
  beside it and one coil grounded at one end) are decided at emit, where the words are read. Gate:
  `ImageDatasheetStyleTests.BlockSymbol_IsItsSystemComponent`, in memory at 1×.

Committed fixtures after the phase: rf-amp 1× topology right (designators 4/5), the gain block's Amp and coax ports at
both scales, LDO still exact; regulator, test circuit and gain block stay skipped with measured reasons in
`ImageStylePictureTests.cs`.

## 25. IMF-9 — settings chosen per picture, "not a schematic", and what Paste says

Paste asks nothing (L1), so the reader now chooses for itself: it reads a few ways and keeps the most plausible
(R-imf9-2), refuses what is no schematic in one sentence (R-imf9-3), and Paste says in one line what it did
(R-imf9-4), cancellable with Esc (R-imf9-6). All of it is inside `SchematicImageRecognition.Read`, so the dialog's
reading, `recognize` (`--profile auto`, the default) and MCP choose exactly as Paste does (R-imf9-7, L5).

### 25.1 Plausibility (`ReadingPlausibility`)

Five terms in [0, 1]: **ink explained** (`SchematicImageRead.ExplainedInk / InkPixels` — ink inside a glyph box, a
symbol region or its fills, a supply mark, an erased annotation, or within ½ w + 1.5 px of a wire), **parts named**
(recognised, or multi-pin of a known kind — an IC box, a triangle, a block; a multi-pin region of no kind counts for
nothing), **pins honest** (a two-pin part with two wired pins, no more leads than its template has, an unnamed
multi-pin region at most 4), **words read** (mean of word regions read and designators found over the parts that carry
one; ½ when there are no words), **graph sane** (1 − (two-pin parts shorted by their own pins + nets with fewer than two
pins that no label, supply mark, terminal or port names) / (two-pin parts + nets)).

**The fit.** Non-negative least squares of FieldScore's correctness (the mean of the stated tallies and topology) on the
terms, over the 16 committed schematic fixtures' readings, every candidate reading of them, and the illustration read
as a schematic (correctness 0) — 34 rows — with a ridge pull (λ = 1) toward equal weights, normalised to sum 1. Without
the pull the fit puts 0.94 on parts named and nothing on three terms (r = 0.88); with it: **ink 0.10, parts 0.62, pins
0.00, words 0.09, nets 0.19** (r = 0.87). Pins honest fits to zero because, on these readings, every region with too
many leads is also a region of no known kind, which parts named already counts; it stays a term and is reported.

| picture | reading | ink | parts | pins | words | nets | right (FieldScore) | score |
|---|---|---|---|---|---|---|---|---|
| ds-gain-block-1x | measured | 0.98 | 1.00 | 1.00 | 0.74 | 0.93 | 0.77 | 0.96 |
| ds-gain-block-1x | enlarged | 0.96 | 0.80 | 1.00 | 0.73 | 0.94 | 0.51 | 0.84 |
| ds-gain-block-2x | measured | 0.99 | 0.63 | 1.00 | 0.57 | 0.96 | 0.74 | 0.72 |
| ds-gain-block-2x | bold | 0.99 | 0.63 | 1.00 | 0.54 | 0.96 | 0.67 | 0.72 |
| ds-ldo-1x | measured | 1.00 | 1.00 | 1.00 | 0.79 | 1.00 | 1.00 | 0.98 |
| ds-ldo-1x | enlarged | 1.00 | 1.00 | 1.00 | 0.79 | 1.00 | 1.00 | 0.98 |
| ds-ldo-2x | measured | 1.00 | 1.00 | 1.00 | 0.75 | 1.00 | 1.00 | 0.98 |
| ds-regulator-1x | measured | 0.99 | 1.00 | 1.00 | 1.00 | 1.00 | 0.81 | 1.00 |
| ds-regulator-1x | enlarged | 0.99 | 0.92 | 1.00 | 0.89 | 1.00 | 0.76 | 0.94 |
| ds-regulator-1x | bold | 0.99 | 1.00 | 1.00 | 0.02 | 1.00 | 0.83 | 0.91 |
| ds-regulator-2x | measured | 1.00 | 1.00 | 1.00 | 1.00 | 1.00 | 0.83 | 1.00 |
| ds-regulator-2x | bold | 1.00 | 1.00 | 1.00 | 0.00 | 1.00 | 0.83 | 0.91 |
| ds-rf-amp-1x | measured | 0.99 | 0.92 | 1.00 | 0.81 | 0.88 | 0.97 | 0.91 |
| ds-rf-amp-1x | bold | 0.99 | 0.92 | 1.00 | 0.81 | 0.88 | 0.97 | 0.91 |
| ds-rf-amp-1x | enlarged | 0.99 | 0.83 | 0.92 | 0.86 | 0.93 | 0.51 | 0.87 |
| ds-rf-amp-2x | measured | 1.00 | 0.80 | 1.00 | 0.89 | 0.94 | 0.83 | 0.85 |
| ds-rf-amp-2x | bold | 1.00 | 0.80 | 1.00 | 0.88 | 0.94 | 0.83 | 0.85 |
| ds-test-circuit-1x | enlarged | 1.00 | 0.78 | 1.00 | 0.84 | 1.00 | 0.52 | 0.85 |
| ds-test-circuit-1x | measured | 0.88 | 0.78 | 1.00 | 0.81 | 1.00 | 0.52 | 0.83 |
| ds-test-circuit-1x | bold | 0.88 | 0.78 | 1.00 | 0.81 | 1.00 | 0.52 | 0.83 |
| ds-test-circuit-2x | measured | 1.00 | 0.78 | 1.00 | 0.83 | 1.00 | 0.52 | 0.85 |
| heavy-iec | measured | 0.99 | 1.00 | 1.00 | 0.91 | 1.00 | 0.96 | 0.99 |
| heavy-iec | bold | 0.99 | 1.00 | 1.00 | 0.82 | 1.00 | 0.96 | 0.98 |
| illustration | as a schematic | 0.94 | 0.00 | 1.00 | 0.50 | 0.29 | 0.00 | 0.19 |
| ladder-dashed | measured | 0.99 | 1.00 | 1.00 | 0.67 | 1.00 | 0.92 | 0.97 |
| loop-coil-names | measured | 1.00 | 1.00 | 0.50 | 0.50 | 0.67 | 1.00 | 0.89 |
| low-res | enlarged | 0.99 | 1.00 | 1.00 | 0.42 | 1.00 | 0.88 | 0.95 |
| low-res | measured | 0.99 | 1.00 | 1.00 | 0.00 | 1.00 | 0.75 | 0.91 |
| low-res | bold | 0.99 | 1.00 | 1.00 | 0.00 | 1.00 | 0.75 | 0.91 |
| textbook-zigzag | measured | 0.99 | 1.00 | 1.00 | 1.00 | 1.00 | 1.00 | 1.00 |
| textbook-zigzag | bold | 0.99 | 1.00 | 1.00 | 0.88 | 1.00 | 1.00 | 0.99 |
| typeset | measured | 1.00 | 1.00 | 1.00 | 1.00 | 1.00 | 1.00 | 1.00 |
| typeset | enlarged | 1.00 | 0.88 | 1.00 | 0.95 | 0.90 | 0.76 | 0.90 |

**The floor is 0.30.** Every committed schematic scores ≥ 0.72; the illustration read as a schematic scores 0.19. The
floor sits low in that gap because two downloaded web schematics read poorly score 0.41 and 0.43 — a poor reading of a
real schematic is the dialog's to correct, not a refusal. It applies only when the kind was read (Paste, the dialog's
Auto), never when the user stated *Schematic*.

**A runaway region** (the pre-IMF-4 failure) lowers the score but cannot take a drawing that is otherwise read below
the floor: merging ds-ldo-2x's parts into one 20-lead region of no kind scores 0.89 against the correct 0.98. That is
what candidate choice needs (the runaway reading loses), and `ReadingPlausibilityTests` holds that, not the floor. It
is built in the test from the real reading rather than recorded as JSON: the score reads only the result's own lists,
and serialising the whole result graph would be a format kept for one fact.

### 25.2 Candidates and the budget

`SchematicImageReadings`: **measured** (always, first, with the caller's control); **enlarged** — a Low picture one
resample step up (`SchematicImageOptions.ResampleBoost`, capped at IMF-2's 4 MP); **crossings** — every four-way
crossing joined, only when the reading has no junction dot, a crossing read apart for want of one, and ≥ 2
T-junctions; **bold** — `NormaliseBoldType` off, only when a word was thinned. Each is run only when it can differ
(`Others`), and only within a **pixel budget** (`WithinBudget`, the R-imf9-5 counter): every reading of a simple
diagram together at most 2.5 MP, of a complex one 5 MP and only while the clock allows another reading as long as the
measured one within 10 s. Simple = ≤ 20 symbol regions and ≤ 2,500 px on the longer side as read. Ties keep the
measured reading. Candidates are only run with the profile measured and no corrections; the dialog's pick
(`SchematicImageInput.Reading`) reads that one alone, and so does `--profile <name>`.

Over every committed fixture and every downloaded picture, within the budget a candidate is chosen twice — `low-res`
and `picture-e`, both enlarged (FieldScore 0.75 → 0.88 on `low-res`). Without the budget it was also chosen on
`ds-test-circuit-1x` and `picture-ds-e1` (enlarged, FieldScore unchanged) at two to three times the time, which is why
the budget is pixels. A candidate FieldScore rates worse was never chosen. **bold** was never chosen; it is kept because the brief names it and a
heavy-type picture the corpus lacks may want it.

### 25.3 Time (owner's Debug build, scratch harness, one reading vs Auto)

The budget is held by counters; these are the times, measured once. The O8 targets are **not met by the reader
itself**: one reading of a 1× datasheet crop, enlarged by IMF-2, already takes 1–5 s in Debug (2–3.5 s a megapixel),
and a 60-region evaluation board 18 s. Auto adds at most a second reading's worth where the budget lets one run.

| picture | one | Auto | readings |
|---|---|---|---|
| ds-ldo-2x / ds-ldo-1x | 0.7 / 1.2 s | 0.7 / 1.2 s | 1 / 1 |
| ds-gain-block-2x / 1x | 1.4 / 2.3 s | 2.5 / 1.8 s | 2 / 1 |
| ds-regulator-2x / 1x | 2.5 / 4.8 s | 3.7 / 4.8 s | 2 / 1 |
| ds-rf-amp-2x / 1x | 2.2 / 3.5 s | 3.1 / 3.5 s | 2 / 1 |
| heavy-iec | 1.9 s | 3.4 s | 2 |
| typeset | 4.5 s | 4.5 s | 1 |
| low-res | 1.8 s | 3.3 s | 3 |
| picture-ds-d2 | 3.8 s | 6.3 s | 2 |
| picture-ds-f1 (complex) | 8.6 s | 8.4 s | 1 |
| picture-ds-g1 (complex) | 18.4 s | 18.3 s | 1 |
| illustration, picture-g | 0.2 s | 0.2 s | 0 (kind) |

The kind check runs first and alone: 20–190 ms on every picture here (R-imf9-5's 0.5 s).

### 25.4 Not a schematic

`ImageKind` gained a **shaded illustration** rule (none): ≥ 2 chromatic inks, filled share ≥ 0.5, orthogonal share
< 0.2 and a skeleton no wider than 6 px. The last is what keeps a layout a layout: filled copper skeletonises at its
own width (15–49 px on the gates' layout pictures, whose filled rectangles skeletonise to diagonals and failed the
first version of the rule); an illustration's outlines at 1–3 px. IMF-1's illustration and the downloaded shaded
picture are refused by the kind check alone (zero readings). Paste still reads a picture classified *layout* as a
schematic — forced away from layout, never past none.

### 25.5 Paste

One live row in Messages — "Paste Image as Schematic: reading the picture… (Esc to cancel)", its stages from the
`RunControl` — bound to the paste's `RunCancellation`. Esc in the window (tunnelling, first), the row's right-click
Cancel and closing the target document all cancel through that one object; both Paste commands are disabled while one
is in flight. The reader checks the token at every stage, once a thinning pass (`Skeleton.Thin`) and before each region
the matcher names, and returns `Cancelled` rather than throwing. A cancelled paste keeps no picture (it is written only
after the read), adds no shape and no undo step; the row ends "Paste cancelled.". Otherwise the row becomes the one
outcome line (`SchematicPasteOutcome`): counts, then the parts needing a look — `?n`, an amplifier on default gain, an
IC or block on default parameters, a part whose value became a variable — which are what the paste selects, and a
**Correct…** action opening the dialog seeded with the same reading (`ImageSourceViewModel.SeedSchematic`, no read
again). Paste Image as Layout has the same row, Esc and cancel.

Gates: `ReadingPlausibilityTests`, `ReadCancellationTests`, `ImageNothingHereTests` (illustration added; the kind check
alone, zero readings), `PasteOutcomeTests`, `PasteCancelTests`, and `ImageDatasheetStyleTests`/`ImageWebStyleTests`
through Auto.

## 26. IMF-10 — the field numbers against the overview's §4

The last phase measures; it changes no reader behaviour. Three things changed in the measuring itself:

- **The scorer reads as Paste reads.** `FieldScore.Read` forced every picture's kind to a schematic, which also forced a
  picture the kind check calls *none* past it — so the table read the shaded illustration and `picture-g` that Paste
  refuses. It now hands the reader no kind, as Paste does: a layout-looking picture is still read as a schematic, one
  that is no drawing is refused, and the floor applies. Two columns were added: `reading`, the candidate kept (§25.2),
  and `plaus`, its plausibility.
- **The field pictures have answers.** Each local datasheet circuit but the stress picture `ds-g`, and each web
  schematic, now carries an `answer.cnl` beside its `expected.json` (git-ignored with the pictures), so `topo` is
  scored on them too. The answers follow the committed fixtures' conventions: a terminal, a coax connector or a name
  beyond an open wire end is a port to ground, unless the name is a supply's (`PortWords.IsSupply`); an IC is an SnP on
  its signal pins with ground its reference; an amplifier triangle is `Amp in 0 out 0`. `ds-d`'s `kinds` said `D`,
  which the netlist never writes (`Diode`), so its diodes could not be counted; and `ds-c` gained its designators,
  `ds-d` and `ds-f` their ports.
- **One committed answer was the defect.** `ds-regulator` drew VIN and VOUT as names beyond open wire ends, which IMF-6
  reads as ports — as `loop-coil-names`' answer already says — while its answer had no port. With the two ports added,
  `ds-regulator-2x` reads exactly and its fact is turned on; 1× is short one designator.

`ImageFieldTests` holds each field picture where its reading stands against what §4 promises it (R-imf10-2): kinds,
ports and the amplifier or IC no fewer right, a topology read stays read, no region over the largest drawn part's pins
(the reading's own on web a, d and h, which miss it), `picture-g` refused. `PasteDatasheetEndToEndTests` (R-imf10-3)
pastes the gain block at the view model into an open schematic holding a part: nothing asked, one line, the Amp
between two ports, nothing joined to what was there, the picture under it, and the document run as Simulate runs it —
S21 = 20.00 dB at 1 GHz at 1×. At 2× the same paste does not simulate (below), and that case is skipped with the
reason.

### 26.1 The tables (2026-10-11, Debug build)

Committed style pictures:

```
picture                  auto       read     kinds  ?   topo ports gnd   term  dev   block desig values names region     ms  reading    plaus
ds-gain-block-1x         schematic  read     4/5    0   no   2/2   4/4   -     1/1   1/1   3/5   1/2    1/1   3        1734  measured   0.96
ds-gain-block-2x         schematic  read     4/5    7   no   2/2   4/4   -     1/1   1/1   2/5   0/2    1/1   3        2507  measured   0.72
ds-ldo-1x                schematic  read     2/2    0   yes  2/2   3/3   2/2   1/1   1/1   2/2   2/2    2/2   4        1198  measured   0.98
ds-ldo-2x                schematic  read     2/2    0   yes  2/2   3/3   2/2   1/1   1/1   2/2   2/2    2/2   4         696  measured   0.98
ds-regulator-1x          schematic  read     7/7    0   yes  2/2   4/4   -     1/1   1/1   6/7   6/7    2/2   3        4787  measured   1.00
ds-regulator-2x          schematic  read     7/7    0   yes  2/2   4/4   -     1/1   1/1   7/7   7/7    2/2   3        3685  measured   1.00
ds-rf-amp-1x             schematic  read     5/5    1   yes  2/2   4/4   -     1/1   1/1   4/5   3/5    0/1   8        3518  measured   0.91
ds-rf-amp-2x             schematic  read     5/5    3   no   2/2   4/4   -     1/1   1/1   4/5   3/5    0/1   8        3184  measured   0.85
ds-test-circuit-1x       schematic  read     3/3    2   no   2/2   2/3   -     0/1   0/1   3/3   3/3    0/1   2        1968  measured   0.83
ds-test-circuit-2x       schematic  read     3/3    2   no   2/2   2/3   -     0/1   0/1   3/3   3/3    0/1   2        1082  measured   0.85
heavy-iec                schematic  read     8/8    0   yes  1/1   6/6   -     3/3   -     7/9   -      -     3        3457  measured   0.99
illustration             none!      refused  -      0   -    -     -     -     -     -     -     -      -     0         170  -          -
ladder-dashed            schematic  read     6/6    0   yes  2/2   -     4/4   -     -     4/6   -      -     2         955  measured   0.97
loop-coil-names          schematic  read     2/2    0   yes  3/3   -     -     -     -     -     -      3/3   2         323  measured   0.89
low-res                  schematic  read     2/2    0   yes  2/2   -     4/4   -     -     1/2   -      -     2        3256  enlarged   0.95
textbook-zigzag          schematic  read     2/2    0   yes  2/2   -     4/4   -     -     2/2   -      -     2        1901  measured   1.00
typeset                  schematic  read     5/5    0   yes  -     1/1   -     1/1   -     6/6   4/5    -     2        4534  measured   1.00
```

Field pictures (local, git-ignored; datasheet crops first):

```
picture                  auto       read     kinds  ?   topo ports gnd   term  dev   block desig values names region     ms  reading    plaus
picture-ds-a1            schematic  read     3/6    6   no   0/2   -     -     1/1   1/1   5/6   -      0/1   5        5108  measured   0.68
picture-ds-a2            schematic  read     3/6    8   no   0/2   -     -     0/1   1/1   5/6   -      0/1   4        2754  measured   0.70
picture-ds-b1            schematic  read     2/4    3   no   1/2   -     -     1/1   1/1   2/4   -      -     3        2040  measured   0.67
picture-ds-b2            schematic  read     2/4    3   no   0/2   -     -     0/1   0/1   3/4   -      -     3        1419  measured   0.68
picture-ds-c1            schematic  read     3/5    3   no   2/2   -     -     1/1   1/1   1/5   -      0/1   4         768  measured   0.70
picture-ds-c2            schematic  read     3/5    4   no   2/2   -     -     0/1   1/1   3/5   -      0/1   4        1041  measured   0.76
picture-ds-d1            schematic  read     6/7    1   no   2/2   1/1   -     1/1   1/1   2/4   1/3    -     3        5923  measured   0.92
picture-ds-d2            schematic  read     6/7    0   no   1/2   0/1   -     0/1   0/1   4/4   3/3    -     2        6516  measured   0.93
picture-ds-e1            schematic  read     0/3    1   no   0/2   -     -     0/1   0/1   -     -      -     2        2417  measured   0.60
picture-ds-e2            schematic  read     0/3    3   no   0/2   -     -     0/1   0/1   -     -      -     4        1514  measured   0.54
picture-ds-f1            schematic  read     5/18   28  no   1/1   -     -     0/2   1/2   -     -      -     4        9137  measured   0.43
picture-ds-f2            schematic  read     11/18  29  no   0/1   -     -     1/2   1/2   -     -      -     4        7438  measured   0.52
picture-ds-g1            schematic  read     -      25  -    -     -     -     1/4   -     -     -      -     16      19110  measured   0.71
picture-ds-g2            schematic  read     -      42  -    -     -     -     1/4   -     -     -      -     16      12900  measured   0.70
picture-ds-h1            schematic  read     2/2    0   no   2/2   3/3   -     1/1   0/1   2/2   0/2    1/2   4        2104  measured   0.97
picture-ds-h2            schematic  read     2/2    0   no   2/2   2/3   -     1/1   0/1   1/2   1/2    1/2   4        1287  measured   0.96
picture-a                schematic  read     3/8    14  no   -     4/4   6/6   -     -     -     -      -     4        1138  measured   0.66
picture-b                schematic  read     3/4    1   yes  -     -     -     -     -     1/2   -      -     2        1023  measured   0.89
picture-c                schematic  read     7/8    3   no   -     -     -     3/3   -     5/8   -      -     3        1388  measured   0.84
picture-d                schematic  read     0/2    9   no   -     0/1   -     -     -     -     -      0/2   4        1225  measured   0.41
picture-e                schematic  read     4/5    0   no   -     -     -     -     -     -     -      -     2        3145  enlarged   0.97
picture-f                schematic  read     6/6    1   no   -     -     4/4   -     -     -     -      -     2        1874  measured   0.94
picture-g                none!      refused  -      0   -    -     -     -     -     -     -     -      -     0         235  -          -
picture-h                schematic  read     6/7    9   no   -     -     -     1/2   -     -     2/7    -     4        1209  measured   0.64
picture-i                schematic  read     1/2    1   no   -     -     -     -     -     -     -      3/3   2         556  measured   0.57
```

### 26.2 Against §4, target by target

- **Committed fixtures read exactly — 6 of 16 met** (`ds-ldo` both, `ds-regulator-2x`, `textbook-zigzag`, `typeset`,
  `loop-coil-names`). The ten misses, each from its skip reason in `ImageStylePictureTests.cs`: `ds-gain-block` — at 1×
  `Cbypass` reads as a supply bar, so the bias node is ground; at 2× the RFC coil comes apart into five `?` parts and
  the junction it meets reads as a 3-port SnP. `ds-regulator-1x` — `Ci` reads `C1`, its i's dot fused at the screen
  pen. `ds-rf-amp` — `C3`'s designator, touching its plate, is not read; at 2× also the hop where L1's lead crosses the
  IC's VCC lead reads as a `?` part, which puts the VCC pin on a net of its own. `ds-test-circuit` — the solid DUT box
  drawn round the triangle is read as one part with it, so no Amp. `heavy-iec` — R₄'s subscript reads `d`, Q₁ is not
  read. `ladder-dashed` — L₂ and L₃, lifted off their coils, come apart. `low-res` — the 7 px serif italic R is refused
  as heavy.
- **Every coloured datasheet picture classified as a schematic — met.** All sixteen crops `auto` = schematic.
- **At least 5 of the 7 datasheet circuits at both scales with every R, L, C, D by kind, the topology, the RF ports and
  the amplifier or IC as one part — 0 of 7.** The nearest are `ds-h` (kinds and ports right, IC wrong) and `ds-d1`
  (6 of 7 kinds, ports and IC right). By circuit:
  - `ds-a` (RF amplifier): the IC box is divided by its pin-number boxes into several blocks (one read as a coupler);
    the hairline RF choke comes apart into five pieces; the side-by-side C3/C4 pair is one region; the filled-dot RF
    connectors are `?`, so no port.
  - `ds-b` (gain block): at 2× the triangle's region takes C1 in and is no Amp, and C3 with its ground is read as a
    word; the coax connectors are `?` at 2× (one of two at 1×).
  - `ds-c` (gain block): the RFC coil and the Rbias zig-zag beside its junction dot are `?`; at 2× the pin markers
    above and below the triangle stay in its region and it is no Amp. Ports right at both.
  - `ds-d` (regulator): at 1× D2 is not read and the terminal names misread (V_I as `U1`), the IC right; at 2× words
    misread as net names join the rails — the ground under the return rail reads as the word `I11` and names it, V_O
    reads `VD` — so every part lands between the same two nets.
  - `ds-e` (test circuit): each solid module box (the blocking capacitor's, the bias tee's) is one block with what it
    holds, the triangle inside the dashed DUT frame is read as annotation, the coax connectors are `?` — IMF-8's
    unmet frame rule.
  - `ds-f` (op-amp, stress-like): 28–29 regions of no known kind; the op-amp's feedback and output network do not read.
  - `ds-h` (LDO): the `VEN` pin's wire, which ends at the switch drawing, is kept as an IC port; the switch's `ON`
    is taken for a port name; `VIN` reads `V1N` and its terminal is no port; the GND pin's ground reads as a word.
- **Three quarters of datasheet designators read right — 28 of 42 (67 %).** Met on a1, a2, b2, d2, h1; not on b1
  (2/4), c1 (1/5), c2 (3/5), d1 (2/4), h2 (1/2). `ds-c`'s word designators touching their symbols, with parenthetical
  notes beside them, are most of the loss.
- **Every value on ds-d read — 3/3 at 2×, 1/3 at 1×.**
- **Web: `picture-g` refused in one sentence — met** ("This picture does not look like a schematic: a shaded
  illustration…", by the kind check alone). **6 of 8 with kinds and topology — 1 of 8** (`b`, whose zig-zag is `?` but
  emitted 0 Ω in its place). `a`: the heavy serif zig-zags come apart at their corners (14 `?`). `c`: the transistor in
  its circle reads as a FET, three wire pieces as `?`, and the source's return is not ground. `d`: the hatched zig-zag
  is `?`, and C with its ground is one 4-pin block. `e`: C₁ under R_S is not read. `f`: kinds right, but the dashed
  continuation between L₄ and L_n−1 reads as a `?` part joining them. `h`: the transistor reads as a 4-pin block, the
  coil in pieces. `i`: the looped coil is `?`. Half the designators: not met where stated (`c` 5/8, `b` 1/2).
- **No region with more pins than the drawn part — met on every datasheet picture and committed fixture; missed on
  web `a`, `d`, `h`** (4-pin regions where a two-pin part's pieces or a transistor and its neighbour merged).
- **Paste time — not met** (§25.3): 0.3–6.5 s for the simple pictures in Debug, 7–19 s for `ds-f` and `ds-g`. The
  reader itself costs 2–3.5 s a megapixel in Debug; Auto adds no reading a simple picture's pixel budget does not allow.
  Esc cancels at any point (`PasteCancelTests`).
- **No regression — met:** the image gates below are green.

**What it adds up to.** The series moved the datasheet crops from no passive read by kind to half or more of them on
every circuit but `ds-e` and `ds-f`, no runaway region, colour classified, and the amplifier or IC one part on four of
the seven circuits at 1× — but not to pasted circuits that simulate as drawn. The misses cluster in four places, each a reader phase of its own: hairline
coils that come apart; boxes drawn round parts (module and DUT frames, pin-number boxes); words read as net names
that join nets; and coax and filled-dot connectors. The user page says so (`create-from-image.md` §pasting).
