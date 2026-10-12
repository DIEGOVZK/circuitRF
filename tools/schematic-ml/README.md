# schematic-ml — the training kit for circuitRF's schematic picture reader

This kit trains the models behind **Edit ▸ Paste Image as Schematic** and **Create Schematic from Image**. It imports
pictures of schematics, checks their labels, and (in later phases) trains, measures and exports the two networks that
ship inside circuitRF as `src/Vision/Models/*.crfnn`.

Only circuitRF developers use it. Users never train anything and never need Python: the trained weights ship inside
the application. You need this kit when new training pictures arrive, or when a drawing convention reads badly and
the models have to be retrained.

How the whole system fits together, and why each contract here is shaped the way it is:
[`docs/design/schematic-vision.md`](../../docs/design/schematic-vision.md).

## The data rule

1. **Training data never enters the repository.** That covers real pictures, their labels, generated pictures,
   checkpoints and exports. All of it lives in the data home (below), outside the working tree. Every tool here refuses
   a data home inside the repository, and `.gitignore` also ignores `**/circuitRF-ml-data/` in case someone points one
   there anyway. The repository holds the code, `classes.json`, `units.json`, the label schema, three tiny fixtures
   drawn by our own code, and the shipped weights.
2. **No vendor or editor name, anywhere.** Many source pictures come from folders or files named after a commercial
   tool or a part maker. The importer gives every picture a neutral id (`family-c-3f2a9c01de`), and the only record of
   which folder a family came from is the data home's own `manifest.json`. Don't copy a source folder or file name
   into code, a comment, a style pack, a test, a brief, a model card or a RESOLVED.md entry. Describe a family by its
   id and by what it looks like ("blue on cream, zig-zag resistors"). The repository's pre-commit vendor-name grep
   covers `tools/schematic-ml/` too.

## Setting up Python

The kit needs **Python 3.13**. Its dependencies are pinned to exact versions in `crfml/requirements.txt`. All of
them are MIT, BSD or HPND licensed, and they are for development only: nothing here ships.

```bash
cd tools/schematic-ml
python3.13 -m venv .venv            # .venv/ is git-ignored
.venv/bin/pip install -r crfml/requirements.txt
```

Every command below runs from `tools/schematic-ml` with that environment (`.venv/bin/python`, or `source
.venv/bin/activate` first).

## The data home

`$CRF_ML_DATA`, or `~/circuitRF-ml-data` when it is unset. Every tool also takes `--data <dir>`.

```
manifest.json                 every real picture: id, family, sha256, size, split, imported-from (local only)
real/<family>/<id>.<ext>      the picture, renamed; <id>.mllabel.json and its netlist truth <id>.cnl once labelled
review.json, review/          the reviewer's verdict on each label, and the contact sheets
synth/<generator>/<batch>/    generated pictures + labels — regenerable, safe to delete
fonts/                        open-licensed fonts for generated text (fetch_fonts), each family with its licence; index.json
runs/<run-id>/                checkpoints, logs, metrics.json
exports/<model>-<version>/    .onnx, .crfnn, probe, model card draft
```

Back it up like any other irreplaceable folder. The labels in `real/` take human time to make.

## Importing pictures

```bash
.venv/bin/python -m crfml.importer <folder>                  # each top-level sub-folder becomes one family
.venv/bin/python -m crfml.importer <folder> --family family-c # add pictures to an existing family
.venv/bin/python -m crfml.importer --from-corpus ../../testdata/image-sources
```

- Each top-level sub-folder becomes one family, lettered in name order (`family-a`, `family-b`, …). A folder imported
  later takes the next free letter. Pictures loose at the top level form one family of their own. Re-importing a
  folder already in the manifest puts its new pictures in the same family.
- A picture is named `<family>-<first 10 hex of its sha256>`. Its file name is not kept anywhere. The same picture
  imported twice is imported once.
- PNG and JPEG are stored as they came, because JPEG artefacts are training signal. WebP, GIF (first frame) and BMP are
  decoded and stored as PNG.
- **The test split is decided once, here, and never moves.** A picture is `test` when
  `int(sha256[:8], 16) % 4 == 0`. Each family this import touches is then topped up to `min(2, n // 2)` test pictures
  from its new pictures, lowest hash first (`"splitRule": "balance"` in the manifest). The trainer never loads a
  `test` picture.
