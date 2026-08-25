#!/usr/bin/env python3
"""Fetch English / zh-CN / zh-TW item names from poe2db.tw into item-localization.json.

    python3 Plugins/LootValue/scripts/fetch_item_localization.py

Join key is the page slug (Headhunter, Chaos_Orb), which is identical across
/us/ /cn/ /tw/. No Trade API, no other repo.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import time
import urllib.error
import urllib.request
from html.parser import HTMLParser
from pathlib import Path

PLUGIN_DIR = Path(__file__).resolve().parents[1]
DEFAULT_OUT = PLUGIN_DIR / "item-localization.json"
USER_AGENT = "GameHelper-LootValue/1.0 (+https://poe2db.tw)"
WHITESPACE_RE = re.compile(r"\s+")

# Category pages that list items. Add slugs here when poe2db grows new groups.
CATEGORY_SLUGS = (
    "Unique_item",
    "Stackable_Currency",
    "Augment",
    "Omen",
    "Incubators",
    "Liquid_Emotions",
    "Essence",
    "Splinter",
    "Catalysts",
    "Map_Fragments",
    "Inscribed_Ultimatum",
    "Trial_Coins",
    "Pinnacle_Keys",
    "Jewels",
    "Vault_Keys",
    "Relics",
    "Strongbox",
    "Life_Flasks",
    "Mana_Flasks",
    "Charms",
    "Gem",
    "Skill_Gems",
    "Support_Gems",
    "Meta_Skill_Gem",
    "Spirit_Gems",
    "Lineage_Supports",
    "Waystones",
    "Tablet",
    "Hideout",
    "Quest",
)

LOCALES = ("us", "cn", "tw")


def slug_from_href(href: str) -> str:
    href = (href or "").strip()
    if not href or href.startswith(("?", "http://", "https://", "#")):
        return ""
    path = href.split("?", 1)[0].rstrip("/")
    parts = [p for p in path.split("/") if p]
    if parts and parts[0] in LOCALES:
        parts = parts[1:]
    return parts[-1] if parts else ""


def is_item_anchor(classes: str, hover: str) -> bool:
    cls = classes.lower()
    if "uniqueitem" in cls or "uniqueitems" in cls:
        return True
    if "item_currency" in cls or "whiteitem" in cls or "gemitem" in cls:
        return True
    return "BaseItemTypes" in hover or "UniqueItems" in hover


class ItemListParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.by_slug: dict[str, dict[str, str]] = {}
        self._slug: str | None = None
        self._unique_name: list[str] = []
        self._unique_type: list[str] = []
        self._text: list[str] = []
        self._span: str | None = None

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        ad = dict(attrs)
        if tag == "a" and self._slug is None:
            if not is_item_anchor(ad.get("class") or "", ad.get("data-hover") or ""):
                return
            slug = slug_from_href(ad.get("href") or "")
            if not slug:
                return
            self._slug = slug
            self._unique_name = []
            self._unique_type = []
            self._text = []
            self._span = None
            return
        if tag == "span" and self._slug is not None:
            cls = ad.get("class") or ""
            if "uniqueName" in cls:
                self._span = "name"
            elif "uniqueTypeLine" in cls:
                self._span = "type"

    def handle_data(self, data: str) -> None:
        if self._slug is None:
            return
        if self._span == "name":
            self._unique_name.append(data)
        elif self._span == "type":
            self._unique_type.append(data)
        else:
            self._text.append(data)

    def handle_endtag(self, tag: str) -> None:
        if tag == "span":
            self._span = None
            return
        if tag != "a" or self._slug is None:
            return
        name = WHITESPACE_RE.sub(" ", "".join(self._unique_name)).strip()
        typ = WHITESPACE_RE.sub(" ", "".join(self._unique_type)).strip()
        text = WHITESPACE_RE.sub(" ", "".join(self._text)).strip()
        if not name:
            name = text
        if name:
            rec = self.by_slug.setdefault(self._slug, {})
            rec["name"] = name
            if typ:
                rec["type"] = typ
                rec["full"] = f"{name} {typ}"
        self._slug = None


def fetch(url: str, timeout: int) -> str:
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Accept": "text/html"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read().decode("utf-8", errors="replace")


def parse_page(html: str) -> dict[str, dict[str, str]]:
    parser = ItemListParser()
    parser.feed(html)
    parser.close()
    return parser.by_slug


def merge_locale(store: dict[str, dict[str, dict[str, str]]], locale: str, items: dict[str, dict[str, str]]) -> None:
    for slug, rec in items.items():
        store.setdefault(slug, {})[locale] = rec


def build_json(store: dict[str, dict[str, dict[str, str]]]) -> dict[str, dict[str, str]]:
    out: dict[str, dict[str, str]] = {}

    def put(english: str, zh_cn: str, zh_tw: str) -> None:
        english = english.strip()
        zh_cn = (zh_cn or english).strip()
        zh_tw = (zh_tw or english).strip()
        if not english:
            return
        if zh_cn == english and zh_tw == english:
            return
        out[english] = {"zh_CN": zh_cn, "zh_TW": zh_tw}

    for locs in store.values():
        us = locs.get("us") or {}
        cn = locs.get("cn") or {}
        tw = locs.get("tw") or {}
        put(us.get("name", ""), cn.get("name", ""), tw.get("name", ""))
        put(us.get("full", ""), cn.get("full", ""), tw.get("full", ""))
    return dict(sorted(out.items()))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--delay", type=float, default=0.25)
    ap.add_argument("--timeout", type=int, default=30)
    ap.add_argument("--slugs", nargs="*", default=list(CATEGORY_SLUGS))
    args = ap.parse_args()

    store: dict[str, dict[str, dict[str, str]]] = {}
    failures = 0
    total = len(args.slugs) * len(LOCALES)
    n = 0
    for slug in args.slugs:
        for locale in LOCALES:
            n += 1
            url = f"https://poe2db.tw/{locale}/{slug}"
            try:
                html = fetch(url, args.timeout)
                items = parse_page(html)
                merge_locale(store, locale, items)
                print(f"[{n}/{total}] {locale}/{slug}  {len(items)} items", flush=True)
            except (urllib.error.URLError, TimeoutError, OSError) as ex:
                failures += 1
                print(f"[{n}/{total}] FAIL {url}: {ex}", file=sys.stderr, flush=True)
            if args.delay:
                time.sleep(args.delay)

    items = build_json(store)
    if "Chaos Orb" not in items or "Headhunter" not in items:
        print("sanity check failed: expected Chaos Orb and Headhunter", file=sys.stderr)
        return 1

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(items, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(f"wrote {len(items)} names ({len(store)} slugs) -> {args.out}")
    if failures:
        print(f"warning: {failures} page(s) failed", file=sys.stderr)
    return 0 if items else 1


if __name__ == "__main__":
    sys.exit(main())
