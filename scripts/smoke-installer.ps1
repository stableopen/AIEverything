param(
    [string]$InstallerPath = (Join-Path $PSScriptRoot '..\dist\AIEverything-Setup-1.0.0.exe')
)

$ErrorActionPreference = 'Stop'

$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\AIEverything'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AIEverything'
$startMenuShortcut = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) `
    'AIEverything\AIEverything.lnk'
$desktopShortcut = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) `
    'AIEverything.lnk'
$settingsPath = Join-Path $env:LOCALAPPDATA 'AIEverything\settings.json'
$settingsBackupPath = "$settingsPath.installer-smoke-backup"
$hadSettings = Test-Path -LiteralPath $settingsPath -PathType Leaf
$databasePath = Join-Path $env:LOCALAPPDATA 'AIEverything\content.db'
$databaseHashBefore = if (Test-Path -LiteralPath $databasePath -PathType Leaf) {
    (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash
} else {
    $null
}

$preexisting = @(
    (Test-Path -LiteralPath $installDirectory),
    (Test-Path -LiteralPath $uninstallKey),
    (Test-Path -LiteralPath $startMenuShortcut),
    (Test-Path -LiteralPath $desktopShortcut)
) -contains $true
if ($preexisting) {
    throw 'Installer smoke test requires no existing AIEverything installation or shortcuts.'
}
if (Test-Path -LiteralPath $settingsBackupPath) {
    throw "Refusing to overwrite an existing settings backup: $settingsBackupPath"
}

$appProcess = $null
$installStarted = $false
$settingsBackedUp = $false
try {
    if ($hadSettings) {
        Copy-Item -LiteralPath $settingsPath -Destination $settingsBackupPath
        $settingsBackedUp = $true
    }

    $install = Start-Process -FilePath $installer -ArgumentList '/S' -Wait -PassThru
    if ($install.ExitCode -ne 0) {
        throw "Silent installer exited with code $($install.ExitCode)."
    }
    $installStarted = $true

    $appPath = Join-Path $installDirectory 'AIEverything.exe'
    $daemonPath = Join-Path $installDirectory 'AIEverything.Daemon.exe'
    $uninstallerPath = Join-Path $installDirectory 'Uninstall.exe'
    foreach ($required in @($appPath, $daemonPath, $uninstallerPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Installed payload is missing: $required"
        }
    }
    foreach ($required in @($uninstallKey, $startMenuShortcut)) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "Installer registration is missing: $required"
        }
    }

    $appProcess = Start-Process -FilePath $appPath -PassThru
    $windowDeadline = [DateTime]::UtcNow.AddSeconds(35)
    do {
        Start-Sleep -Milliseconds 250
        $appProcess.Refresh()
    } while ($appProcess.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $windowDeadline)
    if ($appProcess.MainWindowHandle -eq 0) {
        throw 'Installed application window did not appear.'
    }

    $daemonDeadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $installedDaemon = Get-CimInstance Win32_Process |
            Where-Object {
                $_.Name -eq 'AIEverything.Daemon.exe' -and
                $_.ExecutablePath -and
                [System.IO.Path]::GetFullPath($_.ExecutablePath) -eq
                    [System.IO.Path]::GetFullPath($daemonPath)
            }
        if (-not $installedDaemon) {
            Start-Sleep -Milliseconds 250
        }
    } while (-not $installedDaemon -and [DateTime]::UtcNow -lt $daemonDeadline)
    if (-not $installedDaemon) {
        throw 'Installed daemon did not start from the installation directory.'
    }

    # Let the first status refresh settle before testing a normal user close.
    Start-Sleep -Seconds 5
    [void]$appProcess.CloseMainWindow()
    if (-not $appProcess.WaitForExit(15000)) {
        throw 'Installed application did not close normally.'
    }

    $uninstall = Start-Process -FilePath $uninstallerPath -ArgumentList '/S' -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) {
        throw "Silent uninstaller exited with code $($uninstall.ExitCode)."
    }

    $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 250
    } while ((Test-Path -LiteralPath $installDirectory) -and
             [DateTime]::UtcNow -lt $cleanupDeadline)

    $residualProcesses = Get-CimInstance Win32_Process |
        Where-Object {
            $_.Name -like 'AIEverything*.exe' -and
            $_.ExecutablePath -and
            [System.IO.Path]::GetFullPath($_.ExecutablePath).StartsWith(
                [System.IO.Path]::GetFullPath($installDirectory),
                [System.StringComparison]::OrdinalIgnoreCase)
        }
    $residualPaths = @(
        $installDirectory,
        $uninstallKey,
        $startMenuShortcut,
        $desktopShortcut
    ) | Where-Object { Test-Path -LiteralPath $_ }
    if ($residualProcesses -or $residualPaths) {
        throw "Uninstall left residual state: $($residualPaths -join ', ')"
    }

    $databasePreserved = if ($null -eq $databaseHashBefore) {
        -not (Test-Path -LiteralPath $databasePath)
    } else {
        (Test-Path -LiteralPath $databasePath -PathType Leaf) -and
        (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash -eq $databaseHashBefore
    }
    if (-not $databasePreserved) {
        throw 'Uninstall changed or removed the user content index database.'
    }

    [pscustomobject]@{
        InstallerExitCode   = $install.ExitCode
        AppWindowAppeared   = $true
        InstalledDaemonRan  = $true
        UninstallerExitCode = $uninstall.ExitCode
        ResidualProcesses   = 0
        ResidualPaths       = 0
        DatabasePreserved   = $databasePreserved
    }
}
finally {
    if ($null -ne $appProcess -and -not $appProcess.HasExited) {
        [void]$appProcess.CloseMainWindow()
        if (-not $appProcess.WaitForExit(5000)) {
            Stop-Process -Id $appProcess.Id -Force
        }
    }

    if ($installStarted -and (Test-Path -LiteralPath (Join-Path $installDirectory 'Uninstall.exe'))) {
        # Keep failure cleanup on the product's own uninstall path.
        Start-Process -FilePath (Join-Path $installDirectory 'Uninstall.exe') `
            -ArgumentList '/S' -Wait | Out-Null
    }

    if ($settingsBackedUp -and (Test-Path -LiteralPath $settingsBackupPath)) {
        Copy-Item -LiteralPath $settingsBackupPath -Destination $settingsPath -Force
        Remove-Item -LiteralPath $settingsBackupPath -Force
    }
    elseif (-not $hadSettings -and (Test-Path -LiteralPath $settingsPath)) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
}
