"""Draft labels for unlabelled real pictures (brief-iml-4 R-iml4-1).

    python -m crfml.prelabel                          every picture in the data home with no label and no draft
    python -m crfml.prelabel family-c-1a2b3c4d5e …    these pictures (ids)
    python -m crfml.prelabel --family family-c [--force]

Each draft is written as ``<id>.draft.mllabel.json`` beside its picture; the annotation page opens a draft when there
is no label, and saving it there writes ``<id>.mllabel.json``. A draft is never a label: nothing trains on it and
nothing scores it.

**Where the draft comes from.** ``circuitrf recognize <picture> --json --image-kind schematic`` — circuitRF's own
picture reader, through its command line, so the draft is whatever the shipped reader reads. Until IML-7 that is the
rule-based reader; once the trained models are wired in, the same call drafts with them, and this module needs no
change (label, train, pre-label the next batch with the better model). The reader's JSON gives each symbol's kind,
box and orientation and each word's string, class and box; it carries no wire geometry, so wires are traced here from
the picture's ink (``geometry.trace``) with the symbols and words taken out. Pins are put where the class puts them on
the symbol's box and moved onto the nearest traced wire end.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from pathlib import Path

from .cli import CliMissing, run
from .data import DataHomeError, load_classes, load_manifest, resolve_data_home
from . import geometry as G
from .values import read_value_text

LABEL_SUFFIX = ".mllabel.json"
DRAFT_SUFFIX = ".draft.mllabel.json"

# the reader's kind labels (RecognisedSymbol.KindLabel) → classes
_KIND = {
    "Resistor": "resistor", "Resistor (variable)": "resistor", "Inductor": "inductor", "Capacitor": "capacitor",
    "Ground": "ground", "Port": "terminal", "TransmissionLine": "tline", "ShortStub": "stub-short",
    "OpenStub": "stub-open", "Diode": "diode", "Amplifier": "amplifier", "Ic": "ic-box", "Transformer": "transformer",
    "Transistor NPN": "bjt-npn", "Transistor PNP": "bjt-pnp", "Transistor NFET": "fet-n", "Transistor PFET": "fet-p",
    "Source AC": "source-ac", "Source DC": "source-dc", "Source SUPPLY": "source-dc", "Source CURRENT": "source-current",
}
_BLOCKS = ("attenuator", "filter", "circulator", "switch", "coupler", "balun", "mixer", "amplifier")
_ROLE = {"Designator": "designator", "Value": "value", "LineParameters": "value", "Name": "net"}


def class_of(kind_label: str) -> tuple[str, str | None] | None:
    """The class (and variant) a reader's kind label is, or None for a kind with no class (``?``)."""
    if kind_label in _KIND:
        return _KIND[kind_label], ("battery" if kind_label == "Source BATTERY" else None)
    if kind_label == "Source BATTERY":
        return "source-dc", "battery"
    k = kind_label.lower()
    if k.startswith("transistor"):
        return ("bjt-pnp" if "pnp" in k else "fet-p" if re.search(r"\bp", k[10:]) else "fet-n" if "fet" in k else "bjt-npn"), None
    for b in _BLOCKS:
        if b in k:
            return ("switch-spdt" if b == "switch" and ("spdt" in k or "throw" in k) else b), None
    return None


def _orientation(text: str | None) -> tuple[int, bool]:
    if not text:
        return 0, False
    m = re.search(r"(\d+)", text)
    turn = int(m.group(1)) % 360 if m else 0
    return (turn if turn in (0, 90, 180, 270) else 0), "mirror" in text.lower()


