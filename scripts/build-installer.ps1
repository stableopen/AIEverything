param(
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$dist = [System.IO.Path]::GetFullPath((Join-Path $root 'dist\standalone\win-x64'))
$installer = [System.IO.Path]::GetFullPath(
    (Join-Path $root 'dist\AIEverything-Setup-1.0.0.exe'))
$makensisCandidates = @(
    'C:\Program Files (x86)\NSIS\makensis.exe',
    'C:\Program Files\NSIS\makensis.exe'
)
$makensis = $makensisCandidates | Where-Object {
    Test-Path -LiteralPath $_ -PathType Leaf
} | Select-Object -First 1
if ($null -eq $makensis) {
    throw 'NSIS makensis.exe was not found.'
}

& (Join-Path $PSScriptRoot 'build-standalone.ps1') -SkipTests:$SkipTests

& $makensis `
    "/DPRODUCT_ROOT=$root" `
    "/DPRODUCT_DIST=$dist" `
    (Join-Path $root 'installer\AIEverything.nsi')
if ($LASTEXITCODE -ne 0) {
    throw "NSIS failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "Installer was not created: $installer"
}

Write-Output "PASS: created installer $installer"
