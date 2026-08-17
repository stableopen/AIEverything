$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$dist = [System.IO.Path]::GetFullPath((Join-Path $root 'dist\win-x64'))
$rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not $dist.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to manage publish directory outside plugin root: $dist"
}

dotnet restore (Join-Path $root 'AIEverything.sln')
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

dotnet test (Join-Path $root 'AIEverything.sln') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }

$stopScript = Join-Path $PSScriptRoot 'stop-daemon.ps1'
if (Test-Path -LiteralPath $stopScript -PathType Leaf) {
    & $stopScript
}

if (Test-Path -LiteralPath $dist) {
    $resolvedDist = (Resolve-Path -LiteralPath $dist).Path
    if (-not $resolvedDist.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Resolved publish directory escaped plugin root: $resolvedDist"
    }

    Remove-Item -LiteralPath $resolvedDist -Recurse -Force
}

foreach ($project in @(
    'src\AIEverything.Server\AIEverything.Server.csproj',
    'src\AIEverything.Daemon\AIEverything.Daemon.csproj',
    'src\AIEverything.ExtractorWorker\AIEverything.ExtractorWorker.csproj'
)) {
    dotnet publish (Join-Path $root $project) `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -o $dist
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $project with exit code $LASTEXITCODE."
    }
}

$nativeDll = Join-Path $dist 'Everything64.dll'
foreach ($name in @(
    'AIEverything.Server.exe',
    'AIEverything.Daemon.exe',
    'AIEverything.ExtractorWorker.exe'
)) {
    $executable = Join-Path $dist $name
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Publish did not create $executable"
    }
}
if (-not (Test-Path -LiteralPath $nativeDll -PathType Leaf)) {
    throw "Publish did not copy $nativeDll"
}

Write-Output "PASS: published AIEverything Server, Daemon, and ExtractorWorker to $dist"
