# Field pictures for Create from Image

This folder holds pictures Create from Image is tested against: the kind users actually bring — an application circuit
from a datasheet, a layout figure, a screenshot. They are other people's pictures, so **nothing in this folder is ever
committed except this README**: `.gitignore` ignores every folder under it (`/testdata/image-sources/*/`). On a fresh
clone the field tests skip and say why.

## What goes here

One folder per picture, under a **generic name** — `picture-a`, `picture-b`, … — never the name of the product, the
document or the company it came from, and no file name that says either. Inside it, the picture itself (any format
circuitRF reads: PNG, JPEG, BMP, GIF, WebP) and an `expected.json` written by hand from the picture.

A picture folder without an `expected.json` is passed over.

## `expected.json`

`picture` and `kind` are required; every other key is optional, and a picture is checked only on what its file states.
Counts are read exactly. The same schema describes the committed style pictures under
`tests/Ui.Tests/ImageRecognition/Fixtures/style-pictures/`, which are drawn by our own code and carry an `answer`.

| Key | Meaning |
|---|---|
| `picture` | The picture's file name, in this folder. |
| `kind` | `schematic` or `layout` — what the picture is. It is also the kind the picture is read as. |
| `umPerPx` | A layout picture's scale, µm per pixel. Required for a layout: a picture never states its scale. |
| `technology` | A layout picture's technology: a shipped technology's id, or a `.ctech` path relative to this folder. The shipped two-layer board when left out. |
| `source` | Where the drawing's conventions come from: `datasheet` or `web`. |
| `refused` | `true` when the picture is no circuit and must be refused. |
| `answer` | A `.cnl` in this folder: the circuit as drawn. The reading's part–net graph is compared with it (kinds and connections, ground and ports included; names, values and port numbers left out). |
| `parts` | Parts read: resistors, inductors, capacitors and the parts of no known kind. |
| `nets` | Nets besides ground. |
| `kinds` | Elements by type, as the netlist writes them: `{ "R": 2, "C": 3, "Port": 2 }` — a type not named is not checked. |
| `grounds` | Ground symbols drawn. |
| `terminals` | Terminal circles drawn. |
| `ports` | Ports the circuit has. |
| `transistors`, `sources`, `amplifiers`, `ics`, `transformers` | Those drawn. |
| `designators` | The designators drawn, as a list (`["C1", "Cblock", "Cblock"]`). |
| `values` | Designator → the value printed beside it (`{ "R1": "240", "CIN": "1u" }`); a part number is compared as text. |
| `names` | Net and port names drawn. |
| `style` | A free-text note on how the picture is drawn. Never checked. |

`ImageFieldTests` scores every schematic picture with `FieldScore` — one line per picture, printed to the test output
(and so to `TestResults/last-run.trx`) — and fails a picture only on what the series' acceptance promises for it. A
layout picture is still held to its counts.

```json
{
  "picture": "picture.png",
  "kind": "schematic",
  "source": "datasheet",
  "kinds": { "L": 1, "C": 3 },
  "ports": 2,
  "designators": ["C1", "C2", "C3", "L1"],
  "values": { "L1": "22nH" }
}
```
