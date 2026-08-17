$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$skillPath = Join-Path $root 'skills\aieverything-search\SKILL.md'

if (-not (Test-Path -LiteralPath $skillPath -PathType Leaf)) {
    throw "Missing skill file: $skillPath"
}

$content = Get-Content -LiteralPath $skillPath -Raw -Encoding UTF8
$frontmatterMatch = [regex]::Match($content, '\A---\r?\n(?<body>.*?)\r?\n---\r?\n', 'Singleline')
if (-not $frontmatterMatch.Success) {
    throw 'SKILL.md must begin with YAML frontmatter.'
}

$frontmatterLines = $frontmatterMatch.Groups['body'].Value -split '\r?\n'
$keys = @($frontmatterLines | ForEach-Object {
    if ($_ -match '^([a-z_]+):') { $Matches[1] }
})
if ($keys.Count -ne 2 -or $keys -notcontains 'name' -or $keys -notcontains 'description') {
    throw 'Frontmatter must contain exactly name and description.'
}

$nameLine = $frontmatterLines | Where-Object { $_ -match '^name:' }
if (($nameLine -replace '^name:\s*', '').Trim() -ne 'aieverything-search') {
    throw 'Skill name must be aieverything-search.'
}

$descriptionLine = $frontmatterLines | Where-Object { $_ -match '^description:' }
$description = ($descriptionLine -replace '^description:\s*', '').Trim()
if ($description -notmatch '^Use when\b') {
    throw 'Description must start with Use when.'
}
if ($description -notmatch '(?i)local.*(files|folders)|(files|folders).*local') {
    throw 'Description must mention local files or folders.'
}
if ($description -notmatch '(?i)Everything|recursive search') {
    throw 'Description must mention Everything or recursive search.'
}

foreach ($toolName in @(
    'search_local_files',
    'search_everything_query',
    'aieverything_status',
    'search_local_content',
    'search_local_hybrid',
    'aieverything_index_status'
)) {
    if ($content -notmatch [regex]::Escape($toolName)) {
        throw "Skill must reference MCP tool $toolName."
    }
}

if ($content -notmatch '(?is)(filename|path).*(content|document body)') {
    throw 'Skill must distinguish filename/path metadata search from content search.'
}
if ($content -notmatch '(?i)read-only' -or $content -notmatch '(?i)whole-drive') {
    throw 'Skill must be read-only and prohibit recursive whole-drive fallback.'
}
foreach ($format in @('TXT', 'MD', 'MARKDOWN')) {
    if ($content -notmatch $format) {
        throw "Skill must disclose body format $format."
    }
}
if ($content -match 'aieverything_manage_roots') {
    throw 'Read-only Skill must not expose root mutation.'
}
if ($content -notmatch '(?is)fall back.*Everything.*unavailable') {
    throw 'Skill must define fallback behavior when Everything is unavailable.'
}
if ($content -notmatch '(?is)fall back.*not indexed|not indexed.*fall back') {
    throw 'Skill must define fallback behavior for unindexed locations.'
}

$wordCount = @($content -split '\s+' | Where-Object { $_ }).Count
if ($wordCount -ge 500) {
    throw "Skill must stay below 500 words; found $wordCount."
}

Write-Output "PASS: aieverything-search skill contract ($wordCount words)"
