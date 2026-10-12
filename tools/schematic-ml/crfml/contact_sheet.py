"""Review sheets for labelled real pictures, and the review record (brief-iml-4 R-iml4-4, R-iml4-6).

    python -m crfml.contact_sheet --family family-c              one PNG per six pictures, in <data>/review/
    python -m crfml.contact_sheet --all                          every family
    python -m crfml.contact_sheet --mark family-c-1a2b3c4d5e ok   the reviewer's verdict: ok | redo [--note "…"]
    python -m crfml.contact_sheet --status                       what is labelled, valid, reviewed — per family
    python -m crfml.contact_sheet --summary                      the design note's labelling counts, as Markdown

Each row of a sheet is one picture: on the left the picture with its label drawn over it (parts by class, pins,
wires, junctions, crossings, words by role); on the right its netlist truth (``<id>.cnl`` from crfml.label_to_cnl)
drawn by circuitRF itself — ``circuitrf netlist --to-schematic`` then ``circuitrf render`` — so a wrong connection in
the label shows as a wrong circuit. The verdicts live in ``<data>/review.json``; a label is done when it validates
and its verdict is ``ok``. ``redo`` sends it back to annotation.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
import tempfile
from collections import Counter
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

from .data import DataHomeError, load_manifest, resolve_data_home
from .geometry import load_rgb

LABEL_SUFFIX = ".mllabel.json"
PER_SHEET = 6
ROW_H = 520
COL_W = 900

GROUP_COLOURS = {
    "two-pin": (230, 60, 60), "one-pin": (60, 160, 60), "source": (200, 120, 0), "transistor": (160, 60, 200),
    "block": (0, 140, 200), "box": (0, 110, 160), "line": (200, 0, 140), "annotation": (120, 120, 120),
}
ROLE_COLOURS = {
    "designator": (0, 170, 230), "value": (0, 170, 90), "net": (220, 170, 0), "port": (240, 110, 0),
    "pin-number": (150, 150, 150), "pin-name": (150, 100, 200), "title": (90, 90, 90), "other": (170, 170, 170),
}
WIRE = (255, 0, 200)


def _font(size: int) -> ImageFont.ImageFont:
    try:
        return ImageFont.load_default(size=size)
    except TypeError:
        return ImageFont.load_default()


def draw_overlay(picture: Path, label: dict, classes: dict) -> Image.Image:
    """The picture, paled, with its label drawn over it in the picture's own pixels."""
    rgb = Image.fromarray(load_rgb(picture))
    img = Image.blend(rgb, Image.new("RGB", rgb.size, (255, 255, 255)), 0.45)
    d = ImageDraw.Draw(img)
    scale = max(1.0, max(img.size) / 1400.0)
    lw = max(1, round(scale))
    font = _font(max(10, round(11 * scale)))
    groups = {c["name"]: c["group"] for c in classes["classes"]}

    for a in label.get("annotations", []):
        x0, y0, x1, y1 = a["box"]
        _dashed_box(d, (x0, y0, x1, y1), (120, 120, 120), lw)
        d.text((x0 + 2, y0 + 1), a["kind"], fill=(120, 120, 120), font=font)
    for b in label.get("ignore", []):
        d.rectangle(b, outline=(0, 0, 0), width=lw)
        d.line([(b[0], b[1]), (b[2], b[3])], fill=(0, 0, 0), width=lw)
    for w in label["wires"]:
        pts = [tuple(p) for p in w["points"]]
        d.line(pts, fill=WIRE, width=lw + 1)
        for p in (pts[0], pts[-1]):
            r = 2 * lw
            d.rectangle([p[0] - r, p[1] - r, p[0] + r, p[1] + r], outline=WIRE, width=lw)
    for x, y in label.get("junctions", []):
        r = 4 * lw
        d.ellipse([x - r, y - r, x + r, y + r], fill=(255, 0, 0))
    for x, y in label.get("crossings", []):
        r = 5 * lw
        d.line([(x - r, y - r), (x + r, y + r)], fill=(0, 0, 255), width=lw + 1)
        d.line([(x - r, y + r), (x + r, y - r)], fill=(0, 0, 255), width=lw + 1)
    for t in label["texts"]:
        c = ROLE_COLOURS.get(t["role"], (170, 170, 170))
        d.polygon([tuple(p) for p in t["quad"]], outline=c, width=lw)
    for p in label["parts"]:
        c = GROUP_COLOURS.get(groups.get(p["class"], ""), (0, 0, 0))
        x0, y0, x1, y1 = p["body"]
        d.rectangle([x0, y0, x1, y1], outline=c, width=lw + 1)
        o = p["orientation"]
        tag = p["class"] + (f".{p['variant']}" if p.get("variant") else "") + f" {o['turn']}" + ("m" if o["mirror"] else "")
        d.text((x0, max(0, y0 - 12 * scale)), tag, fill=c, font=font)
        for q in p["pins"]:
            r = 3 * lw
            d.ellipse([q["x"] - r, q["y"] - r, q["x"] + r, q["y"] + r], outline=c, width=lw + 1)
            d.text((q["x"] + r, q["y"] + r), q["name"], fill=c, font=font)
    return img


