#!/usr/bin/env bash
# Build with Windows dotnet.exe, publish the GitHub Release from WSL.
#   scripts/publish.sh 1.4.12
#   scripts/publish.sh --skip-build 1.4.12
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REPO="WAY29/Gamehelper"
DOTNET="${DOTNET:-/mnt/c/Program Files/dotnet/dotnet.exe}"
CONFIG=Release
SKIP_BUILD=0
VERSION=""
NOTES=()

usage() {
  echo "usage: $0 [--skip-build] [--repo owner/name] x.y.z [changelog line]..."
  exit 1
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-build) SKIP_BUILD=1; shift ;;
    --repo) REPO="$2"; shift 2 ;;
    -h|--help) usage ;;
    -*) echo "unknown option: $1"; usage ;;
    *)
      if [[ -z "$VERSION" ]]; then VERSION="$1"; else NOTES+=("$1"); fi
      shift
      ;;
  esac
done

[[ "${VERSION:-}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || usage
TAG="v$VERSION"
PUBLISH="$ROOT/publish"
OUT="$ROOT/GameHelper/bin/$CONFIG/net10.0-windows/win-x64"
SIGN_PROJ="$ROOT/scripts/GenerateUpdateSigningKey/GenerateUpdateSigningKey.csproj"
DL_PROJ="$ROOT/Downloader/Downloader.csproj"

command -v gh >/dev/null || { echo "gh missing. Install GitHub CLI in WSL, then gh auth login"; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "Not logged in. Run: gh auth login"; exit 1; }
[[ -x "$DOTNET" ]] || { echo "dotnet.exe missing: $DOTNET"; exit 1; }

set_version() {
  python3 - "$1" "$VERSION" <<'PY'
import re, sys
from pathlib import Path
p, ver = Path(sys.argv[1]), sys.argv[2]
t = p.read_text(encoding="utf-8")
asm = ver + ".0"
t = re.sub(r"<Version>[^<]+</Version>", f"<Version>{ver}</Version>", t)
t = re.sub(r"<AssemblyVersion>[^<]+</AssemblyVersion>", f"<AssemblyVersion>{asm}</AssemblyVersion>", t)
t = re.sub(r"<FileVersion>[^<]+</FileVersion>", f"<FileVersion>{asm}</FileVersion>", t)
p.write_text(t, encoding="utf-8")
PY
}

if [[ ${#NOTES[@]} -eq 0 && -s "$ROOT/release-notes.txt" ]]; then
  mapfile -t NOTES < <(grep -v '^[[:space:]]*$' "$ROOT/release-notes.txt" || true)
fi
if [[ ${#NOTES[@]} -eq 0 ]]; then
  NOTES=("WAY29 fork: CJK atlas names, GameNotLoaded latch fix, overlay DPI follows the game")
fi

echo "=== Publish $TAG -> https://github.com/$REPO ==="
set_version "$ROOT/GameHelper/GameHelper.csproj"
set_version "$ROOT/Launcher/Launcher.csproj"

if [[ "$SKIP_BUILD" -eq 0 ]]; then
  echo "=== Build ($CONFIG) ==="
  "$DOTNET" restore "$(wslpath -w "$ROOT/GameOverlay.sln")"
  "$DOTNET" build "$(wslpath -w "$ROOT/GameOverlay.sln")" -c "$CONFIG" --no-restore
  rm -rf "$PUBLISH"
  mkdir -p "$PUBLISH"
  cp -a "$OUT"/. "$PUBLISH"/
  find "$PUBLISH" -name '*.pdb' -delete
  echo "=== Downloader ==="
  "$DOTNET" publish "$(wslpath -w "$DL_PROJ")" -c "$CONFIG" -r win-x64 \
    -o "$(wslpath -w "$ROOT/publish-downloader")" \
    -p:PublishSingleFile=true -p:SelfContained=false
  cp "$ROOT/publish-downloader/GameHelperDownloader.exe" "$ROOT/GameHelperDownloader.exe"
fi

[[ -f "$PUBLISH/GameHelper.exe" ]] || { echo "missing $PUBLISH/GameHelper.exe"; exit 1; }

STAGE="$(python3 "$ROOT/scripts/make-release-assets.py" "$ROOT" "$VERSION" "${NOTES[@]}")"
echo "=== Sign manifest ==="
KEY="$ROOT/update-signing.key"
[[ -f "$KEY" ]] || { echo "missing $KEY — run: $DOTNET run --project $(wslpath -w "$SIGN_PROJ") -c Release -- ensure"; exit 1; }
openssl dgst -sha256 -sign "$KEY" -out /tmp/manifest.sig.bin "$STAGE/manifest.json"
base64 -w0 /tmp/manifest.sig.bin > "$STAGE/manifest.sig"

if [[ -f "$ROOT/GameHelperDownloader.exe" ]]; then
  cp "$ROOT/GameHelperDownloader.exe" "$STAGE/GameHelperDownloader.exe"
fi

NOTES_FILE="/tmp/gamehelper-github-notes-$TAG.md"
printf '%s\n' "${NOTES[@]}" | sed 's/^/- /' > "$NOTES_FILE"

if gh release view "$TAG" --repo "$REPO" >/dev/null 2>&1; then
  echo "Updating $TAG"
  gh release edit "$TAG" --repo "$REPO" --notes-file "$NOTES_FILE" --title "GameHelper $TAG"
else
  echo "Creating $TAG"
  gh release create "$TAG" --repo "$REPO" --title "GameHelper $TAG" --notes-file "$NOTES_FILE" --latest
fi

gh release upload "$TAG" \
  "$STAGE/GameHelper-$VERSION-full.zip" \
  "$STAGE/manifest.sig" \
  "$STAGE/changelog-history.json" \
  --repo "$REPO" --clobber
if [[ -f "$STAGE/GameHelperDownloader.exe" ]]; then
  gh release upload "$TAG" "$STAGE/GameHelperDownloader.exe" --repo "$REPO" --clobber
fi
gh release upload "$TAG" "$STAGE/manifest.json" --repo "$REPO" --clobber

echo "Release: https://github.com/$REPO/releases/tag/$TAG"
echo "ZIP:     https://github.com/$REPO/releases/latest/download/GameHelper-$VERSION-full.zip"
