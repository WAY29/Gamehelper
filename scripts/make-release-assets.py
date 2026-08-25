#!/usr/bin/env python3
"""Build zip + manifest.json for scripts/publish.sh. Prints stage dir on stdout."""
from __future__ import annotations

import hashlib
import json
import shutil
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

EXCLUDE_DIR = {"configs"}
EXCLUDE_NAMES = {
    "update.config.json",
    "update.file-hashes.json",
    "update.state.json",
    "VERSION.txt",
    "VERTEILUNG-HINWEIS.txt",
    "imgui.ini",
    "price_cache.json",
    "prices.json",
    "github.config.json",
    "github.config.example.json",
}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def main() -> None:
    root = Path(sys.argv[1])
    version = sys.argv[2]
    notes = sys.argv[3:]
    publish = root / "publish"
    files: list[str] = []
    for p in sorted(publish.rglob("*")):
        if not p.is_file():
            continue
        rel = p.relative_to(publish).as_posix()
        parts = rel.split("/")
        if parts[0] in EXCLUDE_DIR or "config" in parts or p.name in EXCLUDE_NAMES or p.suffix == ".pdb":
            continue
        files.append(rel)

    hist_path = root / "changelog-history.json"
    hist = json.loads(hist_path.read_text(encoding="utf-8-sig")) if hist_path.exists() else {"releases": []}
    hist["releases"] = [r for r in hist.get("releases", []) if r.get("version") != version]
    published = datetime.now(timezone.utc).isoformat()
    hist["releases"].insert(0, {"version": version, "published": published, "changelog": notes})
    hist_path.write_text(json.dumps(hist, ensure_ascii=False, indent=4) + "\n", encoding="utf-8")
    (publish / "changelog-history.json").write_text(hist_path.read_text(encoding="utf-8"), encoding="utf-8")

    zip_name = f"GameHelper-{version}-full.zip"
    zip_path = Path("/tmp") / zip_name
    if zip_path.exists():
        zip_path.unlink()
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for rel in files:
            zf.write(publish / rel, rel)
        zf.write(publish / "changelog-history.json", "changelog-history.json")

    digest = sha256(zip_path)
    manifest = {
        "version": version,
        "published": published,
        "changelog": notes,
        "distribution": "zip",
        "remove": [
            "Plugins\\RuneforgeHelper",
            "Plugins\\FarmTracker",
            "Plugins\\MapKillCounter",
            "Plugins\\StashValue",
        ],
        "package": {"name": zip_name, "hash": digest, "size": zip_path.stat().st_size},
        "files": [{"path": rel, "hash": sha256(publish / rel)} for rel in files],
    }
    stage = Path("/tmp") / f"gamehelper-github-release-v{version}"
    if stage.exists():
        shutil.rmtree(stage)
    stage.mkdir()
    (stage / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    shutil.copy2(zip_path, stage / zip_name)
    shutil.copy2(publish / "changelog-history.json", stage / "changelog-history.json")
    print(stage)


if __name__ == "__main__":
    main()
