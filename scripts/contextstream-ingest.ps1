<#
.SYNOPSIS
    Ingest this repo into ContextStream (semantic search index).

.DESCRIPTION
    Runs contextstream-mcp.exe ingest with credentials from .env via envmcp.
    Use from a normal terminal (or this script uses cmd.exe so ingest sees a console).

.PARAMETER Force
    Pass --force to re-upload all files.
#>
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe = Join-Path $env:LOCALAPPDATA 'ContextStream\contextstream-mcp.exe'
if (-not (Test-Path $exe)) {
    Write-Error "contextstream-mcp not found: $exe"
}

$forceArg = if ($Force) { ' --force' } else { '' }
$inner = "cd /d `"$repoRoot`" && npx -y envmcp --env-file .env cmd /c `"`"$exe`" ingest --path `"$repoRoot`"$forceArg`""
# New console so the CLI gets a TTY (ingest fails with "not a terminal" under non-interactive hosts).
$p = Start-Process -FilePath $env:ComSpec -ArgumentList @('/c', $inner) -PassThru -Wait
exit $p.ExitCode
