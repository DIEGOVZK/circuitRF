"""Check picture labels against the schema and against sense (brief-iml-1 R-iml1-5).

    python -m crfml.validate <label.mllabel.json | folder>…
    python -m crfml.validate [--data <dir>]          every label in the data home

One line per finding, `<path>: error: …` or `<path>: warning: …`. Exit 1 when any error was found, else 0 — a warning
(a wire end that reaches nothing) is reported and does not fail, because a picture may really draw one.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from jsonschema import Draft202012Validator
from PIL import Image

from .data import SCHEMA_PATH, DataHomeError, load_classes, resolve_data_home
from .values import read_value_text

LABEL_SUFFIX = ".mllabel.json"
DEFAULT_PEN = 2.0


@dataclass
class Finding:
    severity: str  # "error" | "warning"
    message: str


def _load_schema() -> Draft202012Validator:
    with open(SCHEMA_PATH, encoding="utf-8") as f:
        return Draft202012Validator(json.load(f))


def ink_mask(path: Path) -> np.ndarray:
    """Pixels that differ from the picture's background — the commonest colour on its border."""
    with Image.open(path) as img:
        rgb = np.asarray(img.convert("RGB"), dtype=np.int16)
    border = np.concatenate([rgb[0], rgb[-1], rgb[:, 0], rgb[:, -1]])
    quant = border // 16
    keys, counts = np.unique(quant[:, 0] * 256 + quant[:, 1] * 16 + quant[:, 2], return_counts=True)
    k = keys[np.argmax(counts)]
    bg = border[(quant[:, 0] * 256 + quant[:, 1] * 16 + quant[:, 2]) == k].mean(axis=0)
    return np.abs(rgb - bg).max(axis=2) > 48


def _near_ink(mask: np.ndarray, x: float, y: float, r: float) -> bool:
    h, w = mask.shape
    x0, x1 = max(0, int(math.floor(x - r))), min(w, int(math.ceil(x + r)) + 1)
    y0, y1 = max(0, int(math.floor(y - r))), min(h, int(math.ceil(y + r)) + 1)
    if x0 >= x1 or y0 >= y1:
        return False
    yy, xx = np.mgrid[y0:y1, x0:x1]
    disk = (xx - x) ** 2 + (yy - y) ** 2 <= r * r
    return bool(mask[y0:y1, x0:x1][disk].any())


def _dist_to_segment(p, a, b) -> float:
    (px, py), (ax, ay), (bx, by) = p, a, b
    dx, dy = bx - ax, by - ay
    L2 = dx * dx + dy * dy
    t = 0.0 if L2 == 0 else max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / L2))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def _dist_to_polyline(p, pts) -> float:
    return min(_dist_to_segment(p, pts[i], pts[i + 1]) for i in range(len(pts) - 1))


