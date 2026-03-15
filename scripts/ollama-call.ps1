<#
.SYNOPSIS
    Calls Ollama HTTP API directly and returns full JSON response with token counts.

.DESCRIPTION
    This script calls Ollama's /api/generate endpoint and returns the complete
    JSON response including real token counts (prompt_eval_count, eval_count)
    and timing metadata. For thinking-capable models (e.g. DeepSeek R1), use
    -Think to get separate thinking_tokens and response_tokens.

.PARAMETER Model
    The model to use (default: deepseek-r1:8b)

.PARAMETER Prompt
    The prompt to send to the model

.PARAMETER System
    Optional system prompt

.PARAMETER Context
    Optional conversation context (JSON array of messages)

.PARAMETER Think
    Enable thinking mode for reasoning models (DeepSeek R1). Returns thinking_text,
    thinking_tokens, and response_tokens separately.

.PARAMETER Raw
    If set, returns raw JSON string instead of PowerShell object

.EXAMPLE
    .\ollama-call.ps1 -Prompt "What is 2+2?"

.EXAMPLE
    .\ollama-call.ps1 -Prompt "Explain quantum computing" -Think
#>

param(
    [string]$Model = "deepseek-r1:8b",
    [Parameter(Mandatory=$true)]
    [string]$Prompt,
    [string]$System = "",
    [string]$Context = "",
    [switch]$Think,
    [switch]$Raw
)

$ErrorActionPreference = "Stop"

$body = @{
    model = $Model
    prompt = $Prompt
    stream = $false
}

if ($System) {
    $body.system = $System
}

if ($Context) {
    $body.context = $Context | ConvertFrom-Json
}

if ($Think) {
    $body.think = $true
}

$jsonBody = $body | ConvertTo-Json -Compress -Depth 10

try {
    $response = Invoke-RestMethod -Uri "http://localhost:11434/api/generate" `
        -Method Post `
        -Body $jsonBody `
        -ContentType "application/json" `
        -TimeoutSec 300

    $evalCount = $response.eval_count
    $thinkingText = if ($response.thinking) { [string]$response.thinking } else { "" }
    $responseText = if ($response.response) { [string]$response.response } else { "" }

    # Ratio-based split: eval_count = thinking_tokens + response_tokens
    $thinkingLen = $thinkingText.Length
    $responseLen = $responseText.Length
    $totalLen = $thinkingLen + $responseLen
    $thinkingTokens = 0
    $responseTokens = $evalCount
    if ($totalLen -gt 0 -and $evalCount -gt 0) {
        $thinkingTokens = [math]::Round($evalCount * ($thinkingLen / $totalLen))
        $responseTokens = $evalCount - $thinkingTokens
    }

    # Add computed fields for easier consumption
    $result = @{
        model = $response.model
        response = $responseText
        done = $response.done
        input_tokens = $response.prompt_eval_count
        output_tokens = $evalCount
        total_tokens = ($response.prompt_eval_count + $evalCount)
        thinking_text = $thinkingText
        thinking_tokens = $thinkingTokens
        response_tokens = $responseTokens
        total_output_tokens = $evalCount
        duration_ms = [math]::Round($response.total_duration / 1000000)
        load_duration_ms = if ($response.load_duration) { [math]::Round($response.load_duration / 1000000) } else { 0 }
        prompt_eval_duration_ms = if ($response.prompt_eval_duration) { [math]::Round($response.prompt_eval_duration / 1000000) } else { 0 }
        eval_duration_ms = if ($response.eval_duration) { [math]::Round($response.eval_duration / 1000000) } else { 0 }
        raw = @{
            prompt_eval_count = $response.prompt_eval_count
            eval_count = $evalCount
            total_duration = $response.total_duration
            load_duration = $response.load_duration
            prompt_eval_duration = $response.prompt_eval_duration
            eval_duration = $response.eval_duration
        }
    }

    if ($Raw) {
        $result | ConvertTo-Json -Depth 10
    } else {
        [PSCustomObject]$result
    }
}
catch {
    Write-Error "Failed to call Ollama API: $_"
    exit 1
}
