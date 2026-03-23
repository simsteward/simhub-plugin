# List LISTENING sockets on ports used by Sim Steward observability compose + SimHub plugin defaults.
# Run from anywhere: pwsh -NoProfile -File scripts/check-obs-ports.ps1
# Requires: Windows PowerShell 5+ or pwsh (Get-NetTCPConnection).

$ErrorActionPreference = "SilentlyContinue"

$ports = @(
    @{ Port = 3000;  Name = "Grafana (compose)" },
    @{ Port = 3100;  Name = "Loki HTTP (compose)" },
    @{ Port = 3500;  Name = "loki-gateway nginx (compose)" },
    @{ Port = 4317;  Name = "OTLP gRPC (otel-collector host map)" },
    @{ Port = 4318;  Name = "OTLP HTTP (otel-collector host map)" },
    @{ Port = 8080;  Name = "data-api (compose)" },
    @{ Port = 8888;  Name = "SimHub built-in HTTP (dashboard)" },
    @{ Port = 8889;  Name = "Often SimHubWPF or other apps (compose uses host 18889 instead)" },
    @{ Port = 9090;  Name = "Prometheus (compose)" },
    @{ Port = 13133; Name = "OTel collector health_check (compose)" },
    @{ Port = 18889; Name = "Collector /metrics on host (mapped to container 8889)" },
    @{ Port = 19847; Name = "Sim Steward WebSocket (SIMSTEWARD_WS_PORT default)" }
)

Write-Host "Checking LISTENING TCP ports (Sim Steward / SimHub-related)...`n"

$listen = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue
if (-not $listen) {
    Write-Host "Get-NetTCPConnection returned nothing (need admin or older OS). Falling back to netstat."
    netstat -ano | findstr LISTENING
    exit 0
}

foreach ($row in $ports) {
    $p = $row.Port
    $hits = $listen | Where-Object { $_.LocalPort -eq $p }
    if ($hits) {
        Write-Host "=== PORT $p - $($row.Name) ==="
        foreach ($h in $hits | Select-Object -Unique LocalAddress, LocalPort, OwningProcess) {
            $proc = Get-Process -Id $h.OwningProcess -ErrorAction SilentlyContinue
            $pn = if ($proc) { $proc.ProcessName } else { "?" }
            Write-Host ("  {0}:{1}  PID {2}  {3}" -f $h.LocalAddress, $h.LocalPort, $h.OwningProcess, $pn)
        }
        Write-Host ""
    }
}

$any = $false
foreach ($row in $ports) {
    if ($listen | Where-Object { $_.LocalPort -eq $row.Port }) { $any = $true; break }
}
if (-not $any) {
    Write-Host 'None of the listed ports show LISTENING (stack likely down, or SimHub closed).'
}
Write-Host ""
Write-Host "PASS: Port scan complete."
