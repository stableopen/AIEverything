param(
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'dist'))
$dist = [System.IO.Path]::GetFullPath((Join-Path $distRoot 'standalone\win-x64'))
$stage = [System.IO.Path]::GetFullPath((Join-Path $distRoot '.standalone-stage'))
$zip = [System.IO.Path]::GetFullPath((Join-Path $distRoot 'AIEverything-1.0.4-win-x64.zip'))
$checksum = [System.IO.Path]::GetFullPath((Join-Path $distRoot 'AIEverything-1.0.4-win-x64.zip.sha256'))
$rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
$releaseFileVersion = '1.0.4.0'
$modelDirectoryName = 'mmarco-mMiniLMv2-L12-H384-v1'
$modelRelativePath = Join-Path 'Models' $modelDirectoryName
$protectedArchives = @(
    @{
        Path = (Join-Path $distRoot 'AIEverything-V0.20-win-x64.zip')
        Hash = 'F143532D288194D1BF9B81486301D160ABCBC22E78FFE60D6C0C15CA7CA0DF46'
    },
    @{
        Path = (Join-Path $distRoot 'AIEverything-V0.20.1-win-x64.zip')
        Hash = 'EAD417D6B45DAB2AA79A10F171493AC9AE41848643193F30EA08FB16319BC657'
    }
)

function Assert-RequiredFile([string] $Path, [string] $Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing $Description`: $Path"
    }
}

function Assert-ProtectedArchives {
    $verified = 0
    foreach ($archive in $protectedArchives) {
        if (-not (Test-Path -LiteralPath $archive.Path -PathType Leaf)) {
            continue
        }

        $actual = (Get-FileHash -LiteralPath $archive.Path -Algorithm SHA256).Hash
        if ($actual -ne $archive.Hash) {
            throw "Protected historical archive changed: $($archive.Path) expected $($archive.Hash), actual $actual"
        }
        $verified++
    }
    if ($verified -gt 0) {
        Write-Output "PASS: verified $verified existing historical archive(s)."
    }
}

function Assert-ModelAssets([string] $PublishRoot) {
    $modelRoot = Join-Path $PublishRoot $modelRelativePath
    if (-not (Test-Path -LiteralPath $modelRoot -PathType Container)) {
        throw "Publish did not keep the model external: $modelRoot"
    }

    $required = @(
        'config.json',
        'LICENSE.apache-2.0.txt',
        'MODEL_CARD.md',
        'model_quint8_avx2.onnx',
        'model-calibration.json',
        'model-manifest.json',
        'sentencepiece.bpe.model',
        'SHA256SUMS.txt',
        'special_tokens_map.json',
        'tokenizer_config.json'
    )
    foreach ($name in $required) {
        Assert-RequiredFile (Join-Path $modelRoot $name) "model asset $name"
    }

    $actualNames = @(Get-ChildItem -LiteralPath $modelRoot -File |
        ForEach-Object { $_.Name } | Sort-Object)
    $difference = @(Compare-Object ($required | Sort-Object) $actualNames)
    if ($difference.Count -ne 0) {
        throw "Model asset set differs from the frozen ten-file manifest: $($difference | Out-String)"
    }

    $manifestPath = Join-Path $modelRoot 'model-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]$manifest.schema_version -ne 1 -or
        [string]$manifest.revision -ne '1427fd652930e4ba29e8149678df786c240d8825' -or
        [string]$manifest.license -ne 'Apache-2.0') {
        throw 'Model manifest identity, revision, or license does not match the frozen local model.'
    }

    $manifestEntries = @($manifest.files)
    if ($manifestEntries.Count -ne 8) {
        throw "Expected eight hashed model payload entries, found $($manifestEntries.Count)."
    }

    $manifestHashes = @{}
    foreach ($entry in $manifestEntries) {
        $name = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($name) -or
            [System.IO.Path]::GetFileName($name) -ne $name) {
            throw "Unsafe model-manifest path: $name"
        }

        $path = Join-Path $modelRoot $name
        Assert-RequiredFile $path "manifest model payload $name"
        $file = Get-Item -LiteralPath $path
        if ($file.Length -ne [int64]$entry.bytes) {
            throw "Model length mismatch for $name`: expected $($entry.bytes), actual $($file.Length)."
        }

        $expectedHash = ([string]$entry.sha256).ToUpperInvariant()
        $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) {
            throw "Model hash mismatch for $name`: expected $expectedHash, actual $actualHash."
        }

        $manifestHashes[$name] = $expectedHash
    }

    $sumEntries = @{}
    foreach ($line in Get-Content -LiteralPath (Join-Path $modelRoot 'SHA256SUMS.txt') -Encoding UTF8) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([0-9A-Fa-f]{64})\s{2}(.+)$') {
            throw "Invalid SHA256SUMS line: $line"
        }

        $name = $Matches[2].Trim()
        $hash = $Matches[1].ToUpperInvariant()
        if (-not $manifestHashes.ContainsKey($name) -or $manifestHashes[$name] -ne $hash) {
            throw "SHA256SUMS does not match model-manifest.json for $name."
        }

        $sumEntries[$name] = $hash
    }
    if ($sumEntries.Count -ne $manifestHashes.Count) {
        throw 'SHA256SUMS does not enumerate every frozen model payload.'
    }
}

