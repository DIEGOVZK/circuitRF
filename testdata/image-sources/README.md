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

`picture` and `kind` are required; every other key is optional, and a test checks only what the file states. Counts
are read exactly.

| Key | Meaning |
|---|---|
| `picture` | The picture's file name, in this folder. |
| `kind` | `schematic` or `layout` — what the picture is. It is also the kind the picture is read as. |
| `umPerPx` | A layout picture's scale, µm per pixel. Required for a layout: a picture never states its scale. |
| `technology` | A layout picture's technology: a shipped technology's id, or a `.ctech` path relative to this folder. The shipped two-layer board when left out. |
| `parts` | Parts read: resistors, inductors, capacitors and the parts of no known kind. |
| `nets` | Nets besides ground. |
| `kinds` | Elements by type, as the netlist writes them: `{ "R": 2, "C": 3, "Port": 2 }` — a type not named is not checked. |

`ImageFieldTests` also requires that every picture reads with no refusal.

```json
{
  "picture": "circuit.png",
  "kind": "schematic",
  "parts": 5,
  "nets": 4,
  "kinds": { "L": 2, "C": 3, "Port": 2 }
}
```