def _dashed_box(d: ImageDraw.ImageDraw, box, colour, lw: int, dash: int = 6) -> None:
    x0, y0, x1, y1 = box
    for (ax, ay), (bx, by) in (((x0, y0), (x1, y0)), ((x1, y0), (x1, y1)), ((x1, y1), (x0, y1)), ((x0, y1), (x0, y0))):
        n = max(1, int(math.hypot(bx - ax, by - ay) / dash))
        for i in range(0, n, 2):
            t0, t1 = i / n, min(1.0, (i + 1) / n)
            d.line([(ax + (bx - ax) * t0, ay + (by - ay) * t0), (ax + (bx - ax) * t1, ay + (by - ay) * t1)], fill=colour, width=lw)


def render_netlist(cnl: Path, work: Path) -> Image.Image | None:
    """circuitRF's own drawing of a netlist: ``netlist --to-schematic`` then ``render``. None when either refuses."""
    from .cli import run
    csch, png = work / (cnl.stem + ".csch"), work / (cnl.stem + ".png")
    if run(["netlist", cnl, "--to-schematic", "-o", csch]).returncode != 0:
        return None
    if run(["render", csch, "-o", png]).returncode != 0 or not png.is_file():
        return None
    with Image.open(png) as im:
        return im.convert("RGB")


def _fit(img: Image.Image, w: int, h: int) -> Image.Image:
    img = img.copy()
    img.thumbnail((w, h), Image.LANCZOS)
    return img


# ── the review record ───────────────────────────────────────────────────────────────────────────────────────────────

def review_path(home: Path) -> Path:
    return home / "review.json"


def load_review(home: Path) -> dict:
    p = review_path(home)
    if p.is_file():
        return json.loads(p.read_text(encoding="utf-8"))
    return {"version": 1, "about": "Each real picture's review: verdict ok | redo | null, a note, and the parts the class list lacks.",
            "pictures": {}}


