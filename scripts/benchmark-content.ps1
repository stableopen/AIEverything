param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Query,

    [ValidateRange(1, 1000)]
    [int] $Iterations = 100
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$server = Join-Path $root 'dist\win-x64\AIEverything.Server.exe'
if (-not (Test-Path -LiteralPath $server -PathType Leaf)) {
    throw "Missing published executable: $server"
}

& (Join-Path $PSScriptRoot 'start-daemon.ps1') | Out-Null

function Invoke-ContentSearch {
    $json = & $server content-search $Query --limit 1
    if ($LASTEXITCODE -ne 0) {
        throw "content-search failed with exit code $LASTEXITCODE."
    }

    return ($json | Out-String | ConvertFrom-Json)
}

Invoke-ContentSearch | Out-Null
$durations = New-Object 'System.Collections.Generic.List[double]'
for ($index = 0; $index -lt $Iterations; $index++) {
    $response = Invoke-ContentSearch
    $durations.Add([double] $response.queryDurationMs)
}

$ordered = @($durations | Sort-Object)
$medianIndex = [Math]::Floor(($ordered.Count - 1) / 2)
$p95Index = [Math]::Max(0, [Math]::Ceiling($ordered.Count * 0.95) - 1)

[pscustomobject]@{
    query = $Query
    iterations = $Iterations
    minimumMs = [Math]::Round($ordered[0], 3)
    medianMs = [Math]::Round($ordered[$medianIndex], 3)
    p95Ms = [Math]::Round($ordered[$p95Index], 3)
    maximumMs = [Math]::Round($ordered[-1], 3)
} | ConvertTo-Json
