# Ensure Ollama is running and required sentinel models are pulled.
# Called automatically by obs:up via pnpm.

$ErrorActionPreference = "Stop"

$OllamaUrl = "http://localhost:11434"
$Models = @("qwen3:8b", "qwen3:32b")

function Test-OllamaRunning {
    try {
        $null = Invoke-RestMethod -Uri "$OllamaUrl/api/tags" -TimeoutSec 3
        return $true
    }
    catch {
        return $false
    }
}

# Start Ollama if not already running
if (-not (Test-OllamaRunning)) {
    Write-Host "Ollama not detected — starting..."
    Start-Process -FilePath "ollama" -ArgumentList "serve" -WindowStyle Hidden

    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-OllamaRunning)) {
        if ((Get-Date) -gt $deadline) {
            Write-Error "Ollama did not start within 20 seconds. Is it installed and on PATH?"
            exit 1
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "Ollama started."
}
else {
    Write-Host "Ollama already running."
}

# Pull each model — ollama pull is idempotent (no-ops if already up to date)
foreach ($model in $Models) {
    Write-Host "Checking $model..."
    ollama pull $model
}

Write-Host "Ollama ready. Models: $($Models -join ', ')"
