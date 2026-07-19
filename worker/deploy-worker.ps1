# Deploy the Sim Steward Cloudflare Worker (Data API: D1 + R2 + KV, JWT auth).
#
# MANUAL, human-run runbook. This dev environment holds no Cloudflare credentials
# by design; the account owner runs this interactively after `wrangler login`.
#
# Run from repo root or worker/:  .\worker\deploy-worker.ps1 [-DryRun]
#   -DryRun   Print every command without executing it.
#
# Design: docs/superpowers/specs/2026-07-19-cloudflare-incident-storage-design.md
# Runbook: docs/DATA-API-DEPLOY.md
#
# The Worker deploys on its own cadence — it is deliberately NOT folded into the
# plugin's deploy.ps1 (different toolchain, different credentials, manual gate).

#requires -Version 7

param(
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$WorkerRoot = $PSScriptRoot

# Resource names (must match wrangler.toml bindings).
$D1Name  = "simsteward-db"
$R2Name  = "simsteward-incident-index"
$KVName  = "simsteward-device-codes"

function Write-Step { param([string]$Msg) Write-Host "`n=== $Msg ===" -ForegroundColor Cyan }
function Write-Note { param([string]$Msg) Write-Host "  $Msg" -ForegroundColor Yellow }

# Run a wrangler command, or just print it under -DryRun. Non-fatal steps
# (create-if-not-exists) pass -AllowFail so a "already exists" error doesn't abort.
function Invoke-Wrangler {
    param(
        [string[]]$WranglerArgs,
        [switch]$AllowFail
    )
    $display = "npx wrangler " + ($WranglerArgs -join " ")
    if ($DryRun) {
        Write-Host "  [dry-run] $display" -ForegroundColor DarkGray
        return
    }
    Write-Host "  > $display" -ForegroundColor Gray
    & npx wrangler @WranglerArgs
    if ($LASTEXITCODE -ne 0) {
        if ($AllowFail) {
            Write-Note "Command exited $LASTEXITCODE (continuing — resource may already exist)."
        } else {
            throw "wrangler exited $LASTEXITCODE for: $display"
        }
    }
}

Push-Location $WorkerRoot
try {
    Write-Host "Sim Steward Worker deploy" -ForegroundColor Green
    if ($DryRun) { Write-Note "DRY RUN — no commands will execute." }

    # ── Prerequisites ────────────────────────────────────────────────────────
    Write-Step "Prerequisites"

    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        throw "node not found on PATH. Install Node.js: https://nodejs.org"
    }
    Write-Host "  node: $(node --version)" -ForegroundColor Gray

    if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
        throw "npx not found on PATH (ships with Node.js). Install Node.js and retry."
    }

    # `wrangler whoami` is a read-only auth probe — always run it (even in dry-run)
    # so the operator learns immediately whether they are logged in.
    $whoami = & npx wrangler whoami 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $whoami -match "not authenticated|You are not logged in") {
        Write-Host $whoami -ForegroundColor DarkGray
        throw "Not logged in to Cloudflare. Run 'wrangler login' (opens a browser), then re-run this script."
    }
    Write-Host "  wrangler authenticated:" -ForegroundColor Gray
    Write-Host ($whoami.Trim() -split "`n" | Select-Object -First 4 | ForEach-Object { "    $_" }) -ForegroundColor DarkGray

    if (-not (Test-Path (Join-Path $WorkerRoot "schema.sql"))) {
        throw "schema.sql not found in $WorkerRoot — cannot apply D1 schema."
    }

    # ── 1. Provision D1 ──────────────────────────────────────────────────────
    Write-Step "1. Create D1 database '$D1Name'"
    Invoke-Wrangler @("d1", "create", $D1Name) -AllowFail
    Write-Note "Copy the printed 'database_id' into worker/wrangler.toml under [[d1_databases]] (replace any REPLACE_AFTER_D1_CREATE placeholder)."

    # ── 2. Provision R2 ──────────────────────────────────────────────────────
    Write-Step "2. Create R2 bucket '$R2Name'"
    Invoke-Wrangler @("r2", "bucket", "create", $R2Name) -AllowFail

    # ── 3. Provision KV ──────────────────────────────────────────────────────
    Write-Step "3. Create KV namespace '$KVName'"
    Invoke-Wrangler @("kv", "namespace", "create", $KVName) -AllowFail
    Write-Note "Copy the printed KV namespace 'id' into worker/wrangler.toml under [[kv_namespaces]]."

    # ── 4. Apply schema ──────────────────────────────────────────────────────
    Write-Step "4. Apply schema.sql to remote D1"
    Write-Note "Requires database_id from step 1 to be filled in wrangler.toml first."
    Invoke-Wrangler @("d1", "execute", $D1Name, "--remote", "--file=./schema.sql")

    # ── 5. JWT signing secret (interactive) ──────────────────────────────────
    Write-Step "5. Set JWT signing secret"
    Write-Note "This step needs YOU to type/paste the secret — run it yourself now (not auto-run to keep this script non-interactive):"
    Write-Host "      npx wrangler secret put JWT_HMAC_SECRET" -ForegroundColor White
    Write-Note "Use a long random value, e.g.:  [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Max 256 }))"

    # ── 6. Deploy ────────────────────────────────────────────────────────────
    Write-Step "6. Deploy the Worker"
    Write-Note "Ensure wrangler.toml has database_id + KV id filled and JWT_HMAC_SECRET is set before deploying."
    Invoke-Wrangler @("deploy")

    # ── Post-deploy checklist ────────────────────────────────────────────────
    Write-Step "Post-deploy checklist (manual — Cloudflare dashboard + plugin .env)"
    Write-Host @"
  [ ] Cloudflare Access application (Zero Trust dashboard):
        - Team domain: <your-team>.cloudflareaccess.com
        - Self-hosted app on your custom domain covering:  /approve  and  /admin/*
        - Policy: email One-Time-PIN (OTP), allowlisting your admin email(s)
        - Keep it per-path so the data-plane / auth API routes stay OUTSIDE Access
          (they use our own JWT, not Access).
  [ ] Fill ACCESS_TEAM_DOMAIN and ACCESS_AUD in worker/wrangler.toml, then redeploy:
        npx wrangler deploy
  [ ] In the plugin's .env, set:
        SIMSTEWARD_CLOUD_API_URL   = https://<deployed-worker-hostname>   (no trailing slash)
        SIMSTEWARD_CLOUD_APP_TOKEN = <this build's app token>
  [ ] First run: pair a device via the /approve page (Cloudflare Access OTP login).
"@ -ForegroundColor Yellow

    Write-Host "`nDone." -ForegroundColor Green
    if ($DryRun) { Write-Note "DRY RUN complete — nothing was executed." }
}
finally {
    Pop-Location
}
