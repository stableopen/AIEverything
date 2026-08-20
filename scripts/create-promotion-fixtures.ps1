param(
    [string] $OutputDirectory = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'AIEverything Promotion Demo')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$docx = Join-Path $output 'Quarterly-Sales-Plan.docx'
$corrupt = Join-Path $output 'Corrupt-Word-Sample.docx'

function Add-ZipText(
    [System.IO.Compression.ZipArchive] $Archive,
    [string] $Name,
    [string] $Content) {
    $entry = $Archive.CreateEntry($Name)
    $stream = $entry.Open()
    try {
        $encoding = New-Object System.Text.UTF8Encoding($false)
        $writer = New-Object System.IO.StreamWriter($stream, $encoding)
        try { $writer.Write($Content) }
        finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }
}

if (Test-Path -LiteralPath $docx) { Remove-Item -LiteralPath $docx -Force }
$archive = [System.IO.Compression.ZipFile]::Open(
    $docx,
    [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Add-ZipText $archive '[Content_Types].xml' @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
</Types>
'@
    Add-ZipText $archive '_rels/.rels' @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>
'@
    Add-ZipText $archive 'word/document.xml' @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
    <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Sales Plan</w:t></w:r></w:p>
    <w:p><w:r><w:t>Quarterly </w:t></w:r><w:r><w:t>operating plan for the internal promotion demo.</w:t></w:r></w:p>
    <w:p><w:r><w:t>RegionalTargetCanary confirms the north region target.</w:t></w:r></w:p>
    <w:tbl><w:tr>
      <w:tc><w:p><w:r><w:t>Region</w:t></w:r></w:p></w:tc>
      <w:tc><w:p><w:r><w:t>TableCellCanary</w:t></w:r></w:p></w:tc>
    </w:tr></w:tbl>
    <w:sectPr/>
  </w:body>
</w:document>
'@
}
finally { $archive.Dispose() }

[System.IO.File]::WriteAllText($corrupt, 'This is intentionally not a valid DOCX package.')
Write-Output "DOCX=$docx"
Write-Output "CORRUPT=$corrupt"
