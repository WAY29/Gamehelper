#!/usr/bin/env python3
"""Fetch English / zh-CN / zh-TW tablet affix text from poe2db.tw.

    python3 Plugins/StashUtility/scripts/fetch_mod_localization.py

Same tablet slugs as poe2-marketwright. Align us/cn/tw by page order, then map
onto StashUtility Ids (strip Tower/Map prefixes, then English text). Writes
stashutility.mod.<Id> into Localization/*.json without touching other keys.
"""

from __future__ import annotations

import argparse
import html as html_lib
import json
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

PLUGIN_DIR = Path(__file__).resolve().parents[1]
MOD_DB = PLUGIN_DIR / "Data" / "ModDatabase.cs"
LOC_DIR = PLUGIN_DIR / "Localization"
USER_AGENT = "GameHelper-StashUtility/1.0 (+https://poe2db.tw)"
TAG_RE = re.compile(r"<[^>]+>")
SPACE_RE = re.compile(r"\s+")
CATALOG_RE = re.compile(r'new (?:TabletMod|WaystoneMod)\("([^"]+)", "([^"]*)"')
OBJ_RE = re.compile(r'\{"Name":".*?"hover":".*?"\}')
TABLE_ROW_RE = re.compile(
    r"<tr>\s*<td>\d+</td>\s*<td>(?:Prefix|Suffix|前缀|后缀|前綴|後綴)</td>\s*<td>(.*?)</td>",
    re.IGNORECASE | re.DOTALL,
)
ARTICLES_RE = re.compile(r"\b(an|a|the|of|in|to|for|from|by|with)\b")
NUM_RE = re.compile(r"\(\d+[—\-–]\d+\)|\d+")

TABLET_SLUGS = (
    "Tablet",
    "Breach_Tablet",
    "Expedition_Tablet",
    "Delirium_Tablet",
    "Ritual_Tablet",
    "Irradiated_Tablet",
    "Overseer_Tablet",
    "Abyss_Tablet",
    "Temple_Tablet",
)
LOCALES = ("us", "cn", "tw")
LOCALE_FILES = {
    "us": "en-US.json",
    "cn": "zh-CN.json",
    "tw": "zh-Hant.json",
}


def catalog() -> list[tuple[str, str]]:
    return CATALOG_RE.findall(MOD_DB.read_text(encoding="utf-8"))


def family_key(name: str) -> str:
    if name.startswith("Tower"):
        name = name[5:]
    if name.startswith("Map"):
        name = name[3:]
    return name


def norm_en(text: str) -> str:
    text = clean_text(text).lower()
    text = NUM_RE.sub(" ", text)
    text = ARTICLES_RE.sub(" ", text)
    return re.sub(r"[^a-z]+", "", text)


def fetch(url: str, timeout: int) -> str:
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, "Accept": "text/html"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read().decode("utf-8", errors="replace")


def clean_text(raw: str) -> str:
    text = TAG_RE.sub("", raw or "")
    text = html_lib.unescape(text)
    return SPACE_RE.sub(" ", text).strip()


def parse_mods(page_html: str) -> list[tuple[str, str]]:
    out: list[tuple[str, str]] = []
    for raw in OBJ_RE.findall(page_html):
        try:
            obj = json.loads(raw)
        except json.JSONDecodeError:
            continue
        text = clean_text(obj.get("str") or "")
        if not text or len(text) > 200:
            continue
        fams = obj.get("ModFamilyList") or []
        fam = fams[0] if fams else ""
        if fam:
            out.append((fam, text))
    if out:
        return out
    for cell in TABLE_ROW_RE.findall(page_html):
        text = clean_text(cell)
        if text and len(text) <= 200:
            out.append(("", text))
    return out


def match_catalog(family: str, en_text: str, unused: dict[str, str]) -> str | None:
    key = family_key(family)
    needle = norm_en(en_text)
    best = None
    best_score = 0
    for mod_id, name in unused.items():
        hay = norm_en(name)
        fam_hit = family_key(mod_id) == key or mod_id == family
        if hay and needle and (hay == needle or hay in needle or needle in hay):
            score = 1000 if hay == needle else min(len(hay), len(needle))
        elif fam_hit:
            score = 2
        else:
            continue
        if fam_hit:
            score += 2
        if score > best_score:
            best, best_score = mod_id, score
    return best


