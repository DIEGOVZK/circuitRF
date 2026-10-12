# Schematic pictures read by trained models

**Series:** `docs/sonnet-briefs/brief-iml-0-overview.md` (decisions L1–L11, owner decisions O1–O5) · **Code:**
`tools/schematic-ml/` (the training kit), later `tools/SchematicSynth` (the generators), `src/Vision` (inference) and
`src/Design/Schematic/Recognition/` (the reader) · **Handbook:** `tools/schematic-ml/README.md` · **Builds on:** Create
from Image (`docs/design/image-to-circuit.md`), whose back half (emit, drawing, paste, dialog, `recognize`) stays.

This note says what the system is, what each of its contracts is, and why each is shaped as it is. IML-1 wrote §1–§3, IML-2 §4;
each later phase adds its own section.

---

## 1. What it is

Two trained networks replace the rule-based front half of the schematic picture reader, the part that decides
which ink is a wire, which is a symbol and which is a word. Everything after that point stays as it is: the
skeleton graph, the emit into a TestBench and parts table, the drawing, paste, the dialog, `recognize` and MCP.

```
picture ─► pass 1: perception net, low resolution ─► is it a schematic? + px per part
        ─► resample so a typical part is the size the net was trained at (tile a huge picture)
        ─► pass 2: perception net ─► parts (class, box, orientation), pin heat map, masks (wire / symbol / text /
                                     annotation), junction dots and non-connecting crossings, text-line regions
        ─► text net on each text-line crop ─► strings
        ─► classical assembly: wire mask → skeleton graph → wires; pins matched to each class's pin layout; wire ends
           to pins; junctions decide crossings; strings → ValueGrammar; words → designators, values, nets, ports
        ─► SchematicImageResult ─► the unchanged back half; IML-8's size frame places parts at the picture's scale
```

There is **one perception net and one text net**. The perception net answers every question about *where* (parts,
pins, wires, dots and text regions) from one shared backbone with several heads. Reading characters needs a
different input, a narrow crop normalised to its height, and a different output, a sequence; that is why the text
net is a separate network. Two passes of the same perception net handle scale. In the owner's starting set a picture
can be anywhere from 294 to 7,016 px wide, and a part from 20 to 400 px long.

**The assembly stays classical** because turning a clean mask into a graph and applying the junction-dot rule is exact
geometry that is already written and tested. The rule-based reader failed earlier, at deciding which ink is what.

Seventy-two unlabelled pictures cannot train a network, so the models learn mostly from **generated pictures with
exact labels**:

- circuitRF's own schematics, drawn by its own renderer (IML-2);
- other drawing conventions, drawn from style packs (IML-3);
- the real pictures, labelled once (IML-4), used to fine-tune and above all to **measure**. A fixed share of them is
  never trained on.

Where things live:

