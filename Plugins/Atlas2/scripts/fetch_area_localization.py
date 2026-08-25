#!/usr/bin/env python3
"""Fetch zh-CN/zh-TW atlas map names from poe2db.tw Waystones#EndGameMaps.

    python3 Plugins/Atlas2/scripts/fetch_area_localization.py

Three pages (us/cn/tw), joined by slug. Map ids come from WorldAreaNames.tsv.
"""

from __future__ import annotations

import json
import re
import urllib.request
from html.parser import HTMLParser
from pathlib import Path

PLUGIN_DIR = Path(__file__).resolve().parents[1]
TSV = PLUGIN_DIR.parents[1] / "GameHelper" / "Data" / "WorldAreaNames.tsv"
OUT = PLUGIN_DIR / "json" / "area-localization.json"
USER_AGENT = "GameHelper-Atlas2/1.0 (+https://poe2db.tw)"
LOCALES = ("us", "cn", "tw")


class EndGameMapsParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.in_end = False
        self.depth = 0
        self.by_slug: dict[str, str] = {}
        self._href: str | None = None
        self._text: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        ad = dict(attrs)
        if tag == "div" and ad.get("id") == "EndGameMaps":
            self.in_end = True
            self.depth = 1
            return
        if not self.in_end:
            return
        if tag in ("div", "section", "ul", "li"):
            self.depth += 1
        if tag == "a" and "WorldAreas" in (ad.get("class") or ""):
            self._href = ad.get("href") or ""
            self._text = []

    def handle_endtag(self, tag: str) -> None:
        if not self.in_end:
            return
        if tag == "a" and self._href is not None:
            name = re.sub(r"\s+", " ", "".join(self._text)).strip()
            slug = self._href.split("?", 1)[0].rstrip("/").split("/")[-1]
            if slug and name:
                self.by_slug.setdefault(slug, name)
            self._href = None
        if tag in ("div", "section", "ul", "li"):
            self.depth -= 1
            if self.depth <= 0:
                self.in_end = False

    def handle_data(self, data: str) -> None:
        if self._href is not None:
            self._text.append(data)


def fetch(url: str) -> str:
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Accept": "text/html"})
    with urllib.request.urlopen(req, timeout=30) as resp:
        return resp.read().decode("utf-8", errors="replace")


def parse_locale(locale: str) -> dict[str, str]:
    html = fetch(f"https://poe2db.tw/{locale}/Waystones")
    parser = EndGameMapsParser()
    parser.feed(html)
    parser.close()
    return parser.by_slug


def slug_from_name(name: str) -> str:
    name = name.replace("'", "").replace("’", "")
    return re.sub(r"[^\w]+", "_", name.strip()).strip("_")


def slug_from_id(internal_id: str) -> str:
    rest = internal_id[3:] if internal_id.startswith("Map") else internal_id
    return re.sub(r"([a-z])([A-Z])", r"\1_\2", rest)


def load_maps() -> list[tuple[str, str]]:
    rows = []
    for line in TSV.read_text(encoding="utf-8").splitlines():
        if not line or line.startswith("#") or "\t" not in line:
            continue
        id_, name = line.split("\t", 1)
        rows.append((id_, name.strip()))
    return rows


def main() -> None:
    by_locale = {loc: parse_locale(loc) for loc in LOCALES}
    print({loc: len(names) for loc, names in by_locale.items()})
    out: dict[str, dict[str, str]] = {}
    for internal_id, english in load_maps():
        slugs = []
        for s in (slug_from_name(english), slug_from_id(internal_id)):
            if s and s not in slugs:
                slugs.append(s)
        zh_cn = zh_tw = ""
        for slug in slugs:
            zh_cn = zh_cn or by_locale["cn"].get(slug, "")
            zh_tw = zh_tw or by_locale["tw"].get(slug, "")
        if not zh_cn and not zh_tw:
            continue
        rec = {}
        if zh_cn:
            rec["zh_CN"] = zh_cn
        if zh_tw:
            rec["zh_TW"] = zh_tw
        out[internal_id] = rec
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(out, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {len(out)} -> {OUT}")


if __name__ == "__main__":
    main()
