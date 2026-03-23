# Load KEY=VALUE pairs from .env-style files into the current process environment.
# Later files override earlier keys. Skips comments and blank lines.
# Usage (repo root):  . .\scripts\load-dotenv.ps1
#                      Import-DotEnv @((Join-Path $PSScriptRoot "..\.env"))
# Optional second file (e.g. LOKI_PUSH_TOKEN from Docker stack):
#   Import-DotEnv @($envPath, (Join-Path $RepoRoot "observability\local\.env.observability.local"))

function Import-DotEnv {
    param(
        [Parameter(Mandatory)]
        [string[]]$Path
    )
    foreach ($file in $Path) {
        if ([string]::IsNullOrWhiteSpace($file) -or -not (Test-Path -LiteralPath $file)) { continue }
        $content = Get-Content -LiteralPath $file -Raw -ErrorAction Stop
        foreach ($rawLine in $content -split "`r?`n") {
            $line = $rawLine.Trim()
            if (-not $line -or $line.StartsWith("#")) { continue }
            if ($line.StartsWith("export ", [System.StringComparison]::OrdinalIgnoreCase)) {
                $line = $line.Substring(7).Trim()
            }
            $eq = $line.IndexOf("=")
            if ($eq -le 0) { continue }
            $key = $line.Substring(0, $eq).Trim()
            if (-not $key) { continue }
            $val = $line.Substring($eq + 1).Trim()
            # Trailing comment: KEY=value # note  (space before #)
            $spHash = $val.IndexOf(" #")
            if ($spHash -ge 0) { $val = $val.Substring(0, $spHash).Trim() }
            if ($val.Length -ge 2) {
                if (($val.StartsWith('"') -and $val.EndsWith('"')) -or ($val.StartsWith("'") -and $val.EndsWith("'"))) {
                    $val = $val.Substring(1, $val.Length - 2)
                }
            }
            Set-Item -Path "Env:$key" -Value $val
        }
    }
}

# Primary env file: use -EnvFile path (absolute or repo-relative) instead of default repo .env; then merge observability/local/.env.observability.local if present.
function Resolve-SimStewardEnvPaths {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [string]$EnvFile = ''
    )
    $list = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($EnvFile)) {
        $resolved = if ([System.IO.Path]::IsPathRooted($EnvFile)) { $EnvFile } else { Join-Path $RepoRoot $EnvFile }
        if (-not (Test-Path -LiteralPath $resolved)) {
            throw "Env file not found: $resolved"
        }
        [void]$list.Add($resolved)
    } else {
        $def = Join-Path $RepoRoot '.env'
        if (Test-Path -LiteralPath $def) { [void]$list.Add($def) }
    }
    $obs = Join-Path $RepoRoot 'observability\local\.env.observability.local'
    if (Test-Path -LiteralPath $obs) { [void]$list.Add($obs) }
    return ,$list.ToArray()
}
