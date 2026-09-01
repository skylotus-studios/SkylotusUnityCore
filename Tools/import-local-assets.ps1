<#
.SYNOPSIS
    Imports the license-restricted third-party assets that are deliberately not in git.

.DESCRIPTION
    Some packs in this project are licensed per-seat (Unity Asset Store EULA and similar)
    and must not be redistributed. They are listed in .gitignore, so a fresh clone does
    not have them and any scene referencing them shows missing sprites until you run this.

    The canonical copies live in the original SkylotusUnityCore checkout at
    C:\dev\SkylotusUnityCore. This script copies them from there into the current project.

    Each pack is copied with its sibling .meta file. Unity identifies an asset by the GUID
    in that .meta, so copying it keeps every existing scene and prefab reference intact;
    without it Unity mints a new GUID on import and the references break exactly as if the
    pack were still absent.

.PARAMETER List
    Show what the project expects and what is already present. Copies nothing.

.EXAMPLE
    .\Tools\import-local-assets.ps1

.EXAMPLE
    .\Tools\import-local-assets.ps1 -List

.NOTES
    Exit codes: 0 all present, 1 something missing at the source, 2 not a project root.
#>
[CmdletBinding()]
param(
    [switch]$List
)

$ErrorActionPreference = 'Stop'

# --- Assets to import --------------------------------------------------------
# Paths are relative to the project root. Add an entry here when you vendor another
# license-restricted pack, and add the matching .gitignore entry.

$Assets = @(
    'Assets\Animated Loading Icons'
)

# The reference checkout every clone imports from.
$Source = 'C:\dev\SkylotusUnityCore'

# The project this script lives in, not the caller's current directory.
$Project = Split-Path $PSScriptRoot -Parent

# --- Preflight ---------------------------------------------------------------

if (-not (Test-Path (Join-Path $Project 'Assets'))) {
    Write-Host "error: not a Unity project root (no Assets\ directory in $Project)." -ForegroundColor Red
    exit 2
}

if ($List) {
    Write-Host 'License-restricted assets this project expects:'
    foreach ($asset in $Assets) {
        if (Test-Path (Join-Path $Project $asset)) {
            Write-Host "  [present] $asset"
        }
        else {
            Write-Host "  [MISSING] $asset" -ForegroundColor Yellow
        }
    }
    exit 0
}

if (-not (Test-Path $Source)) {
    Write-Host "error: source project not found: $Source" -ForegroundColor Red
    Write-Host '       This script imports from the original SkylotusUnityCore checkout.'
    Write-Host '       If yours lives elsewhere, edit $Source at the top of this script.'
    exit 1
}

if ((Resolve-Path $Source).Path -eq (Resolve-Path $Project).Path) {
    Write-Host 'Source and destination are the same project - nothing to do.'
    exit 0
}

Write-Host "Importing from: $Source"
Write-Host ''

# --- Copy --------------------------------------------------------------------

$missing = 0
$copied = 0

foreach ($asset in $Assets) {
    $src = Join-Path $Source $asset
    $dst = Join-Path $Project $asset

    if (-not (Test-Path $src)) {
        Write-Host "  MISSING at source: $asset" -ForegroundColor Yellow
        $missing++
        continue
    }

    if (Test-Path $dst) {
        Write-Host "  already present, skipped: $asset"
        continue
    }

    $parent = Split-Path $dst -Parent
    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    Copy-Item -Path $src -Destination $dst -Recurse

    # Copy the .meta alongside so the GUID - and every reference to it - survives.
    if (Test-Path "$src.meta") {
        Copy-Item -Path "$src.meta" -Destination "$dst.meta"
    }

    Write-Host "  imported: $asset" -ForegroundColor Green
    $copied++
}

Write-Host ''

if ($missing -gt 0) {
    Write-Host "$missing asset(s) were not found at the source. Scenes referencing them will" -ForegroundColor Red
    Write-Host 'show missing sprites. Point $Source at a checkout that has them.' -ForegroundColor Red
    exit 1
}

if ($copied -eq 0) {
    Write-Host 'Nothing to do - everything was already present.'
}
else {
    Write-Host "$copied asset(s) imported. Let Unity reimport, then check for missing references."
}

exit 0
