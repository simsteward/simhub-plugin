<#
.SYNOPSIS
  One-shot elevated setup: display-topology map, FpsExporter service install, and the Alloy
  scrape-target config change — everything needed to get game_fps/display_fps/frames_dropped_total
  flowing into Grafana Cloud that can't be done from a non-elevated or remote session.

.DESCRIPTION
  Run this from an elevated PowerShell, on your own physical console session (not RDP) — the
  display-topology step specifically needs a real interactive desktop to see real monitors.

  Order matters: the display-topology map is generated before the service starts, so the
  service picks it up on its first read (connector-map.json is only read once at startup).

.EXAMPLE
  cd C:\Users\winth\dev\sim-steward\simhub-plugin
  .\scripts\fps-exporter\setup-all.ps1
#>

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "Run this from an elevated (Administrator) PowerShell."
    exit 1
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Output "=== 1/4: Mapping monitors to connector types ==="
& (Join-Path $scriptDir "dump-display-topology.ps1")

Write-Output "`n=== 2/4: Installing fps-exporter as a Windows Service ==="
& (Join-Path $scriptDir "install-service.ps1")

Write-Output "`n=== 3/4: Verifying the service ==="
Get-Service FpsExporter
try {
    $resp = Invoke-WebRequest -Uri "http://127.0.0.1:9101/metrics" -TimeoutSec 5 -UseBasicParsing
    Write-Output "metrics endpoint: HTTP $($resp.StatusCode)"
    Write-Output $resp.Content
} catch {
    Write-Warning "metrics endpoint not reachable yet: $($_.Exception.Message)"
}

Write-Output "`n=== 4/4: Adding the Alloy scrape target ==="
$alloyConfig = "C:\Program Files\GrafanaLabs\Alloy\config.alloy"
if (-not (Test-Path $alloyConfig)) {
    Write-Warning "Alloy config not found at $alloyConfig — skipping this step, add it manually."
} elseif (Select-String -Path $alloyConfig -Pattern 'fps_exporter' -Quiet) {
    Write-Output "fps_exporter scrape block already present in config.alloy, skipping."
} else {
    Copy-Item $alloyConfig "$alloyConfig.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
    Add-Content -Path $alloyConfig -Value @'

prometheus.scrape "fps_exporter" {
  targets = [
    {"__address__" = "127.0.0.1:9101", "instance" = "Win-Pc"},
  ]
  scrape_interval = "5s"
  forward_to      = [prometheus.remote_write.grafana_cloud.receiver]
}
'@
    Write-Output "config.alloy updated (backup saved alongside it)."
    Restart-Service Alloy
    Get-Service Alloy
}

Write-Output ""
Write-Output "Done. Launch iRacing or the pit-wall browser, wait ~10s, then check:"
Write-Output "  curl.exe http://127.0.0.1:9101/metrics"
Write-Output "Real game_fps/display_fps values (not just frames_dropped_total) confirm the whole pipeline works."
Write-Output "Not yet verified anywhere in this session: killing the PresentMon child process and confirming it respawns within ~5s. Worth doing once, manually, via Task Manager + %LOCALAPPDATA%\FpsExporter\fps-exporter.log."
