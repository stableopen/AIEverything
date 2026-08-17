$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$maximumTrackedBytes = 50MB
$errors = [System.Collections.Generic.List[string]]::new()

$tracked = @(& git -C $root ls-files)
if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }

$forbiddenPrefixes = @('dist/', '.codex/')
$forbiddenDataExtensions = @(
    '.db', '.db-shm', '.db-wal', '.sqlite', '.sqlite3', '.log', '.dmp')
$textExtensions = @(
    '.cs', '.csproj', '.props', '.targets', '.json', '.md', '.txt', '.ps1', '.psm1',
    '.py', '.xml', '.xaml', '.yaml', '.yml', '.ini', '.config', '.sln', '.gitignore')

foreach ($relative in $tracked) {
    $normalized = $relative.Replace('\', '/')
    if ($forbiddenPrefixes | Where-Object {
            $normalized.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }) {
        $errors.Add("Tracked private/generated path: $relative")
    }

    $extension = [System.IO.Path]::GetExtension($relative).ToLowerInvariant()
    if ($forbiddenDataExtensions -contains $extension) {
        $errors.Add("Tracked local data or diagnostic file: $relative")
    }
    if ($normalized.EndsWith(
            '/model_quint8_avx2.onnx', [StringComparison]::OrdinalIgnoreCase)) {
        $errors.Add("Tracked ONNX model must be fetched, not committed: $relative")
    }

    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $file = Get-Item -LiteralPath $path
    if ($file.Length -gt $maximumTrackedBytes) {
        $errors.Add("Tracked file exceeds 50 MiB: $relative ($($file.Length) bytes)")
    }
    if ($textExtensions -notcontains $extension -and
        [System.IO.Path]::GetFileName($relative) -ne '.gitignore') { continue }

    $content = Get-Content -LiteralPath $path -Raw -Encoding UTF8

    foreach ($match in [regex]::Matches($content, '(?<![A-Za-z0-9])sk-[A-Za-z0-9_-]{24,}')) {
        $token = $match.Value
        if ($token -notmatch '(?i)fake|test|example|dummy|placeholder') {
            $errors.Add("Possible real API key in $relative")
            break
        }
    }

    if ($content -match '(?im)(api[_ -]?key|token|secret)\s*[:=]\s*["'']?(?!fake|test|example|dummy|placeholder|your-)[A-Za-z0-9_-]{24,}') {
        $errors.Add("Possible assigned credential in $relative")
    }

    foreach ($match in [regex]::Matches(
            $content, '(?i)[A-Z]:\\Users\\(?<user>[^\\/\s"'']+)')) {
        $userName = $match.Groups['user'].Value
        if ($userName -notmatch '^(TestUser|CurrentUser|current|other|User|Example)$') {
            $errors.Add("Author-specific Windows user path in $relative`: $($match.Value)")
            break
        }
    }

    if ($content -match '(?i)(?<![A-Za-z0-9])D:\\document\\') {
        $errors.Add("Author-specific work root in $relative")
    }
}

$readmePath = Join-Path $root 'README.md'
if (-not (Test-Path -LiteralPath $readmePath -PathType Leaf)) {
    $errors.Add('README.md is missing.')
}
else {
    $readme = Get-Content -LiteralPath $readmePath -Raw -Encoding UTF8
    foreach ($match in [regex]::Matches($readme, '!?(?:\[[^\]]*\])\((?<target>[^)]+)\)')) {
        $target = $match.Groups['target'].Value.Trim().Trim('<', '>')
        if ($target -match '^(?i:https?://|mailto:|#)') { continue }
        $target = ($target -split '#', 2)[0]
        if ([string]::IsNullOrWhiteSpace($target)) { continue }
        $localPath = Join-Path $root ([Uri]::UnescapeDataString($target).Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $localPath)) {
            $errors.Add("Broken local README link: $target")
        }
    }
}

if ($errors.Count -gt 0) {
    $errors | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw "Open-source audit failed with $($errors.Count) finding(s)."
}

Write-Output "PASS: audited $($tracked.Count) tracked files for large assets, secrets, local data, private paths, and README links."
