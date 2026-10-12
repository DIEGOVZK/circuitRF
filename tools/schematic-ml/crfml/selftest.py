"""The training kit's self-test (brief-iml-1 §3). Needs no data home, no GPU and no network.

    python -m crfml.selftest

Not part of `dotnet test` (the routine gate runs no Python, L9). Run it after changing anything under
tools/schematic-ml/ — the class list, the label schema, units.json, the importer or the validator.
"""

from __future__ import annotations

import contextlib
import copy
import io
import hashlib
import json
import random
import re
import sys
import tempfile
import traceback
from pathlib import Path

from PIL import Image

from . import importer, values
from .data import CLASSES_PATH, DataHomeError, load_classes, load_manifest, repo_root, resolve_data_home
from .fixtures import FIXTURES
from .validate import LABEL_SUFFIX, check_label

FAILURES: list[str] = []


def check(cond: bool, what: str) -> None:
    if not cond:
        FAILURES.append(what)


# ── the class list ──────────────────────────────────────────────────────────────────────────────────────────────────

def test_classes() -> None:
    doc = load_classes()
    names = [c["name"] for c in doc["classes"]]
    check(len(names) == len(set(names)), "class names are unique")
    for c in doc["classes"]:
        n = c["name"]
        check(re.fullmatch(r"[a-z][a-z0-9]*(-[a-z0-9]+)*", n) is not None, f"{n} is kebab-case")
        check(c["symmetry"] in doc["symmetries"], f"{n}: symmetry {c['symmetry']} is in the vocabulary")
        check(isinstance(c["sizeReference"], bool), f"{n}: sizeReference is a boolean")
        cr = c["circuitrf"]
        check(cr == "ignore" or (isinstance(cr, dict) and isinstance(cr.get("kind"), str)), f"{n}: circuitrf is a kind or ignore")
        if c["pins"] != "perimeter":
            pins = [p["name"] for p in c["pins"]]
            check(len(pins) == len(set(pins)), f"{n}: pin names unique")
            for p in c["pins"]:
                x, y = p["at"]
                check(-0.5 <= x <= 1.5 and -0.5 <= y <= 1.5, f"{n}: pin {p['name']} near its body box")
    check(len(doc["charset"]) == len(set(doc["charset"])), "charset has no repeated character")
    for c in doc["classes"]:
        for p in ([] if c["pins"] == "perimeter" else c["pins"]):
            check(all(ch in doc["charset"] for ch in p["name"]), f"{c['name']}: pin {p['name']} is in the charset")


# ── the value grammar against units.json ────────────────────────────────────────────────────────────────────────────

def test_units() -> None:
    with open(values.UNITS_PATH, encoding="utf-8") as f:
        units = json.load(f)
    for ex in units["examples"]:
        text = ex["text"]
        got = values.parse_value_word(text)
        if "line" in ex:
            check(isinstance(got, list) and [(p.key, p.dim) for p in got] == [(k, d) for k, _, d in ex["line"]]
                  and all(abs(p.si - si) <= 1e-12 * max(1, abs(si)) for p, (_, si, _) in zip(got, ex["line"])),
                  f"units example {text!r} reads as its line parameters (got {got})")
        else:
            ok = isinstance(got, values.Value) and abs(got.si - ex["si"]) <= 1e-9 * max(1e-30, abs(ex["si"])) + 1e-30 \
                 and list(got.dims) == ex["dims"] and got.not_fitted == ex.get("notFitted", False)
            check(ok, f"units example {text!r} reads as {ex['si']} {ex['dims']} (got {got})")
    for text in units["rejects"]:
        check(values.parse_value_word(text) is None, f"{text!r} is not a value")
    # circuitRF draws a shown parameter as `Name = value unit`; a label's value text is read with its spaces closed up.
    for text in ("R = 50 Ohm", "C = 2.62 pF", "L = 0.56 nH", "Z = 50 Ω", "W = 0.5 mm", "F = 2.4 GHz", "R = 4.7 kΩ"):
        check(values.read_value_text(text) is not None, f"circuitRF's own spelling {text!r} reads as a value")
    for text in ("Num = 1", 'File = "amp.s2p"'):
        check(values.read_value_text(text) is None, f"{text!r} is not a value")


# ── the fixtures, and the validator's findings on broken copies of one ──────────────────────────────────────────────

def test_fixtures() -> None:
    labels = sorted(FIXTURES.glob("*" + LABEL_SUFFIX))
    check(len(labels) == 3, f"three fixtures (found {len(labels)})")
    for lp in labels:
        findings = check_label(lp)
        check(not findings, f"{lp.name} validates clean: " + "; ".join(f.message for f in findings))


def _mutated(base: dict, mutate, tmp: Path, name: str) -> list:
    label = copy.deepcopy(base)
    mutate(label)
    p = tmp / f"{name}{LABEL_SUFFIX}"
    p.write_text(json.dumps(label, ensure_ascii=False), encoding="utf-8")
    (tmp / label.get("image", "divider.png")).write_bytes((FIXTURES / "divider.png").read_bytes())
    return check_label(p)