def check_label(label_path: Path, classes: dict | None = None, schema: Draft202012Validator | None = None) -> list[Finding]:
    classes = classes or load_classes()
    schema = schema or _load_schema()
    out: list[Finding] = []
    err = lambda m: out.append(Finding("error", m))
    warn = lambda m: out.append(Finding("warning", m))

    try:
        with open(label_path, encoding="utf-8") as f:
            label = json.load(f)
    except (OSError, json.JSONDecodeError) as e:
        return [Finding("error", f"not readable as JSON: {e}")]

    schema_errors = sorted(schema.iter_errors(label), key=lambda e: list(e.path))
    for e in schema_errors:
        where = "/".join(str(p) for p in e.path) or "(root)"
        err(f"schema: {where}: {e.message}")
    if schema_errors:
        return out

    by_name = {c["name"]: c for c in classes["classes"]}
    W, H = label["width"], label["height"]
    pens = [w["pen"] for w in label["wires"] if "pen" in w]
    pen = float(np.median(pens)) if pens else DEFAULT_PEN
    reach = max(pen, 1.5) + 1.0

    mask = None
    image_path = label_path.parent / label["image"]
    if not image_path.is_file():
        err(f"image {label['image']} not found beside the label")
    else:
        with Image.open(image_path) as img:
            if img.size != (W, H):
                err(f"the label says {W}×{H} and the picture is {img.size[0]}×{img.size[1]}")
        mask = ink_mask(image_path)

    # ids
    texts = {}
    for t in label["texts"]:
        if t["id"] in texts:
            err(f"text id {t['id']} is used twice")
        texts[t["id"]] = t
    part_ids = set()
    wire_ends = [w["points"][i] for w in label["wires"] for i in (0, -1)]

    pins_all = []
    for p in label["parts"]:
        pid = p["id"]
        if pid in part_ids:
            err(f"part id {pid} is used twice")
        part_ids.add(pid)
        cls = by_name.get(p["class"])
        if cls is None:
            err(f"part {pid}: class {p['class']} is not in classes.json")
            continue
        if "variant" in p and p["variant"] not in cls.get("variants", []):
            err(f"part {pid}: {p['class']} has no variant {p['variant']}")
        x0, y0, x1, y1 = p["body"]
        if not (x0 < x1 and y0 < y1):
            err(f"part {pid}: body {p['body']} is empty or inverted")
        names = [q["name"] for q in p["pins"]]
        if cls["pins"] == "perimeter":
            if len(names) < 2:
                err(f"part {pid}: a {p['class']} has pins round its perimeter, at least 2; this one has {len(names)}")
            if len(set(names)) != len(names):
                err(f"part {pid}: pin names repeat")
        else:
            want = [q["name"] for q in cls["pins"]]
            if len(names) != len(want):
                err(f"part {pid}: a {p['class']} has {len(want)} pin(s); this one has {len(names)}")
            elif sorted(names) != sorted(want):
                err(f"part {pid}: a {p['class']}'s pins are {', '.join(want)}; this one's are {', '.join(names)}")
        for q in p["pins"]:
            pins_all.append((q["x"], q["y"]))
            on_ink = mask is not None and _near_ink(mask, q["x"], q["y"], reach)
            on_end = any(math.hypot(q["x"] - e[0], q["y"] - e[1]) <= reach for e in wire_ends)
            if mask is not None and not on_ink and not on_end and not p.get("cut"):
                err(f"part {pid}: pin {q['name']} at ({q['x']:g}, {q['y']:g}) is more than a pen width from any ink or wire end")
        refs = ([p["designator"]] if p.get("designator") else []) + p.get("values", [])
        for ref in refs:
            if ref not in texts:
                err(f"part {pid}: text {ref} does not exist")
        for ref in p.get("values", []):
            if ref in texts and read_value_text(texts[ref]["string"]) is None:
                err(f"part {pid}: value text {ref} ({texts[ref]['string']!r}) does not read as a value")

    # wire ends: a pin, a junction, another wire, the picture's edge — or the edge of window chrome, where a screenshot's
    # canvas ends inside the picture
    junctions = label.get("junctions", [])
    wires = label["wires"]
    chrome = [a["box"] for a in label.get("annotations", []) if a["kind"] == "chrome"]

    def on_chrome(x: float, y: float) -> bool:
        return any(b[0] - reach <= x <= b[2] + reach and b[1] - reach <= y <= b[3] + reach for b in chrome)

    for wi, w in enumerate(wires):
        for end in (w["points"][0], w["points"][-1]):
            x, y = end
            if (min(x, y) <= reach or x >= W - 1 - reach or y >= H - 1 - reach or on_chrome(x, y)
                    or any(math.hypot(x - a, y - b) <= reach for a, b in pins_all)
                    or any(math.hypot(x - a, y - b) <= reach for a, b in junctions)
                    or any(_dist_to_polyline(end, o["points"]) <= reach for oi, o in enumerate(wires) if oi != wi)):
                continue
            warn(f"wire {wi}: its end at ({x:g}, {y:g}) reaches no pin, junction, other wire, picture edge or chrome")

    return out


def _labels_in(paths: list[Path]) -> list[Path]:
    found = []
    for p in paths:
        if p.is_dir():
            found.extend(sorted(p.rglob("*" + LABEL_SUFFIX)))
        else:
            found.append(p)
    return found


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m crfml.validate", description=__doc__.split("\n\n")[0])
    ap.add_argument("paths", nargs="*", help="labels, or folders searched for *.mllabel.json")
    ap.add_argument("--data", help="with no paths: check every label in this data home")
    args = ap.parse_args(argv)
    paths = [Path(p) for p in args.paths]
    if not paths:
        try:
            home = resolve_data_home(args.data, create=False)
        except DataHomeError as e:
            print(f"refused: {e}", file=sys.stderr)
            return 1
        paths = [home / "real", home / "synth"]
    classes, schema = load_classes(), _load_schema()
    labels = _labels_in([p for p in paths if p.exists()])
    errors = 0
    for lp in labels:
        for f in check_label(lp, classes, schema):
            print(f"{lp}: {f.severity}: {f.message}")
            errors += f.severity == "error"
    print(f"{len(labels)} label(s) checked, {errors} error(s).", file=sys.stderr)
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
