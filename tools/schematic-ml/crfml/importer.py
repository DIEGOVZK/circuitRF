"""Import real schematic pictures into the data home (brief-iml-1 R-iml1-2).

    python -m crfml.importer <folder> [--family <id>] [--data <dir>]
    python -m crfml.importer --from-corpus <testdata/image-sources> [--data <dir>]

Each top-level sub-folder of <folder> becomes one family (family-a, family-b, … in order of first import; a later
import of a new folder takes the next letter), and the loose pictures at its top level become one family of their own.
A picture is renamed <family>-<first 10 hex of its sha256>. The original FOLDER is recorded only in the data home's
manifest.json; the original FILE name is kept nowhere, because many name a tool or a part. Nothing here prints either.

The split is decided once, here: a picture is `test` when int(sha256[:8], 16) % 4 == 0, then each family touched by
this import is topped up to min(2, n // 2) test pictures from this import's own new pictures, lowest hash first. A
split already in the manifest is never recomputed.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import re
import shutil
import sys
from pathlib import Path

from PIL import Image

from .data import DataHomeError, load_manifest, resolve_data_home, save_manifest

PICTURE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"}
FAMILY_ID = re.compile(r"^[a-z0-9]+(-[a-z0-9]+)*$")


def family_letters(index: int) -> str:
    """0 → a, 25 → z, 26 → aa."""
    s = ""
    index += 1
    while index:
        index, r = divmod(index - 1, 26)
        s = chr(ord("a") + r) + s
    return s


def hash_split(sha256: str) -> str:
    return "test" if int(sha256[:8], 16) % 4 == 0 else "train"


def _pictures_under(folder: Path, recursive: bool) -> list[Path]:
    it = folder.rglob("*") if recursive else folder.iterdir()
    files = [p for p in it if p.is_file() and not any(part.startswith(".") for part in p.relative_to(folder).parts)
             and p.suffix.lower() in PICTURE_EXTENSIONS]
    return sorted(files, key=lambda p: str(p.relative_to(folder)).lower())


def _stored_bytes(data: bytes) -> tuple[bytes, str, tuple[int, int]]:
    """What is stored and under which extension: PNG and JPEG as they came, everything else decoded to PNG."""
    with Image.open(io.BytesIO(data)) as img:
        img.load()
        size = img.size
        fmt = img.format
        if fmt == "PNG":
            return data, ".png", size
        if fmt == "JPEG":
            return data, ".jpg", size
        img.seek(0)
        frame = img.copy()
    if frame.mode not in ("1", "L", "LA", "P", "RGB", "RGBA"):
        frame = frame.convert("RGBA")
    buf = io.BytesIO()
    frame.save(buf, format="PNG")
    return buf.getvalue(), ".png", size


class Importer:
    def __init__(self, home: Path):
        self.home = home
        self.manifest = load_manifest(home)
        self.by_sha = {p["sha256"]: p for p in self.manifest["pictures"]}
        self.new: list[dict] = []
        self.duplicates = 0
        self.skipped: list[str] = []

    # ── families ────────────────────────────────────────────────────────────────────────────────────────────────

    def _family_for(self, source: dict) -> str | None:
        for fam, rec in self.manifest["families"].items():
            if source in rec.get("sources", []):
                return fam
        return None

    def _next_family(self) -> str:
        used = set(self.manifest["families"])
        i = 0
        while f"family-{family_letters(i)}" in used:
            i += 1
        return f"family-{family_letters(i)}"

    def _attach(self, family: str, source: dict) -> None:
        rec = self.manifest["families"].setdefault(family, {"sources": []})
        if source not in rec["sources"]:
            rec["sources"].append(source)

    # ── pictures ────────────────────────────────────────────────────────────────────────────────────────────────

    def import_group(self, files: list[Path], source: dict, family: str | None, *, forced_split: str | None = None,
                     truth_name: str | None = None) -> None:
        """Import one family's pictures. `family` None takes the source's family, or the next letter."""
        family = family or self._family_for(source)
        for path in files:
            data = path.read_bytes()
            sha = hashlib.sha256(data).hexdigest()
            if sha in self.by_sha:
                self.duplicates += 1
                continue
            try:
                stored, ext, size = _stored_bytes(data)
            except Exception:  # an unreadable file is reported by count, and by path only in the local log
                self.skipped.append(str(path))
                continue
            if family is None:
                family = self._next_family()
            self._attach(family, source)
            pid = f"{family}-{sha[:10]}"
            rel = Path("real") / family / f"{pid}{ext}"
            (self.home / rel).parent.mkdir(parents=True, exist_ok=True)
            (self.home / rel).write_bytes(stored)
            if truth_name and (truth := path.parent / truth_name).is_file():
                shutil.copyfile(truth, self.home / rel.with_suffix(".cnl"))
            record = {
                "id": pid, "family": family, "sha256": sha, "size": [size[0], size[1]],
                "split": forced_split or hash_split(sha), "splitRule": "corpus" if forced_split else "hash",
                "file": rel.as_posix(), "importedFrom": source["path"],
            }
            self.manifest["pictures"].append(record)
            self.by_sha[sha] = record
            self.new.append(record)

    def rebalance(self) -> None:
        """Top each family this import touched up to min(2, n // 2) test pictures, from its new pictures only."""
        for family in sorted({r["family"] for r in self.new}):
            members = [p for p in self.manifest["pictures"] if p["family"] == family]
            target = min(2, len(members) // 2)
            tests = sum(p["split"] == "test" for p in members)
            for rec in sorted((r for r in self.new if r["family"] == family and r["split"] == "train"),
                              key=lambda r: r["sha256"]):
                if tests >= target:
                    break
                rec["split"], rec["splitRule"] = "test", "balance"
                tests += 1

    def finish(self) -> None:
        self.rebalance()
        save_manifest(self.home, self.manifest)
        if self.skipped:
            with open(self.home / "import-log.txt", "a", encoding="utf-8") as f:
                for s in self.skipped:
                    f.write(f"unreadable, not imported: {s}\n")

    def report(self) -> str:
        lines = []
        fams = sorted({p["family"] for p in self.manifest["pictures"]})
        new_by_family = {}
        for r in self.new:
            new_by_family[r["family"]] = new_by_family.get(r["family"], 0) + 1
        lines.append(f"{'family':<14}{'pictures':>9}{'train':>7}{'test':>6}{'new':>6}")
        for fam in fams:
            m = [p for p in self.manifest["pictures"] if p["family"] == fam]
            lines.append(f"{fam:<14}{len(m):>9}{sum(p['split'] == 'train' for p in m):>7}"
                         f"{sum(p['split'] == 'test' for p in m):>6}{new_by_family.get(fam, 0):>6}")
        total = self.manifest["pictures"]
        lines.append(f"{'total':<14}{len(total):>9}{sum(p['split'] == 'train' for p in total):>7}"
                     f"{sum(p['split'] == 'test' for p in total):>6}{len(self.new):>6}")
        if self.duplicates:
            lines.append(f"{self.duplicates} picture(s) already imported (same content), not imported again.")
        if self.skipped:
            lines.append(f"{len(self.skipped)} file(s) could not be read as pictures; their paths are in "
                         f"{self.home / 'import-log.txt'}.")
        lines.append(f"manifest: {self.home / 'manifest.json'}")
        return "\n".join(lines)


def import_folder(imp: Importer, folder: Path, family: str | None) -> None:
    folder = folder.resolve()
    if family is not None:
        imp.import_group(_pictures_under(folder, True), {"path": str(folder), "scope": "all"}, family)
        return
    subfolders = sorted((d for d in folder.iterdir() if d.is_dir() and not d.name.startswith(".")),
                        key=lambda d: d.name.lower())
    for d in subfolders:
        imp.import_group(_pictures_under(d, True), {"path": str(d), "scope": "folder"}, None)
    imp.import_group(_pictures_under(folder, False), {"path": str(folder), "scope": "top-level"}, None)


def import_corpus(imp: Importer, corpus: Path) -> None:
    """testdata/image-sources: picture-ds-* → corpus-ds, every other picture-* → corpus-web; all test, with truth."""
    corpus = corpus.resolve()
    for d in sorted((d for d in corpus.iterdir() if d.is_dir() and (d / "picture.png").is_file()),
                    key=lambda d: d.name):
        family = "corpus-ds" if d.name.startswith("picture-ds-") else "corpus-web"
        imp.import_group([d / "picture.png"], {"path": str(corpus), "scope": "corpus"}, family,
                         forced_split="test", truth_name="answer.cnl")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m crfml.importer", description=__doc__.split("\n\n")[0])
    ap.add_argument("folder", nargs="?", help="a folder of pictures; each top-level sub-folder is one family")
    ap.add_argument("--family", help="put every picture of <folder> in this family (an existing id, or a new one)")
    ap.add_argument("--from-corpus", metavar="DIR", help="import testdata/image-sources as corpus-ds / corpus-web")
    ap.add_argument("--data", help="the data home (default $CRF_ML_DATA, else ~/circuitRF-ml-data)")
    args = ap.parse_args(argv)
    if not args.folder and not args.from_corpus:
        ap.error("give a folder to import, or --from-corpus")
    if args.family is not None and not FAMILY_ID.match(args.family):
        ap.error(f"--family must be a lower-case id such as family-c, not {args.family!r}")
    try:
        home = resolve_data_home(args.data)
    except DataHomeError as e:
        print(f"refused: {e}", file=sys.stderr)
        return 1
    imp = Importer(home)
    if args.folder:
        folder = Path(args.folder)
        if not folder.is_dir():
            print(f"refused: {folder} is not a folder", file=sys.stderr)
            return 1
        import_folder(imp, folder, args.family)
    if args.from_corpus:
        corpus = Path(args.from_corpus)
        if not corpus.is_dir():
            print(f"refused: {corpus} is not a folder", file=sys.stderr)
            return 1
        import_corpus(imp, corpus)
    imp.finish()
    print(imp.report())
    return 0


if __name__ == "__main__":
    sys.exit(main())
