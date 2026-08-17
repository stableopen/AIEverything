param(
    [string] $ExecutablePath = (Join-Path $PSScriptRoot '..\dist\standalone\win-x64\AIEverything.exe'),
    [string] $FileNameQuery = 'AIEverything',
    [string] $NoResultQuery = 'AIEverythingV100NoResult_7E74E245',
    [string] $ScreenshotDirectory = (Join-Path $env:TEMP 'aieverything-v100-smoke')
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AIEverythingSmokeWin32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] public static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
'@

[void][AIEverythingSmokeWin32]::SetProcessDpiAwarenessContext([IntPtr](-4))

function Save-WindowScreenshot([IntPtr] $handle, [string] $path) {
    $directory = Split-Path -Parent $path
    if ($directory) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    $rect = [AIEverythingSmokeWin32+Rect]::new()
    if (-not [AIEverythingSmokeWin32]::GetWindowRect($handle, [ref] $rect)) {
        throw 'GetWindowRect failed.'
    }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $dc = $graphics.GetHdc()
        try { $printed = [AIEverythingSmokeWin32]::PrintWindow($handle, $dc, 2) }
        finally { $graphics.ReleaseHdc($dc) }
        if (-not $printed) { $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size) }
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}

function Find-ById([Windows.Automation.AutomationElement] $root, [string] $id) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-Element([Windows.Automation.AutomationElement] $element) {
    if ($element.Current.ControlType -eq [Windows.Automation.ControlType]::RadioButton) {
        $selection = [Windows.Automation.SelectionItemPattern]$element.GetCurrentPattern(
            [Windows.Automation.SelectionItemPattern]::Pattern)
        $selection.Select()
        return
    }
    $pattern = [Windows.Automation.InvokePattern]$element.GetCurrentPattern(
        [Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Set-Text([Windows.Automation.AutomationElement] $element, [string] $value) {
    $pattern = [Windows.Automation.ValuePattern]$element.GetCurrentPattern(
        [Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($value)
}

function Get-ToggleState([Windows.Automation.AutomationElement] $element) {
    $pattern = [Windows.Automation.TogglePattern]$element.GetCurrentPattern(
        [Windows.Automation.TogglePattern]::Pattern)
    $pattern.Current.ToggleState
}

function Get-ResultRows([Windows.Automation.AutomationElement] $root) {
    $grid = Find-ById $root 'ResultsGrid'
    if ($null -eq $grid) { return $null }
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::DataItem)
    $grid.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-RightClick([Windows.Automation.AutomationElement] $element) {
    $bounds = $element.Current.BoundingRectangle
    $x = [int]($bounds.Left + [Math]::Min(24, [Math]::Max(2, $bounds.Width / 2)))
    $y = [int]($bounds.Top + [Math]::Max(2, $bounds.Height / 2))
    if (-not [AIEverythingSmokeWin32]::SetCursorPos($x, $y)) {
        throw 'Could not position the pointer over the result row.'
    }
    [AIEverythingSmokeWin32]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
    [AIEverythingSmokeWin32]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
}

function Close-ContextMenu {
    [AIEverythingSmokeWin32]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
    [AIEverythingSmokeWin32]::keybd_event(0x1B, 0, 0x0002, [UIntPtr]::Zero)
}

function Wait-Until([scriptblock] $condition, [int] $seconds, [string] $failure) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $failure
}

$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$settingsPath = Join-Path $env:LOCALAPPDATA 'AIEverything\settings.json'
$rankingPath = Join-Path $env:LOCALAPPDATA 'AIEverything\ranking.db'
$statePaths = @($settingsPath, $rankingPath, "$rankingPath-wal", "$rankingPath-shm")
$backupSuffix = ".v100-smoke-backup-$PID-$([Guid]::NewGuid().ToString('N'))"
$stateBackups = @()
$process = $null

try {
    $existing = @(Get-Process -Name 'AIEverything' -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        throw 'Close the existing AIEverything desktop process before running the isolated UI smoke.'
    }
    foreach ($statePath in $statePaths) {
        $hadFile = Test-Path -LiteralPath $statePath -PathType Leaf
        $backupPath = "$statePath$backupSuffix"
        if ($hadFile) { Move-Item -LiteralPath $statePath -Destination $backupPath }
        $stateBackups += [pscustomobject]@{
            Path = $statePath
            Backup = $backupPath
            HadFile = $hadFile
        }
    }
    $process = Start-Process -FilePath $executable -PassThru
    [void](Wait-Until {
        $process.Refresh()
        if ($process.MainWindowHandle -ne 0) { return $process.MainWindowHandle }
    } 35 'AIEverything main window did not appear.')

    $window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $requiredIds = @(
        'SearchBox', 'SettingsButton', 'MinimizeButton', 'MaximizeRestoreButton', 'CloseButton',
        'AllModeButton', 'NameModeButton', 'ContentModeButton', 'ResultsGrid', 'QueryStatusText')
    $elements = @{}
    foreach ($id in $requiredIds) {
        $elements[$id] = Find-ById $window $id
        if ($null -eq $elements[$id]) { throw "Required UI Automation element was not found: $id" }
    }

    foreach ($id in @('SearchBox', 'SettingsButton', 'AllModeButton', 'NameModeButton', 'ContentModeButton')) {
        if (-not $elements[$id].Current.IsEnabled) { throw "Startup control was disabled: $id" }
    }
    foreach ($removedId in @('PreviewButton', 'OpenButton', 'LocateButton', 'CopyButton')) {
        if ($null -ne (Find-ById $window $removedId)) {
            throw "Removed footer action is still present: $removedId"
        }
    }

    $bounds = $window.Current.BoundingRectangle
    $dpi = [AIEverythingSmokeWin32]::GetDpiForWindow($process.MainWindowHandle)
    $widthDips = [Math]::Round($bounds.Width / ($dpi / 96.0))
    $heightDips = [Math]::Round($bounds.Height / ($dpi / 96.0))
    if ($widthDips -ne 900 -or $heightDips -ne 560) {
        throw "Unexpected first-launch size: ${widthDips}x${heightDips} DIPs at $dpi DPI."
    }
    $settingsBounds = $elements['SettingsButton'].Current.BoundingRectangle
    $titleBarBottom = $bounds.Top + (36 * ($dpi / 96.0))
    if ($settingsBounds.Top -lt $bounds.Top -or $settingsBounds.Bottom -gt $titleBarBottom) {
        throw 'SettingsButton is not contained in the single title bar.'
    }
    Save-WindowScreenshot $process.MainWindowHandle (Join-Path $ScreenshotDirectory 'empty.png')

    Set-Text $elements['SearchBox'] $FileNameQuery
    Invoke-Element $elements['NameModeButton']
    $rows = Wait-Until {
        $candidate = Get-ResultRows $window
        if ($null -ne $candidate -and $candidate.Count -gt 0) { return $candidate }
    } 25 "No filename result appeared for '$FileNameQuery'."
    $selection = [Windows.Automation.SelectionItemPattern]$rows.Item(0).GetCurrentPattern(
        [Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Milliseconds 250
    Invoke-RightClick $rows.Item(0)
    $contextActions = @{}
    foreach ($id in @('PreviewMenuItem', 'OpenMenuItem', 'LocateMenuItem', 'CopyMenuItem')) {
        $contextActions[$id] = Wait-Until {
            Find-ById ([Windows.Automation.AutomationElement]::RootElement) $id
        } 5 "Result context action did not appear: $id"
    }
    $fileNamePreviewEnabled = $contextActions['PreviewMenuItem'].Current.IsEnabled
    $fileNameOpenEnabled = $contextActions['OpenMenuItem'].Current.IsEnabled
    $fileNameLocateEnabled = $contextActions['LocateMenuItem'].Current.IsEnabled
    $fileNameCopyEnabled = $contextActions['CopyMenuItem'].Current.IsEnabled
    if ($fileNamePreviewEnabled) { throw 'Preview context action must be disabled for a filename result.' }
    if (-not $fileNameOpenEnabled -or -not $fileNameLocateEnabled -or -not $fileNameCopyEnabled) {
        throw 'Open, locate, and copy context actions must be enabled for a filename result.'
    }
    Close-ContextMenu
    Save-WindowScreenshot $process.MainWindowHandle (Join-Path $ScreenshotDirectory 'filename.png')

    Set-Text $elements['SearchBox'] $NoResultQuery
    [void](Wait-Until {
        $status = $elements['QueryStatusText'].Current.Name
        if ($status -eq '找到 0 项') { return $status }
    } 25 "No-result state did not appear for '$NoResultQuery'.")
    Save-WindowScreenshot $process.MainWindowHandle (Join-Path $ScreenshotDirectory 'no-result.png')

    Invoke-Element $elements['SettingsButton']
    $settingsWindow = Wait-Until {
        $condition = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::AutomationIdProperty, 'ContentSettingsWindow')
        [Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [Windows.Automation.TreeScope]::Descendants, $condition)
    } 10 'Owned settings window did not appear.'
    foreach ($id in @(
            'SettingsStatusText', 'SettingsToggleButton', 'SettingsSyncButton',
            'SettingsDatabasePathText', 'SettingsPrivacyNote', 'SettingsScrollViewer',
            'BehaviorRankingToggle', 'ClearBehaviorButton', 'LocalModelToggle',
            'LocalModelStatusText', 'DeepSeekDisclosureCheck', 'DeepSeekToggle',
            'DeepSeekStatusText', 'SettingsCloseButton')) {
        if ($null -eq (Find-ById $settingsWindow $id)) { throw "Settings element was not found: $id" }
    }
    $settingsStatus = Wait-Until {
        $value = (Find-ById $settingsWindow 'SettingsStatusText').Current.Name
        $databasePath = (Find-ById $settingsWindow 'SettingsDatabasePathText').Current.Name
        if ($value -and $databasePath -and $databasePath -ne '-') { return $value }
    } 10 'Settings status did not finish loading.'
    $privacyBounds = (Find-ById $settingsWindow 'SettingsPrivacyNote').Current.BoundingRectangle
    $closeBounds = (Find-ById $settingsWindow 'SettingsCloseButton').Current.BoundingRectangle
    if ($privacyBounds.Bottom -gt $closeBounds.Top) {
        throw 'Settings privacy note overlaps the footer.'
    }
    $behaviorDefault = Get-ToggleState (Find-ById $settingsWindow 'BehaviorRankingToggle')
    $localModelDefault = Get-ToggleState (Find-ById $settingsWindow 'LocalModelToggle')
    $disclosureDefault = Get-ToggleState (Find-ById $settingsWindow 'DeepSeekDisclosureCheck')
    $deepSeekDefault = Get-ToggleState (Find-ById $settingsWindow 'DeepSeekToggle')
    if ($behaviorDefault -ne [Windows.Automation.ToggleState]::On -or
        $localModelDefault -ne [Windows.Automation.ToggleState]::On -or
        $disclosureDefault -ne [Windows.Automation.ToggleState]::Off -or
        $deepSeekDefault -ne [Windows.Automation.ToggleState]::Off) {
        throw "Unsafe ranking defaults: behavior=$behaviorDefault local=$localModelDefault disclosure=$disclosureDefault deepseek=$deepSeekDefault"
    }
    $deepSeekElement = Find-ById $settingsWindow 'DeepSeekToggle'
    if ($deepSeekElement.Current.IsEnabled) {
        throw 'DeepSeek toggle must stay disabled until disclosure is accepted.'
    }
    $deepSeekStatus = (Find-ById $settingsWindow 'DeepSeekStatusText').Current.Name
    $notAuthorizedText = -join ([char]0x672A, [char]0x6388, [char]0x6743)
    if (-not $deepSeekStatus.Contains($notAuthorizedText)) {
        throw "Unexpected default DeepSeek status: $deepSeekStatus"
    }

    $scrollElement = Find-ById $settingsWindow 'SettingsScrollViewer'
    $scrollPattern = [Windows.Automation.ScrollPattern]$scrollElement.GetCurrentPattern(
        [Windows.Automation.ScrollPattern]::Pattern)
    if (-not $scrollPattern.Current.VerticallyScrollable) {
        throw 'Settings content is not vertically scrollable.'
    }
    $scrollPattern.SetScrollPercent(
        [Windows.Automation.ScrollPattern]::NoScroll,
        100)
    Start-Sleep -Milliseconds 250
    $settingsHandle = [IntPtr]$settingsWindow.Current.NativeWindowHandle
    Save-WindowScreenshot $settingsHandle (Join-Path $ScreenshotDirectory 'settings-ranking.png')
    $localModelStatus = (Find-ById $settingsWindow 'LocalModelStatusText').Current.Name
    Invoke-Element (Find-ById $settingsWindow 'SettingsCloseButton')

    $searchTextAfterSettings = ([Windows.Automation.ValuePattern]$elements['SearchBox'].GetCurrentPattern(
        [Windows.Automation.ValuePattern]::Pattern)).Current.Value
    if ($searchTextAfterSettings -ne $NoResultQuery) { throw 'Closing settings changed the main query.' }

    [pscustomobject]@{
        Executable = $executable
        WindowTitle = $window.Current.Name
        UiWidthDips = $widthDips
        UiHeightDips = $heightDips
        Dpi = $dpi
        FileNameRows = $rows.Count
        SettingsInTitleBar = $true
        FileNameContextPreviewEnabled = $fileNamePreviewEnabled
        FileNameContextOpenEnabled = $fileNameOpenEnabled
        FileNameContextLocateEnabled = $fileNameLocateEnabled
        FileNameContextCopyEnabled = $fileNameCopyEnabled
        SettingsStatus = $settingsStatus
        SettingsPrivacyContained = $privacyBounds.Bottom -le $closeBounds.Top
        SettingsVerticallyScrollable = $scrollPattern.Current.VerticallyScrollable
        BehaviorRankingDefault = $behaviorDefault
        LocalModelDefault = $localModelDefault
        LocalModelStatus = $localModelStatus
        DeepSeekDisclosureDefault = $disclosureDefault
        DeepSeekDefault = $deepSeekDefault
        DeepSeekStatus = $deepSeekStatus
        QueryPreservedAfterSettings = $searchTextAfterSettings
        ResultStatus = $elements['QueryStatusText'].Current.Name
        Screenshots = $ScreenshotDirectory
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force }
    }
    foreach ($state in $stateBackups) {
        if (Test-Path -LiteralPath $state.Path -PathType Leaf) {
            Remove-Item -LiteralPath $state.Path -Force
        }
        if ($state.HadFile -and (Test-Path -LiteralPath $state.Backup -PathType Leaf)) {
            Move-Item -LiteralPath $state.Backup -Destination $state.Path
        }
    }
}