def save_review(home: Path, review: dict) -> None:
    p = review_path(home)
    tmp = p.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(review, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    tmp.replace(p)


def _labelled(home: Path, family: str | None) -> list[tuple[str, Path]]:
    out = []
    for lp in sorted((home / "real").rglob("*" + LABEL_SUFFIX)):
        if lp.name.endswith(".draft" + LABEL_SUFFIX):
            continue
        pid = lp.name[: -len(LABEL_SUFFIX)]
        if family is None or lp.parent.name == family:
            out.append((pid, lp))
    return out


def sheets(home: Path, family: str | None, classes: dict) -> list[Path]:
    from .label_to_cnl import target_for, write
    from .validate import check_label
    review = load_review(home)
    out_dir = home / "review"
    out_dir.mkdir(exist_ok=True)
    written = []
    items = _labelled(home, family)
    by_family: dict[str, list] = {}
    for pid, lp in items:
        by_family.setdefault(lp.parent.name, []).append((pid, lp))
    font = _font(20)
    with tempfile.TemporaryDirectory() as td:
        work = Path(td)
        for fam, rows in sorted(by_family.items()):
            for page in range(0, len(rows), PER_SHEET):
                chunk = rows[page: page + PER_SHEET]
                sheet = Image.new("RGB", (2 * COL_W + 30, len(chunk) * (ROW_H + 40) + 10), (255, 255, 255))
                d = ImageDraw.Draw(sheet)
                for r, (pid, lp) in enumerate(chunk):
                    label = json.loads(lp.read_text(encoding="utf-8"))
                    picture = lp.parent / label["image"]
                    errors = [f for f in check_label(lp, classes) if f.severity == "error"]
                    write(lp, classes)
                    cnl = target_for(lp)
                    y = r * (ROW_H + 40) + 10
                    verdict = review["pictures"].get(pid, {}).get("verdict")
                    head = f"{pid}   parts {len(label['parts'])}  wires {len(label['wires'])}  words {len(label['texts'])}"
                    head += f"   {'VALID' if not errors else f'{len(errors)} ERROR(S)'}   verdict: {verdict or '—'}"
                    d.text((10, y), head, fill=(0, 0, 0) if not errors else (200, 0, 0), font=font)
                    left = _fit(draw_overlay(picture, label, classes), COL_W, ROW_H)
                    sheet.paste(left, (10, y + 30))
                    right = render_netlist(cnl, work)
                    if right is not None:
                        sheet.paste(_fit(right, COL_W, ROW_H), (COL_W + 20, y + 30))
                    else:
                        d.text((COL_W + 30, y + 60), "circuitRF could not draw this netlist", fill=(200, 0, 0), font=font)
                    d.line([(0, y + ROW_H + 35), (sheet.width, y + ROW_H + 35)], fill=(200, 200, 200), width=1)
                p = out_dir / f"{fam}-sheet-{page // PER_SHEET + 1}.png"
                sheet.save(p)
                written.append(p)
    return written


def status(home: Path, classes: dict) -> str:
    from .validate import check_label
    manifest = load_manifest(home)
    review = load_review(home)
    rows = Counter()
    lines = ["family      pictures  labelled  valid  ok  redo"]
    fams = sorted({p["family"] for p in manifest["pictures"]})
    for fam in fams:
        pics = [p for p in manifest["pictures"] if p["family"] == fam]
        lab = valid = ok = redo = 0
        for p in pics:
            lp = home / "real" / fam / f"{p['id']}{LABEL_SUFFIX}"
            if not lp.is_file():
                continue
            lab += 1
            valid += not any(f.severity == "error" for f in check_label(lp, classes))
            v = review["pictures"].get(p["id"], {}).get("verdict")
            ok += v == "ok"
            redo += v == "redo"
        rows[fam] = len(pics)
        lines.append(f"{fam:<11} {len(pics):>8}  {lab:>8}  {valid:>5}  {ok:>2}  {redo:>4}")
    return "\n".join(lines)


def summary(home: Path) -> str:
    """The labelling counts the design note §6 records: ids and counts only."""
    manifest = load_manifest(home)
    split = {p["id"]: p["split"] for p in manifest["pictures"]}
    fam_rows: dict[str, Counter] = {}
    classes, roles = Counter(), Counter()
    class_split, role_split = Counter(), Counter()
    for lp in _labelled(home, None):
        pid, path = lp
        label = json.loads(path.read_text(encoding="utf-8"))
        s = split.get(pid, "?")
        fam = path.parent.name
        c = fam_rows.setdefault(fam, Counter())
        c["pictures"] += 1
        c[s] += 1
        c["parts"] += len(label["parts"])
        c["words"] += len(label["texts"])
        c["wires"] += len(label["wires"])
        for p in label["parts"]:
            classes[p["class"]] += 1
            class_split[(p["class"], s)] += 1
        for t in label["texts"]:
            roles[t["role"]] += 1
            role_split[(t["role"], s)] += 1
    out = ["| family | pictures | train | test | parts | wires | words |", "|---|---:|---:|---:|---:|---:|---:|"]
    tot = Counter()
    for fam, c in sorted(fam_rows.items()):
        out.append(f"| {fam} | {c['pictures']} | {c['train']} | {c['test']} | {c['parts']} | {c['wires']} | {c['words']} |")
        tot.update(c)
    out.append(f"| **all** | {tot['pictures']} | {tot['train']} | {tot['test']} | {tot['parts']} | {tot['wires']} | {tot['words']} |")
    out += ["", "| class | parts | train | test |", "|---|---:|---:|---:|"]
    for k, n in sorted(classes.items(), key=lambda kv: (-kv[1], kv[0])):
        out.append(f"| `{k}` | {n} | {class_split[(k, 'train')]} | {class_split[(k, 'test')]} |")
    out += ["", "| role | words | train | test |", "|---|---:|---:|---:|"]
    for k, n in sorted(roles.items(), key=lambda kv: (-kv[1], kv[0])):
        out.append(f"| `{k}` | {n} | {role_split[(k, 'train')]} | {role_split[(k, 'test')]} |")
    return "\n".join(out)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m crfml.contact_sheet", description=__doc__.split("\n\n")[0])
    ap.add_argument("--data", help="the data home (default $CRF_ML_DATA, else ~/circuitRF-ml-data)")
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--family", help="draw the sheets of one family")
    g.add_argument("--all", action="store_true", help="draw every family's sheets")
    g.add_argument("--mark", nargs=2, metavar=("ID", "VERDICT"), help="record a verdict: ok or redo")
    g.add_argument("--status", action="store_true", help="labelled / valid / reviewed, per family")
    g.add_argument("--summary", action="store_true", help="the labelling counts for the design note, as Markdown")
    ap.add_argument("--note", help="with --mark: what is wrong, or a part the class list lacks")
    args = ap.parse_args(argv)
    try:
        home = resolve_data_home(args.data, create=False)
    except DataHomeError as e:
        print(f"refused: {e}", file=sys.stderr)
        return 1
    from .data import load_classes
    classes = load_classes()
    if args.mark:
        pid, verdict = args.mark
        if verdict not in ("ok", "redo"):
            print("refused: a verdict is ok or redo", file=sys.stderr)
            return 1
        review = load_review(home)
        rec = review["pictures"].setdefault(pid, {})
        rec["verdict"] = verdict
        if args.note:
            rec["note"] = args.note
        save_review(home, review)
        print(f"{pid}: {verdict}")
        return 0
    if args.status:
        print(status(home, classes))
        return 0
    if args.summary:
        print(summary(home))
        return 0
    for p in sheets(home, None if args.all else args.family, classes):
        print(p)
    return 0


if __name__ == "__main__":
    sys.exit(main())