def _transform(u: float, v: float, turn: int, mirror: bool) -> tuple[float, float]:
    if mirror:
        u = 1 - u
    du, dv = u - 0.5, v - 0.5
    for _ in range(turn // 90):
        du, dv = -dv, du
    return du + 0.5, dv + 0.5


def _box(b: dict) -> list[float]:
    return [float(b["left"]), float(b["top"]), float(b["right"]), float(b["bottom"])]


def draft(picture: Path, reading: dict, family: str | None, classes: dict) -> dict:
    """A draft label from the reader's JSON (``result.recognize``) and the picture's ink."""
    rgb = G.load_rgb(picture)
    mask = G.ink_mask(rgb)
    h, w = mask.shape
    by_name = {c["name"]: c for c in classes["classes"]}

    texts = []
    for wd in reading.get("words", []):
        s = wd.get("text") or ""
        if not s.strip():
            continue
        x0, y0, x1, y1 = _box(wd["box"])
        role = _ROLE.get(wd.get("class", ""), "other")
        if role == "value" and read_value_text(s) is None:
            role = "other"
        texts.append({"id": f"t{len(texts) + 1}", "quad": [[x0, y0], [x1, y0], [x1, y1], [x0, y1]], "string": s, "role": role})

    parts = []
    for sym in reading.get("symbols", []):
        found = class_of(sym.get("kind", "?"))
        if found is None:
            continue
        cls_name, variant = found
        cls = by_name.get(cls_name)
        if cls is None:
            continue
        box = _box(sym["box"])
        turn, mirror = _orientation(sym.get("orientation"))
        part = {"id": f"p{len(parts) + 1}", "class": cls_name}
        if variant:
            part["variant"] = variant
        part["orientation"] = {"turn": turn, "mirror": mirror}
        part["body"] = box
        part["pins"] = []
        if cls["pins"] != "perimeter":
            for q in cls["pins"]:
                u, v = _transform(q["at"][0], q["at"][1], turn, mirror)
                part["pins"].append({"name": q["name"], "x": box[0] + u * (box[2] - box[0]), "y": box[1] + v * (box[3] - box[1])})
        ref = sym.get("refdes")
        if ref:
            near = [t for t in texts if t["string"] == ref]
            if near:
                cx, cy = (box[0] + box[2]) / 2, (box[1] + box[3]) / 2
                t = min(near, key=lambda t: math.hypot(t["quad"][0][0] - cx, t["quad"][0][1] - cy))
                t["role"] = "designator"
                part["designator"] = t["id"]
        parts.append(part)

    exclude = [p["body"] for p in parts] + [G.quad_box(t["quad"]) for t in texts]
    traced = G.trace(mask, exclude, [(q["x"], q["y"]) for p in parts for q in p["pins"]])
    ends = [e for s in traced.segments for e in s.ends()]
    pen = traced.pen

    for p in parts:
        b = p["body"]
        reach = max(2 * pen + 3, 0.5 * max(b[2] - b[0], b[3] - b[1]))
        if by_name[p["class"]]["pins"] == "perimeter":
            # a box's pins: every wire end on its edge
            k = 0
            for (x, y) in ends:
                dx = max(b[0] - x, 0, x - b[2])
                dy = max(b[1] - y, 0, y - b[3])
                if math.hypot(dx, dy) <= pen + 2.5:
                    k += 1
                    p["pins"].append({"name": str(k), "x": x, "y": y})
            continue
        for q in p["pins"]:
            best = min(ends, key=lambda e: math.hypot(e[0] - q["x"], e[1] - q["y"]), default=None)
            if best is not None and math.hypot(best[0] - q["x"], best[1] - q["y"]) <= reach:
                q["x"], q["y"] = best
    parts = [p for p in parts if by_name[p["class"]]["pins"] != "perimeter" or len(p["pins"]) >= 2]
    for p in parts:
        for q in p["pins"]:
            q["x"], q["y"] = round(q["x"], 1), round(q["y"], 1)
        p["body"] = [round(v, 1) for v in p["body"]]

    return {"version": 1, "image": picture.name, "width": w, "height": h, "origin": "real", "family": family, "style": None,
            "parts": parts,
            "wires": [{"points": s.points(), "pen": round(s.pen, 1)} for s in traced.segments],
            "junctions": [list(j) for j in traced.junctions], "crossings": [list(c) for c in traced.crossings],
            "texts": texts, "annotations": [], "ignore": []}


def read_picture(picture: Path) -> tuple[dict | None, str]:
    """The reader's JSON for a picture read as a schematic, or None and why not."""
    p = run(["recognize", picture, "--json", "--image-kind", "schematic"], timeout=600)
    try:
        doc = json.loads(p.stdout)
    except json.JSONDecodeError:
        return None, (p.stderr.strip().splitlines() or ["no JSON"])[-1]
    rec = (doc.get("result") or {}).get("recognize")
    if rec is None:
        return None, "; ".join(d.get("message", "") for d in doc.get("diagnostics", [])) or doc.get("status", "refused")
    return rec, "ok"


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m crfml.prelabel", description=__doc__.split("\n\n")[0])
    ap.add_argument("ids", nargs="*", help="picture ids (default: every unlabelled picture)")
    ap.add_argument("--data", help="the data home (default $CRF_ML_DATA, else ~/circuitRF-ml-data)")
    ap.add_argument("--family", help="only this family")
    ap.add_argument("--force", action="store_true", help="redraft pictures that already have a draft (never a label)")
    args = ap.parse_args(argv)
    try:
        home = resolve_data_home(args.data, create=False)
    except DataHomeError as e:
        print(f"refused: {e}", file=sys.stderr)
        return 1
    classes = load_classes()
    manifest = load_manifest(home)
    pictures = [p for p in manifest["pictures"] if (not args.ids or p["id"] in args.ids)
                and (not args.family or p["family"] == args.family)]
    done = 0
    for rec in pictures:
        folder = home / "real" / rec["family"]
        picture = next((folder / f"{rec['id']}{ext}" for ext in (".png", ".jpg") if (folder / f"{rec['id']}{ext}").is_file()), None)
        if picture is None:
            continue
        if (folder / f"{rec['id']}{LABEL_SUFFIX}").is_file():
            continue
        out = folder / f"{rec['id']}{DRAFT_SUFFIX}"
        if out.is_file() and not args.force:
            continue
        try:
            reading, why = read_picture(picture)
        except CliMissing as e:
            print(f"refused: {e}", file=sys.stderr)
            return 1
        if reading is None:
            reading = {}
            print(f"{rec['id']}: the reader refused it ({why}); the draft has traced wires only", file=sys.stderr)
        label = draft(picture, reading, rec["family"], classes)
        out.write_text(json.dumps(label, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        done += 1
        print(f"{rec['id']}: {len(label['parts'])} parts, {len(label['wires'])} wires, {len(label['texts'])} words")
    print(f"{done} draft(s) written.", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