function Assert-ZipMatchesDirectory([string] $ArchivePath, [string] $DirectoryPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrWhiteSpace($entry.Name)) { continue }
            $name = $entry.FullName.Replace('\', '/').ToLowerInvariant()
            if ($entries.ContainsKey($name)) { throw "Duplicate ZIP entry: $($entry.FullName)" }
            $entries[$name] = $entry
        }

        $prefix = $DirectoryPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
            [System.IO.Path]::DirectorySeparatorChar
        $files = @(Get-ChildItem -LiteralPath $DirectoryPath -Recurse -File)
        if ($entries.Count -ne $files.Count) {
            throw "ZIP file count $($entries.Count) does not match publish file count $($files.Count)."
        }

        foreach ($file in $files) {
            $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
            $key = $relative.ToLowerInvariant()
            if (-not $entries.ContainsKey($key)) { throw "ZIP is missing $relative" }
            $entry = $entries[$key]
            if ($entry.Length -ne $file.Length) {
                throw "ZIP length mismatch for $relative."
            }

            $stream = $entry.Open()
            try {
                $sha = [System.Security.Cryptography.SHA256]::Create()
                try {
                    $entryHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '')
                }
                finally { $sha.Dispose() }
            }
            finally { $stream.Dispose() }
            $fileHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            if ($entryHash -ne $fileHash) { throw "ZIP hash mismatch for $relative." }
        }

        if ($entries.ContainsKey('aieverything.server.exe')) {
            throw 'Standalone ZIP must not contain AIEverything.Server.exe.'
        }
    }
    finally { $archive.Dispose() }
}

foreach ($path in @($distRoot, $dist, $stage, $zip, $checksum)) {
    if (-not $path.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to manage path outside repository root: $path"
    }
}

Assert-ProtectedArchives

$sourceModel = Join-Path $root `
    'src\AIEverything.Desktop\Models\mmarco-mMiniLMv2-L12-H384-v1\model_quint8_avx2.onnx'
if (-not (Test-Path -LiteralPath $sourceModel -PathType Leaf)) {
    throw ('Missing frozen local ranking model. Run: powershell -NoProfile ' +
        '-ExecutionPolicy Bypass -File scripts\fetch-model.ps1')
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

& (Join-Path $PSScriptRoot 'stop-installed-daemon.ps1') -InstallDirectory $dist

foreach ($path in @($dist, $stage)) {
    if (Test-Path -LiteralPath $path) {
        $resolved = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $path).Path)
        if (-not $resolved.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Resolved publish path escaped repository root: $resolved"
        }

        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null
New-Item -ItemType Directory -Path $stage -Force | Out-Null

$projects = [ordered]@{
    app = 'src\AIEverything.App\AIEverything.App.csproj'
    daemon = 'src\AIEverything.Daemon\AIEverything.Daemon.csproj'
    worker = 'src\AIEverything.ExtractorWorker\AIEverything.ExtractorWorker.csproj'
}
foreach ($name in $projects.Keys) {
    $output = Join-Path $stage $name
    dotnet publish (Join-Path $root $projects[$name]) `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $($projects[$name]) with exit code $LASTEXITCODE."
    }
}

$appPublish = Join-Path $stage 'app'
Copy-Item -Path (Join-Path $appPublish '*') -Destination $dist -Recurse -Force
foreach ($appOnlyArtifact in @(
        'AIEverything.Server.exe',
        'AIEverything.Server.runtimeconfig.json')) {
    $artifactPath = Join-Path $dist $appOnlyArtifact
    if (Test-Path -LiteralPath $artifactPath) {
        Remove-Item -LiteralPath $artifactPath -Force
    }
}

