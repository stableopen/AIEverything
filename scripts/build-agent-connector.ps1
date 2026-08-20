param(
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'dist'))
$connector = [System.IO.Path]::GetFullPath(
    (Join-Path $distRoot 'agent-connector\win-x64'))
$stage = [System.IO.Path]::GetFullPath((Join-Path $distRoot '.agent-plugin-stage'))
$packageRoot = Join-Path $stage 'aieverything'
$zip = [System.IO.Path]::GetFullPath(
    (Join-Path $distRoot 'AIEverything-Agent-Plugin-1.0.2.zip'))
$rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar

foreach ($path in @($connector, $stage, $zip)) {
    if (-not $path.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to manage path outside repository root: $path"
    }
}

dotnet restore (Join-Path $root 'AIEverything.sln')
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'AIEverything.sln') `
        -c Release `
        --no-restore `
        --filter 'Category!=Integration' `
        --nologo `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
}

foreach ($path in @($connector, $stage)) {
    if (Test-Path -LiteralPath $path) {
        $resolved = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $path).Path)
        if (-not $resolved.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Resolved connector path escaped repository root: $resolved"
        }

        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $connector -Force | Out-Null
dotnet publish (Join-Path $root 'src\AIEverything.Server\AIEverything.Server.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $connector
if ($LASTEXITCODE -ne 0) { throw "Agent connector publish failed with exit code $LASTEXITCODE." }

Copy-Item -LiteralPath (Join-Path $root 'vendor\everything-sdk\Everything64.dll') `
    -Destination (Join-Path $connector 'Everything64.dll') -Force
foreach ($required in @('AIEverything.Server.exe', 'Everything64.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $connector $required) -PathType Leaf)) {
        throw "Agent connector is missing $required."
    }
}

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageRoot '.codex-plugin') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageRoot 'skills') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageRoot 'dist\agent-connector') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root '.codex-plugin\plugin.json') `
    -Destination (Join-Path $packageRoot '.codex-plugin\plugin.json') -Force
Copy-Item -LiteralPath (Join-Path $root '.mcp.json') -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $root 'skills\aieverything-search') `
    -Destination (Join-Path $packageRoot 'skills') -Recurse -Force
Copy-Item -LiteralPath $connector `
    -Destination (Join-Path $packageRoot 'dist\agent-connector') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $packageRoot -Force

if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
$archive = [System.IO.Compression.ZipFile]::Open(
    $zip,
    [System.IO.Compression.ZipArchiveMode]::Create)
$packagePrefix = $packageRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
try {
    Get-ChildItem -LiteralPath $packageRoot -Recurse -File | ForEach-Object {
        if (-not $_.FullName.StartsWith(
                $packagePrefix,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Package file escaped staging directory: $($_.FullName)"
        }
        $entryName = $_.FullName.Substring($packagePrefix.Length).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $_.FullName,
            $entryName,
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $archive.Dispose()
}
Remove-Item -LiteralPath $stage -Recurse -Force

Write-Output "PASS: published optional Agent connector to $connector"
Write-Output "PASS: created optional Agent Plugin package $zip"
