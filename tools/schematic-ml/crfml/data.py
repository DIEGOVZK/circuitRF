"""The data home (brief-iml-1 R-iml1-1).

Every picture, label, generated batch, run and export lives under one folder outside the repository:
``--data`` if given, else ``$CRF_ML_DATA``, else ``~/circuitRF-ml-data``. Nothing under it is ever committed, and every
tool refuses a data home inside the repository's working tree — a ``git add -A`` there would publish the pictures.

Layout::

    manifest.json                 every real picture: id, family, sha256, size, split, imported-from (local only)
    real/<family>/<id>.<ext>      the picture, renamed; <id>.mllabel.json beside it once labelled
    synth/<generator>/<batch>/    generated pictures + labels — regenerable, safe to delete
    fonts/                        open-licensed fonts for generated text, with LICENSES/
    runs/<run-id>/                checkpoints, logs, metrics.json
    exports/<model>-<version>/    .onnx, .crfnn, probe, model card draft
"""

from __future__ import annotations

import json
import os
from pathlib import Path

KIT_DIR = Path(__file__).resolve().parent.parent
CLASSES_PATH = KIT_DIR / "classes.json"
SCHEMA_PATH = KIT_DIR / "schema" / "mllabel.schema.json"
MANIFEST_VERSION = 1
SUBDIRS = ("real", "synth", "fonts", "runs", "exports")


class DataHomeError(Exception):
    """A data home that must not be used; the message says why."""


def repo_root() -> Path | None:
    """The circuitRF working tree this kit sits in, or None when the kit was copied out of one."""
    for d in [KIT_DIR, *KIT_DIR.parents]:
        if (d / "circuitrf.slnx").is_file():
            return d
    return None


def resolve_data_home(arg: str | None = None, *, create: bool = True) -> Path:
    """The data home, refused when it lies inside the repository."""
    raw = arg or os.environ.get("CRF_ML_DATA") or str(Path.home() / "circuitRF-ml-data")
    home = Path(raw).expanduser().resolve()
    root = repo_root()
    if root is not None and (home == root or root in home.parents):
        raise DataHomeError(
            f"the data home {home} is inside the circuitRF repository; training data never enters the repository. "
            f"Point --data or CRF_ML_DATA outside {root}.")
    if create:
        home.mkdir(parents=True, exist_ok=True)
        for sub in SUBDIRS:
            (home / sub).mkdir(exist_ok=True)
    return home


def load_classes(path: Path = CLASSES_PATH) -> dict:
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def manifest_path(home: Path) -> Path:
    return home / "manifest.json"


def load_manifest(home: Path) -> dict:
    p = manifest_path(home)
    if not p.is_file():
        return {"version": MANIFEST_VERSION, "families": {}, "pictures": []}
    with open(p, encoding="utf-8") as f:
        return json.load(f)


def save_manifest(home: Path, manifest: dict) -> None:
    manifest["pictures"].sort(key=lambda r: (r["family"], r["id"]))
    p = manifest_path(home)
    tmp = p.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")
    os.replace(tmp, p)
