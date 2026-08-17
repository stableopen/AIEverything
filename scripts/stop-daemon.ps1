$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$expectedPath = [System.IO.Path]::GetFullPath(
    (Join-Path $root 'dist\win-x64\AIEverything.Daemon.exe'))
$stopped = 0

foreach ($process in @(Get-Process -Name 'AIEverything.Daemon' -ErrorAction SilentlyContinue)) {
    try {
        $actualPath = [System.IO.Path]::GetFullPath($process.MainModule.FileName)
    }
    catch {
        continue
    }

    if (-not $actualPath.Equals($expectedPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        continue
    }

    Stop-Process -Id $process.Id -Force
    Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    $stopped++
}

Write-Output "PASS: stopped $stopped AIEverything daemon process(es) from this plugin."
