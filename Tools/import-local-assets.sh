#!/usr/bin/env bash
#
# Import license-restricted third-party assets that are deliberately NOT in git.
#
# Some packs in this project are licensed per-seat (Unity Asset Store EULA and similar)
# and must not be redistributed. They are listed in .gitignore, so a fresh clone does not
# have them and any scene referencing them will show missing sprites until you run this.
#
# The canonical copies live in a reference checkout on your machine — by default the
# original SkylotusUnityCore clone. This script copies them into the current project.
#
# Usage
#   ./Tools/import-local-assets.sh                    # copy from the default source
#   ./Tools/import-local-assets.sh /path/to/source    # copy from somewhere else
#   SKYLOTUS_ASSET_SOURCE=/path ./Tools/import-local-assets.sh
#   ./Tools/import-local-assets.sh --list             # show what is expected, copy nothing
#
# Exit codes: 0 all present, 1 something missing at the source, 2 not run from a project root.

set -euo pipefail

# ─── Assets to import ────────────────────────────────────────────────────────
# Paths are relative to the project root. Add a line here when you vendor another
# license-restricted pack, and add the matching .gitignore entry.
ASSETS=(
    "Assets/Animated Loading Icons"
)

# ─── Source resolution ───────────────────────────────────────────────────────
DEFAULT_SOURCE="/c/dev/SkylotusUnityCore"
SOURCE="${1:-${SKYLOTUS_ASSET_SOURCE:-$DEFAULT_SOURCE}}"

if [[ "${1:-}" == "--list" ]]; then
    echo "License-restricted assets this project expects:"
    for a in "${ASSETS[@]}"; do
        if [[ -e "$a" ]]; then echo "  [present] $a"; else echo "  [MISSING] $a"; fi
    done
    exit 0
fi

# ─── Preflight ───────────────────────────────────────────────────────────────
if [[ ! -d "Assets" ]]; then
    echo "error: run this from a Unity project root (no Assets/ directory here)." >&2
    exit 2
fi

# Windows paths are accepted and converted, so C:\dev\... and /c/dev/... both work.
SOURCE="${SOURCE//\\//}"
if [[ "$SOURCE" =~ ^([A-Za-z]):(/.*)$ ]]; then
    SOURCE="/${BASH_REMATCH[1],,}${BASH_REMATCH[2]}"
fi

if [[ ! -d "$SOURCE" ]]; then
    echo "error: source project not found: $SOURCE" >&2
    echo "       pass a path, or set SKYLOTUS_ASSET_SOURCE." >&2
    exit 1
fi

if [[ "$(cd "$SOURCE" && pwd)" == "$(pwd)" ]]; then
    echo "Source and destination are the same project — nothing to do."
    exit 0
fi

echo "Importing from: $SOURCE"
echo

# ─── Copy ────────────────────────────────────────────────────────────────────
missing=0
copied=0

for asset in "${ASSETS[@]}"; do
    src="$SOURCE/$asset"

    if [[ ! -e "$src" ]]; then
        echo "  MISSING at source: $asset"
        missing=$((missing + 1))
        continue
    fi

    if [[ -e "$asset" ]]; then
        echo "  already present, skipped: $asset"
        continue
    fi

    mkdir -p "$(dirname "$asset")"
    cp -r "$src" "$asset"

    # Unity identifies assets by the GUID in the sibling .meta file. Copying it keeps
    # every existing scene and prefab reference intact; without it Unity mints a new
    # GUID on import and the references break exactly as if the pack were absent.
    if [[ -f "$src.meta" ]]; then
        cp "$src.meta" "$asset.meta"
    fi

    echo "  imported: $asset"
    copied=$((copied + 1))
done

echo
if [[ $missing -gt 0 ]]; then
    echo "$missing asset(s) were not found at the source. Scenes referencing them will"
    echo "show missing sprites. Point the script at a checkout that has them."
    exit 1
fi

if [[ $copied -eq 0 ]]; then
    echo "Nothing to do — everything was already present."
else
    echo "$copied asset(s) imported. Let Unity reimport, then check for missing references."
fi