def test_validator_findings() -> None:
    base = json.loads((FIXTURES / f"divider{LABEL_SUFFIX}").read_text(encoding="utf-8"))
    cases = [
        ("unknown-class", lambda l: l["parts"][1].update({"class": "resistr"}), "error", "not in classes.json"),
        ("pin-count", lambda l: l["parts"][1]["pins"].pop(), "error", "has 2 pin(s)"),
        ("pin-names", lambda l: l["parts"][1]["pins"][0].update({"name": "x"}), "error", "pins are"),
        ("pin-off-ink", lambda l: l["parts"][1]["pins"][0].update({"x": 180, "y": 185}), "error", "more than a pen width"),
        ("missing-text", lambda l: l["parts"][1].update({"designator": "t99"}), "error", "does not exist"),
        ("bad-value", lambda l: l["texts"][1].update({"string": "abc"}), "error", "does not read as a value"),
        ("bad-variant", lambda l: l["parts"][1].update({"variant": "zener"}), "error", "has no variant"),
        ("schema", lambda l: l.pop("texts"), "error", "schema:"),
        ("size", lambda l: l.update({"width": 201}), "error", "the label says"),
        ("dangling", lambda l: l["wires"].append({"points": [[120, 150], [120, 170]], "pen": 2}), "warning", "reaches no pin"),
    ]
    clean = [
        # an unreadable value keeps its role with no string (brief-iml-4 R-iml4-5)
        ("unread-value", lambda l: l["texts"][1].update({"string": None})),
    ]
    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)
        for name, mutate, severity, phrase in cases:
            findings = _mutated(base, mutate, tmp, name)
            check(any(f.severity == severity and phrase in f.message for f in findings),
                  f"validator: {name} gives a {severity} saying {phrase!r} (got {[f.message for f in findings]})")
        for name, mutate in clean:
            findings = _mutated(base, mutate, tmp, name)
            check(not findings, f"validator: {name} validates clean (got {[f.message for f in findings]})")


# ── the netlist truth ──────────────────────────────────────────────────────────────────────────────────────────────

def test_label_to_cnl() -> None:
    from .label_to_cnl import convert
    for lp in sorted(FIXTURES.glob("*" + LABEL_SUFFIX)):
        name = lp.name[: -len(LABEL_SUFFIX)]
        committed = (FIXTURES / f"{name}.cnl").read_text(encoding="utf-8")
        got = convert(json.loads(lp.read_text(encoding="utf-8")), name=name)
        check(got == committed, f"label_to_cnl: {name}.cnl is what its label gives (rerun crfml.label_to_cnl on the fixtures)")


# ── the importer ────────────────────────────────────────────────────────────────────────────────────────────────────

def _picture(path: Path, seed: int, fmt: str) -> bytes:
    rnd = random.Random(seed)
    img = Image.new("RGB", (40 + seed % 7, 30 + seed % 5), (255, 255, 255))
    for _ in range(60):
        img.putpixel((rnd.randrange(img.width), rnd.randrange(img.height)), (0, 0, 0))
    path.parent.mkdir(parents=True, exist_ok=True)
    img.save(path, format=fmt)
    return path.read_bytes()