| | Where | What |
|---|---|---|
| Training kit | `tools/schematic-ml/` (Python, developer-only) | import, validation, label tools, training, evaluation, export |
| Generators | `tools/SchematicSynth` (C#, in the solution) | generated pictures with exact labels |
| Inference | `src/Vision` (managed C#, no native code) | the `.crfnn` runtime and the two shipped models |
| Reader | `src/Design/Schematic/Recognition/` | model outputs → `SchematicImageResult` |
| Data | `$CRF_ML_DATA`, **outside the repository** | pictures, labels, generated batches, runs, exports |

## 2. The data home and the label format

### 2.1 The data home

`$CRF_ML_DATA`, default `~/circuitRF-ml-data`, and every tool takes `--data`. The layout is in the handbook. Its
rules:

- **Outside the working tree, enforced.** `crfml.data.resolve_data_home` refuses a data home at or under the
  repository root. That check is the real guard. The `.gitignore` line `**/circuitRF-ml-data/` is a second guard, for
  the default name only. A single `git add -A` with pictures inside the tree would put third-party screenshots in the
  history forever (L1).
- **Neutral names (L2).** Many source pictures are named after a commercial tool or a part. The importer names each
  picture `<family>-<sha256[:10]>`, names each family `family-<letter>`, and records which folder a family came from
  in `manifest.json` only, which never leaves the machine. The original *file* name is recorded nowhere, not even in
  the manifest. The importer prints families and counts, never a source name. Unreadable files are reported by count,
  and their paths go only to the data home's `import-log.txt`.
- **Families are letters in name order.** Top-level sub-folders are lettered in case-insensitive name order on first
  import, so the same set of folders imported on another machine gets the same letters. Pictures loose at the top level are
  one more family. A folder seen before keeps its family; a new one takes the next free letter.
- **Formats.** PNG and JPEG are stored byte for byte. A JPEG's compression artefacts are exactly what the reader meets
  in the field, so re-encoding would throw away training signal. WebP, GIF (first frame) and BMP are decoded to PNG,
  which keeps every later tool to two formats. BMP is not in the brief's list of conversions; it is converted because
  nothing gains from keeping it.
- **The split is a function of content, decided once (L8).** A picture is `test` when
  `int(sha256[:8], 16) % 4 == 0`. That depends on neither import order nor the machine. A family can be unlucky,
  though: by that rule alone, a family of five may end up with no test pictures at all. So after each import, every
  family the import touched is topped up to `min(2, n // 2)` test pictures, taking this import's new pictures in hash
  order and never moving an existing one. The `min(…, n // 2)` keeps a family of one or two from losing all its
  training pictures to the test split. The split is then written to the manifest with its rule (`hash`, `balance`
  or `corpus`) and never recomputed. A later import can add test pictures to a family but cannot take one away. This
  makes the top-up depend on how pictures were batched into imports, which is acceptable because it happens once
  and is frozen.
- **The corpus is all test.** `testdata/image-sources` (the datasheet and web pictures of the IMF series) imports as
  `corpus-ds` and `corpus-web`, every picture `test`, with its `answer.cnl` beside it as `<id>.cnl`. Three of its 25
  pictures have no `answer.cnl` yet; IML-4 derives truth for them as it does for the rest.

**The starting set, as imported (2026-10-12):** 72 files, 71 distinct pictures (one file was a byte-identical copy),
in five families. Four came from sub-folders and one from the top level. 51 are train and 20 test. With the corpus's
25, the data home holds 96 pictures, 45 of them test.

| family | pictures | train | test |
|---|---:|---:|---:|
| family-a | 23 | 16 | 7 |
| family-b | 5 | 3 | 2 |
| family-c | 19 | 13 | 6 |
| family-d | 19 | 16 | 3 |
| family-e | 5 | 3 | 2 |
| corpus-ds | 16 | 0 | 16 |
| corpus-web | 9 | 0 | 9 |

### 2.2 The label format

`tools/schematic-ml/schema/mllabel.schema.json`, version 1. There is one `<id>.mllabel.json` beside each picture,
and every coordinate is in that picture's own pixels, origin top-left, y down. Why it is shaped this way:

- **Pixels, not normalised coordinates.** Labels are drawn, reviewed and overlaid on the picture they describe. A
  pixel coordinate can be checked against the picture by eye, and the trainer normalises whenever it resamples.
- **A part is a class, an orientation, a body box and named pins.** The body box is what IML-8 measures a picture's
  part size from, so it is the part's drawing *without* the straight leads that end on its pins — `GlyphBody`'s rule
  (§4.3), the same one IML-4's labelling guide and IML-8 use; a pin may therefore sit outside the box, at the end of
  its lead. Lead length is an editor's convention, not the part's. Pins are named with the class's own
  pin names, so IML-7 can map them onto circuitRF's pins by name. `orientation` is `{turn, mirror}` relative to the
  class's canonical drawing: mirror `x ↦ 1−x` first, then turn clockwise by 0, 90, 180 or 270 degrees. That is a
  rotation and a reflection, as `SymbolRotation` and `MirrorX` are in the schematic model.
- **Texts are separate objects that parts point at**, through `designator` and `values`, which hold text ids. A
  string belongs to one text line, and the text net reads lines, not parts. A word's role (`designator`, `value`,
  `net`, `port`, `pin-number`, `pin-name`, `title`, `other`) is what the assembly has to learn to decide. Each text
  has a `quad`, four corners in reading order, because datasheet text is often rotated 90 degrees.
- **Junctions and crossings are listed explicitly.** Whether two crossing wires connect is the question a wire mask
  cannot answer. The perception net has a head for each, so the label holds both.
- **`annotations` and `ignore` are kept apart.** An annotation (an arrow, a frame, window chrome, a title block) is
  ink the network must learn to recognise *and drop*, so it is trained on. An `ignore` region is neither trained on
  nor scored: a smudge, a cropped-off fragment, something nobody could label.
- **`cut` marks a part the picture's edge cuts off**, so the validator does not demand ink at a pin outside the
  picture, and scoring does not count it against the reader.
- **`masks` exist only for generated pictures.** A generator knows every pixel it drew. For real pictures, IML-6
  derives masks from the vector label.
- **Netlist truth is a separate `<id>.cnl`.** Topology is scored as graph isomorphism on a netlist (IMF-1's
  `NetlistShape.SameTopology`). A netlist is also what the corpus already holds, and it reads in every circuitRF tool.
- **`params` hold base-SI text** (`"R": "28.495"`), for a value a picture states that is not a single value word.

**The validator** (`crfml.validate`) holds a label to the schema and then to sense, with one line per finding and
exit 1 on any error. A wire end that reaches nothing is a *warning*, because real pictures do draw one, and a check
that refused them would teach labellers to hide it. Value texts are read by `crfml/values.py`, a line-for-line port
of `ValueGrammar` and the parts of `BomTablePaste` it reads values through. Its tables come from
`tools/schematic-ml/units.json`. `tests/Ui.Tests/Vision/UnitsTableTests` holds that file to the C# grammar, both ways
wherever a table can be enumerated: every listed prefix, ohm spelling, decimal-point letter, not-fitted word and
line unit reads in C#, and every letter the file leaves out does not. A label the validator accepts is therefore one
circuitRF's own reader reads the same way.

**A known gap:** the grammar reads ohms, farads and henries, and nothing for volts or amperes. A source's `3 V` is not
a value word, so a source's value goes in `params` until `ValueGrammar` learns those units.

## 3. The class list

`tools/schematic-ml/classes.json`. It is the one list the generators, the trainer, the exported models (each carries a
copy) and IML-7's mapping read. It also holds the text net's `charset`, so a generator never draws a character the
reader cannot output.

Each class has:

- **`pins`**, in the class's **canonical orientation**, normalised to its body box. For a box whose pins depend on the
  part, it is `"perimeter"`. A two-terminal part is canonically vertical, pin 1 at the top, as circuitRF draws its
  own R, L and C. A multi-port block is canonically drawn the way circuitRF draws that kind, so a detected
  orientation becomes a `SymbolRotation` with no per-class offset.
- **`symmetry`**: which orientations are the same drawing, *pin positions included*, given which pins are
  interchangeable. The trainer must not punish a resistor read upside down. The brief's vocabulary was `none`,
  `c2`, `c4`, `mirror` and `c2+mirror`. Two values are added, each needed by a class in the list:
  - **`flip`**: the same drawing reflected across the axis *across* the pins (`y ↦ 1−y`), which is a turn of 180°
    after a mirror. An inductor's humps, a step's outline and an amplifier triangle are symmetric that way and no
    other.
  - **`c4+mirror`**: every orientation. A cross junction and a no-connect mark are.
  `mirror` is always the reflection `x ↦ 1−x` in the canonical frame, through the pins of a vertical two-terminal part.
- **`circuitrf`**: the kind it emits, or `"ignore"` for ink that is drawn but is not circuit (probes, analysis and
  controller boxes, no-connect marks). For a class that draws fewer pins than its kind has, `open` and `grounded` name
  the kind pins the emit leaves open or ties to ground: a stub is a TLIN with one end open or shorted. For the
  transformer, `count` and `coupled` say it emits two coupled inductors, as the current emit already does.
  `tests/Ui.Tests/Vision/ClassListTests` holds every kind to `SymbolKind`. It also checks that the pins drawn plus
  the open and grounded ones are exactly that kind's pins, by name, from `SymbolPortDefs`, and that a perimeter
  class maps to a variadic kind.
- **`sizeReference`**: whether this class's body may set the picture's scale (IML-8). It is true for parts drawn
  roughly the same size in every editor (R, L, C, the polarised capacitor, diode, ground, the three sources and the
  port). It is false for anything whose size follows its pin count or its label (boxes, amplifiers, lines, terminals).
- **`variants`**, optional: what a part's `variant` may say. A variant is the same symbol with one mark added and the
  same emit (a zener's bent bar, a battery's plates), so it is not a separate class, which would split one shape's
  training examples in two.

**Departures from the brief's v1 list**, all of them splits, each because one class would have needed two pin
layouts:

- `op-amp` is split from `amplifier`. An RF amplifier triangle has two pins and emits the System `Amp`. An op-amp has
  three to five and emits a box (`Snp`), so it takes perimeter pins.
- `switch-spdt` is split from `switch`. An SPST switch has two pins (`Switch`) and an SPDT three (`SwitchD`).
- `schottky` is added to the diode's variants beside LED, zener and varactor. It is common in RF datasheets and drawn
  as a diode with one more mark.

**Mapping choices worth knowing:** `port` → `Term`, the box-with-source port that draws both terminals. A `terminal` or
`power-port` (the end of a net, or a named rail) → `Pin`, because both are a name on a net, not a source. `source-ac` →
`P1Tone` and `source-dc` → `Vdc`, as decided in IMF O1. `fet-n`/`fet-p` → the Curtice FETs, the reader's existing
choice. `via` → `ViaGnd`, because a via on a schematic is almost always a via to ground. `mstep` → `Mtaper`.

## 4. Generated pictures of circuitRF's own schematics (IML-2)

`tools/SchematicSynth` (C#, in `circuitrf.slnx`, so an API change breaks its build rather than its next run):

```bash
dotnet run --project tools/SchematicSynth -- crf --count 30000 --seed 1 --out $CRF_ML_DATA/synth/crf/batch-001 --preview 24
```

Each picture `crf-<seed>-<index>` writes `<id>.png|.jpg`, four masks (`<id>.wire.png`, `.symbol.png`, `.text.png`,
`.annotation.png`, 8-bit 0/255), `<id>.cnl` (the netlist truth) and `<id>.mllabel.json`, written last through a
temporary name: a label on disk means the picture is complete, which is what makes a batch resumable. Picture `k` of
seed `s` draws only from its own random stream, so a batch is the same bytes whatever the thread count (checked:
8 threads against 1, 420 files identical). About 30 pictures a second on ten cores. `--out` inside the repository is
refused (L1).

### 4.1 Circuits

`CircuitGenerator` chains weighted recipes between a source (a port mostly, a one-tone or current source sometimes)
and a load: LC ladders (low-, high-, band-pass, 2–9 sections), pi and tee pads, L-sections, matching networks
(series/shunt L and C, open and shorted TLIN stubs, quarter-wave lines), microstrip networks (MLIN, MTEE, MCROSS,
MBEND, MTAPER, stubs ending open, on a via or on a capacitor), an amplifier stage (BJT or FET, bias divider or gate
supply, RF choke, DC blocks, supply and decoupling, now and then a protection diode), a detector or limiter, a chain
of system blocks (amplifier, filter, attenuator, SPST and SPDT switch, circulator, coupler, balun, mixer with its LO),
an S-parameter block of 2–4 ports, and mixtures of two to four of these. Now and then a VAR block.

**It writes `.cnl` text and reads it** with `CnlReader`, so the truth is exactly what circuitRF's own reader makes of
it. Two consequences worth knowing: the system blocks bind each port as a (+, −) pair (`Amp:AMP1 in 0 out 0`), and
the reader knows `Ohm`, `kOhm`, `uH`, `um` but not `Ω`. The palette spells units `Ω`, `kΩ`, `µH`, `µm`, so after
drawing, 70 % of parts have their units turned to the palette's spelling; the comparison treats the two as one.

Designators are the palette prefix and the next free number (`R3`, `TL2`, `Term1`, `AMP1`). Some shown parameters
are hidden, some hidden ones shown, now and then a type label is turned off.

### 4.2 Drawings

- **Tidy** (≈ 42 %): `NetlistSchematic.Build`. It labels every net; a random share of the labels is taken off again,
  and put back if the drawing then extracts to a different circuit (a label can be the only thing joining two runs).
- **Hand-placed** (≈ 43 %): each part at a random grid position, turn (R0 half the time) and sometimes mirrored,
  never overlapping another's glyph and label block; a ground symbol on each grounded pin, out along its lead and
  turned away from the part; wired by `SchematicAutoRouter` (Design grants the generator its internals for exactly
  this); a terminal the router cannot reach is joined by net labels, and about a third of the wired nets carry
  their name. The router's own rules produce the non-connecting crossings.
- **Authored** (≈ 15 %): the repository's `.csch` files whose every part has a class (15 of the 40 today), with
  their placed pictures and free texts removed — an example built from an image keeps that image under its drawing,
  parts and words included, and nothing would label them. One to three parts move one grid step with their wires
  following (a move that changes the circuit is undone), and lumped values are re-drawn. Their truth is their own
  extraction.

**Every drawing is extracted with `NetExtractor` and compared with its truth before a picture is made of it**
(R-iml2-6): same instances by name and type, the same parameters, and each pin on corresponding nets under one
consistent renaming, ground kept ground. A drawing that fails is re-drawn Tidy, then a new circuit is drawn.

**Classes.** `ClassList.OfKind` maps each kind circuitRF draws to its class; a circuit that would draw a kind with
no class is never generated, because unlabelled ink teaches a detector that a part is background. A TLIN with one
end open is labelled `stub-open`, its one pin at the connected end. circuitRF's DC source *is* a battery, so its
`source-dc` parts carry the `battery` variant. Classes circuitRF never draws — `capacitor-polarised`, `power-port`,
`op-amp`, `ic-box`, `stub-short` (circuitRF draws a TLIN and a ground symbol), `transformer`, and the annotation
classes but `controller` (VAR blocks) — are IML-3's. On 300 pictures every class circuitRF draws appears in at least
1 % of them; the batch prints the histogram at its end and marks any class under 1 %.

### 4.3 Pictures, labels and masks

**Every pixel is `SchematicRenderer`'s.** The view is random: the shipped theme light (60 %) or dark, grid on (60 %)
or off; a zoom at which a resistor's body spans 12–300 device pixels, log-uniform; a canvas of 480–1400 × 360–1000
logical pixels, or one shrunk to the drawing; a crop centred near a random part when the drawing is bigger than the
window (a uniformly random window over a big drawing at high zoom was mostly empty canvas), and a deliberate offset
that cuts parts at the edge 20 % of the time; a selection 8 %, a placed picture underneath 5 % (drawn by the
generator, `.underlay/` in the batch folder). The renderer draws its grid as lines only, so there are no dot grids;
it has no hover outline, so there are none either.

**Screenshot conditions** (`Screenshot`): Retina 2× (40 %) by scaling the canvas, so a one-pixel line becomes two as
it does on screen; chrome bands half the time — toolbar, status bar, scroll bars, a side panel — flat shapes with no
text and no real application's artwork, each an `annotation` of kind `chrome`; a downscale (50 %) by 0.5–0.95 with
nearest, linear, cubic or area filtering; blur 15 %; JPEG at quality 40–95 (45 %), otherwise PNG, posterised 12 % of
the time. Chrome and rescale apply to the masks too; blur, compression and palette are the picture's alone.

**Labels come from the model and the renderer, never from pixels.** A part's `body` is `GlyphBody.Of` — the glyph
without the straight leads ending on its pins (a `LinePrimitive` with an end on a pin; a polyline's first or last
segment when it starts or ends on one; text is not body; curves bounded tightly by sampling) — through the instance
transform and the view. `GlyphBody` lives in `src/Design/Schematic` because IML-8 measures with the same rule. Pins
are the symbol's pins, named as the kind names them (which is the class's pin names, `ClassListTests`). `turn` is
the instance's `SymbolRotation`, `mirror` its `MirrorX`: the class's canonical drawing IS circuitRF's.

**Word quads come from the renderer.** `SchematicRenderer.MeasureTexts` runs `Draw` onto an `SKNoDrawCanvas`, and
each draw site reports the string it just drew with the font, origin and turn it drew it with — so a quad cannot be
somewhere the text is not. It is read-only and changes nothing the application draws. The quad is the line box,
ascent to descent and origin to advance width. A word cut by the canvas edge goes to `ignore`, not `texts`. Roles:
instance name → `designator`; a shown parameter → `value` when the grammar reads it, else `other` (`Num = 1`,
`File = "amp.s2p"`); net label → `net`; text inside a glyph → `pin-number` if digits, `pin-name` for
RF/LO/IF/TX/RX, else `other`; type name → `other`. A polarity mark (`+`, `−`) is drawn as text but is part of the
glyph — another editor draws it with strokes — so it is symbol ink and not a word. A hidden parameter is never
drawn, so never measured, so never a word.

**circuitRF draws a value as `R = 50 Ohm`, and the grammar does not read that.** `ValueGrammar` reads `R=50 Ohm`
and `Z=50Ohm`. The label keeps the string as drawn — the text net must learn to read what is on the screen — and
`crfml.values.read_value_text` (and the generator's `ValueText`) closes up the spaces round the `=` and before the
unit before the grammar reads it. **IML-7's assembly must do the same.** The validator calls it for every value
text, and also accepts a wire ending on a chrome band's edge, which is where a screenshot's canvas ends.

**Masks are the renderer drawing filtered copies of the model** — wires only (wires, junction dots, open-end
marks); symbols only (each glyph's drawing and polarity marks, port markers, no labels or words); words only
(labels, net labels, a glyph's words) — each thresholded against the theme's background, and the annotation mask is
the chrome bands plus whatever a selection changed. A filtered copy hands each component its glyph as its own
per-instance symbol, the path the renderer already takes for one, and every pass is a new model instance.

**Checks before writing.** `LabelChecks` is the validator's structural checks ported to C# (classes, pins, ids,
references, values, charset, bodies inside or `cut`, wire ends), plus one only a generator can make: each pin is on
ink in the *symbol* mask. A picture with an error is not written. Gates: `tests/Ui.Tests/Vision/SchematicSynthTests`
(three seeds, the checks, the netlist round trip by `Netlists.Same` and independently by `NetlistShape.SameTopology`,
hidden parameters are not words, and a seed is the same bytes twice; about a second) and `GlyphBodyTests` (R, L, C,
ground, port, one-tone source and BJT: rendered, leads rubbed out, the remaining ink is the body box within a pen).