- `--from-corpus` imports the datasheet and web corpus as `corpus-ds` and `corpus-web`. Every one of those pictures is
  `test`, and each `answer.cnl` is copied beside its picture as `<id>.cnl`, its netlist truth.

The importer prints a table of families and splits. It never prints a source folder or file name. Files that could not
be read as pictures are counted on the console, and their paths are written only to `import-log.txt` in the data home.

## Checking labels

```bash
.venv/bin/python -m crfml.validate <label.mllabel.json | folder>…
.venv/bin/python -m crfml.validate                           # every label in the data home
```

Prints one line per finding and exits 1 on any error. It checks the label against `schema/mllabel.schema.json` and
then against sense: every class is in `classes.json`; each part has its class's pins; every pin is within a pen width
of ink or of a wire end; every wire end reaches a pin, a junction, another wire or the picture's edge (a warning
otherwise); every referenced text exists; and every value text reads under circuitRF's value grammar.
`crfml/values.py` is a Python port of `ValueGrammar`, built on `units.json`, and
`tests/Ui.Tests/Vision/UnitsTableTests` holds that file to the C# side.

## Generating pictures of circuitRF's own schematics

The generator is C#, not Python: it draws with circuitRF's own renderer. From the repository root:

```bash
dotnet run --project tools/SchematicSynth -- crf --count 30000 --seed 1 --out $CRF_ML_DATA/synth/crf/batch-001 --preview 24
```

- **The same seed gives the same bytes**, whatever `--threads` is. A new batch is a new seed, not a re-run.
- **It is resumable.** A picture whose `.mllabel.json` exists is skipped, so an interrupted run carries on where it
  stopped. To regenerate a picture, delete its label.
- **It refuses an `--out` inside the repository.**
- Each picture writes the picture (`.png` or `.jpg`), its four masks, its `.cnl` netlist truth and its label. Every
  label passes the checks `crfml.validate` makes before it is written, and every drawing has already been extracted
  and compared with its netlist.
- At the end it prints a class histogram: how many pictures hold each class. A class circuitRF draws that falls under
  1 % is marked. Classes circuitRF never draws are marked as such; they come from the style packs (below).
- `--preview 24` writes `preview.png` in the batch folder: the first 24 pictures with their labels drawn over them.
  **Look at it before training on a batch.** `--count 0 --preview 24` draws the sheet for a batch already made.

About 30 pictures a second on ten cores, so 30,000 take a quarter of an hour. How the pictures and labels are made,
and why: `docs/design/schematic-vision.md` §4.

## Generating pictures in other editors' conventions

The same circuits, drawn again through **style packs** — one convention per JSON file (IML-3). First fetch the fonts,
once per data home:

```bash
cd tools/schematic-ml && .venv/bin/python -m crfml.fetch_fonts     # open-licensed faces into $CRF_ML_DATA/fonts
cd ../.. && dotnet run --project tools/SchematicSynth -- foreign --packs all --count 60000 --seed 1 \
    --out $CRF_ML_DATA/synth/foreign/batch-001 --preview 52
dotnet run --project tools/SchematicSynth -- packs                 # the packs, their medium, families and classes
```

- `--packs all` or a comma list (`--packs textbook-serif,iec-boxes`). A pack's `weight` sets its share.
- Same determinism, resumability, refusals and histogram as `crf`, plus a per-pack table. Every label carries
  `"style": "<pack>"`, so IML-6 can score each pack and leave one out.
- **Fonts.** `fetch_fonts` downloads a pinned list and checks every file's SHA-256 before keeping it; the faces and
  their licence files stay in the data home and are never committed. Without them a batch still runs on circuitRF's own
  shipped faces, and its log names every style it had to fall back for. Run `fetch_fonts` before a real batch.
- `--preview N` writes `preview.png` (the first pictures of each pack, labels drawn over them) **and**
  `preview-real.png` (real pictures from the families the packs were written from). Look at the two side by side.
- About 40 pictures a second on ten cores.

