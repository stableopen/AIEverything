$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist\win-x64'
$server = Join-Path $dist 'AIEverything.Server.exe'
$daemon = Join-Path $dist 'AIEverything.Daemon.exe'

foreach ($path in @($server, $daemon, (Join-Path $dist 'AIEverything.ExtractorWorker.exe'))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing published executable: $path. Run scripts\build.ps1 first."
    }
}

function Test-DaemonReady {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $server content-index status 2>$null | Out-Null
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

if (Test-DaemonReady) {
    Write-Output 'PASS: AIEverything content daemon is already ready.'
    exit 0
}

$process = Start-Process `
    -FilePath $daemon `
    -ArgumentList @('run') `
    -WorkingDirectory $dist `
    -WindowStyle Hidden `
    -PassThru

$deadline = [DateTime]::UtcNow.AddSeconds(15)
do {
    Start-Sleep -Milliseconds 250
    if ($process.HasExited) {
        throw "AIEverything content daemon exited with code $($process.ExitCode)."
    }

    if (Test-DaemonReady) {
        Write-Output "PASS: started AIEverything content daemon (PID $($process.Id))."
        exit 0
    }
} while ([DateTime]::UtcNow -lt $deadline)

throw 'AIEverything content daemon did not become ready within 15 seconds.'
