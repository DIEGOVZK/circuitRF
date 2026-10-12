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
real/<family>/<id>.<ext>      the picture, renamed; <id>.mllabel.json beside it once labelled
synth/<generator>/<batch>/    generated pictures + labels — regenerable, safe to delete
fonts/                        open-licensed fonts for generated text, with LICENSES/
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
