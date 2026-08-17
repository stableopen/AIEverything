param(
    [Parameter(Mandatory = $true)]
    [string] $InstallDirectory
)

$ErrorActionPreference = 'Stop'

$installRoot = [System.IO.Path]::GetFullPath($InstallDirectory)
$expectedPath = [System.IO.Path]::GetFullPath(
    (Join-Path $installRoot 'AIEverything.Daemon.exe'))
$stopped = 0

foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='AIEverything.Daemon.exe'")) {
    if ([string]::IsNullOrWhiteSpace($process.ExecutablePath)) {
        continue
    }

    $actualPath = [System.IO.Path]::GetFullPath($process.ExecutablePath)

    if (-not $actualPath.Equals($expectedPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        continue
    }

    Stop-Process -Id $process.ProcessId -Force
    Wait-Process -Id $process.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
    $stopped++
}

Write-Output "Stopped $stopped AIEverything daemon process(es) from $installRoot."