def merge_locale_file(path: Path, updates: dict[str, str]) -> None:
    data: dict[str, str] = {}
    if path.exists():
        data = json.loads(path.read_text(encoding="utf-8"))
    data = {k: v for k, v in data.items() if not k.startswith("stashutility.mod.")}
    for key, value in updates.items():
        if value:
            data[key] = value
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--delay", type=float, default=0.25)
    ap.add_argument("--timeout", type=int, default=30)
    args = ap.parse_args()

    items = catalog()
    unused = dict(items)
    found: dict[str, dict[str, str]] = {}
    rows: list[tuple[str, str, str, str]] = []

    total = len(TABLET_SLUGS)
    for n, slug in enumerate(TABLET_SLUGS, 1):
        pages: dict[str, list[tuple[str, str]]] = {}
        for locale in LOCALES:
            url = f"https://poe2db.tw/{locale}/{slug}"
            try:
                pages[locale] = parse_mods(fetch(url, args.timeout))
                print(f"[{n}/{total}] {locale}/{slug}  {len(pages[locale])} mods", flush=True)
            except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError, OSError) as ex:
                print(f"[{n}/{total}] FAIL {url}: {ex}", file=sys.stderr, flush=True)
                pages[locale] = []
            if args.delay:
                time.sleep(args.delay)

        us_mods = pages.get("us") or []
        cn_mods = pages.get("cn") or []
        tw_mods = pages.get("tw") or []
        for i, (fam, en_text) in enumerate(us_mods):
            cn_text = cn_mods[i][1] if i < len(cn_mods) else en_text
            tw_text = tw_mods[i][1] if i < len(tw_mods) else en_text
            rows.append((fam, en_text, cn_text, tw_text))

    scored: list[tuple[int, str, int]] = []
    for row_i, (fam, en_text, _, _) in enumerate(rows):
        for mod_id, name in unused.items():
            key = family_key(fam)
            hay = norm_en(name)
            needle = norm_en(en_text)
            fam_hit = family_key(mod_id) == key or mod_id == fam
            if hay and needle and hay == needle:
                score = 1000
            elif hay and needle and (hay in needle or needle in hay):
                score = min(len(hay), len(needle))
            elif fam_hit:
                score = 3
            else:
                continue
            if fam_hit:
                score += 2
            scored.append((score, mod_id, row_i))

    scored.sort(reverse=True)
    used_rows: set[int] = set()
    for score, mod_id, row_i in scored:
        if mod_id not in unused or row_i in used_rows:
            continue
        unused.pop(mod_id)
        used_rows.add(row_i)
        fam, en_text, cn_text, tw_text = rows[row_i]
        found[mod_id] = {"us": en_text, "cn": cn_text, "tw": tw_text}

    for mod_id, name in list(unused.items()):
        if not mod_id.startswith("Tower"):
            continue
        hay = norm_en(name)
        if not hay:
            continue
        best = None
        best_score = 0
        for fam, en_text, cn_text, tw_text in rows:
            needle = norm_en(en_text)
            if not needle:
                continue
            if hay == needle:
                score = 1000
            elif hay in needle or needle in hay:
                score = min(len(hay), len(needle))
            else:
                continue
            if score > best_score:
                best, best_score = (en_text, cn_text, tw_text), score
        if best:
            unused.pop(mod_id, None)
            found[mod_id] = {"us": best[0], "cn": best[1], "tw": best[2]}

    print(f"matched {len(found)}/{len(items)} catalog ids")
    missing = [i for i, _ in items if i not in found]
    if missing:
        print("unmatched:", ", ".join(missing))

    for locale, filename in LOCALE_FILES.items():
        updates = {
            f"stashutility.mod.{mod_id}": texts.get(locale) or texts.get("us") or ""
            for mod_id, texts in found.items()
        }
        merge_locale_file(LOC_DIR / filename, updates)
        print(f"wrote {len(updates)} mod strings -> {filename}")

    return 0 if found else 1


if __name__ == "__main__":
    sys.exit(main())