**Adding a convention is adding a file, not code.** A pack is `tools/SchematicSynth/StylePacks/<name>.json`, named by
what it looks like (never by a tool's name). Its glyphs come from the library in `StylePacks/glyphs/*.json`; a new way
of drawing a class is one more `"<class>.<style>"` entry there. Both folders are compiled in by glob — rebuild and the
new pack is in `--packs all`. Then run `dotnet test tests/Ui.Tests --filter FullyQualifiedName~Vision.StylePackTests`:
it holds every glyph's pins to its strokes and to its class's pin names, every class to three packs, and generates
three pictures from every pack. The fields are explained in `docs/design/schematic-vision.md` §5.

## Labelling the real pictures

The real pictures are labelled once, by hand, and kept in the data home beside each picture (IML-4). The rules —
what a part's body box is, where a pin sits, which role a word has — are [`LABELLING.md`](LABELLING.md); read it
before labelling anything. The tools, in the order they are used:

```bash
.venv/bin/python -m crfml.prelabel                        # drafts: <id>.draft.mllabel.json for every unlabelled picture
.venv/bin/python -m crfml.annotate                        # the annotation page, http://127.0.0.1:8765 (--open opens it)
.venv/bin/python -m crfml.label_to_cnl --check            # netlist truth <id>.cnl for every label, checked by circuitRF
.venv/bin/python -m crfml.contact_sheet --family family-c # review sheets in <data>/review/
.venv/bin/python -m crfml.contact_sheet --mark <id> ok    # or: redo --note "what is wrong"
.venv/bin/python -m crfml.contact_sheet --status          # labelled / valid / reviewed, per family
.venv/bin/python -m crfml.contact_sheet --summary         # the counts design note §6 records
```

- **`prelabel`** reads each picture with circuitRF's own reader (`circuitrf recognize --json`), so once the trained
  models are wired into the reader the drafts come from them with no change here. The reader's JSON carries no wire
  geometry, so the draft's wires are traced from the picture's ink. A draft is never trained on or scored.
- **`annotate`** serves one page from Python's standard library on 127.0.0.1 only; it fetches nothing from the
  network. It opens a picture's label, else its draft, else a blank one. Keyboard-first: the key list is on the page.
  It validates in place (the findings are clickable and show where on the picture), snaps a wire onto the ink's
  centreline (`c`) and fits a part's body to its ink by the guide's rule (`f`). Saving writes `<id>.mllabel.json`,
  keeps the previous one as `.bak`, and writes the netlist truth.
- **`label_to_cnl`** follows the reader's own rules for nets and ports (`LABELLING.md`, *The netlist truth*). For a
  corpus picture, whose truth came with it, the derived netlist is `<id>.derived.cnl`, and
  `tests/Ui.Tests/Vision/LabelToCnlTests` checks it is topologically the truth whenever the data home is present.
- **`contact_sheet`** puts each labelled picture, with its label drawn over it, beside circuitRF's own drawing of its
  netlist (`circuitrf netlist --to-schematic`, then `render`) — six pictures a page. Verdicts live in
  `<data>/review.json`; a label is done when it validates and its verdict is `ok`.

The label tools run the circuitRF command line built in this repository (`dotnet build src/Cli`; the newer of the
Debug and Release builds), or the one `$CRF_CLI` names.

## The self-test

```bash
.venv/bin/python -m crfml.selftest
```

Run it after changing anything in this folder: `classes.json`, `units.json`, the schema, the importer or the validator.
It needs no data home, GPU or network, and finishes in a few seconds. It is **not** part of `dotnet test`, because the
routine gate runs no Python. The C# side of the shared files is held by `tests/Ui.Tests/Vision/ClassListTests` and
`UnitsTableTests`, which are in the routine gate.

Its three fixtures in `tests/fixtures/` are drawn by `python -m crfml.fixtures`. Rerun that only when the label format
changes, and commit the PNG and JSON together.

## Changing the shared files

- **`classes.json`**: the class list. An exported model carries a copy, so adding, removing or renaming a class
  means retraining. Run the self-test and `dotnet test tests/Ui.Tests --filter FullyQualifiedName~Vision`.
- **`units.json`**: the value grammar's unit table. Change it together with `BomTablePaste`/`ValueGrammar` in C#;
  `UnitsTableTests` fails until the two agree.
- **`schema/mllabel.schema.json`**: the label format. Its `version` is 1. A change that existing labels would fail
  needs a new version and a migration, because the real pictures' labels hold human time.