$requiredCopies = @(
    @{ Source = (Join-Path $root 'vendor\everything-sdk\Everything64.dll'); Destination = 'Everything64.dll' },
    @{ Source = (Join-Path $stage 'daemon\AIEverything.Daemon.exe'); Destination = 'AIEverything.Daemon.exe' },
    @{ Source = (Join-Path $stage 'worker\AIEverything.ExtractorWorker.exe'); Destination = 'AIEverything.ExtractorWorker.exe' }
)
foreach ($copy in $requiredCopies) {
    if (-not (Test-Path -LiteralPath $copy.Source -PathType Leaf)) {
        throw "Publish did not create required file: $($copy.Source)"
    }

    Copy-Item -LiteralPath $copy.Source -Destination (Join-Path $dist $copy.Destination) -Force
}

if (-not (Test-Path -LiteralPath (Join-Path $dist 'AIEverything.exe') -PathType Leaf)) {
    throw 'App publish did not create AIEverything.exe.'
}

foreach ($executableName in @(
        'AIEverything.exe',
        'AIEverything.Daemon.exe',
        'AIEverything.ExtractorWorker.exe')) {
    $executablePath = Join-Path $dist $executableName
    Assert-RequiredFile $executablePath "versioned executable $executableName"
    $publishedVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
        $executablePath).FileVersion
    if ([version]$publishedVersion -ne [version]$releaseFileVersion) {
        throw "$executableName version must be $releaseFileVersion, actual $publishedVersion."
    }
}

Assert-ModelAssets $dist
foreach ($runtimeFile in @('onnxruntime.dll', 'onnxruntime_providers_shared.dll')) {
    Assert-RequiredFile (Join-Path $dist $runtimeFile) "ONNX Runtime native library $runtimeFile"
}
foreach ($notice in @(
        'licenses\Microsoft.ML.OnnxRuntime\LICENSE.txt',
        'licenses\Microsoft.ML.OnnxRuntime\ThirdPartyNotices.txt',
        'licenses\Microsoft.ML.Tokenizers\LICENSE.txt',
        'licenses\Microsoft.ML.Tokenizers\THIRD-PARTY-NOTICES.txt')) {
    Assert-RequiredFile (Join-Path $dist $notice) "bundled third-party notice $notice"
}

foreach ($engineFile in @('Everything.exe', 'Everything.ini', 'LICENSE.txt')) {
    $enginePath = Join-Path $dist (Join-Path 'EverythingEngine' $engineFile)
    if (-not (Test-Path -LiteralPath $enginePath -PathType Leaf)) {
        throw "App publish did not include filename engine payload: $enginePath"
    }
}

$sqlite = Get-ChildItem -LiteralPath (Join-Path $stage 'daemon') -Filter 'e_sqlite3.dll' -File |
    Select-Object -First 1
if ($null -eq $sqlite) {
    throw 'Daemon publish did not create e_sqlite3.dll.'
}
Copy-Item -LiteralPath $sqlite.FullName -Destination (Join-Path $dist 'e_sqlite3.dll') -Force

Copy-Item -LiteralPath (Join-Path $root 'docs\STANDALONE-README.txt') `
    -Destination (Join-Path $dist 'README.txt') -Force
Copy-Item -LiteralPath (Join-Path $root 'PRIVACY.md') -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $dist -Force
New-Item -ItemType Directory -Path (Join-Path $dist 'tools') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'stop-installed-daemon.ps1') `
    -Destination (Join-Path $dist 'tools\stop-daemon.ps1') -Force

if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
if (Test-Path -LiteralPath $checksum) {
    Remove-Item -LiteralPath $checksum -Force
}
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip -CompressionLevel Optimal

Assert-ZipMatchesDirectory $zip $dist
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Set-Content -LiteralPath $checksum -Value "$zipHash  AIEverything-1.0.4-win-x64.zip" -Encoding ascii -NoNewline
Assert-ProtectedArchives

Remove-Item -LiteralPath $stage -Recurse -Force

Write-Output "PASS: published standalone AIEverything to $dist"
Write-Output "PASS: created portable package $zip"
Write-Output "PASS: created checksum sidecar AIEverything-1.0.4-win-x64.zip.sha256"
Write-Output "PASS: verified model manifest, external assets, licenses, and ZIP byte identity"
Write-Output "SHA256: $zipHash"