def test_importer() -> None:
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        src, home = root / "incoming", root / "data"
        names = []
        def pic(rel, seed, fmt):
            names.append(Path(rel).stem)
            return _picture(src / rel, seed, fmt)
        pic("Zeta Tool/zeta-shot-1.png", 1, "PNG")
        pic("Zeta Tool/zeta-shot-2.png", 2, "PNG")
        pic("Zeta Tool/deeper/zeta-shot-3.jpg", 3, "JPEG")
        pic("alpha editor/alpha-a.webp", 4, "WEBP")
        pic("alpha editor/alpha-b.gif", 5, "GIF")
        pic("alpha editor/alpha-c.bmp", 6, "BMP")
        dup = (src / "Zeta Tool/zeta-shot-1.png").read_bytes()
        (src / "alpha editor/alpha-copy.png").write_bytes(dup)
        pic("loose-top.png", 7, "PNG")
        (src / "notes.txt").write_text("not a picture")

        check(importer.main([str(src), "--data", str(home)]) == 0, "importer exits 0")
        m = load_manifest(home)
        fams = {f: rec["sources"][0]["path"] for f, rec in m["families"].items()}
        check(set(fams) == {"family-a", "family-b", "family-c"}, f"three families (got {sorted(fams)})")
        check(fams.get("family-a", "").endswith("alpha editor") and fams.get("family-b", "").endswith("Zeta Tool"),
              "sub-folders take letters in name order")
        check(m["families"].get("family-c", {}).get("sources", [{}])[0].get("scope") == "top-level",
              "loose top-level pictures are their own family")
        pics = m["pictures"]
        check(len(pics) == 7, f"seven pictures, the duplicate once (got {len(pics)})")
        for p in pics:
            check(re.fullmatch(rf"{p['family']}-[0-9a-f]{{10}}", p["id"]) is not None, f"{p['id']} is <family>-<hash>")
            check(p["id"].endswith(p["sha256"][:10]), f"{p['id']} carries its own hash")
            f = home / p["file"]
            check(f.is_file(), f"{p['file']} exists")
            with Image.open(f) as img:
                check(list(img.size) == p["size"], f"{p['id']} size recorded")
                check(img.format in ("PNG", "JPEG"), f"{p['id']} stored as PNG or JPEG")
                check((img.format == "JPEG") == (f.suffix == ".jpg"), f"{p['id']} extension matches its format")
            if p["splitRule"] == "hash":
                check(p["split"] == importer.hash_split(p["sha256"]), f"{p['id']} split is the hash rule")
        check(sum(p["file"].endswith(".jpg") for p in pics) == 1, "the JPEG stays a JPEG")
        for fam in fams:
            mem = [p for p in pics if p["family"] == fam]
            check(sum(p["split"] == "test" for p in mem) >= min(2, len(mem) // 2), f"{fam} has its test pictures")
        # no original file name anywhere in the data home, in a file name or inside a file
        everything = [str(p.relative_to(home)) for p in home.rglob("*")]
        texts = (home / "manifest.json").read_text(encoding="utf-8")
        for n in names:
            check(not any(n in e for e in everything), f"file name {n} not used in the data home")
            check(n not in texts, f"file name {n} not written to the manifest")

        # a second import adds nothing and moves no split
        before = {p["id"]: p["split"] for p in pics}
        check(importer.main([str(src), "--data", str(home)]) == 0, "re-import exits 0")
        m2 = load_manifest(home)
        check({p["id"]: p["split"] for p in m2["pictures"]} == before, "re-import changes nothing")
        # a new folder takes the next letter; an existing folder's new picture joins its family
        pic("Mid Tool/mid-1.png", 8, "PNG")
        pic("Zeta Tool/zeta-shot-4.png", 9, "PNG")
        importer.main([str(src), "--data", str(home)])
        m3 = load_manifest(home)
        check("family-d" in m3["families"], "a new folder takes the next letter")
        check({p["id"]: p["split"] for p in m3["pictures"] if p["id"] in before} == before, "old splits never move")
        z4 = hashlib.sha256((src / "Zeta Tool/zeta-shot-4.png").read_bytes()).hexdigest()
        check(any(p["sha256"] == z4 and p["family"] == "family-b" for p in m3["pictures"]),
              "a later picture in a known folder joins that folder's family")

        # --family forces one family
        pic("extra/x1.png", 10, "PNG")
        importer.main([str(src / "extra"), "--family", "family-b", "--data", str(home)])
        x1 = hashlib.sha256((src / "extra/x1.png").read_bytes()).hexdigest()
        check(any(p["sha256"] == x1 and p["family"] == "family-b" for p in load_manifest(home)["pictures"]),
              "--family puts the pictures in that family")

        # the corpus: every picture test, its netlist truth beside it
        corpus = root / "image-sources"
        _picture(corpus / "picture-ds-a1/picture.png", 20, "PNG")
        (corpus / "picture-ds-a1/answer.cnl").write_text("R:R1 a 0 R=1 Ohm\n")
        _picture(corpus / "picture-z/picture.png", 21, "PNG")
        (corpus / "picture-z/answer.cnl").write_text("C:C1 a 0 C=1 pF\n")
        importer.main(["--from-corpus", str(corpus), "--data", str(home)])
        cps = [p for p in load_manifest(home)["pictures"] if p["family"].startswith("corpus-")]
        check(sorted(p["family"] for p in cps) == ["corpus-ds", "corpus-web"], "corpus families by folder kind")
        check(all(p["split"] == "test" for p in cps), "every corpus picture is test")
        check(all((home / p["file"]).with_suffix(".cnl").is_file() for p in cps), "corpus netlist truth copied")

    # a data home inside the repository is refused
    root = repo_root()
    if root is not None:
        try:
            resolve_data_home(str(root / "circuitRF-ml-data"), create=False)
            check(False, "a data home inside the repository is refused")
        except DataHomeError:
            pass


def main() -> int:
    tests = [test_classes, test_units, test_fixtures, test_validator_findings, test_label_to_cnl, test_importer]
    for t in tests:
        n = len(FAILURES)
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                t()
        except Exception:
            FAILURES.append(f"{t.__name__} raised:\n{traceback.format_exc()}")
        print(f"{'ok  ' if len(FAILURES) == n else 'FAIL'} {t.__name__}")
    for f in FAILURES:
        print(f"  - {f}")
    print(f"{len(tests)} groups, {len(FAILURES)} failure(s)")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
