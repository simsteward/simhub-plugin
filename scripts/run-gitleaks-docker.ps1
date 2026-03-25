# Run official Gitleaks image against full git history (mount repo at /repo).
# Usage (repo root): pwsh -NoProfile -File scripts/run-gitleaks-docker.ps1
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$abs = (Resolve-Path $repoRoot).Path
docker run --rm -v "${abs}:/repo" zricethezav/gitleaks:latest detect --source=/repo --verbose
